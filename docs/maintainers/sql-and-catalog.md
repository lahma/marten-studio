# SQL and catalog reads

This is the full text of D13 (the SQL console) and D27 (the database browser, with its *Reach, not scope* and *D27 in PostgresStudio* passages), moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D13

**D13 — The SQL console is off by default, and its real guard is the transaction.** `RunSql` is a
capability like any other. What makes it safe is `SET TRANSACTION READ ONLY` plus `statement_timeout` and
an optional `SET LOCAL ROLE`, not the statement parser — the parser exists to produce a nicer error
message, and treating it as the security boundary is how these features get CVEs. `README.md` says it
plainly: grant it to people you would give `psql` to.

## D27

**D27 — The database browser: one capability, one schema filter, and Marten's tables are never read raw.**
It lists the database's other objects by kind, gated like `RunSql`, whose power it is: `Capabilities.BrowseDatabase`
(off by default and under `ReadOnly`) and `BrowsableSchemas` (exact names, or `"*"` for every schema the role has
`USAGE` on but Postgres' own and extensions'). The store's *own* schemas' structure (names, columns, keys, indexes,
trigger names, estimates) needs neither, as the Schema screen shows it; everything else, every non-Marten definition and
every row needs both, plus the write policy asked **with `TenantId = null`**, as nothing filters a non-Marten table by
tenant. `Capabilities.ViewDefinitions` (issue #22) is the definitions' second way in, authorized the same way with its own
name: a visitor it admits reads the definitions and the structure of the schemas `BrowsableSchemas` names *exactly*, never a
row, never a schema only `"*"` admits and never a Marten-owned object outside the store's schemas; `BrowseDatabase` implies
it, and only `DatabaseAccess.ObjectGateAsync`, the object browser's gate, asks about it. Marten's document and event
tables and `mt_` objects are never read raw (the browser links to Documents and
Events, where tenancy, soft delete and the serializer apply), but the rows of flat-table projections and
`ExtendedSchemaObjects`, Marten-managed *relational* data, are browsable. A hidden document type
(`IsDocumentTypeVisible`) is absent from every list with its indexes, keys, triggers and partitions, and a view over its
table is refused; per-tenant partitions and sequences roll up, their names being the tenant list. Classification fails
closed: if a registered store's declarations cannot be read, nothing is "Other"; a `Storage.Add(IFeatureSchema)`
feature, which only Marten's internal `AllActiveFeatures` reaches, is classified by its `mt_` prefix. `BrowsableSchemas`
limits the screens, not `RunSql`: `SqlConsoleRole` is the real boundary, and `"*"` without it is event 9230 at startup.
Free text is masked and structure blanked: every catalog read pins `search_path` to `pg_catalog`, so deparsed names
carry their schema, and `WithheldNames` masks a withheld schema's qualifier as `‹withheld›` in types, defaults, CHECK,
index, view, routine and trigger definitions, `SET search_path` and comments. A view reading from or referring to a
withheld schema is refused its rows and query, as is a trigger with a withheld function and, while any document type may
be hidden, a view calling user code or reading a document table no store declares.
**A list locks nothing it lists** (no `pg_partition_*`, `pg_sequence_last_value` or size function): a tenant-partitioned
store has thousands of partitions, and the lock table is shared. Lists read `pg_catalog.pg_sequence`, never the
`pg_sequences` view, which on PostgreSQL 18 locks every sequence for every reader; a sequence's value is read on demand,
one at a time, behind `has_sequence_privilege`, which on 18 keeps the `RowExclusiveLock` away. Nor does a list call a
deparse function that opens what it describes, as its `AccessShareLock` queues behind a migration's `ACCESS EXCLUSIVE`
on 14 to 18: `pg_get_indexdef` in any form, `pg_get_expr` with a relation (a default's `adbin`, a partition's
`relpartbound`: `pg_get_expr(relpartbound, <relid>)`), `pg_get_viewdef`, a table CHECK's `pg_get_constraintdef`,
`pg_get_partkeydef`. Lists read partition bounds with `pg_get_expr(relpartbound, 0)`, which opens nothing
(`LintMatrixLiveTests`); T20 parses that text in C# (`PartitionBoundText`), in ISO `DateStyle`, which the health
runner pins beside `search_path` (`HealthSessionQueries`). A list that must deparse takes the sweep's one `pg_locks`
read (D29) and leaves a locked table's text out (`HealthTableQueries.IndexesSql`); a by-oid detail read may wait for its
one relation, under `lock_timeout`.
Another store's key, alias and name in the "could not be built" notice show only to a visitor its store policy passes,
its Marten-managed rows need its store and write policies, and other stores' databases are enumerated only when a store
policy (or, while `BrowseDatabase` is on, a write policy) will be asked, never for a `DynamicMultiple` store. No rows
are read from a foreign table, a partitioned table with a foreign partition, a table with a foreign inheritance child or
a view reading one (a materialized view's rows are local, and read); from a view over Marten's tables or another store's
whose policies refuse the visitor; or from a table or view with partitions or children in a withheld schema. A view is
judged on what it reaches through views and SQL-standard-bodied functions (Postgres records their dependencies), the
schemas it names as values, its collations and its text search objects. A source-text function cannot be followed, so a
view calling one is refused its rows while any schema the reading role may read is withheld (for `SECURITY DEFINER`, any
schema): hence the sample's `legacy.format_employee_name` has a `RETURN` body. A schema an extension owns needs an exact
`BrowsableSchemas` entry, and one not admitted is masked like a withheld schema without counting as withheld for
dependencies. Known limits (in the notes): routine bodies' own name resolution, hidden types Marten has not learned
(`Schema.For`, `AllKnownDocumentTypes()`), domain CHECK functions, schemas newer than the 60-second cache, source-text
functions under `"*"` with a narrowed `SqlConsoleRole`, dynamic-SQL table names, foreign partitions under a minute old.
The first read of a relation's rows per signature (filter, sort, paging mode, never the cursor) is audited per store,
database and relation, never from a static render: browser pages read only once interactive and once their address's
scope applies, so a refused explicit request is one 9202/9203 per page view and a page whose gate is closed refuses
nothing. Activity and the Overview show an entry under `BrowseDatabase` or `RunSql` only while the capability is on and
the write policy passes the reader for its scope; a browser read the store policy refused is recorded under
`BrowseDatabase` too, each policy asked once per scope per sweep. Postgres' error text about a browser read is masked
like everything else except a value the read bound, echoed as typed; the Schema screen withholds a failure's text
outright when it cannot read the schema list to check it, and a failed apply's Activity entry carries its SQLSTATE, the
words staying in event 9206. Row reads do not pin `search_path`: a bare `=` must find `citext`'s own operator, and an
unqualified name names no schema.
**Reach, not scope.** An operation Marten runs over a whole database (a progression correction, a rebuild, a high-water
restart, a store-global agent, cancelling a rebuild, the dead-letter "Rewind subscription", a schema apply) is
authorized as `(store, database, null)` unless the database is exclusively the selected tenant's, never on a sharded
tenancy, which pools tenants; the SQL console, the Mode A subquery lift and `EXPLAIN` are always asked with
`TenantId = null`, as nothing narrows a typed statement to a tenant. The one exception is the dead-letter rewind under
`UseTenantPartitionedEvents` from Marten 9.51 with JasperFx 2.91 (`RuntimeFeature.TenantSubscriptionRewind`), whose
per-tenant overload touches only that tenant's agents and dead letters (#5700, jasperfx#1025): it is authorized as
`(store, database, tenant)` and made only for an agent this process's daemon runs, with that agent's own names, as the
overload starts the tenant's agents wherever it is asked. There the store-global rewind is never made, and Skip event is
`MarkEventsAsSkipped`'s tenant overload (#5698), asked about the tenant; below either version both stay refused
(SEC1-fix4). Skip this group (M3) is Skip event's path over one dead-letter group, at most 1,000 events an action: its
checks in order, one statement proving every sequence in scope (`EventQueryBuilder.BuildSequencesInScope`), its
commit (`CommitSkipAsync`, which `StorageEnsuringCallTests` holds to those two callers) and one audit entry naming the
group and the count; under `UseTenantPartitionedEvents` it runs in one tenant, never a loop over several. The studio
never removes an orphaned progression row (`ProgressionRowClassifier`, from `AllShards()`), and refuses Start and the
store-global Rewind on a HotCold node whose distributor holds no lock (`DaemonLeadership`, M47). A tenant's database is
found without asking a dynamic tenancy for a tenant it lacks (`DatabaseReach.FindTenantDatabaseAsync`). Enumerating
a dynamic tenancy's databases (`AllDatabases()`, `Tenancy.DescribeDatabasesAsync()`, 9.50.1's
`IEventStore.TryDescribeDatabasesAsync()`) is not read-only at any Marten through 9.56.0, and the studio, enumerating
on the first navigation, can be the first caller: a known limit, not a per-poll cost. Measurements, history and
details: decision-notes.md, D27.
**D27 in PostgresStudio.** The gate is reused with the source policy and `PostgresStudioResource` (D5),
`BrowsableSchemas` playing "the store's own schemas": the schemas it names exactly are the source's own, their structure
shown to anyone the source policy admits, their rows needing `BrowseDatabase` and their definitions `ViewDefinitions` or
`BrowseDatabase`, each with the write policy (asked with no tenant, as a Postgres Studio scope has none). A schema only
`"*"` admits needs `BrowseDatabase` for its structure and definitions too, `"*"` being a statement about the database, not
the configuration; a system schema is never browsable. Naming
nothing means `public`, applied after every configuration, so naming schemas replaces it. With no declarations only the
`mt_` prefix names an object Marten's, failing closed: its rows are never read, nor a view over it, its definition shows
only with `BrowseDatabase`, and the database page says once that it holds a Marten store and points to Marten Studio. A
Marten database whose host would hide types belongs in Marten Studio: PostgresStudio cannot honour another application's
`IsDocumentTypeVisible`.
