# 0003 - Temporary tenant header and idempotency rules

Status: accepted (tenant header is temporary)

## Context

Events and endpoints belong to a tenant. Real authentication (API keys) is planned
for v0.2, but the endpoints have to exist and be testable before that.

## Decision

### Tenant identification (temporary)

The caller sends its tenant id in the `X-Tenant-Id` header. This can be faked by
anyone, so it must not be exposed publicly. It is replaced by API-key
authentication in v0.2, where the tenant is derived from the key.

All queries are filtered by tenant. A resource of another tenant behaves as if it
does not exist (404), so ids cannot be probed.

### Idempotency

- The key comes from the `Idempotency-Key` header or the `idempotencyKey` body
  field (the header wins). It is optional.
- Same tenant + same key + same content: the original event is returned with
  `200 OK` and `duplicate: true`. Nothing new is stored.
- Same tenant + same key + different content: `409 Conflict`.
- The guarantee comes from a unique database index, not from a read-then-write
  check. Two simultaneous requests cannot both insert. The loser catches the
  unique violation and reports the winner's event. A read before the insert is
  kept only as a cheap fast path.

### Atomic acceptance

The event and all its delivery tasks are saved in one `SaveChanges` call (one
transaction). An accepted event can never exist without its deliveries.

## Consequences

- Deliveries are created for the endpoints that are active at ingestion time.
  An endpoint created a moment later does not receive that event.
- Event-type filtering per endpoint does not exist yet; every active endpoint of
  the tenant receives every event.
- Endpoint URLs are not yet checked against private network addresses (SSRF).
  This must be solved before the service is exposed publicly.
