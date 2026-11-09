# Webhook Service

[![CI](https://github.com/ridvan-byr/webhook-service/actions/workflows/ci.yml/badge.svg)](https://github.com/ridvan-byr/webhook-service/actions/workflows/ci.yml)

A self-hostable service that delivers webhooks reliably: signed requests,
retries with backoff, a dead-letter state, and a full delivery history.

> Status: early scaffold (v0.1 in progress). Nothing here is production-ready yet.

## Why

Sending an HTTP request to another system is easy. Doing it reliably is not:
receivers crash, time out, or return errors; the same event can be delivered
twice; and nobody can tell afterwards what actually happened. This project is a
from-scratch implementation of those problems, with the design decisions
written down in [`docs/decisions`](docs/decisions).

It is a learning and portfolio project. Hosted and mature alternatives such as
Svix exist; this project does not try to replace them.

## Planned stack

- .NET (ASP.NET Core, background worker), EF Core
- PostgreSQL (queue via `FOR UPDATE SKIP LOCKED`)
- Next.js + TypeScript admin dashboard
- Docker Compose, GitHub Actions
- Later: RabbitMQ transport, SQL Server support, Azure deployment

## Layout

```
src/WebhookService.Core            domain model and interfaces
src/WebhookService.Infrastructure  persistence and transports
src/WebhookService.Api             HTTP API
src/WebhookService.Worker          delivery worker
tests/WebhookService.Tests         tests
web                                admin panel (Next.js)
docs/decisions                     architecture decision records
```

## Run locally

Requirements: .NET 8 SDK, Docker.

```bash
docker compose up -d
dotnet build
dotnet test
dotnet run --project src/WebhookService.Api
```

Then open `/health`.

Persistence tests start their own PostgreSQL container with Testcontainers, so
Docker must be running when you run `dotnet test`.

## API

### Authentication

Every route except `/health` needs an API key: `Authorization: Bearer whk_...`.
Keys belong to a tenant, and a tenant only ever sees its own data
([ADR 0005](docs/decisions/0005-api-key-authentication.md)).

The operator creates tenants. This is switched off unless an admin key is configured, for
example with an environment variable (use a long random value, never commit it):

```bash
export Admin__ApiKey="<long random value>"
```

Create a tenant. The response contains its first API key, **shown only once**:

```bash
curl -X POST http://localhost:5000/admin/tenants \
  -H "X-Admin-Key: $Admin__ApiKey" \
  -H "Content-Type: application/json" \
  -d '{"name":"Acme"}'
```

A tenant can create more keys, list them (only the public prefix is shown) and revoke them:

| Request | Result |
|---|---|
| `POST /api-keys` | New key, shown once |
| `GET /api-keys` | Name, prefix, creation and revocation time of each key |
| `DELETE /api-keys/{id}` | Revokes a key at once (`409` if it is the key making the request) |

### Sending events

Register a receiver. The signing secret is returned once:

```bash
curl -X POST http://localhost:5000/endpoints \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/hook"}'
```

Send an event. Repeating the same `Idempotency-Key` never creates a second event:

```bash
curl -X POST http://localhost:5000/events \
  -H "Authorization: Bearer $API_KEY" \
  -H "Idempotency-Key: order-42" \
  -H "Content-Type: application/json" \
  -d '{"type":"order.created","payload":{"id":42}}'
```

Read what happened (all of these are limited to the caller's tenant):

| Request | Result |
|---|---|
| `GET /events/{id}` | The event, its payload and one summary per delivery |
| `GET /deliveries?status=Dead&limit=50` | Deliveries, newest first, each with its event type and endpoint address. `status` is `Pending`, `InProgress`, `Succeeded` or `Dead` |
| `GET /deliveries/{id}` | One delivery with every attempt: status code, duration, response snippet, error |
| `POST /deliveries/{id}/resend` | Queues a `Dead` delivery again with a fresh set of attempts (`409` for any other status) |
| `GET /me` | The tenant and the key behind the credentials. Never contains the key itself |

Switch an endpoint off, or on again. An inactive endpoint gets no new events; deliveries that
already exist are not affected:

```bash
curl -X PATCH http://localhost:5000/endpoints/$ENDPOINT_ID \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"isActive":false}'
```

Run the delivery worker in a second terminal (the API applies the database migrations in
Development, so start it first):

```bash
dotnet run --project src/WebhookService.Worker
```

Every request to a receiver carries these headers, so the receiver can verify it:

| Header | Meaning |
|---|---|
| `Webhook-Id` | Event id. Identical on every retry, use it to detect duplicates |
| `Webhook-Timestamp` | Unix seconds when the attempt was sent |
| `Webhook-Signature` | `v1,` + base64 HMAC-SHA256 of `{id}.{timestamp}.{body}` using the endpoint secret |

Delivery is at-least-once. See [ADR 0004](docs/decisions/0004-delivery-worker.md).

### Where webhooks may be sent

Tenants choose the destination URL, so the service refuses to contact non-public addresses
(localhost, private networks, the cloud metadata address, ...). The check happens when the worker
connects, so it also holds against names that change their DNS answer later
([ADR 0006](docs/decisions/0006-ssrf-protection.md)). By default only `https` URLs are accepted.

Two settings control this, read by both the API and the worker. The defaults are the safe ones; the
Development settings relax them so that a receiver on `localhost` works. **Never enable them for a public
deployment:**

```json
"Security": { "AllowPrivateNetworks": false, "RequireHttps": true }
```

Run the worker in a network that cannot reach your internal services at all. The application check is
the second line of defence, not the first.

### Known limitations

- No rate limiting, so keys could be guessed by brute force (the 256-bit secret makes that
  impractical, but failed attempts are not throttled or logged yet).
- A public address can still belong to the operator's own infrastructure, and ports are not restricted.
- No event-type filtering per endpoint.

## Admin panel

A Next.js application in [`web/`](web). Work in progress: so far it is the frame (menu, light and dark theme,
security headers). The screens that show your deliveries come in the next steps, and so does signing in.
The design decisions, and why a few packages are pinned, are in
[ADR 0007](docs/decisions/0007-admin-panel.md).

Requirements: Node 20.9 or later. Node 20 reached end of life in April 2026, so Node 22 or 24 is recommended.

```bash
cd web
npm ci
npm run dev
```

Open http://localhost:3000. Configuration is read from `web/.env.local`; copy `web/.env.example` for the
values that exist so far. They are read on the server only and are never sent to the browser.

| Command | What it does |
|---|---|
| `npm test` | Unit and component tests (Vitest, React Testing Library, axe-core) |
| `npm run lint` | ESLint with the strict `jsx-a11y` accessibility rules |
| `npm run typecheck` | Generates the route types, then runs the TypeScript compiler |
| `npm run build` | Production build |
| `npm run audit:prod` | Fails on a known vulnerability in a package that ships with the panel |

### Database migrations

The EF Core CLI is a local tool, restore it once:

```bash
dotnet tool restore
```

Create a migration after changing the model:

```bash
dotnet tool run dotnet-ef migrations add <Name> --project src/WebhookService.Infrastructure --output-dir Persistence/Migrations
```

## Roadmap

- [x] v0.1 events, endpoints, signed delivery, retries, delivery history, manual re-send
- [ ] v0.2 CI (done), API-key auth (done), SSRF protection (done), Next.js dashboard (frame done: sign-in, screens and data to come)
- [ ] v0.3 RabbitMQ transport
- [ ] v0.4 SQL Server support
- [ ] v0.5 Azure deployment guide
