# 0.1.1

- The detail view of a document whose type has a `[Version]` property no longer answers 500 and
  terminates the Blazor circuit ([#1](https://github.com/lahma/marten-studio/issues/1)). Marten
  registers a *search-only* duplicated field for every metadata column a Marten attribute names a member
  for — `[Version]`, `[CreatedAt]`, `[TenantId]` and the rest — so that a LINQ comparison against the
  member reads the column rather than the JSON. It carries the metadata column's own name, `mt_version`,
  and Marten creates no column for it: its own `DocumentTable` skips exactly these. The studio took them
  for real duplicated columns, so `mt_version` was named twice in the single-document select list and
  rendered twice in the metadata pane, and the second row carrying a key the first already had threw
  inside Blazor's diff builder. The pane also keys its rows on position now, so a repeated column name
  costs a repeated row rather than a session. A filter typed as `Version:` still reads `mt_version`
  rather than the JSON copy of it, which Marten leaves one write behind, and the index advisor no longer
  suggests a `Duplicate(x => x.Version)` that Marten would discard.
- Three other lists keyed on values that are not unique no longer end a session either: the recent-query
  drawer (the same where clause run against two collections repeats its text — reachable by a visitor
  with no capability at all), the SQL console's Postgres notices, and the destructive-statement list in
  the schema migration dialog.
- The package no longer ships a Blazor JS initializer, so a host that runs a Blazor app of its own stops
  fetching a studio script on every page it serves ([#2](https://github.com/lahma/marten-studio/issues/2)).
  A `*.lib.module.js` in an RCL's `wwwroot` is a JS initializer by convention, and Blazor loads the
  initializers of every referenced RCL into *every* Blazor app in the process — from the site root, on
  pages that never mention the studio. With `AuthorizationPolicy` set that path is authorized too, so the
  fetch redirected to the login page, came back as `text/html`, and the console logged a MIME-type error
  on every page load, the host's own unauthenticated login page included. The helpers are an ordinary
  script at `_content/MartenStudio/js/marten-studio.js` now, loaded by the studio's own shell relative to
  its `<base href>`, so it resolves under the mount path and only the studio's pages ask for it. Nothing
  else changes: the same helpers, applied to the prerendered markup sooner than the initializer ran.

# 0.1.0

- First release. Marten Studio is an embeddable Blazor Server admin UI for MartenDB, shipped as one
  MIT-licensed Razor Class Library. `AddMartenStudio()` in the container and `MapMartenStudio()` in the
  endpoint pipeline mount it inside an existing ASP.NET Core application, against that application's own
  configured `IDocumentStore`. It is not a standalone application, not a fleet monitor and not a
  connection-string tool: everything it renders, it learns from the host's `StoreOptions`.
- The public API is five types and nothing else, snapshot-tested so it cannot grow by accident.
  `MartenStudioServiceCollectionExtensions.AddMartenStudio(this IServiceCollection, Action<MartenStudioOptions>?)`
  registers the services; `MartenStudioEndpointRouteBuilderExtensions.MapMartenStudio(this IEndpointRouteBuilder)`
  and its `(builder, pattern)` overload map the endpoints and return an `IEndpointConventionBuilder`
  covering the studio's pages, its Blazor circuit and its static assets. `MartenStudioOptions` carries
  the twenty settings — path, title, the three authorization policies, `ReadOnly`, `Capabilities`, the
  paging, timeout and row limits, `SqlConsoleRole`, `ExactCountThreshold`, `RefreshInterval`,
  `RebuildShardTimeout`, `IsDocumentTypeVisible`, `IncludeAncillaryStores`, `KnownTenantIds` and
  `DiscoverTenantIds` — all validated at startup. `MartenStudioCapabilities` is the nine mutating
  capabilities, every one `false` by default, with `All()` as the one-line opt-in.
  `MartenStoreResource` is the record the store and write policies are evaluated against, carrying the
  store name, the database identity, the tenant and the capability being exercised.
- Nine screens. Overview (one card per registered store, its databases, document and event types, and
  the PostgreSQL version). Documents (collections rail with estimated counts, column chooser, keyset
  paging, a search grammar that compiles to parameterised SQL with an index verdict and a "Show SQL"
  disclosure, a JSON viewer, a "referenced by" panel counting what points at the document on screen,
  and — behind capabilities — editing with a round-trip diff, delete, undelete and bulk delete).
  Relationships (the store's document types as a foreign-key diagram, drawn from what `StoreOptions`
  declares cross-checked against what `pg_constraint` actually holds, with an accessible table beside it
  and no JavaScript). Query (a guarded Marten `where` clause mode that needs no capability, and a
  SQL console gated on `RunSql`). Events (streams, stream detail with a timeline and aggregate time
  travel, the global feed, event types, and dead letters). Projections (shard state and progression,
  daemon control, rebuilds behind a typed confirmation, high-water and progression correction). Schema
  (drift, tables, indexes, functions and DDL, with an honest account of what an apply would remove).
  Configuration (everything the host told Marten, read back out, never a connection string). Activity
  (the last five hundred actions taken through the studio in this process).
- Authorization is the host's, in three layers plus two switches: `AuthorizationPolicy` decides who gets
  in and covers the Blazor circuit as well as the pages; `StoreAuthorizationPolicy` decides which store,
  database and tenant, resource-based and asked on every data call rather than only at the selector;
  `WriteAuthorizationPolicy` decides each mutating call with the capability named. `ReadOnly` and
  `Capabilities` sit on top of all three. A mapping that answers none of it fails startup with a message
  naming the three ways to answer, because "I forgot to add `RequireAuthorization`" must not be a silent
  state.
- Every mutating operation is capability-gated in the service layer rather than in the markup, and
  audited on the failing path as well as the succeeding one — to a five-hundred-entry in-memory ring the
  Activity page reads, and to the host's own `ILogger` under event ids 9200 to 9211, which is the copy
  that survives a deployment.
- The SQL console's guarantee is its transaction, not its parser: `SET TRANSACTION READ ONLY` with a
  statement timeout, a lock timeout, an idle-in-transaction timeout and an optional `SET LOCAL ROLE`,
  every setting applied through `set_config` parameters rather than interpolated text. The statement
  guard and the function denylist produce better messages than a SQLSTATE; `SqlConsoleRole` is the only
  thing that genuinely narrows what the console can reach.
- Reads are parameterised SQL built from Marten's own metadata in one place, with identifiers quoted
  through a builder; writes go through a Marten session so upsert semantics, metadata columns,
  soft delete, hierarchy discriminators and tenancy stay exactly as Marten defines them. No read or
  navigation path executes DDL, no second projection daemon is ever started, and the host's
  `StoreOptions` are never modified.
- Runs in any ASP.NET Core host on .NET 10, including an API-only one with no Blazor components of its
  own: the package brings `blazor.web.js` through `Microsoft.AspNetCore.App.Internal.Assets` and needs
  no `RequiresAspNetWebAssets` in the host, and the studio disables antiforgery on its own endpoints so
  it imposes no middleware-ordering requirement. Sub-path mounting moves the pages, the circuit, the
  framework script and the asset mirror together. No third-party UI, CSS or icon library, and no global
  styles: a host that adds the package and never maps it is the application it was.
- The sample host carries a demo-data generator, in development or behind `--allow-data-generation`: a
  cancellable background job with progress, an ETA and the daemon's lag that writes up to a million
  documents and a million events across a quarter of a million streams, and a truncate that removes
  exactly what it generated. The studio's own screens are measured against it on every integration run,
  so a change that makes a page cost more at a million rows fails the build rather than being noticed
  later.
