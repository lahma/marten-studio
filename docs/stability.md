# Versioning and stability

The studio family is still being shaped: packages are split and renamed, registrations change, and a design that turns
out wrong is corrected in the next release, not carried along. So a release of any size (patch, minor or major) may break
what a host compiles against. This page says what little is promised, and
[docs/upgrading.md](upgrading.md) names every break of a release, with what to write instead.

The packages always ship together, at one version, each depending on exactly its own version of the ones below it, and
each checks at startup that the others are that version. Upgrade them together, and pin the version you tested.

## What holds

These are about safety, not about compatibility, and no release trades them away:

- **Capabilities are off until the host turns them on.** Every capability is off by default, a new one arrives off, and no
  release turns one on for you. `ReadOnly` keeps meaning that nothing writes.
- **Authorization is never widened.** A release never widens who may see or do something without a setting the host
  changes. A fix that narrows it (a gap closed) can arrive in any release.
- **A finding code keeps its meaning.** A code in [docs/health/codes.md](health/codes.md) is a letter and two digits,
  never reused for another check; a retired check keeps its row, marked with the release that retired it. New checks
  arrive in any release, so a new finding can appear and the opt-in ASP.NET Core health check can report `Degraded`
  where it did not before: accept a check you do not want (`AcceptedChecks`).
- **A log event id keeps its meaning.** An id is never reused for another event. Filter on the id, not on the message
  or the category.

## What may change in any release

- **The public API**: package ids, assembly names, namespaces, type and member names, registration calls and options.
  The approved baselines (`PublicApiTest*.verified.txt` in the test projects) make every such change deliberate and
  reviewed, not rare.
- **The snapshot JSON** ([docs/diagnostics.md](diagnostics.md#snapshot-json)) and the diagnostics read model: a change
  that is not an addition moves `schemaVersion`; a reader should ignore keys it does not know.
- **Capability names**, and which operations a capability covers, as long as nothing is turned on that was off.
- **Log categories**, which follow the names of internal types, and the event id ranges a package uses for new events.
- **Page addresses** (`/marten/...`, `/postgres/...`): a page can move. A pasted link is kept working where that is
  cheap.
- **Markup and CSS**: the studio's HTML, its `ms-` classes and its design tokens are not a theming surface. A host must
  not style the studio from its own stylesheet; the studio never styles the host.
- **Wording**: a finding's title, detail and remedy, a page's sentences, a log message's text and how evidence is spelt.
  Match on codes and event ids, never on text.
- **The studio's own SQL** - what "Show SQL" shows - within the rules in [docs/security.md](security.md) (catalog reads
  lock nothing they list; the console's transaction is read-only).
- **Internal types and Razor components**: the pages and their parts are public types only because Razor makes them so.
  A host renders the studio by mapping it, never by placing one of its components.

## What it runs on

| | Supported | Tested |
|---|---|---|
| .NET | `net10.0` | .NET 10; on .NET 11.0 (GA, 11.0.0) both studios run in a Release build of the host and, from 1.2.0, in a Debug build with the 11 SDK (1.1.x does not start the studio's page there: build the host with `-p:EnableHotReloadInRuntimeConfigDevFile=false` until you are on 1.2.0). On .NET 11 the circuit starts only for a visitor the `FallbackPolicy` admits; an `AllowAnonymous()` mapping under a fail-closed policy is the usual case (dotnet/aspnetcore#69624, open): see [Hosting notes](../README.md#hosting-notes) |
| Marten (Marten Studio) | 9.x from 9.57.0 | 9.57.0 on every build |
| Npgsql (Postgres Studio, Diagnostics) | from 9.0.4 | 9.0.4 and 10.0.3 |
| PostgreSQL | 15 to 19; 14 works, untested | every major from 15 to 19, 19 on its release candidate until its general availability |

PostgreSQL 14 reaches its upstream end of life on 2026-11-12. 1.1 took it out of the test matrix (the changelog says so):
every read the studio makes on 14 is still there, and health check S01 still names its end of life, but no release is
tested against it, and a minor release may stop supporting it outright. PostgreSQL 19 is supported in 1.1 on its release
candidate, and the matrix moves to its general-availability image in a later patch.

[docs/postgres-versions.md](postgres-versions.md) says what each version adds.

Any release may raise the Marten and Npgsql floors (1.1 raised them to Marten 9.57.0 and Npgsql 9.0.4): a host that is
on an older one upgrades it before it upgrades the studio.

## Security fixes

In the latest release line only: see [SECURITY.md](../SECURITY.md).