# 0002 - Store the event payload as `text`, not `jsonb`

Status: accepted

## Context

Every delivery is signed with HMAC over the request body. The receiver recomputes
the signature from the bytes it receives. PostgreSQL's `jsonb` type normalizes
JSON: it drops insignificant whitespace, reorders keys and collapses duplicate keys.

## Decision

Store `events.PayloadJson` as `text`. The payload is kept byte for byte as the
sender provided it, and the signature is computed over exactly those bytes.

## Consequences

- No JSON querying inside the database. That is acceptable: the service never
  interprets the payload.
- Invalid JSON would be stored too, so validation must happen at the API boundary.
- A persistence test checks that a payload with unusual whitespace comes back unchanged.
