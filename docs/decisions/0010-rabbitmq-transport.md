# 0010 - RabbitMQ as a delivery transport

Status: proposed. Steps 1 (the seam) and 2 (the lease) of the plan are built; the RabbitMQ transport itself is not.

## Context

### What the code is today

[ADR 0001](0001-at-least-once-delivery.md) said the queue is behind an interface. It is not, and that line has been
corrected. What exists:

- The **`deliveries` table is the queue**. A row is the unit of work and also the record of what happened to it.
- `EventIngestionService` writes an event and one `Delivery` row per active endpoint in one transaction.
- `DeliveryProcessor.ClaimAsync` selects due rows with raw SQL (`FOR UPDATE SKIP LOCKED`) and calls
  `Delivery.StartAttempt`, which moves the row to `InProgress`, counts the attempt and sets `LockedUntil`.
- `DeliveryProcessor.DeliverAsync` loads the delivery, sends it, checks that this attempt still owns the delivery
  (the claim may have expired and another worker taken over), records the attempt and calls `MarkSucceeded` or
  `MarkFailed`. `MarkFailed` computes the next `NextAttemptAt` with the retry policy (exponential backoff and jitter).
- A worker that dies leaves an `InProgress` row whose `LockedUntil` passes; the next claim query takes it again.
- `DeliveryWorker` polls: claim a batch, process it, and when there was nothing wait `PollInterval` (one second).

So *scheduling*, *claiming*, *crash recovery* and *history* are all the same table and the same state machine in the
domain model. That is a strength (one transaction, one source of truth) and it is also why a queue interface does not
exist: the queue and the data are not separate things.

### Why do this at all

Honestly: **for this workload, PostgreSQL with `SKIP LOCKED` is enough**, and a broker adds a moving part and new
ways to fail. The reasons to add RabbitMQ anyway are modest and worth stating without dressing them up:

- Work is **pushed** to a worker as it becomes due, instead of found by every worker polling the database every
  second: one relay polls on behalf of all workers, and idle workers do not query the database at all. (An earlier
  version of this text also claimed that latency stops being bounded by `PollInterval`. That is not so: the relay
  polls the database at `PollInterval` too, so a delivery still waits for the relay's next pass. Only a wake-up
  from the database itself, such as `LISTEN/NOTIFY`, would remove that bound.)
- Worker hosts can be scaled and throttled (prefetch, consumers) **independently of database connections**.
- It is a transport many teams already run and expect, and this is a portfolio project meant to show it done
  carefully, with the failure modes written down.

If the broker's value were only speed, the cheaper answer would be PostgreSQL `LISTEN/NOTIFY` to wake workers. That is
listed below as an alternative, not rejected as unreasonable.

## Options considered

1. **Hybrid: the message is a wake-up call; PostgreSQL stays the source of truth** (chosen, below).
2. **RabbitMQ as the queue and the only source of truth for state**, with the database as history. Rejected: state
   would live in two places that can disagree (a message published but the row never committed, or the reverse),
   which forces an outbox anyway; and manual re-send, the delivery states shown in the panel and the retry policy all
   read the database.
3. **RabbitMQ for retry timing** (delayed messages or TTL queues). Rejected: per-message TTL in a classic queue only
   expires at the head of the queue, so a short delay can wait behind a long one; fixed delay tiers (one queue per
   delay) cannot express the jittered, exponential delays the domain already computes; the delayed-message exchange
   is a plugin to install and operate. The database already holds `NextAttemptAt`.
4. **A messaging library** (MassTransit, Wolverine, NServiceBus). Rejected for now: it would own the retry,
   outbox and consumer semantics this project wants to show and test itself, and it adds a large dependency. Not a
   judgement on the libraries.
5. **PostgreSQL `LISTEN/NOTIFY` to wake workers.** Valid and much cheaper; does not give a broker. Kept as the
   fallback if the broker turns out not to be worth its cost.
6. **Kafka.** Rejected: a log is a poor fit for per-item acknowledgement, retry and re-send, and ordering is not needed.

## Decision

### The message is a wake-up call, not the work

The row in `deliveries` remains the work and the record. A message says only "delivery X is due, try it". It carries
the **delivery id and nothing else**: no payload, no endpoint address, no signing secret. The consumer reads
everything from the database. This has three consequences that make the design simple:

- A **lost** message is harmless: the row is still due, and it is published again when its lease expires (below).
- A **duplicated** message is harmless: the consumer claims the row with the existing state machine; a row that is
  already `InProgress`, `Succeeded` or `Dead` cannot be claimed, and the duplicate is acknowledged and dropped.
- The broker never holds what must stay secret, so its disk, its management UI and its logs do not need to be
  treated as holding payloads or secrets.

The guarantee stays what ADR 0001 says: **at-least-once, with the database deciding**.

### Two parts, one seam

The seam is *how a worker learns which delivery to try*. Everything after that, claiming one delivery, sending,
the ownership check, recording the attempt and the state transitions, is the code that exists today and does not
change.

```csharp
// Proposed shape; the exact names are for the implementation to settle.
public interface IDeliverySource
{
    // Calls `handle` for every delivery that should be attempted now, after the database claim
    // (Delivery.StartAttempt) was made, and does whatever acknowledgement the transport needs once
    // `handle` returns. Runs until `ct` is cancelled.
    Task RunAsync(Func<DeliveryClaim, CancellationToken, Task> handle, CancellationToken ct);
}

public sealed record DeliveryClaim(Guid DeliveryId, int AttemptNumber);   // exists today as a private Claim
```

- **`PostgresDeliverySource`**: today's polling loop, unchanged in behaviour (claim a batch, run `handle` in
  parallel). It stays the default.
- **`RabbitMqDeliverySource`**: a consumer that, for each message, makes the same database claim for that one
  delivery, calls `handle`, then acknowledges.
- **`DeliveryRelay`** (a hosted service that exists only with the RabbitMQ transport): finds due deliveries and
  publishes a message for each.

`Delivery:Transport` (`Postgres` or `RabbitMq`) chooses the source. Workers of both kinds can even run at the same
time against one database, because both go through the same claim; that is a deliberate property, tested.

### The relay

The relay uses today's due query, plus a lease so a due row is not published on every pass:

```sql
SELECT * FROM deliveries
WHERE (("Status" = 'Pending'    AND "NextAttemptAt" <= now)
    OR ("Status" = 'InProgress' AND "LockedUntil"   <= now))
  AND ("EnqueuedUntil" IS NULL OR "EnqueuedUntil" <= now)
ORDER BY "NextAttemptAt" LIMIT n
FOR UPDATE SKIP LOCKED
```

**Lease first, then publish** (this replaces what this section said before step 2, see "What step 2 found"). In one
short transaction the relay selects the rows, sets `EnqueuedUntil = now + EnqueueLease` on each through the domain
model, and **commits**. Only then does it publish a message per row **with publisher confirms**, outside any
transaction. The order matters:

- the publish fails or is not confirmed: the relay gives the leases back (`ReleaseEnqueue`), so the rows are due again
  at once;
- the relay dies after committing and before publishing, or the message is lost, or nobody acts on it: nothing gives
  the lease back, so it runs out and the rows are announced again. The cost is a delay of at most `EnqueueLease`, and
  only in these failure cases;
- everything works: the row stays quiet until a worker claims it, which ends the lease.

`EnqueuedUntil` is a new nullable column (one migration). It is cleared by `StartAttempt` (the message did its job, and
a crashed worker is then recovered by its lock, not by a lease that no longer matters), `MarkSucceeded`, `MarkFailed`
(so a retry is announced when it is due and not when an older, longer lease runs out) and `Requeue`. A lease never
changes what the delivery is, and a worker that polls the database can still claim a leased delivery.

`EnqueueLease` has **no required relation to `LockDuration` or `RequestTimeout`**. An earlier version of this text
demanded `EnqueueLease > LockDuration + RequestTimeout` "so a slow but healthy attempt is not published a second
time". That was wrong: a healthy attempt is `InProgress` with a valid lock, and such a row is never selected,
whatever the lease. The lease only governs the window in which a row is still `Pending` and a message about it is
outstanding. It should be longer than the time a message normally waits before a worker takes it (otherwise rows are
announced twice, which is harmless but wasteful) and short enough that a lost message is noticed in reasonable time.
The default is two minutes; the only rule enforced is that it is positive.

### The consumer

For each message: parse the delivery id, then in one transaction `SELECT ... WHERE "Id" = @id AND <due condition>
FOR UPDATE SKIP LOCKED` and `StartAttempt`. If a row came back, call `handle`; if not (already done, locked by
someone else, not due), there is nothing to do. **Acknowledge in every case, after the result is in the database or
the claim was refused, and never requeue.** Retry timing belongs to the database alone:

- a failure inside `handle` that cannot be recorded (the database is down) leaves the row `InProgress`; its lock
  expires, the relay publishes it again;
- a worker that dies mid-send never acknowledges; the broker redelivers; the consumer's claim fails (the row is
  `InProgress` and its lock has not expired) and acknowledges; the lock expires and the relay publishes again.

Not requeueing is what prevents a hot loop between a failing consumer and the broker.

Consumer settings: manual acknowledgements, `prefetch = MaxParallelism`, connection recovery on.

### Topology

A durable direct exchange `webhooks.deliveries` and a durable queue `webhooks.deliveries.due` (a quorum queue where
the broker supports it; the design does not depend on replication). A message that cannot be parsed is poison: it is
rejected without requeue into a dead-letter queue `webhooks.deliveries.poison`, and is never retried by the
consumer. Persistent messages, publisher confirms on. (As built, the poison queue is reached through the broker's
default exchange, which routes by queue name, so the work queue's `x-dead-letter-routing-key` is the poison queue's
name and no separate dead-letter exchange exists.) If a queue already exists with different settings the broker
refuses the declaration and the process reports it; nothing is overwritten.

### Failure modes

| What happens | What the system does |
|---|---|
| Broker unreachable | The relay cannot publish and backs off; rows stay due in the database; nothing is lost; deliveries resume when it returns. With the Postgres transport selected nothing changes. |
| Broker restarts, queue contents lost | The leases expire and the relay publishes again. |
| Message duplicated or redelivered | The second claim fails on the state machine; acknowledged and dropped. |
| Consumer crashes mid-send | See the consumer section; the delivery is attempted again after its lock expires, as today. |
| Relay crashes after leasing and before publishing | The leases run out and the rows are announced again; a delay of at most `EnqueueLease`. |
| Message cannot be published or is not confirmed | The relay gives the leases back and the rows are due again at once. |
| Receiver is slow or failing | Unchanged: the attempt is recorded and `MarkFailed` schedules the retry in the database. |
| Malformed message | Dead-lettered, logged once, ignored. |
| Database unreachable | The relay and consumers fail and retry with a backoff, as the worker does today. |
| All workers stopped | Nothing is consumed; the rows stay due. |

A transport that is down for good stops deliveries when `RabbitMq` is selected; the answer is to switch the setting
back to `Postgres`, which needs no data migration. That reversibility is part of the design.

### Security

- Credentials come from the environment, never from the image or the repository. RabbitMQ's default `guest` user is
  only allowed from the same machine, so the Compose file creates a user from `.env`.
- The broker is not published to the host except, optionally, the management interface on `127.0.0.1`.
- No payload or secret in a message (above). TLS to the broker is a setting (`Delivery:RabbitMq:UseTls`), off by
  default for the local Compose setup and documented as required anywhere else.
- Dependabot watches the RabbitMQ image; major versions are excluded.

### Configuration

| Setting | Meaning |
|---|---|
| `Delivery__Transport` | `Postgres` (default) or `RabbitMq` |
| `Delivery__RabbitMq__Host`, `__Port`, `__Username`, `__Password`, `__UseTls` | The broker |
| `Delivery__EnqueueLease` | How long an announced delivery is left alone before it is announced again (default two minutes, must be positive) |

## How it will be tested

The point of a seam is that both sides keep the same promises, so the tests that define the promises run against
both:

- The tests in `DeliveryProcessorTests` (exact payload and signature, a retry that is not sent before its time, dead
  after the last attempt, slow and unreachable receivers, inactive endpoints, a crashed worker picked up after its
  lock, two workers never sending the same delivery) become **contract tests** run once per transport, the
  RabbitMQ run against a real broker in a container (Testcontainers).
- Tests only the RabbitMQ side needs: a duplicate message; a message for a finished delivery; a message for a
  delivery that does not exist; a poison message; the broker stopped and started again mid-run; the broker down while
  events are accepted; publish not confirmed; a consumer killed before it acknowledges; the relay killed after
  leasing and before publishing; a Postgres worker and a RabbitMQ worker running together over the same deliveries.
- A variant of `scripts/smoke.sh` with `Delivery__Transport=RabbitMq` and the broker in Compose, run in CI.
- As in the earlier slices, deliberate breakages (an acknowledgement before the commit, a requeue, a removed lease
  check) must make tests fail, and what was run is written down.

## Plan

Each step is its own pull request, in this order, because the first makes the others small:

1. **Extract the seam, no new behaviour.** `IDeliverySource` and `PostgresDeliverySource`; `DeliveryProcessor`
   keeps the delivery logic. The existing tests pass unchanged; the contract tests are parameterised with one
   transport. *Done when:* every existing test passes, and the two-workers test passes through the seam.
   **Done**, see "What step 1 found" below.
2. **Schema and the lease.** `EnqueuedUntil`, the lease rules in the domain model, the due query as its own tested
   unit (`DeliveryEnqueueStore`). Nothing is published yet. **Done**, see "What step 2 found" below.
3. **The RabbitMQ transport**, in two pull requests.
   - **3a, the code.** Relay, publisher with confirms, topology, consumer, `Delivery__Transport` and its settings,
     the relay as a hosted service of the worker, contract tests and broker failure tests against a real RabbitMQ
     (Testcontainers). **Done**, see "What step 3a found" below.
   - **3b, running it.** The Compose service (`--profile rabbitmq`), `.env.example`, the smoke test with
     `Delivery__Transport=RabbitMq` in CI, the README.
4. **Documentation and release.** README, changelog, the failure table checked against what the tests showed,
   `0.3.0`.

## What step 1 found

Written after the fact, as a record and not as part of the decision.

**What was built.** `IDeliverySource` and `DeliveryClaim` (the private `Claim` made public);
`PostgresDeliverySource` holds the claim SQL and the polling loop that used to be in `DeliveryWorker`;
`DeliveryProcessor` is now only "attempt one delivery" (`DeliverAsync`); `DeliveryWorker` is a few lines that connect
the two. The delivery tests became `DeliveryTransportContract`, an abstract class; `PostgresTransportTests` derives
from it and says how to start a worker. They are unchanged apart from how the harness is created. A second transport
adds one derived class and one driver.

**The loop had no tests, so it was pinned before it was moved.** Six tests of the running background service (it
delivers without being driven, asks again at once while busy, rests when idle and notices new work, survives a failing
batch, stops promptly while resting and while pausing) passed against the old code first. One did not, and that is
the one change in behaviour: **stopping the worker during the pause after an error ended the task as cancelled and
never wrote "Delivery worker stopped."** The exception for the cancelled wait was outside the `try`. A host swallows
it at shutdown, so nobody had noticed. It is fixed and tested. To make the pause testable without a five second wait,
it became a setting, `Delivery__ErrorBackoff`, with the same default (5 s).

**Checking the tests by breaking the code found three more gaps** (17 deliberate breakages, 13 caught the first
time), all closed before this was finished:

- Removing `FOR UPDATE SKIP LOCKED` **did not fail any test.** The two-workers test depends on two claims overlapping
  by chance. A deterministic test now holds a row's lock the way a worker in the middle of claiming would, and
  checks that the claim skips the row instead of waiting.
- Nothing checked `MaxParallelism`. A receiver that answers after a moment now measures how many requests overlap.
- Nothing checked that an idle or failing worker rests and does not spin, or that stopping is not logged as an error.
  The test clock counts how often it is asked for the time (a worker asks once per round), and the harness keeps what
  was logged.

One of the breakages (removing the `Commit` of the claim) made a test **loop for ever**: the loop that drains a
worker only stops when it is handed nothing, and a claim that was never committed always hands something. That would
have hung CI until its time limit. The loop now has a limit and fails instead.

## What step 2 found

Written after the fact, like the section above.

**What was built.** `Delivery.EnqueuedUntil` with `MarkEnqueued`, `ReleaseEnqueue` and `IsEnqueuedAt`, and the rules above;
a migration that adds one nullable column (existing rows are untouched, and the migration can be undone);
`Delivery:EnqueueLease` (default two minutes, must be positive); and `DeliveryEnqueueStore` with `LeaseDueAsync`
(the due query plus "not under a lease", oldest first, one batch, skipping rows another relay is leasing) and
`ReleaseAsync`. It is registered but nothing in production calls it yet, so behaviour is unchanged.

**Thinking it through before building it found two mistakes in this ADR**, both corrected above:

- **A race in the relay.** The text said to publish inside the transaction that holds the row locks and commit
  afterwards. But the consumer claims with `FOR UPDATE SKIP LOCKED`. A message that arrives right after it was
  published finds the row still locked by the relay's open transaction, skips it, and acknowledges and drops the message.
  The row then waits for the whole lease. That would have delayed essentially every delivery. Leasing first and
  committing before publishing removes the race, and also holds no database lock across a network call.
- **A wrong rule for the lease.** See the paragraph about `EnqueueLease` above.

**The "due" condition is written twice** (here and in the claim of `PostgresDeliverySource`), because both must be raw SQL.
A test seeds every state a delivery can be in, asks the relay what it would lease and a polling worker what it
claims, and requires the same answer.

**Checking the tests by breaking the code** is recorded in the pull request for this step.

## What step 3a found

Written after the fact, like the sections above.

**What was built.** `DeliveryRelay` (lease, commit, publish, give the lease back on failure, back off when the broker
struggles), `IDeliveryPublisher` and `RabbitMqDeliveryPublisher` (persistent, mandatory messages, publisher confirms),
`RabbitMqConnection` (one shared connection, opened when first needed, automatic recovery, topology declared once),
`RabbitMqDeliverySource` (the consumer), `DeliveryClaimStore.TryClaimAsync` (claim one delivery by id through the same
SQL conditions as the polling claim), `DeliveryMessage` (a strict reader and writer of the message), the
`Delivery__Transport` setting and `Delivery__RabbitMq__*`, and `DeliveryRelayWorker`, which runs the relay only when
the transport is `RabbitMq`. Without a RabbitMQ setting nothing connects to a broker and nothing changes.

**Tested against a real broker.** The whole transport contract (everything the polling transport must do) passes
against RabbitMQ 4.1 and PostgreSQL in containers, plus tests that only a broker can fail: a lost message is announced
again once its lease runs out, a delivery a polling worker took first is not attempted again when its message arrives,
events accepted while the broker is down are delivered after it returns, two consumers on one queue never attempt the
same delivery twice, a consumer survives a broker restart (`rabbitmqctl stop_app` and `start_app`), and a broker that
is not there when the consumer starts is waited for.

**Checking the tests by breaking the code** (22 deliberate breakages): 17 were caught the first time. Four survived
and three of them showed real gaps, now closed with tests: nothing checked that an idle relay rests (it would have
polled the database in a tight loop), nothing checked the prefetch limit (a worker would have hoarded messages other
workers could have taken), and nothing checked the publish timeout.

**That last one was a real bug, not only a missing test.** The timeout was handed to the client as a cancellation
token. A broker that has blocked its publishers (here by raising its memory alarm) does not react to it, and the
publish waited for more than 20 seconds with a one second limit. The relay would have hung for as long as the broker
was blocked. The limit is now enforced around the call itself, closing the channel is bounded as well, and a test
blocks a real broker to prove it.

**One breakage still survives, and it is not a gap in behaviour.** Acknowledging a message whose handling was cancelled
by shutdown changes nothing observable, because stopping closes the channel and a closed channel cannot acknowledge:
the broker gives the message back either way. The code leaves it unacknowledged on purpose and says why, but no test
can tell the two apart.

**Smaller findings.** A suite of this size over per-test databases exhausted PostgreSQL's connections
("too many clients") and looked like a flaky test in code that had nothing wrong; the test harness now releases its
connection pool when it is disposed. The poison queue is reached through the default exchange, so there is no separate
dead-letter exchange (see Topology). If the database is unreachable while a message is claimed, the message is
acknowledged and the delivery waits for its lease to run out (at most `EnqueueLease`), instead of coming back at once
and spinning.

**Not done yet** (step 3b): the Compose service, `.env.example`, the smoke test with this transport in CI, and the
README. Until then the transport can be tried only through the tests or by hand.

## Limits and open questions

- **Ordering is not preserved** and never was: concurrent workers and retries reorder deliveries to one endpoint.
  Receivers cannot assume order. This is not new, but a broker invites the assumption, so it is stated.
- **No per-endpoint fairness or rate limit.** One slow receiver can occupy consumers; the existing receiver timeout
  bounds that. A per-endpoint concurrency limit is a separate decision.
- The client is RabbitMQ.Client 7.2.2 (asynchronous channels; publisher confirmation tracking turned on per channel,
  so a publish completes when the broker has confirmed that message). The design does not depend on a method name.
- Quorum or classic queue, and the prefetch value, are settings to measure, not to guess. No performance claim is made
  here: the case for the broker is decoupling and one poller instead of many, not latency (see the first section).
- Two sources of work for one delivery means the metrics of "due" and "in flight" must come from the database, not
  from the broker's queue length. Observability beyond log lines is not part of this plan.
- Running a broker is an operational cost this project's README should say plainly.
