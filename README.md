# Marten Studio

An embeddable Blazor Server admin UI for [MartenDB](https://martendb.io), shipped as an MIT-licensed
Razor Class Library built on [Postgres Studio](#two-packages). You add `AddMartenStudio()` to your container and
`MapMartenStudio()` to your endpoint pipeline, and the studio runs inside your own ASP.NET Core application against *that
application's own* configured `IDocumentStore` — browsing document collections and their JSON,
inspecting and steering event streams, projections and the async daemon, running guarded queries and
managing schema. Everything it knows, it knows because you configured Marten that way: aliases,
duplicated fields, soft delete, tenancy, projections and metadata columns all come from your
`StoreOptions`. That is the whole reason a Marten UI beats pgAdmin, and it is why the studio is keyed on
`IDocumentStore` and never on a connection string.

It is **not** a standalone application, **not** a fleet monitor and **not** a connection-string tool.
There is no "point it at a database" mode, and there never will be — a studio that could be aimed
anywhere would have to re-implement everything Marten already knows, and would get it subtly wrong. Both
packages are keyed on the databases the host registered, Marten's or not: Marten Studio on its stores, Postgres
Studio on its `NpgsqlDataSource`s, each with the application's own role, pool and TLS. If
you want production telemetry for a fleet of Marten services, that is
[CritterWatch](https://jasperfx.net)'s job; if you want a general SQL client, that is pgAdmin's. The
niche in between — *this* process, *this* store, with Marten's own metadata in front of you and every
mutating action off until you turn it on — is what Marten Studio is for.

Registering the studio must never change how your application behaves. No global styles, no
component-library version pinned onto your host, no middleware ordering requirement, no altered
authentication, no second projection daemon. A host that adds the package and never maps it is
byte-for-byte the application it was.

## Two packages

Since 0.5 the studio is two packages, released together at one version:

| Package | For | Register and map | Mounted at |
|---|---|---|---|
| `MartenStudio` | an application with a Marten `IDocumentStore` | `AddMartenStudio()`, `MapMartenStudio()` | `/marten` |
| `PostgresStudio` | any application that registers an `NpgsqlDataSource`, and no Marten | `AddPostgresStudio()`, `MapPostgresStudio()` | `/postgres` |

**Which to choose.** If the application uses Marten, take `MartenStudio`: it brings `PostgresStudio` with it, at
exactly its own version, and shows everything Postgres Studio shows for the store's databases - the database browser,
Sessions, the health advisor and its pages, the SQL console, Relationships - beside the Marten screens. Otherwise take
`PostgresStudio` alone: it references Npgsql and no Marten, JasperFx or Weasel package, and browses the application's
own data sources. This README is Marten Studio's; Postgres Studio's is
[`src/PostgresStudio/README.md`](src/PostgresStudio/README.md), with the long form in
[`docs/postgresstudio.md`](docs/postgresstudio.md).

**One studio per application.** `AddPostgresStudio()` throws in an application that called `AddMartenStudio()`, and
the reverse; each `Map*Studio()` names the other call to make. Marten Studio's database browser shows what else lives
in the store's own databases - a job store, a legacy schema - but a separate plain database of a Marten application
is not listed in 0.5.

**Upgrade both together.** The two assemblies are one program in two files, so `MartenStudio` depends on exactly its
own `PostgresStudio` (`[0.5.0]` in the nuspec). A central `PackageVersion` for `PostgresStudio`, if your repository has
one, must equal `MartenStudio`'s, or be removed. Under `CentralPackageTransitivePinningEnabled` a central pin at another
version makes NuGet drop `PostgresStudio` from the graph without a warning, so `AddMartenStudio()` checks at startup
and throws an `InvalidOperationException` that says which version to pin.

## Screenshots

| | |
|---|---|
| [![Overview, light](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/overview-light.png)](docs/screenshots/overview-light.png) | [![Overview, dark](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/overview-dark.png)](docs/screenshots/overview-dark.png) |
| Overview — one card per store, with tenancy, stream identity and the databases behind it | The same page in the dark theme, which follows the theme picker behind the header's gear button |
| [![Document list](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/documents-list.png)](docs/screenshots/documents-list.png) | [![Document detail](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/document-detail.png)](docs/screenshots/document-detail.png) |
| Documents — the collections rail, the search grammar and its index verdict | One document: JSON on the left, everything the row's metadata columns say on the right |
| [![Stream detail](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/stream-detail.png)](docs/screenshots/stream-detail.png) | [![Dead letters](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/dead-letters.png)](docs/screenshots/dead-letters.png) |
| A stream's timeline, each event's JSON and headers, with aggregate time travel beside it | Dead letters, expanded to the failing event and its exception |
| [![Projections](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/projections.png)](docs/screenshots/projections.png) | [![Schema drift](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/schema-drift.png)](docs/screenshots/schema-drift.png) |
| Projections and the async daemon: shard state, progression and the high-water mark | Schema — what Marten would change, before anything is applied |
| [![Relationships](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/relationships.png)](docs/screenshots/relationships.png) | [![Health](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/health.png)](docs/screenshots/health.png) |
| Relationships — the foreign keys `StoreOptions` declares, cross-checked against the ones Postgres holds | Health — findings by area, each with its evidence, how sure the advisor is and what to change |
| [![Database browser](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/database-browser.png)](docs/screenshots/database-browser.png) | [![Table rows](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/database-rows.png)](docs/screenshots/database-rows.png) |
| Database — every table, view, function, trigger, sequence and type beside the store, Marten's own told apart; here a Quartz.NET job store | A table's rows: a filter with its index verdict, keyset paging over a composite key, typed headers and "≈ date" hints |
| [![Row detail](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/database-row-detail.png)](docs/screenshots/database-row-detail.png) | [![Relationships over tables](https://raw.githubusercontent.com/lahma/marten-studio/main/docs/screenshots/relationships-quartz.png)](docs/screenshots/relationships-quartz.png) |
| One row: every column, what it points at and what points at it | Relationships over plain tables beside the document types |

## Install

```shell
dotnet add package MartenStudio
```

One package reference. The studio brings `Marten` (floor `9.39.0`, the first release with the fixes for
two critical LINQ injection advisories) and `PostgresStudio` at exactly its own version, which brings
`Npgsql` (floor `8.0.3`) and `Microsoft.AspNetCore.App.Internal.Assets` (floor `10.0.12`, three `.js` files and an
MSBuild target, no runtime assembly), and it takes everything else — Blazor, SignalR, authorization, endpoint
routing — from the shared framework, so it pins no ASP.NET Core patch onto your application. Target framework:
`net10.0`. Your application's Npgsql stays the one Marten brings, which is never below Postgres Studio's floor;
Marten Studio is tested at the Marten floor and at Marten 9.54.2, and not on Npgsql 10, which no tested Marten brings.

**PostgreSQL 14 to 18** are supported, and every release is tested on each. 14 reaches the community's end of life on
2026-11-12, and 19 is supported from its general availability. Below 14 the studio still reads the server, with no
promise. A health check that needs more privilege than the reading role has says "not checked", which grant it needs
and who can give it on your platform. See [`docs/postgres-versions.md`](docs/postgres-versions.md), and, for Amazon
Aurora, [`docs/aurora.md`](docs/aurora.md).

## Register and mount

The minimum is two calls, and a decision about who may open it:

```csharp
builder.Services.AddMartenStudio();

// ...

app.MapMartenStudio().RequireAuthorization();
```

The studio is then at `/marten`, read-only, behind whatever `RequireAuthorization()` means in your
application. The sample host does the same thing with every option spelled out — this block is compiled
from [`samples/MartenStudio.Sample/Program.cs`](samples/MartenStudio.Sample/Program.cs), so it cannot
drift:

<!-- snippet: readme_register -->
```csharp
builder.Services
    .AddMarten(options => SampleStore.Configure(options, connectionString))
    .UseLightweightSessions()
    // --no-daemon: Disabled registers no coordinator, which is a host whose projections run elsewhere.
    .AddAsyncDaemon(sample.NoDaemon ? DaemonMode.Disabled : DaemonMode.Solo)
    .InitializeWith<SampleDataSeeder>();

builder.Services.AddMartenStudio(options =>
{
    // Who gets in, and who may change anything: the studio evaluates both against the host's own
    // authorization, and never authenticates anybody itself.
    //
    // --anonymous leaves AuthorizationPolicy unset on purpose. AllowAnonymous() on the mapping does win
    // over a configured policy - the studio's endpoints carry both and AllowAnonymous is what the
    // authorization middleware honours - but saying "this policy governs the studio" and then "anyone
    // may open it" in the same application is two answers to one question, and a demo should show the
    // one it means.
    options.AuthorizationPolicy = sample.Anonymous ? null : SamplePolicies.Studio;
    options.WriteAuthorizationPolicy = SamplePolicies.StudioWrite;

    // Every mutating operation is off until the host says otherwise. `All()` is the one-line, greppable
    // opt-in; `ReadOnly` overrides it whatever it says.
    options.Capabilities = sample.ReadOnly ? new MartenStudioCapabilities() : MartenStudioCapabilities.All();
    options.ReadOnly = sample.ReadOnly;

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
    ? app.MapMartenStudio(path)
    : app.MapMartenStudio();

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

`sample.Anonymous`, `sample.ReadOnly` and `sample.Path` are the demo's own command-line switches; in
your host they are just values. `AddMartenStudio()` may be called before or after `AddMarten()` — the
scan that finds your stores runs lazily, on first use, when the container is complete.

`MapMartenStudio("/ops/marten")` mounts the studio somewhere else. The pattern wins over
`MartenStudioOptions.Path`, and the pages, the static assets, the Blazor circuit and the framework
script all move with it.

### The opt-in health check

The [health advisor](#screens) can also answer an ASP.NET Core health check, which is how the host's own
monitoring gets to alert on it. It is opt-in, and this block is compiled from the sample as well:

<!-- snippet: readme_health -->
```csharp
// Optional, and AddMartenStudio() does not do it for you: the health advisor's configuration and catalog
// checks as one ASP.NET Core health check. It reports codes and counts, never a name, and runs the checks at
// most once per HealthInterval however often the endpoint is probed.
builder.Services.AddHealthChecks().AddMartenStudio(tags: ["marten"]);
```
<!-- endSnippet -->

Map it the usual way, with `app.MapHealthChecks("/healthz")`. `AddMartenStudio(name, failureStatus, tags,
timeout)` takes ASP.NET Core's usual arguments (`name` is `marten-studio` unless given). Each run sweeps the
configuration and catalog checks over every registered store and the databases each one holds, at most 25 a
run, and reports `failureStatus` (`Unhealthy` unless given) on a Critical finding and `Degraded` on a Warning,
on a check that failed or ran out of time, and on a store that would not build or list its databases. Its data
is codes and counts only, never a tenant, a session, a table, a statement or a database name, because there is
no visitor to authorize. Concurrent probes share one run and the probes in between are answered from the
latest result, so an orchestrator probing every few seconds costs one run per `HealthInterval`. A host that
never calls it is unchanged. The details are in [`docs/health.md`](docs/health.md#export).

## Authorization

There are three layers, and two process-wide switches on top of them. All five are yours to configure;
the studio authenticates nobody.

1. **`AuthorizationPolicy`** — *who gets in*. Applied to every studio endpoint: the pages, the Blazor
   circuit and the static assets. `RequireAuthorization()` on the builder returned by
   `MapMartenStudio()` does the same job, and covers the pages and the circuit.
2. **`StoreAuthorizationPolicy`** — *which store, database and tenant*. Resource-based, evaluated
   against a `MartenStoreResource`, and asked on **every** data call rather than only at the scope
   selector. Listings are filtered rather than annotated: a tenant you may not see is one you never
   learn exists.
3. **`WriteAuthorizationPolicy`** — *each mutating call*, against the same resource with the capability
   name attached. Falls back to `StoreAuthorizationPolicy` when you configure only one.

On top: **`ReadOnly`** turns every capability off whatever `Capabilities` says, and **`Capabilities`**
decides which mutating operations exist at all.

### The startup guard

A mapping that answers none of this refuses to start, before the web host binds a listener:

> Marten Studio is mapped with no authorization. Its pages can read and edit every document in every
> Marten store in this process, archive event streams, control the projection daemon, apply schema
> changes and run SQL, so Marten Studio refuses to start rather than serve that by accident. Say which
> you meant:
>
> - `app.MapMartenStudio().RequireAuthorization()` authorizes its pages and its Blazor circuit;
> - `services.AddMartenStudio(options => options.AuthorizationPolicy = "...")` authorizes those and the
>   static assets with them;
> - `app.MapMartenStudio().AllowAnonymous()` serves it to anyone, deliberately.
>
> A non-null `AuthorizationOptions.FallbackPolicy` satisfies this too, since it covers every endpoint
> that states nothing.

"I forgot to add `RequireAuthorization`" must not be a silent state, so it is a startup failure instead.

### What protects the circuit

Both `MartenStudioOptions.AuthorizationPolicy` and `RequireAuthorization()` on the returned builder
cover the Blazor circuit as well as the pages — the builder holds the studio's page endpoints *and* its
`/_blazor` endpoints, so a statement about the studio is a statement about both. That matters: the
circuit is the dangerous half, because it is the half that runs components. The studio stamps no
`AllowAnonymous` of its own on `/_blazor`.

The static assets under `_content/PostgresStudio/` (both studios' shell is Postgres Studio's) are package content
— CSS and a little JS. With a
studio policy configured they carry it too; without one they opt out explicitly, so an application with
a fail-closed `FallbackPolicy` can still load the stylesheet.

### Resource-based authorization

`MartenStoreResource` is what the two store policies are evaluated against:

```csharp
public sealed record MartenStoreResource(
    string StoreName,           // "default", or the marker interface name of an ancillary store
    string DatabaseIdentifier,  // Marten's database identity - never a connection string
    string? TenantId,           // the selected tenant, or null for a view spanning all of them
    string? Capability);        // "EditDocuments", "RunSql", ... - or null for a read
```

One handler answers for the scope selector, the page frame and every data call. This example is
*illustrative* — it is not compiled from the sample, which uses plain claim policies:

```csharp
// Illustrative, not compiled.
public sealed class MartenStudioRequirement : IAuthorizationRequirement;

public sealed class MartenStudioHandler
    : AuthorizationHandler<MartenStudioRequirement, MartenStoreResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MartenStudioRequirement requirement,
        MartenStoreResource resource)
    {
        // Reads: the tenant has to be one this user owns - and a resource with no tenant is every
        // tenant at once, so it takes an explicit all-tenants claim rather than passing for everyone.
        bool mayRead = resource.TenantId is null
            ? context.User.HasClaim("tenant", "*")
            : context.User.HasClaim("tenant", resource.TenantId);

        // Writes: the capability name arrives on the resource, so one handler can be as coarse or as
        // fine as you like - "ops may rebuild, nobody but me may run SQL".
        bool mayWrite = resource.Capability switch
        {
            null => true,
            "RunSql" or "BrowseDatabase" or "ViewDatabaseActivity" or "RunLinqQueries"
                => context.User.IsInRole("dba"),
            _ => context.User.IsInRole("ops"),
        };

        if (mayRead && mayWrite)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
```

A `null` tenant is not "no tenant to check" but every tenant at once — the all-tenants view, and every
`BrowseDatabase` read, every `ViewDatabaseActivity` read, every SQL console run and every operation that
reaches a whole shared database (a dead-letter rewind, a progression correction, a rebuild, a schema
apply), which are always asked with `TenantId = null` because nothing narrows them to a tenant — so a
handler that let it through for everybody would hand a user scoped to one tenant all of them.

Register it the usual way and name the policy:

```csharp
// Illustrative, not compiled.
builder.Services.AddSingleton<IAuthorizationHandler, MartenStudioHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("MartenStudioStore", policy => policy.AddRequirements(new MartenStudioRequirement()));

builder.Services.AddMartenStudio(options =>
{
    options.AuthorizationPolicy = "MartenStudio";
    options.StoreAuthorizationPolicy = "MartenStudioStore";
    options.WriteAuthorizationPolicy = "MartenStudioStore";
});
```

With no store policy configured, nothing is asked and everything passes — which is what keeps an
application that never set the option exactly as it was.

## Capabilities

Thirteen booleans, **all `false` by default** and all turned on by `MartenStudioCapabilities.All()`. A freshly
mapped studio is a read-only browser of Marten's own data.

| Capability | What it allows |
|---|---|
| `EditDocuments` | Edit a document's JSON and save it through the store's own serializer and session |
| `DeleteDocuments` | Delete, soft-delete and undelete documents, one at a time or a selection |
| `ArchiveStreams` | Archive an event stream. On an event store that is not conjoined, a stream that also holds another tenant's events needs the write policy for the database as a whole |
| `ManageDeadLetters` | Discard dead-letter records, mark events as skipped, and rewind a subscription to the event that failed (a rewind reaches every tenant in the database, so it is asked with `TenantId = null` unless the database holds only the selected tenant). On a store with `Events.UseTenantPartitionedEvents`, below Marten 9.51 Skip event and Rewind subscription are refused, because Marten skips and rewinds by a number every tenant counts on its own; from 9.51 each acts on the dead letter's tenant alone and is asked for that tenant, and a rewind is offered only where the daemon in this process runs that tenant's agent |
| `ControlDaemon` | Pause and resume the async daemon hosted in this process, start and stop individual projection agents while it is paused, and restart the high-water agent |
| `RebuildProjections` | Rebuild a projection |
| `CorrectProgression` | Advance the high-water mark, or correct projection progression in the database |
| `ApplySchemaChanges` | Apply pending schema migrations |
| `RunSql` | Run read-only SQL from the Query page's console. Authorized like a write and always with `TenantId = null` — the console is not limited to a tenant — which also governs the Mode A `EXPLAIN` and the subquery lift |
| `BrowseDatabase` | Browse the rows and definitions of non-Marten tables, views, functions, triggers, sequences and types in the schemas `BrowsableSchemas` names. A read, authorized like `RunSql` as a write, and always with `TenantId = null` |
| `ViewDatabaseActivity` | See who and what is using the database: other sessions' process ids, roles, client addresses, application names and statements, on the Health page, on Database › Sessions and in the Projections screen's high-water Explain, and run the health advisor's statement checks over `pg_stat_statements`. Without it the counts, ages and states are still shown. A read, authorized like `RunSql` as a write, and always with `TenantId = null`. Off by default, in `All()` |
| `CompactStreams` | Compact an event stream from Stream detail: Marten's own `CompactStreamAsync<T>` replaces its events up to a version with a snapshot of its aggregate, and the folded events are deleted. Authorized for the scope as selected, tenant included - and, on an event store that is not conjoined, for the database as a whole when the stream also holds an event another tenant's session appended. Off by default, in `All()` — see [`docs/compaction.md`](docs/compaction.md) |
| `RunLinqQueries` | Run LINQ mode on the Query page (Marten 9.48+): a typed predicate parsed in this process and applied to Marten's own `Query<T>()`. A read, authorized like a write — with the tenant for a conjoined collection, with `TenantId = null` for one not divided by tenant and for the plan. Off by default, in `All()` — see [`docs/linq-mode.md`](docs/linq-mode.md) |

```csharp
options.Capabilities = MartenStudioCapabilities.All();   // everything: the one-line, greppable opt-in
options.Capabilities.RebuildProjections = true;          // or one switch at a time, from all-off
options.ReadOnly = true;                                 // and this overrides whichever you wrote
```

Every disabled control says the exact property you would set, spelled the way you would write it:

> Editing documents is not enabled. Set `MartenStudioOptions.Capabilities.EditDocuments` to `true`, or
> `MartenStudioOptions.Capabilities = MartenStudioCapabilities.All()` to enable all of them.

and with the master switch on:

> `MartenStudioOptions.ReadOnly` is `true`, which turns every capability off: the mutating operations,
> and the reads authorized like writes (`RunSql`, `BrowseDatabase`, `ViewDatabaseActivity` and
> `RunLinqQueries`).

Hiding a button is a convenience. The refusal itself lives in the service layer, where the operation
does, because a Blazor circuit is a long-lived object a client can drive — and every refusal is written
to the audit log just like a success.

## Options

Every property of `MartenStudioOptions`. Validation runs at startup: a value outside the range given here
fails the host with a message that names the option.

| Option | Default | |
|---|---|---|
| `Path` | `/marten` | Where the studio is mounted. A plain rooted URL path. `MapMartenStudio(pattern)` overrides it. |
| `Title` | `Marten Studio` | Browser tab and sidebar heading. |
| `AuthorizationPolicy` | `null` | Policy applied to every studio endpoint — pages, circuit and static assets. |
| `StoreAuthorizationPolicy` | `null` | Policy evaluated against `MartenStoreResource` before a store, database or tenant is listed, framed or read. |
| `WriteAuthorizationPolicy` | `null` | Policy evaluated against `MartenStoreResource` with the capability named, before every mutation. Falls back to `StoreAuthorizationPolicy`. |
| `ReadOnly` | `false` | Master switch: every capability off, mutating controls not rendered, services refuse. |
| `Capabilities` | all off | Which mutating operations, and which reads authorized like writes (`RunSql`, `BrowseDatabase`, `ViewDatabaseActivity`, `RunLinqQueries`), are enabled. |
| `DefaultPageSize` | `50` | Rows per page when a list is first shown. 1 … `MaxPageSize`. |
| `MaxPageSize` | `500` | The largest page a user may pick. 1 … 5000. |
| `QueryTimeout` | 30 seconds | How long a studio query may run. It is a server-side `statement_timeout` where the studio owns a transaction — the SQL console, the Marten `where` clause, the recent-documents scan — and Npgsql's client-side `CommandTimeout` everywhere else. 1 second … 10 minutes. |
| `MaxInlineDocumentBytes` | 512 KiB | Documents larger than this are not fetched into list views and show as raw text in the detail view. 1 byte … 64 MiB. |
| `MaxSqlConsoleRows` | `500` | Row cap for the SQL console; the reader stops there rather than rewriting your statement. 1 … 10 000. |
| `SqlConsoleRole` | `null` | A Postgres role the SQL console **and the Marten `where` clause** switch to with `SET LOCAL ROLE` inside their read-only transaction — as do LINQ mode, the database browser and the health advisor's reads, except its statement checks, which read `pg_stat_statements` as the store's own role. Must be a plain identifier — and must be able to `select` from the document tables, or the Query page answers `42501`. |
| `ExactCountThreshold` | `100000` | Above this estimated row count, collection counts stay `pg_class.reltuples` estimates, prefixed `~`, instead of becoming `count(*)` — and asking for an exact count is **refused**, with the reason on the badge, rather than being paid for. A conjoined collection under a tenant scope is the exception: that count is `where tenant_id = …`, which Marten makes index-backed by putting `tenant_id` first in the primary key, so it is always counted exactly and this threshold never applies to it. |
| `RefreshInterval` | 5 seconds | How often live pages poll. Paused while the tab is hidden. 1 second … 5 minutes. |
| `HealthInterval` | 60 seconds | How often the health advisor re-checks a database while its Health page or the Overview is open. One check serves every open tab and circuit for that long, and nothing is checked while no page is open and nothing probes the opt-in health check, if you registered it (whose runs follow the same interval). 15 seconds … 1 hour, and never shorter than `RefreshInterval`: a longer `RefreshInterval` wins. The deeper checks are cached for five minutes whatever it says. |
| `RebuildShardTimeout` | 1 hour | The per-shard replay budget handed to Marten's rebuild. At least 1 minute — see the warning below. |
| `IsDocumentTypeVisible` | `null` | `Func<Type, bool>` filter over the registered document types, applied in the data layer and not only in navigation. Marten's own `DeadLetterEvent` is asked too, and hiding it hides the dead-letter screen, its navigation entry and Overview tile, Discard and Rewind, and the advisor's D06 — so a delegate written as an allow-list of your own types hides the dead letters unless it answers `true` for `typeof(DeadLetterEvent)`. A delegate that throws for it is read as hiding it. |
| `IncludeAncillaryStores` | `true` | Whether stores registered with `AddMartenStore<T>()` appear beside the default `IDocumentStore`. |
| `KnownTenantIds` | empty | Tenant ids to offer in the selector. When non-empty, discovery is skipped. |
| `DiscoverTenantIds` | `true` | When `KnownTenantIds` is empty, whether to discover tenants from Marten's descriptors and then from a bounded query. |
| `BrowsableSchemas` | empty | Schemas beyond the store's own that the database browser may show, and whose non-Marten rows and definitions it may read (with `Capabilities.BrowseDatabase`). Exact schema names, or `"*"` for every schema the studio's role has `USAGE` on except Postgres' own and extensions'. It limits what the screens show, not what `RunSql` can read — `SqlConsoleRole` is the real boundary. |

> **`RebuildShardTimeout` is not a nicety.** A rebuild tears the projection's tables down *before* the
> timeout starts applying to the replay, so a rebuild that exceeds it stops with the tables already
> emptied and the operation recorded as failed. The default is an hour, where Marten's own no-timeout
> overload would have silently used five minutes. Set it generously.

## Screens

**Overview** — one card per registered store: databases, document types, event types, the PostgreSQL
version, tenancy, stream identity, event append mode and the schemas in play. A store that will not
build gets its own error region rather than blanking the page for the ones that do. A Health tile and the
three worst findings of the selected database sit beside the store cards.

**Health** — the health advisor, at `/marten/health` (the long form is
[`docs/health.md`](docs/health.md), and every code, its variants and what its subject names are in
[`docs/health/codes.md`](docs/health/codes.md)): checks that read Marten's configuration and the database and say what
is wrong, how sure they are, and which `StoreOptions` line would change it — for a table the store declares a remedy
is never DDL, and one that needs a newer Marten says so. The table checks judge every table in the store's schemas,
and in the schemas `BrowsableSchemas` admits while `Capabilities.BrowseDatabase` is on, so a job store or a legacy
schema beside the store is judged too: its remedies are migration SQL, shown "for your own migrations - the studio
never runs this", and a recognised library's table is left out of the index-design rules. A finding about a table
outside the store's own schemas is shown only to a visitor the database browser's gate admits to that schema, and the
navigation badge never counts it. They are grouped by area: the daemon and the high-water mark (D),
events (E), tables and indexes (T), connections and sessions (C), replication (R), the server (S) and the
configuration (K). Each finding carries its evidence and when it was measured, Show SQL, a caveat such as
"counters since `stats_reset` at 09:14", a link to its section of the docs, and "mute in this browser". A
sweep runs only while a Health page or the Overview is open, or when the [opt-in health
check](#the-opt-in-health-check) is probed, at most once per `HealthInterval` per database however many
tabs watch, in the studio's read-only transaction, without DDL and without a lock beyond what a read
takes; **Run deeper checks** reads a bounded slice of the data, and **Run statement checks** reads
`pg_stat_statements` behind `ViewDatabaseActivity`. A database-wide number needs the store policy for the
database as a whole, and anything that names a session, a role, an address, a statement or a tenant needs
`ViewDatabaseActivity`. The navigation entry carries a severity badge, a toast says when a Critical
finding appears, and findings also show where they belong: D01 and D02 as Projections banners, index marks
on Schema › Indexes, E05 on Event types. **Database › Sessions** (`/marten/database/sessions`) lists this
database's sessions, locks and prepared transactions under the same two gates, labels a lock by the
store's own table it belongs to, counts sessions by application beside the server's connection budget, and has no
cancel or terminate button. The Health page's **Snapshot** menu views, copies or downloads what your pages show of
the database as JSON, built from your own view of each page.

**Statements** (Operations, `/marten/statements`) — where the database's time goes, statement by statement, grouped
by Marten operation and document type, behind `ViewDatabaseActivity`.

**Maintenance** (Operations, `/marten/maintenance`) — what autovacuum will do next and why: each table against the
triggers autovacuum itself uses, what is running now, the workers, the freeze forecast and the vacuum horizon, with an
on-demand bloat measure; see [`docs/health/tables.md`](docs/health/tables.md#the-maintenance-page).

**Storage** (Operations, `/marten/storage`) — how big the database is, how fast it grows and which tables hold the
space, from the catalog's own page counts (no size function over a table, nothing locked); the exact size of one table
is on its page in the database browser.

**Replication** (Operations, `/marten/replication`) — slots, the standbys streaming from the server or a standby's own
replay, the archiver, WAL volume, checkpoints, I/O by backend type and tablespaces; names behind
`ViewDatabaseActivity`.

**Server** (System, `/marten/server`) — the server's version, platform and uptime, what the studio's reading role can
see grant by grant, a curated view of its settings, the extensions it uses and what stands in their way, and the
security posture.

> **Seeing replication lag.** On a primary, `pg_stat_replication` shows a standby's positions and lag only to a role
> with `pg_read_all_stats`, which `pg_monitor` includes. Any other role sees a sender's process id, role and application
> name. R03 and the Replication page then say "not checked", naming the grant and who can give it on your platform: a
> superuser when self-hosted, the master user on RDS and Aurora, the server admin on Azure, a `cloudsqlsuperuser` member
> on Cloud SQL. `pg_monitor` also lets the archiver's evidence count the WAL files waiting in `archive_status`, and the
> size of `pg_wal`. Without any grant, a data source that reaches a standby (a multi-host data source) measures lag
> there instead: what the standby has received less what it has replayed is exact and readable by any role, and so is
> whether replay is paused. A physical slot's `restart_lsn` is no substitute: it tracks flush, not replay.

**Documents** — a collections rail with per-collection counts (estimates, prefixed `~`; below
`ExactCountThreshold` the `=` beside a collection pays for an exact `count(*)`, and above it the studio
declines and says so on the badge), then a list with column chooser, keyset paging and a search
grammar (`field:value`, `>=`, `is:deleted`, `tenant:…`) that compiles to parameterised SQL against the
duplicated columns and JSON paths Marten actually created. Each search gets an index verdict —
green/amber/red with the `StoreOptions` line that would fix it — and a "Show SQL" disclosure with the
statement that ran. The detail view puts the JSON viewer (tree or raw, search, copy, download, "copy as
C# record") beside a metadata panel of the row's real columns. With `EditDocuments` you edit the JSON
in place and see a **round-trip diff** before saving: Marten has no untyped write path, so the edit is
deserialized into the CLR type and serialized back, and any property the type does not have is *gone* —
the dialog lists dropped, changed and added properties and refuses the save until you acknowledge the
drops. Saves are optimistically concurrent (`UpdateExpectedVersion` / `UpdateRevision`); a row that
moved comes back as a conflict with nothing written. With `DeleteDocuments` you get delete (soft where
the mapping says soft, hard otherwise), undelete, and a bulk delete over the selection.

**Database** — everything else in the store's Postgres database, for a host that keeps a Quartz.NET job
store, a legacy schema or another library's tables beside Marten. A schema rail, and kind tabs for
tables, views, functions, triggers, sequences and types; Marten's own objects are quieter, sorted last and
linked to where their data is really read, and a Quartz.NET, Wolverine, EF Core, Hangfire or Flyway object
is hinted by name. Each object has a detail page — columns, keys and indexes, foreign keys both ways,
triggers, a view's query, and its neighbourhood in the relationships diagram — and a table, view or
materialized view that is not Marten's has a **Rows** tab: a `col = value` / `col ~ text` / `col is:null`
filter with an index verdict (and "Run anyway" before an unindexed read of a large table), keyset paging
over composite keys, typed and sortable headers, JSON and `bytea` cells opening in the viewer, a "≈ date"
hint under `bigint` columns holding .NET ticks or epoch times, and row detail that follows foreign keys
both ways. The structure of the store's own schemas is shown to every visitor, as the Schema screen always
did; everything else — other schemas, definitions of what Marten does not own, and every row — needs
`Capabilities.BrowseDatabase`, a schema `BrowsableSchemas` names (or `"*"`), and the write policy asked
with `TenantId = null`, because nothing filters those tables by tenant. It is read-only, runs in the same
read-only transaction as the SQL console as `SqlConsoleRole`, locks nothing it lists, and audits the first
read of every relation. It never reads the rows of Marten's own document or event tables (they are read in
Documents and Events, where tenancy, soft delete and the serializer apply), never reads a foreign table,
directly or through a view, never shows a hidden document type or anything built on it, and masks the name
of a schema the visitor may not see as `‹withheld›` wherever Postgres prints it. A `vector`, `halfvec` or
`sparsevec` value is shown collapsed to its type and first components, with Expand and Copy.

**Relationships** — the document types of the store as a graph: one node per registered type with its
collection colour, its .NET type and a `reltuples` estimate, one arrow per foreign key. What
`StoreOptions` declares and what `pg_constraint` actually holds are cross-checked, so an arrow is
*declared and enforced*, *declared only* — configured, never applied, nothing enforcing it — or *in the
database only*, which Marten does not know about and an apply under `CreateOrUpdate` would drop. Keys
into the database's other tables are drawn too, as square, schema-coloured nodes — a Documents / Tables /
Both switch and schema chips narrow the picture, composite-key labels leave out the leading columns both
primary keys share, and a `NOT VALID` key is dashed — while a key into a schema the visitor may not see is
counted, never named, and a type hidden by `IsDocumentTypeVisible` appears nowhere at all. Clicking a node opens the collection; hovering an arrow
names the column, the target and the delete action. Beside the picture — and, on a narrow screen,
instead of it — a table carries the same relationships with the same links, which is also what a screen
reader gets. There is no pan, no zoom and no JavaScript: the layout is computed on the server and is a
pure function of the configuration, so the same store draws the same diagram every time. On a document's
detail page, **Referenced by** names each collection that points at it and how many of its documents do
— exact up to a thousand and "1,000+" beyond that, so the answer never costs a sequential scan — each row
linking to that collection already filtered on this document.

**Query** — three modes, and the asymmetry between the first two is the design. *Mode A, a Marten
`where` clause*, is a read against one document type and needs no capability, so the service holds it to
being a *clause*: no `;`, no statement of its own, brackets that balance, a tail that is
`[order by …] [limit n] [offset n]` and nothing else, no function that reads, writes or waits outside
the row, no `::regclass` cast — and, without `RunSql`, no subquery, union, join, `lateral` or
`returning` either. It runs inside the same `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK` as the
console, under the same `QueryTimeout` and the same optional `SqlConsoleRole`. Results come back as the
`data` column itself, never round-tripped through a CLR type, the composed statement is shown, and a plan
is offered where `RunSql` allows one (`EXPLAIN`, never `EXPLAIN ANALYZE`). *Mode B, the SQL console*, is arbitrary
read-only SQL against the whole database, is gated on `RunSql`, and is limited by `MaxSqlConsoleRows`,
`QueryTimeout`, a 3-second lock timeout, a 60-second idle-in-transaction timeout and an optional
`SqlConsoleRole` — inside `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK`. The row cap stops the reader;
it never appends a `LIMIT` to your statement. See [Security model and limits](#security-model-and-limits).
*LINQ mode* ([`docs/linq-mode.md`](docs/linq-mode.md)), on Marten 9.48+ and gated on `RunLinqQueries`,
takes the predicate your code would write (`Status == "Open" && Total > 100`) and an ordering, parses them
with JasperFx's Dynamic LINQ under its allow-list and the studio's own, and applies them to Marten's own
`Query<T>()` on a session pinned to the scope, so tenancy, soft deletes and hierarchies are Marten's. It
shows the rows, the SQL Marten built, its plan (`EXPLAIN`, never `ANALYZE`) and index advice with the
`StoreOptions` line that would declare the index. It builds Marten's statement without `ToCommand`, so it
never creates a table, and it runs in the same read-only transaction as the console.

**Events** — *Streams* lists and filters streams; *stream detail* draws the timeline with each event's
JSON, headers and metadata, offers aggregate **time travel** (replayed on demand by Marten's own
aggregator over the events up to a version, writing nothing and running no projection; the studio's own
read of the events creates and migrates nothing, though an `Apply` method that loads data through the
session it is handed is the host's code and reads as the host wrote it), and archives the stream with
`ArchiveStreams`. With `CompactStreams` it **compacts** a long stream: Marten's own
`CompactStreamAsync<T>`, on a session pinned to the stream's tenant and database, folds the events up to
the version the dialog shows into one snapshot and deletes them, behind a typed confirmation that says so
(see [`docs/compaction.md`](docs/compaction.md)). *Streams* also finds a stream by its aggregate's
**natural key**, within the stream's own tenant. *Feed* is the global sequence, newest first, filtered by
event type, sequence range, stream, tenant, a **DCB tag** (`tag:<type>=<value>`, with its index verdict)
and the archived/skipped flags the store actually has, with a follow mode; each event's tags are chips
that link to that filter. *Event types* counts what the store has seen and lists the tag types with each
table's key. *Dead letters* lists the daemon's failures with the exception, and with `ManageDeadLetters`
discards a record, marks the underlying event as skipped — only on a store that enabled event skipping,
because it writes `mt_events.is_skipped` and a store without that column has no way to express the idea —
or rewinds the projection's subscription to the event that failed, behind a typed confirmation that states
what gets replayed, where the daemon is hosted in this process.

**Projections** — every registered projection with its shards, their state and progression against the
high-water mark, refreshed from a lazy observer on `ShardStateTracker` where the daemon is hosted in
this process and from polling where it is not. With the capabilities on: pause and resume the projection
daemon, restart the high-water agent (`ControlDaemon`), rebuild a projection behind a typed confirmation
(`RebuildProjections`), and advance or correct progression (`CorrectProgression`). Pausing is
process-wide — it stops the coordinator's leadership runner and then every agent of every database this
process hosts for that store — and it is the only stop that holds: with the coordinator running, it
restarts any agent it finds missing within `LeadershipPollingTime`, so per-agent Start and Stop are
offered only while the daemon is paused and are refused with that reason while it is not. A rebuild
outlives the circuit that started it and is tracked by name. A composite projection's members, whose
progression rows its own shard advances in one batch, are listed under it and offer no Start or Stop of their
own. Where no daemon is hosted in this process,
the page says so — that is a value, not an error, and the studio never starts a daemon of its own. Each
shard also shows when it last progressed and a trend with an estimated catch-up, a rebuild in progress is a
state on its row, Pause, Stop and Rebuild quote the store's `StopAndDrainTimeout`, and below Marten 9.50
Start warns that a rebuild started outside the studio cannot be seen.

**Schema** — five tabs: *Drift* (what Marten would change), *Tables*, *Indexes*, *Functions* and *DDL*.
*Tables* shows each table's TOAST size, dead and HOT percentages, last autovacuum and storage parameters
(with drift against the configuration on Marten 9.47+), and names each pgvector column with its
dimensions; *Indexes* marks invalid, redundant and unused indexes from the health advisor's last check and
says what an HNSW or IVFFlat index's operator class and parameters mean. Nothing here executes DDL on a
read path: schema names, declared indexes, managed tables and installed functions come from `StoreOptions`
and Marten's own feature schemas, and `Check`, `Preview` and `DDL` sit behind buttons that say on the
button what they may create. What those three show spans the whole database — every tenant's partition by
name, every document type's table — so they answer only a visitor the store policy allows for the database
with no tenant selected, and, while `IsDocumentTypeVisible` hides a document type, one past the database
browser's gate (`Capabilities.BrowseDatabase` and the write policy). An apply migrates the whole database,
every tenant, whichever tenant is selected, so both policies are asked about the database as a whole.
*Tables* says whose each table is by the database browser's rules, rolls partitions into their parent
without listing them, leaves hidden document types out, and shows a partition count or a Marten tenancy
table's row count only past that gate, because either is the number of tenants. *Functions* reads a body
when you open its row: Marten's own for everybody, the application's own only with
`Capabilities.BrowseDatabase` and a schema `BrowsableSchemas` admits. A name in a schema you may not see
reads `‹withheld›`, as in the browser. If a registered store cannot be built, the tabs keep what the other
stores declare and withhold, and say, what that store could own. The reads give up after three seconds
behind a migration's lock rather than wait for it. A known limit: *Tables* and *Indexes* list the store's
own schemas only, so a table handed to `StoreOptions.Storage.ExtendedSchemaObjects` in a schema of its own
(one no document type, the event store or a projection also uses) is not on either tab. The database
browser shows it once `Capabilities.BrowseDatabase` is on, the write policy passes and `BrowsableSchemas`
admits that schema. The tables-and-indexes health checks cover it, and show their findings about it only
to a visitor past that same gate.

> **An apply runs under `AutoCreate.CreateOrUpdate`, and `CreateOrUpdate` is not additive.** Weasel's
> update path emits `drop index` for every physical index your configuration does not declare,
> `drop column` for every extra column, `alter column … type` for a changed one,
> `alter table … drop constraint … CASCADE` for a changed primary key, and for a changed partition
> scheme a `create table … as select` / `drop table … cascade` pair that copies the whole table out and
> back. The preview is rendered under the same mode so what you read is what would run, and the
> destructive statements are listed in the dialog before a typed confirmation. **A hand-made index is
> dropped by the next apply** unless you declare it, or name it in
> `opts.Schema.For<T>().IgnoreIndex("…")` — or `opts.Events.IgnoreIndex("…")` on an event table, and
> `table.IgnoreIndex("…")` on a projection's or an `ExtendedSchemaObjects` table, which an apply migrates
> just the same — which takes it off both sides so it is neither created nor dropped. The Indexes and
> Drift tabs say this on screen, and name the index.

**Configuration** — everything your application told Marten, read back out: serializer, tenancy, schema
names, metadata columns, projections, the databases. Never a connection string and never a credential; a
database is identified by server, port, name and schema. The studio changes nothing here. A Runtime card
names the Marten, JasperFx, Weasel and Npgsql versions the host runs, the host's environment, and which of
the features newer Marten releases add this host has.

**Activity** — the last 500 actions taken through the studio in this process, newest first, with user,
scope, action, target, outcome, capability and message. It is in memory and goes away with the process.
The durable record is your own `ILogger`, under the studio's reserved event ids: 9200–9299, and from 0.5 also
9300–9329 and 9400–9519.

## The diagnostics API

`IPostgresDiagnostics`, registered by `AddMartenStudio()` (and by `AddPostgresStudio()`), gives your own code what the
studio reads: the databases it would list, and for each one the findings, with stable codes and variants, and Storage,
Statements, Settings and Connections sections - for an e-mail, an admin page of your own or your own alerting, worded in
your own language and keyed on the codes. It answers host code, not a visitor, so no studio policy, capability or
`ReadOnly` is consulted; statement texts, application names, other sessions' evidence and findings about one tenant
come back only when the call sets `IncludeActivity`, which is off by default. The studio never serves a result: no
endpoint, no log line above Debug, no metric, no Activity entry. See [`docs/diagnostics.md`](docs/diagnostics.md).

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
  naming it is refused until the tenant is registered.

Conjoined tenancy is per document type, so a store whose documents are tenanted while its event store is
not is the normal case and is handled: a single-tenanted collection is not filtered by the scope's
tenant, because there is nothing to filter on and hiding it would hide something genuinely shared.

## Hosting notes

- **It works in an API-only host.** No `.razor` file of your own is needed, and you do not need to set
  `RequiresAspNetWebAssets` — `PostgresStudio`, which `MartenStudio` brings, references
  `Microsoft.AspNetCore.App.Internal.Assets`, whose `buildTransitive` targets define `_framework/blazor.web.js` as
  *your* project's static web asset, two package hops away. Verified against a scratch API-only host that has no
  Blazor components at all and references only `MartenStudio`.
- **The stylesheet and script moved in 0.5**, from `{Path}/_content/MartenStudio/` to
  `{Path}/_content/PostgresStudio/` (`css/studio.css`, `js/studio.js`). A Content-Security-Policy, WAF rule or proxy
  allow-list that names the old path must name the new one; every page's URL is unchanged.
- **You do not need `app.UseAntiforgery()`.** The studio has no `<form>` and no `<EditForm>`; every
  mutation is a Blazor event over the circuit, and SignalR's own same-origin check is what stands
  between a cross-site page and that circuit. The studio's component endpoints say
  `.DisableAntiforgery()`, so a host that never calls the middleware still gets a 200 for `GET /marten`.
  A host that *does* call it is unaffected — the middleware simply has nothing to validate here.
- **Sub-path mounting is a first-class shape.** `MapMartenStudio("/ops/marten")` moves the pages, the
  `/_blazor` circuit, the `opaque-redirect` endpoint, `blazor.web.js` and the
  `_content/PostgresStudio/` mirror under the path, and the shell renders a studio-rooted `<base href>`.
  Every mount is studio-rooted — including the default `/marten` — so a host that has a Blazor app of
  its own does not end up with two endpoints on `/_blazor`.
- **Behind a reverse proxy**, forward the studio prefix and it works: the studio's plumbing is all under
  its own path. If the proxy strips a prefix, give the application the same prefix with
  `app.UsePathBase("/…")` so that the routes and the `<base href>` agree; mounting at
  `MapMartenStudio("/ops/marten")` and stripping `/ops` at the proxy without a matching `UsePathBase` is
  the shape that renders locally and 404s in production.
- **Static assets.** `app.MapStaticAssets()` (or `UseStaticFiles()`) serves the stylesheet and the
  studio's browser helpers the normal way. The studio also maps its own
  `_content/PostgresStudio/{**path}` endpoints as a fallback, so an API-only host that configures neither
  still gets a styled, interactive studio. Both are asked for by the studio's own pages and by nothing
  else: the package ships no Blazor JS initializer, so a host that runs a Blazor app of its own never
  fetches anything of the studio's on pages that have nothing to do with it.
- **Not trimmable.** `IsTrimmable=false` is stated in the csproj on purpose. Blazor Server sets
  `[Parameter]` properties by name from the render tree and the document viewer deserializes into types
  discovered from `IDocumentType.DocumentType` at run time; a trimmer told it may cut has no way to see
  either. An application that publishes trimmed or native AOT does so without the studio.
- **There is no "integrate into my own Blazor app" mode.** The studio maps its own
  `MapRazorComponents<MartenStudioApp>()` root and re-roots it. Adding its pages to your own router is
  not supported in v1.

## Sample application

```shell
dotnet run --project samples/MartenStudio.Sample
```

Then open <http://localhost:5210>. With no connection string configured, the sample starts a throwaway
PostgreSQL container, prints a loud banner saying it did, and seeds a demo domain chosen to make the
studio's screens worth looking at: a duplicated unique column, a soft-deleted type with a foreign key and
optimistic concurrency, a strong-typed id, a subclass hierarchy in one table, application-assigned string
keys, one type with every optional metadata column on and one with all of them off, a two-megabyte
document, conjoined multi-tenant invoices, and an event store with three projections, one archived stream
and one deliberately poisoned stream that the async daemon turns into dead letters. It never falls back
to `localhost` silently; without Docker it throws with guidance. Set `ConnectionStrings:Marten` to point
it at a database of your own. On its throwaway database (or one `--allow-data-generation` names) it creates a
narrowed role, `studio_sample_reader`, and sets `SqlConsoleRole` to it, so the SQL console, the database browser and
the health checks read as that role; it creates no role and grants nothing anywhere else.

Postgres Studio has a sample of its own, with no Marten: `dotnet run --project samples/PostgresStudio.Sample`, then
<http://localhost:5211> - an admin database and three tenant databases listed through the host's own
`IPostgresDatabaseSource`; see [`samples/PostgresStudio.Sample/README.md`](samples/PostgresStudio.Sample/README.md).

Sign in as `admin`, `ops` or `viewer` — the password is the user name. They differ by claim: `admin` has
read, write and destroy, `ops` read and write, `viewer` read only, which is how you can see what each
layer of the authorization contract does.

| Switch | |
|---|---|
| `--anonymous` | Map the studio with `AllowAnonymous()` instead of a policy, to show what an unauthenticated studio looks like. The landing page draws a red banner saying so. Never do this anywhere real. |
| `--readonly` | Set `MartenStudioOptions.ReadOnly`, which turns every mutating capability off however they were configured. |
| `--path /ops/marten` | Mount the studio somewhere other than `/marten`, which is what exercises the sub-path re-rooting. |
| `--no-daemon` | Host no async daemon (`AddAsyncDaemon(DaemonMode.Disabled)`), the shape of a production host whose projections run elsewhere. The studio shows progress from the database and logs nothing about it above Debug. |

> **Argument order does not matter.** The sample's boolean switches — `--anonymous`, `--readonly`,
> `--allow-data-generation`, `--no-daemon` — are switches, not `--key value` pairs: writing one means `true` and the
> token after it is left alone, so `dotnet run -- --anonymous --urls http://localhost:5000` and
> `dotnet run -- --urls http://localhost:5000 --anonymous` do the same thing. An explicit value is
> still accepted where a script wants to pass a variable (`--readonly false`, `--readonly=false`), and
> only a literal `true` or `false` counts as one. `--path /ops/marten` is a pair and does take the
> token after it. This needs saying because .NET's own command-line configuration provider does *not*
> work that way — it reads `--key value` pairs, so a bare switch would otherwise swallow your `--urls`
> and then drop the URL without a word; `SampleOptions.HostArguments` reconciles the two before the
> host sees the arguments.

### Demo data generator

In the `Development` environment (or with `--allow-data-generation` elsewhere) the sample's landing page
carries a **Demo data** panel for the `admin` user — presets **Small**, **Medium** (about 100 k documents
and 100 k events), **Large** (about 1.2 M documents, 1.25 M events across 250 k streams) and **Custom** —
that runs as a cancellable background job with a progress bar, rows per second, an ETA and the async
daemon's lag, plus a truncate that removes exactly what was generated. Documents go in through a binary
`COPY`; order streams go through Marten sessions so the inline projection runs and the async ones have
something to catch up on, with one poisoned stream per 10 000 so the dead-letter screen has real content.
The studio's own services are measured against the Medium set in every integration run; the Large set
runs under `MARTENSTUDIO_LARGE=1`. See [`samples/MartenStudio.Sample/README.md`](samples/MartenStudio.Sample/README.md).
The same panel has **Make it unhealthy** switches - a pinned gap, a session idle in a transaction, a bloated
table, a hogged pool - each turning itself off after ten minutes; see the sample's README.

## Security model and limits

The long form is in [`docs/security.md`](docs/security.md). The short form:

- **Nothing mutating is on by default.** Thirteen capability flags, all `false`, plus `ReadOnly`. The
  dashboard that is dangerous by default is the one somebody maps without reading the docs.
- **Refusals live in the service layer**, not in the markup, and are audited whether they succeed or
  fail: `StudioCapabilityGuard.Require(…)` → scope resolution with the write policy → the operation →
  the audit record.
- **The SQL console's real guard is the transaction, not the parser.** Every statement runs inside
  `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK` with `statement_timeout`, `lock_timeout`,
  `idle_in_transaction_session_timeout` and an optional `SET LOCAL ROLE`, all set with `set_config`
  parameters rather than interpolated text. `ReadOnlySqlGuard` — which allows only `select`, `with`,
  `explain`, `table` and `values`, and only one statement — exists to produce a better message than
  SQLSTATE `25006`, and treating it as the security boundary is how these features get CVEs.
- **A read-only transaction is not a sandbox.** `select pg_terminate_backend(pg_backend_pid())` runs
  happily inside one; so do `pg_read_file`, `lo_export`, `pg_ls_dir`, `dblink` and the session-level
  advisory-lock family. Those are refused **by name** through a denylist
  (`ReadOnlySqlGuard.DisallowedFunctions`, quoted in full in `docs/security.md`), and that refusal is
  worth exactly what a parser's refusal is worth: a determined caller can reach the same function
  through a view, a `SECURITY DEFINER` wrapper or an alias, and the list will not see it.
- **The only true narrowing is `SqlConsoleRole`.** A Postgres role that was never granted `EXECUTE` on
  those functions, and can read only what you meant it to read, cannot be talked around however the call
  is spelled. Configure one if you enable the console. Without it, the console reads everything the
  store's own Postgres role can read — every table in the database, Marten's or not.
- **Grant `RunSql` only to people you would give `psql` to.** That is the whole of it.
- **`ViewDatabaseActivity` shows other people's sessions.** `pg_stat_activity`, `pg_locks` and
  `pg_stat_statements` describe every session on the server, and a statement's text carries literals and
  other applications' names. Without the capability the advisor and Database › Sessions show counts, ages
  and states, never who; grant it to the people you would let read other applications' statements. Operations ›
  Statements is behind it too: it reads `pg_stat_statements` as the store's own role, so another role's statements
  never appear, but another application's do when it connects as the same role.
- **The diagnostics API answers your code, not a visitor.** No policy, capability or `ReadOnly` is asked; it reads
  nothing your code could not read through the same data source or store, and Activity-grade data needs
  `IncludeActivity`. If you expose a result, put it behind your own authorization.
- **LINQ mode evaluates an expression somebody typed, inside your application.** Marten evaluates in process
  every part of a predicate that does not reference the document, so the text passes the studio's own
  lexical screen, JasperFx's Dynamic LINQ allow-list and the studio's own walk of the parsed expression before
  Marten sees it, and only literals reach Marten's evaluator. It reads any document of a type the visitor can
  see, as Marten's own `Query<T>()` reads it. Grant `RunLinqQueries` to people trusted with the store's data;
  `docs/linq-mode.md` has the threat model.
- **Mode A is guarded differently, because it needs no capability.** The studio composes the statement
  itself — `select … from <table> as d where 1 = 1 and <tenant> and <not deleted> and ( <your clause> )
  and <tenant> and <not deleted>` — so the tenant in the header and the soft-delete rule apply exactly as
  they do in the Documents browser, and the SQL the page shows is the SQL that ran. The studio's terms
  are repeated *after* your predicate as well as before it, because a parenthesised predicate only
  contains an `or` while the parentheses hold. The clause may not carry a second statement, be a
  statement of its own, leave a bracket unbalanced, hang anything but a sort list off `order by`, call a
  function that reads, writes or waits outside the row, or cast to `regclass`/`regproc` — **none of
  which `RunSql` lifts**. What `RunSql` lifts is reaching another relation: subquery, union, join,
  `lateral`, `returning`. The scanner errs towards refusing, and every refusal names what would lift it.
- **Audit.** Every mutating operation and every refusal is written to a 500-entry in-memory ring (the
  Activity page) *and* to your `ILogger` under the reserved event ids **9200–9299**, which is the copy
  that survives a deployment. 9200 `ActionPerformed`, 9201 `ActionFailed`, 9202 `CapabilityDenied`, 9203
  `ScopeAuthorizationDenied`, 9204 `SqlExecuted`, 9205 `SqlRejected`, 9206 `SchemaChangeApplied`, 9207
  `ProjectionRebuildStarted`, 9208 `ProjectionRebuildFinished`, 9209 `DaemonControlRequested`, 9210
  `StoreUnavailable`, 9211 `DocumentWriteRoundTripDropped`. A database browser's first read of a
  relation's rows is 9235 and an opened row 9236, a read of other sessions' details is 9245, a LINQ
  mode run, refusal and Postgres failure are 9260, 9261 and 9262, a bloat measure is audited as "Measure bloat", and
  the Statements page's first read in a circuit is 9452. The ranges 9200–9299, 9300–9329 and 9400–9519 are
  reserved. Every value a visitor could shape - a typed `?store=`, a statement, a reason - reaches the log escaped and
  bounded, so it cannot write a log line of any length or forge a second one.
- **Log levels.** A state the host configured is not a warning. No async daemon in this process, a
  daemon run by an external system (`AsyncMode = ExternallyManaged`, which Wolverine's managed
  distribution sets), event tables that do not exist yet, a speculative count or a query of your own
  running out of its budget — each is a value the page already shows, logged at `Debug` at most. A real
  anomaly on a path the studio polls every `RefreshInterval` is logged at `Warning` **once per store,
  database and exception type per ten minutes**, and at `Debug` in between, so filter on the event id
  rather than muting the category:

  | Id | Event | Logged when |
  |---|---|---|
  | 9202, 9203 | `CapabilityDenied`, `ScopeAuthorizationDenied` | Always `Warning`: a capability or policy refused an action |
  | 9210 | `StoreUnavailable` | A registered store will not build |
  | 9211 | `DocumentWriteRoundTripDropped` | Always `Warning`: an edit was saved and the round trip dropped properties |
  | 9212 | `DaemonUnreachable` | A registered coordinator answered with something other than a daemon for this database, or could not be constructed for a reason other than Wolverine's unknown-store shape |
  | 9213 | `StoreDatabasesUnreadable` | A store's databases could not be listed |
  | 9214 | `PostgresVersionUnreadable` | The server version could not be read; for a store with no default database (master-table, sharded, or static without `AsDefault()`) it is unknown, at Debug |
  | 9215 | `ShardTrackerUnobservable` | The in-process shard tracker could not be observed |
  | 9216 | `ProjectionSummaryUnreadable` | The Overview's or the navigation's projection summary failed |
  | 9217 | `EventReadFailed` | An event-store read failed for a reason other than a statement timeout, or `IsDocumentTypeVisible` threw for `DeadLetterEvent` |
  | 9218 | `StreamTimestampMissing` | A hand-migrated `mt_streams` has a row with a null timestamp |
  | 9219 | `TenantDiscoveryFailed` | The scope selector could not discover a store's tenants |
  | 9220 | `LiveUpdateHandlerFailed` | A page's own refresh-failure handler threw |
  | 9221 | `TenantPolicyFailed` | The store policy threw while the scope selector filtered a database's tenants |
  | 9222 | `HealthCheckFailed` | A health check failed for a reason nobody chose; one the host's setup rules out is Debug |
  | 9246 | `SessionActivityReadFailed` | Database › Sessions could not read the sessions for a reason other than a privilege or a timeout |
  | 9251 | `DiagnosticsSectionFailed` | The diagnostics API could not read a section of a snapshot |
  | 9270 | `HealthExportFailed` | The opt-in health check itself failed, so it reports `Degraded` |
  | 9320 | `SubscriptionRewindRefused` | Always `Warning`: Marten refused a dead-letter rewind as an unknown subscription; its words are here and not on the page |
  | 9410 | `StorageReadFailed` | The Storage page could not read the database's sizes for a reason nobody chose |
  | 9420 | `BloatMeasureFailed` | Always `Warning`: a bloat measure a visitor pressed failed for an unexpected reason |
  | 9430, 9431 | `ReplicationReadFailed`, `TablespaceSizeFailed` | A Replication section could not be read; a tablespace size a visitor asked for failed (always `Warning`, one line per click) |
  | 9440 | `ServerFactsReadFailed` | A section of the Server page could not be read |
  | 9451 | `StatementsReadFailed` | The Statements page could not read `pg_stat_statements` for a reason nobody chose |
  | 9500, 9501 | `CapacitySourceFailed`, `AuroraReadFailed` | The host's `IPostgresCapacitySource` threw or timed out; one of Aurora's own reads failed |

- **What the studio never does:** start a second projection daemon, execute DDL on a read or navigation path
  of its own (the exception is Marten's: the first enumeration of a master-table, sharded or single-server
  tenancy, under *Current limitations*), write document DML of its own, replace your `StoreOptions.Logger`,
  or show a connection string or a credential anywhere.

## Current limitations

- **Mode A takes no parameters.** The clause is handed to Marten as SQL text; there is no `?`/`@p`
  binding, so a literal you paste in is SQL too. What keeps that from being an ungated console is the
  clause guard — no second statement, balanced brackets, the function denylist, and without `RunSql` no
  nested read — together with the read-only transaction it runs in; not a parameter you could have put
  the value in safely.
- **No push, no hub.** Live pages poll at `RefreshInterval` through a single-flight snapshot cache,
  paused on hidden tabs; only projection state gets sub-second updates, and only where the daemon is
  hosted in this process. There is no SignalR hub of the studio's own in v1 — adding one would add a
  host-visible endpoint for something polling covers.
- **No patch, no bulk update.** Documents are edited one at a time through a full round trip. Deleting
  a selection is supported; changing one is not.
- **Earlier versions of a document are only visible where there is an event stream.** Marten keeps one
  row per document, so the studio can show you the current document and — for an aggregate — replay its
  stream to any version. There is no per-document history for a plain document type.
- **Editing a hierarchy row has edges.** A subclass row is previewed and saved as the subclass, but a
  row stamped with a subclass this process does not map, or a hierarchy row with no discriminator under
  an abstract root, is **refused by name** rather than rewritten as its root type. Undelete is refused
  outright for a document whose id the studio cannot build a typed expression for.
- **No "integrate into my own Blazor app" mode.** The studio maps its own component root; its pages
  cannot be added to your router (D11).
- **No keyboard cheat sheet.** The JSON viewer, its search bar and the editor have keyboard handling,
  but there is no global shortcut map and no page that lists what exists.
- **No live SQL tail.** It would require replacing your `StoreOptions.Logger`, which is exactly the
  "registration changes host behaviour" failure this project refuses. Every generated grid carries a
  "Show SQL" disclosure instead.
- **Read-only and the capabilities are process-wide**, not per store, per database or per tenant. *Which*
  scopes a visitor sees is expressible, through `StoreAuthorizationPolicy`; "this tenant may edit but not
  delete" is not.
- **Counts are estimates above `ExactCountThreshold`.** A `~` prefix says so. The `=` beside a collection
  asks for an exact `count(*)`: below the threshold you get one, bounded by Npgsql's `CommandTimeout`
  (from `QueryTimeout`) rather than by a server-side `statement_timeout`; above it the answer is the same
  estimate marked "estimate only", with the reason on the badge, and the button is not offered again. The
  exception is a conjoined collection under a tenant scope: that count is `where tenant_id = …`, which
  Marten makes index-backed by putting `tenant_id` first in the primary key, so it is always counted
  exactly — and such a visitor is never shown the whole-table estimate at all, because `reltuples` counts
  every tenant's rows.
- **A mapping made after the host has started is never checked** by the startup guard — from a hosted
  service, or a lazily built `EndpointDataSource`. Map the studio while the application is being built.
- **Some of the studio needs a newer Marten than its floor.** The package depends on Marten 9.39.0 and
  works there; what later releases add lights up at run time, and the Configuration page's Runtime card says
  which of it this host has. LINQ mode needs Marten 9.48+ (JasperFx 2.84) and is absent below it. The
  `FillFactor` and `StorageParameter` remedies, the Schema screen's storage-parameter drift and E08's
  `DcbTagVersionFillFactor` need 9.47+. D03 reads the high-water agent's own liveness stamp from 9.43; below
  it, an old stamp says nothing and D03 says it cannot tell. Telling that a projection is being rebuilt by
  something other than Marten Studio needs 9.50 (JasperFx 2.86): below it, Start cannot see such a rebuild and
  warns that it cannot, and a start during one runs a second agent beside the replay. On a store that numbers
  events per tenant (`UseTenantPartitionedEvents`), Skip event and Rewind subscription on the dead-letter page
  need 9.51: from it each acts on one tenant, and a rewind is offered only for a tenant whose agent the daemon
  in this process runs; below it both are refused, because Marten skips and rewinds by a number every tenant
  has. Compacting a stream whose folded events carry DCB tags needs 9.51 too; below it Postgres refuses the
  delete and nothing changes. The Schema screen's Check, Preview and Generate script buttons create
  Marten's HiLo objects (`mt_hilo`, `mt_get_next_hi`) below 9.50.3 and nothing from it, and each says which.
  From 9.52 a rebuild replays from the projection's start floor rather than from the first event, so the
  rebuild dialog names the floor stored for it in `mt_event_projection_floors` (read with the studio's own SQL)
  and says that a floor set in code may apply; D05 stops offering `SkipApplyErrors` for a database error in
  projection code, which from 9.52 pauses the shard at any setting and is never dead-lettered; and the
  Advance high water mark dialog says that the advance first ensures the event store's storage - with no tenant
  selected in every database of the store, each under its own `AutoCreate` - and, on a store that numbers events per
  tenant, which tenants' marks it moves.
- **Enumerating a dynamic tenancy's databases is not read-only**, at any Marten through 9.54.2.
  `AllDatabases()` and `DescribeDatabasesAsync()` build the tenancy's databases, and the first time a
  store instance does, a master-table or sharded tenancy applies its lookup table and upserts its seed rows,
  and a single-server tenancy creates every configured database that does not exist yet. The lookup table's
  apply is gated, and from Marten 9.51 run, under the tenancy's own `AutoCreate` when it sets one and the
  store's `AutoCreateSchemaObjects` otherwise; below 9.51 it always runs under the store's (`All`
  included, and a store's `None` becomes `CreateOrUpdate` when the tenancy's own says anything else). A
  tenancy that sets none applies under the store's mode at every version. The studio enumerates on the first navigation, so in a process that runs no
  daemon of its own it can be that first caller; a host whose daemon runs in-process, or that applies on
  startup, has always enumerated before it. The opt-in health check's own listing never enumerates one
  (AGENTS.md D27); a hosted projection coordinator it asks for its daemons enumerates the store, as its own
  leadership loop does every cycle.
- **Marten Studio is a store console, not an ETL tool.** Import, dumping a whole collection, copying
  between stores and scripted migration are outside it. The Query page exports the page you are looking
  at — the row cap has already stopped the reader by then — and never a table.

## Building from source

```powershell
.\build.ps1 Test                 # restore, compile, run all four test projects
.\build.ps1 Test --playwright    # ... and install the Chromium build the browser suites drive first
.\build.ps1 Pack                 # both NuGet packages into artifacts/packages
```

The fast suites need nothing:

```powershell
dotnet test tests\MartenStudio.Tests
dotnet test tests\PostgresStudio.Tests
```

The integration suite needs Docker — run `docker info` first. It starts one `postgres:17-alpine` through
Testcontainers and reuses it between runs locally; a few classes start a server of their own, never
reused: a `postgres:17-alpine` with settings of its own, and `pgvector/pgvector:0.8.1-pg17` for the
pgvector tests. **Any run that might overlap another must opt out**, because schema names derive from test
class names and two runs on one reused container drop each other's tables:

```powershell
$env:MARTENSTUDIO_PG_REUSE='false'
dotnet test tests\MartenStudio.Integration.Tests
dotnet test tests\PostgresStudio.Integration.Tests
```

Postgres Studio's live suite has a container reuse identity of its own and never attaches to the Marten suite's; its
version matrix starts one server per PostgreSQL major, 14 to 18, and 19's release candidate as a preview.

(`TESTCONTAINERS_REUSE_ENABLE` is not read by Testcontainers 4.x and must not be used.)

`.\build.ps1 Browsers` installs the Chromium build the browser tests drive. It is deliberately opt-in
locally — a developer should not pay a 150 MB download for a test run — and automatic on CI, where a
missing browser has to be a failure rather than a silent skip.

## Contributing

`AGENTS.md` at the repository root is the single source of truth for this codebase: the hard rules, the
numbered design decisions with their rationale, the package budget and the directory layout. Read it
before opening a pull request — several of the rules exist because the obvious thing was tried and broke
something.

## License

MIT. See [LICENSE](LICENSE).
