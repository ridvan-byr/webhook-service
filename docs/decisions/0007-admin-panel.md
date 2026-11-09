# 0007 - Admin panel

Status: accepted. Only the scaffold exists so far (menu, theme, security headers); the screens that
use the API come in the next steps.

## Context

Tenants need to see their deliveries, find the ones that failed, send a failed one again, manage
endpoints and send a test event. The API only knows API keys, which are credentials for machines. The panel
is a Next.js application in `web/`.

## Decisions

### The browser never sees the API key

The panel is a "backend for frontend": the Next.js server calls the API, the browser only talks to the
Next.js server. After sign-in the key is kept in an encrypted, `httpOnly`, `Secure`, `SameSite=Strict`
cookie (AES-256-GCM, secret from the `SESSION_SECRET` environment variable, absolute lifetime of 8 hours).
JavaScript running in the page cannot read the key, so a cross-site scripting bug cannot steal it, and the API
needs no CORS configuration. `API_BASE_URL` and `SESSION_SECRET` are server-only: never `NEXT_PUBLIC_*`.

How it works:

- Signing in sends the pasted key to `GET /me`. Only if the API accepts it is the cookie set, and every key
  the API turns down gets the same message, so the form tells an attacker nothing about keys.
- The cookie holds `v1.<iv>.<ciphertext>.<tag>`. The key for AES-256-GCM is derived from `SESSION_SECRET`
  with HKDF, the expiry sits inside the encrypted payload, and anything that does not decrypt, has been
  changed or has run out is treated as signed out. In production the cookie is called `__Host-session`,
  so a sibling subdomain cannot plant or overwrite it.
- Every page asks the API `GET /me` again (once per request), so a revoked key stops working immediately.
  An API outage shows a message but keeps the session; only a `401` ends it. A layout cannot remove a
  cookie, so an ended session goes through `/session/expired`, a route that clears it and returns to `/login`.
- `src/proxy.ts` only sends visitors without any session cookie to `/login`. It is a shortcut, not the check.
- The API client (`src/lib/api/client.ts`) accepts only paths that continue the API's own address, does not
  follow redirects, never caches, times out after 10 seconds and returns only a kind of failure, never the
  key or what the API wrote.
- Signing out overwrites the cookie with the same attributes. `cookies().delete` is not enough: a browser
  ignores a change to a `__Host-` cookie that is not `Secure`, which a real-browser test showed (the session
  survived a sign-out) and a unit test now covers.
- Every page and every Server Action authenticates itself through `requireApi()`. A layout does not run again
  on each navigation, and a Server Action can be called without its page, so neither can rely on a check made
  elsewhere. An id from the address bar only becomes part of a path to the API after it is checked to be a
  real id, and a status filter is looked up in a fixed list instead of being passed on.
- Everything shown on the deliveries screens (event types, endpoint addresses, errors, what a receiver
  answered) was written by other systems, so it is only ever rendered as text.

### Content-Security-Policy with a nonce, no inline scripts or styles

`src/proxy.ts` (called `middleware.ts` before Next.js 16) creates a 128-bit nonce for every request and sends
a policy that allows scripts and styles only when they carry it, plus `'strict-dynamic'`. There is no
`'unsafe-inline'`, so the application must never render a `style="..."` attribute: use classes.
`unsafe-eval` and `ws:` are allowed in development only. A nonce that is not base64 is refused, because it
is written into a header.

`upgrade-insecure-requests` from the framework's example is left out: it breaks trying the production build
on `http://localhost`, and HSTS plus TLS where the panel is deployed do the same work.

### Nothing is prepared in advance or cached

A new Next.js project turns on `cacheComponents` and `partialPrefetching`. Both are switched off. A nonce has
to be new for every request, which needs pages rendered at request time, and a page shows the data of one
signed-in tenant, which must never be shared. The root layout is `force-dynamic`, and the build output was
checked: every route is marked dynamic. A test guards these settings, because no other test would notice
if one of them were changed back.

### Authorization stays in the API

`proxy.ts` does not decide who may see what. The API checks the key on every call. (The framework's own
documentation also advises against using the proxy for authorization.)

### Looks

System fonts only (nothing is downloaded from another site), light and dark theme that follows the operating
system, one accent colour, status shown as colour plus icon plus text. Every pair of text and background has a
measured WCAG contrast ratio of at least 4.5:1 (3:1 for control borders) in both themes; the values are in
`globals.css`.

### Tests

Vitest and React Testing Library; `axe-core` for what can be found without a rendering engine. jsdom does not
lay anything out, so contrast cannot be tested there: it is checked on the palette and in a real browser.
Linting uses the strict `jsx-a11y` rules. Each security-relevant check was verified by breaking the code on
purpose and confirming that a test fails.

## Node version and pinned packages

The author's machine runs Node 20.18.0, which reached end of life in April 2026. Several current tools assume
that a CommonJS module can `require()` an ES module (Node 20.19, 22.12 and later), which Node 20.18 cannot do.
To keep everything working there, these are pinned (the ESLint, TypeScript and `@types/node` rows are compatibility holds, not Node 20.18 ones).
`.github/dependabot.yml` tells Dependabot to leave them alone; remove an entry there together with its pin:

| Pin | Why | Remove when |
|---|---|---|
| `tailwindcss` and `@tailwindcss/turbopack` 4.3.3 (exact) | 4.3.4 and later need `magic-string` 1.x, which is ESM only | Node is 22.12 or later |
| `jsdom` 25.0.1 and an override of `cssstyle` to 4.1.0 | newer `cssstyle` versions pull an ESM-only chain | Node is 22.12 or later |
| `eslint` stays on 9 | `eslint-plugin-jsx-a11y` and the React and Next.js plugins do not list ESLint 10 in their peer ranges, so `npm ci` stops with `ERESOLVE` | Those plugins support ESLint 10 |
| `vitest` stays on 4 and `@vitejs/plugin-react` on 4 | Vitest 5 needs Node 22.12 or later; plugin-react 6 needs Vite 8, whose Rolldown binary does not load on Node 20.18 | Node is 22.12 or later |
| `typescript` stays below 6.1 | `typescript-eslint` supports TypeScript up to 6.0, so TypeScript 7 fails the lint step | `typescript-eslint` supports the newer version |
| `@types/node` stays on 20 | The types should describe the lowest Node the panel supports; newer ones would allow APIs Node 20 does not have | Node 20 is no longer supported |
| `minimatch` 11.0.5, only for `@typescript-eslint/typescript-estree` | fixes a denial-of-service advisory in the version that tool asks for | `typescript-eslint` asks for a fixed version itself |

The CI job runs on Node 24 and on Node 20.18.0. The second one exists so that a dependency update which would
break the author's machine turns CI red instead of passing unnoticed. When Node is upgraded, drop that entry
and the pins above.

## Limits

- The sign-in page is one more place where keys can be guessed, and neither it nor the API has rate limiting.
  With a 256-bit secret this is impractical, but failed attempts are not throttled.
- Changing `SESSION_SECRET` signs everybody out. There is no rotation with two secrets at once.
- A signed-out session cannot be revoked on the server: the cookie is the session. Signing out forgets it in
  this browser; to cut off a copy of the cookie, revoke the key in the API.
- A stolen session cookie works for up to 8 hours. The key can be revoked in the API at any time.
- The CSP check was verified with a real browser: a normal page load logs no violations and an injected inline
  event handler is blocked. An injected `<script>` created from trusted code is allowed to run by
  `'strict-dynamic'`; that is how the policy is designed to work.
- The Node 24 job could not be run on the author's machine; it is verified only by CI.
