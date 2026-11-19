# AGENTS.md

Guidance for humans and AI agents working in this repository. `CLAUDE.md` imports this file. It holds
what applies in every directory; each area's own `AGENTS.md` holds the rest (*Where the rules live*). A rule or a decision
is written in one file only: never copy one into another.

This repository ships **Data Studio**: one MIT-licensed embeddable Blazor Server admin UI that runs inside the host's own
ASP.NET Core application, as a shell and three modules, and the engines under them as packages of their own (D30). A host adds
`AddDataStudio()` to its container, adds the modules it wants to the builder that call returns, and `MapDataStudio()` to its
endpoint pipeline:

- **The shell** (`DataStudio`) is the chrome: the mount, the layout and rail, the front page, the Activity page, the
  authorization of every endpoint, the JSON toolkit and the assets. It references ASP.NET Core and nothing else.
- **The Marten module** (`DataStudio.Marten`, for [MartenDB](https://martendb.io)) runs against *that application's own*
  configured `IDocumentStore` — browsing document collections and their JSON, inspecting and steering event streams,
  projections and the async daemon, running guarded queries and managing schema. It brings the Postgres module over the
  stores' databases.
- **The Postgres module** (`DataStudio.Postgres`) is for any application that registers an `NpgsqlDataSource`, Marten or
  not, against the application's own data sources: the database browser, Sessions, the health advisor's pages, the SQL
  console and Relationships. The Marten module is built on it.
- **The Kafka module** (`DataStudio.Kafka`) reads the topics and records of the Kafka clusters the application's
  own credentials can read, alone or beside the others, and needs no database.
- **The engines** have no UI: `DataStudio.Postgres.Diagnostics` (no ASP.NET Core) holds the health sweep and its checks, the
  facts they read, `IPostgresDiagnostics` and the health-check export, and a host with no UI to mount (a worker, a job host,
  a shared library) takes it alone and calls `AddPostgresDiagnostics()`; `DataStudio.Kafka.Core` is the Kafka engine, and
  `DataStudio.Kafka.Wolverine` lists a Wolverine application's clusters and topics.

That is seven packages at one version, each pinned exactly to the ones below it. Data Studio is not a standalone
application, a fleet monitor or a connection-string tool. Everything the Marten module knows, it knows because the host
configured Marten that way: aliases, duplicated fields, soft delete, tenancy, projections and metadata columns all come from
`StoreOptions`. That is the whole reason a Marten UI is better than pgAdmin, and it is why the data layer is keyed on
`IDocumentStore`, the Postgres module's on the application's own `NpgsqlDataSource` registrations and the Kafka module's on
the application's own cluster configuration, never on a connection string.

Two constraints govern every design choice here:

- **Registering Data Studio must never change host behaviour.** No global styles, no component-library version pinned onto
  the host, no middleware ordering requirement, no altered authentication, no second projection daemon, nothing that connects
  at registration. A host that adds a package and never maps the shell must be byte-for-byte the application it was.
- **Each package takes exactly what it needs, and the assembly is the fence** (hard rule 16). The shell references ASP.NET
  Core and nothing else; the Postgres module and the engine never reference Marten, JasperFx or Weasel, nor anything Kafka;
  the engine has no ASP.NET Core; the Kafka module references no Postgres module, no engine and no Npgsql. A host with no
  Marten must never get Marten through it, and a host with only Kafka no database stack.

The original product design — information architecture, every screen, the JSON viewer, the authorization model, the
phased delivery plan — is the approved plan at `C:\Users\marko\.claude\plans\i-d-like-you-to-composed-galaxy.md`; the split
into packages and its public API are designed in `marten-studio-0.5-postgresstudio-design.md` and
`marten-studio-0.5-public-api-design.md` beside it, and 2.0's shell, names and addresses in
`.alta/plans/2026-11-16-datastudio-v2.md`. `README.md`, each package's `README.md` and `docs/` are the long
form a user reads. Read the design before designing anything; read *this* file before writing anything.

## Where the rules live

This file holds what applies in every directory. What applies to one area is in that area's own `AGENTS.md`, which an agent working there reads as well (a `CLAUDE.md` beside each imports it). Read the file of every area your change touches before writing in it.

| You are working in | Read its file | It holds |
|---|---|---|
| `src/DataStudio/`, or any component, stylesheet or script | [src/DataStudio/AGENTS.md](src/DataStudio/AGENTS.md) | hard rules 3; D2, D10, D11, D22, D23, D24, D25, D26, D34, D44 |
| `src/DataStudio.Postgres/`, `src/DataStudio.Postgres.Diagnostics/` | [src/DataStudio.Postgres/AGENTS.md](src/DataStudio.Postgres/AGENTS.md) | hard rules 4; D8, D13, D27, D29, D31, D32, D33 |
| `src/DataStudio.Marten/` | [src/DataStudio.Marten/AGENTS.md](src/DataStudio.Marten/AGENTS.md) | hard rules 10, 11, 14, 15; D6, D7, D9, D14, D28 |
| `src/DataStudio.Kafka/`, `src/DataStudio.Kafka.Core/`, `src/DataStudio.Kafka.Wolverine/` | [src/DataStudio.Kafka/AGENTS.md](src/DataStudio.Kafka/AGENTS.md) | hard rules 17; D35, D36, D37, D38, D39, D40, D41, D42, D43, D45, D46, D47, D48 |
| `tests/` | [tests/AGENTS.md](tests/AGENTS.md) | D16, D19; Testing |
| `samples/` | [samples/AGENTS.md](samples/AGENTS.md) | D17, D18 |
| `build/`, `Directory.*.props`, a package reference, `.github/workflows/`, a release | [build/AGENTS.md](build/AGENTS.md) | hard rules 2, 8; D15; the package budget, Build, Release engineering |

Each area file names the topic documents of `docs/maintainers/` to read first. Those hold every rule's and decision's full text, which wins where the two differ; the evidence and history behind a rule are in [decision-notes.md](docs/maintainers/decision-notes.md), and the 1.x to 2.0 names in [v2-public-api.md](docs/maintainers/v2-public-api.md).

## Hard rules

Each rule's full text, with its lists of events, tests and history, is in
[hard-rules.md](docs/maintainers/hard-rules.md) under the same number.

The rules that hold in every directory are whole here. The others are in their area's file, under the same number:

- **2.** Never hand-edit `.github/workflows/*.yml`. [build](build/AGENTS.md)
- **3.** No MudBlazor, and no third-party UI, CSS or icon library, ever. [shell](src/DataStudio/AGENTS.md)
- **4.** No raw SQL outside the three builders' folders, `src/DataStudio.Postgres.Diagnostics/Internal/Sql/`, `src/DataStudio.Postgres/Internal/Sql/` and `src/DataStudio.Marten/Internal/Sql/`. [Postgres](src/DataStudio.Postgres/AGENTS.md)
- **8.** `.gitignore` must never contain a bare `build` rule. [build](build/AGENTS.md)
- **10.** In the Marten module, Marten 9 is async-only, and its optional parts are genuinely optional. [Marten](src/DataStudio.Marten/AGENTS.md)
- **11.** In the Marten module, never call `store.BuildProjectionDaemonAsync()`. [Marten](src/DataStudio.Marten/AGENTS.md)
- **14.** In the Marten module, a read or navigation path never calls `AllSchemaNames()`, `AllObjects()`, `ToDatabaseScript()` or `CreateMigrationAsync()` — and nothing may describe `CreateOrUpdate` as additive. [Marten](src/DataStudio.Marten/AGENTS.md)
- **15.** In the Marten module, open Marten sessions only with `SessionOptions.ForDatabase(resolved.TenantId, resolved.Database)`. [Marten](src/DataStudio.Marten/AGENTS.md)
- **17.** In the Kafka module, a read never writes. [Kafka](src/DataStudio.Kafka/AGENTS.md)

1. **No new NuGet package without a decision recorded in the package budget.** The budget, in [build/AGENTS.md](build/AGENTS.md), is complete. If a package
   looks necessary, add a dated entry to *Package budget changes* (`docs/maintainers/package-budget-changes.md`) saying
   what it is, what it replaces, and why nothing already present can do the job — before referencing it. Full text: hard
   rule 1.

5. **Every mutating operation, and every read authorized like a write, is capability-gated in the service layer, and
   audited.** (The Postgres module has only the latter: `RunSql`, `BrowseDatabase`, `ViewDefinitions` and
   `ViewDatabaseActivity`.) Hiding a button is a UI convenience; the refusal has to live where the operation does.
   `StudioCapabilityGuard.Require(…)` → scope resolution (with `WriteAuthorizationPolicy`) → the operation →
   `audit.Record`, written on failure as well as on success. A value a visitor could shape reaches the log only through
   `StudioLogValue`: escaped, a backslash doubled, a name bounded at 256 characters and other text at 4,000. The one
   exception is 9204's executed statement, which is kept whole up to the circuit's 32 KB receive bound, and by its head
   and tail beyond it. Full text, with the events this holds in: hard rule 5.

6. **No `async void`.** Component events are `Func<Task>`, handlers are `private async Task` with a `try`/`catch` that
   puts the failure on screen. **The lambda form is equally forbidden and the source scanner cannot see it:** an `async`
   lambda converted to an `Action`, or added to an `EventHandler`, is an `async void` with different spelling; every
   event this codebase raises is declared `event Func<Task>?`, except three internal synchronous `event Action?` members
   whose handlers only ask for a render — `StudioState.LinkScopeChanged`, `IStudioModuleRail.LinksChanged` and
   `DatabaseScopeAccess.Changed` — which the compiled-assembly scan guards. `NoAsyncVoidTests` scans `src/` and the
   compiled assemblies for a void-returning `[AsyncStateMachine]` method (`KafkaNoAsyncVoidTests` does the same for the
   Kafka module and its engine). Full text: hard rule 6.

7. **A public API change needs a reviewed Verify baseline and a CHANGELOG entry.** Each package has a baseline of its own.
   The shell's surface is two static classes and two classes (`AddDataStudio`, `MapDataStudio`, `DataStudioBuilder`,
   `DataStudioOptions`); the Postgres module's one static class, two classes and one record (`AddPostgres`,
   `PostgresOptions`, `PostgresCapabilities`, `PostgresResource`); the Marten module's two static classes, two classes and two
   records (`AddMarten`, `AddMartenDiagnostics`, `MartenOptions`, `MartenCapabilities`, `MartenStoreResource`,
   `ProjectionLagThreshold`), which must not move because of the Postgres module; the Kafka module's one static class, two
   classes and one record (`AddKafka`, `KafkaOptions`, `KafkaCapabilities`, `KafkaTopicResource`); and the engine's
   (`DataStudio.Postgres.Diagnostics`) is `AddPostgresDiagnostics`, the health-check extension, `PostgresDiagnosticsOptions`,
   the six inbound types (`IPostgresDatabaseSource`, `IPostgresCapacitySource` and their records), the D31 read model and the
   connection history's four (`AddPostgresConnectionHistory`, its options, `IPostgresConnectionHistoryStore` and
   `PostgresConnectionSample`). `DataStudio.Kafka.Core`'s is its own, and `DataStudio.Kafka.Wolverine`'s is one static class
   and one class (`UseWolverineKafka`, `KafkaWolverineOptions`).
   Anything else that appears in any baseline is a mistake, not a feature.
   Never hand-edit a `.verified.txt`: run the test, read the diff deliberately, say in the pull request what moved and
   why, then accept the baseline. The read model is extend-only (D31). **`docs/health/codes.md` is a contract too, and a
   row is never deleted**: every registered check's code has its row; a retired check keeps its row, marked with the
   release that retired it, and its code is never reassigned; editing or deleting a row is a reviewed baseline change
   (`HealthCodesTests.verified.txt`); a new check adds its row in the same pull request as its `## <code> <title>`
   heading. Full text: hard rule 7.

9. **LF line endings everywhere** (`.gitattributes` enforces it), UTF-8 without BOM. `TreatWarningsAsErrors` is on and
   `CS1591` is an **error in `src/`**, every assembly — the compiler is the lint step. `BL0005` (setting a
   `[Parameter]` from outside its component) is an error. Full text: hard rule 9.

12. **The agent definitions in `.claude/agents/*.md` are the only way work is delegated.** A packet is routed to exactly
    one of them; ad-hoc prompting bypasses the rules those files carry (base-commit verification, the shared-files list,
    the reporting format). Full text: hard rule 12.

13. **A packet routed to Sonnet (`studio-grunt`) must be fully specified** (D21). It may only be given work whose
    expected output the orchestrator can state outright — copy these files with these renames, emit this class from this
    table, run this command and paste the output. If a step needs a decision the packet does not make, the correct
    behaviour is to stop and report the question, never to guess. Full text: hard rule 13.

16. **The fences are the assemblies.** The references run one way: Marten → Postgres → shell, Postgres → engine, Kafka →
    shell and Kafka.Core, Kafka.Wolverine → Kafka.Core. **The shell, `src/DataStudio`, references ASP.NET Core and nothing
    else** (`ShellFenceTests`). **The Postgres module and the engine, `src/DataStudio.Postgres` and
    `src/DataStudio.Postgres.Diagnostics`, reference no Marten, JasperFx or Weasel assembly or package**, directly, through a
    project or transitively: `PostgresFenceTests` holds that line at the project files, the restore graphs, the built
    assemblies' references and the test project's output folder, and `CoreFenceTests` at the text the compiler does not
    bind. **The engine also has no ASP.NET Core** (no `FrameworkReference`, `Microsoft.AspNetCore.*`,
    `Microsoft.JSInterop` or Razor), checked at its project file, restore graph and built references. The Marten module
    reaches the Postgres module only through `InternalsVisibleTo`, and is packed with an **exact** dependency on its own
    Postgres build (`[x.y.z]`, D30), which `Pack` asserts in the nuspec and `AddMarten()` checks at startup. The Postgres
    module, the engine and the Marten module reference no `Confluent.*`, `Wolverine*` or `DataStudio.Kafka*` assembly or
    package, and their source names no Kafka (`PostgresKafkaFenceTests`, `ModuleNeutralityTests`). **The Kafka module
    references the shell and its engine and no Postgres module, no engine of Postgres and no Npgsql**, no Marten, JasperFx,
    Weasel or Wolverine, and its built assembly no `Confluent.*` (the client is `DataStudio.Kafka.Core`'s)
    (`KafkaFenceTests`, `KafkaCoreFenceTests`). `DataStudio.Kafka.Wolverine` references `DataStudio.Kafka.Core` and
    WolverineFx.Kafka only: no shell, no module, no Marten, Weasel or Npgsql (`KafkaWolverineFenceTests`), and
    `DataStudio.Kafka.Tests` does not reference it. A page assembly's root is one segment, unique and never one the shell
    uses (`/postgres`, `/marten`, `/kafka`), and the route table refuses a route two pages claim. Full text: hard rule 16.

## Design decisions (D1–D49)

Every one of these was taken deliberately. Reversing one is allowed; doing it without reading the rationale is not. D1,
D8, D9, D14, D15 and D20 are short enough to stand whole where they are; every other entry is the decision itself, with a pointer
to its full text.

The decisions that hold in every directory are whole here. The others are in their area's file, under the same number:

| Decision | In |
|---|---|
| **D2** Interactive Server render mode only, and `IsTrimmable=false` said out loud | [shell](src/DataStudio/AGENTS.md) |
| **D6** Reads are raw parameterised SQL, writes go through a Marten session | [Marten](src/DataStudio.Marten/AGENTS.md) |
| **D7** Document edit shows a round-trip diff before saving | [Marten](src/DataStudio.Marten/AGENTS.md) |
| **D8** Counts are `pg_class.reltuples` estimates by default, prefixed `~` | [Postgres](src/DataStudio.Postgres/AGENTS.md) |
| **D9** Document and stream ids travel in the query string | [Marten](src/DataStudio.Marten/AGENTS.md) |
| **D10** No SignalR hub | [shell](src/DataStudio/AGENTS.md) |
| **D11** Sub-path mounting is ported from Quartz.Dashboard, and the shell does it for every module | [shell](src/DataStudio/AGENTS.md) |
| **D13** The SQL console is off by default, and its real guard is the transaction | [Postgres](src/DataStudio.Postgres/AGENTS.md) |
| **D14** No live SQL tail | [Marten](src/DataStudio.Marten/AGENTS.md) |
| **D15** Fallout's stable channel (10.5.0), CHANGELOG.md as the version authority, all four workflows generated, trusted publishing for nuget.org | [build](build/AGENTS.md) |
| **D16** xunit.v3 3.2.2 and Verify.XunitV3 32.0.0, eight test projects all matching `*.Tests` | [tests](tests/AGENTS.md) |
| **D17** The demo domain lives in `samples/DataStudio.Sample.Marten.Domain` and does not reference `DataStudio.Marten` | [samples](samples/AGENTS.md) |
| **D18** The sample host starts a throwaway Postgres when no connection string is configured | [samples](samples/AGENTS.md) |
| **D19** Playwright browsers are installed by a Fallout `Browsers` target; locally the browser suite skips with a reason, on CI a missing browser throws | [tests](tests/AGENTS.md) |
| **D22** `blazor.web.js` reaches the host through a `PackageReference`, not through a copy in this package | [shell](src/DataStudio/AGENTS.md) |
| **D23** The studio has no forms, so it disables antiforgery on its own endpoints | [shell](src/DataStudio/AGENTS.md) |
| **D24** The packages ship no Blazor JS initializer; the shell loads `js/studio.js` itself | [shell](src/DataStudio/AGENTS.md) |
| **D25** Every data table sits in its own labelled scroll region, and the shell never scrolls sideways | [shell](src/DataStudio/AGENTS.md) |
| **D26** The stylesheet's UX sections are appended in cascade order, and later wins on purpose | [shell](src/DataStudio/AGENTS.md) |
| **D27** The database browser: one capability, one schema filter, and Marten's tables are never read raw | [Postgres](src/DataStudio.Postgres/AGENTS.md) |
| **D28** LINQ mode is its own capability, parsed by JasperFx's Dynamic LINQ, built without `ToCommand`, and unconditional since the 9.57 floor | [Marten](src/DataStudio.Marten/AGENTS.md) |
| **D29** The health advisor: one shared sweep, three gates, and two deliberate exceptions | [Postgres](src/DataStudio.Postgres/AGENTS.md) |
| **D31** The diagnostics API: what the studio found, for the host's own code, by stable code, and never served by the studio | [Postgres](src/DataStudio.Postgres/AGENTS.md) |
| **D32** The studio draws every plan, plan insights judge it, and EXPLAIN ANALYZE is the console's alone | [Postgres](src/DataStudio.Postgres/AGENTS.md) |
| **D33** The Connection page shows a data source's settings one by one, never its connection string | [Postgres](src/DataStudio.Postgres/AGENTS.md) |
| **D34** A module joins the shell: the Kafka module is one, and so are the database modules | [shell](src/DataStudio/AGENTS.md) |
| **D35** The Kafka module is authorized on a resource of its own, by policies of its own | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D36** `DataStudio.Kafka.Core` is the Kafka engine: Confluent.Kafka alone, behind a wire seam that exists for the fake | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D37** In the Kafka module, a read never writes | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D38** The Kafka module's windows and negatives are truthful | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D39** The Kafka module's search predicates are one hand-written grammar, three-valued, and compared as bytes where they can be | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D40** The Kafka module redacts inside the engine, before any sink | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D41** The Kafka module bounds the cost of a scan in the engine | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D42** The Kafka module correlates by declared fields, in one audited scan | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D43** `DataStudio.Kafka.Wolverine` discovers configuration and nothing else | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D44** Links between modules are pure, internal and grant nothing | [shell](src/DataStudio/AGENTS.md) |
| **D45** The Kafka module exports what the visitor holds, redacted, through the circuit | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D46** The Kafka module's live tail is a scan with no end | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D47** The Kafka module's profiles lower limits per cluster, explicitly, and never grant | [Kafka](src/DataStudio.Kafka/AGENTS.md) |
| **D48** The record-index seam narrows a search and proves nothing | [Kafka](src/DataStudio.Kafka/AGENTS.md) |

**D1 — `net10.0` only.** Marten 9 targets net9.0 and net10.0, .NET 9 is out of support, and the build template this
repository came from is net10-only. A second target framework would double the test matrix and the
conditional-compilation surface to support a runtime nobody should be deploying.

**D3 — Few dependencies a package, and no upper bound on any.** The shell (`DataStudio`) takes `Microsoft.AspNetCore.App.Internal.Assets`
(floor `10.0.12`) and `FrameworkReference Microsoft.AspNetCore.App`, nothing else. The Postgres module takes the shell and
`DataStudio.Postgres.Diagnostics`, each at exactly its own version, plus the framework reference. The Marten module takes two dependencies
(`Marten`, floor `9.57.0`, and the Postgres module at exactly its own version), plus the framework reference; it has no direct `Npgsql`, which
arrives through `Weasel.Postgresql`. The engine (`DataStudio.Postgres.Diagnostics`) takes `Npgsql` (floor `9.0.4`) and four
`Microsoft.Extensions.*` abstractions (floor `10.0.0`), with no framework reference at all. The Kafka module takes the shell and
`DataStudio.Kafka.Core`, each at exactly its own version, plus the framework reference; the Kafka engine takes `Confluent.Kafka` (floor
`2.14.0`) and three `Microsoft.Extensions.*` abstractions, and `DataStudio.Kafka.Wolverine` `DataStudio.Kafka.Core` at exactly its own
version and `WolverineFx.Kafka` (floor `6.61.0`). No `Marten.AspNetCore`. The floors follow the latest releases (*Package budget changes*,
2026-11-03), tested and packed as [build/AGENTS.md](build/AGENTS.md) says. Full text: [build-and-release.md](docs/maintainers/build-and-release.md#d3).

**D4 — Capabilities are a class of booleans, all defaulting to `false`.** A freshly mapped studio is a read-only
browser; `marten.Capabilities = MartenCapabilities.All()` is the one-line, greppable opt-in, and a disabled control
names the exact property a host would have to set (a page reached by its address for a feature that is off says only that it is
not available here: the Kafka module's pages and the SQL console, #74 and #88; the capability chip names the property). `PostgresCapabilities` has four, all reads authorized like
writes, and `KafkaCapabilities` four. Full text: [authorization.md](docs/maintainers/authorization.md#d4).

**D5 — Three authorization layers, plus two process-wide switches.** The shell's `AuthorizationPolicy` decides who gets in, on every
endpoint including the front page; the module's store policy (`MartenOptions.StoreAuthorizationPolicy`) decides which store, database and
tenant, enforced on **every** data call; `WriteAuthorizationPolicy` decides each mutating call. The shell's `ReadOnly` turns every module's
capabilities off, a module's own `ReadOnly` (Marten, Postgres) turns off that module's only, and `Capabilities` sit on top; a startup guard
refuses a mapping that answered none of it. The Postgres module has the same three layers with `SourceAuthorizationPolicy` (D30), the Kafka module its own (D35).
Full text: [authorization.md](docs/maintainers/authorization.md#d5).

**D12 — The data seam is internal, across every assembly.** Per-area service interfaces (`IDocumentDataService`,
`IEventDataService`, …) are internal, with `InternalsVisibleTo` for the test projects and along the references: from the shell to
the three modules, from the engine to the Postgres and Marten modules, from the Postgres module to the Marten module, and from
`DataStudio.Kafka.Core` to the Kafka module and `DataStudio.Kafka.Wolverine` (D30). Each package's *registration* surface is
snapshot-tested: the shell's two static classes and two classes; the Postgres module's one static class, two classes and one record; the Marten
module's two static classes, two classes and two records; the Kafka module's one static class, two classes and one record; the engine's
`AddPostgresDiagnostics`, the health-check extension, `PostgresDiagnosticsOptions`, the six inbound types (`IPostgresDatabaseSource`,
`IPostgresCapacitySource` and their records), the D31 read model and the connection history's four types. Full text:
[architecture.md](docs/maintainers/architecture.md#d12).

**D20 — The public API snapshot excludes `ComponentBase`-derived types.** Otherwise every markup edit is a contract
change, the baseline becomes noise, and the one diff that mattered gets approved along with forty that did not. There is
no `PublicApiRendering` annotator.

**D21 — Agent routing (revised 2026-10-31: Sonnet implements, Opus validates).** A packet goes to `studio-grunt`
(Sonnet, medium) only when it is mechanical *and* fully specified: verbatim copies and ports with listed renames,
boilerplate emitted from a table in the packet, scripted runs and artefact collection. Design-bearing packets go to
`studio-implementer` (Sonnet, high), but the design itself is not delegated: the orchestrator (Opus) writes every
security, SQL, authorization and Marten-API decision into the packet, and the implementer stops on one the packet leaves
open. Final validation is Opus: `studio-reviewer` (xhigh) reads every packet that touches `src/`, scoped to its diff,
and the orchestrator reviews authorization code itself. Agents read AGENTS.md from context (CLAUDE.md imports it), never
with a fresh Read. Then they Read only the topic document(s) the routing table names for their area. Full text:
[build-and-release.md](docs/maintainers/build-and-release.md#d21).

**D30 — One shell, three modules, and an engine you can take alone.** `DataStudio` is the shell every module draws into;
`DataStudio.Postgres` is the database module, Marten-free (hard rule 16), and `DataStudio.Marten` is built on it through
`InternalsVisibleTo` and an exact version pin, not a public extension API, and `AddMarten()` first runs `PostgresVersionGuard`;
`DataStudio.Kafka` (D34) needs no database. The engine, `DataStudio.Postgres.Diagnostics` (health sweep and checks, facts,
sources, `IPostgresDiagnostics`, the health-check export, `PostgresDiagnosticsOptions` and `AddPostgresDiagnostics`), has no
ASP.NET Core in it and the same exact-pin discipline one level down: the Postgres module is packed with
`DataStudio.Postgres.Diagnostics [x.y.z]` and `DataStudio [x.y.z]`, and `AddPostgres()` first runs
`PostgresDiagnosticsVersionGuard`, which `AddMarten()`'s guard repeats for the engine and the shell. The modules register the engine
themselves: one registration per container, `AddPostgresDiagnostics()` refused beside `AddPostgres()` or `AddMarten()`. **One shell per
application**, and `AddDataStudio()` and each `Add…()` are idempotent; `AddMarten()` brings the Postgres module's pages as part of the Marten module and is refused
beside `AddPostgres()`. Settings split in seams: `IShellSettings` (the shell's, which a module's settings answer), `IDiagnosticsSettings`
for the engine and `IStudioSettings` for the Postgres module; a module's one options object answers the ones it is. The public
`AddMarten`'s own body stays free of Postgres types (`PostgresVersionGuardTests` scans its IL). `PostgresResource` has no tenant and
is deliberately unrelated to `MartenStoreResource` and `KafkaTopicResource`: a handler written for one must never be handed another. Each
module's words are its own (`MartenText`, `PostgresText`, `KafkaText`, D49), so a change in one module moves no other's; static assets live only in
`_content/DataStudio/`. Full text: [architecture.md](docs/maintainers/architecture.md#d30).

**D49 — One product, one wording, no 1.x.** The product is Data Studio and a module is Marten, Postgres or Kafka: no sentence,
title, credit, tile, heading or log template says a removed product name (`ProductNamesTests`), there is no `StudioProduct`, each
module's words are its own and are pinned in a `WordsTests` baseline of their own (one per module, and one for the engine alone), and
each log event has one template, with its id, level and property names kept. Modules are shown in the order the host
registered them. Nothing of 1.x is kept for compatibility: no old package id, registration call, option name, address (a 1.x
address is a 404) or log category. Full text: [architecture.md](docs/maintainers/architecture.md#d49).

## Layout

The directory tree is in `docs/maintainers/layout.md`: every directory under `src/`, `samples/`, `tests/`, `build/`,
`.claude/` and `docs/`, and what belongs in each. Read it before adding a file. Add a directory to it in the same pull
request that creates the directory, because `AgentsLayoutTests` fails on a directory the tree misses and on one that no
longer exists. The roots in brief:
- `src/DataStudio/` is the shell; `src/DataStudio.Postgres/` is the Postgres module, with no Marten, and
  `src/DataStudio.Postgres.Diagnostics/` the engine under it; `src/DataStudio.Marten/` holds the Marten screens and checks;
  `src/DataStudio.Kafka/`, `src/DataStudio.Kafka.Core/` and `src/DataStudio.Kafka.Wolverine/` are the Kafka module, its engine and
  its Wolverine adapter. A module has `Components/`, `Services/` and, where it has SQL, `Internal/Sql/` (hard rule 4).
- `samples/` holds the demo hosts and their demo data (D17, D18).
- `tests/` holds the eight test projects ([tests/AGENTS.md](tests/AGENTS.md)).
- `build/` is the Fallout orchestrator.
- `docs/` holds the user documentation. `docs/maintainers/` holds the layout tree, the budget's dated entries and the
  decision notes.

Outside those roots: `.github/workflows/` holds the four **generated** workflow files (hard rule 2),
`.config/dotnet-tools.json` pins the Fallout CLI, `.fallout/` holds the parameter file and the generated build schema,
`data-studio.slnx` is the solution, `data-studio-postgres.slnf` is the solution filter the latest-Npgsql pass builds (the
projects that set `DataStudioPostgresPinsNpgsql`, and the shell they reference, which has no Npgsql to pin), and `CHANGELOG.md` is the version authority.

`docs/maintainers/` also holds the topic documents the area files name.

## Build, test, release

```powershell
.\build.ps1 Test                           # restore, compile, run all eight test projects
.\build.ps1 Pack                           # the seven NuGet packages into artifacts/packages
dotnet test tests\DataStudio.Marten.Tests   # one fast suite alone
```

`CHANGELOG.md` is the version authority, and its first line must be a version header. The orchestrator, the version switches, the package budget and how a release is cut are in [build/AGENTS.md](build/AGENTS.md); the suites, container reuse (`MARTENSTUDIO_PG_REUSE=false` for any run that might overlap another), Docker and Verify baselines are in [tests/AGENTS.md](tests/AGENTS.md).
