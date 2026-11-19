# Authorization and capabilities

Who gets in, which store, database, tenant or topic they see, and what they may change. The threat model behind it is [security.md](security.md).

## Authorization

There are three layers, and two kinds of switch on top of them. All of it is yours to configure; the studio authenticates
nobody.

1. **`DataStudioOptions.AuthorizationPolicy`** - *who gets in*. Applied to every studio endpoint, whatever the module: the
   front page, Activity, every module's pages, the Blazor circuit, the static assets and the forward of your own
   `_framework` files. `RequireAuthorization()` on the builder `MapDataStudio()` returns does the same job, and covers the
   pages, the circuit and that forward.
2. **A module's resource policy** - *which store, database, source, cluster or tenant*. Resource-based, evaluated against the
   module's own resource and asked on **every** data call rather than only at a scope selector:
   `MartenOptions.StoreAuthorizationPolicy` against a `MartenStoreResource`, `PostgresOptions.SourceAuthorizationPolicy`
   against a `PostgresResource` and, for Kafka, `KafkaOptions.ClusterAuthorizationPolicy` against a `KafkaTopicResource`,
   asked once for each topic. Listings are filtered rather than annotated: a tenant you may not see is one you never learn exists. The
   three resources are unrelated on purpose, and no module falls back to another's policy: a handler written for one is
   never handed another's resource.
3. **A module's write policy** - *each mutating call*, against the same resource with the capability name attached:
   `WriteAuthorizationPolicy` on the Marten and Postgres modules (it falls back to the module's resource policy when you
   configure only one), and `KafkaOptions.CapabilityAuthorizationPolicy` on the Kafka module (it falls back to the cluster
   policy).

On top: **`ReadOnly`** (two switches, below) turns capabilities off whatever `Capabilities` says, and **`Capabilities`**, a
module's own, decides which mutating operations exist at all.

### The startup guard

A mapping that answers none of this refuses to start, before the web host binds a listener. The message says what every
module you registered would expose, once each; for a Marten host it reads:

> Data Studio is mapped with no authorization. Its pages carry no authorization, so every document, event stream and
> schema in this process is readable by anyone who can reach this URL. Data Studio refuses to start rather than serve that
> by accident. Say which you meant:
>
> - `app.MapDataStudio().RequireAuthorization()` authorizes its pages and its Blazor circuit;
> - `services.AddDataStudio(options => options.AuthorizationPolicy = "...")` authorizes those and the static assets with
>   them;
> - `app.MapDataStudio().AllowAnonymous()` serves it to anyone, deliberately.
>
> A non-null `AuthorizationOptions.FallbackPolicy` satisfies this too, since it covers every endpoint that states nothing.

"I forgot to add `RequireAuthorization`" must not be a silent state, so it is a startup failure instead.

### What protects the circuit

Both `DataStudioOptions.AuthorizationPolicy` and `RequireAuthorization()` on the returned builder cover the Blazor circuit as
well as the pages - the builder holds the studio's page endpoints *and* its `/_blazor` endpoints, so a statement about the
studio is a statement about both. That matters: the circuit is the dangerous half, because it is the half that runs
components. The studio stamps no `AllowAnonymous` of its own on `/_blazor`.

The static assets under `_content/DataStudio/` are package content - CSS and a little JS. With a studio policy configured
they carry it too; without one they opt out explicitly, so an application with a fail-closed `FallbackPolicy` can still
load the stylesheet.

The `{Path}/_framework/{**file}` forward is **not** among them: it serves files that are *yours* (the host's `_framework`
tree, for example the .NET 11 SDK's hot-reload initializer), so it can never opt out. With an `AuthorizationPolicy` it
carries the policy; without one it is part of what `MapDataStudio()` returns, so your `RequireAuthorization()` or
`AllowAnonymous()` on the mapping and your `FallbackPolicy` govern it exactly as they govern the page that imports those
files. It also never serves more than your own root would: a root endpoint that refuses the visitor, or does not answer GET,
is a 404 under the mount.

### Resource-based authorization

`MartenStoreResource` is what the Marten module's two policies are evaluated against:

```csharp
public sealed record MartenStoreResource(
    string StoreName,           // "default", or the marker interface name of an ancillary store
    string DatabaseIdentifier,  // Marten's database identity - never a connection string
    string? TenantId,           // the selected tenant, or null for a view spanning all of them
    string? Capability);        // "EditDocuments", "RunSql", ... - or null for a read
```

One handler answers for the scope selector, the page frame and every data call. The Postgres module's
`PostgresResource(SourceName, DatabaseIdentifier, Capability)` has no tenant, and `KafkaTopicResource(Cluster, TenantId,
Topic, Capability)` names a topic. This example is *illustrative* - it is not compiled from the sample, which uses plain
claim policies:

```csharp
// Illustrative, not compiled.
public sealed class MartenStoreRequirement : IAuthorizationRequirement;

public sealed class MartenStoreHandler
    : AuthorizationHandler<MartenStoreRequirement, MartenStoreResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        MartenStoreRequirement requirement,
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

A `null` tenant is not "no tenant to check" but every tenant at once - the all-tenants view, and every `BrowseDatabase`
read, every `ViewDatabaseActivity` read, every SQL console run and every operation that reaches a whole shared database (a
dead-letter rewind, a progression correction, a rebuild, a schema apply), which are always asked with `TenantId = null`
because nothing narrows them to a tenant - so a handler that let it through for everybody would hand a user scoped to one
tenant all of them.

Register it the usual way and name the policy:

```csharp
// Illustrative, not compiled.
builder.Services.AddSingleton<IAuthorizationHandler, MartenStoreHandler>();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("MartenStore", policy => policy.AddRequirements(new MartenStoreRequirement()));

builder.Services
    .AddDataStudio(studio => studio.AuthorizationPolicy = "DataStudio")
    .AddMarten(marten =>
    {
        marten.StoreAuthorizationPolicy = "MartenStore";
        marten.WriteAuthorizationPolicy = "MartenStore";
    });
```

With no resource policy configured, nothing is asked and everything passes - which is what keeps an application that never
set the option exactly as it was.

## Capabilities

Every module has capabilities of its own, **all `false` by default**: a freshly mapped studio is a read-only browser. The
Marten module has fourteen, all turned on by `MartenCapabilities.All()`. The Postgres module has four, all reads authorized
like writes - `RunSql`, `BrowseDatabase`, `ViewDefinitions` and `ViewDatabaseActivity`, which mean what the table below says
of them - and `PostgresCapabilities.All()` turns them on. The Kafka module has four (`SearchRecords`, `ReadPayloads`,
`ExportRecords` and `LiveTail`), turned on by `KafkaCapabilities.All()` and described in [kafka.md](kafka.md).
The Marten module's:

| Capability | What it allows |
|---|---|
| `EditDocuments` | Edit a document's JSON and save it through the store's own serializer and session |
| `DeleteDocuments` | Delete, soft-delete and undelete documents, one at a time or a selection |
| `ArchiveStreams` | Archive an event stream. On an event store that is not conjoined, a stream that also holds another tenant's events needs the write policy for the database as a whole |
| `ManageDeadLetters` | Discard dead-letter records, mark events as skipped, and rewind a subscription to the event that failed (a rewind reaches every tenant in the database, so it is asked with `TenantId = null` unless the database holds only the selected tenant). On a store with `Events.UseTenantPartitionedEvents`, below Marten 9.51 Skip event and Rewind subscription are refused, because Marten skips and rewinds by a number every tenant counts on its own; from 9.51 each acts on the dead letter's tenant alone and is asked for that tenant, and a rewind is offered only where the daemon in this process runs that tenant's agent; and skip a whole dead-letter group's events, at most 1,000 an action after typing the count, through Skip event's own checks |
| `ControlDaemon` | Pause and resume the async daemon hosted in this process, start and stop individual projection agents while it is paused, and restart the high-water agent |
| `RebuildProjections` | Rebuild a projection |
| `CorrectProgression` | Advance the high-water mark, correct projection progression in the database, or remove an orphaned progression row (Marten's own `DeleteProjectionProgressByShardNameAsync`, after a confirmation and a second look at the table; asked for the database as a whole, `TenantId = null`, whatever tenant is selected, and never for a row the daemon may still read) |
| `ApplySchemaChanges` | Apply pending schema migrations |
| `RunSql` | Run read-only SQL from the Query page's console, and its Explain analyze; the sidebar's Plan entry is there only while it is on. Authorized like a write and always with `TenantId = null` — the console is not limited to a tenant — which also governs the Mode A `EXPLAIN`, the subquery lift and What if? under a console or Mode A plan |
| `BrowseDatabase` | Browse the rows and definitions of non-Marten tables, views, functions, triggers, sequences and types in the schemas `BrowsableSchemas` names. A read, authorized like `RunSql` as a write, and always with `TenantId = null`. Implies `ViewDefinitions` |
| `ViewDefinitions` | Read the definitions alone - a view's or materialized view's query, a function's or procedure's body, a trigger's definition, a type's description - of the non-Marten objects in the schemas `BrowsableSchemas` names *exactly*, and see those schemas' structure; never a row, never a sequence's current value (data, so `BrowseDatabase`'s), and never a schema only `"*"` admits. For a host that keeps rows off the screen and still wants the SQL its own migrations wrote. A read, authorized like `BrowseDatabase` as a write (the write policy asked with this name, always with `TenantId = null`). Off by default, in `All()` |
| `ViewDatabaseActivity` | See who and what is using the database: other sessions' process ids, roles, client addresses, application names and statements, on the Health page, on Database › Sessions and in the Projections screen's high-water Explain, and run the health advisor's statement checks over `pg_stat_statements`. Without it the counts, ages and states are still shown. A read, authorized like `RunSql` as a write, and always with `TenantId = null`. Off by default, in `All()` |
| `CompactStreams` | Compact an event stream from Stream detail: Marten's own `CompactStreamAsync<T>` replaces its events up to a version with a snapshot of its aggregate, and the folded events are deleted. Authorized for the scope as selected, tenant included - and, on an event store that is not conjoined, for the database as a whole when the stream also holds an event another tenant's session appended. Off by default, in `All()` — see [`compaction.md`](compaction.md) |
| `RunLinqQueries` | Run LINQ mode on the Query page: a typed predicate parsed in this process and applied to Marten's own `Query<T>()`, or to the event log through `QueryRawEvents()`. A read, authorized like a write — with the tenant for a conjoined collection (or conjoined events), with `TenantId = null` for one not divided by tenant and for the plan. It also governs What if? beside LINQ mode's and the Documents list's index advice. Off by default, in `All()` — see [`linq-mode.md`](linq-mode.md) |

```csharp
// Inside .AddMarten(marten => { ... }) and .AddDataStudio(studio => { ... }):
marten.Capabilities = MartenCapabilities.All();   // everything: the one-line, greppable opt-in
marten.Capabilities.RebuildProjections = true;    // or one switch at a time, from all-off
marten.ReadOnly = true;                           // turns the Marten module's capabilities off, whichever you wrote
studio.ReadOnly = true;                           // and this turns every module's off
```

There are two read-only switches. **`DataStudioOptions.ReadOnly`** turns every module's capabilities off. A module's own
(`MartenOptions.ReadOnly`, `PostgresOptions.ReadOnly`) turns off that module's alone, so it never turns the Kafka module's
capabilities off; Kafka has no switch of its own and obeys the shell's. A host that relied on a module's `ReadOnly` to keep
Kafka read-only sets `DataStudioOptions.ReadOnly` too: otherwise Kafka's capabilities, if turned on, are on, and with
neither Kafka policy set the application refuses to start.

Every disabled control says the exact property you would set, spelled the way you would write it:

> Editing documents is not enabled. Set `MartenOptions.Capabilities.EditDocuments` to `true`, or
> `MartenOptions.Capabilities = MartenCapabilities.All()` to enable all of them.

and with a read-only switch on, the one that is on:

> `MartenOptions.ReadOnly` is `true`, which turns every capability of the Marten pages off: the mutating operations,
> and the reads authorized like writes (`RunSql`, `BrowseDatabase`, `ViewDatabaseActivity` and
> `RunLinqQueries`).

or, for the shell's, "`DataStudioOptions.ReadOnly` is `true`, which turns every capability off". The capability chip in the
header names the switch that is on, and each module's group in it.

Hiding a button is a convenience. The refusal itself lives in the service layer, where the operation does, because a Blazor
circuit is a long-lived object a client can drive - and every refusal is written to the audit log just like a success.
