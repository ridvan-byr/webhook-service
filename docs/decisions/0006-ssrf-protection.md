# 0006 - Protection against server-side request forgery (SSRF)

Status: accepted

## Context

Tenants choose the URL the worker sends requests to. A hostile tenant can pick an address
inside the network the service runs in, for example the cloud metadata address
`169.254.169.254`, `localhost`, or a database on a private network. The worker would make
that request from the inside, and the start of the response is stored and shown back to the
tenant (`GET /deliveries/{id}`), so this is also a way to read internal data.

## Decision

### The real protection is at connection time

A URL or a host name can be fine when it is registered and point somewhere else later (DNS
rebinding), so checking at registration is not enough. Every connection of the worker is
opened by `SafeConnector`, which:

1. resolves the host name itself,
2. refuses the request if **any** returned address is not public (a name that mixes public and
   private answers is refused as a whole),
3. connects to one of the addresses it just checked, without a second lookup.

Step 3 closes the gap between "what was checked" and "what was connected to".

### What counts as public

`IpAddressPolicy` refuses loopback, private ranges, link-local (including the metadata address),
carrier-grade NAT, documentation, benchmarking and reserved ranges, multicast and broadcast, and the
IPv6 equivalents. IPv6 addresses that only wrap an IPv4 address (IPv4-mapped, NAT64, 6to4,
IPv4-compatible) are judged by the IPv4 address inside, otherwise `::ffff:127.0.0.1` would pass.

### Registration check (convenience, not protection)

`EndpointUrlValidator` gives an early, readable error: https only (by default), no credentials in the
URL, no private literal addresses, no names like `localhost`, `*.internal`, `*.local` or single-label
names, and no disguised numeric hosts (`2130706433`, `0x7f.0.0.1`).

### Other measures

- Redirects are not followed (ADR 0004).
- The HTTP client does not use a proxy from the environment, so the connection really goes to the checked address.
- The error shown to the tenant for a blocked destination is generic. The resolved address is only in the server log.

### Configuration

`Security:AllowPrivateNetworks` (default `false`) and `Security:RequireHttps` (default `true`), read by both
the API and the worker. The Development settings relax both so a receiver on `localhost` works. Never
enable them for a public deployment.

## Verified by

Tests with a real local receiver and simulated DNS answers prove that nothing reaches the receiver when a
destination or a resolved address is private, including the mixed-answer case. Each protection (the
address check, the wiring into the HTTP client, the IPv6 unwrapping, the registration check) was removed
temporarily to confirm that tests fail without it.

## Limits

- The list of reserved ranges is maintained by hand and can become outdated.
- A **public** address can still be an internal service of the operator (for example a public IP of the
  operator's own infrastructure). Only the operator can know that.
- Ports are not restricted: a tenant can target any port on a public host.
- The blocked address is not logged per tenant and there is no rate limiting of abusive tenants.

**Defence in depth:** run the worker in a network that cannot reach internal services at all (an egress
firewall or network policy that allows only the public internet). The application check is the second
line of defence, not the first.
