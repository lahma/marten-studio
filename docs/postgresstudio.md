# Postgres Studio

Postgres Studio (the `PostgresStudio` package) is the studio for an ASP.NET Core application that keeps its data in
PostgreSQL through Npgsql, with or without Marten. It runs inside the application, against the `NpgsqlDataSource`s the
application registered, and shows what lives in those databases: tables, views, functions, triggers, sequences and
types and their rows, the sessions and locks on the server, the foreign keys, a health advisor, where the database's
time goes, autovacuum, storage, replication and the server's settings. Its package readme
([`src/PostgresStudio/README.md`](../src/PostgresStudio/README.md)) is the short form; this page is the long one.

It is also the core of Marten Studio. Since 0.5 Marten Studio is built on it: the shell, the authorization layers, the
audit, the database browser, Sessions, the health advisor and its pages, the SQL console and Relationships are Postgres
Studio's, and `MartenStudio` adds the Marten screens and checks. An application that uses Marten takes `MartenStudio`,
which brings this package at exactly its own version; everything below about the shared screens holds there too, with
the store in place of the data source.

## Install and register

```shell
dotnet add package PostgresStudio
```

The package references `Npgsql` (floor 8.0.3; tested at 8.0.3 and 10.0.3) and
`Microsoft.AspNetCore.App.Internal.Assets` (floor 10.0.12, which puts `_framework/blazor.web.js` in your application's
own static web assets), takes everything else from the shared framework, and references no Marten, JasperFx or Weasel
package. It targets `net10.0`. PostgreSQL 14 to 18 are supported ([postgres-versions.md](postgres-versions.md)).

```csharp
builder.Services.AddNpgsqlDataSource(connectionString);   // your application's own data source
builder.Services.AddPostgresStudio(options =>
{
    options.AuthorizationPolicy = "Admins";
});

// ...

app.MapPostgresStudio();                     // /postgres, or MapPostgresStudio("/ops/postgres")
```

A mapping that says nothing about authorization refuses to start, before the host binds a listener:

> Postgres Studio is mapped with no authorization. Its pages show the structure of every database this application
> registered and, with its capabilities on, can read their rows, run SQL against them and list other sessions'
> statements, so Postgres Studio refuses to start rather than serve that by accident. Say which you meant:
>
> - `app.MapPostgresStudio().RequireAuthorization()` authorizes its pages and its Blazor circuit;
> - `services.AddPostgresStudio(options => options.AuthorizationPolicy = "...")` authorizes those and the static assets
>   with them;
> - `app.MapPostgresStudio().AllowAnonymous()` serves it to anyone, deliberately.

A non-null `AuthorizationOptions.FallbackPolicy` satisfies the guard too. `AllowAnonymous()` is an answer, and is what
K07 then raises as a finding while any capability is on.

**One studio per application.** `AddPostgresStudio()` throws in an application that called `AddMartenStudio()`, and the
reverse; `MapPostgresStudio()` throws in one that registered Marten Studio, and says to call `MapMartenStudio()`.

**Hosting.** It works in an API-only host with no `.razor` file of its own, needs no `app.UseAntiforgery()` (it has no
forms; every action is a Blazor event over the circuit), and registers no global style or script: the stylesheet and
`js/studio.js` are served under `{Path}/_content/PostgresStudio/` and asked for by the studio's own pages alone.
`MapPostgresStudio("/ops/postgres")` moves the pages, the circuit, the assets and the framework script under that path;
behind a proxy that strips a prefix, give the application the same prefix with `app.UsePathBase`.

## Sources: what it browses

A **source** is something the application registered that names one or more databases. The scope picker shows them,
and the URL carries the choice (`?store=<source>&db=<database>`), so a pasted link reopens the same database.

- **`default`: the application's unkeyed `NpgsqlDataSource`.** Found by itself: what `AddNpgsqlDataSource(cs)` from
  Npgsql.DependencyInjection registers, or `AddSingleton(NpgsqlDataSource.Create(cs))`. The last unkeyed registration
  decides, because it is the one the container resolves, and only a singleton is listed: a scoped or transient data
  source would build a pool per visitor, so it is left out and logged once (event 9300, Information).
  `IncludeDefaultDataSource = false` leaves it out on purpose.
- **`AddDataSource(name, resolve)`: any other data source of the application's**, by name - a keyed one
  (`sp => sp.GetRequiredKeyedService<NpgsqlDataSource>("orders")`), or one an EF Core or Dapper host keeps elsewhere.
  Keyed data sources are never discovered by themselves.
- **`AddDatabaseSource(name, resolve)`: a set of databases the application lists itself**, one per tenant say, through
  an `IPostgresDatabaseSource` it registers:

```csharp
// Illustrative, not compiled.
public sealed class TenantDatabases(TenantDirectory directory) : IPostgresDatabaseSource
{
    public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (string tenant in await directory.TenantIdsAsync(cancellationToken))   // reads a table; creates nothing
        {
            yield return tenant;
        }
    }

    public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(directory.DataSourceFor(name));                           // your cached instance, or null
}

builder.Services.AddSingleton<TenantDatabases>();
builder.Services.AddPostgresStudio(o => o.AddDatabaseSource("tenants", sp => sp.GetRequiredService<TenantDatabases>()));
```

  The contract: `ListAsync` names the databases and must not create a database, run DDL or build a data source; the
  studio lists at most once a minute, and again when a visitor asks for a refresh. `GetDataSourceAsync` returns your own
  data source for one name, cached by you; the studio never disposes it or keeps it beyond one read, and asks for one
  only when somebody opens that database or a sweep reaches it. **Names are identities**: a name keys authorization
  (`PostgresStudioResource.DatabaseIdentifier`), the health history, mutes and caches, so it must stay the same for the
  same database, also when the database moves server. It must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$` and be unique,
  ignoring case, across every source's databases; a name that is not is left out and logged (event 9301).

Every `resolve` factory is called once, when the source is first used, with the application's **root** service
provider, and its result is kept for the life of the process; a data source or database source the container registers
as scoped or transient is refused, and the source is shown as unavailable, saying why. A source's key is a letter or
digit then up to 63 letters, digits, `.`, `_` or `-`, unique across sources, and never `default` for one you add.

There is no connection-string overload, on purpose. The studio reads through your data sources, with their role, pool,
TLS and settings, so it holds no credential of its own, and it renders what the application configured rather than
whatever somebody can type.

## Authorization

Three layers and two process-wide switches. The studio authenticates nobody: it asks your application's authorization
stack, about the visitor the circuit was opened for.

| | Option | Decides | Asked |
|---|---|---|---|
| 1 | `AuthorizationPolicy` | Who may open the studio at all | By the authorization middleware, on every studio endpoint |
| 2 | `SourceAuthorizationPolicy` | Which source and database | On every data call, resource-based against `PostgresStudioResource` |
| 3 | `WriteAuthorizationPolicy` | Each read authorized like a write | Before it, against the same resource with the capability named; falls back to layer 2 |
| - | `Capabilities` | Which of those reads exist at all | In the service layer, before layer 3 |
| - | `ReadOnly` | None of them | Before everything |

```csharp
public sealed record PostgresStudioResource(
    string SourceName,           // "default", or the name given to AddDataSource / AddDatabaseSource
    string DatabaseIdentifier,   // the database's identity in that source - never a connection string
    string? Capability);         // "BrowseDatabase", "ViewDatabaseActivity", "RunSql" - or null for a read
```

It has no tenant, and it is deliberately unrelated to Marten Studio's `MartenStoreResource`: a handler written for one
is never handed the other. One handler can answer every question:

```csharp
// Illustrative, not compiled.
public sealed class StudioRequirement : IAuthorizationRequirement;

public sealed class StudioHandler : AuthorizationHandler<StudioRequirement, PostgresStudioResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, StudioRequirement requirement, PostgresStudioResource resource)
    {
        // A tenant's support engineer sees that tenant's database, and an operator every one.
        bool maySee = context.User.IsInRole("ops")
            || (resource.SourceName == "tenants" && context.User.HasClaim("tenant", resource.DatabaseIdentifier));

        bool mayDo = resource.Capability switch
        {
            null => true,
            "RunSql" => context.User.IsInRole("dba"),
            _ => context.User.IsInRole("ops"),
        };

        if (maySee && mayDo)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
```

Listings are filtered rather than annotated: a database the source policy refuses is one the visitor never learns
exists. With no source policy configured, nothing is asked and everything passes, so an application that never set the
option is exactly the application it was.

## Capabilities

Four, all `false` by default, all turned on by `PostgresStudioCapabilities.All()`, all off under `ReadOnly`. Each is a
read that is authorized like a write, because each reads beyond structure:

- **`BrowseDatabase`** - the rows of the database browser's tables and views, and every definition in every schema
  `BrowsableSchemas` admits. It implies `ViewDefinitions`.
- **`ViewDefinitions`** - the definitions alone (a view's or materialized view's query, a function's or procedure's body,
  a trigger's definition, a sequence's value, a type's description) in the schemas `BrowsableSchemas` names **exactly**:
  for a host that keeps its rows off the screen and still wants the SQL its own migrations wrote. Never a row, and never
  a schema only `"*"` admits.
- **`ViewDatabaseActivity`** - other sessions' process ids, roles, client addresses, application names and statements,
  on Database › Sessions, the Health page and Operations › Statements. Without it the counts, ages and states are still
  shown.
- **`RunSql`** - the Query page's read-only SQL console.

Every disabled control names the property that would enable it (`PostgresStudioOptions.Capabilities.RunSql`), and the
refusal itself lives in the service layer and is audited.

## The database browser and its gate

**Database › Tables** lists what the visible schemas hold, by kind, and each object has a detail page: columns, keys and
indexes, foreign keys both ways, triggers, a view's query, its neighbourhood in the relationships diagram, and, for a
table, view or materialized view, a **Rows** tab with a filter and its index verdict, keyset paging and row detail that
follows foreign keys both ways.

`BrowsableSchemas` decides which schemas it shows:

- The schemas it names **exactly** are the data source's own. Their **structure** - names, columns, keys, indexes,
  trigger names, estimates - is shown to anyone the source policy admits. Their **definitions** need `ViewDefinitions` or
  `BrowseDatabase`, and their **rows** `BrowseDatabase`, each with the write policy asked with the capability's name.
- With nothing named, the list is `public`. The default is applied after every configuration, code and
  `appsettings.json` alike, so a list that names schemas **replaces** it: to keep `public` beside `legacy`, name both.
- `"*"` admits every schema the reading role has `USAGE` on, except the system schemas and the schemas an extension
  owns. A schema only `"*"` admits is shown - structure and definitions included - only with `BrowseDatabase` and the
  write policy, because `"*"` is a statement about the database rather than the configuration: `ViewDefinitions` never
  reaches it. Only an exact entry admits a schema an extension owns, and a system schema is never browsable.
- A schema the visitor may not see is masked as `‹withheld›` wherever Postgres prints its name - in types, defaults,
  CHECK and index definitions, view, routine and trigger definitions and comments - and a view that reaches into one is
  refused its rows.

Every list reads `pg_catalog` in a read-only transaction under `lock_timeout`, locks nothing it lists, and never calls a
deparse function that would queue behind a migration. Every first read of a relation's rows is audited (event 9235), and
an opened row is 9236.

**`BrowsableSchemas` limits what the screens show, not what can be read.** The SQL console reads whatever its role can,
and so do the browser's row reads: `SqlConsoleRole` is the real boundary for both. `"*"` with `BrowseDatabase` and no
`SqlConsoleRole` is logged at startup (event 9230, Warning) and is health check K06.

**A Marten table in a plain database.** Postgres Studio has no Marten, so it knows no store's declarations: anything
named like Marten's own (`mt_`) is listed as Marten's, its rows are never read, nor a view over one, and its definition
is shown only with `BrowseDatabase`, which `ViewDefinitions` does not stand in for. The database page says once that the database holds a Marten store, and points to
Marten Studio, where that store's tenancy, soft deletes and `IsDocumentTypeVisible` apply - Postgres Studio cannot
honour another application's visibility rules.

## The pages

| Section | Page | What it shows | Gate |
|---|---|---|---|
| Data | Overview | The server, the health glance, the largest tables, connections by state, recent studio activity | Source policy |
| Data | Query | The SQL console | `RunSql` and the write policy |
| Database | Tables | The database browser (above) | Structure: source policy; definitions: `ViewDefinitions` or `BrowseDatabase`; rows: `BrowseDatabase` |
| Database | Sessions | Sessions, locks and prepared transactions, sessions by application against the connection budget | Counts: source policy; who: `ViewDatabaseActivity` |
| Database | Relationships | The foreign keys as a diagram and a table, drawn on the server | Source policy; far ends per the browser's gate |
| Operations | Health | The health advisor (below) | Per finding (below) |
| Operations | Statements | The top statements by execution time, grouped by verb and table | `ViewDatabaseActivity` and the write policy |
| Operations | Maintenance | Each table against autovacuum's own triggers, maintenance running now, the workers, the freeze forecast, the vacuum horizon, and an on-demand bloat measure | Source policy; names per the browser's gate |
| Operations | Storage | Size, growth and the largest tables, from the catalog's page counts | Source policy; names per the browser's gate |
| Operations | Replication | Slots, standbys, the archiver, WAL, checkpoints, I/O by backend type, tablespaces | Numbers: source policy; names: `ViewDatabaseActivity` |
| System | Server | Version, platform, what the reading role can see, settings, extensions, the security posture | Source policy |
| System | Activity | The last 500 actions taken through the studio | Per entry, as its capability |

Every database-wide page asks the source policy about the database as a whole. A page polls at `RefreshInterval` while
its tab is visible, through caches shared by every circuit, so ten open tabs cost what one does.

## The health advisor

The Health page (`/postgres/health`, long form in [health.md](health.md), every code in
[health/codes.md](health/codes.md)) runs the shared checks: 44 of the advisor's 73 in 0.5, the ones that read Postgres
alone. The `D` and `E` checks and the Marten-only `C`, `K` and `T` ones run only in Marten Studio.

- **What a sweep judges.** Every ordinary and partitioned table of the schemas `BrowsableSchemas` admits, up to the 2,000
  largest by estimated size; every table check says what it left out, in counts. A table is the application's own, a
  recognised library's (Quartz.NET, Wolverine, EF Core's and FluentMigrator's history tables; exempt from the
  index-design rules only), or somebody else's, and a Marten store's `mt_` table is that store's.
- **Remedies.** A table's remedy is migration SQL - `ALTER TABLE … SET (…)`, `CREATE INDEX CONCURRENTLY`,
  `REINDEX INDEX CONCURRENTLY`, `CREATE TABLE … PARTITION OF` - shown under "For your own migrations - the studio never
  runs this". Every name is quoted, a name that cannot be quoted safely gets no statement, and a Marten table gets
  none. A server setting's remedy names where it changes on the platform the server runs on.
- **When it runs.** Only while a Health page or the Overview is open, or the opt-in health check is probed, at most once
  per `HealthInterval` per database however many tabs watch, in a read-only transaction as `SqlConsoleRole`, under
  `statement_timeout` and `lock_timeout`, without DDL. The statement checks read `pg_stat_statements` as the data
  source's own role, fenced to `userid = current_user` and this database ([security.md](security.md)).
- **Who sees what.** A database-wide number needs the source policy for the database. Anything that names a session, a
  role other than the reading one, a client address, an application or a statement needs `ViewDatabaseActivity` and the
  write policy. A finding about a table in a schema the visitor may not see is counted, never named.

`services.AddHealthChecks().AddPostgresStudio()` turns the same checks into one ASP.NET Core health check (name
`postgres-studio` unless given). Each run checks up to 25 databases - every single-database source first, then each
database source from where the last run stopped - and reports codes and counts only. Details, and the meaning of
`databasesSkipped`, are in [health.md](health.md#export).

## The SQL console

`RunSql`, the write policy asked with the capability named, and then:

```sql
BEGIN;
  SET TRANSACTION READ ONLY;
  SET LOCAL statement_timeout = …;                    -- QueryTimeout
  SET LOCAL lock_timeout = …;                         -- 3 seconds
  SET LOCAL idle_in_transaction_session_timeout = …;  -- 60 seconds
  SET LOCAL ROLE …;                                   -- SqlConsoleRole, when set
  <your statement>
ROLLBACK;
```

The statement parser and its function denylist exist for a better error message; the transaction and the role are the
guard ([security.md](security.md#the-sql-console)). `MaxSqlConsoleRows` stops the reader and never rewrites your
statement. Grant `RunSql` to people you would give `psql` to, and set `SqlConsoleRole` to a role a DBA created with
`SELECT` on what the console should read - K05 raises a console without one.

## Options

| Option | Default | Bounds and notes |
|---|---|---|
| `Path` | `/postgres` | A plain rooted path; `MapPostgresStudio(pattern)` overrides it |
| `Title` | `Postgres Studio` | |
| `AuthorizationPolicy` | `null` | Every studio endpoint: pages, circuit, static assets |
| `SourceAuthorizationPolicy` | `null` | Against `PostgresStudioResource`, capability `null` |
| `WriteAuthorizationPolicy` | `null` | Against `PostgresStudioResource`, capability named; falls back to the source policy |
| `ReadOnly` | `false` | Every capability off |
| `Capabilities` | all off | `BrowseDatabase`, `ViewDatabaseActivity`, `RunSql` |
| `BrowsableSchemas` | `public` | Exact names, case-sensitive as Postgres stores them, or `"*"`; a list replaces the default |
| `IncludeDefaultDataSource` | `true` | List the unkeyed singleton `NpgsqlDataSource` as `default` |
| `SqlConsoleRole` | `null` | A plain identifier; `SET LOCAL ROLE` for the console, the browser and the health reads (not the statement checks) |
| `QueryTimeout` | 30 s | 1 second to 10 minutes |
| `DefaultPageSize` | `50` | 1 to `MaxPageSize` |
| `MaxPageSize` | `500` | 1 to 5,000 |
| `MaxInlineValueBytes` | 512 KiB | 1 byte to 64 MiB; a larger cell is read on demand |
| `MaxSqlConsoleRows` | `500` | 1 to 10,000 |
| `ExactCountThreshold` | `100000` | Above it a count stays the `reltuples` estimate; 0 means always estimate |
| `RefreshInterval` | 5 s | 1 second to 5 minutes |
| `HealthInterval` | 60 s | 15 seconds to 1 hour; a longer `RefreshInterval` wins |

Validation runs when the options are first read, which `MapPostgresStudio()` does at startup; a value outside its
bounds fails the host with a message that names the option.

## The diagnostics API and compute capacity

`IPostgresDiagnostics`, registered by `AddPostgresStudio()`, is the same reads for your own code: the databases the
studio would list, and for each the findings, by stable code and variant, with Storage, Statements, Settings and
Connections sections. It answers host code rather than a visitor, so no policy, capability or `ReadOnly` is asked;
Activity-grade data needs `IncludeActivity`, which is off by default; and the studio never serves a result. See
[diagnostics.md](diagnostics.md).

`IPostgresCapacitySource`, registered as a singleton, reports what SQL cannot see - an Aurora Serverless v2 cluster's ACU,
say. It is asked once per `HealthInterval` per database and instance, given five seconds, and its answer is shown on the
Overview, the Health page and the Server page and read by C03 and C11. See [aurora.md](aurora.md).

## Logging and audit

Every audited action and every refusal is written to the Activity ring (500 entries, in memory) and to your `ILogger`.
Postgres Studio logs Marten Studio's event ids, at the same levels and with the same properties, in its own words
("Postgres Studio user … in data source …"), under `PostgresStudio.*` categories; filter on the event id to see either
studio. Its ids are 9200-9299, 9300-9329 and 9400-9519; [security.md](security.md#audit-events) lists the audit ones.
Every value a visitor could shape reaches a log line escaped and bounded.

## Npgsql

The package depends on Npgsql 8.0.3 or later: 7.x reports no pool instruments, which C01 and Sessions' pool panel read,
and 8.0.0 to 8.0.2 carry CVE-2024-32655. It is tested at 8.0.3 and at 10.0.3, whose renamed pool instruments
(`db.client.connection.count` and `.max`) it reads as well as 8's and 9's.

## Limits

- One studio per application in 0.5; a Marten application uses Marten Studio, whose database browser shows what else
  lives in the store's own databases.
- No tenant in the scope, and no row-level filtering: a Postgres Studio visitor who may read a table's rows reads all
  of them.
- A host database source's names are identities; renaming one resets that database's history and mutes.
- The studio stays English; a host words findings in its own language through the diagnostics API's codes.

## The sample

`samples/PostgresStudio.Sample` (`dotnet run`, <http://localhost:5211>) has no Marten: an admin database as the
application's own data source, with Quartz.NET's job store, a schema of catalog edge cases and the application's own
tables, and three tenant databases listed through its own `IPostgresDatabaseSource`. Sign in as `admin`, `ops` or
`viewer` (the password is the user name); the write policy is asked per capability. With no
`ConnectionStrings:Postgres` it starts a fresh throwaway Postgres and says so; see its
[README](../samples/PostgresStudio.Sample/README.md).
