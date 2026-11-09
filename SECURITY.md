# Security policy

## Reporting a vulnerability

Please report it privately, not in a public issue or pull request:

1. Open the **Security** tab of this repository.
2. Choose **Report a vulnerability** (GitHub's private vulnerability reporting).

Say what you found, how to reproduce it (the shortest steps that work), which version or commit you tried, and what
you think an attacker could do with it. A proof of concept is welcome; please do not use it against anyone's data
but your own.

This is a one-person learning and portfolio project. I will read every report and answer as soon as I can, but I
cannot promise a response time, and there is no bounty. If a report leads to a fix, I will credit you in the
changelog unless you would rather I did not.

## Supported versions

Only the latest release gets fixes. Before 1.0 there are no long-term branches.

## What is in scope

- The API, the worker and the admin panel in this repository.
- The Docker images and the Compose file as they are shipped here.

Things I am especially interested in: ways to read or change another tenant's data, to get past the API key check,
to make the service connect to an address it should refuse (the protections of
[ADR 0006](docs/decisions/0006-ssrf-protection.md)), to run script in the panel or steal its session
([ADR 0007](docs/decisions/0007-admin-panel.md)), or to get a secret out of a container or an image.

## What is out of scope

- The demo receiver in `demo/`, which exists only to try the service on one computer.
- Problems that need the settings the project warns against, such as `ALLOW_PRIVATE_NETWORKS=true` on a server that
  can be reached from outside.
- Weaknesses that are already listed as known limitations in the [README](README.md#known-limitations) and the
  [changelog](CHANGELOG.md), unless you found a way to make them worse than described. At the time of writing this
  includes the missing rate limiting.
- Findings that only apply to an old version, or to software this project depends on (report those to the project
  that owns them).

## How the project handles security

Decisions and the checks behind them are written down in [`docs/decisions`](docs/decisions). Pull requests run the
tests, the panel's accessibility and lint rules, a dependency audit and a smoke test of the whole service in
Docker. Dependabot proposes updates to dependencies and base images.
