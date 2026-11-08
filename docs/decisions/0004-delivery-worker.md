# 0004 - Delivery worker design

Status: accepted

## Context

Accepted events have to reach receivers reliably, with several workers allowed to run
at the same time, and without a message broker in v0.1.

## Decision

### Claiming work

A worker claims due deliveries inside one transaction:

```sql
SELECT * FROM deliveries
WHERE (Status = 'Pending'    AND NextAttemptAt <= now)
   OR (Status = 'InProgress' AND LockedUntil   <= now)
ORDER BY NextAttemptAt
LIMIT n
FOR UPDATE SKIP LOCKED
```

`SKIP LOCKED` makes a worker ignore rows another worker is claiming instead of waiting,
so workers never get the same delivery and never block each other. The state change
(`StartAttempt`) is performed by the domain model, not by SQL, so the transition rules
live in one place.

A test runs two workers over 30 deliveries and checks that each is claimed exactly once.
Without `SKIP LOCKED` the same test claims 60 (verified by removing it temporarily).

### Crashed workers

A claim lasts `LockDuration`. If a worker dies, the delivery becomes claimable again when
the lock expires. `LockDuration` must be longer than `RequestTimeout`, otherwise a slow
request would be picked up by a second worker while still running (checked at startup).

### Sending

- The payload bytes that are signed are the bytes that are sent.
- Redirects are not followed (a receiver must not redirect the service to another address).
- Only the first 2 KB of a response are read and 500 characters are stored.
- Every attempt is stored, including timeouts and connection errors.

### Results

Any 2xx response is success. Everything else, including 3xx, is a failure and is retried
with exponential backoff and jitter until `MaxAttempts`, then the delivery is `Dead`.

### Late results

After sending, the worker reloads the delivery. If the claim was taken over meanwhile
(the request outlived the lock), its result is discarded and the newer attempt wins.
A very small window remains between that check and the save. Closing it fully needs
an optimistic concurrency token on the delivery row, left for later.

### Signature format

`Webhook-Id`, `Webhook-Timestamp` and `Webhook-Signature: v1,<base64 HMAC-SHA256>` over
`{id}.{timestamp}.{body}`. Modelled on the Standard Webhooks convention from memory;
compatibility with existing verifier libraries has not been verified.
`Webhook-Id` is the event id and stays the same across retries, so receivers can use it
to detect duplicates.

## Consequences

- Delivery is at-least-once: a receiver can see the same event twice (for example when it
  answered 200 but the response was lost) and must be idempotent.
- A shutdown during a send leaves the delivery claimed until the lock expires, which delays
  that retry by up to `LockDuration`.
- Endpoint URLs are not yet restricted: a tenant can point an endpoint at an internal
  address (SSRF). Must be fixed before public exposure.
