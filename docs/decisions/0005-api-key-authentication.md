# 0005 - API key authentication

Status: accepted

## Context

Until now the caller named its own tenant in an `X-Tenant-Id` header, which anyone could
fake (ADR 0003 called it temporary). Real authentication is needed before the service is
exposed.

## Decision

### Keys

A key looks like `whk_<12 hex prefix>_<43 characters from 32 random bytes>`.

- The prefix is public. It is stored in clear, has a unique index, and is only used to find
  the right row.
- Only a **SHA-256 hash** of the whole key is stored. A database leak does not reveal usable
  keys. The key itself exists only in the response that creates it and can never be shown again.
- A plain fast hash is enough because the keys are long and random. Slow password hashes
  (bcrypt, Argon2) exist to protect low-entropy, human-chosen secrets.
- Hashes are compared in constant time.
- "Unknown key", "wrong secret" and "revoked" give the same answer (`401`).

### The tenant comes from the key

The authentication handler turns a valid key into a signed-in principal with `tenant_id` and
`api_key_id` claims. No route reads the tenant from the request. The `X-Tenant-Id` header was
removed, and a test checks that it no longer grants access.

### Secure by default

A fallback authorization policy requires an authenticated user for every endpoint. Only
`/health` and the admin route opt out with `AllowAnonymous()`. A route added later and
forgotten is protected rather than open.

The handler looks up the key service only when an `Authorization` header is present, so a
request without credentials (like `/health`) never touches the database.

### Creating tenants

`POST /admin/tenants` creates a tenant and its first key. It is protected by an operator
key from configuration (`Admin:ApiKey`), compared in constant time. If no admin key is
configured, the route answers `404`: the feature is off. The admin key is never committed;
it comes from the environment.

### Managing keys

A tenant can create, list and revoke its own keys. Listing shows only name, prefix and
timestamps. Revoking takes effect on the next request. Revoking the key that authenticates the
request is refused (`409`), because it would lock a tenant out if it was their only key.
Another tenant's key id answers `404`.

## Verified by

Each security control was removed temporarily to check that tests fail without it: the
secret comparison, the revoked check, the default-deny policy and the admin key check.

## Consequences

- Events, endpoints and deliveries still carry a plain tenant id without a foreign key to the
  `tenants` table. Only `api_keys` has one.
- There was no rate limiting or throttling of failed attempts when this was written. It was added later, see
  [ADR 0009](0009-failed-authentication-limit.md).
- There is no `last used` tracking: it would add a database write to every request.
- A lost key cannot be recovered. The tenant creates a new one and revokes the old one.
- A lost admin key means changing the configuration and restarting.
