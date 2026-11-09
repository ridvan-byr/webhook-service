# 0008 - Running the whole service in containers

Status: accepted.

## Context

Running the service by hand takes four processes (PostgreSQL, the API, the worker and the panel) and some
settings. Someone who wants to try it should need a few commands, and what they get should be safe by default,
because people copy the example they are given.

## Decisions

### One Dockerfile for the .NET programs, one for the panel

`Dockerfile` builds the API and the worker from the same restored sources, with one target for each
(`--target api`, `--target worker`). The project files are copied first so the package restore is cached until a
dependency changes. The final images are the ASP.NET and the plain .NET runtime images and run as the `app` user
those images provide. `web/Dockerfile` uses the `standalone` output of Next.js (`next.config.ts`), so the image
carries only the packages the server uses, and runs as `node`. Nothing secret is needed to build:
`API_BASE_URL` and `SESSION_SECRET` are read when the server runs, so no secret ends up in an image layer.

### Safe defaults, and the unsafe choices are explicit

- The two secrets have no default and `docker-compose.yml` refuses to start without them (`${VAR:?...}`), with a
  message that says where to set them. A secret with a built-in default would be known to everyone who reads the
  repository.
- The API and the panel are published on `127.0.0.1` only. PostgreSQL is not published at all; only the other
  containers reach it.
- The containers drop every Linux capability and cannot gain new ones. PostgreSQL is the exception: its image
  starts as root and switches to its own user, which needs a few capabilities (found by trying: with all of them
  dropped it never became healthy).
- The delivery protections stay on: HTTPS only, and no addresses on a private network (ADR 0006). Trying the
  service against the demo receiver, which is on a private network and speaks plain http, needs
  `ALLOW_PRIVATE_NETWORKS=true` and `REQUIRE_HTTPS=false`, and `.env.example` says in so many words not to do that
  on a server that can be reached from outside.
- The panel's session cookie is `Secure` and `__Host-` prefixed in production (ADR 0007). Browsers accept that
  on `http://localhost`, but not on another host name over plain http: for anything but a local try-out, put the
  panel behind HTTPS.

### A demo receiver, outside the service

`demo/receiver.js` answers 200 on `/ok`, 500 on `/fail` and slowly on `/slow`, and prints what it received with the
signature headers. It runs only with `--profile demo`, from a mounted file, so it has no image of its own and is
not part of what is shipped.

### Two compose files

`docker-compose.yml` is the whole service. `docker-compose.dev.yml` is only the database, for working on the code
with `dotnet run` and `npm run dev`. They are separate because the whole-service file requires the secrets, and
Compose checks every service in a file even when only one is started, so the one-service workflow would otherwise
need secrets it does not use.

### A smoke test that is also the CI check

`scripts/smoke.sh` builds the images, starts everything with made-up secrets and its own project name, and checks:
the API answers and refuses a caller without a key, a tenant can be created, an event reaches the receiver with a
`v1` signature and the delivery succeeds, the panel is up, redirects a visitor without a session and sends a
Content-Security-Policy with a nonce, PostgreSQL is not published, and nothing runs as root. It removes everything
afterwards. CI runs the same script, so what is checked on a laptop is what is checked in the pull request.

## Limits

- The images are built from the Microsoft and Docker Hub base images by tag, not by digest. Dependabot proposes
  base image updates (major versions of .NET, Node and PostgreSQL are excluded: they are decisions).
- The API and the worker have no container health checks: the base images have no `curl`, and adding one only for
  that was not worth the extra layer. Compose waits for PostgreSQL to be healthy, and the worker waits for the API
  to have started (not to be ready). A worker that started too early was not observed in testing; if it ever stops
  for that reason, `restart: unless-stopped` brings it back.
- On the first start, the API's log shows a failed `SELECT ... FROM "__EFMigrationsHistory"`. That is Entity Framework
  looking for its history table before it creates it; it is not an error.
- `docker compose down` also needs the two settings, because every service in the file is checked. Keep them in
  `.env`.
- There is no HTTPS in front of the panel. The README says what to do for a deployment that is not local.
