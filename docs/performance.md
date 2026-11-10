# Performance

What this service does under load on one machine, how it was measured, and what the numbers do and do not say.
They are here to compare the two transports with each other and to find problems, not to promise a rate anywhere
else.

## How it was measured

`scripts/loadtest.sh [postgres|rabbitmq]` starts the whole stack with Docker Compose (API, worker, PostgreSQL, the
demo receiver, and RabbitMQ for the second transport), sends events through the API with `scripts/loadtest.mjs`, waits
until every delivery has succeeded, prints the result, and removes everything. Nothing is installed besides Docker and
Node.

- **ingest** is measured by the client: how fast the API accepts events and how long each request takes.
- **throughput** is deliveries finished per second, from the first delivery being stored to the last attempt
  ending. **pickup** is how long a delivery waited between being stored and its first attempt starting. Both are read
  from timestamps the service itself wrote to the database, not from the client.
- The receiver answers `200` at once and does not log, so it is not the limit. A real receiver is slower, and
  then the receiver, not the service, sets the rate.

**The machine:** a laptop with an Intel Core i5-14500HX (20 logical cores) and 16 GB of RAM, Windows 11, Docker
Desktop with 20 CPUs and about 8 GB. Everything, including the load generator, ran on it. PostgreSQL 16 and
RabbitMQ 4.1 in containers with their default settings. The worker had its default settings (batch of 20, 8 requests
at a time) unless it says "tuned".

**How much to trust it.** Each case was run once, so differences of about 15 percent are within what a second run could
change. Two runs of the same case early on gave 462 and 468 deliveries per second for the database transport, which
suggests the noise is smaller than that, but that is two runs, not a study. There is one worker container, one
database, and one broker.

## Results

Two thousand events to one endpoint (one thousand for the paced runs), sent 32 at a time.

| Situation | Transport | Delivered per second | Waited before the first attempt, median / 95th percentile |
|---|---|---:|---:|
| Burst, defaults | PostgreSQL | 468 | 2.1 s / 2.7 s |
| Burst, defaults | RabbitMQ | 278 | 3.5 s / 5.3 s |
| Burst, tuned (batch 100, 32 at a time) | PostgreSQL | 678 | 1.5 s / 1.5 s |
| Burst, tuned (batch 100, 32 at a time) | RabbitMQ | 615 | 1.3 s / 1.3 s |
| Paced, 100 events per second | PostgreSQL | 93 | 0.54 s / 1.0 s |
| Paced, 100 events per second | RabbitMQ | 94 | 0.53 s / 1.1 s |

The API accepted between 1,000 and 1,500 events per second in every run, which is faster than the worker delivers
them, so in a burst the "waited" column is mostly the time spent in the backlog and says how fast the worker works
through it, not how quick a single delivery is. The paced rows, below what the worker can deliver, are the ones that
show what a delivery waits when nothing is queued.

## What this says

1. **RabbitMQ does not make deliveries faster, and with the default settings it was slower.** When nothing is queued
   (the paced rows) the two transports wait the same: about half a second on average, up to about a second. That is
   the poll interval (`Delivery__PollInterval`, one second) either way, because the relay polls the database just as the
   polling worker does. This is what [ADR 0010](decisions/0010-rabbitmq-transport.md) says after it was corrected, and
   now it is measured. The reason to run a broker is not speed.
2. **The first version of the relay was much slower than that: 134 deliveries per second against 462.** It sent the
   messages of a batch one after the other and waited for the broker to confirm each one before sending the next. A
   durable queue confirms after writing to disk, so every message cost a round trip and a write. Sending a batch at
   once and waiting for the confirms together brought it to about 280. This was found by running the load test, not by
   reading the code, and is why the test exists.
3. **The remaining gap closes when the worker takes more at a time.** With a batch of 100 and 32 requests at a time
   both transports deliver more than 600 per second, and the difference between them (678 and 615) is within the noise.
   The defaults are conservative on purpose.
4. **The API is not the limit.** It accepted around 1,300 events per second on this machine, with a request taking about
   20 ms at the median and 130 ms at the 99th percentile.

## What this does not say

- Nothing about a slow receiver, many endpoints, large payloads, several workers, a database on another machine, or a
  broker with replication. Each of those changes the result, some a lot.
- Nothing about how long it holds up. These runs last seconds.
- Nothing about a different computer. The ratio between the transports is more likely to carry over than the absolute
  numbers, and even that is a guess.

## What the service reports about itself

The worker records, with the .NET metrics API under the meter `WebhookService` (no extra package; anything that
understands it can collect it, for example `dotnet-counters monitor --name WebhookService.Worker WebhookService`):

| Metric | What it tells you |
|---|---|
| `webhook.delivery.attempts` (by `outcome`: `succeeded`, `failed`, `dead`, `discarded`) | How many attempts finished, and how they ended. |
| `webhook.delivery.attempt.duration` (ms) | How long the receivers take to answer. |
| `webhook.delivery.pickup.delay` (ms) | How long a delivery waited after it was due before an attempt started. The one to alert on: it grows when workers cannot keep up. |
| `webhook.relay.leased`, `webhook.relay.unconfirmed` | RabbitMQ only: what the relay announced, and what the broker did not confirm. |
| `webhook.message.outcomes` (by `outcome`) | RabbitMQ only: what became of each message a worker received. |

Tags are a short fixed set of words. They never carry a delivery id, an address or a tenant, which a test checks.
Nothing exports them anywhere by default; sending them to an observability backend is a setting that has not been
added yet.

## Running it yourself

```bash
scripts/loadtest.sh postgres
scripts/loadtest.sh rabbitmq
EVENTS=5000 CONCURRENCY=64 DELIVERY_BATCH_SIZE=100 DELIVERY_MAX_PARALLELISM=32 scripts/loadtest.sh rabbitmq
EVENTS=1000 RATE=100 scripts/loadtest.sh postgres      # paced: what a delivery waits when nothing is queued
```

It needs ports 3000 and 5005 free, like `scripts/smoke.sh`.
