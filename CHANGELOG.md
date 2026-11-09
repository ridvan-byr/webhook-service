# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project uses [Semantic Versioning](https://semver.org/). Before 1.0 anything may still change.

## [Unreleased]

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

[0.2.0]: https://github.com/ridvan-byr/webhook-service/releases/tag/v0.2.0
