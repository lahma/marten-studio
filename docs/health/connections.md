# Health: connections and sessions (C)

These checks look at who is connected to the store's database and what they hold: this process's connection
pool (C01, C09), the server's connection budget (C02, C03), sessions and statements that hold things back (C04,
C05, C06, C07), the database's own counters (C08), whether a transaction-mode pooler stands between the
HotCold daemon and its lock (C10), and how much of what the database reads its shared buffers hold (C11). Back to the
[index](../health.md).

## What these checks read, and when

`pg_stat_activity`, `pg_locks`, `pg_prepared_xacts` and `pg_stat_database` are read **only by a health sweep**
- once per `MartenStudioOptions.HealthInterval` (a minute by default) for everybody watching - and by the
**Database › Sessions** page, at the same interval or on Refresh, through a shared snapshot that is never read
twice within ten seconds. Nothing on the five-second `RefreshInterval` path reads them: they are scans of the
server's process array and lock table, cheap once a minute and not something to do on every poll of every open
tab.

Every read is bounded to **this database** (`datname = current_database()`), to client sessions, and away from
the studio's own session, and runs in the studio's read-only transaction under `statement_timeout` and
`lock_timeout`, as `SqlConsoleRole` when one is set, on the primary of a multi-host data source. One number is the whole server's: C03 counts every client
connection on the server, because `max_connections` is the server's and every database spends it - and it
counts them, nothing more.

No statement names a table. Statement text is never put into a finding (see below).

## Who sees what

A finding's count, ages, states, wait classes, lock modes and the Marten label of what is locked are shown to
anybody the store policy passes for the database as a whole (`TenantId = null`).

Who the sessions are - **process ids, roles, client addresses, application names, connection times** and, on
the Sessions page, **statements** - needs `MartenStudioOptions.Capabilities.ViewDatabaseActivity` (off by
default, in `All()`, off under `ReadOnly`) **and** the write policy, asked with
`Capability = "ViewDatabaseActivity"` and `TenantId = null`. Without it a visitor reads "withheld" in place of
each of those, with what it would take.

**Statement text is never part of a finding.** Another application's statement carries its literals and its
schemas, and what a visitor may read of it depends on which schemas they may see - which a finding, shared by
everybody watching the database, cannot know. It is on Database › Sessions, masked per visitor: every schema the
visitor may not see is replaced with `‹withheld›` wherever the statement names it - as a qualifier, and as any
whole word that spells it, which errs towards masking - by the same rule the database browser uses for
Postgres' own words (AGENTS.md D27). When the visitor's withheld schemas cannot be read, statements are withheld
outright. A statement Postgres cut at `track_activity_query_size` (or the studio at 4 096 characters) loses its
last word before it is masked, because a cut text can end in the first half of a schema's name (in a multibyte
encoding Postgres cuts on a character, up to three bytes short of the setting, and the studio allows for that).
A lock on a relation in a schema the visitor may not see names no relation, and a document type that
`IsDocumentTypeVisible` hides is never named to anybody.

**A statement about a hidden document type is withheld, not masked.** Marten's own SQL names a document's table
and spells out its members, and `IsDocumentTypeVisible` hides a type whatever schema it lives in - so a statement
that names a hidden type's table is replaced with a note saying so. While `IsDocumentTypeVisible` is set at all, so
is a statement that names an `mt_doc_` table no registered store declares: a type nothing in the process has
touched yet may be a hidden one, and a tenant partition's name carries a tenant id. When the declarations of every
store cannot be read and types may be hidden, every statement is withheld. A statement that names a document
table of another registered store whose store policy refuses the visitor is withheld too, with a note of its
own: that store's alias is in the table's name. A statement that names a hidden table only through dynamic SQL is
not detected - D27's limit for source text. Literals are shown as written - a tenant id in a `where` clause, a
document's JSON - which is why statements need `ViewDatabaseActivity` at all. D01's Explain on the Projections
screen shows statements by this same rule.

The first read of session details in a circuit, per store and database, is one Activity entry and one event
9245; the page's interval reads and Refresh record nothing more. Every refusal is audited. The Health page's own
session evidence (who is idle in a transaction, who waits, who holds a prepared transaction) is audited the same
way: the first time it is shown in a circuit for a store and database, and again only when the set of checks
showing it changes.

**How sure.** A Postgres role that is not a member of `pg_read_all_stats` reads other roles' sessions with
their backend type, state, timings, client and statement blank. They are still counted - a row whose backend type
is withheld and that has a role behind it is a client session - and never judged, and every finding then says
"cannot see into N sessions of other roles" rather than "none". That is the host's choice of role, an expected
state: it is a sentence on the page and never a warning in the log.

## C01 Connection pool saturated

**Warning** when more than 80% of this process's pool to the database is lent out; **Critical** when it is fully
lent out on two consecutive checks.

Every Marten session, query and daemon batch borrows its connection from the database's Npgsql pool. When all of
them are out, the next request waits inside Npgsql for up to its `Timeout` and then fails with "The connection
pool has been exhausted": the application stalls while Postgres looks idle. A pool that fills up is more often
connections held too long (C04, C05, sessions never disposed) than a ceiling too low.

### How it reads the pool without changing the host

C01 is read **in process**, from Npgsql's own counters, by a `MeterListener` the studio starts when a sweep (or
an open Sessions page) takes a lease and **disposes when the last lease is given back**. It enables exactly two
of Npgsql's instruments, both **observable**, under whichever of their two names the application's Npgsql
publishes, and reads them with `RecordObservableInstruments` when the check runs:

| Npgsql | Connections per state | Ceiling | Pool tag | State tag |
|---|---|---|---|---|
| 8.x, 9.x | `db.client.connections.usage` | `db.client.connections.max` | `pool.name` | `state` |
| 10.x | `db.client.connection.count` | `db.client.connection.max` | `db.client.connection.pool.name` | `db.client.connection.state` |

Npgsql 10 renamed them to OpenTelemetry's current database client names; the kinds and values (`idle`, `used`) are
the same (measured at 8.0.3, 9.0.4 and 10.0.0 to 10.0.3). The studio never asks which Npgsql it runs beside: it
listens for both and recognises a pool by either tag, so a host on Npgsql 10 - Postgres Studio's own, or a Marten
Studio host that brings it - is read as one on 9 is. The finding's caveat names the two it read.

- **No synchronous instrument is ever enabled**, so nothing is recorded per command: Npgsql records
  `db.client.commands.*` (from 10, `db.client.operation.*`) on every command the host runs, and a listener on any
  of them would add a callback to each one. That includes `db.client.connections.pending_requests`, which is a
  synchronous counter in Npgsql 9.0.4 - and, as `db.client.connection.npgsql.pending_requests`, in 10.0.3 - so C01
  judges "saturated" by the pool reaching its ceiling on consecutive checks instead.
- **Not `db.client.commands.prepared_ratio` either** (from 10, `db.client.operation.npgsql.prepared_ratio`), though
  it is observable: while it is enabled Npgsql counts every command it starts, and reading it resets the counters -
  which would take the studio's reads out of the host's own exporter's numbers.
- The host's own `MeterProvider`, if it has one, is unaffected: a listener is a passive subscriber, Npgsql
  changes nothing it does for the two instruments being enabled, and a reading runs Npgsql's two-integer
  callback on the studio's thread and delivers only to the studio's listener.

`SessionPoolObserverTests` (fast suite) prove those statements against a meter of the test's own - and pin
the shapes of Npgsql's own instruments, so a later Npgsql that changes one fails there - and
`ConnectionPoolLiveTests` that a burst of commands through a watched pool delivers nothing to the listener.
`NpgsqlPoolInstrumentsTests` and `NpgsqlPoolLiveTests` in `PostgresStudio.Tests` do the same for both sets of names,
and read a real pool - C01's report and the Sessions panel - at the Npgsql floor and at the latest-tested Npgsql 10.

The pool is matched to the database by its pool name tag, which is the data source's connection string unless the
host named the data source (`NpgsqlDataSourceBuilder.Name`); a named pool cannot be matched, and the check says
so instead of guessing. Two pools built from one connection string - the host's own `AddNpgsqlDataSource(cs)`
beside Marten's, or `new NpgsqlConnection(cs)` with Marten's connection string, which Npgsql pools on its own -
report under the same name: the observer counts the pools behind each
name, and when they report different numbers the check says it cannot tell which is Marten's and judges nothing
(and Database › Sessions shows no pool). When every pool under the name reports the same numbers, whichever is
Marten's reports them too, so the reading stands. Other processes have pools of their own, which this does not see.

What to change:

```csharp
// The ceiling is the connection string's "Maximum Pool Size" (Npgsql's default is 100):
opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 200);
```

Only once the server has the headroom (C03, C02), and after ruling out sessions that hold connections too long.

## C02 Connection budget across databases

**Warning** when a store with several databases on one server can, between their pools, open more connections
than the server accepts: `max_connections − superuser_reserved_connections − reserved_connections` (the last on
PostgreSQL 16+).

Each database has a pool of its own, and every connection Marten makes to it - the async daemon's leadership
lock included, which Marten builds on the same data source - comes out of it, so the sum of the ceilings is
what this process alone may open. Databases are grouped by host and port as their connection strings spell them.
A store with one database on the server is C03's to watch.

The store's databases are listed without making its tenancy do anything, which decides how exact C02 can be:

- **A single database, or a static list** (`MultiTenantedDatabases`): the databases are configuration, and every
  one is weighed with its own `Max Pool Size`.
- **A single-server tenancy** (`MultiTenantedWithSingleServer`): the databases it has already built, from
  memory. Listing them through the store would run `CREATE DATABASE` for every configured tenant that does not
  exist yet, so a database not built in this process yet is not counted.
- **A master-table or sharded tenancy**: listing its databases reads its master table on every call, and applies
  the table's schema on the first, so C02 never does. It counts the databases this process holds
  (`ITenantedSource.AllActive()`, from memory), takes each to have this database's `Max Pool Size` and to share its
  server, and reports what that adds up to as **Info**, with the estimate said in the caveat.
- **Any other tenancy** cannot be listed without building, and C02 says so instead of checking.

```csharp
opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 20);
```

## C03 Server connection headroom

**Warning** above 80% of the connections an ordinary role may open; **Critical** at 95%.

Counts every client backend on the server, every database included - only the count - against
`max_connections` less what is reserved. Background workers, autovacuum and replication have slots of their own.
The remedy is fewer connections per process, or a pooler in front of the server; `max_connections` is the
server's own setting and the studio never changes it.

The count is whole without `pg_read_all_stats` too, but not because Postgres lists everything plainly: a role
that is not a member reads another role's row with its `backend_type` null, and a filter on `client backend`
alone would drop every such row. So a row whose backend type is withheld and that has a role behind it and a
database is counted as a client session; the superuser's logical replication launcher (a role, no database) and
Postgres' own processes are not. A background worker started for a role in a database (a parallel worker, a
logical replication apply worker) that the role cannot see into is counted with them - over by that much, never
under.

## C04 Idle in transaction

**Warning** when a session has been idle inside an open transaction for a minute; **Critical** at ten.

An open transaction doing nothing keeps every lock it took, holds back vacuum while it has a snapshot, and once it
has written holds a transaction id that Marten's high-water detection can wait behind (D01). Only a session that
holds something back is raised: one with no transaction id, no snapshot and no relation lock pins nothing -
Wolverine's lock listeners are such sessions - and is counted in the status row, not raised.

Marten's own HotCold leader, as Marten configures it by default, is not raised either, for a different reason. At
Marten 9.39 and 9.40 (`Events.UseAdvisoryLockTransaction` on by default) it holds its leadership with
`pg_try_advisory_xact_lock` in a transaction it never ends, with a snapshot - measured live. With
`Events.UseMonitoredAdvisoryLock` on, also the default, that session is `idle in transaction` from taking the lock
until about 0.9 s after the first leadership poll that follows - five seconds or so at the default
`LeadershipPollingTime` of 5,000 ms - and from then on `active`, running the lock monitor's `pg_sleep(60)` inside the
same transaction, the next sleep beginning as the last one ends (measured live at 9.39). So it is idle in its
transaction for a few seconds once, when it takes the lead. From 9.41 the lock is the session's by default and the
leader is not in a transaction at all.

At Marten 9.50.2 (Weasel 9.45, Patched.DistributedLock.Postgres 1.3.3) with `Events.UseAdvisoryLockTransaction = true`
- what a host behind a transaction-mode pooler keeps - there is no `pg_sleep` monitor: the leader sits `idle in
transaction`, with a snapshot, until DistributedLock's keepalive (`SELECT 0`) runs in it about 61 s after the lock,
and again every minute (measured live). Where the connection string sets Npgsql's `Keepalive` or `Multiplexing`,
DistributedLock falls back to 9.39's `pg_sleep` instead: the leader is `active` in it, not idle, and C04 does not judge
it. That reaches C04's minute, so **the leader is recognised by its leadership
lock, never by its name**: a session that holds one of the store's daemon advisory lock ids, granted, in this
database, and is idle in a transaction (`pg_locks` cannot say which scope the lock has), with the leader's shape - no
transaction id and no relation lock - is not raised while it has been idle for less than three minutes
(`ConnectionThresholds.DaemonLeaderIdleBound`, about three keepalive intervals, so a late one on a loaded host is no
finding). The status row says it saw the leader; there is no Info, which would appear every minute on a healthy
host, and T14 already judges the horizon its snapshot holds. Past three minutes it is raised like any other session -
a leader whose keepalive or monitor has stopped, with monitoring turned off or a `LeadershipPollingTime` of minutes,
really does hold back vacuum; `Events.UseAdvisoryLockTransaction = false` is the change that ends it.

The fix is in the application: dispose every session (`await using`), and end what it began. A server-wide
`idle_in_transaction_session_timeout` is the DBA's backstop - and with the leadership lock transaction-scoped it can
also end the HotCold daemon's leadership session: a timeout shorter than the minute or so the leader sits idle in its
transaction (after taking the lead at 9.39, between keepalives at 9.50.2), or any timeout with lock monitoring off.

## C05 Long-running statement

**Warning** when a statement in this database has been running for five minutes.

It holds its locks and its snapshot for as long as it runs. Most often an unindexed query over a large document
table, a rebuild's batch, or a report started by hand. `opts.CommandTimeout` bounds what Marten sends; the Query
page's index advisor says which index a document query needs.

## C06 Lock waits and blocking chains

**Warning** when a session has waited thirty seconds for a lock; **Critical** at five minutes.

What is waited for is labelled the way the store would name it, from the store's own options and without a
connection: **Marten's apply-changes lock** (`ApplyChangesLockId`, 4004 by default), **the async daemon's lock**
(`DaemonLockId`) or **one shard's** (`ProjectionLockIds.Compute` over `Events.DatabaseSchemaName`, the shard and
`DaemonLockId` - the set Marten's own high-water detection uses), **an `mt_streams` row** (two appends to one
stream), **a document table** by its alias. Anything else is "a relation Marten does not own" or "an advisory lock
that is not Marten's", and is named only on the Sessions page.

A lock on an index, a partition, a TOAST table or a column-owned sequence is labelled by **the table it belongs
to** - "an index of the order document table", "a partition of the event store's mt_events table" - and the
statement never selects the locked relation's own name, because those names carry what the label must not: a
tenant partition's the tenant id (`mt_doc_order_acme`), a hidden type's index its alias and an indexed member.
The owner is found in the catalog by oid - `pg_index.indrelid`, `reltoastrelid`, `pg_depend`, and `pg_inherits`
up to a partition's root (never a `pg_partition_*` function) - and classified with the database browser's
classifier over **every** registered store. Only this store's own declared, visible objects and Postgres' system
catalogs are named. A hidden document type's table is "a relation", its part unsaid; another store's table, and an
`mt_` name no store declares, is "a Marten relation this store does not declare", named nowhere, details
included; a per-tenant event sequence is "a tenant's event sequence". When any store's configuration cannot be
read, no relation is named at all.

A blocker in another database - possible on a shared catalog - is "a session in another database", and its
state, role, application and client are not read. The head of each chain - a blocker that does not
wait itself - is described by its state and how long its transaction has been open. Waits are measured from
`pg_locks.waitstart` (PostgreSQL 14+), else from the session's last state change, an upper bound.

## C07 Orphaned prepared transaction

**Critical** when a prepared transaction has waited five minutes for its `COMMIT PREPARED` or `ROLLBACK PREPARED`.

A prepared transaction outlives its session and a restart of the server; nothing ends it on its own. One whose
transaction manager went away holds its locks and row versions for good, holds back vacuum for the whole
database, and can hold Marten's high-water mark still. Its global id and owner are details. A server with
`max_prepared_transactions = 0` (Postgres' default) cannot have any, and the check says so.

## C08 Deadlocks, rollbacks and temp spills

From `pg_stat_database`'s counters, as rates over the advisor's history - which needs two checks one
`HealthInterval` apart while a page is open, and starts again after `pg_stat_reset()`:

- **Warning** for any deadlock in the window: each one rolled back a write.
- **Warning** when more than 5% of at least 1 000 transactions rolled back. Every read the studio makes ends in
  `ROLLBACK`, so the rollbacks this process's studio sent to this database over the window - it counts them per
  database - are subtracted exactly. A backend reports its counters a moment after the fact, so the server may
  not have counted the studio's last few yet; that can only make the subtraction larger. Another process's
  studio watching the same database is not subtracted.
- **Info** when statements wrote temporary files: a sort or a hash that did not fit `work_mem`, usually an
  `ORDER BY` on a document field no index covers.

And per day, over the statistics window - the same counters, cumulative since the database's statistics began, divided
by how long they have counted - once that window is at least a day long and the counters do not look reset (see
[the statistics window](#the-statistics-window) below):

- **Warning** when statements wrote **1 GiB or more of temporary files a day**. It stands on its own: a database that
  spills this much is told so whether or not it spilled in the last hour, and it takes the place of the hour's Info for
  the temp files. Under it, the hour's Info stays as it was.
- A deadlock finding adds the window's **deadlocks per day** to its evidence. Its severity is unchanged: any deadlock in
  the hour is already a Warning.
- Nothing per day is said while the window is under a day, or in the check in which the counters first look reset; the
  status row says how long the window has counted so far.

The kinds of finding are `deadlocks`, `rollbacks` and `temp-files`, as before. Each finding says when the statistics
were last reset. Both studios run it.

### The statistics window

The one place every rate per day - C08's and C11's, and the Storage page's growth - asks since when this database's
counters have counted, and whether they were reset:

- **Since** is the later of `pg_stat_database.stats_reset` and the earlier of the two shared views' `stats_reset`
  (`pg_stat_bgwriter` up to PostgreSQL 16 or `pg_stat_checkpointer` from 17, and `pg_stat_archiver`):
  `max(database, min(shared, archiver))`. From 15, `pg_stat_database.stats_reset` stays null through a crash that
  zeroes every counter, while a crash, a restore and a first start stamp both shared views at one instant on 14 and 17
  alike. A `pg_stat_reset_shared()` of one view - `'archiver'` to clear its failure count, `'bgwriter'` - moves only
  that view and leaves the database's counters alone, so it never moves **Since**.
- **A clean restart is not a reset.** 14 and 15+ keep their statistics across a clean shutdown and start; a crash, an
  immediate shutdown or a restore discards them. So the server's start (`pg_postmaster_start_time()`) moving counts
  only together with a counter going down or a reset time moving.
- **Reset**, for the one check that first sees it, when against the previous check: another server took over (the
  timeline went up - `pg_control_checkpoint().timeline_id`, which shows a promotion once the new timeline has had a
  checkpoint - on a server that started at another time, or the server answering started before the last check with its
  counters down), the cluster's system identifier or major version changed (`pg_upgrade`, a restore from a dump), the
  server restarted without its statistics, a reset time moved, or `xact_commit` or `blks_read` went down. After a
  failover the window starts at the promoted server's reset times; after a counter went down with nothing to say when,
  at the check that saw it.
- **A standby keeps its window.** From PostgreSQL 15 a standby keeps its statistics through a clean restart in
  recovery and through its own promotion, its reset times those of its first start from the base backup; 14 discards
  them at every start of a standby and stamps the times anew (measured on 14.24, 17.11 and 18.6). So neither the watched
  server's own promotion nor a standby following a promotion upstream is a reset. A promoted server counts the rows it
  writes only from its promotion, while its reads count from its first start, so growth just after a failover reads
  low, never high.
- A reset of both shared views at once with this database's counters still climbing - PostgreSQL 17's argument-less
  `pg_stat_reset_shared()` - is not a reset of the window when it is seen live. Seen only after the fact, by a process
  that started since, it reads like a backend's crash and the window begins at it: the shorter side.
- The Storage page, which reads the window without adding to the history, stops calling the statistics reset once
  they have counted for a day.
- The page and each finding say where **Since** came from and what the counters' start looks like: the server's start,
  a reset, a crash, a restore, a failover, a new cluster (only when the system identifier or major version is seen to
  change), or nothing at all.
- **The control functions are optional.** `pg_control_checkpoint()` and `pg_control_system()` are read by statements
  of their own: `has_function_privilege` first, then a call of only the functions it allowed, because PostgreSQL checks
  `EXECUTE` on every function a statement names before it runs, `CASE` or not. When a platform refuses them - revoked,
  missing, or failing with any SQLSTATE - only the timeline and the system identifier are unknown: no failover and no
  new cluster is said from them, a failure is logged at Debug and nowhere else, and the window, C08, C11 and the Storage
  page go on as before.
- When the window's own statement cannot be read, C08's status row says its rates per day were not checked, and the
  Storage page shows its sizes and tables with growth "not available" and why. C11, which is nothing but the window,
  is then not checked for a privilege the role lacks (`42501`), timed out for a statement or lock timeout (`57014`,
  `55P03`), and could not be checked for anything else; the opt-in health check counts the last two as Degraded.

Known limit: from PostgreSQL 15 a database created after the shared views were last reset reads as having counted
since then, because its own `stats_reset` is null and no view an unprivileged role can read says when it was created. Its
rates per day then read low, never high.

## C09 Async shards against the pool

**Warning** when the async daemon hosted in this process runs as many shards on this database, plus its own
connection, as the database's pool has connections.

Each running shard loads and commits through the same pool the application's sessions borrow from, so a
catch-up or a rebuild can take every connection. Per pool: a store with a database per tenant has a pool per
database, and spreading shards across them is C02's budget. A daemon not hosted here, or managed by another
system, is a value: the check says so and finds nothing.

```csharp
opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 100);
// or run the daemon on nodes of its own: AddAsyncDaemon(DaemonMode.HotCold) only there
```

## C10 HotCold daemon behind a transaction pooler, proven

**Critical** when two consecutive transactions on one of this database's connections run on two different
Postgres server processes, this process runs the async daemon `HotCold`, and its leadership lock is
session-scoped (`opts.Events.UseAdvisoryLockTransaction` false - Marten's default from 9.41).

The probe is `select pg_backend_pid()` in two read-only transactions, one after the other, on the sweep's own
connection, which comes from the store database's data source with its pooling as configured. Postgres gives
a connection one server process for its whole life, so a direct connection answers the same process twice. A
pooler that pools by transaction - PgBouncer with `pool_mode = transaction`, Neon's `-pooler` endpoint,
Supabase's Supavisor or PgBouncer on 6543, Azure's PgBouncer on 6432 - may run each transaction on another
server connection, and when it does, the two answers differ. Nothing is locked or written, and each
statement runs under the sweep's `statement_timeout`.

A different process is proof; the same one is **inconclusive**, and quiet. A pooler with one idle server
connection, or one that hands back the most recently released connection first (PgBouncer's default,
`server_round_robin = 0`), answers the same both times. A data source with `Multiplexing=true` sends each
command over whichever physical connection is free, so there two processes would prove nothing, and the
check says it was not checked.

Why it is Critical: a session-scoped advisory lock belongs to the server process that took it, and a
transaction-mode pooler lends that process to other clients between transactions. PgBouncer's feature map
lists session-level advisory locks as "never" working in transaction pooling. The lock then belongs to
whoever borrows the process next, the leader cannot release it from its own connection, and two nodes can
run the same projection, or none can take it over.

What to change (the same as [K04](configuration.md#k04-hotcold-daemon-behind-a-transaction-pooler)):

```csharp
opts.Events.UseAdvisoryLockTransaction = true;

// ...and, on the node that runs the daemon, a direct connection rather than the pooler's
```

The transaction-scoped lock keeps one transaction open on one server connection for as long as the leader
leads, which the pooler cannot hand away - at the cost of that connection and of an open transaction, which
with `Events.UseMonitoredAdvisoryLock` on (the default) at read committed holds a snapshot about a minute at a
time, and with the monitor off or under repeatable read holds back vacuum for as long as it leads (C04, T14).
Neon's and Supabase's documentation both send anything session-scoped to the direct connection.

Never probed for `Solo` (no lock), `ExternallyManaged` (not Marten's lock), a process that runs no daemon,
or a transaction-scoped lock. The two process ids are the studio's own sessions, and still behind
`ViewDatabaseActivity`. This measures what this process's configuration meets; a daemon node with another
connection string may not go through the pooler.

## C11 Low cache hit ratio

**Warning** when fewer than **95 %** of the blocks this database read were found in shared buffers
(`blks_hit / (blks_hit + blks_read)` in `pg_stat_database`) and at least **a million blocks a day** were read from outside
them - both over [the statistics window](#the-statistics-window), so only once it has counted for a day and does not
look reset. Until then the status row says how many hours of statistics there are so far, and nothing is found: a ratio
over an hour after a restart is a cold cache, not a small one.

A block not found in shared buffers is read from the operating system, from its page cache if it is lucky and from
storage if not, so `blks_read` measures the database's own cache, not the disk's. What helps is more memory for the
database, or less to hold in it: an index that reads fewer pages, dead rows vacuumed away, data nobody reads any more moved
out. The finding names no setting and no value: a size derived from the machine's memory is not advice the studio gives.

Its evidence is the ratio, the blocks read a day, both totals, the window and where its start came from. One finding per
database, with no kinds. Both studios run it; every role can read what it reads.

## Database › Sessions

`/marten/database/sessions`, linked from the Database rail: this database's sessions with their state, age, wait
and what blocks them; the locks, labelled as above; and the prepared transactions. Counts are shown with the
store policy; the details need `ViewDatabaseActivity`, and the page names that option where they would be.
There is no cancel and no terminate button: the studio never ends another session.
