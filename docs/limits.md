# Security in short, and current limitations

The short form of [security.md](security.md), and what Data Studio does not do yet.

## Security model and limits

The long form is in [`security.md`](security.md). The short form:

- **Nothing mutating is on by default.** Every module's capability flags are `false`, and two `ReadOnly` switches turn
  them off again (the shell's for every module, a module's own for itself). The
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
- **`EXPLAIN ANALYZE` and What if? stay inside the console's transaction.** Explain analyze runs the statement, so it
  runs only in the console, under `RunSql`, for a statement the guard accepts, and a write the guard misses is refused
  by Postgres (`25006`). What if? needs hypopg, which the studio never installs; it imagines one index that a builder
  wrote, in the read-only transaction, and resets hypopg on the same connection before the connection goes back to
  the pool - a session whose reset fails ends itself, so no pooled connection keeps a hypothetical index.
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
- **The Kafka module's read never writes.** No client it builds creates a topic, commits or stores an offset, joins a
  consumer group or runs your builder delegates; every read assigns explicit offsets, and the module has no produce,
  commit, delete or alter call at all. Its capabilities (`SearchRecords`, `ReadPayloads`, `ExportRecords`, `LiveTail`)
  are off by default, and every cluster and topic it reads is asked of your own policies first
  ([kafka.md](kafka.md)).
- **Audit.** Every mutating operation and every refusal is written to a 500-entry in-memory ring (the
  Activity page) *and* to your `ILogger` under the reserved event ids **9200–9299**, which is the copy
  that survives a deployment. 9200 `ActionPerformed`, 9201 `ActionFailed`, 9202 `CapabilityDenied`, 9203
  `ScopeAuthorizationDenied`, 9204 `SqlExecuted`, 9205 `SqlRejected`, 9206 `SchemaChangeApplied`, 9207
  `ProjectionRebuildStarted`, 9208 `ProjectionRebuildFinished`, 9209 `DaemonControlRequested`, 9210
  `StoreUnavailable`, 9211 `DocumentWriteRoundTripDropped`. A database browser's first read of a
  relation's rows is 9235 and an opened row 9236, a read of other sessions' details is 9245, a LINQ
  mode run, refusal and Postgres failure are 9260, 9261 and 9262, a bloat measure is audited as "Measure bloat", a
  finding's action as its own name ("Find the longest streams", "What-if foreign key lookup", "Try indexes for a table"), an orphaned progression row's removal is 9225 and its refusal 9226, Tenants' first read of per-tenant estimates in a circuit is 9265, and the Statements page's first read in a circuit is 9452. The ranges 9200–9299, 9300–9329 and 9400–9519 (and 9600–9699, the Kafka module's) are
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
  | 9223, 9224 | `HealthActionFailed`, `HealthActionRefused` | A finding's action a visitor pressed failed for an unexpected reason (always Warning, one line per press); or was refused (Debug) |
  | 9246 | `SessionActivityReadFailed` | Database › Sessions could not read the sessions for a reason other than a privilege or a timeout |
  | 9251 | `DiagnosticsSectionFailed` | The diagnostics API could not read a section of a snapshot |
  | 9270 | `HealthExportFailed` | The opt-in health check itself failed, so it reports `Degraded` |
  | 9320 | `SubscriptionRewindRefused` | Always `Warning`: Marten refused a dead-letter rewind as an unknown subscription; its words are here and not on the page |
  | 9410 | `StorageReadFailed` | The Storage page could not read the database's sizes for a reason nobody chose |
  | 9420 | `BloatMeasureFailed` | Always `Warning`: a bloat measure a visitor pressed failed for an unexpected reason |
  | 9430, 9431 | `ReplicationReadFailed`, `TablespaceSizeFailed` | A Replication section could not be read; a tablespace size a visitor asked for failed (always `Warning`, one line per click) |
  | 9440 | `ServerFactsReadFailed` | A section of the Server page could not be read |
  | 9451 | `StatementsReadFailed` | The Statements page could not read `pg_stat_statements` for a reason nobody chose |
  | 9460, 9461 | `ConnectionHistoryRoundFailed`, `ConnectionHistorySampleFailed` | The opt-in connection history could not finish a round, or sample one server; it tries again at the next interval |
  | 9463, 9464 | `ConnectionHistoryBacklogStored`, `ConnectionHistoryBacklogDropped` | The connection history stored the samples its store had refused (Information, once); it dropped the oldest of them past an hour (throttled) |
  | 9490 | `AcceptedCheckRetired` | Always `Information`, once per code: `AcceptedChecks` names a retired check (K10); the entry is ignored and can be removed |
  | 9500, 9501 | `CapacitySourceFailed`, `AuroraReadFailed` | The host's `IPostgresCapacitySource` threw or timed out; one of Aurora's own reads failed |

- **What the studio never does:** start a second projection daemon, execute DDL on a read or navigation path
  of its own (the exception is Marten's: the first enumeration of a master-table, sharded or single-server
  tenancy, under *Current limitations*), write document DML of its own, replace your `StoreOptions.Logger`,
  or show a raw connection string or a credential anywhere: the Connection page lists settings one by one as Npgsql parsed
  them, with every password and every value the studio does not know withheld (D33).

## Current limitations

- **Mode A takes no parameters.** The clause is handed to Marten as SQL text; there is no `?`/`@p`
  binding, so a literal you paste in is SQL too. What keeps that from being an ungated console is the
  clause guard — no second statement, balanced brackets, the function denylist, and without `RunSql` no
  nested read — together with the read-only transaction it runs in; not a parameter you could have put
  the value in safely.
- **No push, no hub.** Live pages poll at `RefreshInterval` through a single-flight snapshot cache,
  paused on hidden tabs; only projection state gets sub-second updates, and only where the daemon is
  hosted in this process. There is no SignalR hub of the studio's own — adding one would add a
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
- **Keyboard shortcuts are listed, not configurable.** Press `?` outside a text field for the list; the
  keys are the ones each control handles when it has focus, and the only global ones are `?`, Find's `/` and Ctrl+K,
  and Esc.
  There are no user-defined shortcuts.
- **No live SQL tail.** It would require replacing your `StoreOptions.Logger`, which is exactly the
  "registration changes host behaviour" failure this project refuses. Every generated grid carries a
  "Show SQL" disclosure instead.
- **Read-only and the capabilities are process-wide switches**, not per store, per database or per tenant. What a
  visitor may do in a scope is expressible all the same: `MartenOptions.StoreAuthorizationPolicy` decides which scopes
  they see, and `MartenOptions.WriteAuthorizationPolicy` is asked on every mutating call with the capability and the store, database and tenant, so
  "this tenant may edit but not delete" is a write policy that allows `EditDocuments` and refuses `DeleteDocuments` for
  that tenant.
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
- **The studio needs Marten 9.57.0 or later, and uses what 9.57 has without asking.** Nothing here is conditional on the
  host's Marten: LINQ mode (JasperFx 2.84, Marten 9.48), the
  `FillFactor` and `StorageParameter` remedies and the Schema screen's storage-parameter drift (9.47), D03's reading of
  the high-water agent's own liveness stamp (9.43), the daemon's own word that a projection is being rebuilt by
  something other than Data Studio (9.50), and, on a store that numbers events per tenant
  (`UseTenantPartitionedEvents`), Skip event and Rewind subscription acting on one tenant (9.51; a rewind is offered
  only for a tenant whose agent the daemon in this process runs). A stream whose folded events carry DCB tags compacts,
  and its tag rows go with them (9.51). The Schema screen's Check, Preview and Generate script buttons create nothing
  (9.50.3). A rebuild replays from the projection's start floor rather than from the first event, so the rebuild dialog
  names the floor stored for it in `mt_event_projection_floors` (read with the studio's own SQL) and says that a floor
  set in code may apply; D05 does not offer `SkipApplyErrors` for a database error in projection code, which pauses the
  shard at any setting and is never dead-lettered (9.52); and the Advance high water mark dialog says that the advance
  first ensures the event store's storage - with no tenant selected in every database of the store, each under its own
  `AutoCreate` - and, on a store that numbers events per tenant, which tenants' marks it moves. The edit dialog shows
  Marten's own preview of a save (9.53). Compacting a stream that folds to nothing leaves it alone (9.57, JasperFx/marten#5797).
- **Enumerating a dynamic tenancy's databases is not read-only**, at any Marten through 9.57.0.
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
- **Data Studio is a store console, not an ETL tool.** Import, dumping a whole collection, copying
  between stores and scripted migration are outside it. The Query page exports the page you are looking
  at — the row cap has already stopped the reader by then — and never a table.
