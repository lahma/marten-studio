# AGENTS.md

Guidance for humans and AI agents working in this repository. `CLAUDE.md` imports this file, so keep it the single
source of truth.

This repository ships two MIT-licensed Razor Class Libraries, embeddable Blazor Server admin UIs that run inside the
host's own ASP.NET Core application, and the diagnostics engine under both as a package of its own (D30):

- **Marten Studio** (`MartenStudio`), for [MartenDB](https://martendb.io). A host adds `AddMartenStudio()` to its
  container and `MapMartenStudio()` to its endpoint pipeline, and the studio runs against *that application's own*
  configured `IDocumentStore` — browsing document collections and their JSON, inspecting and steering event streams,
  projections and the async daemon, running guarded queries and managing schema.
- **Postgres Studio** (`PostgresStudio`), for any application that registers an `NpgsqlDataSource`, Marten or not:
  `AddPostgresStudio()` and `MapPostgresStudio()`, against the application's own data sources. It is also the core
  Marten Studio is built on: the shell, the scope and authorization layers, the audit, the database browser, Sessions,
  the health advisor's pages, the SQL console and Relationships are PostgresStudio's, and MartenStudio adds the Marten
  screens, the Marten checks and the Marten halves of every seam.
- **Postgres Studio Diagnostics** (`PostgresStudio.Diagnostics`, since 0.11), the engine both studios are built on, with
  no UI and no ASP.NET Core: the health sweep and its checks, the facts they read, `IPostgresDiagnostics` and the
  health-check export. A host with no UI to mount (a worker, a job host, a shared library) takes it alone and calls
  `AddPostgresDiagnostics()`; each studio brings it at exactly its own version.

Neither is a standalone application, a fleet monitor or a connection-string tool. Everything Marten Studio knows, it
knows because the host configured Marten that way: aliases, duplicated fields, soft delete, tenancy, projections and
metadata columns all come from `StoreOptions`. That is the whole reason a Marten UI is better than pgAdmin, and it is
why the data layer is keyed on `IDocumentStore`, and Postgres Studio's on the application's own `NpgsqlDataSource`
registrations, and never on a connection string.

Two constraints govern every design choice here:

- **Registering either studio must never change host behaviour.** No global styles, no component-library version pinned
  onto the host, no middleware ordering requirement, no altered authentication, no second projection daemon. A host that
  adds a package and never maps it must be byte-for-byte the application it was.
- **PostgresStudio never references Marten, JasperFx or Weasel, and nothing it ships names them except as text** (hard
  rule 16). A host with no Marten must never get Marten through it.

The original product design — information architecture, every screen, the JSON viewer, the authorization model, the
phased delivery plan — is the approved plan at `C:\Users\marko\.claude\plans\i-d-like-you-to-composed-galaxy.md`; 0.5's
split and its public API are designed in `marten-studio-0.5-postgresstudio-design.md` and
`marten-studio-0.5-public-api-design.md` beside it. `README.md`, `src/PostgresStudio/README.md` and `docs/` are the long
form a user reads. Read the design before designing anything; read *this* file before writing anything.

## Where to read first

AGENTS.md states each rule; the topic document holds its full text, which wins where they differ. Read the topic
document for your area before writing code in it. All documents are in `docs/maintainers/`.

| You are working on | Read first |
|---|---|
| SQL builders, catalog reads, the SQL console, the database browser | [sql-and-catalog.md](docs/maintainers/sql-and-catalog.md) (D13, D27), [plan-analyzer.md](docs/maintainers/plan-analyzer.md) (Explain, Query › Plan) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 4, 5 and 14 |
| Marten documents, events, the daemon, projections, sessions, schema, LINQ mode | [marten.md](docs/maintainers/marten.md) (D6, D7, D28), [sql-and-catalog.md](docs/maintainers/sql-and-catalog.md#d27) ("Reach, not scope": rebuild, rewind, skip, high water, apply), [architecture.md](docs/maintainers/architecture.md#d10) (lag samples, dead-letter groups), [testing.md](docs/maintainers/testing.md) (the Marten API surface test, the sibling source checkouts) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 10, 11, 14 and 15 |
| Health checks, the sweep, the codes table | [health-engine.md](docs/maintainers/health-engine.md) (D29), [architecture.md](docs/maintainers/architecture.md#d31) (stable codes and variants), [sql-and-catalog.md](docs/maintainers/sql-and-catalog.md#d27) (what a list may lock or deparse), [testing.md](docs/maintainers/testing.md) (VACUUM, statistics resets, the version matrix) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 4 and 7 |
| The diagnostics API | [architecture.md](docs/maintainers/architecture.md#d31) (D31), [health-engine.md](docs/maintainers/health-engine.md) (the gates it skips, the content rules it keeps) and [hard-rules.md](docs/maintainers/hard-rules.md) rule 7 |
| Authorization, capabilities, the audit log | [authorization.md](docs/maintainers/authorization.md) (D4, D5), [sql-and-catalog.md](docs/maintainers/sql-and-catalog.md#d27) (the browser's gate, "Reach, not scope"), [health-engine.md](docs/maintainers/health-engine.md) (D29's gates), [architecture.md](docs/maintainers/architecture.md#d30) (`PostgresStudioResource`) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 5, 14 and 15 |
| UI, CSS, JS, Blazor components and circuits | [ui.md](docs/maintainers/ui.md) (D22-D26), [architecture.md](docs/maintainers/architecture.md) (D10, D11, D30's *Words* and assets) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 3, 6 and 15 (interop loss); D9 and D14 are whole above |
| The plan analyzer, Explain, plan insights | [plan-analyzer.md](docs/maintainers/plan-analyzer.md) (D32), [sql-and-catalog.md](docs/maintainers/sql-and-catalog.md) (D13, D27's masking) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 5 and 7 |
| The Connection page, the connection-settings checks (C16-C18, S11-S13), paste-to-compare | [architecture.md](docs/maintainers/architecture.md#d33) (D33), [health-engine.md](docs/maintainers/health-engine.md) (the connection-settings checks and the page's gates) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 5 and 16 |
| The PostgresStudio / MartenStudio split, routes, assets, the data seam, samples | [architecture.md](docs/maintainers/architecture.md) (D1, D2, D11, D12, D17, D18, D20, D30) and [hard-rules.md](docs/maintainers/hard-rules.md) rule 16 (D1, D15 and D20 are whole above) |
| Build, packages, versions, CI, release, agent routing | [build-and-release.md](docs/maintainers/build-and-release.md) (D3, D15, D21) and [hard-rules.md](docs/maintainers/hard-rules.md) rules 1, 2, 8, 9 and 16 (D1, D15 and D20 are whole above) |
| Tests | [testing.md](docs/maintainers/testing.md) (D16, D19) |
| KStudio: the engine (KStudio.Core), Kafka reads, scans, predicates, decoders, redaction | [kstudio.md](docs/maintainers/kstudio.md) (D36-D41) and [testing.md](docs/maintainers/testing.md) (the KStudio suites) |
| The evidence and history behind a rule | [decision-notes.md](docs/maintainers/decision-notes.md); the dated budget entries are in [package-budget-changes.md](docs/maintainers/package-budget-changes.md) |

## Hard rules

Each rule's full text, with its lists of events, tests and history, is in
[hard-rules.md](docs/maintainers/hard-rules.md) under the same number.

1. **No new NuGet package without a decision recorded in this file.** The package budget below is complete. If a package
   looks necessary, add a dated entry to *Package budget changes* (`docs/maintainers/package-budget-changes.md`) saying
   what it is, what it replaces, and why nothing already present can do the job — before referencing it. Full text: hard
   rule 1.

2. **Never hand-edit `.github/workflows/*.yml`.** All four — `build.yml`, `marten-latest.yml`, `publish.yml`,
   `release.yml` — are generated from the `[GitHubActions]` attributes in `build/Build.CI.GitHubActions.cs`, and any
   build run overwrites them. Change the attribute and re-run the build (`dotnet fallout --generate-configuration
   GitHubActions_publish --host GitHubActions` regenerates just one); a run whose generated output differs *aborts*, so
   run it again and commit the regenerated YAML. Full text: hard rule 2.

3. **No MudBlazor, and no third-party UI, CSS or icon library, ever.** Styling is hand-rolled, prefixed `ms-`, and
   driven by the design tokens in `src/PostgresStudio/wwwroot/css/studio.css`. There is still exactly one stylesheet,
   and it holds both studios' sections (D26). Full text: hard rule 3.

4. **No raw SQL outside the three builders' folders, `src/PostgresStudio.Diagnostics/Internal/Sql/`,
   `src/PostgresStudio/Internal/Sql/` and `src/MartenStudio/Internal/Sql/`.** Identifiers are quoted through the builder,
   values are always parameters. The engine's builders, `SqlIdentifier` and `SqlLexer` among them, live in the first; the
   shared studio's own (the console's plans, the browser's rows) in the second; the statements only a Marten store has
   (the document, event, daemon and projection reads) in the third, which calls the first's quoting. The compiled fence in
   `HealthQueryConventionTests` exempts each assembly's builders' namespace by name, and each assembly **only in its own
   folder** (the first two share a namespace). Full text: hard rule 4.

5. **Every mutating operation, and every read authorized like a write, is capability-gated in the service layer, and
   audited.** (PostgresStudio has only the latter: `RunSql`, `BrowseDatabase`, `ViewDefinitions` and
   `ViewDatabaseActivity`.) Hiding a button is a UI convenience; the refusal has to live where the operation does.
   `StudioCapabilityGuard.Require(…)` → scope resolution (with `WriteAuthorizationPolicy`) → the operation →
   `audit.Record`, written on failure as well as on success. A value a visitor could shape reaches the log only through
   `StudioLogValue`: escaped, a backslash doubled, a name bounded at 256 characters and other text at 4,000. The one
   exception is 9204's executed statement, which is kept whole up to the circuit's 32 KB receive bound, and by its head
   and tail beyond it. Full text, with the events this holds in: hard rule 5.

6. **No `async void`.** Component events are `Func<Task>`, handlers are `private async Task` with a `try`/`catch` that
   puts the failure on screen. **The lambda form is equally forbidden and the source scanner cannot see it:** an `async`
   lambda converted to an `Action`, or added to an `EventHandler`, is an `async void` with different spelling; every
   event this codebase raises is declared `event Func<Task>?`, except `StudioState.LinkScopeChanged` (`Action`,
   synchronous by contract, 1.2), which the compiled-assembly scan guards. `NoAsyncVoidTests` scans `src/` and, since
   1.2, the compiled assemblies for a void-returning `[AsyncStateMachine]` method. Full text: hard rule 6.

7. **A public API change needs a reviewed Verify baseline and a CHANGELOG entry.** MartenStudio's surface is three
   static classes, two classes and two records, and it must not move because of PostgresStudio; PostgresStudio's is its
   five registration types, and PostgresStudio.Diagnostics' (a baseline of its own) is `AddPostgresDiagnostics`, the
   health-check extension, `PostgresDiagnosticsOptions`, the six inbound types (`IPostgresDatabaseSource`,
   `IPostgresCapacitySource` and their records), the D31 read model and, since 1.0, the connection history's four
   (`AddPostgresConnectionHistory`, its options, `IPostgresConnectionHistoryStore` and `PostgresConnectionSample`).
   Anything else that appears in any baseline is a mistake, not a feature.
   Never hand-edit a `.verified.txt`: run the test, read the diff deliberately, say in the pull request what moved and
   why, then accept the baseline. The read model is extend-only (D31). **`docs/health/codes.md` is a contract too, and a
   row is never deleted**: every registered check's code has its row; a retired check keeps its row, marked with the
   release that retired it, and its code is never reassigned; editing or deleting a row is a reviewed baseline change
   (`HealthCodesTests.verified.txt`); a new check adds its row in the same pull request as its `## <code> <title>`
   heading. Full text: hard rule 7.

8. **`.gitignore` must never contain a bare `build` rule.** The Fallout orchestrator lives in `build/`, and a stock
   Visual Studio .gitignore silently untracks the whole thing. Full text: hard rule 8.

9. **LF line endings everywhere** (`.gitattributes` enforces it), UTF-8 without BOM. `TreatWarningsAsErrors` is on and
   `CS1591` is an **error in `src/`**, both assemblies — the compiler is the lint step. `BL0005` (setting a
   `[Parameter]` from outside its component) is an error. Full text: hard rule 9.

10. **In MartenStudio, Marten 9 is async-only, and its optional parts are genuinely optional.** There are no synchronous
    LINQ terminals — they throw. Serialize and deserialize through `store.Options.Serializer()`, never your own
    `JsonSerializer`. Never touch `StoreOptions.Storage.AllDocumentMappings` (internal). Every metadata column is
    optional: build the column set with `DocumentTableInfo.FromDocumentType(...).WithPhysicalColumns(...)`, never
    `select *`, and enumerate `MartenDuplicatedFields.Physical`, because `IDocumentType.DuplicatedFields` is not a list
    of columns. Full text: hard rule 10.

11. **In MartenStudio, never call `store.BuildProjectionDaemonAsync()`.** It starts a *second* daemon alongside the
    host's own, and the two fight over the same advisory locks until one hangs. The only supported way to reach a
    running daemon is the DI-registered coordinator, and its absence is a value — `DaemonHosting.NotHostedInThisProcess`
    — not an error. Full text: hard rule 11.

12. **The agent definitions in `.claude/agents/*.md` are the only way work is delegated.** A packet is routed to exactly
    one of them; ad-hoc prompting bypasses the rules those files carry (base-commit verification, the shared-files list,
    the reporting format). Full text: hard rule 12.

13. **A packet routed to Sonnet (`studio-grunt`) must be fully specified** (D21). It may only be given work whose
    expected output the orchestrator can state outright — copy these files with these renames, emit this class from this
    table, run this command and paste the output. If a step needs a decision the packet does not make, the correct
    behaviour is to stop and report the question, never to guess. Full text: hard rule 13.

14. **In MartenStudio, a read or navigation path never calls `AllSchemaNames()`, `AllObjects()`, `ToDatabaseScript()` or
    `CreateMigrationAsync()` — and nothing may describe `CreateOrUpdate` as additive.** (PostgresStudio's read paths
    write nothing, as `FirstNavigationLiveTests` proves for every core page.) Below Marten 9.50.3 the first two *apply
    migrations* when a document type has a numeric or HiLo id; the rule stands at every version. **Nor does a read path
    run a Marten LINQ terminal**, `ToCommand`, `ExplainAsync`, `ToPreviewCommand`, `LoadAsync`, `LoadManyAsync` or a
    `session.Events` read, nor (from 9.52) `FetchProjectionFloorAsync`, its setters or the high-water advance: each
    ensures storage, which creates or migrates it. Reads use the studio's own SQL (`StorageEnsuringCallTests`; LINQ
    mode's composition builds its statement without ensuring storage, and from 1.1 that covers `QueryRawEvents()`, never
    passed a tenant), and schema names, declared indexes and what Marten owns come from `SchemaDeclarationReader`,
    without a connection. Enumerating a dynamic tenancy's databases is not read-only either
    (`TenancyEnumerationCallTests`). Time travel and
    the stepper fold only through `IAggregateFolder.FoldAsync`. **`AutoCreate.CreateOrUpdate` is not additive**; no
    screen, comment or doc may say an apply leaves a hand-made index alone. Apply and its preview run under
    `CreateOrUpdate`, never `AutoCreate.All`; an apply is authorized as `(db, null, ApplySchemaChanges)` whatever tenant
    is selected, and its typed confirmation travels to the service, never re-supplied by the page. Everything
    schema-related is per database, never per store. **The database browser reads the catalog, never Weasel**:
    `pg_catalog` (never `information_schema`), filtered by `nspname = any(@schemas)`, inside `ReadOnlySqlSession`. Full
    text: hard rule 14.

15. **In MartenStudio, open Marten sessions only with `SessionOptions.ForDatabase(resolved.TenantId,
    resolved.Database)`** (or `ForDatabase(resolved.Database)` where the scope has no tenant), never
    `Store.QuerySession(tenantId)` / `LightweightSession(tenantId)`. **Never run a user-supplied clause through
    `Query<T>(string)` / `QueryAsync(Type, string)`**: Marten's `UserSuppliedQueryHandler` adds no tenant or soft-delete
    predicate. A document type Marten registers `SingleTenanted()` whatever the store (`DeadLetterEvent` is one) has no
    `tenant_id` column and no Marten predicate: filter on its own `TenantId` property, and prove a sequence is in scope
    before handing it to an API with no tenant predicate. **In both studios, catch interop loss with `catch (Exception
    e) when (StudioLiveUpdates.IsInteropUnavailable(e))`**, never a ladder of `catch` clauses. Full text: hard rule 15.

16. **The core is Marten-free, and the fence is the assembly.** `src/PostgresStudio` and `src/PostgresStudio.Diagnostics`
    reference no Marten, JasperFx or Weasel assembly or package, directly, through a project or transitively:
    `PostgresStudioFenceTests` holds that line at the project files, the restore graphs, the built assemblies' references
    and the test project's output folder, and `CoreFenceTests` at the text the compiler does not bind. **The diagnostics
    assembly also has no ASP.NET Core** (no `FrameworkReference`, `Microsoft.AspNetCore.*`, `Microsoft.JSInterop` or Razor),
    checked at its project file, restore graph and built references. MartenStudio reaches PostgresStudio only through
    `InternalsVisibleTo`, and is packed with an **exact** dependency on its own PostgresStudio build (`[x.y.z]`, D30),
    which `Pack` asserts in the nuspec and `AddMartenStudio()` checks at startup. A page route belongs to one assembly
    of a studio, below that assembly's own compile-time root (`/postgres`, `/marten`), and the route table refuses a
    route two pages claim. Full text: hard rule 16.

17. **In KStudio, a read never writes.** No client the engine builds may create a topic, commit or store an offset, join
    or subscribe a group, or run a host's builder delegate; every assign carries explicit offsets, and the wire seam has
    no produce, commit, delete or alter method. D37 has the overrides; `ConfluentOverridesTests`,
    `ReadNeverWritesMetadataLiveTests` and `ReadNeverWritesScanLiveTests` hold them. Full text: hard rule 17.

## Design decisions (D1–D41)

Every one of these was taken deliberately. Reversing one is allowed; doing it without reading the rationale is not. D1,
D8, D9, D14, D15 and D20 are short enough to stand whole here; every other entry is the decision itself, with a pointer
to its full text.

**D1 — `net10.0` only.** Marten 9 targets net9.0 and net10.0, .NET 9 is out of support, and the build template this
repository came from is net10-only. A second target framework would double the test matrix and the
conditional-compilation surface to support a runtime nobody should be deploying.

**D2 — Interactive Server render mode only, and `IsTrimmable=false` said out loud.** Blazor Server is what lets the
studio run against the host's in-process `IDocumentStore`; WebAssembly would need an API surface that is itself the
security problem. `IsTrimmable=false` is stated in the csproj, because components are bound by name and the document
viewer deserializes into types found at run time. Full text: [architecture.md](docs/maintainers/architecture.md#d2).

**D3 — Few dependencies a package, and no upper bound on any: MartenStudio (`Marten`, floor `9.57.0`, and PostgresStudio
at exactly its own version), PostgresStudio (PostgresStudio.Diagnostics at exactly its own version, and
`Microsoft.AspNetCore.App.Internal.Assets`, floor `10.0.12`) - each plus `FrameworkReference
Microsoft.AspNetCore.App` - and PostgresStudio.Diagnostics (`Npgsql`, floor `9.0.4`, and four `Microsoft.Extensions.*`
abstractions, floor `10.0.0`), with no framework reference at all.** No `Marten.AspNetCore`; MartenStudio has no direct
`Npgsql`, which arrives through `Weasel.Postgresql`, while PostgresStudio.Diagnostics references `Npgsql` directly and
PostgresStudio takes it through that. The floors follow the latest releases (*Package budget changes*, 2026-11-03),
tested and packed as *Build* says. Full text: [build-and-release.md](docs/maintainers/build-and-release.md#d3).

**D4 — Capabilities are a class of booleans, all defaulting to `false`.** A freshly mapped studio is a read-only
browser; `o.Capabilities = MartenStudioCapabilities.All()` is the one-line, greppable opt-in, and every disabled control
names the exact property a host would have to set. `PostgresStudioCapabilities` has four, all reads authorized like
writes. Full text: [authorization.md](docs/maintainers/authorization.md#d4).

**D5 — Three authorization layers, plus two process-wide switches.** `AuthorizationPolicy` decides who gets in;
`StoreAuthorizationPolicy` decides which store, database and tenant, enforced on **every** data call;
`WriteAuthorizationPolicy` decides each mutating call. `ReadOnly` and `Capabilities` sit on top, and a startup guard
refuses a mapping that answered none of it. Postgres Studio has the same three layers with `SourceAuthorizationPolicy`
(D30). Full text: [authorization.md](docs/maintainers/authorization.md#d5).

**D6 — Reads are raw parameterised SQL, writes go through a Marten session.** Reads are built from `IDocumentType`
metadata against `IMartenDatabase.CreateConnection(ConnectionUsage.Read)`. Writes go through a Marten session
(`StoreObjects` / `DeleteObjects`); the studio never hand-writes document DML. Full text:
[marten.md](docs/maintainers/marten.md#d6).

**D7 — Document edit shows a round-trip diff before saving.** Saving an edited document means deserializing into the CLR
type and serializing it back, so any property the type does not have is gone: the dialog lists dropped, changed and
added properties before the save. An edit keeps its document's id; preview and save refuse a JSON id that differs from
the row's. Full text: [marten.md](docs/maintainers/marten.md#d7).

**D8 — Counts are `pg_class.reltuples` estimates by default, prefixed `~`.** An exact `count(*)` on a 10-million-row
collection is a sequential scan, and a dashboard that does one per collection per refresh is a denial of service against
the database it is supposed to help you understand. Exact counts are available on demand, under `statement_timeout`.

**D9 — Document and stream ids travel in the query string.** The hand-rolled router has no catch-all segment, and Marten
ids are frequently strings that contain `/`. Scope (`?store=&db=&tenant=`) lives in the URL for the same reason a
document id does: a link someone pastes into a chat has to reopen the same thing.

**D10 — No SignalR hub in v1.** Pages poll with a `PeriodicTimer` at `RefreshInterval`, paused on hidden tabs, through a
single-flight snapshot cache so that N circuits cost one query per interval; there is no SignalR hub. Where the daemon
is hosted in this process, a lazy observer on `ShardStateTracker` gives sub-second projection state. Full text:
[architecture.md](docs/maintainers/architecture.md#d10).

**D11 — Sub-path mounting is ported from Quartz.Dashboard.** Compile-time routes are re-rooted at map time, from each
assembly's own root (`/postgres`, `/marten`), and `/_blazor`, `blazor.web.js`, `opaque-redirect` and
`_content/PostgresStudio` are mirrored under the mount path (`StudioEndpointMapper`). Do not rewrite it; there is no
"integrate into the host's own Blazor app" mode in v1. Full text:
[architecture.md](docs/maintainers/architecture.md#d11).

**D12 — The data seam is internal, across both assemblies.** Per-area service interfaces (`IDocumentDataService`,
`IEventDataService`, …) are internal, with `InternalsVisibleTo` for the test projects and, from PostgresStudio, for
MartenStudio (D30), and from the engine for PostgresStudio and MartenStudio. Each package's *registration* surface is
snapshot-tested: MartenStudio's three static classes, two classes and two records; PostgresStudio's five registration
types; PostgresStudio.Diagnostics' `AddPostgresDiagnostics`, the health-check extension, `PostgresDiagnosticsOptions`,
the six inbound types (`IPostgresDatabaseSource`, `IPostgresCapacitySource` and their records), the D31 read model and
the connection history's four types (1.0). Full text: [architecture.md](docs/maintainers/architecture.md#d12).

**D13 — The SQL console is off by default, and its real guard is the transaction.** `RunSql` is a capability like any
other. What makes it safe is `SET TRANSACTION READ ONLY` plus `statement_timeout` and an optional `SET LOCAL ROLE`, not
the statement parser. Grant it to people you would give `psql` to. Since 1.2 the console describes a statement (Parse and
Describe, never Bind or Execute) inside the same read-only transaction before running it, so that columns Npgsql has no
handler for - pgvector's three types among them - are requested as text; this changes neither the guard nor the audit,
and it does not happen on a connection that auto-prepares (`Max Auto Prepare`), where the console runs as before. Full
text: [sql-and-catalog.md](docs/maintainers/sql-and-catalog.md#d13).

**D14 — No live SQL tail in v1.** It would require replacing the host's `StoreOptions.Logger`, which is exactly the
"registration changes host behaviour" failure this project refuses, and Marten 9 defaults `DisableNpgsqlLogging=true`
anyway. Every studio-generated grid carries a "Show SQL" disclosure instead, which answers the question people actually
have.

**D15 — Fallout 10.4.0 stable, CHANGELOG.md as the version authority, all four workflows generated, trusted publishing
for nuget.org.** See *Build* and *Release engineering* below. The important half is that the version lives in one file
that a human edits and no tool rewrites, and that the only credential anywhere in the release path lives for minutes.

**D16 — xunit.v3 3.2.2 and Verify.XunitV3 32.0.0, six test projects all matching `*.Tests`.**
`tests/MartenStudio.Tests`, `tests/PostgresStudio.Tests` and `tests/KStudio.Tests` are fast and need no Docker; the three
`*.Integration.Tests` need Testcontainers, and the first two Playwright. All six match the glob in `ITest.TestProjects`. The pins are load-bearing — see
*Package budget*. Full text: [testing.md](docs/maintainers/testing.md#d16).

**D17 — The demo domain lives in `samples/MartenStudio.SampleDomain` and does not reference `MartenStudio`.** The
missing reference is deliberate and structural: the studio must work against a store that knows nothing about it. The
demo data with no Marten in it is `samples/PostgresStudio.SampleData`, which references no studio either. Full text:
[architecture.md](docs/maintainers/architecture.md#d17).

**D18 — The sample host starts a throwaway Postgres when no connection string is configured.** Zero-config `dotnet run`
starts a throwaway Postgres with a loud banner and throws with guidance when Docker is absent; it must never fall back
to `localhost` silently and appear to work against somebody's real database. The Marten sample judges a reused database
before Marten touches it (`SampleDatabase`). Full text: [architecture.md](docs/maintainers/architecture.md#d18).

**D19 — Playwright browsers are installed by a Fallout `Browsers` target; locally the browser suite skips with a reason,
on CI a missing browser throws.** CI must never report a green run whose browser tests quietly did not happen; the skip
message names the exact command that would install them. Full text: [testing.md](docs/maintainers/testing.md#d19).

**D20 — The public API snapshot excludes `ComponentBase`-derived types.** Otherwise every markup edit is a contract
change, the baseline becomes noise, and the one diff that mattered gets approved along with forty that did not. There is
no `PublicApiRendering` annotator in v1.

**D21 — Agent routing (revised 2026-10-31: Sonnet implements, Opus validates).** A packet goes to `studio-grunt`
(Sonnet, medium) only when it is mechanical *and* fully specified: verbatim copies and ports with listed renames,
boilerplate emitted from a table in the packet, scripted runs and artefact collection. Design-bearing packets go to
`studio-implementer` (Sonnet, high), but the design itself is not delegated: the orchestrator (Opus) writes every
security, SQL, authorization and Marten-API decision into the packet, and the implementer stops on one the packet leaves
open. Final validation is Opus: `studio-reviewer` (xhigh) reads every packet that touches `src/`, scoped to its diff,
and the orchestrator reviews authorization code itself. Agents read AGENTS.md from context (CLAUDE.md imports it), never
with a fresh Read. Then they Read only the topic document(s) the routing table names for their area. Full text:
[build-and-release.md](docs/maintainers/build-and-release.md#d21).

**D22 — `blazor.web.js` reaches the host through a `PackageReference`, not through a copy in this package.** The assets
pack's `buildTransitive` targets make `_framework/blazor.web.js` the consuming project's own static web asset, and
`MapStudioFrameworkScript` tries the host web root, then the host endpoint, then a normally absent packaged copy: the
host's copy must always win. Full text: [ui.md](docs/maintainers/ui.md#d22).

**D23 — The studio has no forms, so it disables antiforgery on its own endpoints.** There is no `<form>` or `<EditForm>`
in `Components/`, so the component endpoints say `.DisableAntiforgery()` and a host that never calls
`app.UseAntiforgery()` still serves them. A screen that grows a real form brings the token back, scoped to that
endpoint. Full text: [ui.md](docs/maintainers/ui.md#d23).

**D24 — The packages ship no Blazor JS initializer; the shell loads `js/studio.js` itself.** A file named
`*.lib.module.js` in a package's `wwwroot` would be loaded into every Blazor app in the process (issue #2), so the shell
document `StudioDocument.razor` loads PostgresStudio's classic `js/studio.js` itself, with a relative `src`.
`NoJsInitializerTests` holds both halves. Full text: [ui.md](docs/maintainers/ui.md#d24).

**D25 — Every data table sits in its own labelled scroll region, and the shell never scrolls sideways.** A `<table
class="ms-table">` is always inside `<TableScroll Label="…">` (or the documents list's own `.ms-table-wrap` carrying the
same three attributes); `TablesAreScrollableTests` fails on a bare table. Fix a too-wide column first: the region is the
floor, not the answer. Full text: [ui.md](docs/maintainers/ui.md#d25).

**D26 — The stylesheet's UX sections are appended in cascade order, and later wins on purpose.** Each packet's new rules
go in a titled section appended to `studio.css`, and a section that re-declares an earlier selector wins by source order
on purpose. Edit an existing rule in place only where the packet's report says so. Full text:
[ui.md](docs/maintainers/ui.md#d26).

**D27 — The database browser: one capability, one schema filter, and Marten's tables are never read raw.** It lists the
database's other objects by kind, gated like `RunSql`: `Capabilities.BrowseDatabase` (off by default and under
`ReadOnly`) and `BrowsableSchemas`; every non-Marten definition and every row needs both, plus the write policy asked
**with `TenantId = null`**. `Capabilities.ViewDefinitions` is the definitions' second way in, authorized the same way
with its own name, and never reads a row. Marten's document and event tables and `mt_` objects are never read raw, and a
hidden document type is absent from every list. `BrowsableSchemas` limits the screens, not `RunSql`: `SqlConsoleRole` is
the real boundary. Every list reads `pg_catalog` and locks nothing it lists. **Reach, not scope:** an operation Marten
runs over a whole database is authorized as `(store, database, null)` unless the database is exclusively the selected
tenant's, never on a sharded tenancy (removing an orphaned progression row, 1.2, always asks `(store, database, null)`);
the SQL console, the Mode A subquery lift and `EXPLAIN` are always asked with `TenantId = null`. Full text, with *D27 in
PostgresStudio*:
[sql-and-catalog.md](docs/maintainers/sql-and-catalog.md#d27).

**D28 — LINQ mode is its own capability, parsed by JasperFx's Dynamic LINQ, built without `ToCommand`, and unconditional
since the 9.57 floor.** It sits behind `RunLinqQueries` (off by default, authorized like a write), parsed by JasperFx's
`DynamicQuery` under `DynamicQueryPolicy` behind a lexical screen and the studio's own walk of the parsed tree, and
built from `BuildLinqParser()` and `BuildDiagnosticCommand`, never `ToCommand`. It was a run-time light-up from Marten
9.48 until the 1.1 floor of 9.57 made it unconditional; the adapter is reached by reflection and still fails closed on a
JasperFx shape it does not recognise. Since 1.1 it also targets the event log through `QueryRawEvents()`, never passed a
tenant. `docs/linq-mode.md` is the threat model. Full text:
[marten.md](docs/maintainers/marten.md#d28).

**D29 — The health advisor: one shared sweep, three gates, and two deliberate exceptions.** A sweep is the database's,
once per interval under no visitor's identity; what a visitor sees of it is decided per finding and per piece of
evidence by three gates: `None` (what the Configuration page already says), `Database` (the store policy asked with
`TenantId = null` about the store's registered key and the resolved database's own `Id.Identity`) and `Activity`
(`ViewDatabaseActivity`, then that store policy, then the write policy with the capability named). A withheld finding is
counted, never described. The two deliberate exceptions: T02, T22 and T23 (and the Statements page) read
`pg_stat_statements` as the store's own role, fenced to `userid = current_user` and this database, while every other
health read and console statement stays under `SqlConsoleRole`; and the opt-in ASP.NET Core health check runs with no
visitor and reports only codes, outcomes, severities and counts, never a name, a tenant, a statement or anything
Postgres said. An accepted check (`AcceptedChecks`, #35) still runs and is shown on its check's row, out of every count
and state; acceptance changes no gate. **Since 1.1 Database › Connection (D33) is the `Database` gate**, as is its
paste-to-compare; only its sessions part and the pool peak counted by application name are `Activity`, recorded
separately, and the Overview's line asks for neither. Since 1.2 a finding may offer an on-demand action
(`IHealthFindingAction`, internal): one visitor's, under at least the finding's gate, audited, and kept nowhere; E06's
reads `mt_streams` as `SqlConsoleRole`. Full text: [health-engine.md](docs/maintainers/health-engine.md#d29).

**D30 — PostgresStudio: the core is a package of its own, and Marten Studio is built on it.** PostgresStudio is
everything about a PostgreSQL database rather than a Marten store, Marten-free (hard rule 16); Marten Studio is built on
it through `InternalsVisibleTo` and an exact version pin, not a public extension API, and `AddMartenStudio()` first runs
`PostgresStudioVersionGuard`. **Since 0.11 the diagnostics engine is a third package,** `PostgresStudio.Diagnostics`
(health sweep and checks, facts, sources, `IPostgresDiagnostics`, the health-check export, `PostgresDiagnosticsOptions` and
`AddPostgresDiagnostics`), with no ASP.NET Core in it and the same exact-pin discipline one level down: PostgresStudio is
packed with `PostgresStudio.Diagnostics [x.y.z]` and `AddPostgresStudio()` first runs
`PostgresStudioDiagnosticsVersionGuard`, which `AddMartenStudio()`'s guard repeats for the engine too. The studios register
the engine themselves: one registration per container, `AddPostgresDiagnostics()` refused beside either studio. Settings
split in two seams, `IDiagnosticsSettings` for the engine and `IStudioSettings` for the studio; a studio's one options
object answers both. PostgresStudio browses the application's own `NpgsqlDataSource` registrations, never a
connection string, and is asked against its own `PostgresStudioResource`. One studio per process. The public
`AddMartenStudio`'s own body stays free of PostgresStudio types (`PostgresStudioVersionGuardTests` scans its IL).
`PostgresStudioResource` has no tenant and is deliberately unrelated to `MartenStoreResource`: a handler written for one
must never be handed the other. Shared sentences come from `IStudioSettings.Text` (`StudioText`), so no Marten Studio
host sees a word change; static assets live only in `_content/PostgresStudio/`. Full text:
[architecture.md](docs/maintainers/architecture.md#d30).

**D31 — The diagnostics API: what the studio found, for the host's own code, by stable code, and never served by the
studio.** `IPostgresDiagnostics` (registered by `AddPostgresDiagnostics()`, or by either studio) returns what the engine
found, for the host's own code, by stable code; there are no mutes without a UI, and its studio paths are mount-relative
strings that mean nothing where no studio is mounted; results are
extend-only records, and activity-grade data is returned only when the call sets `IncludeActivity = true`. The studio
never serves it: nothing in either package puts an API result into an HTTP response, a log line above Debug, a metric,
the Activity ring or the health check's data, and no endpoint maps one; the Health page's Snapshot download, its
Markdown report and its compare are built from the visitor's own gated view, never from this API, and an API call is not
audited. A code is a letter and two digits, never reused, renamed or moved to another area, and a variant is frozen
within its code. Full text: [architecture.md](docs/maintainers/architecture.md#d31).

**D32 — The studio draws every plan, plan insights judge it, and EXPLAIN ANALYZE is the console's alone.** Every plan is
drawn through `QueryPlanReader` and `PlanView`; plan insights are pure evaluators with stable `P` codes in `codes.md`,
never a health check; `EXPLAIN ANALYZE` runs only under `RunSql`, for a statement the guard accepts, in the read-only
transaction; a plan reading a withheld schema is not shown. The facts Query › Plan reads beside a plan it ran (0.10)
pass each relation's schema and every ancestor's, then the row gate for values, which are never a Marten table's. Marten
Studio's own insights (1.0, P26 to P28) come through an internal seam, `IPlanInsightContributor`, that PostgresStudio runs
and MartenStudio registers: pure evaluators over (plan, facts, the tables the store declares from `StoreOptions`, never a
storage-ensuring call), for visible document types only, whose remedies are `StoreOptions` lines. Full text:
[plan-analyzer.md](docs/maintainers/plan-analyzer.md).

**D33 — The Connection page shows a data source's settings one by one, never its connection string.** A catalogue of the
loaded Npgsql's keywords decides what is shown. A `Secret` keyword shows only "set" or "not set"; an unknown keyword's
value is withheld; `PG*` environment values are shown on the same terms as their keyword, except `PGPASSWORD`; an
`Options` value is shown only for real server settings; paths are shown and files are never read. The raw string and the
meter's pool names never leave the reader, so no page, contributor, snapshot, log or audit entry can hold one, and the
sentinel tests hold all of this. The gate is D29's `Database` (the sessions part is `Activity`), and paste-to-compare
removes every secret before anything is read and keeps, logs and audits nothing. Full text:
[architecture.md](docs/maintainers/architecture.md#d33).

**D34 and D35 are reserved** for KStudio's shell seams and its authorization; they land with the shell (KStudio
M2).

**D36 — KStudio.Core is the Kafka engine: Confluent.Kafka alone, behind a wire seam that exists for the fake.** It holds
the catalog, window resolution, the scan engine, predicates, decoders, the governor and redaction, with no ASP.NET Core
and no studio type, and Confluent.Kafka (floor 2.14.0) is its only client (plan K2). Every broker call goes through the
internal `IKafkaWire`, whose only other implementation is the fast suite's hand-written fake broker; one contract suite
runs on the fake and on real brokers (Kafka 3.9 and 4.x), so the fake cannot drift from Kafka. No Confluent type crosses
the seam, and errors are values. Nothing connects at startup and nothing is a hosted service: one admin client per
cluster, created on first use and disposed when idle, and one consumer per scan, on a thread of its own. Engine log events
are 9650 to 9699. Full text: [kstudio.md](docs/maintainers/kstudio.md#d36).

**D37 — In KStudio, a read never writes.** Every client the engine builds is a clone of the host's configuration with the
engine's overrides: no topic auto-creation, no commit and no offset store, explicit offsets on every assign, a group id of
its own that is never subscribed, `auto.offset.reset=error`, an explicit isolation level, the engine's own fetch and queue
sizes, no statistics, no metrics push, and the engine's own log and error handlers. The host's builder delegates and
handlers are never run or inherited; the one hook a host may give is OAuth token refresh, applied to the engine's own
clients only. The engine sends no single-topic metadata request, lists no consumer groups, and has no produce, commit,
delete or alter call at all: the seam does not have one. Live tests prove on the broker that scans created no topic,
committed no offset and left no group. Full text: [kstudio.md](docs/maintainers/kstudio.md#d37).

**D38 — KStudio's windows and negatives are truthful.** A window is `[from, to)`. Per partition, its start is
offsets-for-times at `from`, which is exact; its end is offsets-for-times at `to` plus a margin under `CreateTime`, and at
`to` under `LogAppendTime`; a record matches only if its own timestamp is in the window, so the margin widens the scan,
never the window. Under `read_committed` a partition stops at its last stable offset and says so. Records lost to
retention before or during a scan, records past the margin, undecidable records, pruned partitions and every stop are
reported as values, so "no match" is said only of what was read. A scan stops for exactly one `KafkaScanStopReason` and
keeps an exact per-partition cursor. Full text: [kstudio.md](docs/maintainers/kstudio.md#d38).

**D39 — KStudio's search predicates are one hand-written grammar, three-valued, and compared as bytes where they can
be.** The engine parses the grammar itself (no package and no expression escape hatch): `key`, `header.<name>`,
`ce.<attribute>` and `any`, then `value` and JSON paths behind `ReadPayloads` (M3). A key literal is compiled to bytes
through the topic's key format and compared without decoding. A clause that needs a decode it cannot make is undecidable,
never false, and undecidable records are counted. Clauses run cheapest first and short-circuit, which a decode counter
proves. An error names a position and never repeats a literal. Nothing is deserialized into a CLR type. Full text:
[kstudio.md](docs/maintainers/kstudio.md#d39).

**D40 — KStudio redacts inside the engine, before any sink.** A record leaves the engine only as a `KafkaMatch`, built by
one factory after the topic's rules and the built-in header deny-list (`authorization`, `proxy-authorization`, `cookie`,
`set-cookie`, `x-api-key`) have run; the wire record never leaves `KStudio.Core`. A rule that cannot be applied withholds
the value. A redacted field takes only `=` and `in`, `any` never reads one, and every text the engine writes for a sink -
the audit's predicate, a permalink's predicate, an error - shows a literal on a redacted field as `<redacted>`. Full
text: [kstudio.md](docs/maintainers/kstudio.md#d40).

**D41 — KStudio bounds the cost of a scan in the engine.** Concurrency (two scans per process, one per owner), a byte rate
(10 MiB/s per process, paced by consumption), per-scan bytes, records, duration and matches, and an owner's rolling
hourly byte budget. Memory is stated, about $S(Q + F + m c)$, 95 MiB at the defaults. A scan above the confirmation
threshold starts only with a confirmation the engine checks. A profile or a visitor can only lower a limit. Full text:
[kstudio.md](docs/maintainers/kstudio.md#d41).

## Package budget

Complete, as of the bootstrap commit. Versions are centrally pinned in `Directory.Packages.props`, with
`CentralPackageTransitivePinningEnabled` on.

| Where | Package | Version | Why it is here |
|---|---|---|---|
| `src/MartenStudio` | `Marten` | `9.57.0` (latest tested `9.57.0`) | One of the two `PackageReference`s the shipped package makes. Floor, not exact pin — a consumer on a newer Marten 9.x must not be dragged back. Since 2026-11-03 the floor follows the latest release (the user's policy of 2026-11-02: consumers keep up with Marten): 9.57.0 fixes #5797, where compacting a stream that folds to null replaced its whole history with an empty snapshot, which `CompactStreams` exposed on every host below it, and it makes the run-time light-ups from 9.31 to 9.53 unconditional (see *Package budget changes*, 2026-11-03; the 9.39.0 security floor of 2026-10-15 is history). Both suites also run at a pinned latest-tested version, `MartenStudioMartenLatest`, which equals the floor until the next Marten release; the package is packed only at the floor. JasperFx / JasperFx.Events 2.98.0, Weasel.Core / Weasel.Postgresql / Weasel.Storage 9.48.0 and Npgsql 9.0.4 arrive transitively at the floor. The studio's own compilation drops the JasperFx source generator Marten bundles (`MartenStudio.csproj`). |
| `src/PostgresStudio` | `Microsoft.AspNetCore.App.Internal.Assets` | `10.0.12` | `blazor.web.js`, which as of .NET 10 lives nowhere else (D22); it reaches a MartenStudio host through MartenStudio's `ProjectReference` to PostgresStudio (two hops, measured by PS-0, *Package budget changes* 2026-10-20). Three `.js` files and one MSBuild target: no `lib/`, no `ref/`, no runtime assembly, so it pins no ASP.NET Core patch on the host. Floor, not exact pin — a host whose own SDK already resolves this pack keeps its version, because NuGet takes the higher of the two. Its `buildTransitive` targets are what put the script in the *consumer's* static web assets. MartenStudio no longer references it, but MartenStudio's nuspec still lists it at this version: under `CentralPackageTransitivePinningEnabled` NuGet writes a centrally pinned transitive package into the nuspec (the same line MartenStudio 0.4.0 carried when it referenced the pack directly), and `Pack` asserts the line. |
| `src/PostgresStudio.Diagnostics` | `Npgsql` | `9.0.4` (latest tested `10.0.3`) | The diagnostics engine's database driver, and the studio's: since 0.11-C `src/PostgresStudio` takes it through its `ProjectReference` to this project and no longer references it directly (*Package budget changes* 2026-11-01). PostgresStudio has no Marten to bring it. A central row that only projects setting `PostgresStudioPinsNpgsql` see, so MartenStudio keeps taking Npgsql through Marten and the two never drift there (D3; *Package budget changes* 2026-10-20). Floor, not exact pin; 9.0.4 is Marten 9.57.0's own Npgsql, so a MartenStudio host resolves Marten's Npgsql, which is never lower (`NpgsqlVersionTests`; *Package budget changes* 2026-11-03, which moved it from the 8.0.3 that PS-7 measured on 2026-10-26). `samples/PostgresStudio.SampleData` references it directly too, and `samples/PostgresStudio.Sample`, both PostgresStudio test projects and `src/PostgresStudio` itself set `PostgresStudioPinsNpgsql` so the same row pins it transitively; all of them are in `postgres-studio.slnf`, so the latest-Npgsql pass builds them at 10.0.3. |
| `src/PostgresStudio.Diagnostics` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Diagnostics.HealthChecks` | `10.0.0` each | The engine has no `FrameworkReference` to `Microsoft.AspNetCore.App` (hard rule 16), so the abstractions it registers into, logs through and exports a health check with come as packages. Four conditional central rows, visible only to projects that set `PostgresStudioDiagnosticsPinsExtensions` (this one, and the 0.11 worker sample), so Marten's, JasperFx's and Weasel's own Microsoft.Extensions.* stay unpinned in MartenStudio and its tests. Floors, not exact pins. 10.0.0 and not 8.0.x because Npgsql 10.0.3's net10.0 asset asks for `Logging.Abstractions` 10.0.0 and an older pin would silently win (measured: no NU code, 8.0.0 resolved), which is not a floor (D1: the library is net10.0 only). *Package budget changes* 2026-11-01. |
| `src/KStudio.Core` | `Confluent.Kafka` | `2.14.0` (latest tested `2.16.0`) | KStudio's only Kafka client (plan K2), behind the engine's internal wire seam (D36). Floor, not exact pin: 2.14.0 is what WolverineFx.Kafka 6.61 pins, so a Wolverine host is never raised. It brings `librdkafka.redist` (native librdkafka for Windows, Linux and macOS) at its own version, unpinned. Unconditional row: only the KStudio projects reference it. Not packed before M4 (K8). *Package budget changes* 2026-11-09. |
| `src/KStudio.Core` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options` | `10.0.0` each | The diagnostics engine's conditional rows, seen by KStudio.Core through `KStudioCorePinsExtensions`: the engine has no ASP.NET Core either (D36). |
| `tests/KStudio.Integration.Tests` | `Testcontainers.Kafka` | `4.15.0` | The live suite's brokers, `apache/kafka:3.9.2` and `4.4.0` in KRaft mode, one per image with SASL/PLAIN and ACLs. |
| the three sample hosts, both `tests/…Integration.Tests` | `Testcontainers.PostgreSql` | `4.15.0` | D18's throwaway Postgres (the Marten and Postgres Studio sample hosts and the diagnostics worker), and the container each integration suite runs against. |
| `samples/PostgresStudio.Worker` | `Microsoft.Extensions.Hosting` | `10.0.0` | The generic host the diagnostics worker sample runs in (*Package budget changes* 2026-11-01). A fifth row of the conditional `PostgresStudioDiagnosticsPinsExtensions` group: no shipped package references it. |
| all six test projects | `Microsoft.NET.Test.Sdk` | `18.10.0` | The VSTest host Fallout's `ITest` drives. |
| all six test projects | `xunit.v3` | `3.2.2` | Held — see below. |
| all six test projects | `xunit.runner.visualstudio` | `3.1.5` | The VSTest bridge. |
| all six test projects | `GitHubActionsTestLogger` | `2.4.1` | Held — see below. |
| all six test projects | `AwesomeAssertions` | `9.6.0` | The MIT fork of FluentAssertions. One assertion library, and **no mocking library**: the data seam is our own set of interfaces, and hand-written fakes are smaller and more auditable than a mocking framework's call-matching DSL. Its namespace is `AwesomeAssertions`, not `FluentAssertions` (verified 2026-09-14); every test project declares it as a global using. |
| both fast suites (`tests/MartenStudio.Tests`, `tests/PostgresStudio.Tests`) | `bunit` | `2.13.3` | Component tests. |
| the three fast suites | `Verify.XunitV3` | `32.0.0` | Held — see below. Also the source of each fast suite's `global using Xunit;`, which is why neither csproj declares one. |
| the three fast suites | `PublicApiGenerator` | `11.5.4` | The public API snapshots (D20), one per package. |
| both fast suites | `Microsoft.AspNetCore.TestHost` | `10.0.12` | `UseTestServer()` for the endpoint-metadata and startup-guard tests. TestHost rather than Mvc.Testing: those tests never need a `WebApplicationFactory`. |
| `tests/MartenStudio.Integration.Tests` | `Microsoft.AspNetCore.Mvc.Testing` | `10.0.12` | `WebApplicationFactory` over the real sample host. |
| both `tests/…Integration.Tests` | `Microsoft.Playwright` | `1.62.0` | The browser suites (D19). |
| `build/` | `Fallout.Common`, `Fallout.Components` | `10.4.0` | The orchestrator; the build project opts out of CPM. |
| `build/` | `NuGet.Frameworks` | `7.9.0` | Held — see below. |

SourceLink needs no package reference: it is in-SDK on .NET 8 and later.

### Package budget changes

Every new package, floor move and latest-tested bump needs a dated entry in `docs/maintainers/package-budget-changes.md`
first. What those entries settled, and the versions held deliberately (xunit.v3 3.2.2, Verify.XunitV3 32.0.0, Fallout
10.4.0, NuGet.Frameworks 7.9.0), are in
[build-and-release.md](docs/maintainers/build-and-release.md#package-budget-changes). Floors follow the latest releases: at a minor
release the Marten and Npgsql floors may move to the latest tested versions (user, 2026-11-02). Keep a Marten host on
Marten's own Npgsql (`NpgsqlVersionTests`). Read a package's entries before changing its row.

## Layout

The directory tree is in `docs/maintainers/layout.md`: every directory under `src/`, `samples/`, `tests/`, `build/`,
`.claude/` and `docs/`, and what belongs in each. Read it before adding a file. Add a directory to it in the same pull
request that creates the directory, because `AgentsLayoutTests` fails on a directory the tree misses and on one that no
longer exists. The roots in brief:
- `src/PostgresStudio/` is the shared studio, with no Marten; `src/MartenStudio/` holds the Marten screens and checks.
  Each has `Components/`, `Services/` and `Internal/Sql/` (hard rule 4).
- `samples/` holds both demo hosts and their demo data (D17, D18).
- `tests/` holds the six test projects (*Testing*).
- `build/` is the Fallout orchestrator.
- `docs/` holds the user documentation. `docs/maintainers/` holds the layout tree, the budget's dated entries and the
  decision notes.

Outside those roots: `.github/workflows/` holds the four **generated** workflow files (hard rule 2),
`.config/dotnet-tools.json` pins the Fallout CLI, `.fallout/` holds the parameter file and the generated build schema,
`postgres-studio.slnf` is the solution filter the latest-Npgsql pass builds (exactly the projects that set
`PostgresStudioPinsNpgsql`), and `CHANGELOG.md` is the version authority.

`docs/maintainers/` also holds the topic documents the routing table above names.

## Build

The orchestrator is [Fallout](https://fallout.build) 10.4.0 (stable channel), the maintained hard fork of NUKE. The CLI
is pinned in `.config/dotnet-tools.json` as `fallout.globaltool` (command `fallout`) and resolves `build/_build.csproj`
by folder-name convention.

**10.4.0 is not stale, and 11.0.x is not an upgrade.** Do not "update" Fallout; the edge channel would fail to compile
(`GitHubActionsAttribute.Env`). Full text: [build-and-release.md](docs/maintainers/build-and-release.md#build).

```powershell
.\build.ps1 Test                           # restore, compile, install browsers if asked, run all four test projects
.\build.ps1 Test --playwright              # ... and install Chromium first (D19)
.\build.ps1 Test --marten-version latest   # ... at the latest-tested Marten instead of the floor
.\build.ps1 Test --npgsql-version latest   # ... and then PostgresStudio's projects at the latest-tested Npgsql
.\build.ps1 Test --confluent-version latest   # ... and KStudio at the latest-tested Confluent.Kafka
.\build.ps1 Pack                           # the three NuGet packages into artifacts/packages (always at the floors)
.\build.ps1 Browsers                       # just the Chromium install
```

`CHANGELOG.md` is the **version authority**: its top section is parsed in `OnBuildInitialized` and passed to every
compile, test and pack as `-p:Version=`. The file is never mutated by the build, and its first line must parse as a
version header (`# 0.1.0`) — **do not add a `# Changelog` title**, it would abort the build. `Directory.Build.props`'s
`VersionPrefix` is only the fallback for a plain `dotnet build`, and `VersionFallbackTests` holds it to the CHANGELOG's
top version (without a prerelease suffix): a new CHANGELOG section moves it in the same pull request.

Keep changelog entries as `- ` bullets with wrapped continuation lines: the GitHub Release body is built from them, and
anything else ships it empty (*Release engineering*).

The Marten version is `Directory.Packages.props`'s: `MartenStudioMartenFloor` and `MartenStudioMartenLatest`, chosen by
`--marten-version` (`floor`, `latest` or an exact version, never a range). `Pack` packs at the floors only and refuses
anything else. PostgresStudio's Npgsql follows the same scheme with `--npgsql-version`; anything but its floor gets a
pass of its own over `postgres-studio.slnf`, never the `.slnx`. `Pack` packs `src/PostgresStudio.Diagnostics`, then
`src/PostgresStudio`, then `src/MartenStudio`, all at the CHANGELOG version, each depending on exactly the one below it
(`PostgresStudio.Diagnostics [<version>]`, `PostgresStudio [<version>]`), and `Publish` pushes the three in that order. The
build needs at least one commit. To bump the latest-tested Marten, change `MartenStudioMartenLatest`, run both suites
with `--marten-version latest` and fix or record every difference, in a pull request of its own. A project that sets
`PostgresStudioPinsNpgsql` goes into `postgres-studio.slnf` in the same pull request. Full text:
[build-and-release.md](docs/maintainers/build-and-release.md#build).

## Testing

```powershell
.\build.ps1 Test                                          # everything, through the orchestrator
.\build.ps1 Test --marten-version latest                  # everything, at the latest-tested Marten
dotnet test tests\MartenStudio.Tests                       # the fast suite alone
$env:MARTENSTUDIO_PG_REUSE='false'                         # a container of this run's own; see PostgresFixture
dotnet test tests\MartenStudio.Integration.Tests           # needs Docker - run `docker info` first
dotnet test tests\MartenStudio.Tests -p:MartenStudioMartenVersion=9.57.0   # one suite at another Marten
dotnet test tests\MartenStudio.Tests --filter "FullyQualifiedName~ApiSurface"
dotnet test tests\PostgresStudio.Tests                     # Postgres Studio's fast suite
dotnet test tests\PostgresStudio.Integration.Tests         # its live suite - needs Docker
.\build.ps1 Test --npgsql-version latest                  # ... and then PostgresStudio's projects at the latest-tested Npgsql
dotnet test postgres-studio.slnf -p:PostgresStudioNpgsqlVersion=10.0.3   # that pass alone; never the .slnx
```

**Both suites run at two Marten versions** (both 9.57.0 until the next latest-tested bump): the floor (`build.yml`) and
the pinned latest-tested 9.x (`marten-latest.yml`, which runs `dotnet fallout Test` with `MARTEN_VERSION: latest`). A
change is green only when both are. A fix for a behavioural difference between them must hold at both ends; one that
needs a member only the newer side has is run-time light-up behind a probe, unless the floor itself moves to that
version (allowed since the user's 2026-11-02 policy; *Package budget changes* 2026-11-03). `PublicApiTest` must pass at
both without a baseline edit, which is one reason `JasperFxSourceGeneratorTests` exists.

Container reuse is on by default locally (a second run attaches to the same Postgres) and off on CI.
`MARTENSTUDIO_PG_REUSE=false` is how one run opts out, which every run that might overlap another — parallel agent
packets, two terminals — must do, because schema names derive from test class names and two runs on one reused container
drop each other's tables. `TESTCONTAINERS_REUSE_ENABLE` is **not** read by Testcontainers 4.x (verified by
decompilation, 2026-09-14) and must not be used.

There are seven kinds of test, in six projects: unit, component, endpoint and metadata, public API snapshot,
conventions, Marten API surface, and integration and browser. Waits are always wait-on-condition, **never
`Task.Delay`**. Integration tests drive everything through the studio's own services, so the gating is exercised, not
bypassed. A class that needs a server setting of its own starts a `postgres:17-alpine` of its own, never reused; a test
that creates, grants or alters a role on the matrix takes `PostgresVersionMatrix.IsolatedServerAsync`, never
`ServerAsync`; a live test that needs a `VACUUM` to remove rows, or resets statistics, works in a database of its own.
Marten, JasperFx and Wolverine sources are the sibling checkouts `D:\Work\marten`, `D:\Work\jasperfx` and
`D:\Work\wolverine` (check out the tag `Directory.Packages.props` pins); read them before decompiling, and never modify
them. What each kind holds and the live-suite rules: [testing.md](docs/maintainers/testing.md).

Run `docker info` before the integration suite. If Docker is not running, say so — a skipped Docker suite reported as a
pass is worse than a failure.

To approve a Verify baseline: run the test, open the `.received.*` next to the `.verified.*`, read the diff line by
line, then accept it (`dotnet test` with `DiffEngine` open, or rename the file). `*.received.*` is gitignored and must
never be committed; `*.verified.txt` is checked out LF and UTF-8 by `.gitattributes` because it is compared byte for
byte.

## Release engineering

A release is cut by pushing a `v*.*.*` tag, which starts **two independent workflows**: `publish.yml` (nuget.org) and
`release.yml` (the GitHub Release). `CHANGELOG.md` stays the version authority: land the changelog entry first, then tag
that commit; `CreateGitHubRelease` and `Publish` both refuse unless `GITHUB_REF_NAME` equals `v{version parsed from
CHANGELOG.md}`. The one workflow that can mint a nuget.org key (`publish.yml`, `id-token: write`) must not also hold
repository write. Never push a release version by hand. Run the ladder locally (`dotnet fallout Pack`, then `dotnet
fallout Publish --skip`) before any tag. The NuGet leg, the token exchange's pitfalls and the one-time setup:
[build-and-release.md](docs/maintainers/build-and-release.md#release-engineering).
