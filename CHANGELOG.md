# 0.3.0

- A database browser. A new **Database** section (Tables, and Relationships moved from Data with its route
  unchanged) lists every table, view, materialized view, foreign table, function, procedure, aggregate,
  trigger, sequence and enum, domain, composite or range type in the store's Postgres database that the
  visitor may see — a Quartz.NET job store, a legacy schema, another library's tables — grouped by schema.
  Marten's own objects are told apart at a glance: quieter, sorted last, and linked to where their data is
  really read (Documents, Streams, the Schema screen), and a Quartz.NET, Wolverine, EF Core, Hangfire or
  Flyway object wears a neutral "recognised by name" hint. Object detail (`/marten/database/object`) shows
  columns, keys and indexes, foreign keys both ways, triggers, a view's query and the object's
  neighbourhood in the relationships diagram.
- Rows of non-Marten tables, views and materialized views, read-only: keyset paging over composite keys (by
  `ctid` for a table with no key, by offset for a view); a `col = value` / `col ~ text` / `col is:null`
  filter shown as chips with an index verdict, and "Run anyway" for an unindexed read of a large table;
  Show SQL with parameter names only; an estimate and an on-demand exact count; headers with the type and
  PK and → parent markers; `timestamptz` in the chosen time zone and `timestamp` marked "no tz"; JSON and
  sniffed `bytea` opening in the viewer; a "≈ date" hint under `bigint` columns holding .NET ticks or epoch
  times; a sticky key column, Space to open a row in place and Enter for its detail. Cells are cut on the
  server in bytes, and a page has a byte budget. Row detail
  (`/marten/database/row?schema=&name=&key.<column>=`) shows every column, what the row points at (the
  parent row, a "missing" badge, or the Marten document in Documents) and what points at it (bounded at
  1,000+, with ON DELETE), and copies the row as JSON, its key or a WHERE clause.
- Gated like the SQL console: `MartenStudioCapabilities.BrowseDatabase` (off by default, included in
  `All()`, turned off by `ReadOnly`) and `MartenStudioOptions.BrowsableSchemas` (exact schema names, or
  `"*"` for every schema the studio's role has `USAGE` on except Postgres' own and extensions'). The
  structure of the store's own schemas is shown without either, as the Schema screen always did; everything
  else, every non-Marten definition and every row needs both, and the write policy is asked with `TenantId
  = null`, because nothing filters a non-Marten table by tenant. Reads run in the read-only session as
  `SqlConsoleRole` under `QueryTimeout`, lists lock nothing they list, and every first read of a relation's
  rows is audited by its filter and sort (the values go to the log, never to the Activity ring). `"*"`
  without a `SqlConsoleRole` logs event 9230 once at the start of a host that maps the studio:
  `BrowsableSchemas` limits what the screens show, `SqlConsoleRole` what can be read.
- What the browser never shows: the rows of Marten's document and event tables (they are read in Documents
  and Events, where tenancy, soft delete and the serializer apply) or of any `mt_` table; the rows of a
  foreign table, directly or through a view, a partition or an inheritance child; hidden document types
  (`IsDocumentTypeVisible`) with their indexes, keys, triggers, partitions and leftover per-type functions,
  and the rows of any view over them; per-tenant partition and sequence names; and the names of schemas the
  visitor may not see, which read `‹withheld›` inside every definition, type and default. A view is judged
  on everything it reaches — other views, functions with SQL-standard bodies, schemas named as values,
  collations and text search objects — and a view calling a function whose body the studio cannot follow is
  refused its rows while any schema is withheld.
- The Relationships screen draws the database's other tables beside the document types — Quartz.NET's, a
  legacy schema's, and keys between a document and a plain table — with a Documents / Tables / Both view,
  schema chips, composite-key labels that leave out the leading columns both primary keys share, and dashed
  NOT VALID keys; keys into schemas a visitor may not see are counted, never named. Document detail's
  "Referenced by" lists keys from those tables with their ON DELETE action and links to exactly the rows it
  counted, and a hard delete's confirmation says which rows it will cascade into and what it may change
  that it could not count.
- The Schema screen follows the browser. *Tables* classifies with the same rules (a Quartz.NET table in the
  event schema is no longer badged "event store"), rolls partitions into their parent without naming them,
  and shows a partition count or a Marten tenancy table's row count only past the browser's gate, because
  either is the number of tenants. *Functions* reads a body when its row is opened, and a body Marten does
  not own needs the browser's gate. *Indexes* treats projection and `ExtendedSchemaObjects` tables as
  Marten-managed — an apply drops their undeclared indexes, and the tab now says so — and masks withheld
  schemas. The reads give up after three seconds behind a migration's lock, and a registered store that
  will not build no longer blanks the tabs.
- Security: Schema › Check, Preview and DDL print the whole database — every tenant's partition by name,
  every document type's table — and now answer only a visitor the store policy allows for the database with
  no tenant selected, and, while a document type is hidden, one past the browser's gate. Schema › Apply is
  authorized as `(db, null, ApplySchemaChanges)` whatever tenant is selected, because `CreateOrUpdate`
  reaches every tenant, and its confirmation says so. A host with no store policy and no hidden type sees
  no change.
- Security: an operation Marten runs over a whole database is authorized for the database as a whole unless
  it holds only the selected tenant. That covers Advance high water mark and Correct progression (Marten
  moves the database's high-water mark and rewrites every progression row, so a visitor allowed one tenant
  could make every other tenant's async projections skip their unprocessed events), Rebuild, the high-water
  restart, a store-global agent, cancelling a rebuild, and the dead-letter "Rewind subscription", which
  deletes the projection's dead letters and replays events for every tenant. Without a tenant, a correction
  is authorized for every database it reaches. The SQL console, and the Mode A `EXPLAIN` and subquery lift
  it grants, are always asked with `TenantId = null`.
- Security: the tenant selector and the Configuration page list only the databases and tenants the store
  policy allows; under a store policy the selector is the allowed list with an "Other…" entry and no longer
  reveals that a store has more than 200 tenants. The README's example `MartenStoreResource` handler let a
  `null` tenant through for everybody; it now requires an explicit all-tenants claim, and
  `docs/security.md` says why.
- No warnings for states the host configured. A store whose async projections run elsewhere —
  `DaemonMode.ExternallyManaged`, which Wolverine's managed event-subscription distribution sets, or a
  coordinator that refuses daemon lookups — shows as "External" and its coordinator is never asked: the
  "could not reach the async daemon" warning that repeated at every refresh in every open tab is gone. A
  store with no async projections says there is no daemon to host. Expected conditions (a visitor's own
  timeout, the documents rail's speculative counts, event tables not created yet) are Debug; a real anomaly
  on a polled path is a Warning once per store, database and kind of failure per ten minutes and Debug in
  between (events 9212–9221, listed in the README), and a convention test holds every Warning-or-higher
  site to a written reason. Opening a document, or a definition the gate withholds, writes no audit entry
  and no security warning.
- The daemon card: a hosted daemon on a single-database master-table or sharded tenancy shows as hosted;
  the Projections page no longer asks Marten's coordinator to find or create a database by name, which on a
  sharded tenancy could provision a tenant; pause and resume are refused unless the daemon answered as
  hosted here; and a store whose event tables exist but hold no events has a high-water mark of 0 and no
  "nothing is running these projections" alert.
- The sample host seeds two non-Marten schemas beside the store — Quartz.NET's own job store (vendored,
  Apache-2.0, see `samples/MartenStudio.SampleDomain/Relational/THIRD-PARTY-NOTICES.md`) and a `legacy`
  schema covering every catalog edge case — sets `BrowsableSchemas` to both, never writes into a `quartz`
  or `legacy` schema it did not create, and has a `--no-daemon` switch. The demo-data panel's Truncate no
  longer times out at the Large preset.
- Security: a withheld schema's name is masked inside Postgres' own error text too — the database browser's
  "What Postgres said", failed counts and reference checks, the Relationships panel and the Schema screen's
  check, preview, DDL and tab notices — while the visitor's own typed value is echoed as typed. The
  Activity screen and the Overview's recent activity show a database-browser read or a SQL console run only
  to a visitor who may make it, a registered store that cannot be built or read is named only to a visitor
  that store's policy passes, and other stores' databases are enumerated only when a policy will be asked.
- The database browser's pages read nothing while prerendered, so a pasted link is one audit entry and a
  refused link one 9202/9203 per page view, and using a page whose gate is closed logs nothing. A
  "Referenced by" count past `QueryTimeout` reads "not counted" on its row instead of failing the panel
  with a Warning.
- The browser's rail is the filter, the schemas and the pins on the browser page, and adds the current
  schema's tables and views on object detail with the open object scrolled into view; every count says what
  it counts, and on a phone the rail starts closed and the tabs are one row that scrolls sideways. A link
  to an object that is not there, or not shown to the visitor, reads "Not available here" either way. The
  Relationships diagram is drawn at its natural size in a scroll region rather than shrunk, lists what has
  no key as chips below it, and labels a key that is both tables' whole primary keys "primary key (1:1)".
- Across the studio: the page heading focused after a navigation shows its focus ring only for a keyboard
  user; notices and schema prefixes meet WCAG AA contrast in both themes; the SQL console's text cells are
  table cells again; and the capability chip counts ten, lists "Reads beyond the store" (`RunSql`,
  `BrowseDatabase`) apart from "Mutating operations", and says it describes the studio's configuration
  rather than what the visitor's account may do. `ReadOnly` turns both groups off.
- The database browser waits for the scope a link names before it reads: an in-circuit Back or Forward to
  another database's page no longer reads — and audits — the previous database first, and a Rows tab column
  set remembered in the browser is part of the first read rather than a second one.
- Security: a database-browser read the store policy refuses is recorded under `BrowseDatabase`, so
  Activity and the Overview show the refused name only to a visitor who may browse. Postgres' error text
  masks a withheld schema wherever it stands as a whole word — a localized message, or a name with a space
  or a hyphen. The Schema screen withholds a failure's text instead of showing it unmasked when the schema
  list cannot be read, and a failed apply's Activity entry carries its SQLSTATE rather than Postgres'
  words, which stay in the application's log (event 9206). Activity and the Overview ask each policy once
  per scope per refresh instead of once per entry.
- The capability popover shows every option's full name (wrapping on a phone), the database rail scrolls
  only as far as the open table and keeps its header in view, a policy's refusal of a direct link says the
  store's own structure is still shown, the Relationships description sits under its title, and a table row
  with no badges is one line.

# 0.2.0

- A user-interface pass, measured in a real browser at 1440×900, 1280×720 and 390×844 in both themes.
  Every screen was captured before and after, and every table, header and card below was found by
  measuring the content column rather than by looking at it.
- Wide tables scroll instead of being cut off. The shell hides horizontal overflow so it never grows a
  scrollbar of its own, and no table had a scroll container of its own, so anything wider than the
  content column was simply clipped with no cue: Event types lost four of its six columns at 1440
  because the .NET type column printed the assembly-qualified generic name, Schema → Tables lost its
  scan counters, Activity wrapped a timestamp onto four lines, and on a phone every table was cut.
  Every data table now sits in a labelled, keyboard-reachable scroll region (`role="region"`, a name,
  a tab stop) with an edge fade that appears only on the side that hides content and never over the
  focus ring. The .NET type column shows the C# name (`Compacted<DailySales>`) with the recorded name
  in its tooltip; Activity's store, database and tenant became one Scope column and its timestamp no
  longer wraps; the inline-projection note is no longer shouted in capitals by the header cell it sits
  in. A convention test fails the build if a table is ever added outside a region again.
- The streams list says each id once, on one line. A row used to render the stream id as a truncated
  link and again as a full-width copy box, then wrap its timestamp, so 25 streams took 1 726 px and the
  table overflowed a laptop. The id is one untruncated link with an icon-only copy button beside it
  (`CopyButton`, which announces "Copied" to a screen reader), the per-row feed shortcut is gone (the
  stream page has it), and rows are one line.
- Event cards carry one toolbar's worth of controls, not one toolbar each. Every card in the feed, the
  stream timeline and the dead-letter detail embedded a full JSON toolbar — seven controls per event,
  59 events, a 14 919 px page. A card is compact by default, with a copy button and a "more" toggle
  that reveals the full viewer toolbar for the one event being investigated; the stream moved onto the
  identity line and the `mt_dotnet_type` chip shows the short type name. The feed is 8 499 px, a card
  is 165 px instead of 287, and a phone card no longer overflows its column. Closing that toolbar also
  clears a search it can no longer show.
- The documents browser. The search-syntax help was a flex sibling of the search box and opening it
  shrank the input to 150 px while everything reflowed; it is a popover under the search row now and
  moves nothing. The default list columns are the id, last modified, every duplicated field and the
  size: `mt_version` and `mt_dotnet_type` (the same value on every row of a non-hierarchy collection)
  stay in the column chooser and in `?cols=` but are off by default, and a GUID id is never
  truncated. The list query always reads the badge columns, so a pasted `?cols=id` shows a soft-deleted
  row as deleted rather than live. The detail header went from nine same-weight buttons to six
  controls: Open in Query, Download, a Copy menu (JSON, id, C# record), chevron previous/next with real
  names, Edit and Delete.
- The shell on narrow screens. The phone header stacked tenant, capability chip, time zone and theme
  on four rows; time zone and theme are preferences and live behind one gear button in a Preferences
  popover at every width, so the header is one 56 px row on a phone with the scope pickers still in it.
  The collapsed rail hid its link labels with `display: none`, which also removed their accessible
  names; they are visually hidden now and every link carries a tooltip. Page-header actions wrap under
  a long title instead of over it, the six Overview tiles fit one row, and the Overview's lists wrap
  on a phone.
- The `Marten` floor is `9.31.0` rather than `9.35.0`, so a host already on 9.31 takes the package
  without moving forward; NuGet resolves the higher of the two, so a newer host is unaffected. Every
  stable 9.x was probed downward through restore, build and both test suites: 9.30 and below create
  the extended progression columns only when the flag is on (Marten #5309 ungates them in 9.31, and
  the studio reads them), 9.22.4 to 9.23 have no source-generated dispatcher for the aggregate time
  travel, 9.22.3 and below lack `ShardName.HighWaterMarkFor`, 9.19 and below lack `ShardFailure`, and
  9.0.0 restores with a critical advisory. The walls are recorded in the package budget.

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
