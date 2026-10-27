# Postgres Studio

An embeddable Blazor Server admin UI for the PostgreSQL databases an ASP.NET Core application already registers as
`NpgsqlDataSource`, shipped as one MIT-licensed Razor Class Library. You add `AddPostgresStudio()` to your container
and `MapPostgresStudio()` to your endpoint pipeline, and the studio runs inside your own application against *its own*
data sources: a database browser for tables, views, functions, triggers, sequences and types and their rows, the
sessions and locks on the server, the foreign keys as a diagram, a health advisor that says what is wrong and how sure
it is, the statements that take the database's time, autovacuum's view of every table, storage, replication and the
server's settings - and, off until you turn it on, a read-only SQL console.

It is **not** a connection-string tool. There is no "point it at a database" mode: the studio reads through the data
sources your application configured, with their role, their pool and their TLS, and adds no new place that holds a
credential. It is the core [Marten Studio](https://github.com/lahma/marten-studio#readme) is built on; an application
that uses [Marten](https://martendb.io) takes the `MartenStudio` package instead, which brings this one with it.

Registering the studio must never change how your application behaves: no global styles, no component-library version
pinned onto your host, no middleware ordering requirement, no altered authentication. A host that adds the package and
never maps it is byte-for-byte the application it was.

The long form, with every option and the reasons behind them, is
[`docs/postgresstudio.md`](https://github.com/lahma/marten-studio/blob/main/docs/postgresstudio.md).

## Install

```shell
dotnet add package PostgresStudio
```

One package reference. It brings `Npgsql` (floor `8.0.3`, tested at 8.0.3 and 10.0.3) and
`Microsoft.AspNetCore.App.Internal.Assets` (floor `10.0.12`, three `.js` files and an MSBuild target, no runtime
assembly), and takes everything else from the shared framework. It references no Marten, JasperFx or Weasel package.
Target framework: `net10.0`. PostgreSQL 14 to 18 are supported, and every release is tested on each
([`docs/postgres-versions.md`](https://github.com/lahma/marten-studio/blob/main/docs/postgres-versions.md)).

One studio per application: `AddPostgresStudio()` throws in an application that called `AddMartenStudio()`, and the
reverse.

## Register and mount

The minimum is a data source, two calls, and a decision about who may open it:

```csharp
builder.Services.AddNpgsqlDataSource(connectionString);   // or AddSingleton(NpgsqlDataSource.Create(...))
builder.Services.AddPostgresStudio();

// ...

app.MapPostgresStudio().RequireAuthorization();
```

The studio is then at `/postgres`, showing the structure of the `public` schema of the application's unkeyed data
source, behind whatever `RequireAuthorization()` means in your application. The sample host does it with every option
spelled out - this block is compiled from
[`samples/PostgresStudio.Sample/Program.cs`](https://github.com/lahma/marten-studio/blob/main/samples/PostgresStudio.Sample/Program.cs),
so it cannot drift:

<!-- snippet: readme_register -->
```csharp
// The application's own data source - what AddNpgsqlDataSource(connectionString) from Npgsql.DependencyInjection
// registers, written out here so the sample needs no other package. Postgres Studio finds the unkeyed one by itself and
// lists it as the source "default". It reads through it, with its role, its TLS and its settings, and never holds a
// connection string of its own.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

// One database per tenant, listed by the application itself from a directory in the admin database.
builder.Services.AddSingleton(services =>
    new SampleTenantDatabases(services.GetRequiredService<NpgsqlDataSource>(), connectionString));

builder.Services.AddPostgresStudio(options =>
{
    // Who gets in, and who may use each capability: the studio evaluates both against the host's own authorization,
    // and never authenticates anybody itself. The write policy is asked about each capability by name.
    options.AuthorizationPolicy = SamplePolicies.Studio;
    options.WriteAuthorizationPolicy = SamplePolicies.StudioCapability;

    // All four are off until the host says otherwise: reading rows and definitions, reading definitions alone, other
    // sessions' statements, and the SQL console. `All()` is the one-line, greppable opt-in; `ReadOnly` overrides it
    // whatever it says.
    options.Capabilities = PostgresStudioCapabilities.All();

    // `public` is browsable only while nothing names a schema: a list replaces that default rather than adding to it, so
    // `public` - where every tenant database keeps its tables - is named first. The others are the admin database's:
    // Quartz.NET's job store, a legacy schema of catalog edge cases, and the application's own tables. "*" would open
    // every schema the role can use - fine for a scratch database, not for a shared one.
    options.BrowsableSchemas.Add("public");
    options.BrowsableSchemas.Add(RelationalDemoSchema.QuartzSchemaName);
    options.BrowsableSchemas.Add(RelationalDemoSchema.LegacySchemaName);
    options.BrowsableSchemas.Add(RelationalDemoSchema.SampleSchemaName);

    // The tenant databases, as a second source beside "default": the studio lists them through the application, and
    // asks it for a tenant's data source only when somebody opens that tenant.
    options.AddDatabaseSource("tenants", static services => services.GetRequiredService<SampleTenantDatabases>());
});
```
<!-- endSnippet -->

and the mapping:

<!-- snippet: readme_map -->
```csharp
app.MapPostgresStudio().RequireAuthorization(SamplePolicies.Studio);
```
<!-- endSnippet -->

`MapPostgresStudio("/ops/postgres")` mounts it somewhere else; the pages, the static assets, the Blazor circuit and the
framework script all move with it. It works in an API-only host with no `.razor` file of its own, and needs no
`app.UseAntiforgery()`.

### The opt-in health check

The health advisor can also answer an ASP.NET Core health check, which is how your own monitoring alerts on it:

<!-- snippet: readme_health -->
```csharp
// Optional, and AddPostgresStudio() does not do it for you: the health advisor's checks as one ASP.NET Core health
// check. It reports codes and counts, never a name, and runs the checks at most once per HealthInterval however often
// the endpoint is probed.
builder.Services.AddHealthChecks().AddPostgresStudio(tags: ["postgres"]);
```
<!-- endSnippet -->

Each run checks up to 25 databases, every single-database source first and then each database source in rotation. It
reports `Unhealthy` (or the `failureStatus` you give) on a Critical finding and `Degraded` on a Warning, a check that
failed or ran out of time, or a source it could not reach, and its data is codes and counts only, because there is no
visitor to authorize.

## What it browses

- **The application's unkeyed `NpgsqlDataSource`**, found by itself and listed as the source `default`
  (`IncludeDefaultDataSource`, on by default). Only a singleton is listed: a scoped or transient one would build a pool
  per visitor.
- **Any other data source**, by name: `options.AddDataSource("orders", sp => sp.GetRequiredKeyedService<NpgsqlDataSource>("orders"))`.
- **A set of databases your application lists itself** - one per tenant, say - by name, through an
  `IPostgresDatabaseSource` you register: `options.AddDatabaseSource("tenants", sp => sp.GetRequiredService<TenantDatabases>())`.
  `ListAsync` names the databases and must create nothing; `GetDataSourceAsync` returns your own data source for one of
  them, which the studio never disposes. The names are identities: they key authorization, the health history and
  mutes, so they must stay the same for the same database.

Every factory is called once, with the root service provider, and its result kept for the process. Scope travels in
the URL (`?store=tenants&db=tenant_b`), so a link you paste into a chat reopens the same database.

## Authorization

Three layers and two switches, all yours; the studio authenticates nobody.

1. **`AuthorizationPolicy`** - who gets in. Applied to every studio endpoint: the pages, the Blazor circuit and the
   static assets. `RequireAuthorization()` on the builder `MapPostgresStudio()` returns does the same job for the pages
   and the circuit.
2. **`SourceAuthorizationPolicy`** - which data source and database, resource-based against a
   `PostgresStudioResource(SourceName, DatabaseIdentifier, Capability)` and asked on every data call. A Postgres Studio
   resource has no tenant.
3. **`WriteAuthorizationPolicy`** - each read authorized like a write, against the same resource with the capability
   named. Falls back to `SourceAuthorizationPolicy`.

On top: **`ReadOnly`** turns every capability off, and **`Capabilities`** decides which exist. A mapping that answers
none of this refuses to start, before the host binds a listener; `AllowAnonymous()` is an answer, saying nothing is
not.

## Capabilities

Four, **all `false` by default**, all turned on by `PostgresStudioCapabilities.All()`. Each is a read, authorized like
a write.

| Capability | What it allows |
|---|---|
| `BrowseDatabase` | The rows of tables and views, and the definitions of views, functions, triggers, sequences and types, in the schemas `BrowsableSchemas` admits. Implies `ViewDefinitions` |
| `ViewDefinitions` | The definitions alone - a view's query, a function's body, a trigger's definition, a sequence's value, a type's description - in the schemas `BrowsableSchemas` names exactly: never a row, never a schema only `"*"` admits |
| `ViewDatabaseActivity` | Other sessions' process ids, roles, client addresses, application names and statements, on Database › Sessions, the Health page and Operations › Statements |
| `RunSql` | Read-only SQL from the Query page's console, its Explain analyze, and What if? under a console plan |

**The database browser's gate.** The schemas `BrowsableSchemas` names exactly - `public` when nothing names any - are
the data source's own: their structure (tables, columns, keys, indexes, estimates) is shown to anyone the source policy
admits. Their definitions need `ViewDefinitions` or `BrowseDatabase`, their rows `BrowseDatabase`, and everything in a
schema only `"*"` admits `BrowseDatabase`, each with the write policy. A name in a schema the visitor may not see is masked as `‹withheld›` wherever Postgres prints it. A Marten table
(`mt_`) is listed as Marten's and its rows are never read.

## Options

| Option | Default | |
|---|---|---|
| `Path` | `/postgres` | Where the studio is mounted. `MapPostgresStudio(pattern)` overrides it. |
| `Title` | `Postgres Studio` | Browser tab and sidebar heading. |
| `AuthorizationPolicy`, `SourceAuthorizationPolicy`, `WriteAuthorizationPolicy` | `null` | The three layers above. |
| `ReadOnly` | `false` | Every capability off. |
| `Capabilities` | all off | `BrowseDatabase`, `ViewDefinitions`, `ViewDatabaseActivity`, `RunSql`. |
| `BrowsableSchemas` | `public` | Exact schema names, or `"*"`. A list that names schemas replaces the default. |
| `IncludeDefaultDataSource` | `true` | List the unkeyed `NpgsqlDataSource` as `default`. |
| `SqlConsoleRole` | `null` | A role the console, the database browser and the health advisor's reads (bar its statement checks, which read `pg_stat_statements` as the data source's own role) switch to with `SET LOCAL ROLE`: the one real narrowing. |
| `QueryTimeout` | 30 seconds | How long a studio query may run. 1 second … 10 minutes. |
| `DefaultPageSize`, `MaxPageSize` | `50`, `500` | Rows per page; 1 … 5000. |
| `MaxInlineValueBytes` | 512 KiB | The largest cell value rendered inline. 1 byte … 64 MiB. |
| `MaxSqlConsoleRows` | `500` | Row cap for the SQL console; the reader stops there. 1 … 10 000. |
| `ExactCountThreshold` | `100000` | Above this estimate, counts stay `reltuples` estimates. |
| `RefreshInterval` | 5 seconds | How often live pages poll. 1 second … 5 minutes. |
| `HealthInterval` | 60 seconds | How often the health advisor re-checks a database while a page watches it. 15 seconds … 1 hour. |

## Screens

**Overview** - the server, the health glance, the largest tables, the connections by state and the latest studio
activity. **Query** - the SQL console, inside `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK` under
`QueryTimeout`, a 3-second lock timeout and `SqlConsoleRole`. **Database** - **Tables** (the browser: kind tabs, object
detail, a Rows tab with keyset paging and an index verdict for a filter), **Sessions** (sessions, locks and prepared
transactions, sessions per application) and **Relationships** (the foreign keys, drawn on the server). **Operations** -
**Health** (the advisor: findings by area, each with its evidence, how sure it is and a remedy, which for a table is
migration SQL "for your own migrations - the studio never runs this"), **Statements** (where the database's time goes,
statement by statement, grouped by verb and table), **Maintenance** (autovacuum's triggers per table, what runs now, the
workers, the freeze forecast and the vacuum horizon), **Storage** (size, growth and the largest tables, from the
catalog's page counts) and **Replication** (slots, standbys, the archiver, WAL, checkpoints and I/O). **System** -
**Server** (version, platform, what the reading role can see, settings, extensions and the security posture) and
**Activity** (the last 500 actions taken through the studio).

The health advisor runs only while a Health page or the Overview is open, or the health check is probed, at most once
per `HealthInterval` per database, in a read-only transaction, without DDL and without a lock beyond what a read takes.
Its checks and their codes are listed in
[`docs/health/codes.md`](https://github.com/lahma/marten-studio/blob/main/docs/health/codes.md). T20 says when a
range-partitioned table is running out of partitions: a time range by its clock, a `smallint`, `integer` or `bigint`
range by where its rows stop, from the partitions' estimated rows and without opening one.

**Plans.** The console's **Explain** and **Explain analyze** buttons draw a plan as a tree - each node's type and what
it reads, estimated and actual rows and loops, its share of the plan's time or cost, buffers and I/O - and so does an
`EXPLAIN (FORMAT JSON)` you type. `EXPLAIN ANALYZE` runs only in the console, under `RunSql`, inside its read-only
transaction, so a write its guard misses is refused by Postgres (`25006`). Under every plan, **plan insights** (P01-P08,
[`docs/health/plans.md`](https://github.com/lahma/marten-studio/blob/main/docs/health/plans.md)) point at what is wrong
with it. Operations › Statements opens one statement's drill-in (`?queryid=`): its text, its counters and its generic
plan, planned as `SqlConsoleRole` behind the page's own gate, masked as the text is, and audited. Where a plan's
sequential scan filters on plain columns, **What if?** plans the statement again with an index on them imagined by
hypopg and draws both plans side by side; it creates nothing, and needs hypopg installed, which is your decision.

**Blocking.** Database › Sessions › Blocking (`?view=blocking`) draws who holds up whom as trees, behind
`ViewDatabaseActivity`, with a chain head's `pg_terminate_backend` (and `pg_cancel_backend`) as text to copy - the
studio never runs either - and on PostgreSQL 19 the server's lock waits from `pg_stat_lock`. C12 names the head of a
chain that sits idle in its transaction or has kept others waiting for minutes.

## The diagnostics API

`IPostgresDiagnostics`, registered by `AddPostgresStudio()`, gives your own code what the studio reads: the databases it
would list, and for each the findings, with stable codes and variants, and Storage, Statements, Settings and Connections
sections. It answers host code, not a visitor; Activity-grade data needs `IncludeActivity`, off by default; and the
studio never serves a result. See
[`docs/diagnostics.md`](https://github.com/lahma/marten-studio/blob/main/docs/diagnostics.md).

## Security model and limits

- **Nothing that reads beyond structure is on by default.** Four capabilities, all `false`, plus `ReadOnly`.
- **Refusals live in the service layer** and are audited, success and failure alike, to a 500-entry in-memory ring and
  to your `ILogger` under event ids 9200-9299, 9300-9329 and 9400-9519.
- **The SQL console's real guard is the transaction, not the parser**, and the only true narrowing is `SqlConsoleRole`.
  Without it the console reads everything your data source's role can read. Grant `RunSql` only to people you would
  give `psql` to.
- **`BrowsableSchemas` limits what the screens show, not what `RunSql` can read.** `"*"` with no `SqlConsoleRole` is a
  warning at startup.
- **No tenant.** A Postgres Studio scope is a data source and a database; nothing filters a table by tenant.
- **It cannot honour another application's `IsDocumentTypeVisible`.** A Marten store's database belongs in Marten
  Studio, where the store's tenancy, soft deletes and visibility apply.

The long form is [`docs/postgresstudio.md`](https://github.com/lahma/marten-studio/blob/main/docs/postgresstudio.md),
and the security model in full is
[`docs/security.md`](https://github.com/lahma/marten-studio/blob/main/docs/security.md).

## License

MIT.
