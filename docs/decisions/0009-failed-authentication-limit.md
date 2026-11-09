# 0009 - Limiting failed attempts to authenticate

Status: accepted.

## Context

Until now the API checked an API key as often as it was asked. Keys are long and random (a 12-character public
prefix and a 256-bit secret), so guessing one is not realistic. What remained was cheaper to do and still
unwelcome: a flood of requests with wrong keys, each costing a database lookup and a log line, and the admin key
(`X-Admin-Key`), which is a secret in the same way. The README listed this as a known limitation.

## Decisions

### What is counted

Only attempts that present credentials that turn out to be wrong: a key the API does not accept, a wrong
`Authorization` scheme, or a wrong admin key. Not counted: a request with no credentials at all (nothing was
guessed), a request that succeeds, and the admin route when no admin key is configured (it answers 404 and there is
nothing to guess). A successful request does not reset the count, otherwise someone with one valid key could
interleave it with guesses forever.

### One sliding window per client

A client may fail `MaxFailures` times (default 20) within `Window` (default one minute). The window slides: a failure
counts until it is older than the window, so two bursts at the edge of two windows do not get a fresh start each.
The next request is answered `429 Too Many Requests` with `Retry-After` in whole seconds (at least 1) and a body that
says only to try later. The answer never contains the key or says which attempt failed.

### A blocked client is not answered about keys at all

While a client is over the limit, even a correct key gets a `429`, and the key is not looked up. Checking keys is
what the limit protects, and a blocked client that could still learn whether a key works would defeat it. The price
is that people behind one shared address (an office, the panel) are blocked together. That is why the panel has a
limit of its own, below this one (see below). `/health` is not behind authentication and stays open.

### Who a client is

The address of the connection, or for IPv6 its whole /64 network, because one subscriber usually holds a whole /64
and could otherwise change address at will. IPv4-mapped IPv6 addresses count as the IPv4 address. A request with no
address at all (only in tests) is one shared client, not an unlimited one.

Behind a reverse proxy every connection comes from the proxy. `Security:TrustedProxyNetworks` lists the networks
whose `X-Forwarded-For` is believed (for example `10.0.0.0/8`); the header from anywhere else is ignored, and with
no list it is always ignored. Without that rule anyone could choose a new "address" for every request. Only the
address nearest to the proxy is read, so entries a client added to the header itself are not used.

### Memory

The failures are kept in memory: at most `MaxFailures` timestamps per client, and at most `MaxTrackedClients`
(default 10 000) clients. At the ceiling, clients with no failure left in the window are dropped (at most once a
second, so a flood cannot make the cleanup itself expensive); if the table is still full, an address that is not
tracked yet is not counted. That fails open for a flood from more than 10 000 addresses at once, a choice made so the
limit can never be used to exhaust memory.

### Settings that would switch it off stop the service

`MaxFailures` below 1, a `Window` of zero or less, or a malformed network in `TrustedProxyNetworks` make the API
refuse to start, instead of starting without the protection. The limiter is created at start for the same reason.

### The panel's sign-in has a limit of its own

The panel signs people in through the API, which counts the panel's address like any other. Without care, one
visitor failing 20 times a minute at the panel would make the API refuse the panel for everyone. So the panel counts
failed sign-ins itself, **10 a minute for the whole panel**, below the API's 20, and stops asking the API when it is
reached ("Too many failed attempts. Try again in N seconds."). It counts only keys the API turned down, not an
unreachable or failing API, an empty field or a successful sign-in, and a `429` from the API is shown as the API
refusing for a moment, not as a wrong key.

The panel cannot tell visitors apart safely (the address in a header can be made up unless a trusted proxy sets it),
so it has one count for everybody. The cost is real: someone can use up the allowance and make the sign-in form wait
for up to a minute. Sessions that are already signed in are not affected, because they do not go through the form.
The panel keeps its limiter on `globalThis`, since the server can load a module more than once and each copy would
otherwise count on its own.

### Logging

The log gets one warning when a client reaches the limit (the API, naming the client) or when the panel's limit is
reached. It never contains what was presented.

## How it was checked

- Unit tests of the limiter and of the panel's limiter: counting, the sliding window, the wait, memory ceilings,
  IPv6 networks, the settings, threads.
- API tests with the real pipeline: the first refusal is exactly after the allowed failures; the wait is right; a
  blocked client is refused even with a right key and let in again after the window; other clients are not affected;
  no credentials, successes and a missing admin key are not counted; the admin key shares the count; a trusted proxy's
  forwarded address is used and an untrusted one is ignored.
- Mutation checks: about 60 deliberate breakages of the limiter, the handler, the admin route, the proxy settings
  and the panel's sign-in (comparisons, rounding, a missing check, a setting left unchecked) were each made in turn,
  and the tests were run. All are caught except three that do not change what a client can observe: counting a request
  with no credentials as a failed one instead of as "no result" (both answer 401), the once-a-second limit on cleaning
  up the table (only about cost), and the exact wording of an internal check. The checks also found real gaps, which
  were closed with tests: a ceiling of zero clients, trusting loopback when a proxy list is configured, rounding the
  wait to the nearest second instead of up, and a bad proxy network failing with an unhelpful message.
- Looking at the real thing: with the containers running, 10 wrong keys at the panel's form gave "Too many failed
  attempts. Try again in 59 seconds." on the 11th without the API being asked, wrote one log line for the 13
  attempts, left the signed-in session working, and the API logged no limit at all (the panel stayed below its limit).
- `scripts/smoke.sh` sends wrong keys to the running containers and checks that the 21st is the first refused, that
  `Retry-After` is sensible and that `/health` stays open.

## Limits

- The count is in memory. It starts again with the process, and each running copy of the API counts for itself, so
  with several copies the allowance is per copy.
- There is no lockout per key and no permanent ban: a client waits at most a window. Guessing a key is not
  realistic, so the limit is about load and noise, not about stopping a guess from succeeding.
- Clients behind one address share a count. For the panel this is handled as described above; for anything else
  behind a proxy, configure `TrustedProxyNetworks`.
- A flood from more than `MaxTrackedClients` addresses at once is not counted for the addresses beyond that.
- The panel's single count lets someone delay sign-in for up to a minute. Per-visitor counting needs a trusted proxy
  in front of the panel and is not done.

## Settings

| Setting (environment variable) | Default | Meaning |
|---|---|---|
| `Security__AuthFailureLimit__MaxFailures` | 20 | Failures allowed per client in the window |
| `Security__AuthFailureLimit__Window` | `00:01:00` | How long a failure counts |
| `Security__AuthFailureLimit__MaxTrackedClients` | 10000 | Clients remembered at most |
| `Security__TrustedProxyNetworks__0`, `__1`, … | none | Networks whose `X-Forwarded-For` is believed |
