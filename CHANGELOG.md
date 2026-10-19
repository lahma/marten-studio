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
  three worst findings, a toast says when a Critical finding appears while somebody is watching, and a
  Studio cost line shows the database time the studio's own read-only sessions took in the last five
  minutes.
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
  `Capabilities.ViewDatabaseActivity`, and a document type `IsDocumentTypeVisible` hides is in no finding.
  The navigation badge and the inline banners show a sweep only to a scope the report would be shown to, its
  tenant included.
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
  its largest partition and worded "partition-wide"), T03 invalid index (a build still running is named
  with its phase instead, a Warning rather than a Critical while another role's builds are hidden, and a
  build of another index never hides an older failure), T04 redundant index (never an exclusion index, a
  `NULLS NOT DISTINCT` unique index or an immediate unique index made redundant by one that enforces less),
  T05 unused index on a write-heavy table (Info while its counters were never reset or were reset less than
  a day ago, until a day of the advisor's own samples has seen it unscanned, and Info always on a
  multi-host data source), T06 low HOT-update ratio (saying so when an index on a column every upsert
  changes means fillfactor cannot help, and counting BRIN indexes as blocking HOT before PostgreSQL 16),
  T07 vacuum falling behind, T08 never analysed or stale statistics, T09 large documents (behind Run deeper
  checks), T10 soft deletes accumulating, and T11 HiLo `int` id headroom. The regular ones read the catalog
  and the statistics views (T11 also `mt_hilo`'s one row, once the catalog says it exists), lock no
  partition and never create `mt_hilo`; a `FillFactor` or `StorageParameter` remedy needs Marten 9.47+. See
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
- Health, connections and sessions: C01 this process's pool, read in process from Npgsql's observable
  instruments only (and not judged when two pools share Marten's connection string and disagree), C02 and
  C03 connection budgets, C04 idle in transaction, C05 long statements, C06 lock waits labelled in Marten's
  terms (the apply-changes lock, a daemon lock, an `mt_streams` row, a document table), C07 orphaned
  prepared transactions, C08 deadlock, rollback and temp-file rates, C09 async shards against the pool, and
  C10 (below). A lock is labelled by the table it belongs to, and only this store's own declared, visible
  objects are named. Sessions the studio's role cannot see into are counted, and said to be unreadable.
  C04's remedy says that on Marten 9.39 and 9.40 a server-wide `idle_in_transaction_session_timeout` can
  also end the HotCold daemon's leadership session. C04 recognises Marten's own HotCold leader by its
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
