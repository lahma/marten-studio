# The diagnostics API

From 0.5, the studio's findings and measurements are available to your own code: `IPostgresDiagnostics`, registered by
`AddPostgresStudio()` and by `AddMartenStudio()` - and, from 0.11, by `AddPostgresDiagnostics()` alone, with no studio and
no ASP.NET Core ([Without the UI](#without-the-ui)). It answers what the Health, Storage, Statements, Server and
Database › Sessions pages read, for one database at a time, as typed results with stable codes - so you can put a
summary in your own e-mail, your own admin page or your own alerting, worded in your own language.

It is **read-only** and **never served by the studio**: nothing in either package puts a result into an HTTP response,
a log line above Debug, a metric, the Activity screen or the opt-in health check. Where a result goes is your code's
decision.

## Getting it

`IPostgresDiagnostics` is a singleton in the container. Resolve it wherever your code runs: a hosted service, an endpoint
behind your own authorization, a background job. You do not need to map the studio (`MapPostgresStudio()` /
`MapMartenStudio()`) to use it - an application can register the studio for the API alone - and a host with no UI at all
registers `AddPostgresDiagnostics()` instead.

```csharp
// Postgres Studio: an admin database and one database per tenant.
builder.Services.AddSingleton<TenantDatabases>();          // your own IPostgresDatabaseSource
builder.Services.AddPostgresStudio(o =>
{
    o.IncludeDefaultDataSource = false;
    o.AddDataSource("admin", sp => sp.GetRequiredService<AdminDatabase>().DataSource);
    o.AddDatabaseSource("tenants", sp => sp.GetRequiredService<TenantDatabases>());
});

// A daily summary, in your own wording.
await foreach (PostgresDatabaseRef db in diagnostics.ListDatabasesAsync(ct))
{
    PostgresDiagnosticsSnapshot s = await diagnostics.GetSnapshotAsync(
        db, PostgresDiagnosticsRequest.FindingsOnly with { Storage = true }, ct);

    if (s.Outcome != PostgresReadOutcome.Read)
    {
        mail.Unreachable(db, s.Reason);
        continue;
    }

    foreach (PostgresFinding f in s.Findings.Where(f => f.Severity <= PostgresSeverity.Warning))
    {
        mail.Add(db, wording.For(f.Code, f.Variant) ?? f.Title, f.Subject, f.Action);
    }
}
```

```csharp
// Marten Studio: the same service over the store's databases.
builder.Services.AddMartenStudio(o => { /* as before */ });

PostgresDiagnosticsSnapshot s = await diagnostics.GetSnapshotAsync(
    new PostgresDatabaseRef("default", ""),        // the store's key; "" is its default database
    new PostgresDiagnosticsRequest { Connections = true, IncludeActivity = true },
    ct);
// s.Connections.ByApplication: sessions per application; s.Compute: the capacity your IPostgresCapacitySource reports.
```

## The three members

- **`ListDatabasesAsync`** yields the databases the studio would list, in its order: each data source and database
  source in registration order (the unkeyed data source first) and, in Marten Studio, each store's databases its
  tenancy already holds. Listing creates nothing and builds no data source; a Marten tenancy that builds its databases
  on demand is never asked to (hard rule 14, D27). A source that cannot be listed is left out and logged.
- **`GetSnapshotAsync(database, request)`** returns one database's `PostgresDiagnosticsSnapshot`. It **never throws** for
  a database that is unknown, unreachable or failing: its `Outcome` says so (`NotFound`, `Failed`) with a `Reason` in
  the studio's words. It throws only for a null argument, for cancellation, and for studio options that fail
  validation. A source name is matched without regard to case; the snapshot names the database by the source's own key
  and the database's own identity.
- **`Checks`** lists every check this studio runs, with no outcome: the code vocabulary, for your own test that you have
  wording for every code and variant.

## What a snapshot holds

| Property | What it is | Asked with |
|---|---|---|
| `Outcome`, `Reason` | whether the database was read at all | always |
| `Server` | which server answered: version, platform, instance, whether it is in recovery, when it started | always |
| `Compute` | the compute capacity an `IPostgresCapacitySource` you register reports (Aurora Serverless v2's ACU, say) | always |
| `Findings`, `Checks`, `FindingsMeasuredAt`, `FindingsWithheld` | the advisor's findings and every check's outcome | `Findings` (on by default) |
| `Storage` | the database's size, its growth per day, and its largest tables, each with its all-visible ratio | `Storage` |
| `Statements` | the most expensive statements over the statement checks' window | `Statements` |
| `Settings` | the curated server settings and the extensions, and whether data checksums are on and what they have counted | `Settings` |
| `Connections` | sessions by state for the database, the server's connection budget, and sessions per application | `Connections` |
| `Wal` | the write-ahead log's records, full-page images, bytes, writes and syncs (and their times when the server times them) since the statistics last began | `Wal` |
| `Withheld` | each section or part left out, keyed by its name, with what it would take | always |

`PostgresDiagnosticsRequest.FindingsOnly` asks for the findings alone; `PostgresDiagnosticsRequest.AllSections` for every
section. A section not asked for is `null`.

### A measurement baseline

A baseline taken at a fixed point - a week after a PostgreSQL upgrade, say - reads the numbers a decision is made from:
the statements and their window (`Statements.StatisticsSince`, `Statements.Window`), the size and growth (`Storage`),
the settings and the connection budget (`Settings`, `Connections`), and three more that are cumulative counts of the
server's own, never Activity-grade and so never withheld:

- **`Wal`** is `pg_stat_wal`: `Records`, `FullPageImages` and `Bytes` since `StatsReset`, and the server's WAL writes and
  syncs (below). WAL volume is what a point-in-time-recovery window has to keep; two snapshots' `Bytes` over the time
  between their `MeasuredAt` is a rate, while `StatsReset` is the same in both. It is the Replication page's read, shared
  with it: on Amazon Aurora, whose storage layer writes no WAL of yours, `Outcome` is `NotApplicable` with the reason.
  - **`Writes`** and **`Syncs`** are the server's WAL writes and `fsync`s since the reset, and mean the same on every
    release, so they compare across an upgrade. On PostgreSQL 14 to 17 they are `pg_stat_wal`'s `wal_write` and
    `wal_sync`; on 18, which moved them, they are `pg_stat_io`'s rows with `object = 'wal'`, summed over every backend
    type that writes WAL (`writes`, `fsyncs`). Only the `normal` context is summed: 18's `pg_stat_io` also counts the
    creation of a WAL segment, in context `init`, which `pg_stat_wal` never counted, and including it would make a
    server's syncs jump on upgrading without the server doing anything different. On a server committing synchronously
    both follow its commits, a commit costing about one of each (measured on 14, 17, 18 and 19), and with `fsync = off`
    the syncs stay at nought.
  - **`WriteTimeMs`** and **`SyncTimeMs`** are the time those took, in milliseconds. They are `null` unless
    `track_wal_io_timing` is on - the one setting that times WAL I/O on every release. On 18 and later it still is, and
    `track_io_timing` does not: it times a relation's I/O and no WAL row (measured on 18.6 and 19rc1). The server counts a
    zero while timing is off, which is no time, so the section says `null`, and a time is "since the reset" only if the
    setting was on the whole while.
  - On 18 and later the writes and syncs come from `pg_stat_io`, which is reset on its own (`pg_stat_reset_shared('io')`),
    so `StatsReset` there is the later of its reset and `pg_stat_wal`'s: a counter is comparable between two snapshots
    exactly while `StatsReset` is the same in both. The Replication page's "Writes and syncs" line shows the same numbers.
- **`Settings.DataChecksums`** says whether the server checks page checksums, and **`Settings.ChecksumFailures`** with
  **`LastChecksumFailure`** what this database has counted (`pg_stat_database.checksum_failures`): null while checksums are
  off, where the server counts none, and any number above zero is corruption. Checksums are chosen when a cluster is made,
  on by default from PostgreSQL 18.
- **`Storage.LargestTables[].AllVisibleRatio`** is `relallvisible / relpages`, 0 to 1: how much of the table an index-only
  scan can answer without touching the heap, as of its last `VACUUM` or `ANALYZE`. It decides whether a covering index
  would pay on a large append-mostly table. Null while the catalog counts no pages for it.

The studio's own download carries the checksum fields and the ratio too, as the visitor's Server and Storage pages hold
them, and `Wal`, as the Replication page holds it: so a baseline needs no code of yours, only **Health › Snapshot ›
Download JSON**. It is read at that page's gate, the store policy over the database as a whole; a visitor short of it gets
a `withheld` entry for `wal` with the page's own sentence, and on Amazon Aurora, or a release without the view, the
section is `notApplicable` with its reason.

A section that could not be read is still there, with its own `Outcome` and `Reason`: a privilege the role lacks or a
statement timeout is `Unavailable` or `TimedOut`, anything else `Failed` with the SQLSTATE. The rest of the snapshot is
answered all the same. When the database cannot be reached at all - every check that reads the catalog failed to - the
snapshot's own `Outcome` is `Failed`, and each section asked for is there as `Failed` without being read, so a database
that is down costs one failed sweep, not one failed connection per section.

### Findings and codes

A finding has a stable `Code` (`T03`), a `Variant` when its check finds more than one kind of thing (`index`,
`partitions`), and a `Subject` when it is about one object (`public.mt_doc_order_idx_total`). Its `Key` is
`Code|Variant|Subject`, with the parts it has. Every code, its variants and what its subject names are listed in
[health/codes.md](health/codes.md), which is the contract: a code is never reused or renamed, and a newer studio may
return a code you have no wording for - fall back to `Title`, which is always set.

`Accepted` is true when the application accepted the finding's check (`AcceptedChecks` in the studio's options,
[health.md](health.md#accepting-a-check)), and `AcceptedReason` is the application's own reason. The finding is still
returned, so a snapshot shows the whole picture, but the studio counts it nowhere. A summary of your own - an e-mail, a
support page - leaves out the findings where `Accepted` is true, and needs no list of codes of its own to do it. Every
check's `Findings` count still includes them. Acceptance changes no gate: a finding `IncludeActivity` withholds is still
counted as withheld, and an accepted one is no exception.

`Action` is the remedy as text: a line for your `StoreOptions`, SQL for a migration, a server setting. `ActionKind` says
which. The studio never runs one. `ActionRequires` names what the remedy needs that this application lacks ("Marten
9.47+"), when it does.

#### A line per check

A list of thirty redundant indexes should read as one line, "Redundant indexes (30)", not thirty. `FindingsByCheck()` on
the snapshot groups the findings the way the Health page lists them, so a summary of your own needs no grouping code and
stays in step as checks are added or renamed:

```csharp
foreach (PostgresFindingGroup group in snapshot.FindingsByCheck())
{
    // group.Count > 1: "Redundant indexes (30)"; group.Count == 1: the finding's own title, group.Subject and group.Detail.
    Console.WriteLine($"{group.Severity}  {group.Title}{(group.Count > 1 ? $" ({group.Count})" : "")}");
}
```

A `PostgresFindingGroup` is every unaccepted finding of one check (`Code`):

- `Severity` is the worst in the group, and `Category` the check's area.
- `Count` is how many findings it holds, and `Findings` they are, in the snapshot's order.
- With one finding (`Count == 1`) the group carries that finding's own `Title`, `Subject` and `Detail`, and `StudioPath`
  is the finding's card on the Health page. With more, `Title` is the check's title from `Checks`, `Subject` and `Detail`
  are `null`, and `StudioPath` is the database's Health page (what `GetStudioPath(database, PostgresStudioPage.Health)`
  returns), since no one card is the group's.
- The groups are ordered by severity, worst first, and then by the order of the snapshot's `Checks`; a code `Checks` does
  not list comes last, by code.
- **Accepted findings are left out**, as they are of every count the studio shows, so a check whose findings were all
  accepted has no group. `Findings` on the snapshot is unchanged and still holds them.
- A snapshot that was not read (`Outcome` is not `Read`) or has no findings returns an empty list.

`FindingsByCheck()` is a method, not a property: the snapshot's JSON is exactly what it was, and the groups are computed
from `Findings` each time you call it. `StudioPath` is relative to the studio's mount path, as every path in this API is.

#### The only database

A host with a single database need not list them first. `GetSnapshotAsync(request)` (without a database) reads the one
database `ListDatabasesAsync` lists and returns its snapshot. It throws an `InvalidOperationException` when the studio
lists none or more than one, naming `ListDatabasesAsync` and `GetSnapshotAsync(database, request)` as the way to say which
you mean; it asks the listing for at most two and stops there. It is a default interface member, so a test double of
`IPostgresDiagnostics` keeps compiling and gets it for free.

## `IncludeActivity`

Some of what the studio reads is about other programs' work, not the database as a whole: statement texts, application
names, other sessions' process ids and roles, and findings about one tenant. On the studio's own pages that is behind
`ViewDatabaseActivity` and the write policy (D29's Activity gate). Here it is behind one switch per call,
`PostgresDiagnosticsRequest.IncludeActivity`, **off by default**:

- the `Statements` section is `null`, and `Withheld["statements"]` says why;
- `Connections.ByApplication` is empty - the database's totals and the server's budget are always there - and
  `Withheld["connections.byApplication"]` says why;
- a finding of a check that reads other programs' statements (T02, T22, T23 - `PostgresCheck.RequiresActivity`) or names
  other sessions' blockers, applications, roles and addresses (C12, C13, C14) and a finding about one tenant are counted in
  `FindingsWithheld` and never described, and such a check's `Outcome` is `Withheld`;
- a piece of evidence about another session keeps its label and reads
  `withheld - Activity-grade: call with IncludeActivity = true`.

The default is the safe one: the call somebody writes without reading this page must not be the one that puts
statement texts in an e-mail.

## Linking to the studio

Each finding carries `StudioPath`, its card on the Health page, and `StudioLinks`, the screens that help with it;
`GetStudioPath(database, page)` gives Health, Sessions, its blocking view, Statements, Maintenance, Storage or Server for
a database. Each is relative to where you mapped the studio, with the database's scope in its query, so
`$"{options.Path}/{finding.StudioPath}"` opens it. Take them from here rather than building routes: the studio's routes
and query parameters are its own and may change between releases. A link opens the page for whoever follows it, under
that visitor's own gates; it carries nothing the page would not show them. A finding's `StudioPath` ends in its card's
fragment: the Health page scrolls to the card once it has drawn it, opens the muted list if the card is in it, and marks
the card for two seconds. A finding the report no longer holds, or one the visitor is not shown, reads "This finding is
no longer reported."

## Without the UI

`PostgresStudio.Diagnostics` is the engine under both studios as a package of its own, with no ASP.NET Core in it: the
health sweep and its checks, the facts they read, `IPostgresDiagnostics` and its read model, and the opt-in health check.
Take it when the host has no UI to mount and only wants the findings:

- a **worker or job host** that mails or posts a daily summary, as above, or runs the checks before a deployment;
- a **shared library** of your own that other applications call to ask "is this database healthy?", without the library
  pulling a Blazor UI into every one of them;
- an application that wants the **health check** (`AddHealthChecks().AddPostgresStudio()`) and no pages.

```shell
dotnet add package PostgresStudio.Diagnostics
```

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNpgsqlDataSource(connectionString);      // the unkeyed source is found by itself, as "default"
builder.Services.AddPostgresDiagnostics(o =>
{
    o.HealthInterval = TimeSpan.FromMinutes(5);
    o.BrowsableSchemas.Add("public");
    o.AddDatabaseSource("tenants", sp => sp.GetRequiredService<TenantDatabases>());
});
builder.Services.AddHostedService<DailySummary>();           // resolves IPostgresDiagnostics, as in "Getting it"

await builder.Build().RunAsync();
```

[`samples/PostgresStudio.Worker`](../samples/PostgresStudio.Worker) is this, complete: a generic host that references
`PostgresStudio.Diagnostics` alone and logs a summary of every database at start and then every `Summary:Interval`
(24 hours by default), a line per check (`FindingsByCheck()`) with its link when `Summary:StudioBaseUrl` is set. A real host sends the text where it
already notifies - e-mail, a chat webhook - and keeps the loop.

Code compiled against PostgresStudio 0.10 that uses the types that moved to this package - `IPostgresDiagnostics` and
its read model, `PostgresDatabaseRef`, `IPostgresDatabaseSource`, `IPostgresCapacitySource` and the health-check
extension - must be recompiled against 0.11: they keep their namespace and their names, so it is source-compatible, but
the types are in a new assembly and there is no type forwarding, so it is not binary-compatible.

**What differs from the studio's registration:**

- **No mutes.** Muting a finding is a visitor's choice on the Health page, and there is no visitor. What the host decides
  for everyone is `AcceptedChecks`, which `PostgresDiagnosticsOptions` has and the snapshot reports as `accepted`.
- **Links are mount-relative strings, meaningless without a UI.** `StudioPath`, `StudioLinks` and `GetStudioPath` are the
  same strings as ever (`health?store=default&db=app`) and are only useful joined to the path a studio is mounted at -
  yours, if a separate application hosts one. With nothing mounted anywhere, ignore them.
- **Options are validated on start** (`ValidateOnStart`): a host that registers `AddPostgresDiagnostics()` with a
  `QueryTimeout`, `SqlConsoleRole`, `HealthInterval`, schema name or source name the engine refuses stops at startup, naming
  the member, not at the first read. The studio validates the same members, and its own besides.
- **No K-checks.** K05 to K07 judge the studio's own configuration and live in `PostgresStudio`, so they never run under
  `AddPostgresDiagnostics()` alone, and naming one in `AcceptedChecks` there stops the host at startup.
- **One registration.** `AddPostgresDiagnostics()` throws in a container that already called `AddPostgresStudio()` or
  `AddMartenStudio()`, and they throw after it: the studios register the engine themselves, from the same options object.
  An application that wants a studio calls the studio's method alone.
- **No studio-only options.** `PostgresDiagnosticsOptions` has `QueryTimeout`, `MaxStudioConnections`, `StudioApplicationName` (whose
  derived name ends with `(Postgres Studio Diagnostics)` on a host with no studio), `SqlConsoleRole`, `HealthInterval`,
  `AcceptedChecks`, `BrowsableSchemas`, `IncludeDefaultDataSource`, `AddDataSource` and `AddDatabaseSource`. The path, the
  title, the three authorization policies, `ReadOnly`, the capabilities, paging, the console's limits and `RefreshInterval`
  belong to `PostgresStudioOptions`, which inherits all of the above: configure a member the same way in either.

**The health check** is the same call as with a studio, and needs `AddPostgresDiagnostics()` (or a studio) registered
first; without either it reports `Degraded` and says so:

```csharp
builder.Services.AddHealthChecks().AddPostgresStudio(tags: ["postgres"]);
```

It reports codes, outcomes, severities and counts, never a name, as in the studio's ([Security](#security)).

## Connection history

Database › Sessions › Holders shows who holds the server's connections *now*. The question after an outage is who held them
*then*. `AddPostgresConnectionHistory()` (1.0, issue #43) keeps the answer: a background service samples each server the studio
reads, every `Interval`, and appends what it counted to a store.

```csharp
builder.Services.AddPostgresStudio();              // or AddMartenStudio(), or AddPostgresDiagnostics()
builder.Services.AddPostgresConnectionHistory(o =>
{
    o.Interval = TimeSpan.FromMinutes(1);          // 15 seconds to 1 hour; a minute by default
    o.Retention = TimeSpan.FromDays(7);            // what the in-memory store keeps, and how far back Holders reads; 1 hour to 90 days
    o.MaxApplications = 20;                        // applications a sample names, most connections first; 1 to 100
    o.ShouldSample = ct => leader.IsLeaderAsync(ct); // optional: only the process that answers yes samples
});
```

**What a sample is.** A `PostgresConnectionSample`: when, the server (`host:port`, as the connection string names it - never a
database, a user or a password), which instance answered (its address and start time, so a failover or a restart shows as a
break), the server's client sessions less the sampler's own, the client limit C03 judges against (`max_connections` less the
reserved slots), and the applications with the most sessions, each with its active, idle, idle-in-transaction and unknown
counts. The read is the one Holders makes, in a read-only transaction under `QueryTimeout` and `SqlConsoleRole`: a role
without `pg_read_all_stats` counts what it cannot see into as state unknown.

**Where it is kept.** By default in memory, for `Retention`, at most 20,160 samples a server (two weeks at a minute; the
oldest go first), and gone after a restart. To keep it, register your own `IPostgresConnectionHistoryStore` - before or after
`AddPostgresConnectionHistory()`; yours is the one resolved. It has two members, `AppendAsync` and `ReadAsync(server, since)`;
a table of your own, a time-series database or a log sink all fit. The sampler calls `AppendAsync` once per server per
interval and never reads. The studio's pages call `ReadAsync` at most once every 30 seconds a server, however many visitors
have Database › Sessions open, and the samples are shared between them (the gate is applied to each visitor afterwards).
Each call is cut off at the studio's `QueryTimeout`: a store that hangs leaves Holders without its history for that poll,
and a round without its sample, and slows neither.

**Which servers.** Every server a registered source names, through the first of its databases with a data source - each once,
however many databases it holds, and at most 50. The list is read again every ten minutes. A source whose databases are
dynamic (a host database source of tenant databases, a Marten tenancy that builds its databases) is never enumerated, because
enumerating such a tenancy is not read-only; its servers are sampled only where another source names them.

**The kept connection.** A sampler that opened a connection to sample would count nothing at the one moment that matters:
when the server refuses new ones. So each server gets one connection from the application's own data source, opened once
and kept, named like the studio's own sessions with "history" after the product (`orders-api (Postgres Studio history)`, or
your `StudioApplicationName` with " (history)" after it) for as long as it is kept, idle between samples too, and given its
own name back before it is closed. It is **outside `MaxStudioConnections`**: count it in the application's pool size. Its
time is not the studio's on the Health page's cost line - a session kept for a week is not a week of the studio's work. A
connection that breaks is opened again at the next round; while `ShouldSample` answers no, every kept connection is closed.

**What a visitor sees.** On Holders, anybody the source policy passes sees the counts: the window, the peak against the client
limit and when, how many samples, and how many instance changes. The applications' own peaks need `ViewDatabaseActivity`, as
Holders' applications do. Without the registration, Holders says how to turn it on.

**Failures.** A round or a server that fails is logged as event 9460 or 9461, throttled like the studio's polled reads, and
tried again at the next interval; nothing a page shows fails with it.

## Security

**It answers your code, not a visitor.** No authorization policy, capability or `ReadOnly` is asked: those decide what
the studio's pages show to the people who open them. The API reads nothing your code could not read through the same
data source or store, as the same roles - `SqlConsoleRole` for every read but the statement checks', which run as the
store's own role fenced to `userid = current_user` (D29's first exception). Treat a result as you would the output of
`pg_stat_activity`: it names tables, indexes and settings, and with `IncludeActivity` statement texts and application
names.

**What it does not apply:** the per-visitor decisions - the store, source, write and authorization policies, the
capabilities and `ReadOnly` - and the database browser's masking of schemas withheld from a visitor (D27).

**What it always applies,** because they are about the content rather than the reader:

- a document type you hide with `IsDocumentTypeVisible` is never named, in a finding or a table or a statement;
- a statement text that still carries a literal, or is not a plain query or write, is withheld, and comments are
  masked;
- texts are capped;
- PostgreSQL's own error words are not carried - only the studio's sentence and the SQLSTATE; the server log has the
  rest.

**The studio never serves it.** No endpoint maps a result, no log line above Debug carries one (a call is event 9250, at
Debug, with codes and counts), and an API call is not audited: the Activity ring records what visitors did through the
studio, and your code is not a visitor. The opt-in health check stays codes and counts. `DiagnosticsApiIsNotServedTests`
holds the packages to this.

## Cost

An API nobody calls reads nothing. A call shares the pages' single-flight caches: the findings are at most one
`HealthInterval` old, and the costlier sweeps - the deeper checks and the statement checks - are read from a recent run
unless `RunDeeperChecks` asks for one. So an application polling every few seconds costs one sweep per interval per
database. Fresh reads started by API calls - a sweep or a section read the caches do not hold - run **at most two at a
time** in the process, so a loop over hundreds of databases costs two connections at a time. Cancelling a call stops its
wait at once; a read it already started finishes for the caches and keeps its place among the two until it does.

A snapshot never changes anything: no DDL, no settings, no `pg_stat_reset`. An `IPostgresDatabaseSource` must list its
databases without creating anything, and its names are identities: they key policies, history and mutes, and survive a
database moving server.

The studio keeps state - history rings, cached sweeps, statement texts - for at most about 64 databases at once,
forgetting the least recently used; a database read again starts its "watching for" window afresh.

## Snapshot JSON

`snapshot.ToJson()` writes the snapshot as indented JSON, the same shape the studio's own Snapshot menu downloads. It uses
no serializer options of yours or a global's.

- Property names and enum values are camelCase; times are ISO 8601 in UTC; durations are seconds, under a name ending in
  `Seconds`; byte counts are numbers; `queryId` is a string, because a 64-bit hash is not exact in JavaScript; a number
  that is not finite is `null`.
- **Every key is always present**, with `null` for "not asked, not allowed or not known".
- The snapshot's four findings properties are one `findings` object (`items`, `checks`, `measuredAt`, `withheld`), and a
  finding's `key` is written too, with `accepted` (a boolean) and `acceptedReason` (the application's text or `null`).
- `schema` is `postgresstudio-diagnostics`, so a file attached to a ticket says what it is.

Adding a key keeps the version. Removing or renaming one, changing its type or changing what it means bumps
`schemaVersion`. A reader should ignore keys it does not know. The studio writes only its current version.

| `schemaVersion` | Written by | What changed |
|---|---|---|
| 1 | 0.5.0 | The first version. 0.9 added keys, and kept the version: `wal`, `settings.dataChecksums`, `settings.checksumFailures`, `settings.lastChecksumFailure` and `storage.largestTables[].allVisibleRatio`. 0.10 added `findings.items[].accepted` and `findings.items[].acceptedReason`. |

## The Snapshot menu

The Health page has a **Snapshot** menu - View JSON, Copy JSON, Download JSON - that writes the same shape for the
visitor who uses it. It is **not** this API: it is built in the visitor's circuit from each page's own service as that
visitor, so it holds exactly what their screens hold, masked the same way, and a section they may not see is a
`withheld` entry carrying that page's own sentence. It starts no deeper or statement sweep the visitor did not start, and
nothing is stored: the JSON is handed to the browser and dropped. The file is named
`{studio}-snapshot-{source}-{database}-{yyyyMMddTHHmmssZ}.json`.

## Stability

The read model is results, not seams: there is no way to add a check or change a threshold through it. Its records are
sealed, with `init` properties and no `required` member, so you can build one in your own tests and a new property breaks
nothing. A new section is a new nullable property and a new request flag. The enums - severity, category, outcome,
action kind, platform, table ownership - have explicit values and may gain members. The three interfaces gain members
only as default interface members. `PublicApiShapeTests` holds the packages to this.
