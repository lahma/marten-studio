# Decision notes

Evidence and history moved out of `AGENTS.md`, word for word, so that the rules stay short. A rule is in `AGENTS.md`;
this file says how it was learned. Read the section for a rule before changing it.

## Hard rule 10

Every metadata column is optional — but `Metadata.X.Enabled` alone does not say whether a column exists (verified
against Marten 9.35's `DocumentTable`, 2026-09-14): `tenant_id` exists iff the type is conjoined, `mt_doc_type` iff it
is a hierarchy, `mt_deleted`/`mt_deleted_at` iff its `DeleteStyle` is soft (read by casting the `IDocumentType` to
`Marten.Schema.DocumentMapping`), and only the rest honour `Enabled`; a store with `DisableInformationalFields()` has
no `mt_last_modified` at all.

Marten registers a *search-only* duplicated field for every metadata column a Marten attribute named a member for —
`[Version]`, `[CreatedAt]`, `[LastModified]`, `[TenantId]`, `[CorrelationId]`, `[CausationId]`, `[LastModifiedBy]` — so
that a LINQ comparison against the member reads the column instead of the JSON. Each one carries the *metadata*
column's name (`mt_version`, not a column of its own) and is marked `OnlyForSearching`; Marten's own `DocumentTable`
builds its columns from `DuplicatedFields.Where(x => !x.OnlyForSearching)` and creates nothing for one.

Getting this wrong named `mt_version` twice in a select list and rendered two `<tr>` under one Blazor `@key`, which
ends the circuit rather than the render: issue #1, a 500 on the detail view of every document whose type has a
`[Version]` property.

They also appear *late* — `DocumentMapping` holds the `DocumentSchema` that registers them behind a `Lazy<T>`, so a
mapping from a store that has never built its schema reports none of them, which is every unit test here, and a store
that has served one query reports them all, which is every application.

## Hard rule 14

The first two *apply migrations*: `IMartenDatabase.AllSchemaNames()` is
`AllObjects().Select(x => x.Identifier.Schema).Distinct()`, `AllObjects()` is
`BuildFeatureSchemas().SelectMany(x => x.Objects)`, and Marten's `BuildFeatureSchemas()` is
`StorageFeatures.AllActiveFeatures(this)` — which below Marten 9.50.3 reaches `database.Sequences` whenever any
document type has a numeric or HiLo id. That property is a `Lazy<SequenceFactory>` whose factory calls
`generateOrUpdateFeature(...)` and blocks on it, so it runs `Migrator.ApplyAllAsync` under the database's own
`AutoCreate`, the default being `CreateOrUpdate`.

**Below 9.50.3 a single `AllSchemaNames()` call on an empty schema creates `mt_hilo` and `mt_get_next_hi`** — proven
live by the P7 review, 2026-09-14, which is a studio that wrote DDL because somebody opened a tab. From 9.50.3 (#5665)
`AllActiveFeatures` yields `database.SequenceFeature`, which is described and never applied, so the enumeration is
offline; the rule stands at every version because the floor has the hazard, and `SchemaNoDdlLiveTests` pins it at the
floor and its absence from 9.50.3 (`RuntimeFeature.OfflineSchemaEnumeration`).

**Nor does a read path run a Marten LINQ terminal** (`ToListAsync`, `CountAsync`, `FirstOrDefaultAsync` and the rest),
`ToCommand`, `ExplainAsync` or `ToPreviewCommand` - nor `LoadAsync`, `LoadManyAsync` or a `session.Events` read such as
`FetchStreamAsync` or `AggregateStreamAsync` - because each calls `EnsureStorageExistsAsync` for its type (read at
9.39.0, 9.50.2, 9.51.0 and 9.54.1), which under the database's own `AutoCreate` creates or migrates that storage, and
below 9.50.3 through `AllObjects()` `mt_hilo`, the first time the process asks; reads use the studio's own SQL, and
`StorageEnsuringCallTests` allows the remaining calls only to the capability-gated writes and to F7's LINQ composition,
which `MartenLinqCommand` builds into SQL without it.

Everything schema-related is **per database, never per store**: the same four members exist on `IMartenDatabase`
(through `Weasel.Core.Migrations.IDatabase`) and answer only for the database the visitor resolved, whereas
`store.Storage`'s apply reaches *every* database a `DynamicMultiple` store knows about (and below 9.51 dropped its
`AutoCreate` override, #5693).

And the wording half of the rule, because it was got wrong once: **`AutoCreate.CreateOrUpdate` is not additive.**
Weasel's `TableDelta.WriteUpdate` — which is what an `Update` delta writes, and `Update` is exactly what
`CreateOrUpdate` allows — "emits `drop index` for every physical index the configuration does not declare,
`drop column` for every extra column, `alter column … type` for a changed one,
`alter table … drop constraint … CASCADE` for a changed primary key, and for a changed partition scheme a
`create table … as select` / `drop table … cascade` pair that copies the whole table out and back. Only `Invalid` is
refused, and `Invalid` is a much narrower thing than 'this will lose something'."

Apply runs under `CreateOrUpdate` and never `AutoCreate.All`, the preview is rendered under the same mode so what is
read is what is run, `MigrationRisk` lists the destructive statements in the dialog before the typed confirmation -
and, apart from them, notes that remove nothing: an index built without `CONCURRENTLY` on an existing table, and Weasel
9.46's partition-index resume, whose `DROP INDEX CONCURRENTLY IF EXISTS` of an unattached child, rebuilt and attached
at once, is not counted as a drop - and the typed string travels to the service rather than being re-supplied by the
page.

**The stepper folds with the invoker and nothing else (M1, 0.6).** Time travel and the stepper fold through
`IAggregateFolder.FoldAsync`, Marten's `AggregatorFor<T>().BuildAsync`. The stepper hands it one event and the state
before it wherever `AggregateStepShapes` reads the dispatch as per-event (generated Create/Apply/ShouldDelete,
`Evolve`/`EvolveAsync`). It folds from the stream's first event, up to `AggregateWalk.FromTheStartLimit`, for a
`DetermineAction(Async)` override, a `DeleteEvent<T>()` event, or an aggregator it has not read. No studio code calls
`DetermineActionAsync`, `Evolve` or JasperFx's `ISteppableAggregation` itself, and `AggregateSteppingTests` holds every
step to Marten's single fold at both Marten ends. Known limit: an `Apply` reading the version member or an
`ApplyMetadata`-set member sees, step by step, the value after the previous event (Marten's single fold sets those once
at the end).

## Hard rule 15

An unpinned session on a multi-database store reads the store's default rather than the database the scope selector
named and the audit entry recorded, which is the same class of bug as reading another tenant's rows.

**Never run a user-supplied clause through `Query<T>(string)` / `QueryAsync(Type, string)`**: Marten's
`UserSuppliedQueryHandler` adds no tenant and no soft-delete predicate, so a where clause typed into the studio reads
every tenant's rows and every deleted one — live-proven by the P6 review.

`JSDisconnectedException` derives from `Exception` and *not* from `JSException`, so
`catch (JSException) { } catch (InvalidOperationException) { }` misses exactly the case that happens on every closed
browser tab — and an exception escaping a component's `DisposeAsync` takes the rest of that disposal with it, leaving
the `DotNetObjectReference` that pins the component undisposed and the page's `CancellationTokenSource` uncancelled.

## D3 — Two dependencies a package

No direct `Npgsql` either; it arrives through `Weasel.Postgresql`, and adding it would let the two drift. The Marten
floor is a *security* floor (the two 9.39.0 advisories, *Package budget changes* 2026-10-15), and both suites run at it
and at a pinned latest-tested 9.x (`MartenStudioMartenFloor` / `MartenStudioMartenLatest` in
`Directory.Packages.props`, the `marten-latest` workflow); the package is only ever packed at the floor. The studio's
own compilation runs without the JasperFx source generator Marten bundles, so the shipped assembly carries no JasperFx
generator attribute and has the same public API whichever Marten built it. Since 0.5 the assets pack is
PostgresStudio's, which MartenStudio references, and PostgresStudio - having no Marten to bring it - references
`Npgsql` directly; "no direct `Npgsql`" is MartenStudio's rule alone (*Package budget changes* 2026-10-20 and
2026-10-26, D30). PostgresStudio's Npgsql floor is pinned only in the projects that set `PostgresStudioPinsNpgsql`, by
a conditional central row, and is never above the Npgsql Marten's floor resolves (`NpgsqlVersionTests`); MartenStudio's
nuspec still lists the assets pack, written there by `CentralPackageTransitivePinningEnabled` rather than by a
reference of its own.

## D6 — Reads are raw parameterised SQL, writes go through a Marten session

(There is no `mt_upsert_*` function to call anyway: Marten 9.35 installs none, and the upsert is inline SQL in the
generated write path — verified against a live schema by P7, 2026-09-14.)

## D7 — Document edit shows a round-trip diff before saving

The id check (M5, 0.6) was proved live at the floor before it was fixed: a save whose JSON carried another id, on a type
without Marten's optimistic concurrency, passed the version check on the open document and overwrote the document
with the new id. What M5 read of Marten 9.52 and 9.53's document members, and left alone: `UndeleteDocumentAsync`
opens an unpinned `LightweightSession(tenantId)` and re-stores the row through its CLR type, so undelete stays the
studio's own, under `DeleteDocuments`; the metadata ordering picks its database by tenant and pages with an offset and
an exact `count(*)`, so the Order control is the studio's own keyset read; `QueryEventData<T>` runs LINQ, which ensures
storage, its tenant form opens an unpinned `QuerySession(tenantId)`, and its null form calls `Events.AddEventType<T>()`
on the host's store. Nothing in `IDocumentStoreDiagnostics` could replace a studio read: each member is unpinned, runs
outside `ReadOnlySqlSession`, counts with `count(*)` or reads through `FetchStreamAsync`.

## D10 — No SignalR hub in v1

Dead letters are grouped in C# over a bounded read, and a group's members found again in SQL (M3, 0.6). Measured on
10,000 dead letters (PostgreSQL 17, M3's report): an aggregate that fingerprints every message in SQL - ten nested
`regexp_replace` passes a row - spent about 950 ms of the database's CPU per read, nearly all of it in those passes,
where reading the five fields and each letter's event type (`DeadLetterGroupQueries.BuildGroupRows`, an index probe per
letter) and fingerprinting in C# took about 180 ms end to end. `DeadLetterEvent` stores no event type, so each letter's
event is looked up in `mt_events`; the daemon writes `ApplyEventException`'s "Failure to apply event #N Id(guid)" as the
message, so its fingerprint is near-constant and the exception and event types are what tell causes apart; a
deserialization letter carries no `TenantId`.

## D22 — `blazor.web.js` reaches the host through a `PackageReference`

As of .NET 10 that file is neither in the shared framework nor embedded in any ASP.NET Core assembly: it is a static
web asset in `Microsoft.AspNetCore.App.Internal.Assets`, which the SDK resolves only for a project whose
`RequiresAspNetWebAssets` is true — and the Web SDK defaults that to true only for a project that has `.razor` Content
items *of its own*. A host that adds Marten Studio and writes no Blazor components has none, so
`GET /_framework/blazor.web.js` answered 404, the studio prerendered, never became interactive, and nothing in the
build said a word. **The package cannot set the property on the consumer's behalf** — that is a restore-time decision,
and NuGet deliberately excludes package-provided `.props`/`.targets` from restore evaluation
(`ExcludeRestorePackageImports`), measured rather than assumed. A plain `PackageReference` is what does reach the
consumer: the pack's `buildTransitive` targets run in any Web SDK executable that references the studio, directly or
transitively, and define `_framework/blazor.web.js` as **that project's own** static web asset. So the host serves the
script from its own `MapStaticAssets()`, the studio's mapped `{Path}/_framework/blazor.web.js` endpoint forwards to it,
and nothing here ships a frozen copy of a Microsoft script that would go stale one patch after the package was built.
The three-tier resolution in `MapStudioFrameworkScript` stays — host web root, host endpoint, then a packaged copy that
is now normally absent (`_content/PostgresStudio/_framework/blazor.web.js`) — because the host's copy must always win
where there is one. Since 0.5 the pack is referenced by PostgresStudio, so a MartenStudio host reaches it two hops away
(host, MartenStudio, PostgresStudio, the pack), measured by PS-0 with an API-only host (*Package budget changes*
2026-10-20). *The alternative that was tried and dropped:* vendoring the file, by setting `RequiresAspNetWebAssets` for
the RCL itself and republishing the resolved file as `_content/MartenStudio/_framework/blazor.web.js`. It worked, and
it coupled the shipped script to whatever ASP.NET Core patch built the package.

## D24 — The packages ship no Blazor JS initializer

A file named `*.lib.module.js` in an RCL's `wwwroot` *is* a JS initializer — nothing declares it, the filename alone is
the declaration — and Blazor loads the initializers of every referenced RCL into **every** Blazor app in the process. A
host that runs its own Blazor Server app therefore fetched the studio's initializer from the **site root** on every
page it served, including pages that never mention the studio; with `AuthorizationPolicy` set, that path is authorized
too, so the fetch was redirected to the login page, came back as `text/html`, and the browser logged a MIME-type error
on every page load — the host's own unauthenticated login page included. That is the host's behaviour changed by the
act of referencing this package, which is the thing this project exists to refuse, and it made `AuthorizationPolicy`
unusable for exactly the hosts most likely to set it (issue #2).

The file is an ordinary classic script, PostgresStudio's `wwwroot/js/studio.js` since 0.5 (`marten-studio.js` before),
loaded by the shared shell document `StudioDocument.razor` with a **relative** `src` so it resolves against the
studio-rooted `<base href>` and lands on the `_content/PostgresStudio` mirror under the mount path; its global keeps
the name `window.martenStudio`. It sits at the end of `<body>`, before `blazor.web.js`: the prerendered markup is
parsed by then, so the remembered theme is still applied to it — sooner than `afterWebStarted` ever ran — and every
global is defined before the circuit that calls them starts. Measured, not assumed: the built asset manifest has no
`js-initializer` dependency group at all. *The alternative that was rejected:* keeping the initializer and making it
no-op outside the studio's circuit. It fixes nothing — the fetch, the redirect and the console error all happen before
any of its code runs. `NoJsInitializerTests` is the regression, on both halves: no `*.lib.module.js` under `wwwroot`,
and a shell that still asks for the script it replaced it with. Measured in 0.3.0: a studio navigation resets the
`<html>` element's attributes even though the window survives, so state the script keeps there (the keyboard-or-pointer
input modality behind the heading's focus ring) lives in JS and is re-applied by an observer.

## D25 — Every data table sits in its own labelled scroll region

The cost was that a table wider than the content column was simply cut off with no cue and no way to reach the columns
on the right — measured in the 0.2.0 UX pass at 1 691 px of table in a 1 200 px column on Event types, and every table
on a phone.

## D26 — The stylesheet's UX sections are appended in cascade order

The 0.2.0 pass landed five parallel packets in one file. Each put its new rules in a titled section at the end
(`UX-2`, `UX-3`, `UX-1`, `UX-4`, `UX-5`, in that order) and edited an existing rule in place only where its report
says so; several sections deliberately re-declare an earlier selector (`.ms-event-card`, `.ms-projection-note`,
`.ms-overview-list-stream`, `.ms-doc-detail-actions`) and win by source order. Keep that when touching those
selectors: a rule added *before* the section that overrides it is a rule that does nothing, and `StylesheetTests`
(plus its `.Shell` part) pins the ones that were measured. 0.3.0 appended `DB-4`, `DB-6`, `DB-7`, `DB-5`, `UX-6` and
`UX-7` in that order under the same rule; `UX-6` re-declares a few earlier selectors on purpose (the notices'
contrast, the heading's focus ring) and `StylesheetTests.DatabasePolish` pins them, as `StylesheetTests.FinalPolish`
pins `UX-7`. 0.4.0 appended `HEALTH-1`, `HEALTH-2`, `HEALTH-4`, `HEALTH-3`, `EVENTS-4`, `QUERY-LINQ`, `PGV` and `UX-8`
in that order, under the same rule; `StylesheetTests.QueryRefusal` pins `UX-8`. In 0.5 the file moved whole, to
PostgresStudio's `wwwroot/css/studio.css`, with its section order unchanged, and it holds both studios' rules; a
Marten-only sheet, if one is ever needed, is linked *after* it. 0.5.0 appended the wave-5 sections W5-CAP, W5-R2,
W5-S2, W5-V1, W5-Q1, W5-AUR, W5-B1 and W5-API in that order under the same rule; each section's own stylesheet test
reads it out of `studio.css` by its header.

## D27 — The database browser

0.3.0 lets the studio show what else lives in the store's Postgres database — a Quartz.NET job store, a legacy schema,
another library's tables — as tables, views, functions, triggers, sequences and types.

Known limit: features added with `Storage.Add(IFeatureSchema)` are reachable only through Marten's internal
`AllActiveFeatures`, which hard rule 14 forbids, so they are classified by the `mt_` prefix alone.

Free text is masked and structure is blanked: every catalog read pins `search_path` to `pg_catalog`, so every name
Postgres deparses is printed with its schema, and `WithheldNames` masks a withheld schema's qualifier — in types,
defaults, CHECK and index definitions, view, routine and trigger definitions, `SET search_path` and comments — as
`‹withheld›`. A view that reads from or refers to anything in a withheld schema is refused its rows and its query; so
is a trigger whose function is withheld, and, while any document type may be hidden, a view that calls user code or
reads a document table no store declares.

On PostgreSQL 18 the `pg_sequences` view no longer checks the privilege itself: it calls `pg_sequence_last_value` for
every row and every reader, and that function takes a `RowExclusiveLock` on each sequence even when it then returns
null - so does any read of the view, whatever columns it selects. Lists read `pg_catalog.pg_sequence`; the one value
read goes through `has_sequence_privilege` first, which on 18 is what keeps the lock away (measured, B0, 2026-10-20).

Nor does a list call a deparse function that opens what it describes: `pg_get_indexdef` in every form, `pg_get_expr`
with a relation (a default's `adbin`, a partition's `relpartbound`), `pg_get_viewdef` and `pg_get_constraintdef` of a
table's CHECK each take a transient `AccessShareLock` and queue behind a migration's `ACCESS EXCLUSIVE` on 14 to 18
(measured 2026-10-26); a key's or foreign key's `pg_get_constraintdef` and a trigger without WHEN do not.
`pg_get_partkeydef` and `pg_get_expr(relpartbound, <relid>)` open the relation and queue too, while
`pg_get_expr(relpartbound, 0)` - no relation - does not, and is how a list reads partition bounds (14.24 and 18.6,
measured 2026-10-26; `LintMatrixLiveTests`). A list that must deparse reads `pg_locks` once and leaves a locked table's
text out (`HealthTableQueries.IndexesSql`); a by-oid detail read may wait for its one relation, under `lock_timeout`.

Another store's key and alias are shown only to a visitor that store's store policy passes, and its Marten-managed rows
need its store and write policies. Known limits: a routine's body is shown as its author wrote it (a name it resolves
through its own `SET search_path` or dynamic SQL is not masked); a hidden type nothing in the process has touched, and
that is not registered with `Schema.For`, is unknown to `AllKnownDocumentTypes()` and is listed by its `mt_` name as
infrastructure until Marten learns it (its rows are never read, views over it are refused); a domain CHECK that calls a
function is not followed; a schema created after the 60-second schema-list cache is masked only once the list is read
again.

Rows are never read from a foreign table — nor from a partitioned table with a foreign partition, a table with a
foreign inheritance child, or a view reading any of them (a materialized view's rows are local and are read). A view
over Marten's own tables, or over another store's whose policies refuse the visitor, is refused its rows, and so is a
table or view whose partitions or children sit in a withheld schema. A view is judged on what it reaches through other
views and through functions with SQL-standard bodies (Postgres records their dependencies), and on the schemas it names
as values, its collations and its text search objects. A function whose body is source text cannot be followed: a view
calling one is refused its rows while any schema the reading role may read is withheld (for `SECURITY DEFINER`, while
any schema is withheld) — which is why the sample writes `legacy.format_employee_name` with a `RETURN` body. Residual:
under `"*"` with `SqlConsoleRole` narrowed, such a view is read, and what its function reads is bounded by that role
alone; a definition naming a hidden type's table only through dynamic SQL is not detected; a foreign partition attached
less than a minute ago may not yet be seen.

Every first read of a relation's rows is audited by signature (filter, sort, paging mode — never the cursor), per
store, database and relation, and never from a static render: the browser's pages read only once interactive and once
the scope their address names has been applied (an in-circuit Back or Forward to another database reads nothing against
the previous one), so an explicit request the gate refuses is one 9202/9203 per page view and using a page whose gate
is closed refuses nothing. On Activity and the Overview an entry under `BrowseDatabase` or `RunSql` is shown only while
the capability is on and the write policy passes the reader for its scope; a browser read the store policy refused is
recorded under `BrowseDatabase` too, and each policy is asked once per scope per sweep. Postgres' own error text about
a browser read is masked like everything else (qualifiers, and every whole token that spells a withheld schema —
whatever word precedes it, in any server language, a name with a space or a hyphen matched whole), except a value the
read bound, which is echoed as typed; the Schema screen withholds a failure's text outright when the schema list it
would be checked against cannot be read, and a failed apply's Activity entry carries its SQLSTATE, not Postgres' words,
which stay in event 9206; row reads do not pin `search_path`, because a bare `=` must find `citext`'s own operator, and
a name printed unqualified names no schema. The "could not be built" notice names another store only to a visitor that
store's policy passes, and other stores' databases are enumerated only when a store policy — or a write policy, while
`BrowseDatabase` is on — will be asked, and never for a `DynamicMultiple` store.

The one exception is the dead-letter rewind on a store with `UseTenantPartitionedEvents`, from Marten 9.51 with
JasperFx 2.91 (`RuntimeFeature.TenantSubscriptionRewind`): the daemon's per-tenant overload stops, rewinds and restarts
only that tenant's agents, and Marten deletes only that tenant's dead letters of the shard (#5700, jasperfx#1025), so
it is authorized as `(store, database, tenant)` - and made only for an agent the daemon in this process runs, with that
agent's own names, because the overload starts the tenant's agents wherever it is asked. On such a store the
store-global rewind is never made, and Skip event is the tenant overload of `MarkEventsAsSkipped` (#5698), asked about
the tenant as before. Below either, both stay refused (SEC1-fix4).

Orphaned progression rows (M47, 0.6) are listed and never removed. Marten does have a removal by name,
`DeleteProjectionProgressByShardNameAsync` (present at 9.39 already, marten#4785), but it ensures the event storage
first, so it may only ever be a capability-gated write; a Remove button built on it is left to a later release, and
0.6 shows the call, and the equivalent SQL, as text for the host.

Enumerating a dynamic tenancy's databases is not read-only, at any Marten through 9.54.1: `AllDatabases()`,
`Tenancy.DescribeDatabasesAsync()` and 9.50.1's `IEventStore.TryDescribeDatabasesAsync()` all run
`Tenancy.BuildDatabases()`. Once per store instance, that applies a master-table or sharded tenancy's lookup table -
gated, and from 9.51 (#5692) run, under the tenancy's own `AutoCreate` when it sets one and the store's
`AutoCreateSchemaObjects` otherwise; below 9.51 always run under the store's (`maybeApplyChanges` at `V9.50.2`,
`v9.51.0` and `V9.54.1`). So a tenancy that sets none applies under the store's mode at every version, `All` included,
which drops and recreates a table it cannot alter; and below 9.51 a store's `None` is promoted to `CreateOrUpdate` when
the tenancy's own `AutoCreate` is set to anything but `None`, where from 9.51 the tenancy's own mode runs instead.
9.51's `ShardedTenancy.FindOrCreateDatabase` also builds a pooled database id on a cache miss (#5702). It also upserts
the tenancy's seed rows. On a single-server tenancy it creates every configured database that does not exist yet, under
no `AutoCreate` at all. The studio enumerates on the first navigation, so in a process that runs no daemon of its own
it can be that first caller; a host whose daemon runs in-process, or that applies on startup, has always enumerated
before it. A known limit, not a per-poll cost.

**D27 in PostgresStudio.** The gate is reused unchanged, with `PostgresStudioResource` and the source policy in place
of the store's, and `BrowsableSchemas` playing "the store's own schemas": the schemas it names exactly are the data
source's own, and their structure is shown to anyone the source policy admits, while their rows and definitions need
`BrowseDatabase` and the write policy (asked with no tenant: a Postgres Studio scope has none). A schema only `"*"`
admits needs both for its structure too, because `"*"` is a statement about the database rather than the configuration;
a system schema is never browsable, and only an exact entry admits an extension's. With nothing named, the list is
`public`, applied after every configuration, so a list that names schemas replaces it. There are no declarations, so
the classifier holds no store: the `mt_` prefix rule is the only thing that names an object Marten's, and it fails
closed - such an object's rows are never read, nor a view over one, its definition is shown only with `BrowseDatabase`,
and the database page says once that the database holds a Marten store and points to Marten Studio. PostgresStudio
cannot honour another application's `IsDocumentTypeVisible`, so a Marten database a host would hide types in belongs in
Marten Studio, where that delegate applies.

**`ViewDefinitions` (issue #22, 0.8).** `BrowseDatabase` covered two things, rows and definitions, and Host B of issue #8
wanted the second without the first: a view's query, a trigger function or a function body is schema the application's own
migrations wrote, and exactly what Statements or a health finding points at, while its rows may be credentials or personal
data the studio cannot filter. So definitions have a second way in. `ViewDefinitions` admits the definitions - and, in
Marten Studio, the structure - of the schemas `BrowsableSchemas` names *exactly*; `"*"` never reaches it, because "every
schema the role can use" is a statement about the database, and a definition can carry a literal. `BrowseDatabase` implies
it, so no existing host moves: `DatabaseAccess.RequireDefinitionAsync` asks the write policy about `BrowseDatabase` first
and only then about `ViewDefinitions`, and records a refusal under the first that is on (`ReadCapability`), so a host that
never enabled the new one sees the same Activity entries. Three choices worth keeping: (1) the widened gate is
`DatabaseAccess.ObjectGateAsync`'s alone, read by the object browser and the Schema screen's Functions tab - Health,
Storage, Maintenance, Sessions and the LINQ page's masks read `GateAsync`'s, where only `BrowseDatabase` opens anything, so
the new capability adds no name or number to any other screen; (2) a Marten-owned object outside the store's own schemas
stays `BrowseDatabase`'s (today's rule, which already said an `mt_` definition shows only with it), and the store's own
Marten definitions stay the Schema screen's, free; (3) a sequence's current value stays `BrowseDatabase`'s alone
(`DatabaseGate.SequenceValueAccess`, `RequireBrowseAsync`): it is data, not a definition - it says how many rows a table
has had. `DefinitionAccess` used to decide it with the definitions, and the first cut of `ViewDefinitions` moved it along;
review put it back. A sequence's definition (type, start, increment, range) is on its list row, structure, as ever.

## D29 — The health advisor

The name of the role the studio reads as (`SqlConsoleRole`, or the store's own login where none is set) is
Database-gate information, and so is the login the source's data source connects as, which S04, S05, S08 and S10 name.
A "not checked" row names it, and the grant it lacks, to a visitor the check's own gate admits. C02 and C03 name the
login role too (`session_user`, in the role-limit evidence and the quiet row), at the check's own Database gate. An
Aurora instance's identifier (`aurora_db_instance_identifier()`, `aurora_replica_status().server_id`) is the cluster's
own name for one of its instances, not a session's, a role's or a client's, so it is Database-gate information too:
R03's `aurora-reader` evidence, the Replication page's Aurora replicas, the "Answered by" line, the Server page's
Aurora section (the cluster's instances, the instance's waits, this database's commit latency and DML) and the capacity
line need no `ViewDatabaseActivity`. A session's Aurora waits (`aurora_stat_backend_waits`) are `Activity`: they are
read only for a visitor through that gate, and shown as counts and shares, never as a time.

A finding that names a table in a schema outside the store's own - an `ExtendedSchemaObjects` table in a schema of its
own, `HealthFinding.GatedSchemas` - also needs the database browser's gate for that schema, which D27 withholds and
masks otherwise; the badge, the banners and the Critical toast resolve no scope and cannot ask that gate, so they
withhold such a finding from everybody, and T12 and T13, being the database's, name such a table generically rather
than vanish.

The first exception is to D13 and D27, which call `SqlConsoleRole` the real boundary: T02, T22 and T23 read
`pg_stat_statements`' counters and texts as the store's own role, because the extension shows any other role
`<insufficient privilege>` for Marten's statements and a narrowed console role - the careful host's configuration -
would see nothing of the store. The reads are fenced to `userid = current_user` and this database
(`StatementChecksFenceTests`), so they show what that role could read anyway; a text still carrying a literal as
written, or that is not a plain query or write, is withheld; plans run as `SqlConsoleRole`. The Statements page (W5-Q1)
is the same read: it shows the statement checks' own `pg_stat_statements` read, widened to the hundred statements with
the most execution time (and on Aurora the most storage reads). The fence is unchanged, T02's withholding and comment
masking are applied at read, and every text is behind the Activity gate and the visitor's own mask. Every other health
read, and every console statement, stays under it.

**Two studios, one engine.** A check in MartenStudio's assembly runs only on a Marten source (the registry's source
kinds), and the shared checks run on both. Postgres Studio's `Database` gate asks the source policy about the source's
key and the database's own identity, as Marten Studio asks the store policy about the store's. A relational table's
remedy may be migration SQL, shown "for your own migrations - the studio never runs this" and built only in
`MigrationSqlText`, which quotes every name and gives no statement for a name it cannot; a Marten table's remedy is
never DDL - a `StoreOptions` line in Marten Studio, nothing in Postgres Studio.

**The statistics window** (`StatisticsWindow`) is the one answer to "since when have these counters counted". It is the
later of `pg_stat_database.stats_reset` and the earlier of the two shared views' `stats_reset` (`pg_stat_bgwriter` up
to 16 or `pg_stat_checkpointer` from 17, and `pg_stat_archiver`): `max(database, min(shared, archiver))`. So a
`pg_stat_reset_shared()` of one view never shortens it. A clean restart is not a reset, and neither is the watched
standby's promotion, nor the standby following one upstream; a failover starts the window at the promoted server's
reset times. A verdict that needs a long window gives none until the window covers it, and none while it looks reset:
T01's, T05's and T06's seven days and C08's and C11's day are measured against it.

## D30 — PostgresStudio

0.5 splits the studio into two Razor Class Libraries. **PostgresStudio** is everything that is about a PostgreSQL
database rather than a Marten store: the shell, router and re-rooting (D11); the scope picker and the three
authorization layers (D5); the capability guard and the audit; the database browser (D27), Sessions, the SQL console
(D13) and Relationships' foreign keys; the health engine with 44 of its 73 checks in 0.5.0 (D29), and the Statements,
Maintenance, Storage, Replication and Server pages; the diagnostics API (D31); the JSON toolkit, the stylesheet and the
script. It references `Npgsql`, the assets pack and the shared framework, and nothing from Marten, JasperFx or Weasel
(hard rule 16). **Marten Studio** references PostgresStudio and Marten, and adds the Marten screens, the Marten checks
and the Marten halves of every seam.

*Why one core package rather than shared source.* A host that has Marten takes one package and gets one copy of the
core. A host that has only Npgsql takes the core alone, and the user asked for exactly that. Linked source would put
the same types in two assemblies.

*Why `InternalsVisibleTo` and an exact pin, not a public extension API.* Marten Studio extends nearly every core
service. Publishing those seams would multiply the public surface (hard rule 7) for a plug-in model nobody asked for.
With IVT the two packages are one program in two files, and the nuspec says so with `[x.y.z]`, which
`MartenStudioPinsPostgresStudioExactly` in `MartenStudio.csproj` writes (it brackets NuGet-private item metadata) and
`Pack` asserts in the nuspec text, so a NuGet that renamed those items fails the build rather than shipping `>=`. A
host that upgrades one package without the other gets NU1608 at restore for a direct reference to another version (a
warning, an error under `TreatWarningsAsErrors`), NU1107 for a cousin that needs another version, and NU1605 for a
stale direct one. Under `CentralPackageTransitivePinningEnabled` with a central `PackageVersion` for PostgresStudio at
another version it gets nothing: SDK 10.0.402's resolver drops PostgresStudio from the graph silently, a green build
and a `FileNotFoundException` at startup (PS-0; the closest upstream issue is NuGet/Home#14698). So `AddMartenStudio()`
first runs `PostgresStudioVersionGuard`, which names no PostgresStudio type, loads the assembly by name, compares
informational versions up to the `+`, and throws an `InvalidOperationException` that names the central pin. The public
`AddMartenStudio`'s own body stays free of PostgresStudio types - everything else is in a `NoInlining` method - and
`PostgresStudioVersionGuardTests` scans its IL to keep it so.

*What PostgresStudio browses.* The application's own singleton `NpgsqlDataSource` registrations: the unkeyed one by
default (`IncludeDefaultDataSource`, the source `default`), a keyed or otherwise-held one named with `AddDataSource`,
and a host's own set of databases - one per tenant, say - named with `AddDatabaseSource` and an
`IPostgresDatabaseSource`, which lists names and hands back each one's data source on demand. Never a connection
string, for the reason Marten Studio is keyed on `IDocumentStore`: it renders what the application configured, with the
application's role, pool and TLS. A factory is called once, with the root provider, and its result kept for the process
and never disposed; a scoped or transient registration is refused, because it would build a pool per visitor. A
database's identity is the source's own: a Marten store's database is its `Id.Identity`, unchanged; a DI-registered
data source's is its registration name; a host-listed database's is the name the host lists. It is what `?db=`,
policies, caches, history, mutes and the audit key on, so it must be stable across processes and restarts.

*D27 in PostgresStudio* is D27's gate unchanged, with `BrowsableSchemas` playing the store's own schemas and the `mt_`
prefix rule the only thing that names an object Marten's (D27).

*Routes and assets.* Every page route belongs to exactly one assembly of a studio. Compile-time roots are per assembly
(`/postgres`, `/marten`), and re-rooting is from each assembly's root, so the shared pages sit at `/marten/…` in Marten
Studio. Static assets live only in `_content/PostgresStudio/`.

*Words.* Shared sentences come from `IStudioSettings.Text` (`StudioText`): MartenStudio's instance is 0.4's literals,
pinned by `StudioTextPinTests`, so no host of Marten Studio sees a word change; Postgres Studio's speaks of data
sources, and `PostgresStudioWordsTests` evaluates every shared check in its words and fails on a store word outside a
reasoned allow-list. Shared log events keep MartenStudio's templates in `StudioLog`, and Postgres Studio declares the
same ids, levels and properties in its own words in `PostgresStudioLog`.

*One studio per process in 0.5.* `AddPostgresStudio` and `AddMartenStudio` refuse each other, and each `Map*Studio`
refuses the other's registration. The seams are DI-resolved settings, a descriptor, a source provider and source kinds
in the health registry, so that Marten Studio can later list plain data sources beside its stores in one packet.

*Npgsql.* PostgresStudio's Npgsql floor is pinned only in the projects that set `PostgresStudioPinsNpgsql`, by a
conditional central row, and it is never above the Npgsql Marten's floor resolves (`NpgsqlVersionTests`). The latest
leg runs PostgresStudio at the newest Npgsql major (a pass of its own over `postgres-studio.slnf`) as well as Marten
Studio at the newest Marten 9.x; Marten Studio on Npgsql 10 is not a tested combination (*Build*).

## D31 — The diagnostics API

0.5 gives host code a read of what the studio already reads. Issue #8's two production hosts delete their own
PostgreSQL diagnostics services, and keep at most a page of their own over this.

`IPostgresDiagnostics` is registered by `AddPostgresStudio()` and by `AddMartenStudio()`, one per process like the
studio. It lists the databases the studio would list (`ListDatabasesAsync`). For each database it returns a
`PostgresDiagnosticsSnapshot` (`GetSnapshotAsync`): the findings, by stable code and variant, and typed sections for
storage, statements, settings and extensions, and connections. Two interfaces point the other way:
`IPostgresDatabaseSource`, named with `AddDatabaseSource`, lists a host's own databases and hands the studio each one's
`NpgsqlDataSource` on demand; `IPostgresCapacitySource`, found in DI, reports compute capacity that SQL cannot see,
such as Aurora Serverless v2's ACU. PostgresStudio's public surface is D30's six registration types, the six inbound
types and this read model's twenty-one; its Verify baseline is the list (hard rule 7). MartenStudio's surface does not
change: its hosts reach the read model through the PostgresStudio assembly its exact pin names.

*Why public, when D12 keeps the data seam internal.* The read model is results, not seams. There is no
`IStudioDatabase`, no sweep and no check base class in it, so a host cannot add a check or change a threshold; that is
a non-goal of #8. The studio stays English. A host words findings in its own language, keyed on codes.

*Stable codes.* A code is a letter and two digits. It is a string, never an enum. It is never reused, never renamed and
never changes area; a retired check is marked so in `docs/health/codes.md`, and its code is never reassigned (hard rule
7). A finding's variant (`deadlocks`, `temp-files`) is frozen the same way within its code, and a check that has
variants names one on every finding. A newer studio may return a code a host has no wording for; the host then falls
back to the title, which is the studio's own wording. Severity, category, outcome, action kind, platform and table
ownership are enums with explicit values, and may gain members.

*Extend-only.* Result types are sealed, non-positional records with `init` properties and no `required` member, so a
host can build one in its own tests and a new property breaks nothing. A new section is a new nullable property and a
new request flag. The three interfaces gain members only as default interface members. `PublicApiShapeTests` holds the
package to this. The JSON snapshot carries `schemaVersion`, which only a removal, a rename or a change of meaning
bumps.

*Cost and DDL.* An API nobody calls reads nothing. A call shares the pages' sweep and section caches, so a host polling
every few seconds costs one sweep per interval per database. Fresh reads that calls start run at most two at a time per
process; a cache hit waits behind nothing. Listing never enumerates a tenancy that builds: Marten's databases are the
held ones (D27, hard rule 14). A host's database source must list without creating anything, and its names are
identities: they key policies, history and mutes, and survive a database moving server. The studio keeps per-database
state for at most 64 databases, the least recently used forgotten first (`HealthDatabaseBound`).

## Testing, item 7

Six containers, about 30 s for the folder with the images local, plus a ~110 MB pull per image on a cold CI runner.

`PgVectorLiveTests` starts `pgvector/pgvector:0.8.1-pg17` (`PgVectorServerFixture.Image`), pinned by tag to the
pgvector release whose defaults the studio quotes; an image that cannot be pulled skips that class locally, with the
`docker pull` command in the reason, and fails the run on CI (D19's rule).

`PostgresVersionMatrix` (`tests/PostgresStudio.Integration.Tests/Versions/`, an assembly fixture) starts one
never-reused server per major on first use - `postgres:14.24-alpine` to `18.6-alpine`, and `19rc1-alpine` as a preview
leg that reports and never fails a run - with `pg_stat_statements` preloaded and B0's `app`/`probe`/`stats`/`mon`
roles; `VersionMatrixLiveTests` runs the shared studio's regular and statement checks, every `MatrixReads` read, the
held-lock read and the gate table on each (MartenStudio's checks are not in the matrix).

VERS's Aurora shim (`Versions/AuroraShim.cs`) puts Aurora's functions, written from AWS's signatures, in `pg_catalog`
of a database of its own on the matrix's PostgreSQL 17. It is never applied to a database a self-hosted test reads,
because the detector would then call that database Aurora. `Aurora/AuroraShimLiveTests` proves A1-A8 on it, and that no
role short of a superuser can plant an Aurora marker.

`tests/PostgresStudio.Integration.Tests/` is this suite's Postgres Studio twin: the same rules (D19's gate, wait on the
condition, `MARTENSTUDIO_PG_REUSE`), a container labelled `marten-studio.tests=PostgresStudio.Integration.Tests` so it
never attaches to this suite's, and no Marten anywhere in its graph; it is in `postgres-studio.slnf`, so the Npgsql
pass runs it too.
