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

One package reference. It brings [`PostgresStudio.Diagnostics`](https://www.nuget.org/packages/PostgresStudio.Diagnostics)
at exactly its own version (the diagnostics engine, with `Npgsql` - floor `9.0.4`, tested at 9.0.4 and 10.0.3 - and the
`Microsoft.Extensions.*` abstractions) and `Microsoft.AspNetCore.App.Internal.Assets` (floor `10.0.12`, three `.js` files
and an MSBuild target, no runtime assembly), and takes everything else from the shared framework. It references no Marten,
JasperFx or Weasel package.
Target framework: `net10.0`. PostgreSQL 15 to 19 are tested, and 14 still works
([`docs/postgres-versions.md`](https://github.com/lahma/marten-studio/blob/main/docs/postgres-versions.md)).

**Which package.** Want the UI: this one. Want only the findings in your own code - a worker that mails a daily summary, a
shared library, a job host with nothing to mount - take `PostgresStudio.Diagnostics` alone: it has no ASP.NET Core in it
([`docs/diagnostics.md`](https://github.com/lahma/marten-studio/blob/main/docs/diagnostics.md#without-the-ui)). The two are
released together at one version, and this package depends on exactly its own `PostgresStudio.Diagnostics`
(`[x.y.z]` in the nuspec), so a central `PackageVersion` for it must equal this package's, or be removed:
`AddPostgresStudio()` checks a newer `PostgresStudio.Diagnostics`, or another build of this version, at startup and throws an
`InvalidOperationException` that says which version to pin; a missing or older one fails when `AddPostgresStudio()` is first
called, in the loader, with a `FileNotFoundException` that names it and the version to pin.

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
| `BrowseDatabase` | The rows of tables and views, a sequence's current value, and the definitions of views, functions, triggers and types, in the schemas `BrowsableSchemas` admits. Implies `ViewDefinitions` |
| `ViewDefinitions` | The definitions alone - a view's query, a function's body, a trigger's definition, a type's description - in the schemas `BrowsableSchemas` names exactly: never a row, never a sequence's current value, never a schema only `"*"` admits |
| `ViewDatabaseActivity` | Other sessions' process ids, roles, client addresses, application names and statements, on Database › Sessions, the Health page and Operations › Statements |
| `RunSql` | Read-only SQL from the Query page's console, its Explain analyze, and What if? under a console plan. The sidebar's Query and Plan entries are there only while it is on |

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
| `ApplicationPath` | `null` | Where the header's "Back to application" link goes; no link when `null`. A path that starts with a single `/` (`/`, `/app`; a host behind `UsePathBase` includes its base, `/myapp/`) or an absolute `http` or `https` URL without user information, used exactly as written and never re-rooted under the studio's mount path. Anything else fails startup. |
| `AuthorizationPolicy`, `SourceAuthorizationPolicy`, `WriteAuthorizationPolicy` | `null` | The three layers above. |
| `ReadOnly` | `false` | Every capability off. |
| `Capabilities` | all off | `BrowseDatabase`, `ViewDefinitions`, `ViewDatabaseActivity`, `RunSql`. |
| `BrowsableSchemas` | `public` | Exact schema names, or `"*"`. A list that names schemas replaces the default. |
| `IncludeDefaultDataSource` | `true` | List the unkeyed `NpgsqlDataSource` as `default`. |
| `SqlConsoleRole` | `null` | A role the console, the database browser and the health advisor's reads (bar its statement checks, which read `pg_stat_statements` as the data source's own role) switch to with `SET LOCAL ROLE`: the one real narrowing. |
| `QueryTimeout` | 30 seconds | How long a studio query may run. 1 second … 10 minutes. |
| `MaxStudioConnections` | 2 | The most connections the studio holds at a time of each pool it reads through - each of the application's own data sources. A read that finds all of them in use waits for one, at most `QueryTimeout`, and then says so; a nested read never waits for its own caller. The cap is never above the data source's own `Maximum Pool Size` (the smaller of the two, when `Pooling` is on), so a host never copies a pool's size into this option; a data source the connection history samples through keeps one more connection for it outside the cap, so a dedicated pool for both needs one more. The Health page's studio-cost line shows the cap in force and how many are in use, and C01 says how many of a saturated pool's connections were the studio's. 1 … 100. |
| `StudioApplicationName` | derived | The `application_name` the studio's sessions carry in `pg_stat_activity`, Database › Sessions and an external monitor: by default the application's own `Application Name` with the studio's after it in parentheses (`orders-api (Postgres Studio)`), or the studio's alone. Set for each of the studio's read-only transactions only (`set_config(…, true)`), so a pooled connection goes back to the application as it came; `""` leaves the application's own name. Printable ASCII, at most 63 characters. |
| `DefaultPageSize`, `MaxPageSize` | `50`, `500` | Rows per page; 1 … 5000. |
| `MaxInlineValueBytes` | 512 KiB | The largest cell value rendered inline. 1 byte … 64 MiB. |
| `MaxSqlConsoleRows` | `500` | Row cap for the SQL console; the reader stops there. 1 … 10 000. |
| `ExactCountThreshold` | `100000` | Above this estimate, counts stay `reltuples` estimates. |
| `RefreshInterval` | 5 seconds | How often live pages poll. 1 second … 5 minutes. |
| `HealthInterval` | 60 seconds | How often the health advisor re-checks a database while a page watches it. 15 seconds … 1 hour. |
| `AcceptedChecks` | empty | Health checks the application accepts, by code with a reason: still run and shown, as accepted, but counted nowhere. Read once, at startup (1.2). |

## Screens

**Overview** - the server, the health glance, the largest tables, the connections by state, the latest studio
activity and one line on the connection ("Connection: TLS verified · pool 100 · 2 things worth a look"). **Query** - the SQL console, inside `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK` under
`QueryTimeout`, a 3-second lock timeout and `SqlConsoleRole`. **Database** - **Tables** (the browser: kind tabs, object
detail, a Rows tab with keyset paging and an index verdict for a filter), **Sessions** (sessions, locks and prepared
transactions, sessions per application), **Connection** (how the application connects: its Npgsql settings, the TLS and
pool facts, and what to change) and **Relationships** (the foreign keys, drawn on the server). **Operations** -
**Health** (the advisor: findings by area, each with its evidence, how sure it is and a remedy, which for a table is
migration SQL "for your own migrations - the studio never runs this"), **Statements** (where the database's time goes,
statement by statement, grouped by verb and table), **Maintenance** (autovacuum's triggers per table, what runs now, the
workers, the freeze forecast and the vacuum horizon), **Storage** (size, growth and the largest tables, from the
catalog's page counts) and **Replication** (slots, standbys, the archiver, WAL, checkpoints and I/O). **System** -
**Server** (version, platform, what the reading role can see, settings, extensions and the security posture) and
**Activity** (the last 500 actions taken through the studio). **Find** (1.2), the header box (`/` or Ctrl+K), opens a
table or view by name or a statement by queryid, through the page it opens and under that page's gate.

**Database › Connection** (1.1; the long form is
[`docs/connection.md`](https://github.com/lahma/marten-studio/blob/main/docs/connection.md)) shows how your application
actually connects. A strip of facts gives the server and database, the role, whether the connection is encrypted and
whether the server's certificate is checked, the pool, the Npgsql version loaded and any managed platform or pooler in
front. "Worth a look" lists what the health advisor found about the connection and what Postgres Studio would change -
each with the setting it is about and the fix as a connection string and as `NpgsqlDataSourceBuilder` code, ready to
copy. Below that, every Npgsql setting with its value in effect, where the value came from (the connection string, an
environment variable such as `PGUSER`, or Npgsql's default) and the default; by default only what differs from the default
is shown. It then shows what the server saw of the studio's own connection, and - with `ViewDatabaseActivity` - which
applications this login has sessions for, how old the oldest is and how many are on TLS. A password is only ever shown as
set or not set; the connection string itself is never shown, logged or exported. The page reads and changes nothing.
Under "Compare a connection string" you can paste another Npgsql connection string - the one in a new configuration, one a
colleague sent - to see which of its settings differ from the connection in effect and what the page's advice would be if
the application switched to it. Passwords are removed before anything is read, an option's value is never shown, and the
studio keeps, logs, audits and connects to nothing you paste; it is read in this page only, so it is not part of the audit
log. A password with a `;` in it must be quoted, or the keyword that follows it is refused without a name. The advice is
the page's own hints, judged against what the server reported about the connection in effect; the health advisor's
findings run on the application's real data source only. What you paste travels to the server over SignalR like every
other input in a Blazor Server page, so if your application logs `Microsoft.AspNetCore.SignalR` at Debug level that log
records it (typed SQL in the console included): keep that category at Information or above in production.

The health advisor runs only while a Health page or the Overview is open, or the health check is probed, at most once
per `HealthInterval` per database, in a read-only transaction, without DDL and without a lock beyond what a read takes.
Its checks and their codes are listed in
[`docs/health/codes.md`](https://github.com/lahma/marten-studio/blob/main/docs/health/codes.md). T20 says when a
range-partitioned table is running out of partitions: a time range by its clock, a `smallint`, `integer` or `bigint`
range by where its rows stop, from the partitions' estimated rows and without opening one.

**Plans.** The console's **Explain** and **Explain analyze** buttons draw a plan as a tree - each node's type and what
it reads, estimated and actual rows and loops, its share of the plan's time or cost, buffers and I/O - and so does an
`EXPLAIN (FORMAT JSON)` you type. `EXPLAIN ANALYZE` runs only in the console, under `RunSql`, inside its read-only
transaction, so a write its guard misses is refused by Postgres (`25006`). **Query › Plan** is the same gate as a page of its own:
write a statement, or paste a plan's JSON (which reads nothing), and get it drawn as a tree and a table of every node, with a
summary, the critical path, heat colouring and an export, including one for speedscope. Under every plan, **plan insights** (P01-P20,
[`docs/health/plans.md`](https://github.com/lahma/marten-studio/blob/main/docs/health/plans.md)) point at what is wrong
with it. Operations › Statements opens one statement's drill-in (`?queryid=`): its text, its counters and its generic
plan, planned as `SqlConsoleRole` behind the page's own gate, masked as the text is, and audited. Where a plan's
sequential scan filters on plain columns, **What if?** plans the statement again with an index on them imagined by
hypopg and draws both plans side by side; it creates nothing, and needs hypopg installed, which is your decision.
Statements also has an **Index advisor** (1.2, hypopg): the top statements you may see, each index their plans suggest
tried one at a time, ranked by planned cost saved times calls, with `CREATE INDEX CONCURRENTLY` as the remedy; planned on
a press behind the page's gate, nothing created. T01 and T15 findings offer it, and a what-if of the foreign key's own
lookup.

**Blocking.** Database › Sessions › Blocking (`?view=blocking`) draws who holds up whom as trees, behind
`ViewDatabaseActivity`, with a chain head's `pg_terminate_backend` (and `pg_cancel_backend`) as text to copy - the
studio never runs either - and on PostgreSQL 19 the server's lock waits from `pg_stat_lock`. C12 names the head of a
chain that sits idle in its transaction or has kept others waiting for minutes.

**Holders.** `max_connections` is the server's, and the database that uses it up is often not the one open here. Database ›
Sessions › Holders (`?view=holders`) groups the whole server's client sessions by database, application, role and client
address - sessions, active, idle and idle in a transaction, the oldest idle and idle-in-transaction age - against the budget C03
judges. Anybody the source policy passes sees the counts per database, a database named only where the policy admits them to it
and the rest together as "Other databases"; the applications, roles and addresses, and every database's name, need
`ViewDatabaseActivity`. C13 names the application that holds a tenth of the server's client limit idle while the server is 80%
used, and C14 the connections that set no `Application Name` (Info), both behind that capability.

**Connection history** (1.0). Holders is the server's connections *now*. Register `AddPostgresConnectionHistory()` and a
background sampler counts the server's client sessions by application every minute, over one connection it keeps open for the
purpose, into a store you can replace with your own (`IPostgresConnectionHistoryStore`; the default keeps seven days in
memory). Holders then says the peak against the client limit and when it happened, and, behind `ViewDatabaseActivity`, each
application's own peak. Only one process of a scaled-out application needs to sample: `ShouldSample` is asked before every
round. A sample the store refuses - a table in the sampled database needs a connection from your pool, which is exhausted
at the moment that matters - is not lost: the sampling process holds up to an hour of samples per server, writes them oldest
first before the next one (and, once it stops being the sampler, at every round, so a change of leader does not lose them),
drops the oldest past that hour, and loses them only if it stops; make the store's append idempotent (`on conflict
(server, taken_at) do nothing`), since one that committed and timed out is retried. Holders says how many are waiting. After its first read of a server the studio asks the store only for the samples
since its last read, less five minutes, with a full read every ten minutes; a store should serve `since` with an index on
`(server, taken_at)`, which the example table's primary key already gives. Details in [docs/diagnostics.md](../../docs/diagnostics.md#connection-history).

**Reader only (1.0.1).** A process that must never sample - the web application whose studio pool has no connection to spare -
reads a history another process fills. The worker samples and appends to a store both processes share; the web application
registers `AddPostgresConnectionHistoryReader()` with the same store. It adds no background service, opens no connection of its
own, and refuses to start without the store:

```csharp
// The worker: samples, and appends to the shared store.
builder.Services.AddPostgresDiagnostics();
builder.Services.AddSingleton<IPostgresConnectionHistoryStore, SharedHistoryStore>();
builder.Services.AddPostgresConnectionHistory();

// The web application: reads it, samples nothing.
builder.Services.AddPostgresStudio();
builder.Services.AddSingleton<IPostgresConnectionHistoryStore, SharedHistoryStore>();
builder.Services.AddPostgresConnectionHistoryReader(o => o.Retention = TimeSpan.FromDays(7));
```

The two registrations are exclusive in one container.

## The diagnostics API

`IPostgresDiagnostics`, registered by `AddPostgresStudio()`, gives your own code what the studio reads: the databases it
would list, and for each the findings, with stable codes and variants, and Storage, Statements, Settings and Connections
sections. It answers host code, not a visitor; Activity-grade data needs `IncludeActivity`, off by default; and the
studio never serves a result. With no UI to mount, take `PostgresStudio.Diagnostics` and `AddPostgresDiagnostics()` for the
same API alone. See
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
[`docs/security.md`](https://github.com/lahma/marten-studio/blob/main/docs/security.md). From 1.0 the packages follow
Semantic Versioning over the contract in
[`docs/stability.md`](https://github.com/lahma/marten-studio/blob/main/docs/stability.md).

## License

MIT.
