# Webhook Service

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

## API (v0.1, work in progress)

Authentication is not implemented yet. For now the tenant is passed in an
`X-Tenant-Id` header (a GUID). See [ADR 0003](docs/decisions/0003-tenant-header-and-idempotency.md).

Register a receiver. The signing secret is returned once:

```bash
curl -X POST http://localhost:5000/endpoints \
  -H "X-Tenant-Id: 11111111-1111-1111-1111-111111111111" \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/hook"}'
```

Send an event. Repeating the same `Idempotency-Key` never creates a second event:

```bash
curl -X POST http://localhost:5000/events \
  -H "X-Tenant-Id: 11111111-1111-1111-1111-111111111111" \
  -H "Idempotency-Key: order-42" \
  -H "Content-Type: application/json" \
  -d '{"type":"order.created","payload":{"id":42}}'
```

Events are stored, but nothing is delivered yet: the delivery worker is the next step.

### Known limitations

- Tenant header is not secure (temporary).
- No SSRF protection on endpoint URLs yet.
- No event-type filtering per endpoint.

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

- [ ] v0.1 events, endpoints, signed delivery, retries, tests
- [ ] v0.2 Next.js dashboard, API-key auth, CI
- [ ] v0.3 RabbitMQ transport
- [ ] v0.4 SQL Server support
- [ ] v0.5 Azure deployment guide
