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
(The cookie and sign-in are not built yet; this describes the target.)

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
To keep everything working there, three things are pinned:

| Pin | Why | Remove when |
|---|---|---|
| `tailwindcss` and `@tailwindcss/turbopack` 4.3.3 (exact) | 4.3.4 and later need `magic-string` 1.x, which is ESM only | Node is 22.12 or later |
| `jsdom` 25.0.1 and an override of `cssstyle` to 4.1.0 | newer `cssstyle` versions pull an ESM-only chain | Node is 22.12 or later |
| `minimatch` 11.0.5, only for `@typescript-eslint/typescript-estree` | fixes a denial-of-service advisory in the version that tool asks for | `typescript-eslint` asks for a fixed version itself |

The CI job runs on Node 24 and on Node 20.18.0. The second one exists so that a dependency update which would
break the author's machine turns CI red instead of passing unnoticed. When Node is upgraded, drop that entry
and the pins above.

## Limits

- The sign-in page will be one more place where keys can be guessed, and the API has no rate limiting.
  With a 256-bit secret this is impractical, but failed attempts are not throttled.
- A stolen session cookie works for up to 8 hours. The key can be revoked in the API at any time.
- The CSP check was verified with a real browser: a normal page load logs no violations and an injected inline
  event handler is blocked. An injected `<script>` created from trusted code is allowed to run by
  `'strict-dynamic'`; that is how the policy is designed to work.
- The Node 24 job could not be run on the author's machine; it is verified only by CI.
