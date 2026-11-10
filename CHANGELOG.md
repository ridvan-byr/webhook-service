# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/). Before 1.0 anything may still change.

## [Unreleased]

### Added

- Metrics from the worker, with the .NET metrics API (no new package): attempts by outcome, attempt duration, how long
  a delivery waited after it was due (`webhook.delivery.pickup.delay`), and for RabbitMQ what the relay announced and
  what became of each message. Tags never carry an id, an address or a tenant. See [docs/performance.md](docs/performance.md).
- `scripts/loadtest.sh` and `scripts/loadtest.mjs`: a load test that runs the whole stack with either transport and
  reads the result from the service's own timestamps. The first results are in [docs/performance.md](docs/performance.md).
- `DELIVERY_BATCH_SIZE` and `DELIVERY_MAX_PARALLELISM` in the Compose file (the defaults are unchanged: 20 and 8).
- `LOG_REQUESTS=false` for the demo receiver.

### Changed

- **The RabbitMQ relay publishes a batch at once instead of one message at a time**, and waits for the confirms
  together. The load test showed the one-by-one version delivered about 134 messages per second against 462 for the
  database transport (a durable queue confirms after a disk write, so every message cost a round trip). It now
  reaches about 280 with the default settings and about the same as the database transport when the worker takes more
  at a time. RabbitMQ does not make deliveries faster than the database transport; that is measured now, not only
  argued.

## [0.3.0] - 2026-11-10

### Upgrading

- **Nothing changes unless you ask for it.** `Delivery__Transport` defaults to `Postgres`, which behaves as before.
  One migration runs on start: a nullable `EnqueuedUntil` column on `deliveries` (existing rows untouched). It is
  only used by the RabbitMQ transport.

### Added

- **An optional RabbitMQ transport.** `docker compose --profile rabbitmq up -d` starts a broker and
  `DELIVERY_TRANSPORT=RabbitMq` in `.env` makes the worker use it: a relay in the worker announces due deliveries
  with publisher confirms, and workers act on the messages. A message carries only a delivery id, never a payload,
  address or secret; the database stays the source of truth, so a duplicate, late or invented message cannot make
  anything happen twice. If the broker is down, events are still accepted and are delivered when it returns; setting
  the transport back to `Postgres` needs no migration. Messages that cannot be understood go to a poison queue.
  Settings under `Delivery__RabbitMq__` (there is no default password; the broker refuses to start without one; TLS
  is a setting). The broker is not published to the host; its management page is on `127.0.0.1` only. Design,
  failure cases and what building it found: [ADR 0010](docs/decisions/0010-rabbitmq-transport.md).
- Tested against a real RabbitMQ and PostgreSQL in containers: the whole transport contract, a broker that is
  stopped and restarted, lost messages, two consumers on one queue, and a broker that blocks publishers.
  `scripts/smoke.sh rabbitmq` and a second CI job check end to end that a delivery goes through the broker.
- `Delivery__ErrorBackoff` (default 5 seconds, as before): how long the worker waits after a batch failed.
- `Delivery__EnqueueLease` (default two minutes): how long an announced delivery is left alone before it is announced
  again.

### Changed

- Internal: the polling and claiming of due deliveries moved out of the worker into `PostgresDeliverySource` behind
  `IDeliverySource`. Behaviour is unchanged apart from the fix below.
- Dependabot does not move RabbitMQ to a new major version on its own.

### Fixed

- Stopping the worker while it was pausing after a failed batch ended its task as cancelled and did not log that it
  had stopped. It now stops cleanly.
- Documentation: the README and ADR 0001 said the queue is behind an interface. It is not: the `deliveries` table is
  the queue and the worker claims rows with SQL. Both are corrected.

### Known limits

- RabbitMQ does not make deliveries faster: the relay polls the database at `Delivery__PollInterval` too. The ADR
  first claimed otherwise and was corrected.
- While no worker runs, the relay announces each due delivery again every lease, so the queue grows with harmless
  duplicate messages until a worker returns.
- TLS to the broker is a setting that has not been exercised against a broker with TLS enabled.

## [0.2.1] - 2026-11-09

### Upgrading

- **Behind a reverse proxy, set `Security__TrustedProxyNetworks__0`** (for example `10.0.0.0/8`) to the network of
  the proxy. Without it every request comes from the proxy's address, so all your clients share one limit and a
  few wrong keys from one of them can make the API refuse all of them for a minute. Docker Compose users who reach
  the API directly on `localhost` need to change nothing.
- Clients that retry a request with a wrong key in a tight loop will now meet `429`. Honour `Retry-After`.

### Added

- **A limit on failed attempts to authenticate.** A client that presents wrong API keys (or a wrong admin key) more
  than 20 times in a minute gets `429 Too Many Requests` with `Retry-After`, and is not answered about keys at all
  until the window has passed. The panel's sign-in form has its own, stricter limit (10 a minute) so it never makes
  the API refuse the panel for everyone. Behind a reverse proxy, `Security__TrustedProxyNetworks__0` names the
  networks whose `X-Forwarded-For` is believed. ([ADR 0009](docs/decisions/0009-failed-authentication-limit.md))

### Security

- The API and the panel now log once when a client reaches the limit, without what was presented.

### Changed

- The smoke test also checks that the 21st wrong key in a row is the first one refused.

### Known limitations

The ones of 0.2.0 still apply, except that failed attempts are now limited. In addition:

- Only failed attempts are limited. A valid tenant can send as many requests and events as it likes.
- The count is kept in memory: each running copy of the API counts for itself, and it starts again with the process.
- The panel's sign-in form has one count for everybody, so someone can make it wait for up to a minute. Sessions
  that are already signed in are not affected. ([ADR 0009](docs/decisions/0009-failed-authentication-limit.md))

## [0.2.0] - 2026-11-09

The first release. Everything below is new.

### Added

- **Delivery.** Events are accepted over an HTTP API and delivered to registered endpoints by a background worker,
  at least once, with exponential backoff and jitter, a `Dead` state after the last attempt, and a manual re-send.
  Several workers can run at the same time. A repeated `Idempotency-Key` never creates a second event.
  ([ADR 0001](docs/decisions/0001-at-least-once-delivery.md), [0002](docs/decisions/0002-store-payload-as-text.md),
  [0003](docs/decisions/0003-tenant-header-and-idempotency.md), [0004](docs/decisions/0004-delivery-worker.md))
- **Signed requests.** `Webhook-Id`, `Webhook-Timestamp` and an HMAC-SHA256 `Webhook-Signature` on every request;
  the payload is stored and sent exactly as received.
- **Tenants and API keys.** Keys are created, listed and revoked through the API; only a hash is stored and it is
  compared in constant time. Every route needs a key unless it opts out.
  ([ADR 0005](docs/decisions/0005-api-key-authentication.md))
- **Protection against server-side request forgery.** The address is checked when the worker connects, not only when
  it is registered; HTTPS only and no private networks by default.
  ([ADR 0006](docs/decisions/0006-ssrf-protection.md))
- **Admin panel** (Next.js): sign in with an API key, list and filter deliveries with automatic refresh, see every
  attempt of a delivery and send a dead one again, see an event with its payload, send a test event, and list, switch
  and add endpoints (the signing secret of a new endpoint is shown once). Light and dark theme, usable on a phone,
  checked with axe-core and strict accessibility lint rules. ([ADR 0007](docs/decisions/0007-admin-panel.md))
- **One-command start.** `docker compose up` runs PostgreSQL, the API, the worker and the panel. A demo receiver
  (`--profile demo`) and a smoke test (`scripts/smoke.sh`, also run in CI) are included.
  ([ADR 0008](docs/decisions/0008-containers.md))
- API additions for the panel: `GET /me`, the event type and endpoint address in delivery summaries, and
  `PATCH /endpoints/{id}` to switch an endpoint on or off.
- Continuous integration (build and tests, the panel on Node 24 and Node 20.18, the Docker smoke test) and
  Dependabot with grouped updates.

### Security

- The panel never exposes the API key to the browser. It is kept in an AES-256-GCM encrypted `httpOnly`,
  `SameSite=Strict` cookie, `Secure` and `__Host-` prefixed in production, with an 8 hour lifetime.
- A nonce-based Content-Security-Policy without `unsafe-inline`; every page and every Server Action checks the
  session itself; nothing written by other systems is rendered as markup.
- The containers run as an unprivileged user with all capabilities dropped (PostgreSQL excepted), the API and the
  panel are published on `localhost` only, and PostgreSQL is not published.

### Known limitations

- No rate limiting on the API or on the sign-in page.
- Switching an endpoint back on does not check its address again.
- No event-type filtering per endpoint.
- No HTTPS in front of the panel; put it behind a reverse proxy for anything but a local try-out.
- The panel is tested on Node 24 and on Node 20.18, which is why a few packages are pinned
  (see [ADR 0007](docs/decisions/0007-admin-panel.md)).
- Nothing has been run in production.

[Unreleased]: https://github.com/ridvan-byr/webhook-service/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/ridvan-byr/webhook-service/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/ridvan-byr/webhook-service/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/ridvan-byr/webhook-service/releases/tag/v0.2.0
