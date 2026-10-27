# 0.6.0

0.6 makes the studio explain what it sees. A query plan is drawn as a tree, with insights that say what in it is
wrong, and What if? shows whether an index the studio suggests would help before anybody builds it. Database ›
Sessions draws who holds up whom, and the health advisor judges how much room a range-partitioned table has left. In
Marten Studio a stream can be stepped through its projection one event at a time, each shard keeps a lag history,
dead letters are grouped by cause and orphaned progression rows are listed; of what Marten 9.52 to 9.54 add, 9.53's own
preview of a document save lights up where the host's Marten has it.

- The SQL console has Explain and Explain analyze buttons, and draws a plan as a tree: each node's type and what it
  reads, estimated and actual rows and loops, its share of the plan's time or cost, buffers and I/O, and PostgreSQL
  18's Disabled, Index Searches, Storage and Maximum Storage where printed. An `EXPLAIN` typed with `FORMAT JSON` is
  drawn the same way. `EXPLAIN ANALYZE` runs only here, under `RunSql`, for a statement the console's guard accepts,
  inside its read-only transaction and statement timeout: a write the guard misses is refused by Postgres (`25006`)
  and changes nothing, and every run and refusal is audited as a `RunSql` run.
- Query's Marten where-clause plan is drawn as the same tree, and comes back whole: it used to be cut at the grid's
  4 KB, which broke the JSON of any real plan.
- Operations › Statements has a drill-in: a statement's Plan link (`?queryid=`, so the link reopens it) shows its
  text, its counters and its generic plan, planned as `SqlConsoleRole` behind the page's own gate and audited by
  `queryid`. A text withheld from you, one that names a
  schema you may not see (a qualified view or SQL function would be expanded into the plan), a write, a locking read or
  a cut text gets no plan and says why; a plan that
  reads a schema you may not see is not shown, and every other plan is masked as the text is.
- Plan insights under every plan, with stable codes P01-P08 in `docs/health/codes.md` (a new area, Plans) and their
  docs in `docs/health/plans.md`: a row estimate off tenfold or more, a sort or hash spilled to disk, a lossy bitmap
  heap scan, a sequential scan with a selective filter, a nested loop over a large outer side, a large `OFFSET`, a
  leading-wildcard `LIKE`, and parallel workers short of the plan. On a plain plan, the five that need
  `EXPLAIN ANALYZE` are listed as not judged.
- Where the studio suggests an index, What if? can show whether it would help: with hypopg installed, it plans the
  statement again with that one index imagined, and draws the plan now and the plan with the index side by side, with
  both estimated costs and whether Postgres used the index. It is offered under the SQL console's Explain plans and
  Query's Marten where-clause plan (`RunSql`), on the Statements drill-in (its own gate, as `SqlConsoleRole`, audited
  by `queryid`), and beside LINQ mode's and the Documents list's index advice (`RunLinqQueries`), and T02 links a
  statement to its drill-in. Under a plan it imagines an index on the columns a sequential scan's filter compares; for
  a Marten statement, exactly the index Marten would create for the advisor's `StoreOptions` line, as Marten writes
  it. Where hypopg is not installed, or the role the studio plans as may not use it, it says which, and that
  installing it is the host's decision.
- The what-if creates nothing: it runs in the read-only transaction under `statement_timeout` and `lock_timeout`,
  never runs `CREATE EXTENSION`, and resets hypopg on the same connection before it goes back to the pool - again
  after a failure or a cancellation - and a what-if whose reset fails ends its own session, so the backend never
  returns to the pool.
- Database › Sessions has a Blocking tab (`?view=blocking`): who holds up whom, drawn as trees - each chain's head,
  then every session waiting on it, nested - with each session's process id, role, application, state, wait event,
  transaction and wait ages, the lock mode it holds or wants, and the relation, masked as the database browser masks
  it (`‹withheld›`, with no name, for a schema the visitor may not see). The counts are shown with the summary; the
  trees are session details, behind `ViewDatabaseActivity` and the write policy, and audited as before.
- A blocking chain's head is shown with `select pg_terminate_backend(<pid>);` - and `pg_cancel_backend`, when it runs
  a statement - as text to copy for whoever may run it. The studio never runs either.
- On PostgreSQL 19 the Blocking tab shows `pg_stat_lock`: for the whole server, how many lock requests of each type
  waited, for how long, and how often fast-path slots were full. The section is absent below 19.
- A regular health sweep reads `pg_locks` once, in one statement every check that needs it shares, where 0.5 read it
  seven times in five statements. C06's Show SQL is now that statement.
- T20 "Range partitions running out" now judges a range on a `smallint`, `integer` or `bigint` key by where its rows
  stop: a Warning when they reach the newest partition or leave at most one empty partition above them, from each
  partition's estimated rows; never Critical, since an integer key has no clock. It was judged on its DEFAULT
  partition alone.
- T20 reads its bounds as the text `pg_get_expr(relpartbound, 0)` returns and parses them itself, still opening no
  partition.
- T20's remedy for a Marten table on a static `ByRange()` is the `.AddRange("p20261201", ...)` line its declaration
  takes; a next partition past what an integer key's type holds gets no statement.
- Events › a stream › Step through it event by event: pick a single-stream projection or aggregate and step its stream
  one event at a time. Each step shows the event (type, version, sequence, timestamp, body), the aggregate after it,
  and a JSON diff against the aggregate before it. First, previous, next, last, jump to a version, and Find next
  change of a JSON path (`total`, `items[0].sku`, `$.status`) within the next 5,000 events. The stream, aggregate
  type, step and path are in the query string, so a pasted link reopens the same step.
- The stepper folds with time travel's own fold, Marten's `AggregatorFor<T>().BuildAsync`, one event onto the state
  before it. Each step is the aggregate Marten's single fold of the stream up to it produces, for `Create`/`Apply`,
  `ShouldDelete`, `Evolve`, `EvolveAsync` and self-aggregating types at Marten 9.39.0 and 9.54.2. A projection that
  overrides `DetermineAction`/`DetermineActionAsync`, an event registered with `DeleteEvent<T>()`, or an aggregator
  the studio has not read is folded from the stream's first event for every step instead, for the first 2,000
  events, and the page says so. An `Apply` that reads the version member, or a member `ApplyMetadata` sets, sees at
  each step the value after the previous event: Marten's single fold sets those once, at the end.
- Where applying an event throws, the stepper stops on that event and shows what the projection threw (Marten's
  `ApplyEventException` wrapper taken off). No later step is folded.
- Dead letters and the stepper link both ways: a step lists the dead letters that name its event and links to them
  (Dead letters now filters by `?seq=`), and a dead letter's failing event opens in the stepper on its step, in the
  aggregate its projection folds.
- The stepper is bounded: 20 events per window, the events before it streamed past the fold and never held, every
  call under `QueryTimeout`, and a timeout is a value with Retry. Events are read with the studio's own SQL, never
  `FetchStreamAsync`/`AggregateStreamAsync`, so stepping creates and migrates nothing.
- The stepper is gated as time travel is: reading the stream (the store policy), no capability, not audited. It runs
  your projection's own `Create`/`Apply`/`ShouldDelete`/`Evolve` methods in process, as time travel always has
  (`docs/security.md`).
- Projections: each shard row draws a small hand-rolled SVG chart of its position against the high water, from
  samples the screen keeps itself. The samples come from the read the page and the Overview already share, never a
  read of their own: at most one every `RefreshInterval` (5 s minimum), 120 per shard, in memory. The chart has a
  stalled colouring, a trend arrow (falling behind, catching up, steady, caught up) and a text equivalent beside it.
- Projections: while a rebuild runs, its shard says when it should finish: the events left over the rate measured
  from the rebuild's own samples in the last five minutes, with that window. Until three samples show progress it
  says "not enough samples yet", and it gives no estimate once the position stops moving.
- Projections: thresholds a host writes into `mt_event_progression`'s `warning_behind_threshold` and
  `critical_behind_threshold` are drawn on the shard's chart and said beside it. A "Thresholds that apply" list names
  the studio's own lag colours and D01, D03, D04 and D05's numbers for the store.
- Health: D05 keeps a position series of its own, from the agents the daemon in this process runs, for each shard it
  remembers failing. On a store that partitions its events by tenant, a shard that recovered and was then restarted
  for an unrelated reason is no longer reported again.
- Events › Dead letters has a Grouped by cause view (`?view=grouped`): dead letters grouped by projection and shard,
  event type, exception type and message fingerprint - the message with its numbers, GUIDs, quoted strings,
  timestamps and hex runs set aside - each group with its letters and events, how many are skipped, first and last
  seen, its tenants (named only to a visitor whose store policy passes that tenant) and a sample message. Opening a
  group lists its letters among the newest 20,000 in scope (`?group=`), with every per-letter action.
- "Skip this group" marks a group's events skipped through Skip event's own path - `ManageDeadLetters`, the write
  policy, the tenant proof of every sequence and `MarkEventsAsSkipped`, under `UseTenantPartitionedEvents` its tenant
  overload from Marten 9.51 and refused below it as Skip event is - at most 1,000 events an action, lowest sequence
  first, after typing the count, as one audited operation that names the group and the count.
- The grouped view reads the columns of the newest 20,000 dead letters, never their bodies, once per refresh interval
  across circuits, and groups them in the web process; a database with more says its groups are the newest letters'.
- Skip event proves its sequence with a one-column read shared with the bulk skip; its refusals, audit entries and
  outcome are unchanged.
- Projections lists orphaned progression rows - rows of `mt_event_progression` that nothing the store registers
  advances - each with why. They are counted for every visitor, named only with the whole database allowed, and
  read-only: Marten's own `DeleteProjectionProgressByShardNameAsync` and the equivalent SQL are shown as text for the
  host.
- The daemon card says which agents this node runs, under Solo, HotCold, `MartenDaemonModeIsSolo()` or external
  management, and Start, Stop and Rewind say when they would do nothing here.
- Documents: an Order control puts a collection newest first by last modified, created or revision, where its table
  has the column, over the studio's own keyset-paged read.
- Documents: from Marten 9.53 the save dialog shows what Marten's own preview of the save (`PreviewDocumentJsonAsync`)
  would keep beside the round trip, and holds a save Marten keeps differently or refuses. It opens no session and
  creates nothing; below 9.53 the dialog names the Marten it needs.
- New health check C12, Head of a blocking chain (Connections, both studios, behind `ViewDatabaseActivity`):
  `idle-head` when the head of a chain sits idle in its transaction while a session behind it has waited 10 seconds,
  `long-chain` when a chain's oldest wait reaches 2 minutes whatever its head does; Critical at 5 minutes. Its
  findings name each head and hand over its statements; an `idle-head` finding's remedy is
  `idle_in_transaction_session_timeout`, a `long-chain` finding's the head's statements as a repair to run by hand.
- New health check T18, Tenant skew (Marten Studio, Info): one tenant holds more than half of a conjoined table of
  100,000 rows or more with 5 tenants or more, from `pg_stats` or, for a table Marten partitions by tenant, its
  partitions' estimated rows, never listing a partition. It names the tenant, so it is shown only past the activity
  gate and that tenant's store policy (the diagnostics API: `IncludeActivity`); a `SqlConsoleRole` that may not read
  `tenant_id` gets "not checked" naming the grant.
- New health check D10, Orphaned progression rows (Marten Studio, Info), with Marten's removal by name as its remedy.
- `docs/health/codes.md` gains C12, D10 and T18, and P01 to P08 in a new area, Plans. The plan insights are not health
  checks: they are never swept, muted or returned by the diagnostics API. The studio runs 76 health checks, 31 of
  them in Marten Studio only.
- A document save whose JSON carries another id than the document's own is refused, in the preview and in the save,
  with both ids named. Until now such a save passed the version check on the document that was open and wrote the
  document under the new id - overwriting the document with that id, or creating one - on a type without Marten's
  optimistic concurrency.
- Projections no longer has "Unregistered shards". It also listed subscriptions registered with `Events.Subscribe`
  and each tenant's row under `UseTenantPartitionedEvents`, which are now drawn under their own projection or
  subscription; the rows nothing advances are the orphaned progression rows, and they no longer count towards the
  worst lag.
- Under HotCold, Start and the store-global Rewind are refused on a node that does not lead the shard, because another
  node does; Start and Stop also refuse what would do nothing here (an agent already running, no agent here to stop).
  Under `MartenDaemonModeIsSolo()`, which has no leadership loop, Start and Stop no longer need a pause.
- The Overview's projection count includes the subscriptions registered with `Events.Subscribe`.
- The Statements drill-in plans a statement on every supported major by `prepare` and `force_generic_plan`, as T02's
  plans do, inside the read-only transaction and deallocating what it prepared. `EXPLAIN (GENERIC_PLAN)`, which
  PostgreSQL 16 added, is never used: it does not work through Npgsql.
- The health advisor pins `DateStyle` to ISO for its own read-only transactions, beside `search_path`, so a time
  range is judged whatever `DateStyle` the server, the database or the role sets. The pin changes only how dates are
  printed to the advisor.
- The latest-tested Marten is 9.54.2 (0.5.0 was tested at 9.54.1); the package still depends on Marten 9.39.0 or
  later, and PostgresStudio on Npgsql 8.0.3 or later.
- The test projects take bUnit 2.13.3; nothing that ships references it.
- No package is added, and neither package's public API changes.

# 0.5.0

0.5 makes the studio two packages, and adds the health and diagnostics work of issue #8. Postgres Studio
(`PostgresStudio`) is the studio for any application that registers an `NpgsqlDataSource`, Marten or not, and Marten
Studio is now built on it. Issue #8 brings five pages (Statements, Maintenance, Storage, Replication and Server), 24 new
health checks, Amazon Aurora awareness, PostgreSQL 14 to 18 as a tested range, and a public diagnostics API,
`IPostgresDiagnostics`, that gives the host's own code what the studio reads.

- Marten Studio is two packages. The screens that need no Marten - the shell, the database browser, Sessions, Health
  and the pages beside it, the SQL console, Relationships, Activity and the JSON viewer - moved into PostgresStudio, a
  Razor Class Library that references Npgsql and no Marten, and MartenStudio references it. A host that references
  MartenStudio gets PostgresStudio transitively and changes nothing in its code. Upgrade the two together: a central
  `PackageVersion` for PostgresStudio, if the host has one, must equal MartenStudio's, or be removed.
- MartenStudio depends on exactly its own PostgresStudio, `PostgresStudio [0.5.0]` in the nuspec rather than
  `>= 0.5.0`: the two assemblies are one program, and PostgresStudio grants MartenStudio its internals.
  `AddMartenStudio()` and `AddHealthChecks().AddMartenStudio()` throw an `InvalidOperationException` when the
  application loads no PostgresStudio or another build of it, and say which version to pin. The usual cause is a
  central `PackageVersion` for PostgresStudio at another version under `CentralPackageTransitivePinningEnabled`,
  which makes NuGet drop it from the graph without a warning.
- The studio's static assets moved from `{Path}/_content/MartenStudio/` to `{Path}/_content/PostgresStudio/`, and the
  two files were renamed: `css/marten-studio.css` is `css/studio.css` and `js/marten-studio.js` is `js/studio.js`. A
  Content-Security-Policy, WAF rule or proxy allow-list that names the old path must name the new one. Every page's
  URL under `{Path}` is unchanged, and `{Path}/_framework/blazor.web.js` still comes from the host's own static web
  assets.
- The shared services log under `PostgresStudio.*` categories instead of `MartenStudio.*`: the action log, the
  database browser and its row reads, Sessions, the health advisor and its export, the SQL console (9204 and 9205, for
  `RunSql` and for Mode A's `EXPLAIN`, under `PostgresStudio.Services.Query.SqlConsoleService`), Relationships, live
  updates and the scope state. Event ids, levels and message templates are unchanged, so a filter or alert on an event
  id keeps working; one on the category prefix `MartenStudio` should add `PostgresStudio`. Event 9213 from the health
  export now logs under `MartenStudio.Services.MartenStudioSourceProvider`.
- Marten 9.54.1 is the latest-tested Marten (from 9.50.2); the package still depends on Marten 9.39.0 or later.
- PostgresStudio depends on Npgsql 8.0.3 or later and is tested at 8.0.3 and at 10.0.3: 7.x reports no pool
  instruments, and 8.0.0 to 8.0.2 carry CVE-2024-32655. In a Marten Studio application Npgsql stays the one Marten
  brings, which is never lower than PostgresStudio's floor; Marten Studio on Npgsql 10 is not a tested combination.
- PostgreSQL 14 is the documented floor, and 14 to 18 are supported, each tested by a version matrix that also runs
  PostgreSQL 19's release candidate as a preview (`docs/postgres-versions.md`). A server below 14 is still read; S01's
  finding and the Overview's server line in both studios say it is below the supported floor (14). S01 knows
  PostgreSQL 19 and says its dates are planned until postgresql.org lists them.
- Postgres Studio, the second package: the studio for an application that uses plain Npgsql and no Marten.
  `services.AddPostgresStudio()` and `app.MapPostgresStudio()` serve it at `/postgres`, with the same authorization
  layers (`AuthorizationPolicy`, `SourceAuthorizationPolicy`, `WriteAuthorizationPolicy`, asked about a
  `PostgresStudioResource`) and startup guard as Marten Studio. It lists the application's unkeyed singleton
  `NpgsqlDataSource` by itself, as the source `default`; a keyed one only through `AddDataSource(name, resolve)`; and a
  host's own set of databases, one per tenant say, through `AddDatabaseSource(name, resolve)` and an
  `IPostgresDatabaseSource`. It never takes a connection string. It shows the Overview, the SQL console, the database
  browser (tables, views, functions, triggers, sequences and types, and their rows), Sessions, Relationships, Health,
  Statements, Maintenance, Storage, Replication, Server and Activity. `BrowseDatabase`, `ViewDatabaseActivity` and
  `RunSql` are off by default. `BrowsableSchemas` defaults to `public`, whose structure anyone the source policy admits
  may see; rows and definitions need `BrowseDatabase` and the write policy. It never reads a Marten table's rows
  (`mt_`) and references no Marten package. See `docs/postgresstudio.md`.
- One studio per application. `AddPostgresStudio()` throws in an application that called `AddMartenStudio()`, and the
  reverse. `MapMartenStudio()` throws in an application that registered Postgres Studio, and `MapPostgresStudio()` in
  one that registered Marten Studio, each naming the map call to make instead.
- Postgres Studio's health advisor judges every table of the schemas `BrowsableSchemas` admits (issue #8, G1). Each
  table is the application's own, a recognised library's (Quartz.NET, Wolverine, EF Core's and FluentMigrator's
  history; exempt from the index-design rules only), or somebody else's; a Marten store's `mt_` table is that store's,
  and the advisor says so. The set is capped at the 2,000 largest tables by estimated size, and every table check says
  what was left out.
- Postgres Studio's table remedies are migration SQL (`ALTER TABLE … SET (…)`, `CREATE`, `DROP` or
  `REINDEX INDEX CONCURRENTLY`), shown under "For your own migrations - the studio never runs this" (issue #8, G12).
  Every name is quoted. A name that cannot be quoted, or that carries a line break, a control or format character, or
  a Unicode line or paragraph separator, gets no statement, and nor does a Marten table. Marten Studio's
  configuration-line remedies apply the same rule to the index names they print.
- Postgres Studio never reads a Marten table's rows or a view over one, and shows a Marten object's definition only
  with `BrowseDatabase`. Its database page says once that the database holds a Marten store, and points to Marten
  Studio.
- Postgres Studio speaks of data sources, never of a store. Its health checks, their titles and status rows, the
  health export's description and the Health, Sessions and Server pages' empty states no longer say "store", name
  Marten's API or `StoreOptions`, or give Marten code as a remedy on a plain Postgres host. It logs Marten Studio's
  event ids, at the same levels and with the same properties, in its own words ("Postgres Studio … data source
  {StoreKey}"). Marten Studio's sentences, remedies, log templates and event ids are unchanged.
- The health advisor knows since when a database's counters have counted (the statistics window), and whether a crash,
  a restore, a failover, a new cluster or a statistics reset started them again. The window starts at the later of
  `pg_stat_database.stats_reset` and the earlier of the two shared views' `stats_reset`, so a
  `pg_stat_reset_shared('archiver')` or `('bgwriter')` never shortens it. A clean restart is not a reset, nor is the
  watched standby's promotion or a standby following one upstream; a failover starts the window at the promoted
  server's own reset times; a server seen for the first time reads "since the server started", and "a new cluster" is
  said only when the system identifier or the major version is seen to change. Every per-day rate waits for a day of
  counting, and nothing is judged per day in the sweep that first sees a reset. `pg_control_checkpoint()` and
  `pg_control_system()` are read apart and never called where refused, so a platform without them loses only the
  timeline and the system identifier.
- The Health page says once, in the sweep that first sees it, that the statistics look reset since the last sweep, for
  every reason the statistics window recognises and on every platform (another Aurora instance answering included),
  rather than showing a quiet database.
- A health check whose platform does what it judges is listed as not applicable, with the provider's reason, and never
  run (issue #8, A2). The managed-platform detector counts only markers a role short of a superuser cannot plant:
  Aurora's `aurora_version()` only in `pg_catalog`, a provider's setting only as `pg_settings` lists it and only when
  it comes from the server's own configuration, and `neon_superuser` only with the attributes Neon gives it. So a
  planted marker can no longer silence R02 to R04 or S02.
- "Not checked" names who was refused and who can grant it: a health check Postgres refuses a privilege (42501) names
  the role it ran as (`SqlConsoleRole`, or the store's own login for the statement checks), leaves what was refused to
  Postgres' own words, says a table, schema or sequence is its owner's to grant and a statistics view is covered by
  `pg_monitor`, and names who can grant that on the platform the server runs on. A role counts as holding
  `pg_monitor` or `pg_read_all_stats` only when it has their privileges, not as a `NOINHERIT` or `INHERIT FALSE`
  member. The studio never suggests a `GRANT` to paste.
- "Mute in this browser" keeps an HMAC-SHA256 of each finding's key, under a random key of the browser's own, which
  the browser holds only as the host's ASP.NET Core Data Protection protected it. Before, it kept a plain SHA-256,
  which anybody who could guess a whole key could confirm. Mutes survive a restart, on every instance that shares the
  key ring, for as long as the host's Data Protection keys do; an instance that cannot open the browser's key shows
  nothing muted, and leaves the list alone until you mute something there. The studio registers no Data Protection of
  its own (Blazor Server already does).
- Every mute set before 0.5 is reset once. The list a browser kept is dropped, not migrated, because the HMAC replaces
  the hash. Several keys change in the same release, so a visitor sees one reset: T07's findings gain the variants
  `dead-rows` and `inserts`, and T08's `stale`, `never-analysed` and `since-restart`; `S02|<setting>` becomes
  `S02|setting|<setting>`, `T03|<index>` becomes `T03|index|<index>`, `T03|<table>#partitions` becomes
  `T03|partitions|<table>`, and C03's server-wide finding `C03|server`. C08's, R01's and D02's keys, and S02's
  `pending-restart`, are unchanged. From now on, a T07 finding that began as `inserts` takes the `dead-rows` key, and
  needs muting again, once the table's dead rows are behind too.
- Two checks are raised higher than in 0.4. C08 raises a Warning for 1 GiB or more of temporary files a day over the
  statistics window, where it was Info; below that, the hour's Info is unchanged. T07 is Critical past ten times its
  trigger, or when the table's own `autovacuum_enabled = false`, where it was only ever a Warning; a server with
  `autovacuum = off` leaves each table at Warning, and S02 is the Critical. The opt-in health check reports `Degraded`
  for C08's per-day Warning and for C11, as for every Warning.
- The Studio cost line now counts every connection the studio opens to a database, from open to close: the documents,
  events, projections, Query and schema reads, which it did not count before, as well as the health checks, the SQL
  console and the database browser. So the number it shows grows. Marten's own sessions for writes and daemon control
  are still not counted, and C08's subtraction of the studio's rollbacks is unchanged.
- The opt-in health check rotates. Each run checks every single-database source first, then continues each
  multi-database source from where the last run stopped, 25 databases in all, and asks a database source only for the
  databases it checks. It reports each database's latest result for one full rotation and one run more, counted in runs
  rather than time, so findings do not flap however long a run takes; a database a page swept within the interval is
  not swept again. `databasesSkipped` changes meaning: it now counts only the databases never yet checked in this
  process, not every database past the first 25, and the description says they are "not yet reached by the rotation"
  where it said "past the most one run checks". A platform that lacks or refuses `pg_control_checkpoint()` or
  `pg_control_system()` does not make it `Degraded`.
- The health advisor keeps per-database state for at most 64 databases - history, T14's first-seen marks, cached sweeps
  and statement texts - and forgets the least recently used database's state all together. A database that is read
  again starts its "Watching for" window afresh.
- New check C11 Low cache hit ratio (Warning), in both studios: fewer than 95% of the blocks read found in shared
  buffers, with at least a million blocks read a day, judged once the statistics have counted for a day (issue #8,
  G5). C08's deadlock finding adds the deadlocks per day, and its status row says when the per-day rates were not
  checked. Muting C08's temporary-files Info also mutes its per-day Warning: both use the key `C08|temp-files`.
- C03 also judges the login role's and the database's `CONNECTION LIMIT` (variants `role` and `database`), and compares
  this process's pool ceiling with the role's limit. C02 compares the role's limit with the store's pools on the server
  that log in as that role, read from their connection strings.
- C06 names a VACUUM at the head of a lock chain, and says that an anti-wraparound autovacuum does not give way to a
  migration's lock.
- C01 (connection pool saturated) and Database › Sessions' "This process's pool" read Npgsql 10's pool instruments,
  which Npgsql 10 renamed (`db.client.connection.count` and `db.client.connection.max`, tagged
  `db.client.connection.pool.name` and `db.client.connection.state`), as well as Npgsql 8's and 9's. Before, on a host
  running Npgsql 10 both showed nothing, and said nothing about why. C01's caveat names the instruments it read.
- T07 and T08 judge each table against autovacuum's own triggers: the table's storage parameters over the server's
  settings, PostgreSQL 18's `autovacuum_vacuum_max_threshold` cap and unfrozen share. A table is behind when a counter
  is at least 10,000 past twice its trigger and its last vacuum (or analyze) is more than an hour old. The 0.4 rules of
  20% dead and one day are gone, so a table tuned to a low scale factor is caught at a few percent dead, and an
  append-only table is judged on its inserts. This is host-visible: findings appear and disappear differently. A table
  behind on both dead rows and inserts is one finding, the `dead-rows` one, with both counters.
- When a table is behind, T07's and T08's remedies say what keeps autovacuum away, as far as the sweep can see: a lock
  autovacuum gives way to, held or waited for, by a session or by an orphaned prepared transaction; every worker busy
  (T16), with `autovacuum_max_workers` offered only where the role can count autovacuum's workers; or cost throttling,
  set back to Postgres' default. A lower scale factor is offered only where it would at least halve the trigger.
- T08 `never-analysed` counts rows read only when sequential scans read 1,000 rows on average, judges each partition
  on its own and never sums them, so small tenants, Marten tables read by key and small tables after a counter reset
  stay quiet. It also catches the shape `pg_upgrade` leaves - a table with no statistics that is read but not written -
  and advises `vacuumdb --analyze-in-stages`; a partitioned parent never analysed is Info. T08 `since-restart` is said
  for the first day after the statistics window shows that autovacuum's counters began again (a crash, a restore, a
  failover, `pg_upgrade` or a statistics reset), while tables of at least 10,000 estimated rows that hold half the rows
  have not been analysed since.
- New checks T16 Autovacuum workers saturated (every worker busy for three samples running; with `pg_read_all_stats`
  it counts autovacuum's workers, without it every running VACUUM or ANALYZE, as "at least") and T24 Autovacuum
  switched off for a table (the table's or its TOAST table's `autovacuum_enabled = false`; Critical past half of
  `autovacuum_freeze_max_age`).
- T14 dates each holder from when it was first seen, so its "held for at least" grows past the history ring. It
  matches replication slots to holders by raw transaction id, and names a standby whose feedback goes through a slot
  as "a standby, through slot X".
- T01, T05 and T06 give no usage verdict over statistics that are under 7 days old or that look reset, and their status
  rows say "Withheld: …". After a reset, a restore or a failover they say nothing for 7 days. T05 reads `last_idx_scan`
  from PostgreSQL 16 and no longer keeps its own sample history.
- T05 is no longer Info for its first day. On PostgreSQL 15 or later, against a long-running database whose statistics
  were never reset, it can be a Warning from the first sweep, where 0.4 waited for a day of its own samples.
- New check T15 Foreign key without a covering index, on tables of 8 MiB or more: Warning for `uncovered`, Info for
  `partly-covered`, an index that leads with only some of the key's columns. Marten's default conjoined
  `ForeignKey<TRef>`, keyed on `(member, tenant_id)` and indexed on the member, counts as covered. The Marten remedy is
  `Duplicate(...)`, keeping `notNull: true`, or the `TenancyScope.PerTenant` index configuration; nothing is offered for
  a table Marten creates, and a key into a hidden type's table is never described.
- New check T17 Orphaned document tables, in Marten Studio: `mt_doc_` tables no registered store declares, counted and
  never named.
- New check T19 Partitions against the lock table. It counts PostgreSQL 18's fast-path slots as the power of two at or
  above `max_locks_per_transaction`.
- New check T20 Range partitions running out. It finds a `timestamptz` range's months in the session's time zone, and
  says to create each missed period. A range Marten keeps ahead (`ByRollingRange`) is warned of only within one
  partition width, so the recommended `periodsAhead: 1` is quiet. The remedy is `RollPartitionsForwardAsync` for a
  rolling range, the `CREATE TABLE … PARTITION OF` for `ByExternallyManagedRangePartitions()` or a host's own table, and
  a configuration comment for a static `ByRange`.
- New check T25 Bloated index, b-tree only, estimated from the catalog's entry counts: Warning on the issue's rule, Info
  on key width, counting INCLUDE columns in an entry. The remedy is `REINDEX CONCURRENTLY`.
- New check T26 Integer key running out: a key a sequence feeds that is past half its range - a `smallint` or `integer`
  column, or a `bigint` one on a narrower sequence or on one capped by its own `MAXVALUE` - Warning at half, Critical
  at four fifths. The remedy widens the column or the sequence to `bigint`, or lifts a sequence's own cap with
  `NO MAXVALUE`.
- MartenStudio's table checks (T01–T08, T10–T14, and T02/T22/T23 behind Run statement checks) now judge every ordinary
  and partitioned table in the store's own schemas and - while `Capabilities.BrowseDatabase` is on - in the schemas
  `BrowsableSchemas` admits, not only the tables the store declares (issue #8, G1). A table the store declares is
  judged exactly as before. The host's own relational table beside the store is judged by every rule, with remedies
  that are migration SQL "for your own migrations - the studio never runs this", never a `StoreOptions` line. A
  recognised library's table (`qrtz_*`, `wolverine_*`, `__EFMigrationsHistory`, `VersionInfo`, an `mt_` table no
  store declares) is exempt from the index-design rules (T01, T04, T05) only, and its vacuum, statistics and rebuild
  remedies say the library's design is left alone. A table in a schema only `BrowsableSchemas` admits is judged by
  every rule, gets migration SQL for its upkeep only, and is shown only past the database browser's gate for that
  schema. This is host-visible: a store sharing its database with a job store or a legacy schema may see new findings
  about those tables.
- What a sweep leaves out is said in every table finding's caveat and check row, as counts, never names: "Judged:
  another registered Marten store's tables are not judged here", and past 2,000 tables "Judged: the store's 40
  declared tables and the 1,960 largest of 3,412 other tables by estimated size (a sweep judges at most 2,000)". While
  any document type may be hidden, no table named like a document table is read at all. If another store's
  configuration cannot be read, the sweep judges the declared tables alone and says so.
- T12 and T13 call the judged tables "tables the studio judges" rather than "Marten tables", and T01's and T03's notes
  for an empty store say "No table the studio judges".
- The index checks (T03–T06) no longer wait behind a migration: while a table is locked for a schema change they judge
  every other table and say how many they left out, and T02's index advice skips that table; they used to wait out
  the lock timeout.
- The statement checks' one read of `pg_stat_statements` now also keeps the hundred statements with the most execution
  time (and on Aurora the most storage reads) for the Statements page, still fenced to the store's own role and this
  database. On pg_stat_statements 1.8, a statement's top-level and nested runs are summed into one row.
- New check R02 WAL archiving failing: `failing` (Critical) when the archive command fails, and `backlog` when a hung,
  slow or empty command lets the archiver fall 64 segments and 1 GiB behind and keep falling, Critical at 10 GiB or
  after an hour without a success. The distance is measured from the last archived segment, or from a `.partial`
  file's segment; a `.history` file or a base backup's `.backup` label names no position, and with nothing to measure
  from, R02 counts the WAL written while the archiver archived nothing. A `pg_wal` that cannot be read is said in the
  evidence, and R02 still judges. Not applicable on Aurora, Amazon RDS, Azure and Cloud SQL, which archive WAL
  themselves.
- New check R03 Standby lag: Warning at 5 minutes or 1 GiB behind, Critical at an hour. Only physical senders are
  judged; a subscription or a change data capture connector is not a standby. From the primary it is measured with
  `pg_read_all_stats`, and without it the check says "not checked" and names the grant; a standby that delays replay
  on purpose shows its delay as lag there. From a standby the data source reaches no grant is needed, and the time is
  how long WAL the standby received has gone unreplayed, read from the advisor's samples, never the age of its last
  commit; on a delayed standby only the time beyond `recovery_min_apply_delay` counts. The start of a stall is carried
  from sweep to sweep, so a delay longer than the hour of samples the advisor keeps is judged too; until replay is
  first seen to pass a position the advisor sampled, the status row says "not judged yet" and why. A paused replay is
  its own Warning.
- New check R04 Requested checkpoints: requested checkpoints are 20% or more of at least 10 checkpoints in the
  advisor's window, counted each release's own way (up to 17 the timed count includes checkpoints an idle server
  skipped; from 18 it is `num_done`), or there are more requested checkpoints than `checkpoint_timeout` allows. The
  remedy doubles `max_wal_size`, worded for the platform. Not applicable on Aurora.
- New checks in both studios S04 Connecting as a superuser, S05 Connection without TLS, S06 Anyone may create in an
  application schema, S07 Outdated extension, S08 Connecting role's password expires, S09 Planner method switched off
  and S10 Store connections cannot write (issue #8, G7 and A7). S07 is raised only for an extension the server can
  update, meaning there is an update path from its version to the default; an extension at another version with no
  such path gets a note on its status line.
- S02 also judges `autovacuum = off` (Critical) and `track_activities = off` (Warning).
- New checks in both studios K05 SQL console without a narrowed role, K06 Database browser open to every schema without
  a narrowed role, and K07 Studio open to anonymous visitors with a capability on.
- New check K08 Server timeouts end the daemon's leadership, in Marten Studio only. It judges
  `idle_in_transaction_session_timeout`, `transaction_timeout` and `idle_session_timeout` against the HotCold leader's
  idle stretch, which depends on the lock monitor the process loads: DistributedLock's keepalive from Weasel 9.34
  (Marten 9.39.1), read off the DistributedLock assembly itself, or the `pg_sleep` monitor before it. Measured at Marten
  9.39.0, 9.45.0 and 9.54.1.
- New check D09 Event sequence and daemon progression disagree (Critical), in three variants: `mark-ahead` and
  `shard-ahead`, where the high-water mark or a registered shard is ahead of what the event sequence has handed out,
  so new events take numbers the daemon has already passed (judged only for a store with an async projection or
  subscription); and `sequence-behind`, where the sequence is behind the highest committed event, so the next append
  fails on `mt_events`' primary key (judged for any store with events). These are what a sequence reset, a restore
  from mixed points in time or a logical-replication cut-over leaves behind. A sequence behind the events gets a DBA's
  `setval` to the greater of `max(seq_id)` and the mark; where the mark is ahead as well - the commonest state - that
  one repair is the remedy for both findings, and D09 says not to run Correct progression, which would move
  progression back below events the daemon has already processed. Otherwise a mark ahead gets the projections screen's
  Correct progression (Marten's `TryCorrectProgressInDatabaseAsync`), and once that has done all it can after a
  `RESTART`, the same `setval`, made only while the sequence still reads `is_called = false`, because the next event
  appended would otherwise be treated as processed. A lone shard ahead gets a rebuild (which empties the projection's
  tables first) or a rewind to the lower of the mark and the highest number handed out, and a shard past the mark is
  rewound to the mark once the repair has run. Each `setval` is a statement that reads its target when it runs and
  only moves the sequence forward, so pasting it later is as safe as running it at once; the Health page draws it as
  "A one-off repair, run by hand - the studio never runs this" and names the database to run it in. It is not checked
  under `UseTenantPartitionedEvents`. The high-water read adds one backward primary-key probe of `mt_events`, and
  reads the sequence's `last_value` and `is_called` once.
- D05 no longer offers `SkipApplyErrors = true` for a database error in projection code (a missing column or
  table, a permission), which from Marten 9.52 pauses the shard at any setting and is never dead-lettered: where
  the daemon in this process says the failure is one, the finding says what to fix instead; where nothing can
  tell, the line names that exception.
- On Amazon Aurora, the Health page, both Overviews and the Server page say which instance answered the advisor's last
  sweep, and the Aurora version ("Answered by instance db-1-instance-2 (a reader), Aurora 16.4.1"). If another
  instance answers between sweeps, the Health page says so once, as a reset of the statistics window.
- On Aurora, R03 Standby lag gains the `aurora-reader` variant: each Aurora reader's `replica_lag_in_msec` from
  `aurora_replica_status()`, Warning at 5 minutes and Critical at an hour; the Replication page shows "Aurora replicas"
  in place of the standbys. S03 Checksum failures is not applicable on Aurora, whose storage layer manages the pages;
  R02 and R04 are not applicable there either.
- On Aurora, server-setting remedies name the DB cluster parameter group, or an instance's DB parameter group as well,
  per parameter as AWS's parameter reference lists it, and say what Serverless v2 derives from its capacity. C08's
  temporary-file findings say the files use the instance's local storage, and C11 says whether a cache miss is billed
  as I/O, from `PostgresComputeCapacity.IoOptimized`.
- On Aurora, C03 names what raises `max_connections` there, and each remedy says that a `max_connections` set in a
  custom parameter group replaces Aurora's formula: "Raise the cluster's minimum capacity to 1 ACU or more" when a
  minimum of 0 or 0.5 ACU holds it at 2,000; "Lower the pool ceilings … or put RDS Proxy or PgBouncer in front of it"
  at Aurora's ceiling (a maximum of 32 ACU or more, or `max_connections` at 5,000); otherwise "Raise the cluster's
  maximum capacity (now N ACU)"; and, without a capacity reading, all three.
- On Aurora, R01, T14 and the Replication page read the slots without asking for a WAL position Aurora refuses
  (`pg_current_wal_lsn()` below `wal_level = logical`, or on a reader), and R01 then says that no slot's retention is
  measured.
- A host can report compute capacity that SQL cannot see, such as Aurora Serverless v2's ACU, by registering an
  `IPostgresCapacitySource` as a singleton. It is asked once per `HealthInterval` per database and instance and given
  five seconds; its answer is shown on the Overviews, the Health page and the Server page, and C03 and C11 read it. A
  throw, the source's own timeout included, is event 9500, and a source the container cannot build is treated as no
  source and logged once as 9500. `PostgresComputeCapacity.AuroraServerlessV2` accepts 0 to 384 ACU, platform version
  4's range. See `docs/aurora.md`.
- Operations › Statements, in both studios (issue #8, G6 and A5): the top 10 to 100 statements of the database by
  execution time, over the window the statement checks watched or, before that, the time since the counters began, and
  it says which. It shows share, calls, mean, rows, shared blocks hit and read, and temp written, and every column
  sorts. It sums by group: in Marten Studio by Marten operation and document type, in Postgres Studio by verb and
  table. It sits behind `ViewDatabaseActivity` and the write policy; texts follow T02's rules and each visitor's mask,
  and a hidden document type is never named. On Aurora it can rank by storage reads, from `aurora_stat_statements`. It
  runs the statement checks' read only when the page is opened or refreshed: an open tab's polls start no statement
  checks.
- Operations › Maintenance, in both studios (`/postgres/maintenance`, and `/marten/maintenance`): autovacuum's
  triggers per table, with bars and due, behind and off badges; maintenance running now (a command on a table the page
  does not name is only counted); the workers; the freeze forecast; and the vacuum horizon. It reads nothing of its
  own: its numbers are the health sweep's. An on-demand, audited bloat measure with `pgstattuple` reads the heap first,
  then up to the five largest indexes, smallest first, within `QueryTimeout`, and keeps what it has measured.
- Operations › Storage, in both studios (issue #8, G3): the database's size; its growth a day and a month, once the
  statistics have counted for a day; and the fifteen largest tables, split into heap, indexes and TOAST, with rows,
  share and HOT %. It points at Maintenance for autovacuum's view of the same tables. The figures come from the
  catalog's page counts, which lock nothing they list. A table in a schema the database browser does not show the
  visitor is listed without its name, and with its owner as `‹withheld›`. When the statistics window cannot be read
  the page still shows the size and the largest tables, with growth "not available" and the reason, and it stops
  saying the statistics were reset once they have counted for a day.
- Operations › Replication, in both studios (issue #8, G8): replication slots, with logical decoding statistics and,
  from PostgreSQL 17, failover and synced; the WAL senders streaming from the server, each marked physical or logical,
  with their lag, or on a standby its own replay and pause state and any downstream standbys, listed but not judged;
  the archiver; WAL volume and full-page-image share over the advisor's window; checkpoints; `pg_stat_io` by backend
  type and object (16+); and tablespaces, with a size measured only on demand, one at a time per database. Numbers need
  the store (or source) policy for the database as a whole. Standby names, client addresses, slot names and the
  archive command need `ViewDatabaseActivity`. Another database's logical senders and slots are counted, and never
  named, whatever the visitor may see.
- System › Server, in both studios (issue #8, G7 and A7): the server's version, platform, checksums and uptime; what
  the studio's reading role can see, grant by grant, and which role reads what; a curated `pg_settings` view in seven
  groups, with each setting's source, a pending-restart badge and where it changes on the platform; extension chips
  (pg_stat_statements, pgstattuple, pg_buffercache, pg_repack, pg_squeeze, timescaledb and `track_io_timing`) that
  say what stands in the way, naming the grant; and the security posture, as the Health page shows it. On Aurora it
  has a section of its own: the instance's ten longest waits over the advisor's window (`IO:XactSync` and the rest),
  commit latency and DML by kind, and, behind `ViewDatabaseActivity`, each session's waits as counts and shares.
- Database › Sessions has "By application": sessions per `application_name`, by state, with the server's connection
  budget. The totals show at the database-wide gate; application names need `ViewDatabaseActivity`.
- The Health page has a Snapshot menu: View, Copy and Download of what your pages show of this database, as the
  diagnostics API's JSON. It is built from each page as you see it, and a section you may not see is named under
  `withheld` with that page's sentence. It starts no deeper or statement sweep, and stores nothing.
- On a store with `Events.UseTenantPartitionedEvents`, the dead-letter page's Skip event works from Marten 9.51: it
  marks the dead letter's own tenant's event skipped and no other tenant's event of that number, and is asked as that
  tenant (with "All tenants" selected, the dead letter's). Below 9.51 it stays refused, because Marten skips by number
  alone.
- Rewind subscription on such a store works from Marten 9.51 with JasperFx 2.91: it stops, rewinds and restarts the
  dead letter's tenant's agents alone and deletes only that tenant's dead letters, so it is authorized for that tenant
  rather than for the database as a whole, and it is offered only where the async daemon in this process runs that
  tenant's agent of the projection (on HotCold's cold node or a distributed host it would otherwise start a second
  one). The database-wide rewind is never offered on such a store; below 9.51 the rewind stays refused. A tenant id
  with a `:` in it is not rewound, because its agents' name does not read back as that tenant's and Marten would
  refuse only after the daemon had stopped them; and neither rewind lets Marten's "Unknown subscription name ...
  Available options are ..." - every subscription the store has - reach the page or the audit log.
- Compacting a stream whose folded events carry DCB tags works from Marten 9.51, which deletes their tag rows with
  them; the snapshot keeps the last event's tags. Below 9.51 Postgres still refuses and nothing changes, and the
  dialog says which this host's Marten does.
- From Marten 9.52 a rebuild replays from the projection's start floor rather than from the first event, and the
  rebuild dialog says so: it names the floor stored for the projection in `mt_event_projection_floors` (read with
  the studio's own SQL, never through Marten's floor read, which can create the event store), takes it off the
  events to replay, says that a document only the events at or below it built is deleted and not rebuilt, and
  where none is stored, that a floor the projection's code sets (`SubscribeFromSequence`, `SubscribeFromTime`)
  may still apply. A store migrated below 9.52 shows the new table as one non-destructive `CREATE TABLE` on the
  Schema screen.
- The Advance high water mark dialog says what the advance skips: every sequence number below the new mark with
  no committed event, an append still in flight included - not the events a projection has still to process.
  From Marten 9.52 it also says that the advance first ensures the event store's storage - with no tenant
  selected in every database of the store, each under its own `AutoCreate` - and on a store that numbers events
  per tenant, which tenants' marks it moves; the button's hover text says what the first press may create.
- The Schema screen's Check schema, Preview migration and Generate script say what they may create on the host's
  Marten: Marten's HiLo objects (`mt_hilo`, `mt_get_next_hi`) below 9.50.3, nothing from it.
- The apply section lists notes beside the destructive statements: an index built without `CONCURRENTLY` on an
  existing table, which makes writers wait while it builds (Marten 9.51's new `seq_id` index on each DCB tag table
  built before it is one, and every tagged append waits for it), and Weasel 9.46's partition-index rebuild, whose
  `DROP INDEX CONCURRENTLY IF EXISTS` is no longer listed as "drops an index". Any other `drop index` still is.
- The Configuration page shows `HighWaterProgressStallThreshold` beside "High water staleness" where the host's
  JasperFx has it (2.91, Marten 9.51). A requirement first met by a patch release names the patch: "Marten 9.50.3+",
  not "Marten 9.50+". The transient/permanent classification of a shard failure is known to arrive with Marten 9.50.3.
- LINQ mode: from JasperFx 2.91 (Marten 9.51) a date with no offset is read as UTC by JasperFx itself, and the
  studio's own correction stays idle, as measured.
- The SQL console's Run is no longer blocked by a Mode A run in flight: the two run independently, each gated. If the
  `RunSql` write policy throws, its message now appears in the SQL editor rather than as a page-level alert; the
  service still refuses.
- The diagnostics API (AGENTS.md D31): `IPostgresDiagnostics`, registered by `AddPostgresStudio()` and
  `AddMartenStudio()`, gives the host's own code what the studio reads, one database at a time. `ListDatabasesAsync`
  lists the databases the studio would list, building no data source and enumerating no tenancy. `GetSnapshotAsync`
  returns the findings, with stable codes and variants, and Storage, Statements, Settings and Connections sections; it
  never throws for an unknown, unreachable or failing database, and a database that cannot be reached is read no
  further, each section asked for coming back `Failed`. `IncludeActivity` is off by default, and gates statement texts,
  application names, other sessions' evidence and findings about one tenant. Calls share the pages' caches, and fresh
  reads that API calls start run at most two at a time; cancelling a call stops its wait at once, but a read it has
  already started keeps its place among the two until it finishes. A check's and a finding's `Title` is the studio's
  own wording; key on `Code`. `snapshot.ToJson()` writes `schemaVersion` 1. See `docs/diagnostics.md`.
- PostgresStudio's public surface is its six registration types (`PostgresStudioServiceCollectionExtensions`,
  `PostgresStudioEndpointRouteBuilderExtensions`, `PostgresStudioHealthChecksBuilderExtensions`,
  `PostgresStudioOptions`, `PostgresStudioCapabilities` and `PostgresStudioResource`), its six inbound types
  (`IPostgresDatabaseSource`, `IPostgresCapacitySource`, `PostgresComputeCapacity`, `PostgresDatabaseRef`,
  `PostgresServerInfo` and `PostgresPlatformKind`) and the nineteen types of the diagnostics read model, all in
  namespace `PostgresStudio`. MartenStudio's public surface is unchanged.
- Health check codes are a documented contract: `docs/health/codes.md` lists every code with its area, variants,
  subject kind, studios and the release that shipped it. A code is never reused, renamed or moved, and a retired code
  keeps its row. C08, R01, S02, D02 and T03 now report their fixed kinds of finding as a `Variant` rather than in
  `Subject`, and C03's server-wide finding gains the `server` variant.
- The database browser flags a foreign key with no covering index, and a `SECURITY DEFINER` function without a pinned
  `search_path`, and shows a sequence's headroom on demand, with a rate taken from two reads.
- The database browser's Constraints tab no longer lists a `…_not_null` constraint of kind "Other" for every NOT NULL
  column on PostgreSQL 18, which stores NOT NULL in `pg_constraint` (the Columns tab already says which columns are
  NOT NULL), and on 17 and later the Types list no longer shows a domain's NOT NULL as one of its constraints (the
  domain's NOT NULL flag already says it).
- `samples/PostgresStudio.Sample` (`dotnet run`, http://localhost:5211) is a Postgres Studio host with no Marten: an
  admin database as the application's own `NpgsqlDataSource` and three tenant databases listed through its own
  `IPostgresDatabaseSource` (`AddDatabaseSource("tenants", …)`), cookie sign-in as `admin`, `ops` or `viewer` with a
  write policy asked per capability, `public` named in `BrowsableSchemas` beside the admin database's schemas, and a
  fresh throwaway Postgres when nothing is configured (`MARTENSTUDIO_PG_REUSE=true` reuses one of its own).
- The demo data with no Marten in it - Quartz.NET's job store, the `legacy` schema and `studio_sample.app_settings`,
  and now the tenant databases with one deliberately unindexed foreign key - moved to
  `samples/PostgresStudio.SampleData`, which both samples share. The demo's `legacy.sensor_readings` gets its
  half-year partitions through next year, plus one beyond, every time it is applied, so it never ages into T20.
- The Postgres Studio sample writes its demo data - three tenant databases and the `quartz`, `legacy` and
  `studio_sample` schemas - only on the throwaway Postgres it starts itself, or when started with the new
  `--allow-data-generation` switch. On any other configured database it starts read-only and says so in one line.
- The Marten sample checks its database before Marten touches it. When another build of the sample seeded it, or its
  demo document tables hold rows with no seed marker, startup drops `studio_sample` and `studio_sample_events`
  (`drop schema … cascade`, which also drops whatever in other schemas depends on them) and seeds them again: on the
  throwaway container, and on any database when the host is started with `--allow-data-generation`. Any other
  database is left alone, and the host stops with one line that says what to do. Its throwaway Postgres has a
  container reuse identity of its own, so `dotnet run` no longer opens the integration suite's reused container.
- The Marten sample's SQL console, database browser and health checks read as its own narrowed role,
  `studio_sample_reader`, so K05 shows its remedy applied. The role is created only on the sample's throwaway
  database, or on one that `--allow-data-generation` names, and the sample creates no role and grants nothing anywhere
  else. The role is a member of `pg_monitor`, which reads every session's statement text server-wide. Its Health page
  shows S04 as Info: it logs in to its throwaway server as the superuser.
- A rebuild of the Marten sample's ShipmentTracker skips its poisoned event into a dead letter and finishes
  (`Projections.RebuildErrors.SkipApplyErrors`).
- Audit and refusal log lines escape and bound every value a visitor could shape, so a typed `?store=` or `?db=` cannot
  write a log line of any length, or forge a second one. This covers 9200 to 9207, 9209, 9235, 9236, 9245 and 9452,
  LINQ mode's 9260 to 9263, and every Warning that names a document id, a collection or a scope from the address.
  Control characters, format characters, line and paragraph separators and backslashes are escaped; a name is bounded
  at 256 characters, and a refused statement, a reason or an outcome at 4,000, saying how long a cut value was. 9204
  keeps the statement that ran whole and escaped up to 32,768 characters, the most one circuit message carries by
  default, and past that its first and last 16,384 characters around a count of what was cut, so no padding can keep
  the statement out of the record. The Activity page is unchanged.
- The projection summary's event 9216, the bloat measure's 9420 and the Configuration page's 9214 name the registered
  store key and the database identity, not the link's spelling. The resolver's "no such store", "no such database" and
  "not a tenant" refusals carry a visitor's value escaped and bounded.
- A link to another store's database opens that database: `?store=B&db=X` opens B's database `X` when B lists it,
  where it opened B's first database before, and in Postgres Studio `?store=tenants&db=tenant_b` opens that source's
  database. On Documents, a document, and the database browser's object list, object and row pages, `?store=B` with
  no `db=` now opens B's database of the same name as the one open, if B lists one, and B's first database otherwise.
- `SqlConsoleRole` with a trailing line break is refused at startup like any other value that is not a plain
  identifier (.NET's `$` matched before a final line feed). Relationships logs a scope that is refused or names
  nothing at Debug, not Warning. A scope change that names a tenant the new store or database does not list keeps
  the previous tenant only when the new store lists it too, and otherwise opens all tenants; before, it carried the
  previous store's tenant over unchecked.
- A composite projection's members are listed under their composite, and no longer as "Unregistered shards"
  that nothing advances; they offer no Start or Stop of their own.
- Security: the scope resolver asks the store and write policies again about the registered source key and the
  matched database identity whenever a link spells either another way - another case, or no `?db=` at all - before
  anything is opened or read. A handler that compares `MartenStoreResource` or `PostgresStudioResource` names
  ordinally, a deny-list included, is no longer answered with a store or database it refused by name. The pages
  already used those names; the resolver now does for every caller.
- Security: a Skip event or Rewind subscription whose page goes away after its checks have passed runs to its end on
  a token of its own and is audited. Before, closing the tab could cancel a rewind between stopping the subscription's
  agents and restarting them, which left them stopped with no audit entry.

# 0.4.0

- A health advisor. The new **Health** page (`/marten/health`) reads the store's configuration and its
  database and says what is wrong, how sure it is, and which `StoreOptions` line would change it: a remedy
  is never DDL, and one that needs a newer Marten says which ("Marten 9.47+") and what the host runs.
  Findings are grouped by area, each with its evidence and when it was measured, Show SQL, a caveat, a link
  to its section of `docs/health.md`, and "mute in this browser", which is kept in the browser as a SHA-256
  of the finding's key (never the key itself, which can name a tenant or a stream) and never in the
  database. The "What was checked" table never reads "nothing found" for findings it withholds: below a
  check's gate a row says "findings not shown to you" whether or not there are any, a row that withheld some
  says "N shown, others not shown to you", and the Overview's tile is never a green "nothing to report"
  while any are withheld. The navigation entry carries a severity badge, the Overview a Health tile and the
  three worst findings, a toast says when a Critical finding appears while somebody is watching, once per
  tab (one that clears and comes back toasts again only once a quarter of an hour, or three
  `HealthInterval`s when that is longer, has passed since a sweep last found it, so a check sitting at its
  threshold does not toast every other sweep), and a Studio cost line shows the database time the studio's
  own read-only sessions took in the last five minutes.
- The advisor does no background work. A sweep runs only when the Health page, the Overview or the opt-in
  health check (below) asks, and one sweep of a database serves every tab watching it for
  `MartenStudioOptions.HealthInterval`; the navigation badge never starts one. Every statement runs in the
  studio's read-only transaction under `statement_timeout` and `lock_timeout`, as `SqlConsoleRole` (the
  statement checks excepted, below), takes no lock beyond what a read takes and runs no DDL, and the checks
  that read no database run first, so a slow catalog read never uses up a sweep's budget before them. **Run
  deeper checks**, the bounded reads of the data, cached for five minutes, starts only for a visitor the
  store policy allows the database as a whole. A check that fails is a "could not check" row and event 9222,
  throttled; one the host's setup rules out (no extension, no privilege, an old Postgres) says so and logs
  at Debug. A database-wide number needs the store policy for the database with `TenantId = null`, asked
  about the store by its registered key and the database by the identity the scope resolved to (an empty
  `?db=` included). Anything that names a session, a role, an address, a statement or a tenant needs
  `Capabilities.ViewDatabaseActivity`, and a document type `IsDocumentTypeVisible` hides is in no finding. A
  finding that names a table in a schema outside the store's own also needs the database browser's gate for
  that schema (`Capabilities.BrowseDatabase`, the write policy and a `BrowsableSchemas` entry); the
  navigation badge, the banners and the Critical toast withhold it, and T12 and T13 name such a table
  generically. The navigation badge and the inline banners show a sweep only to a scope the report would be
  shown to, its tenant included.
- Health, daemon and high water: D01 committed events held behind a gap in the event sequence (with Explain
  on the Projections screen, which names the sessions that could be holding it only with
  `ViewDatabaseActivity` and asks the database at most once a minute), D02 async projections behind with
  nothing running the daemon, D03 a shard alive but not moving, D04 a projection falling behind with an ETA
  (only when its lag has grown at two consecutive samples, never on a single burst of appends), D05 a shard
  failed or paused - or one that keeps failing and being restarted, which reads Running between the
  coordinator's restart and its next failure (JasperFx keeps no failure across a restart, and Marten's start
  clears the row's), so the advisor remembers where it saw the shard fail and reports it while it is
  restarted there, until a check sees it past that point or none has seen it fail, or still standing there,
  for ten minutes or three `HealthInterval`s, D06 dead letters accumulating, counted in the table the store
  maps `DeadLetterEvent` to (an alias of the host's own included), D07 gaps the daemon skipped (from
  `mt_high_water_skips`), and D08 a shard rebuilt elsewhere or stopped half-way. No Marten writes
  `mt_event_progression.mode`, so D08 watches each shard's position, and the high-water mark's, and judges a
  replay finished only once the shard is back past where it began. See `docs/health/daemon.md`.
- Health, events: E02 tombstones, E03 hot stream (a Warning only for a stream written by at least ten
  transactions at one a second or more, counted by distinct transaction, so a bulk import in one save is an
  Info) and E04 large payloads, from one bounded read of the newest 100,000 events behind Run deeper
  checks; E05 unregistered event types (named only when the statistics show the alias's own stored type
  name and it does not load here, and a Warning only when a projection or subscription here reads every
  type and its error policy skips nothing), E06 long streams without a snapshot and E07 an archived share
  without partitioning, from PostgreSQL's statistics with no scan; and E08 DCB tag-version churn, whose
  remedy needs Marten 9.47+. See `docs/health/events.md`.
- Health, tables and indexes: T01 sequential scans of a large table (a tenant-partitioned table judged by
  its largest partition and worded "partition-wide"), T03 invalid index (a build still running is named with
  its phase instead, a Warning rather than a Critical while another role's builds are hidden, and a build of
  another index never hides an older failure), T04 redundant index (never an exclusion index, a `NULLS NOT
  DISTINCT` unique index or an immediate unique index made redundant by one that enforces less), T05 unused
  index on a write-heavy table (Info while its counters were never reset or were reset less than a day ago,
  until a day of the advisor's own samples has seen it unscanned, and Info always on a multi-host data
  source), T06 low HOT-update ratio (saying so when an index on a column every upsert changes means
  fillfactor cannot help, and counting BRIN indexes as blocking HOT before PostgreSQL 16), T07 vacuum
  falling behind, T08 never analysed or stale statistics (for a table that is not a document table, the way
  to switch autovacuum back on is said in words), T09 large documents (behind Run deeper checks), T10 soft
  deletes accumulating, and T11 HiLo `int` id headroom. The regular ones read the catalog and the statistics
  views (T11 also `mt_hilo`'s one row, once the catalog says it exists), lock no partition and never create
  `mt_hilo`; a Marten table is any the configuration declares, in whatever schema - an
  `ExtendedSchemaObjects` table in a schema of its own included - and a finding that names one outside the
  store's own schemas is shown only to a visitor the database browser's gate admits to that schema, counted
  as not shown to anybody else; T12 and T13, which are about the whole database, name such a table
  generically instead and stay visible; a `FillFactor` or `StorageParameter` remedy needs Marten 9.47+. See
  `docs/health/tables.md`.
- Health, wraparound and the vacuum horizon: T12 transaction-id age and T13 multixact age judge the oldest
  of the store's tables, this database and the server's oldest database (one `DROP DATABASE` left invalid
  is not counted), at lines measured from the server's own `autovacuum_freeze_max_age` and
  `autovacuum_multixact_freeze_max_age` (T13 also at `vacuum_multixact_failsafe_age` when that comes
  first). Other tables past the forced-vacuum line are counted but never named, and a temporary one is
  called out. Both forecast the time to the forced-vacuum and write-refusal lines from the advisor's own
  samples, worded "at the rate of the last N minutes" and never beyond 90 days; past the forced line, a
  refusal within 90 days is a Warning and within seven days Critical, once the age has stayed past the line
  for half of what the history holds (29 min 30 s at the default `HealthInterval`, never more than 30
  minutes). Marten's own event appends make no multixacts, measured at 9.39 and 9.50.2.
- Health, T14 vacuum held back: it reads this database's sessions and prepared transactions, every
  replication slot and standbys' `hot_standby_feedback`, and names the most severe holder, with the oldest
  of all beside it when that one is further back: a Warning after an hour, Critical after a day or after an
  hour near T12's line, and at least a Warning for any holder past `autovacuum_freeze_max_age`. Ages are
  lower bounds: a snapshot is dated by Postgres' own timestamps only, an id the holder owns (a session's
  transaction id, a prepared transaction's) also by the advisor's samples of the transaction-id counter,
  which reach back about an hour at the default `HealthInterval`, and a slot or a standby is judged by how
  far back it holds. A lazy `VACUUM` and an invalidated slot are never holders, and a lost slot that still
  holds is named as one to drop. A snapshot pinned at another holder's id is behind that holder, so Marten's
  HotCold leader is never named for another holder's transaction, and with Marten's defaults it is never old
  enough to raise on its own. Who the holder is sits behind `ViewDatabaseActivity`; another database's
  holders and logical slots are never named. With a transaction-scoped leadership lock and the lock monitor
  on, the HotCold leader holds its snapshot about a minute at read committed, measured at Marten 9.39 and
  9.50.2: at 9.50.2 (Weasel 9.45, Patched.DistributedLock.Postgres 1.3.3) there is no `pg_sleep(60)` - the
  leader sits idle in its transaction until DistributedLock's keepalive, about once a minute - except where
  the connection string sets Npgsql's `Keepalive` or `Multiplexing`, where it runs `pg_sleep(60)` as at
  9.39; the remedy text says so.
- Health, statement checks: T02 expensive statements, T22 optimistic-concurrency conflicts per document
  type and T23 JIT's share of execution (with the `ConfigureNpgsqlDataSourceBuilder` line that turns JIT
  off for the store's own connections), from `pg_stat_statements`, run only on **Run statement checks** and
  only with `ViewDatabaseActivity`. They read as the store's own role, and only that role's statements in
  its database. T02 flags a statement on the store's tables that took 2% of the window executing (a Warning
  at 10%, over at least 10 s of execution) or averaged 500 ms a call over 10 calls, and plans it
  generically - prepared as `marten_studio_plan`, never `ANALYZE`, a plain `select` only - for the index
  advisor's line. A statement's text is shown only through each visitor's own mask, every comment printed
  as `/* ‹withheld› */`, and is withheld, with the finding and its numbers kept and nothing planned, while
  it still carries a string or dollar-quoted constant as written or is not a plain query or write; a
  numeric constant in a text Postgres kept un-normalised is still shown. Counters are read without their
  text and at most 20 statements are kept, and because reading texts loads the extension's whole text file,
  the texts read are kept (200 per database, for the life of the process) and only new ones asked for. A
  plan's prepared statement is taken back on every way out, and if even that fails, that one session keeps
  only that name until Npgsql resets the session or the studio's next plan there takes it back (Debug event
  9281); the studio never clears the host's pool and never ends a session.
- Schema › Tables shows TOAST size, dead %, HOT %, last autovacuum and storage parameters, with drift
  against what the configuration declares on Marten 9.47+. A partitioned table's parameters are the ones
  its partitions agree on, so a declared `FillFactor` matches, and a disagreement reads "partitions
  differ". Schema › Indexes marks invalid, redundant and unused indexes from the advisor's last check.
- pgvector, read from the catalog and never from a value. Schema › Tables names each `vector`, `halfvec` and
  `sparsevec` column under its table with the dimensions it declares ("embedding vector(1536)"), and the
  database browser's Columns tab badges it; a type is pgvector's when the `vector` extension owns it,
  whatever it is called. An HNSW or IVFFlat index - Marten.PgVector's `VectorIndex<T>()` makes one - says on
  Schema › Indexes and on the browser's Keys tab what its definition only implies: each column's operator
  class, the distance it serves and the operator a query has to order by, and `m`, `ef_construction` or
  `lists` from `reloptions`, with pgvector's default (0.8.1: 16, 64, 100) where the index sets none, said to
  be the default. The browser's rows, row detail and in-place expansion show a vector collapsed to its type
  and first components (`vector(1536) [0.0123, -0.0441, 0.2, …]`) within the existing cell budgets, with
  Expand and Copy of the whole value; the SQL console does not, because it cannot read a vector unless the
  host called `UseVector()`. The JSON viewer collapses an embedding Marten.PgVector stores in a document: by
  default an array of at least 128 numbers, not all of them whole - the smallest common embedding size, so
  ids, counts and short lists of measurements open as they always did - and Expand all and search still
  reach every element.
- Health, connections and sessions: C01 this process's pool, read in process from Npgsql's observable
  instruments only (and not judged when two pools share Marten's connection string and disagree), C02 and
  C03 connection budgets, C04 idle in transaction, C05 long statements, C06 lock waits labelled in Marten's
  terms (the apply-changes lock, a daemon lock, an `mt_streams` row, a document table), C07 orphaned
  prepared transactions, C08 deadlock, rollback and temp-file rates, C09 async shards against the pool, and
  C10 (below). A lock is labelled by the table it belongs to, and only this store's own declared, visible
  objects are named. Sessions the studio's role cannot see into are counted, and said to be unreadable.
  C04's remedy says that a server-wide `idle_in_transaction_session_timeout` can also end the HotCold
  daemon's leadership session while its lock is transaction-scoped (`Events.UseAdvisoryLockTransaction`,
  the default at Marten 9.39 and 9.40). C04 recognises Marten's own HotCold leader by its
  leadership lock, never by its name: a session holding one of the store's daemon lock ids, with the
  leader's shape (a snapshot, no transaction id, no relation lock), idle in its leadership transaction for
  under three minutes, is not raised - at Marten 9.50.2 with `Events.UseAdvisoryLockTransaction = true` it
  waits there between DistributedLock's keepalives, about a minute apart, by design, and T14 judges that
  snapshot - and past three minutes it is raised as before. See `docs/health/connections.md`.
- Database › Sessions (`/marten/database/sessions`): this database's sessions, locks and prepared
  transactions. Counts, ages, states, wait classes and lock modes are shown to a visitor the store policy
  passes for the database as a whole (asked, like the details' write policy, about that database's own
  identity, which is what the audit records); process ids, roles, addresses, applications and statements
  need `ViewDatabaseActivity`. A statement is masked for the schemas the visitor may not see, and withheld
  when it names a hidden document type. The first read of session details per circuit is audited (event
  9245), and so is the first Health report per circuit that shows such details. There are no cancel or
  terminate buttons.
- Health, replication and server: R01 a replication slot holding WAL that nobody reads (a Warning at 1 GiB
  for 10 minutes, or by PostgreSQL 17's own `inactive_since`; Critical when it is unreserved, or at 80% of
  `max_slot_wal_keep_size` on two consecutive samples; Info once Postgres has invalidated it), where a slot
  is named only with `ViewDatabaseActivity` and another database's logical slot never; S01 the server's
  major version against the community's end of life, from a dated table shipped in the package (PostgreSQL
  14's is 2026-11-12); S02 settings that blind or endanger the store - `track_counts` off anywhere,
  `fsync`, `full_page_writes`, `zero_damaged_pages` and `ignore_checksum_failure` in Production,
  `synchronous_commit` off and pending restarts as Info, and never a sizing setting; and S03 page checksum
  failures, across the whole server. S02 fires on the sample's throwaway Postgres, which Testcontainers
  starts with durability off. See `docs/health/replication.md` and `docs/health/server.md`.
- Health recognises a managed platform - Amazon RDS, Aurora, Azure, Cloud SQL, Neon, Supabase - from
  markers each provider documents, and uses it only to word a remedy and to stand down for a provider's
  documented design: Neon's `fsync = off` is a note, not a finding, and an `off` where a platform's storage
  is its own and undocumented (Aurora's `fsync` and `full_page_writes`, Neon's `full_page_writes`) is Info,
  not Critical. Debug event 9290 says which platform was detected.
- Health, configuration: K01 `AutoCreate` left at `All` or `CreateOrUpdate` in Production, K02 extended
  progression tracking off, K03 advanced async tracking off, and K04 with C10 a HotCold daemon whose
  session-scoped leadership lock (Marten's default from 9.41) sits behind a transaction-mode pooler. K04
  judges by host and port (Neon's `-pooler`, 6543, 6432); C10 proves it with `pg_backend_pid()` in two
  consecutive read-only transactions. The remedy is `opts.Events.UseAdvisoryLockTransaction = true` and a
  direct connection for the daemon's node. See `docs/health/configuration.md`.
- An opt-in ASP.NET Core health check: `services.AddHealthChecks().AddMartenStudio(name, failureStatus,
  tags, timeout)` runs the advisor's configuration and catalog checks over every store and every database
  it holds (25 a run), and reports `Unhealthy` on a Critical (or the caller's `failureStatus`), `Degraded`
  on a Warning, a check that failed or ran out of time, or a store that would not build or list its
  databases, and `Healthy` otherwise. Its data is codes and counts only - never a tenant, session, table,
  statement or anything Postgres said. Probes share one run and are answered at once from the latest
  result; a result an interval old starts the next run, and one two intervals old is not served, so that
  probe waits, for at most `timeout`. Its own listing never makes a master-table, sharded or single-server
  tenancy apply, seed or create anything; a hosted projection coordinator is asked for its daemons once per
  sweep, which enumerates the store as the coordinator itself does every cycle. `AddMartenStudio()` does
  not register it, and a host that never calls it is unchanged. New public type:
  `MartenStudioHealthChecksBuilderExtensions`.
- Stream compaction from Stream detail, behind `Capabilities.CompactStreams`. It runs Marten's own
  `CompactStreamAsync<T>` on a session pinned to the tenant and database, stops at the version the dialog
  described, and reports the version Marten actually compacted through, read back from
  `mt_streams.compacted_version` on the primary. That version can be lower on a store that records skipped
  events, and the result says so. The confirmation is typed and checked by the service, the dialog says
  what is deleted (no archiver runs), and a result that cannot be read back is recorded as "saved, not
  confirmed". A stream with the empty GUID, or one whose aggregate `IsDocumentTypeVisible` hides, is never
  compacted. See `docs/compaction.md`.
- DCB tags on the Events screens. The feed takes a `tag:<type>=<value>` filter and shows its index verdict,
  and each event's tags are chips that link to the feed filtered by them. Event types lists the tag types
  with each table's key, and shows the advisor's E05 finding from the cached sweep (nothing when no sweep
  is cached). On a tag table above `ExactCountThreshold` a page's tags are read only when asked, and follow
  mode never reads them by itself.
- Streams finds a stream by its aggregate's natural key as well as by its id. The lookup always honours the
  stream's own tenant and never offers an aggregate `IsDocumentTypeVisible` hides.
- The dead-letter screen honours `MartenStudioOptions.IsDocumentTypeVisible` for Marten's own
  `DeadLetterEvent`, as the advisor's D06 does. A host that hides it gets no Dead letters entry in the
  navigation, no dead-letter tile on the Overview and no count, and Discard and Rewind subscription are
  refused and audited as refused; reached by its address, the page says "Dead letters are hidden by
  MartenStudioOptions.IsDocumentTypeVisible." and reads nothing. A delegate written as an allow-list of the
  application's own types therefore hides the dead letters too: answer `true` for `typeof(DeadLetterEvent)`
  to keep them. A delegate that throws for it is read as hiding it, and is logged (event 9217).
- LINQ mode on the Query page: type the predicate your code would write (`Status == "Open" && Total > 100`)
  and an ordering. It is parsed by JasperFx's Dynamic LINQ under its allow-list and the studio's own,
  applied to Marten's own `Query<T>()` on a session pinned to the tenant and database, so tenancy, soft
  deletes, hierarchies, enum storage and duplicated fields stay Marten's, and it shows the rows, Marten's
  SQL, the plan (`EXPLAIN`, never `ANALYZE`) and index advice with the `StoreOptions` line. It never creates
  a table: it builds Marten's statement without `ToCommand`, which runs `EnsureStorageExists`. A date
  literal is the UTC date it names whatever the server's time zone, and an `in (…)` list binds as one array
  parameter. Marten 9.48+ (on an older Marten the mode is absent and says which it needs), behind
  `Capabilities.RunLinqQueries` (off by default); every refusal, and the first run of each query in a
  circuit, is audited (events 9260-9262). The predicate and the ordering are recorded capped at 200
  characters each, and not at all for a refusal at the capability stage. See `docs/linq-mode.md`.
- Projections: a Last progress column, a trend arrow with an estimated catch-up (withheld while a pause, a
  rebuild or a stopped shard explains the lag), the advisor's D01 and D02 as banners, a rebuild shown as a
  state on its row (one Marten Studio runs at every version, any other on this daemon from JasperFx 2.86),
  a rebuild time estimate from the measured throughput, and the store's `StopAndDrainTimeout` quoted on
  Pause, Stop and Rebuild - or "no time limit" when it is not positive. A progression row's own
  `warning_behind_threshold` and `critical_behind_threshold` are honoured when a host writes them; Marten
  writes none, and the studio offers no way to set one.
- Below Marten 9.50 the Projections screen's Start warns that a rebuild started outside Marten Studio
  cannot be seen, because starting a shard during one runs a second agent beside the replay and can make
  that rebuild fail on a duplicate progression row (23505).
- Configuration has a Runtime card: the Marten, JasperFx, JasperFx.Events, Weasel and Npgsql versions the
  host runs, which optional features they provide, and the host's environment. The Event store card shows
  both tracking switches and, among the daemon's settings, whether its leadership lock is
  transaction-scoped.
- The capability chip counts 13, and its "Reads beyond the store" group is "Reads authorized like writes":
  `RunSql`, `BrowseDatabase`, `ViewDatabaseActivity` and `RunLinqQueries`.
- A master-table or sharded tenancy is enumerated less: the Overview enumerates each store once per load
  instead of once per database plus two, tenant discovery reads the tenant lists Marten has just reconciled
  instead of describing the tenancy again, and the database browser never enumerates another store with a
  dynamic tenancy just to spell this database's identity.
- A single database with `Events.UseTenantPartitionedEvents` lists the tenants Marten has registered in
  `mt_tenant_partitions`, including ones with no data yet, instead of scanning the tenanted tables. The
  selector shows the first 200, then becomes a free-text box. A tenant whose rows are only in a conjoined
  document type that opts out of partitioning is not listed, and a link naming it is refused until the
  tenant is registered.
- The sample's demo panel has switches that make the database unhealthy on purpose: a pinned gap (a
  transaction holding a drawn event sequence number with one event committed above it - D01, C04), a session
  idle in a transaction (C04), a bloated demo table with autovacuum off for it alone (T07) and a hogged
  connection pool (C01). Each turns itself off after 10 minutes, can be turned off by hand, is turned off
  when the host stops, and cleans up after itself - a clean-up that fails shows as Failed - and a host
  killed with the table bloated puts autovacuum back when it next starts. The pinned gap and the pool hog
  run only on the sample's throwaway database unless the host is started with `--allow-data-generation`. The
  sample now starts with no configuration finding, with `Events.EnableExtendedProgressionTracking` on (K02);
  S02's `synchronous_commit` Info, which is the throwaway server's own, still shows.
- The Marten floor is 9.39.0 (it was 9.31.0). Marten 9.39.0 fixes two critical SQL injections in Marten's
  LINQ provider, GHSA-q4xm-rhx9-xjm4 and GHSA-frqq-p5g3-8jq5. The studio reaches neither, but it should not
  pin a host to an injectable Marten, and once the advisories are published NuGet's audit flags every lower
  version. Hosts on 9.31-9.38 need to upgrade Marten.
- Tested at the floor and at Marten 9.50.2: both test suites run at each on every push and pull request (a
  new generated `marten-latest` workflow), and the package is still packed and published at the floor. What
  newer Marten versions add lights up at run time, and the Runtime card says which. `MartenStudio.dll` is
  compiled without the JasperFx source generator Marten bundles, so it carries no JasperFx generator
  attribute and has the same public API whichever Marten built it.
- Three capabilities, each off by default, included in `All()` and turned off by `ReadOnly`:
  `ViewDatabaseActivity` (other sessions' process ids, roles, addresses, applications and statements, and
  the statement checks; authorized like a write, with `TenantId = null`), `CompactStreams` (stream
  compaction, authorized for the scope as selected) and `RunLinqQueries` (LINQ mode, authorized like a
  write). See `docs/security.md`.
- `MartenStudioOptions.HealthInterval` (default 60 s, 15 s to 1 h) sets how often the advisor re-reads a
  database while a page has it open. It is never shorter than `RefreshInterval`: a longer `RefreshInterval`
  wins.
- Fix: from Marten 9.50 (JasperFx 2.86) the daemon refuses to start a shard whose projection is being
  rebuilt, and the studio audited and showed that refused start as a success. A refused start is shown with
  its reason, audited as a failure and kept as evidence for D05, and a start during a rebuild Marten Studio
  is running is refused at every Marten version.
- Fix: opening the dead-letter screen, stepping an aggregate through time, or opening a document whose id
  is a stream could create or migrate Marten storage. Each called a Marten read that ensures its type's
  storage first (a LINQ query, `AggregateStreamAsync`, `FetchStreamStateAsync`), which under the database's
  `AutoCreate` ran the type's migration and, on a store with a numeric id, created `mt_hilo`, the first
  time the process asked. All three read with the studio's own SQL. Time travel still folds the stream with
  Marten's own event storage and aggregator, and on an `mt_events` that lacks a column the configuration
  reads it names the column and points at the Schema screen instead of migrating the table. A dead-letter
  table that does not exist is an empty list, never created.
- Fix: on a store whose dead letters are soft-deleted (`Policies.AllDocumentsSoftDeleted()`, for example),
  the dead-letter badge kept counting letters a Discard had removed from the list. It counts exactly the
  rows the list shows.
- Fix: a document's "view stream" link follows the stream page's tenant rule. With no tenant selected, on a
  conjoined event store, it is offered for a stream in any tenant, not only `*DEFAULT*`'s; with a tenant
  selected, only for a stream whose `mt_streams.tenant_id` is that tenant.
- Fix: Stream detail's time travel offered and replayed aggregates `IsDocumentTypeVisible` hides, and
  replayed a stream whose id is the empty GUID, which folded every event in the log.
- Fix: a master-table tenancy with two tenant rows on one connection string drew two Overview cards under
  one key, which ended the circuit, and listed the database twice in the database picker. Each is shown
  once.
- Fix: a store whose databases cannot be listed breaks only its own Overview card, not the whole page.
- Fix: event 9214 was a Warning, once a window, for every store with no default database to read a Postgres
  version from (master-table, sharded, or a static multi-tenancy without `AsDefault()`), which is the
  host's configuration. Its version shows as unknown and the event is logged at Debug; a host's own tenancy
  whose `Default` throws anything else is still read, and still logged at Warning.
- Fix: on a store whose events are single-tenanted but whose sessions carry tenants (conjoined documents
  beside Marten's default event tenancy), Stream detail's time travel replayed, and Archive archived, any
  tenant's stream whose id was typed - Archive since 0.3. Marten records the appending session's tenant in
  `mt_streams` on every store, but narrows its own stream fetch and its archive by tenant only on a
  conjoined event store. Both now check the stream is the selected tenant's first, as the timeline and
  compaction already did, and another tenant's stream gets the answer a stream that does not exist gets. A
  stream that also holds an event another tenant's session appended is archived or compacted from a tenant's
  scope only by a visitor the write policy allows for the database as a whole, because Marten writes every
  event of the stream. Time travel from a tenant's scope folds only that tenant's events, as the timeline
  shows them, and the panel says so. On a conjoined event store Archive waits for a tenant to be selected,
  as Compact does. Stream detail now reloads when its `?id=` changes, and drops a replay or a page of events
  that answers after it has moved on. A scope change closes an open Archive or Compact dialog. Every refusal
  is audited.
- Fix: with `Events.UseTenantPartitionedEvents`, where each tenant numbers its own events, Marten Studio did
  several things that only make sense when one sequence numbers every event. With All tenants selected the
  Feed paged and followed a number the tenants share, two tenants' events with the same number on one page
  ended the circuit, the Overview listed them and Event types showed their range; the dead-letter screen
  looked the failed event up by that number alone; Skip event marked every tenant's event of that number
  skipped, because Marten skips by sequence alone; and Rewind subscription restarted every tenant's agents
  as one store-wide agent from a floor taken from one tenant's numbering. The Feed, and a stream whose id
  more than one tenant has, now ask for a tenant first, and the Overview says so in a note; Event types
  shows the sequence range as per tenant; the dead letter's event is read, and its stream linked, in its own
  tenant; and Skip event and Rewind subscription are refused on such a store, audited, with their buttons
  saying why.
- Fix: on a master-table tenancy with two tenant rows on one connection string, a scope naming the second
  row's tenant opened its sessions and connections on the first row's instance, with that row's role and
  connection options. It now opens them on the instance built from its own row.
- Fix: a refused query (a Mode A where clause, a SQL console statement, or LINQ mode's predicate or
  ordering) puts the line it points at under the message, at the box's full width, whenever the box is
  narrower than 40rem, and that line scrolls sideways within itself. At 390 px the two had shared one row:
  the message wrapped every two or three words and the line was cut off. A tab in that line, and in the JSON
  editor's, is now one column wide at every width, so the caret stays under the character it points at.

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
