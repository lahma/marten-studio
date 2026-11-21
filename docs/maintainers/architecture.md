# Architecture

This is the full text of D2, D10 to D12, D17, D18, D30, D31, D33 and D49: the packages and the shell they attach to, routing and assets, the data seam, the samples, the diagnostics API, the Connection page's promise and what 2.0 decided about one product. AGENTS.md states each decision in brief; this document holds the detail and wins where they differ.

## D2

**D2 — Interactive Server render mode only, and `IsTrimmable=false` said out loud.** Blazor Server is what
lets the studio run against the host's in-process `IDocumentStore`; WebAssembly would need an API surface
that is itself the security problem. `IsTrimmable=false` is stated in every shipped csproj with a comment rather than
left to the default, because Blazor sets component `[Parameter]` properties by name and the document viewer
deserializes into types discovered from `IDocumentType.DocumentType` at run time — a trimmer told it may
cut has no way to see either.

## D10

**D10 — No SignalR hub.** Pages poll with a `PeriodicTimer` at `DataStudioOptions.RefreshInterval`, paused on hidden
tabs, through a single-flight snapshot cache so that N circuits cost one query per interval rather than N.
Where the daemon is hosted in this process, a lazy observer on `ShardStateTracker` gives sub-second
projection state without any of that. The convention builder carries no hub slot; adding a hub would add a host-visible
endpoint for a feature that polling covers. The projections screen keeps its
own lag samples (`ProjectionLagHistory`), written only by its single-flight snapshot read: 120 per shard, 200 shards per
database, databases bounded by `HealthDatabaseBound` (M2). Dead letters are grouped in the web process from one read
per interval of the newest 20,000 letters' columns (`DeadLetterGroupQueries.BuildGroupRows`), and a group's letters are
found again in SQL by the same rule (`DeadLetterFingerprint`) - the list's only among those same newest letters
(`DeadLetterGroupCache.LettersGrouped`), the skip's count and the skip among all of a group's letters;
`DeadLetterGroupsLiveTests` holds the two equal (M3;
why: decision-notes.md, D10).

## D11

**D11 — Sub-path mounting is ported from Quartz.Dashboard, and the shell does it for every module.** The shell,
`src/DataStudio`, owns the mount: `MapDataStudio()` (`StudioEndpointMapper`) re-roots each page assembly's compile-time
routes at map time, from *that* assembly's own root, so a module's `@page "/marten/documents"` is served at
`{mount}/marten/documents` and the Postgres pages, which the Marten module shows over its databases, at
`{mount}/postgres/…`. **Every assembly keeps its root**: the Postgres module `/postgres`, the Marten module `/marten`, the Kafka
module `/kafka`, and a page's address below the mount is its `@page` template without the leading slash, always. There is no
root module: the shell's own pages are the front page at the mount and Activity at `{mount}/activity`, and the segments the shell
keeps for itself (`activity`, `_blazor`, `_framework`, `_content`, `opaque-redirect`) are no module's. `/_blazor`,
`blazor.web.js`, `opaque-redirect`, `_content/DataStudio` - the shell's assets, whichever modules are mounted - and every other
file of the host's `_framework` tree (`{Path}/_framework/{**file}`, forwarded to
whatever the host serves at `/_framework/{file}`, else its web root's `_framework/`, and a 404 for a path that could
climb out of it) are mirrored under the mount path. The last is what lets the .NET 11 SDK's
hot-reload initializer, which a Debug build lists among the page's Blazor initializers, load under the studio-rooted
`<base href>`; the two literal endpoints keep their own handlers by route precedence. One framework endpoint stays at the root,
because the browser asks for it there: ASP.NET Core 11's Development-only Hot Reload settings
(`/_framework/dotnet-browser-tools/hot-reload-settings.json`), which every `MapRazorComponents` root maps, ordered last
(dotnet/aspnetcore#70461). The studio's copy is rewritten as a parameter that matches only that file, so it ranks below a host's
literal copy by route precedence: a host with a Blazor app of its own answers from its own copy, one without from the studio's (#98).
That forward serves the *host's*
files, so unlike the package-content assets it is never anonymous by the studio's own act: it carries the studio's policy,
or is part of the builder `MapDataStudio()` returns (the caller's statement and the `FallbackPolicy` govern it as they govern
the page), and it answers 404 where the host's root endpoint would refuse the visitor (see D22 in ui.md). Endpoint display names keep the
compile-time template, so no log or diagnostic text is ever built from one. A 1.x address (`{mount}/documents`, `{mount}/health`)
matches nothing and is a 404; there is no redirect (D49). This is ~250 lines of genuinely fiddly engineering that already works in production
somewhere; rewriting it would be the single most likely way to ship something that renders locally and
404s behind a path base. There is no "integrate into the host's own Blazor app" mode.

## D12

**D12 — The data seam is internal, across every assembly.** Per-area service interfaces (`IDocumentDataService`,
`IEventDataService`, …) with `InternalsVisibleTo` for the test projects and, along the references, for the module above:
the shell grants its internals to `DataStudio.Postgres`, `DataStudio.Marten` and `DataStudio.Kafka`, the engine grants its to
`DataStudio.Postgres` and `DataStudio.Marten`, the Postgres module its to `DataStudio.Marten`, and `DataStudio.Kafka.Core` its to
`DataStudio.Kafka` and `DataStudio.Kafka.Wolverine` (`IKafkaClusterSource`, D43). Each package's *registration* surface is snapshot-tested: the shell's is two
static classes and two classes, the Postgres module's one static class, two classes and one record, the Marten module's two static
classes, two classes and two records, the Kafka module's one static class, two classes and one record; the engine's also carries its inbound
types and the D31 read model, which are results and hooks, never seams (hard rule 7). No module reaches another's internals but the Marten
module, which is built on the Postgres module's; the Kafka module reaches neither database module's. Everything a consumer can bind to is everything we have to keep working.

## D17

**D17 — The demo domain lives in `samples/DataStudio.Sample.Marten.Domain` and does not reference
`DataStudio.Marten`.** It is shared by the sample host and by the integration tests, which is the reason it is a
project rather than a folder. The missing reference is deliberate and structural: the studio must work
against a store that knows nothing about it, and a reference here would let that invariant rot without
anyone noticing. The demo data with no Marten in it - the relational demo schemas and the tenant
databases - is `samples/DataStudio.Sample.Postgres.Data` (Npgsql only), which `DataStudio.Sample.Marten.Domain` and the Postgres
sample both reference; it references no studio either.

## D18

**D18 — The sample host starts a throwaway Postgres when no connection string is configured.** Zero-config
`dotnet run` is what makes the sample a demo rather than a setup exercise. It prints a loud banner saying
what it did, and it throws with guidance when Docker is absent — what it must never do is fall back to
`localhost` silently and appear to work against somebody's real database. It applies to every sample that has a
database (the Marten, Postgres and Kafka samples, and the diagnostics worker). The Postgres
sample does the same with `ConnectionStrings:Postgres`, but starts a fresh container unless
`MARTENSTUDIO_PG_REUSE=true`, under a reuse identity of its own (the variable's name is the one the repository started
with, and it is still the one every container this repository starts reads). The Marten sample judges a reused database before
Marten touches it (`SampleDatabase`): one another build of it seeded is dropped and seeded again on the throwaway
container or with `--allow-data-generation`, and on any other database the host stops with one line that says what
to do. Its throwaway container has a reuse identity of its own too, so `dotnet run` never attaches to the integration
suite's reused container. The Kafka sample starts a throwaway Kafka broker and a Postgres of its own, and reuses them
only with `DATASTUDIO_KAFKA_SAMPLE_REUSE`.

## D30

**D30 — One shell, three modules, and an engine you can take alone.** Data Studio is one product in seven packages. The shell,
**`DataStudio`**, is the chrome every module draws into: the mount, the layout and rail, the front page, the Activity page, the
authorization of every endpoint, the JSON toolkit, the stylesheet, the script and the assets; it references ASP.NET Core and
nothing else, so a host with only Kafka gets no Npgsql and no Marten through it (hard rule 16). A **module** is what the shell
shows: **`DataStudio.Postgres`**, everything about a PostgreSQL database rather than a Marten store (the database browser,
Sessions, the Health page, the SQL console, Relationships, the router's database half, the capability guard and the shared checks);
**`DataStudio.Marten`**, the Marten screens, the Marten checks and the Marten half of every seam, built on the Postgres module; and
**`DataStudio.Kafka`** (D34). The **engines** are `DataStudio.Postgres.Diagnostics`, which needs no UI and no ASP.NET Core
(the health sweep and its checks (D29), the facts and their readers and caches, the sources `IPostgresDatabaseSource` and
`IPostgresCapacitySource` and the data-source registry, the SQL builders they need (the first of the three hard-rule-4 folders),
`IPostgresDiagnostics` and its read model (D31), the health-check export, `PostgresDiagnosticsOptions` and `AddPostgresDiagnostics`),
and `DataStudio.Kafka.Core` (D36). A host calls `AddDataStudio()` once and adds a module to the builder it returns:
`AddPostgres()`, `AddMarten()` and `AddKafka()`, in the order it wants them shown, and `MapDataStudio()` serves them all.

*The references.* Marten → Postgres → shell and Postgres → engine; Kafka → shell and Kafka.Core; Kafka.Wolverine → Kafka.Core.
Each package is packed with an **exact** dependency (`[x.y.z]`) on the ones it is built on and checks it at startup, so the
packages are one program in many files, as the nuspec says (*Build*): the Marten module is packed with
`DataStudio.Postgres [x.y.z]` (`DataStudioMartenPinsPostgresExactly`), the Postgres module with `DataStudio.Postgres.Diagnostics [x.y.z]` and
`DataStudio [x.y.z]` (`DataStudioPostgresPinsDiagnosticsExactly`, `DataStudioPostgresPinsShellExactly`), the Kafka module with
`DataStudio [x.y.z]` and `DataStudio.Kafka.Core [x.y.z]` (`DataStudioKafkaPinsFamilyExactly`) and Kafka.Wolverine with
`DataStudio.Kafka.Core [x.y.z]`. The version guards: `AddMarten()` first runs `PostgresVersionGuard`, which names no Postgres type and loads
the assembly by name, and checks the engine and the shell the same way; `AddPostgres()` runs `PostgresDiagnosticsVersionGuard`
before it names an engine type, and checks the shell; the Kafka module's `KafkaCoreVersionGuard` checks the shell and the engine, and
Kafka.Wolverine's `KafkaWolverineVersionGuard` the engine. The public `AddMarten` and `AddPostgres` bodies name no type of what they
guard (the rest is in a `NoInlining` method), and `PostgresVersionGuardTests` and `PostgresDiagnosticsVersionGuardTests` scan their IL to
keep it so, so that a newer assembly whose members moved, or another build of the same version, gets the guard's message and not
a `MissingMethodException`. A guard cannot explain a *missing or older* assembly that a public signature names: `AddPostgres`'s parameter type
`Action<PostgresOptions>` derives from the engine's `PostgresDiagnosticsOptions`, and both `Add…` calls extend the shell's
`DataStudioBuilder`, so the runtime loads the engine and the shell to compile the call itself, in every host, and a missing or older
one fails in the loader with a `FileNotFoundException` that names it and its version (measured 2026-11-01).

*Why `InternalsVisibleTo` and an exact pin, not a public extension API.* A module extends nearly every service of the shell, and
the Marten module nearly every one of the Postgres module; publishing those seams would multiply the public surface (hard rule 7) for a
plug-in model nobody asked for (*Out of scope* in the 2.0 plan: a public module API is a follow-up).

*One shell per application, and what a module is.* The shell's options (`DataStudioOptions`: `Path`, `Title`, `ApplicationPath`,
`AuthorizationPolicy`, `ReadOnly`, `RefreshInterval`) are the shell's, and a module's options have none of them. `AddDataStudio()`
and every `Add…()` are idempotent: called twice they configure the options twice and register once. **One database module per
application:** `AddMarten()` brings the Postgres module's pages over the Marten stores' databases as part of the Marten module (it needs
`DataStudio.Postgres`),
and `AddMarten()` beside `AddPostgres()` is refused, in either order; `AddPostgresDiagnostics()` is refused in a container that holds
either, in either order. A module (D34) is an internal `IStudioModule` that the shell composes into its descriptor at first
resolution, so registration is order-independent for what is refused and order-*dependent* for what is shown: the rail, the banner, the
chip, the credit and the front page's tiles list the modules in the order the host's `Add…()` calls were written. The settings split in
three seams: `IShellSettings` (the shell's options, which every module's settings answer), `IDiagnosticsSettings` (the engine's) and
`IStudioSettings` (the Postgres module's, which is both of those and the studio's), and a module's one options object answers the ones
it is. `AddPostgres()` and `AddMarten()` register the engine themselves from that one object.

*What the engine leaves out.* **No mutes without a UI:** a mute is a visitor's choice, so the engine has
`AcceptedChecks` (the host's) and no mute list, and a finding's `StudioPath` and `StudioLinks` stay mount-relative strings that
mean nothing where no studio is mounted. `samples/DataStudio.Sample.Postgres.Worker` is the engine alone, as a worker host.

*What the Postgres module browses.* The application's own singleton `NpgsqlDataSource` registrations, never a connection
string (see the introduction): the unkeyed one by default (`IncludeDefaultDataSource`, the source `default`), a keyed or
otherwise-held one named with `AddDataSource`, and a host's own databases named with `AddDatabaseSource` and an
`IPostgresDatabaseSource` that lists names and hands back each one's data source on demand. A factory is called once,
with the root provider, and its result kept for the process and never disposed; a scoped or transient registration is
refused, as it would build a pool per visitor. A database's identity is its source's own (a Marten store database's
`Id.Identity`, unchanged; a DI-registered data source's registration name; the name a host lists); `?db=`, policies,
caches, history, mutes and the audit key on it, so it must be stable across processes and restarts.

*Authorization.* The Postgres module is asked against its own `PostgresResource(SourceName, DatabaseIdentifier,
Capability)`, which has no tenant and is deliberately unrelated to `MartenStoreResource` and to `KafkaTopicResource`: a handler written for one
must never be handed another. `SourceAuthorizationPolicy` is its store policy; D27 applies as *D27 in the Postgres module* says. The shell's
`AuthorizationPolicy` decides who gets in, on every endpoint including the front page; `DataStudioOptions.ReadOnly` turns every module's capabilities
off, and a module's own `ReadOnly` (`MartenOptions.ReadOnly`, `PostgresOptions.ReadOnly`) turns off that module's only. The Kafka module has no switch
of its own: it obeys the shell's (D35).

*Routes and assets* follow hard rule 16 and D11; the stylesheet and the script are the shell's, in `_content/DataStudio/`, and no module ships
an asset of its own that the host would load.

*Words.* Each module has its own words, as a `StudioText` (the class is the engine's, with every sentence the database modules share): the Marten module's `MartenText` in
`DataStudio.Marten`, the Postgres module's `PostgresText.Instance` in the engine's assembly, and the engine's own set, `PostgresText.DiagnosticsInstance`, for a host that
calls `AddPostgresDiagnostics()` and has no studio; the Kafka module's `KafkaText` is in `DataStudio.Kafka`, and the shell's are in `StudioShellIdentity`. Each is pinned by a
`WordsTests` baseline (D49).

## D31

**D31 — The diagnostics API: what the studio found, for the host's own code, by stable code, and never served by the
studio.** History: decision-notes.md, D31. `IPostgresDiagnostics`, registered by `AddPostgresDiagnostics()` alone (no UI, no
ASP.NET Core) and by `AddPostgres()` and `AddMarten()` (one registration per process: the
modules register it themselves and the engine's own call is refused beside them), lists the databases the studio would list (`ListDatabasesAsync`)
and returns each one's `PostgresDiagnosticsSnapshot` (`GetSnapshotAsync`): findings by stable code and variant, and
typed sections for storage, statements, settings and extensions, connections and the write-ahead log (`Wal`). `IPostgresDatabaseSource` (D30) and
`IPostgresCapacitySource`, found in DI to report compute capacity SQL cannot see (Aurora Serverless v2's ACU), point the
other way. A Marten host reaches it through the Postgres module and the engine its exact pins name (hard rule 7).

*Why public, when D12 keeps the data seam internal.* It is results, not seams: with no `IStudioDatabase`, sweep or check
base class, a host cannot add a check or change a threshold (a non-goal of #8). The studio stays English; a host words
findings in its own language, keyed on codes.

*No visitor, so no visitor's gates, and no new reach.* The API answers host code, which already holds the data source or
the store, so it reads nothing that code could not: the pages' statements, as the same roles (`SqlConsoleRole` for all
but D29's first exception, whose `userid = current_user` fence holds), through the same single-flight caches. It skips
the per-visitor decisions (the store, source, write and authorization policies, the capabilities, `ReadOnly`, which is
about the people who open the studio, and D27's masking of withheld schemas) and keeps the content rules: no hidden
document type is named; a statement text still carrying a literal, or not a plain query or write, is withheld; comments
are masked; texts are capped; Postgres' error words are not carried, only the studio's sentence and the SQLSTATE.

*Activity-grade data is opt-in per call.* What D29 puts behind `Activity` (the statements section, application names,
other sessions' evidence, Activity checks' findings, findings about one tenant) is returned only when the call sets
`IncludeActivity = true`, else counted as withheld, as a visitor short of that gate is told: a section by name in
`Withheld`, a finding in `FindingsWithheld`, evidence by its label. The default is the safe one, for D4's reason: a call
written without reading the docs must not put statements in an e-mail.

*The studio never serves it.* Nothing in any package puts an API result into an HTTP response, a log line above
Debug, a metric, the Activity ring or the health check's data, and no endpoint maps one: where a result goes is the
host's decision. So the opt-in health check stays codes and counts (D29's second exception); the Health page's Snapshot
download, its Markdown report and its compare are built from the visitor's own gated view, section by section,
never from this API (`VisitorSnapshotBuilder`, fenced by `DiagnosticsApiIsNotServedTests`), and the compare opens the
visitor's own file only past that view's gate and keeps, logs and audits nothing of it; and an API call is not audited,
as host code is not a visitor: a call is event 9250, at Debug, codes and counts only.

*Page paths.* A finding's `StudioPath`, `GetStudioPath` and `PostgresPageLink.Path` are relative to the mount, have no leading slash and start
with the Postgres module's segment (`postgres/health`), so `$"{studioOptions.Path}/{link.Path}"` opens the page whichever
module list the host composed; the route and the scope query are the studio's own and may change between releases. The one exception is
`PostgresPage.Overview`, which is the home of the module that owns the database (issue #84): the engine reads it from
`IDiagnosticsSettings.DatabaseHomeRoute` (`postgres`, its own answer; the Marten module answers `marten`), so the path is the page a visitor
ends on and not the Postgres module's address, which a Marten host redirects.

*Stable codes.* A code is a letter and two digits, a string, never an enum, never reused, renamed or moved to another
area (hard rule 7). A finding's variant (`deadlocks`, `temp-files`) is frozen the same way within its code, and a check
with variants names one on every finding. Severity, category, outcome, action kind, platform and table ownership are
enums with explicit values, and may gain members.

*Extend-only* (hard rule 7). Result types are sealed, non-positional records with `init` properties and no `required`
member, so a host can build one in its tests and a new property breaks nothing. A new section is a new nullable property
and request flag; the three interfaces gain members only as default interface members. The JSON snapshot's
`schemaVersion` changes only for a removal, a rename or a change of meaning; a snapshot's producer is `Data Studio {release}`, and a
snapshot a 1.x release wrote still compares, with a notice.

*Accepted checks* (issue #35). `PostgresFinding.Accepted` and `AcceptedReason` are the API's half of D29's accepted
checks: the finding is returned either way, so a host's summary leaves out `Accepted` ones without a code list of its own.

*Cost and DDL.* An API nobody calls reads nothing. Calls share the pages' sweep and section caches, so polling every few
seconds costs one sweep per interval per database; fresh reads that calls start run at most two at a time per process,
and a cache hit waits behind nothing. Listing never enumerates a tenancy that builds: Marten's databases are the held
ones (D27, hard rule 14). A host's database source must list without creating anything, and its names (D30's identities)
survive a database moving server. Per-database state is kept for at most 64 databases, least recently used forgotten
first (`HealthDatabaseBound`).

## D33

**D33 — The Connection page shows a data source's settings one by one, never its connection string.** Database ›
Connection (both database modules) answers "how does this application actually connect, and what would I change". The
question is answered from a connection string, and a connection string is where passwords live, so the page's design is
that the string never reaches it.

*The catalogue decides what is shown.* `NpgsqlSettingsCatalog` lists the keywords of the Npgsql that is loaded (read from
`NpgsqlConnectionStringBuilder`, so a newer Npgsql's keywords appear without a release of ours), each with a category, a
type, Npgsql's default and an exposure (`NpgsqlSettingExposure`: shown as it is, a `Path`, `Options`, or `Secret`). `NpgsqlSettingsReader` parses the data source's
string with Npgsql's own builder, walks the catalogue, and emits one redacted record per keyword: its value in effect, where
it came from (the connection string, the environment variable that Npgsql major falls back to, or its default; explicit
versus default is `ShouldSerialize` on the canonical keyword, never `ContainsKey`) and the default.

*What is withheld.*
- A `Secret` keyword (`Password`, `SSL Password`, and whatever the catalogue marks) shows only "set" or "not set", resolved
  through Npgsql's own canonicalisation, never an alias list of ours.
- A keyword the catalogue does not know has its value withheld: the page says it is present and not what it holds.
- `PG*` environment values are shown on the same terms as their keyword (`PGUSER` as `Username`), except `PGPASSWORD`,
  which is only ever "set".
- An `Options` value is shown only when it sets real server settings (`-c name=value` for a name PostgreSQL knows); any other
  text is withheld, since it is free text the studio cannot judge.
- Paths (`Root Certificate`, `SSL Certificate`, `Passfile`) are shown, and the studio never reads a file: it names the path
  and says nothing of what is in it.
- The raw string, and the meter's pool names (which Npgsql builds from the connection string), never leave the reader.
  C18 counts pools and keeps only counts.

*Held by tests, not by care.* `ConnectionSentinelTests`, `ConnectionChecksSentinelTests`,
`ConnectionSnapshotAndSentinelTests` and the Marten section's sentinel test in `MartenConnectionInsightTests` run a data
source whose every secret, path, host and option is a sentinel string through the reader, the page builder, the checks, the
contributors, the snapshot, the log and the audit, and fail if a sentinel appears anywhere. `NamedPoolSiblingsTests` (#56) does the same for C18's identification of named pools: sentinel pool names, a sibling's
Application Name, host, database, login and password reach none of the finding, the export, the diagnostics snapshot or
the logs, and the host, database and login reach only the facts' own settings; a pool name that is not a connection string
is identified against the names of the data sources the studio resolves, inside the same reader, and what is not
identified is counted and said as not judged. A new keyword, row or card that could carry a value is a change to the catalogue and the sentinel set together.

*The gate.* `Database` (D29): the source policy asked with `TenantId = null`, before anything is read. The sessions part
(this application's sessions by application name, and the pool peak counted by application name) is `Activity`
(`ViewDatabaseActivity`). The page reads and changes nothing and is not audited; paste-to-compare is the same gate, removes
every secret before the text is read, and keeps, logs, audits and connects to nothing (D29, health-engine.md).

*What it cannot see* is said on the page rather than guessed: settings made in code on the `NpgsqlDataSourceBuilder`, a
`NpgsqlDataSource` built by a host's own factory, and the default password file.

## D49

**D49 — One product, one wording, no 1.x.** 2.0 decided what no earlier number covers, and each of these is a rule for the next change:

- **One product and one wording.** The product is Data Studio; a module is Marten, Postgres or Kafka. There is no `StudioProduct` that picks
  between two sets of sentences, and no sentence, title, credit, tile or heading says "Marten Studio", "Postgres Studio" or "KStudio"
  (`ProductNamesTests` holds the Marten and Postgres modules' words and every string literal and Razor text of `src/`, the Kafka module's own
  suite its words, with an allow-list of the few literals that name a 1.x thing on purpose). A module says what only it knows in its own words, and the nouns a module's source kind needs ("Marten
  store", "data source") stay descriptor data. The words of each module are pinned in a baseline of its own, so a change of wording is read as a
  diff and never by accident: `WordsTests_DataStudioMarten`, `…Postgres`, `…PostgresDiagnostics` (the engine alone) and `…Kafka`
  (`WordsTests` in each suite).
- **One log template per event.** `StudioLog` and `PostgresStudioLog`, which kept two templates for one id so that 0.4's words never
  moved, are one class: each id has one template, says "Data Studio" where it names the product and calls a Marten store or a plain data
  source a "source". **An event id is a promise** (`docs/stability.md`), and the ids, the levels and the property names did not move, with three exceptions
  recorded where they happened: 9230 gained three option-name properties, fourteen Debug lines lost their `{Product}` property, and a Postgres
  source's events carry `{TenantId}` = `(all)`. A template's words are not a promise (`docs/stability.md`: match on ids, never on text). The scans that keep it so are `LogEventConventionsTests`
  (the Marten module's events, and that no template of a module, the Postgres module or the engine names a removed product),
  `PostgresLogEventConventionsTests`, `ShellLogEventConventionsTests`, `KafkaLogEventConventionsTests` and `EngineLogEventsTests` over one
  `LogEventScan`; log categories are under `DataStudio.*` (`DataStudio.Kafka.Core.Scans`). The studio's own database sessions carry the
  application-name suffix "Data Studio" ("Data Studio Diagnostics" for the engine alone), a health snapshot's producer is
  "Data Studio {release}", and the `ms-` CSS prefix, the `martenStudio` script global, the `ms_theme` and `ms_tz` cookies and the mute keys'
  storage key are internal and kept, so a saved theme survives.
- **Module order is registration order.** The rail, the anonymous banner, the capability chip, the credit and the front page's tiles list the
  modules in the order the host wrote its `Add…()` calls on the builder; nothing about a module decides where it goes, and what is refused
  never depends on the order (D30, D34).
- **No compatibility with 1.x.** No old package id, registration call, option name, address, log category or application name is kept, shimmed
  or forwarded: `AddMartenStudio()` does not exist, `{mount}/documents` is a 404, and a 1.x name sits beside its 2.0 one only in
  `docs/upgrading.md`, `docs/maintainers/v2-public-api.md` (the record of the renames), the CHANGELOG's old sections and dated
  history. A breaking change is allowed whenever it is the better design, and it is said in the CHANGELOG and, for the
  surface, in `docs/maintainers/v2-public-api.md`.
