# Security policy

## Supported versions

Data Studio's seven packages ship together, at one version, each pinned exactly to the ones below it: `DataStudio`,
`DataStudio.Postgres.Diagnostics`, `DataStudio.Postgres`, `DataStudio.Marten`, `DataStudio.Kafka.Core`, `DataStudio.Kafka`
and `DataStudio.Kafka.Wolverine`. Security fixes are made in the latest release line only.

| Version | Supported |
|---|---|
| 2.x (latest minor) | Yes |
| 1.x (Marten Studio, Postgres Studio, KStudio) | No: upgrade to 2.x ([docs/upgrading.md](docs/upgrading.md)) |
| 0.x | No |

The Marten module's floor on Marten (9.57.0) follows the latest release. It is well past the first Marten 9 release with
the fixes for GHSA-q4xm-rhx9-xjm4 and GHSA-frqq-p5g3-8jq5 (9.39.0), which was the floor before.

## Reporting a vulnerability

Please do not open a public issue. Report it privately through the repository's
[Security tab](https://github.com/lahma/marten-studio/security) ("Report a vulnerability"), with what you found, the
version, and how to reproduce it. You will get an answer within a week, and a fix, an advisory and a release as soon as one
is ready; you are credited in the advisory unless you ask not to be.

## What Data Studio is, and is not

Data Studio is an admin UI that runs inside your own application, with its data sources, stores, Kafka credentials and
roles. What it guards, and how, is written down in [docs/security.md](docs/security.md), and for the Kafka module in
[docs/kafka.md](docs/kafka.md): each module's three authorization layers, the capabilities (all off by default), the two
read-only switches, the read-only transaction under the SQL console, the audit, and what is masked from whom.

A report that the studio lets a visitor do what those documents say a host's configuration allowed (a capability the host
turned on, a policy that admits everybody) is a configuration question rather than a vulnerability. One where the studio
does more than the documents say is a vulnerability, and we want to hear about it.
