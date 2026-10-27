# The diagnostics API

From 0.5, the studio's findings and measurements are available to your own code: `IPostgresDiagnostics`, registered by
`AddPostgresStudio()` and by `AddMartenStudio()`. It answers what the Health, Storage, Statements, Server and
Database › Sessions pages read, for one database at a time, as typed results with stable codes - so you can put a
summary in your own e-mail, your own admin page or your own alerting, worded in your own language.

It is **read-only** and **never served by the studio**: nothing in either package puts a result into an HTTP response,
a log line above Debug, a metric, the Activity screen or the opt-in health check. Where a result goes is your code's
decision.

## Getting it

`IPostgresDiagnostics` is a singleton in the container. Resolve it wherever your code runs: a hosted service, an endpoint
behind your own authorization, a background job. You do not need to map the studio (`MapPostgresStudio()` /
`MapMartenStudio()`) to use it - an application can register the studio for the API alone.

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
| `Storage` | the database's size, its growth per day, and its largest tables | `Storage` |
| `Statements` | the most expensive statements over the statement checks' window | `Statements` |
| `Settings` | the curated server settings and the extensions | `Settings` |
| `Connections` | sessions by state for the database, the server's connection budget, and sessions per application | `Connections` |
| `Withheld` | each section or part left out, keyed by its name, with what it would take | always |

`PostgresDiagnosticsRequest.FindingsOnly` asks for the findings alone; `PostgresDiagnosticsRequest.AllSections` for every
section. A section not asked for is `null`.

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

`Action` is the remedy as text: a line for your `StoreOptions`, SQL for a migration, a server setting. `ActionKind` says
which. The studio never runs one. `ActionRequires` names what the remedy needs that this application lacks ("Marten
9.47+"), when it does.

## `IncludeActivity`

Some of what the studio reads is about other programs' work, not the database as a whole: statement texts, application
names, other sessions' process ids and roles, and findings about one tenant. On the studio's own pages that is behind
`ViewDatabaseActivity` and the write policy (D29's Activity gate). Here it is behind one switch per call,
`PostgresDiagnosticsRequest.IncludeActivity`, **off by default**:

- the `Statements` section is `null`, and `Withheld["statements"]` says why;
- `Connections.ByApplication` is empty - the database's totals and the server's budget are always there - and
  `Withheld["connections.byApplication"]` says why;
- a finding of a check that reads other programs' statements (T02, T22, T23 - `PostgresCheck.RequiresActivity`) and a
  finding about one tenant are counted in `FindingsWithheld` and never described, and such a check's `Outcome` is
  `Withheld`;
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
that visitor's own gates; it carries nothing the page would not show them.

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
  finding's `key` is written too.
- `schema` is `postgresstudio-diagnostics`, so a file attached to a ticket says what it is.

Adding a key keeps the version. Removing or renaming one, changing its type or changing what it means bumps
`schemaVersion`. A reader should ignore keys it does not know. The studio writes only its current version.

| `schemaVersion` | Written by | What changed |
|---|---|---|
| 1 | 0.5.0 | The first version. |

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
