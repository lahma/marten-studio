# Architecture

This is the full text of D2, D10 to D12, D17, D18, D30 and D31: the two packages, routing and assets, the data seam, the samples and the diagnostics API, moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D2

**D2 — Interactive Server render mode only, and `IsTrimmable=false` said out loud.** Blazor Server is what
lets the studio run against the host's in-process `IDocumentStore`; WebAssembly would need an API surface
that is itself the security problem. `IsTrimmable=false` is stated in the csproj with a comment rather than
left to the default, because Blazor sets component `[Parameter]` properties by name and the document viewer
deserializes into types discovered from `IDocumentType.DocumentType` at run time — a trimmer told it may
cut has no way to see either.

## D10

**D10 — No SignalR hub in v1.** Pages poll with a `PeriodicTimer` at `RefreshInterval`, paused on hidden
tabs, through a single-flight snapshot cache so that N circuits cost one query per interval rather than N.
Where the daemon is hosted in this process, a lazy observer on `ShardStateTracker` gives sub-second
projection state without any of that. The convention builder already carries an optional hub slot for v2;
adding a hub now would add a host-visible endpoint for a feature that polling covers. The projections screen keeps its
own lag samples (`ProjectionLagHistory`), written only by its single-flight snapshot read: 120 per shard, 200 shards per
database, databases bounded by `HealthDatabaseBound` (M2). Dead letters are grouped in the web process from one read
per interval of the newest 20,000 letters' columns (`DeadLetterGroupQueries.BuildGroupRows`), and a group's letters are
found again in SQL by the same rule (`DeadLetterFingerprint`) - the list's only among those same newest letters
(`DeadLetterGroupCache.LettersGrouped`), the skip's count and the skip among all of a group's letters;
`DeadLetterGroupsLiveTests` holds the two equal (M3;
why: decision-notes.md, D10).

## D11

**D11 — Sub-path mounting is ported from Quartz.Dashboard.** Compile-time routes are re-rooted at map time, from
each assembly's own root (`/postgres` for PostgresStudio's pages, `/marten` for MartenStudio's), and `/_blazor`,
`blazor.web.js`, `opaque-redirect` and `_content/PostgresStudio` - the shared assembly's assets, whichever studio
is mounted - are mirrored under the mount path (`StudioEndpointMapper`). Endpoint display names keep the
compile-time template, so no log or diagnostic text is ever built from one. This is ~250 lines of genuinely fiddly engineering that already works in production
somewhere; rewriting it would be the single most likely way to ship something that renders locally and
404s behind a path base. There is no "integrate into the host's own Blazor app" mode in v1.

## D12

**D12 — The data seam is internal, across both assemblies.** Per-area service interfaces (`IDocumentDataService`,
`IEventDataService`, …) with `InternalsVisibleTo` for the test projects - and, from PostgresStudio, for MartenStudio
(D30). Each package's *registration* surface is three static classes, two classes and one record, snapshot-tested;
PostgresStudio's also carries its inbound types and the D31 read model, which are results and hooks, never seams
(hard rule 7). Everything a consumer can bind to is everything we have to keep working.

## D17

**D17 — The demo domain lives in `samples/MartenStudio.SampleDomain` and does not reference
`MartenStudio`.** It is shared by the sample host and by the integration tests, which is the reason it is a
project rather than a folder. The missing reference is deliberate and structural: the studio must work
against a store that knows nothing about it, and a reference here would let that invariant rot without
anyone noticing. Since 0.5 the demo data with no Marten in it - the relational demo schemas and the tenant
databases - is `samples/PostgresStudio.SampleData` (Npgsql only), which `MartenStudio.SampleDomain` and the Postgres
Studio sample both reference; it references no studio either.

## D18

**D18 — The sample host starts a throwaway Postgres when no connection string is configured.** Zero-config
`dotnet run` is what makes the sample a demo rather than a setup exercise. It prints a loud banner saying
what it did, and it throws with guidance when Docker is absent — what it must never do is fall back to
`localhost` silently and appear to work against somebody's real database. It applies to both samples. The Postgres
Studio sample does the same with `ConnectionStrings:Postgres`, but starts a fresh container unless
`MARTENSTUDIO_PG_REUSE=true`, under a reuse identity of its own. The Marten sample judges a reused database before
Marten touches it (`SampleDatabase`): one another build of it seeded is dropped and seeded again on the throwaway
container or with `--allow-data-generation`, and on any other database the host stops with one line that says what
to do. Its throwaway container has a reuse identity of its own too, so `dotnet run` never attaches to the integration
suite's reused container.

## D30

**D30 — PostgresStudio: the core is a package of its own, and Marten Studio is built on it.** 0.5 splits the studio into
two Razor Class Libraries, so a host with Marten gets one copy of the core and a host with only Npgsql takes the core
alone, as the user asked. **PostgresStudio** is everything about a PostgreSQL database rather than a Marten store: the
introduction's list, plus the router and re-rooting (D11), the capability guard, the Statements, Maintenance, Storage,
Replication and Server pages, the diagnostics API (D31), the JSON toolkit, the stylesheet, the script and the shared
checks (D29). Its references are D3's, and hard rule 16 fences them.

*Why `InternalsVisibleTo` and an exact pin, not a public extension API.* Marten Studio extends nearly every core
service, and publishing those seams would multiply the public surface (hard rule 7) for a plug-in model nobody asked
for; with IVT the two packages are one program in two files, as the nuspec's `[x.y.z]` says (*Build*).
`AddMartenStudio()` first runs `PostgresStudioVersionGuard` (*Build*; evidence: decision-notes.md, D30), which names no
PostgresStudio type and loads the assembly by name; the public `AddMartenStudio`'s own body stays free of PostgresStudio
types (the rest is in a `NoInlining` method), and `PostgresStudioVersionGuardTests` scans its IL to keep it so.

*What PostgresStudio browses.* The application's own singleton `NpgsqlDataSource` registrations, never a connection
string (see the introduction): the unkeyed one by default (`IncludeDefaultDataSource`, the source `default`), a keyed or
otherwise-held one named with `AddDataSource`, and a host's own databases named with `AddDatabaseSource` and an
`IPostgresDatabaseSource` that lists names and hands back each one's data source on demand. A factory is called once,
with the root provider, and its result kept for the process and never disposed; a scoped or transient registration is
refused, as it would build a pool per visitor. A database's identity is its source's own (a Marten store database's
`Id.Identity`, unchanged; a DI-registered data source's registration name; the name a host lists); `?db=`, policies,
caches, history, mutes and the audit key on it, so it must be stable across processes and restarts.

*Authorization.* Postgres Studio is asked against its own `PostgresStudioResource(SourceName, DatabaseIdentifier,
Capability)`, which has no tenant and is deliberately unrelated to `MartenStoreResource`: a handler written for one must
never be handed the other. `SourceAuthorizationPolicy` is its store policy; D27 applies as *D27 in PostgresStudio* says.

*Routes and assets* follow hard rule 16 and D11 (the shared pages sit at `/marten/…` in Marten Studio); static assets
live only in `_content/PostgresStudio/`.

*Words.* Shared sentences come from `IStudioSettings.Text` (`StudioText`): MartenStudio's instance is 0.4's literals,
pinned by `StudioTextPinTests`, so no Marten Studio host sees a word change. Shared log events keep MartenStudio's
templates in `StudioLog`; `PostgresStudioLog` declares the same ids, levels and properties in Postgres Studio's words.

*One studio per process in 0.5.* `AddPostgresStudio` and `AddMartenStudio` refuse each other, and each `Map*Studio`
refuses the other's registration (what the seams leave room for: decision-notes.md, D30).

## D31

**D31 — The diagnostics API: what the studio found, for the host's own code, by stable code, and never served by the
studio.** History: decision-notes.md, D31. `IPostgresDiagnostics`, registered by `AddPostgresStudio()` and by
`AddMartenStudio()` (one per process, like the studio), lists the databases the studio would list (`ListDatabasesAsync`)
and returns each one's `PostgresDiagnosticsSnapshot` (`GetSnapshotAsync`): findings by stable code and variant, and
typed sections for storage, statements, settings and extensions, connections and the write-ahead log (`Wal`). `IPostgresDatabaseSource` (D30) and
`IPostgresCapacitySource`, found in DI to report compute capacity SQL cannot see (Aurora Serverless v2's ACU), point the
other way. MartenStudio's hosts reach it through the PostgresStudio assembly their exact pin names (hard rule 7).

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

*The studio never serves it.* Nothing in either package puts an API result into an HTTP response, a log line above
Debug, a metric, the Activity ring or the health check's data, and no endpoint maps one: where a result goes is the
host's decision. So the opt-in health check stays codes and counts (D29's second exception); the Health page's Snapshot
download is built from the visitor's own gated view, section by section, never from this API (`VisitorSnapshotBuilder`,
fenced by `DiagnosticsApiIsNotServedTests`); and an API call is not audited, as host code is not a visitor: a call is
event 9250, at Debug, codes and counts only.

*Stable codes.* A code is a letter and two digits, a string, never an enum, never reused, renamed or moved to another
area (hard rule 7). A finding's variant (`deadlocks`, `temp-files`) is frozen the same way within its code, and a check
with variants names one on every finding. Severity, category, outcome, action kind, platform and table ownership are
enums with explicit values, and may gain members.

*Extend-only* (hard rule 7). Result types are sealed, non-positional records with `init` properties and no `required`
member, so a host can build one in its tests and a new property breaks nothing. A new section is a new nullable property
and request flag; the three interfaces gain members only as default interface members. The JSON snapshot's
`schemaVersion` changes only for a removal, a rename or a change of meaning.

*Cost and DDL.* An API nobody calls reads nothing. Calls share the pages' sweep and section caches, so polling every few
seconds costs one sweep per interval per database; fresh reads that calls start run at most two at a time per process,
and a cache hit waits behind nothing. Listing never enumerates a tenancy that builds: Marten's databases are the held
ones (D27, hard rule 14). A host's database source must list without creating anything, and its names (D30's identities)
survive a database moving server. Per-database state is kept for at most 64 databases, least recently used forgotten
first (`HealthDatabaseBound`).
