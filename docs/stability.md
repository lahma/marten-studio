# Versioning and stability

From 1.0, Marten Studio, Postgres Studio and Postgres Studio Diagnostics follow [Semantic Versioning](https://semver.org):
a patch release fixes, a minor release adds, and only a major release breaks what this page lists as the contract. The
three packages always ship together, at one version: `MartenStudio` depends on exactly its own `PostgresStudio`, which
depends on exactly its own `PostgresStudio.Diagnostics`, and each checks at startup that the other is that version.
Upgrade them together.

## The contract

What a minor or patch release will not break:

- **The public API** of each package, as its approved baseline lists it (`PublicApiTest*.verified.txt` in the test
  projects): Marten Studio's registration and options (`AddMartenStudio`, `MapMartenStudio`, `MartenStudioOptions`,
  `MartenStudioCapabilities`, `MartenStoreResource`, the health-check extension), Postgres Studio's five registration
  types, and the diagnostics package's registration, options, inbound interfaces, the diagnostics read model and the
  connection history's types. Records and options gain members; enums gain members (with explicit values); interfaces a
  host implements gain members only with a default implementation. A member to be removed is marked `[Obsolete]` for at
  least one minor release first, and goes only in the next major.
- **The finding codes** in [docs/health/codes.md](health/codes.md): a code is a letter and two digits, never reused,
  renamed or moved to another area, and a variant is frozen within its code. A retired check keeps its row, marked with
  the release that retired it. **New checks arrive in minor releases** - so a new finding can appear, and the opt-in
  ASP.NET Core health check can report `Degraded` where it did not before. Accept a check you do not want
  (`AcceptedChecks`) rather than pinning a version.
- **The snapshot JSON** ([docs/diagnostics.md](diagnostics.md#snapshot-json)): versioned by `schemaVersion`, extend-only
  within a version; a reader should ignore keys it does not know.
- **Capabilities and their defaults**: every capability is off by default, and a new one arrives off. A capability is not
  renamed, and no release turns one on for you. `ReadOnly` keeps meaning that nothing writes.
- **Authorization**: a release never widens who may see or do something without a setting the host changes. A fix that
  narrows it (a gap closed) can arrive in a patch.
- **Log event ids**: the reserved ranges (9200-9299, 9300-9329, 9400-9519) keep their meaning; an id is never reused for
  another event. Filter on the id, not the message.

## What is not a contract

- **Page addresses** (`/marten/...`, `/postgres/...`) are kept working where possible - a pasted link should keep opening
  the same thing - but they are not an API: a page can move, with a redirect where one is cheap.
- **Markup and CSS**: the studio's HTML, its `ms-` classes and its design tokens are not a theming surface. A host must
  not style the studio from its own stylesheet; the studio never styles the host.
- **Wording**: a finding's title, detail and remedy, a page's sentences and a log message's text can be improved in any
  release. Match on codes and event ids, never on text.
- **The studio's own SQL** - what "Show SQL" shows - can change in any release, within the rules in
  [docs/security.md](security.md) (catalog reads lock nothing they list; the console's transaction is read-only).
- **Internal types**, which the packages share through `InternalsVisibleTo`: they change whenever the code needs them to.
- **Razor components**: the pages and their parts are public types only because Razor makes them so. Their parameters
  change whenever a page needs them to; a host renders the studio by mapping it, never by placing one of its components.

## What it runs on

| | Supported | Tested |
|---|---|---|
| .NET | `net10.0` | .NET 10; on .NET 11 (release candidate 3) both studios run in a Release build of the host and, from 1.2.0, in a Debug build with the 11 SDK (1.1.x does not start the studio's page there: build the host with `-p:EnableHotReloadInRuntimeConfigDevFile=false` until you are on 1.2.0). On .NET 11 the circuit starts only for a visitor the `FallbackPolicy` admits; an `AllowAnonymous()` mapping under a fail-closed policy is the usual case (dotnet/aspnetcore#69624, open): see [Hosting notes](../README.md#hosting-notes) |
| Marten (Marten Studio) | 9.x from 9.57.0 | 9.57.0 on every build |
| Npgsql (Postgres Studio, Diagnostics) | from 9.0.4 | 9.0.4 and 10.0.3 |
| PostgreSQL | 15 to 19; 14 works, untested | every major from 15 to 19, 19 on its release candidate until its general availability |

PostgreSQL 14 reaches its upstream end of life on 2026-11-12. 1.1 took it out of the test matrix (the changelog says so):
every read the studio makes on 14 is still there, and health check S01 still names its end of life, but no release is
tested against it, and a minor release may stop supporting it outright. PostgreSQL 19 is supported in 1.1 on its release
candidate, and the matrix moves to its general-availability image in a later patch.

[docs/postgres-versions.md](postgres-versions.md) says what each version adds.

A minor release may raise the Marten and Npgsql floors (1.1 raised them to Marten 9.57.0 and Npgsql 9.0.4): a host that is
on an older one upgrades it before it upgrades the studio.

## Security fixes

In the latest minor release of the latest major only: see [SECURITY.md](../SECURITY.md).
