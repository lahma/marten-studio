# Security policy

## Supported versions

Marten Studio, Postgres Studio and Postgres Studio Diagnostics ship together, at one version, each pinned exactly to the
others. Security fixes are made in the latest release line only.

| Version | Supported |
|---|---|
| 1.x (latest minor) | Yes |
| 0.x | No: upgrade to 1.x ([docs/upgrading.md](docs/upgrading.md)) |

Marten Studio's floor on Marten (9.57.0 since 1.1) follows the latest release. It is well past the first Marten 9
release with the fixes for GHSA-q4xm-rhx9-xjm4 and GHSA-frqq-p5g3-8jq5 (9.39.0), which was the floor before.

## Reporting a vulnerability

Please do not open a public issue. Report it privately through the repository's
[Security tab](https://github.com/lahma/marten-studio/security) ("Report a vulnerability"), with what you found, the
version, and how to reproduce it. You will get an answer within a week, and a fix, an advisory and a release as soon as one
is ready; you are credited in the advisory unless you ask not to be.

## What the studio is, and is not

Both studios are admin UIs that run inside your own application, with its data sources and roles. What they guard, and
how - the three authorization layers, the capabilities (all off by default), the read-only transaction under the SQL
console, the audit, what is masked from whom - is written down in [docs/security.md](docs/security.md). A report that
the studio lets a visitor do what that document says a host's configuration allowed (a capability the host turned on, a
policy that admits everybody) is a configuration question rather than a vulnerability; one where the studio does more
than the document says is a vulnerability, and we want to hear about it.
