# Setting Data Studio up

Which packages to take and what they bring, registering and mounting, every option, and what tenancy and hosting need. The quick start is in the [README](../README.md#quick-start).

## Packages and modules

Data Studio is seven packages, released together at one version.

| Package | What it is | Register | Pages |
|---|---|---|---|
| `DataStudio` | The shell: the mount, the layout, the rail, the front page, Activity and the assets. ASP.NET Core and nothing else; every module brings it | `AddDataStudio()`, `MapDataStudio()` | `/studio`, `/studio/activity` |
| `DataStudio.Marten` | The Marten module, for an application with a Marten `IDocumentStore`; brings the Postgres module | `.AddMarten()` | `/studio/marten/…`, and the Postgres pages over the stores' databases |
| `DataStudio.Postgres` | The Postgres module, for any application that registers an `NpgsqlDataSource` and has no Marten | `.AddPostgres()` | `/studio/postgres/…` |
| `DataStudio.Postgres.Diagnostics` | The findings in your own code, with no UI and no ASP.NET Core: a worker, a job host, a shared library | `services.AddPostgresDiagnostics()` | none |
| `DataStudio.Kafka` | The Kafka module, over the application's own Kafka credentials; needs no database module | `.AddKafka()` | `/studio/kafka/…` |
| `DataStudio.Kafka.Core` | The Kafka engine, with no ASP.NET Core; brought by `DataStudio.Kafka` | none: take `DataStudio.Kafka` | none |
| `DataStudio.Kafka.Wolverine` | Lists a Wolverine application's Kafka clusters and topics in the Kafka module | `kafka.UseWolverineKafka()` | none |

**Which to choose.** If the application uses Marten, take `DataStudio.Marten`: it brings `DataStudio.Postgres` with it, at
exactly its own version, and shows everything the Postgres module shows for the store's databases - the database
browser, Sessions, the health advisor and its pages, the SQL console, Relationships - beside the Marten screens.
Otherwise take `DataStudio.Postgres` alone: it references Npgsql and no Marten, JasperFx or Weasel package, and browses
the application's own data sources. If the application has Kafka, add `DataStudio.Kafka` beside either, or alone: a host
with only Kafka gets no Postgres module, no engine and no Npgsql. And if all you want is the health advisor's findings in
your own code - a worker that mails a daily summary, a shared library - take `DataStudio.Postgres.Diagnostics` alone, the
engine the Postgres module is built on, which has no ASP.NET Core in it
([`diagnostics.md`](diagnostics.md#without-the-ui), and its own
[README](../src/DataStudio.Postgres.Diagnostics/README.md)). The Postgres module's own README is
[`src/DataStudio.Postgres/README.md`](../src/DataStudio.Postgres/README.md), with the long form in
[`postgres.md`](postgres.md), and the Kafka module's is [`kafka.md`](kafka.md).

**One shell per application.** `AddDataStudio()` and each module's call are idempotent, and the modules appear (in the
rail, the banner, the capability chip, the credit line and the front page) in the order the calls were written.
`AddMarten()` brings the Postgres module's pages over the stores' databases, as part of the Marten module (one tile, one
rail), so a Marten host does not call `.AddPostgres()`, and the two together are refused. The Marten
module's database browser shows what else lives in the store's own databases - a job store, a legacy schema - but a
separate plain database of a Marten application is not listed.

**The Kafka module for Apache Kafka®** adds a Kafka section to the studio: the clusters, topics, partitions and consumer groups
your application's own Kafka credentials can describe, and, behind capabilities that are off by default, a search for the
records you need. Call `.AddKafka()` on the same builder; there is no separate map call, and the Kafka pages are served
under the studio's own path, at `/studio/kafka`. It never creates a topic, never commits an offset, and asks your own
policies about every cluster and topic it reads. Apache Kafka is a trademark of the Apache Software Foundation. Data
Studio is not affiliated with or endorsed by the Apache Software Foundation.

#### The Kafka module in five lines

```csharp
using Confluent.Kafka;
using DataStudio;
using DataStudio.Kafka;

builder.Services
    .AddDataStudio()
    .AddKafka(kafka =>
    {
        kafka.AddCluster("primary", new ClientConfig { BootstrapServers = "…" });
        kafka.Topic("orders.v1", t => t.Key(KafkaKeyFormat.Utf8String).Redact(r => r.Header("customer-email")));
    });
```

The Kafka pages are at `/studio/kafka`: the topics this application's credentials can describe, each topic's
partitions, watermarks, configuration and consumer-group lag, and the cluster's settings, with every secret shown only
as "set" or "not set". With `kafka.Capabilities.SearchRecords = true` and a policy that answers who may use it, **Search**
finds records by key, header and CloudEvents attribute in a time window, shows exactly what it read and why it stopped,
and links every record by its offset. With `ReadPayloads` it reads values too, with JSON-path searches and redaction
rules; `ExportRecords` saves results as JSON Lines or CSV, and `LiveTail` follows topics as records arrive. A cluster
marked `Profile(KafkaProfile.Production)` gets lower limits and no export or tail. See [kafka.md](kafka.md).

**Upgrade them together.** The assemblies are one program in several files: each package depends on exactly its own
version of the ones below it (an exact `[x.y.z]` dependency in the nuspec), so a central `PackageVersion` for any of them,
if your repository has one, must equal the others', or be removed. Under `CentralPackageTransitivePinningEnabled` a central
pin at another version makes NuGet drop the package from the graph without a warning, so each `Add…()` call checks the
packages beneath it at startup and throws an `InvalidOperationException` that says which version to pin; a missing or
older one fails when the call is first made, in the loader, with a `FileNotFoundException` that names the package and
the version to pin.

## What each package brings

One package reference per module. `DataStudio.Marten` brings `Marten` (floor `9.57.0`: the floors follow the latest
releases, and 9.57.0 is the first with the fix for compacting a stream that folds to nothing) and `DataStudio.Postgres` at
exactly its own version. That brings `DataStudio.Postgres.Diagnostics`, the engine, and with it `Npgsql` (floor `9.0.4`,
the one Marten 9.57.0 itself uses), and `DataStudio`, the shell, and with it `Microsoft.AspNetCore.App.Internal.Assets`
(floor `10.0.12`, three `.js` files and an MSBuild target, no runtime assembly). Everything else (Blazor, SignalR,
authorization, endpoint routing) comes from the shared framework, so no package pins an ASP.NET Core patch onto your
application. `DataStudio.Kafka` brings `DataStudio` and `DataStudio.Kafka.Core` (over `Confluent.Kafka`, floor `2.14.0`) and
nothing that touches a database. Your application's Npgsql stays the one Marten brings, which is never below the Postgres
module's floor; the Marten module is tested at Marten 9.57.0, the floor, and not on Npgsql 10, which no tested Marten
brings.

**PostgreSQL 15 to 19** are tested (19 on its release candidate until it is generally available, planned 2026-11-19). 14
works but reaches its end of life on 2026-11-12 and is untested. Below 14 the studio still reads the server, with no
promise. A health check that needs more privilege than the reading role has says "not checked", which grant it needs
and who can give it on your platform. See [`postgres-versions.md`](postgres-versions.md), and, for Amazon
Aurora, [`aurora.md`](aurora.md).

## Register and mount

The minimum is two calls, and a decision about who may open it:

```csharp
using DataStudio;
using DataStudio.Marten;

builder.Services
    .AddDataStudio()
    .AddMarten();

// ...

app.MapDataStudio().RequireAuthorization();
```

The studio is then at `/studio`, read-only, behind whatever `RequireAuthorization()` means in your application. A host
adds one call for each module it has, on the same builder, in the order it wants them listed: `.AddMarten(...)`,
`.AddPostgres(...)` and `.AddKafka(...)` (`AddMarten()` brings the Postgres module's pages over the stores' databases, as
part of the Marten module, so a Marten host does not add `.AddPostgres()`, and the two together are refused). A host with only Kafka writes `AddDataStudio().AddKafka(...)`,
and a worker with no UI writes `services.AddPostgresDiagnostics()` and no shell at all. The sample host does the same thing
with every option spelled out - this block is compiled from
[`samples/DataStudio.Sample.Marten/Program.cs`](../samples/DataStudio.Sample.Marten/Program.cs), so it cannot drift:

<!-- snippet: readme_register -->
```csharp
builder.Services
    .AddMarten(options => SampleStore.Configure(options, connectionString))
    .UseLightweightSessions()
    // --no-daemon: Disabled registers no coordinator, which is a host whose projections run elsewhere.
    .AddAsyncDaemon(sample.NoDaemon ? DaemonMode.Disabled : DaemonMode.Solo)
    .InitializeWith<SampleDataSeeder>();

builder.Services
    .AddDataStudio(studio =>
    {
        // Who gets in: the shell evaluates it against the host's own authorization, and never authenticates anybody itself.
        //
        // --anonymous leaves AuthorizationPolicy unset on purpose. AllowAnonymous() on the mapping does win
        // over a configured policy - the studio's endpoints carry both and AllowAnonymous is what the
        // authorization middleware honours - but saying "this policy governs the studio" and then "anyone
        // may open it" in the same application is two answers to one question, and a demo should show the
        // one it means.
        studio.AuthorizationPolicy = sample.Anonymous ? null : SamplePolicies.Studio;

        // The master switch: every module's capabilities are off, whatever they say.
        studio.ReadOnly = sample.ReadOnly;
    })
    .AddMarten(options =>
    {
        // Who may change anything: the module evaluates it against the host's own authorization.
        options.WriteAuthorizationPolicy = SamplePolicies.StudioWrite;

        // Every mutating operation is off until the host says otherwise. `All()` is the one-line, greppable
        // opt-in; the shell's `ReadOnly` overrides it whatever it says.
        options.Capabilities = sample.ReadOnly ? new MartenCapabilities() : MartenCapabilities.All();

        // The database browser (Capabilities.BrowseDatabase, part of All()) may also show these schemas: the
        // Quartz.NET job store and the legacy relational schema the sample seeds beside the store. "*" would
        // open every schema the studio's role can use - fine for a scratch database, not for a shared one.
        options.BrowsableSchemas.Add(RelationalDemoSchema.QuartzSchemaName);
        options.BrowsableSchemas.Add(RelationalDemoSchema.LegacySchemaName);

        // The demo's invoices are conjoined multi-tenant. Naming the tenants here is the cheapest of the
        // three discovery tiers and the only one that can answer before any events have been written.
        foreach (string tenantId in SampleStore.TenantIds)
        {
            options.KnownTenantIds.Add(tenantId);
        }
    });
```
<!-- endSnippet -->

and the mapping:

<!-- snippet: readme_map -->
```csharp
IEndpointConventionBuilder studio = sample.Path is { Length: > 0 } path
    ? app.MapDataStudio(path)
    : app.MapDataStudio();

if (sample.Anonymous)
{
    // --anonymous: what an unauthenticated studio looks like. The startup guard accepts this because it
    // is an answer; it refuses only a mapping that says nothing at all.
    studio.AllowAnonymous();
}
else
{
    studio.RequireAuthorization(SamplePolicies.Studio);
}
```
<!-- endSnippet -->


`sample.Anonymous`, `sample.ReadOnly` and `sample.Path` are the demo's own command-line switches; in your host they are just
values. `AddDataStudio()` may be called before or after your own `services.AddMarten()`: the scan that finds your stores
runs lazily, on first use, when the container is complete.

`app.MapDataStudio("/ops")` mounts the studio somewhere else, with each module's pages under a segment of its own
(`/ops/marten`, `/ops/postgres`, `/ops/kafka`). The pattern wins over `DataStudioOptions.Path`, and the pages, the static
assets, the Blazor circuit and the framework script all move with it.

### The opt-in health check

The [health advisor](screens.md) can also answer an ASP.NET Core health check, which is how the host's own
monitoring gets to alert on it. It is opt-in, and this block is compiled from the sample as well:

<!-- snippet: readme_health -->
```csharp
// Optional, and AddMarten() does not do it for you: the health advisor's configuration and catalog
// checks as one ASP.NET Core health check. It reports codes and counts, never a name, and runs the checks at
// most once per HealthInterval however often the endpoint is probed.
builder.Services.AddHealthChecks().AddMartenDiagnostics(tags: ["marten"]);
```
<!-- endSnippet -->

Map it the usual way, with `app.MapHealthChecks("/healthz")`. `AddMartenDiagnostics(name, failureStatus, tags,
timeout)` takes ASP.NET Core's usual arguments (`name` is `data-studio-marten` unless given; `AddPostgresDiagnostics(...)`
on the health-check builder takes the same, names its check `data-studio-postgres` unless given and reads the registered
data sources). Each run sweeps the
configuration and catalog checks over every registered store and the databases each one holds, at most 25 a
run, and reports `failureStatus` (`Unhealthy` unless given) on a Critical finding and `Degraded` on a Warning,
on a check that failed or ran out of time, and on a store that would not build or list its databases. Its data
is codes and counts only, never a tenant, a session, a table, a statement or a database name, because there is
no visitor to authorize. Concurrent probes share one run and the probes in between are answered from the
latest result, so an orchestrator probing every few seconds costs one run per `HealthInterval`. A host that
never calls it is unchanged. The details are in [`health.md`](health.md#export).

## Options

The shell's are `DataStudioOptions`, set in `AddDataStudio(studio => ...)`:

| Option | Default | |
|---|---|---|
| `Path` | `/studio` | Where the studio is mounted. A plain rooted URL path. `MapDataStudio(pattern)` overrides it. Each module's pages are under its own segment below it. |
| `Title` | `Data Studio` | Browser tab and sidebar heading. |
| `ApplicationPath` | `null` | Where the header's "Back to application" link goes; no link when `null`. A path that starts with a single `/` (`/`, `/app`; a host behind `UsePathBase` includes its base, `/myapp/`) or an absolute `http` or `https` URL without user information, used exactly as written and never re-rooted under the studio's mount path. Anything else fails startup. |
| `AuthorizationPolicy` | `null` | Policy applied to every studio endpoint - the front page, Activity, every module's pages, the circuit and the static assets. |
| `ReadOnly` | `false` | Master switch: every module's capabilities off, mutating controls not rendered, services refuse. A module has a switch of its own that turns only its own capabilities off. |
| `RefreshInterval` | 5 seconds | How often live pages poll. Paused while the tab is hidden. 1 second … 5 minutes. |

The Marten module's are every property of `MartenOptions`, set in `.AddMarten(marten => ...)`; the Postgres module's
(`PostgresOptions`) and the Kafka module's (`KafkaOptions`) are in [postgres.md](postgres.md) and
[kafka.md](kafka.md). Validation runs at startup: a value outside the range given here fails the host with a
message that names the option.

| Option | Default | |
|---|---|---|
| `StoreAuthorizationPolicy` | `null` | Policy evaluated against `MartenStoreResource` before a store, database or tenant is listed, framed or read. |
| `WriteAuthorizationPolicy` | `null` | Policy evaluated against `MartenStoreResource` with the capability named, before every mutation. Falls back to `StoreAuthorizationPolicy`. |
| `ReadOnly` | `false` | Turns this module's capabilities off, whatever `Capabilities` says: mutating controls not rendered, services refuse. `DataStudioOptions.ReadOnly` does the same for every module; this one leaves the others alone. |
| `Capabilities` | all off | Which mutating operations, and which reads authorized like writes (`RunSql`, `BrowseDatabase`, `ViewDatabaseActivity`, `RunLinqQueries`), are enabled. |
| `DefaultPageSize` | `50` | Rows per page when a list is first shown. 1 … `MaxPageSize`. |
| `MaxPageSize` | `500` | The largest page a user may pick. 1 … 5000. |
| `QueryTimeout` | 30 seconds | How long a studio query may run. It is a server-side `statement_timeout` where the studio owns a transaction — the SQL console, the Marten `where` clause, the recent-documents scan — and Npgsql's client-side `CommandTimeout` everywhere else. 1 second … 10 minutes. |
| `MaxStudioConnections` | 2 | The most connections the studio holds at a time of each pool it reads through - each of the application's own data sources. A read that finds all of them in use waits for one, at most `QueryTimeout`, and then says so; a nested read never waits for its own caller. The cap is never above the data source's own `Maximum Pool Size` (the smaller of the two, when `Pooling` is on), so a host never copies a pool's size into this option; a data source the connection history samples through keeps one more connection for it outside the cap, so a dedicated pool for both needs one more. The Health page's studio-cost line shows the cap in force and how many are in use, and C01 says how many of a saturated pool's connections were the studio's. 1 … 100. |
| `StudioApplicationName` | derived | The `application_name` the studio's sessions carry in `pg_stat_activity`, Database › Sessions and an external monitor: by default the application's own `Application Name` with the studio's after it in parentheses (`orders-api (Data Studio)`), or the studio's alone. Set for each of the studio's read-only transactions only (`set_config(…, true)`), so a pooled connection goes back to the application as it came; `""` leaves the application's own name. Printable ASCII, at most 63 characters. |
| `MaxInlineValueBytes` | 512 KiB | Documents larger than this are not fetched into list views and show as raw text in the detail view. 1 byte … 64 MiB. |
| `MaxSqlConsoleRows` | `500` | Row cap for the SQL console; the reader stops there rather than rewriting your statement. 1 … 10 000. |
| `SqlConsoleRole` | `null` | A Postgres role the SQL console **and the Marten `where` clause** switch to with `SET LOCAL ROLE` inside their read-only transaction — as do LINQ mode, the database browser and the health advisor's reads, except its statement checks, which read `pg_stat_statements` as the store's own role. Must be a plain identifier — and must be able to `select` from the document tables, or the Query page answers `42501`. |
| `ExactCountThreshold` | `100000` | Above this estimated row count, collection counts stay `pg_class.reltuples` estimates, prefixed `~`, instead of becoming `count(*)` — and asking for an exact count is **refused**, with the reason on the badge, rather than being paid for. A conjoined collection under a tenant scope is the exception: that count is `where tenant_id = …`, which Marten makes index-backed by putting `tenant_id` first in the primary key, so it is always counted exactly and this threshold never applies to it. |
| `HealthInterval` | 60 seconds | How often the health advisor re-checks a database while its Health page or the Overview is open. One check serves every open tab and circuit for that long, and nothing is checked while no page is open and nothing probes the opt-in health check, if you registered it (whose runs follow the same interval). 15 seconds … 1 hour, and never shorter than `DataStudioOptions.RefreshInterval`: a longer `RefreshInterval` wins. The deeper checks are cached for five minutes whatever it says. |
| `AcceptedChecks` | empty | Health checks the application accepts, by code with a reason (`options.AcceptedChecks["K05"] = "..."`): they still run and are shown, as accepted, but count nowhere - not in the Health page's state, the badge, the banners or the opt-in health check's status. Startup refuses an unknown code and a blank or over-long reason; an entry for a retired code (K10) is accepted and ignored, with one `Information` line (event 9490) saying it can be removed. Read once, when the options are validated at startup. See [health.md](health.md#accepting-a-check). |
| `RebuildShardTimeout` | 1 hour | The per-shard replay budget handed to Marten's rebuild. At least 1 minute — see the warning below. |
| `ProjectionThresholds` | empty | How far behind the high-water mark a projection may fall, in events, before the studio colours it amber and red, by projection name: `o.ProjectionThresholds["Trips"] = new(WarningBehind: 5_000, CriticalBehind: 50_000);` (`ProjectionLagThreshold`). It colours the Projections page and the Overview, draws the lines on a shard's chart and is what D04 judges a lag against, and it wins over numbers a host wrote into a progression row's `warning_behind_threshold` and `critical_behind_threshold` (Marten writes neither) and over the studio's own 1,000 and 100,000. It applies to **every store** by projection name alone; a name no projection of a store has is not an error, the Projections page's *Thresholds that apply* says so. Names are compared exactly. Startup refuses an entry whose numbers are not above zero or whose `CriticalBehind` is below its `WarningBehind`, naming the projection. |
| `IsDocumentTypeVisible` | `null` | `Func<Type, bool>` filter over the registered document types, applied in the data layer and not only in navigation. Marten's own `DeadLetterEvent` is asked too, and hiding it hides the dead-letter screen, its navigation entry and Overview tile, Discard and Rewind, and the advisor's D06 — so a delegate written as an allow-list of your own types hides the dead letters unless it answers `true` for `typeof(DeadLetterEvent)`. A delegate that throws for it is read as hiding it. It must answer from the type alone and the same way every time: a shape check's result and the Event flow's model are shared between visitors who resolve the same scope, so it hides types, not visitors (keep a visitor from a type with `StoreAuthorizationPolicy`; see [security.md](security.md#what-isdocumenttypevisible-hides)). |
| `IncludeAncillaryStores` | `true` | Whether stores registered with `AddMartenStore<T>()` appear beside the default `IDocumentStore`. |
| `KnownTenantIds` | empty | Tenant ids to offer in the selector. When non-empty, discovery is skipped. |
| `DiscoverTenantIds` | `true` | When `KnownTenantIds` is empty, whether to discover tenants from Marten's descriptors and then from a bounded query. |
| `BrowsableSchemas` | empty | Schemas beyond the store's own that the database browser may show, and whose non-Marten rows and definitions it may read (with `Capabilities.BrowseDatabase`; with `Capabilities.ViewDefinitions`, the schemas named exactly, their definitions alone). Exact schema names, or `"*"` for every schema the studio's role has `USAGE` on except Postgres' own and extensions'. It limits what the screens show, not what `RunSql` can read — `SqlConsoleRole` is the real boundary. |

> **`RebuildShardTimeout` is not a nicety.** A rebuild tears the projection's tables down *before* the
> timeout starts applying to the replay, so a rebuild that exceeds it stops with the tables already
> emptied and the operation recorded as failed. The default is an hour, where Marten's own no-timeout
> overload would have silently used five minutes. Set it generously.

## Multi-tenancy, multiple databases and multiple stores

Scope is a triple — **store, database, tenant** — chosen in the header and carried in the query string
(`?store=&db=&tenant=`), so a link you paste into a chat reopens the same thing. Every data call
resolves that scope and re-asks `StoreAuthorizationPolicy` about it.

- **Stores.** The default `IDocumentStore` is `default`; every store registered with
  `AddMartenStore<T>()` appears under its marker interface's name. `IncludeAncillaryStores = false`
  hides the ancillary ones. A store that will not build is shown as an unavailable row with the reason,
  not silently omitted.
- **Databases.** A `MultiTenantedDatabases` or otherwise multi-database store lists its databases;
  sessions are always opened with `SessionOptions.ForDatabase(tenantId, database)` so the reads come
  from the database the selector named and the audit entry recorded. Everything schema-related is per
  *database*, never per store.
- **Tenants.** Three discovery tiers, cheapest first: `KnownTenantIds` (you said so — nothing is
  discovered); what Marten itself lists for the database — Marten's own `DescribeDatabasesAsync`, which
  answers for a static configuration without touching the database, the tenants a master-table or sharded
  tenancy's database lists after the enumeration the studio has just made, without a second read of its
  lookup table, and on a single database with `Events.UseTenantPartitionedEvents` the tenants Marten has
  registered, including ones with no data yet; and, when `DiscoverTenantIds` allows it, a bounded
  `select distinct` over every table in the store that has a tenant column, capped at 201 rows and cached for
  a minute. Past 200 tenants the selector becomes a free-text box, because a picker that silently omits
  tenants is worse than no picker. The selector says which tier answered. A tenant-partitioned database lists
  only tenants registered in `mt_tenant_partitions`: a tenant whose rows are only in a conjoined document type
  that opts out of partitioning (`[DoNotPartition]` / `DisablePartitioningIfAny`) is not listed, and a link
  naming it is refused until the tenant is registered. Tenants shows the same tenants the selector offers, placed in
  their databases, with statistics only.

Conjoined tenancy is per document type, so a store whose documents are tenanted while its event store is
not is the normal case and is handled: a single-tenanted collection is not filtered by the scope's
tenant, because there is nothing to filter on and hiding it would hide something genuinely shared.

## Hosting notes

- **It works in an API-only host.** No `.razor` file of your own is needed, and you do not need to set
  `RequiresAspNetWebAssets` — `DataStudio`, which every module brings, references
  `Microsoft.AspNetCore.App.Internal.Assets`, whose `buildTransitive` targets define `_framework/blazor.web.js` as
  *your* project's static web asset, up to three package hops away.
- **The stylesheet and script are served from `{Path}/_content/DataStudio/`** (`css/studio.css`, `js/studio.js`): a
  Content-Security-Policy, WAF rule or proxy allow-list names that path.
- **You do not need `app.UseAntiforgery()`.** The studio has no `<form>` and no `<EditForm>`; every
  mutation is a Blazor event over the circuit, and SignalR's own same-origin check is what stands
  between a cross-site page and that circuit. The studio's component endpoints say
  `.DisableAntiforgery()`, so a host that never calls the middleware still gets a 200 for `GET /studio/marten`.
  A host that *does* call it is unaffected — the middleware simply has nothing to validate here.
- **Sub-path mounting is a first-class shape.** `MapDataStudio("/ops")` moves the pages, the
  `/_blazor` circuit, the `opaque-redirect` endpoint, `blazor.web.js` and the
  `_content/DataStudio/` mirror under the path, and the shell renders a studio-rooted `<base href>`.
  Every mount is studio-rooted — including the default `/studio` — so a host that has a Blazor app of
  its own does not end up with two endpoints on `/_blazor`.
- **.NET 11 and the hot-reload initializer.** A Debug build of the host with the .NET 11 SDK lists a hot-reload
  script among the page's Blazor initializers. The studio serves the host's whole `_framework` tree under its path, so
  that script loads and the page starts. In Development the script asks `/_framework/dotnet-browser-tools/hot-reload-settings.json`
  at the root: a host with a Blazor app of its own answers it from its own endpoint, and the studio's copy answers only where
  the host has none.
- **.NET 11 and a fail-closed `FallbackPolicy`.** ASP.NET Core 11 holds the async hub methods Blazor's circuit uses to the
  `FallbackPolicy` whatever the endpoint allows, so the circuit starts only for a visitor the `FallbackPolicy` admits; an
  `AllowAnonymous()` mapping under a fail-closed policy is the usual case
  ([dotnet/aspnetcore#69624](https://github.com/dotnet/aspnetcore/issues/69624), open). The studio does not work around
  it; until it is fixed, an anonymous mapping on .NET 11 needs an application without a fail-closed `FallbackPolicy`.
- **Navigation inside the studio is handled by its circuit.** A link, Back and a page's own address change do not
  fetch the page from the server.
- **Behind a reverse proxy**, forward the studio prefix and it works: the studio's plumbing is all under
  its own path. If the proxy strips a prefix, give the application the same prefix with
  `app.UsePathBase("/…")` so that the routes and the `<base href>` agree; mounting at
  `MapDataStudio("/ops")` and stripping `/ops` at the proxy without a matching `UsePathBase` is
  the shape that renders locally and 404s in production.
- **Static assets.** `app.MapStaticAssets()` (or `UseStaticFiles()`) serves the stylesheet and the
  studio's browser helpers the normal way. The studio also maps its own
  `_content/DataStudio/{**path}` endpoints as a fallback, so an API-only host that configures neither
  still gets a styled, interactive studio. Both are asked for by the studio's own pages and by nothing
  else: the package ships no Blazor JS initializer, so a host that runs a Blazor app of its own never
  fetches anything of the studio's on pages that have nothing to do with it. The document names an icon of its
  own, `_content/DataStudio/favicon.svg`, so the browser does not ask your site root for `/favicon.ico`.
- **A host's own Blazor Server options survive, in either order.** Registering the studio does not undo a
  `services.AddServerSideBlazor().AddHubOptions(...)` you made before it: the framework's own repeated hub-options
  registration is left out, so your `ClientTimeoutInterval` and `MaximumReceiveMessageSize` stay yours.
- **Not trimmable.** `IsTrimmable=false` is stated in the csproj on purpose. Blazor Server sets
  `[Parameter]` properties by name from the render tree and the document viewer deserializes into types
  discovered from `IDocumentType.DocumentType` at run time; a trimmer told it may cut has no way to see
  either. An application that publishes trimmed or native AOT does so without the studio.
- **There is no "integrate into my own Blazor app" mode.** The studio maps its own
  `MapRazorComponents<DataStudioApp>()` root and re-roots it. Adding its pages to your own router is
  not supported.
