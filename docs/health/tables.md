# Health: tables and indexes (T)

These checks answer: **why is the app slow, and what is going to bite us?** They read the catalog and the
statistics views (`pg_class`, `pg_index`, `pg_inherits`, `pg_depend`, `pg_stats`, `pg_stat_user_tables`,
`pg_stat_user_indexes`, `pg_stat_progress_create_index`, `pg_database`) - tier 1, at every sweep - except T09, which reads a bounded sample of the data behind
**Run deeper checks**, and T02, T22 and T23, which read `pg_stat_statements` behind **Run statement checks** (tier 3,
[below](#the-statement-checks-t02-t22-t23)); T14 also reads who holds the vacuum horizon back (`pg_stat_activity`,
`pg_locks`, `pg_prepared_xacts`, `pg_replication_slots`, `pg_stat_replication`). Back to the [index](../health.md).

What every check here has in common:

- **Every table, with the store's declarations laid over it.** The checks judge every ordinary and partitioned
  table in the store's own schemas, and - while `Capabilities.BrowseDatabase` is on - in the schemas
  `BrowsableSchemas` admits (exact names, or `"*"`), not only the tables the store declares.
  - **A "Marten table" is one the store's configuration declares**: a document type's table, the event store's
    tables, a projection's or an `ExtendedSchemaObjects` table, and `mt_hilo`. Each is in the schema it is
    declared in, an `ExtendedSchemaObjects` table in a schema of its own included. That list comes from the
    configuration alone, never from `AllObjects()` or `AllSchemaNames()` (which migrate). Each declared table is
    judged exactly as before: its kind, the origin of each of its indexes, and `StoreOptions` remedies. Marten's own
    event tables and bookkeeping count as the store's own, because their indexes are already told apart by origin.
  - **Every other table** comes from one catalog statement per sweep, estimates only, which locks nothing it lists.
    Each one is classified by name:
    - a table a recognised library names (`qrtz_*`, `wolverine_*`, `__EFMigrationsHistory`, `VersionInfo`, and an
      `mt_` table nobody declares) is that library's. It is exempt from the index-design rules (T01, T04, T05)
      only: vacuum, statistics, size and wraparound still apply to it, and a remedy for those says the library's
      design is left as it is;
    - any other table in the store's own schemas is the host's own relational table, judged by every rule and given
      migration SQL "for your own migrations - the studio never runs this";
    - a table in a schema that only `BrowsableSchemas` admits is somebody else's. It is judged by every rule, with
      migration SQL only for its upkeep.
  - **What is left out.** A document type `IsDocumentTypeVisible` hides is left out: its table is not even read
    while a type may be hidden, because then no table named like a document table is read from the catalog at all.
    Another registered store's tables are left out too: a sweep has no visitor whose policy for that store it could
    ask. Past 2,000 tables, the smallest by estimated size are left out, with the store's own tables always kept.
    What is left out is said in every finding's caveat ("Judged: ..."), as counts and never as names.
  - **Gated like the database browser.** A table outside the store's own schemas is not in one of the store's own
    schemas for the database browser or the Schema screen's lists, and that includes an `ExtendedSchemaObjects`
    table in a schema of its own. So a finding that names it needs the database browser's gate for that schema as
    well as the check's own: `Capabilities.BrowseDatabase`, the write policy and a `BrowsableSchemas` entry (or
    `"*"`) that admits it, as the browser asks before it shows that schema at all. Anybody else has the finding
    counted as not shown to them, and the navigation badge, which cannot ask that gate, counts it as not shown for
    everybody. T12 and T13, which are about the whole database, stay visible instead. They name such a table only as
    "a Marten-managed table outside the store's own schemas" or "a table outside the studio's own schemas", as a
    check row's note does.
- **One read, shared.** The regular checks share one read of the tables and one of their indexes per sweep,
  so the whole area costs a regular sweep nine statements on the sample store - the catalog's list of the tables
  beside the store, those two, T10's `pg_stats` read, T11's two (whether `mt_hilo` exists, then its one row), and
  three for T12-T14: the wraparound read the three share, T14's read of who holds the horizon, and the
  replication-slot read it shares with R01.
- **A list locks nothing it lists.** Sizes are `relpages` estimates, never `pg_relation_size`, which opens the
  table and queues behind any migration's lock on it; partitions are walked through `pg_inherits`, never
  `pg_partition_tree()`, which holds a lock on every partition. A tenant-partitioned table is one row, its
  partitions' figures rolled in, and a partition is never named: Marten names a tenant's partition after the
  tenant. The only row of a user table a regular sweep reads is `mt_hilo`'s (T11), and only once the catalog
  says it exists. `pg_get_indexdef` opens each indexed table for an instant to name its columns, so the index read
  first looks in `pg_locks` and deparses nothing on a table somebody holds or awaits `ACCESS EXCLUSIVE` on - a
  migration of the store's or of any library's: T03-T06 then say "not checked" for that sweep, without waiting, and
  read the indexes again at the next. Nothing is held past a call.
- **Rates, not totals.** The `pg_stat_*` counters run from the last statistics reset, which may be a year ago,
  so T01 and T06 sample them into the in-memory history and judge the change over the window it holds, and say
  "watching" until two samples exist. A counter that goes down is a reset, and the window restarts there.
- **Held, not glimpsed.** A batch delete or a bulk load makes any table look bloated or unanalysed for the
  minute before autovacuum wakes up, so T07 and T08 speak only when the condition has held for ten minutes of
  samples - unless autovacuum is switched off for the table or the server, when there is nothing to wait for.
- **Remedies are `StoreOptions` lines for a Marten table**, and migration SQL for the host's own relational table
  (built with every name quoted, and never run by the studio). `FillFactor(n)` and `StorageParameter(name, value)` need Marten 9.47
  or later, and say so. Marten manages only the storage parameters a mapping declares and does not reset one it
  does not, so a DBA's `ALTER TABLE ... SET (...)` on an older Marten is left by an apply - unless the apply
  rebuilds the table, as a changed partition scheme does (`AutoCreate.CreateOrUpdate` is not additive). It is
  described in words, never offered as a button.
- **A partitioned table's storage parameters are its partitions'.** Postgres keeps none on a partitioned table
  itself, and Weasel writes and compares a declared one per partition, so the catalog walk rolls the partitions'
  `reloptions` up: a parameter every partition has at one value is the table's (so `autovacuum_enabled = false`
  on every partition switches autovacuum off for T07 and T08), and one they disagree on is "partitions differ" -
  by the parameter's name, never a partition's.

## T01 Sequential scans on a large table

**Warning.** A table with more than 100,000 live rows whose sequential scans read the table's worth of rows at
least three times over the window, with an average scan (allowing for `max_parallel_workers_per_gather`, since a
parallel scan counts once per worker) reading at least half of it.

A sequential scan reads every page to find the rows a query wants. On a large table that is the query's whole
cost, and it grows with the table. Which statement it was is T02's to say, behind **Run statement checks**; the
remedy here is the index line, and the finding links to Query in Marten mode on the collection, whose index advice says
whether an index serves a where clause and which member it needs:

```csharp
opts.Schema.For<Order>().Index(x => x.Status);
// or, where it is also sorted on or compared as a column:
opts.Schema.For<Order>().Duplicate(x => x.Status);
```

The studio's own exact counts, `pg_dump` and a one-off report are sequential scans too.

**A partitioned table is judged by its largest partition.** A tenant-partitioned table counts one scan per
partition read, and a per-tenant query that reads its own small partition whole is often the right plan. So the
100,000 line applies to the table's largest partition, and so does "an average scan read a good part of one
scan's worth" - never the mean partition, which one large tenant among many small ones makes small enough for
every per-tenant scan of a small partition to pass for a whole-table scan. Both are rolled up from the catalog
without opening a partition. Fifty tenants of 4,000 rows, each query scanning its own tenant's partition, is no
finding, and neither is one tenant of 200,000 beside 499 of 1,000 whose queries never read the large one; a
finding on a partitioned table is worded "partition-wide". The counters are rolled up, so a scan of a large
partition and a scan of a small one cannot be told apart: whole scans of the large partitions that are
outnumbered by per-tenant scans of small ones pull the average down and are missed - the quiet side of the line,
on purpose.

## T03 Invalid index

**Critical** (or **Warning**, see below). An index on a Marten table that Postgres has marked invalid
(`pg_index.indisvalid = false`), or invalid indexes on a partitioned table's partitions (counted, never named) -
and that no index build is working on right now.

A `CREATE INDEX CONCURRENTLY` that failed half-way - a unique build that met a duplicate, a cancelled statement
- leaves an invalid index behind, and so does a partitioned index some partition has no index attached for. The
planner never uses it; every insert and every update still maintains it.

**A build that is still running looks exactly the same**: `CREATE INDEX CONCURRENTLY`, `REINDEX CONCURRENTLY`'s
`_ccnew` index and Weasel's concurrent build on a partitioned table until its last partition is attached are all
invalid while they work, and re-applying then would queue a `DROP INDEX` for an `AccessExclusiveLock` behind the
build, with every query on the table queued behind that. So the check reads `pg_stat_progress_create_index`
(which takes no lock) beside `pg_index`: an invalid index a build is working on is no finding - the check's note
says "being built" and the phase. Postgres hides the table of another role's build from a role without
`pg_read_all_stats`; while any such build is running, an invalid index is a **Warning** whose text and caveat say
so, never a Critical.

"Working on" is narrow: the build's own index (`index_relid`); for a `REINDEX CONCURRENTLY`, the `_ccnew` or
`_ccold` copy of the index it reports (its first phase reports the index it copies, not the copy); and for a
partitioned index, a `CREATE INDEX` on one of its partitions, which it waits for to be attached. A build of
**another** index on the same table is not a reason this one is invalid - matching it would hide an older failure
for as long as a nightly rolling `REINDEX` ran - so the older failure stays a finding, and its caveat says a build
is running on the table and to let it finish before re-applying or dropping anything.

On an ordinary table, re-apply the configuration. Weasel reads an invalid index as drift even when its definition
matches the declaration, so an apply drops it and builds it again (weasel#503); one nobody declares is dropped by
the same apply:

```csharp
await store.Storage.Database.ApplyAllConfiguredChangesToDatabaseAsync();
// or: dotnet run -- db-patch (review it), then dotnet run -- db-apply
```

The apply is the whole configuration's, under `AutoCreate.CreateOrUpdate`, which is **not additive**: it also
drops every undeclared index on a Marten table and alters changed columns - read the script first. An index the
configuration ignores (`IgnoreIndex`) is skipped by an apply (short of one that rebuilds the whole table) and
needs a DBA.

**On a partitioned table a re-apply cannot resume the build**: Weasel reads only the partitioned table's own
indexes, and rebuilding under the same partition index name fails (`42P07`) while the leftover index on the
partition is still there. The finding says, in words, that a DBA drops that leftover partition index first; the
next apply then builds the index again. It offers no code and names no partition, because Marten names a
tenant's partition after the tenant.

## T04 Redundant index

**Warning.** An index whose job another valid index on the same table already does: the same access method,
predicate, operator classes, collations and sort order, and keys that are an exact duplicate or a leading prefix
of a b-tree's - with its `INCLUDE` columns among the other's.

It is never a primary key, and never a unique index unless the other is unique on exactly the same keys, because
a unique index on `(a)` enforces more than one on `(a, b)`. Nor is it ever an exclusion constraint's index
(`indisexclusion`), a unique `NULLS NOT DISTINCT` index unless the other is one too (`indnullsnotdistinct`), or an
immediate unique index when the other is `DEFERRABLE` (`indimmediate`): each of those enforces something the other
does not, and `ON CONFLICT` can use only an immediate one. An index Marten adds itself (`mt_deleted`,
`mt_doc_type`, `tenant_id`) or the event store declares is never reported as the redundant one. A redundant index
costs every write and buys no read; remove its declaration (`.Index(...)`, `.Duplicate(...)`, `.UniqueIndex(...)`),
and the next apply drops it. One nobody declares is dropped by the next apply anyway.

## T05 Unused index on a write-heavy table

**Warning** (Info until "never scanned" is proven, see below). A valid, non-unique index of at least 1 MiB that
nothing has scanned since the counters began, on a table that has taken at least 100,000 inserts, updates and
deletes in that time.

Every insert, and every update that is not HOT, writes an entry into every index. The finding states the size
and the write volume, and how old the counters are: `pg_stat_reset()`, a crash and a restore zero `idx_scan`, and
an index used only by a monthly job looks unused for most of the month. Unique indexes, primary keys, exclusion
constraints, Marten's own structural indexes and the event store's are never reported.

The finding is only **Info** while "never scanned" is unproven: counters reset less than a day ago, or counters
that were never reset, whose age is unknown. The advisor's own samples are the proof it can offer there: once
they have seen the index unscanned, without a break, for a day, the finding is a Warning.

On a **multi-host data source** the finding is Info always. Its reads may go to a standby, which counts its own
index scans, and the advisor's samples are of the primary's counters - no length of them says anything about a
standby's. The finding says to check `pg_stat_user_indexes` on every standby before removing the index.

## T06 Low HOT-update ratio

**Warning** (or **Info**, see below). Fewer than half of a document table's updates were HOT updates, over more
than 10,000 updates in the window.

A HOT update stays on its page and touches no index; it needs free space on the page and no change to an indexed
column. With the default fillfactor of 100 the pages are full, so leave room:

```csharp
opts.Schema.For<Order>().FillFactor(80);   // Marten 9.47+
```

Fillfactor affects only pages written after the change. **Unless an index reads a column every upsert
changes**: Marten rewrites `data`, stamps `mt_last_modified` and draws a new `mt_version` on every upsert, so a
GIN index on the document, any computed `Index(x => x.Member)` (an expression over `data`) or
`IndexLastModified()` makes every update non-HOT whatever the free space. Then the finding is **Info** and says
fillfactor cannot help, naming the indexes; for a computed index the answer is a duplicated field, a column of
its own that changes only when the member does:

```csharp
opts.Schema.For<Order>().Duplicate(x => x.Status);   // in place of .Index(x => x.Status)
```

The columns an index reads are Postgres' own record of them (`pg_depend`). A BRIN index is left out on
PostgreSQL 16 and later, where a summarizing index no longer blocks HOT, and counted before 16, where it does.
Event-store tables are not judged: `mt_dcb_tag_version`'s fillfactor is E08's.

## T07 Vacuum falling behind

**Warning.** More than 20% of a Marten table is dead rows, at least 10,000 of them, and it was last vacuumed
(by hand or by autovacuum) more than a day ago or never - held for ten minutes of samples.

A dead row is an old version an update or a delete left behind; only vacuum makes its space reusable, and until
it runs every scan reads it. For a document table:

```csharp
opts.Schema.For<Order>().StorageParameter("autovacuum_vacuum_scale_factor", 0.02);   // Marten 9.47+
```

so autovacuum runs at 2% dead rather than 20%. On a table autovacuum is switched off for, the remedy is
`StorageParameter("autovacuum_enabled", true)`; with the server's `autovacuum = off` it is a DBA's. A
partitioned table counts as switched off when every partition is; when its partitions disagree, the finding
says autovacuum may be off for some of them. A DBA's `ALTER TABLE ... SET (...)` on a table is not reset by an
apply unless the apply rebuilds the table, as a changed partition scheme does. A long-running transaction stops
vacuum removing dead rows even when it runs - see the connections checks.

## T08 Never analysed, or stale statistics

**Warning.** A Marten table with at least 1,000 live rows that has never been analysed (`reltuples = -1`), or
whose `n_mod_since_analyze` is at least 10,000 and more than a fifth of `reltuples`, with no analyze in a day -
held for ten minutes of samples.

The planner chooses every plan from these statistics, and the studio's `~` counts are `reltuples`: both are
wrong until the table is analysed. For a document table:

```csharp
opts.Schema.For<Order>().StorageParameter("autovacuum_analyze_scale_factor", 0.05);   // Marten 9.47+
```

A DBA's `ANALYZE` fixes the statistics now; the parameter keeps them fixed. A partitioned table is judged by its
partitions' own statistics, because autovacuum never analyses a partitioned parent.

## T09 Large documents

**Info**, or **Warning** past 512 KiB. A deeper check: behind **Run deeper checks**, cached for five minutes.
The 95th percentile of a collection's stored document size (`pg_column_size(data)`, compressed) is at least
64 KiB, from a `TABLESAMPLE SYSTEM` read of about 2,000 rows.

Marten reads and writes a document whole: every load deserializes all of it, every upsert writes all of it again
into TOAST and leaves the old version for vacuum. A document that keeps growing is usually two things that want
to be stored apart - model it, or split it. The share of the table in TOAST comes from `pg_class` and is free.

Never sampled: a hidden type's table, a partitioned table (a sample would lock every partition), and a table of
fewer than 1,000 rows - one enormous document is a document, not a pattern. Sampling stops after 20 tables or
ten seconds, largest first.

## T10 Soft deletes accumulating

**Info.** More than 30% of a soft-deleted collection's documents are deleted, by `pg_stats`' most-common-value
frequency for `mt_deleted` - no scan, no count. Only for a document type whose `DeleteStyle` is soft; a role
that may not read the column sees no statistics, and the check says it cannot see.

Every query filters the deleted rows out of every page it reads. If they are kept for an audit, purge them after
the retention period:

```csharp
await using var session = store.LightweightSession();
session.HardDeleteWhere<Order>(x => x.DeletedBefore(DateTimeOffset.UtcNow.AddDays(-90)));
await session.SaveChangesAsync();
```

## T11 Numeric id headroom

**Warning** at 50% of `int.MaxValue`, **Critical** at 80%. A HiLo sequence that hands out `int` ids (an `int` id,
or a strong-typed id wrapping one) has issued blocks reaching `(hi_value + 1) × MaxLo`.

An `int` id past `int.MaxValue` does not fail: it wraps to a negative number. Blocks are drawn per process and
abandoned on every restart, so the sequence runs out faster than the row count suggests. Move to a `long` id
before it matters - an id-column change Marten's apply makes, which every reference to the ids must follow.

Which types use HiLo is read from the mappings, never from `database.Sequences`, whose first touch creates
`mt_hilo` (hard rule 14's own trap). `mt_hilo` is read only once the catalog says it exists; when it does not,
no HiLo id has been issued yet, and the check says so.

## Wraparound and the vacuum horizon (T12, T13, T14)

These three share one database-wide read, `pg_database` and a scan of `pg_class` - a catalog read that locks
nothing it lists - and T14 adds one of `pg_stat_activity`, `pg_locks` and `pg_prepared_xacts` and one of
`pg_replication_slots` and `pg_stat_replication`. All three run in the regular sweep, at `HealthInterval`, never on a
poll.

- **The whole database counts, and only the store's tables are named.** Wraparound protection acts on the
  database's `datfrozenxid` - the oldest table in it, the store's or not - and Postgres refuses writes to *every*
  database once the server's oldest reaches its line. So a never-frozen legacy table, another library's job store, a
  temporary table or a document type the studio hides holds the age up as much as a Marten table, and is counted;
  only the store's own, visible tables are named (D27). A temporary table is called out: autovacuum cannot freeze
  one, only the session that created it can.
- **A forecast, and only what the window supports.** Each sweep samples `pg_snapshot_xmax(pg_current_snapshot())`
  (the next transaction id, read without assigning one; PostgreSQL 13+) and the next multixact id
  (`datminmxid + mxid_age(datminmxid)`, from shared memory, needing no privilege). Two samples give a rate, and the
  forecast is the time to the *forced-vacuum line* (`autovacuum_freeze_max_age`) and to the *write-refusal line* - a
  margin short of 2^31: three million on PostgreSQL 14 and later, one million on 13 (the PostgreSQL documentation's
  "Routine Vacuuming"). Every forecast says "at the rate of the last N minutes", with N the run of samples the
  in-memory history really holds; below two samples, or when the counter did not move, there is none; and nothing is
  forecast more than 90 days out. The forecast is linear and assumes nothing freezes in between - which is false
  before the forced line (autovacuum is about to freeze), so there the time to the forced line is only a status note,
  and past it a forecast alone is a finding only once the age has stayed past the line for half of what the in-memory
  history is sure to hold - `(60 - 1) x HealthInterval / 2`, and never more than 30 minutes: 29 min 30 s at the default
  `HealthInterval` of a minute, 14 min 45 s at 30 seconds, 7 min 22 s at 15 (the same bound D03's window takes). A
  routine anti-wraparound vacuum, or `template0` and `postgres` reaching the line, cross it and come back sooner.
- **The lines are the server's own** (kept from H4): Warning 40% and Critical two-thirds of the way from the forced
  line to wraparound.

## T12 Transaction-id age

**Warning** 40% of the way from the server's `autovacuum_freeze_max_age` to wraparound, **Critical** two-thirds
of the way - about 1.0 and 1.5 billion at the default of 200 million. The age judged is the oldest of three: the
store's tables (their own `age(relfrozenxid)`, their TOAST tables' and their partitions'), this database's
`age(datfrozenxid)`, and the server's oldest database's (a database `DROP DATABASE` left invalid is not counted).
And, from the forecast: **Warning** when the age has stayed past `autovacuum_freeze_max_age` for that half of the
history (29 min 30 s of samples at the default `HealthInterval`) and, at the rate of the last N minutes, writes would be
refused within 90 days - **Critical** within seven - even below the Warning age.

Postgres must freeze every row before its 32-bit transaction counter wraps, and refuses writes to the whole server
at about 2.1 billion. Autovacuum forces a freeze at `autovacuum_freeze_max_age`, so a table this far past it is one
autovacuum has not managed to freeze - usually a transaction, a replication slot or a prepared transaction holding
the horizon back (T14 names it). The lines are measured from the server's own setting, read with the tables,
because a DBA who raised it to a billion and a half has a server on which a table at 1.2 billion has not yet been
asked to freeze. One finding for the database, naming the oldest of the store's tables and both lines, and counting
the other tables past `autovacuum_freeze_max_age` (how many are temporary) without naming them. Past the forced line
the forecast is the time left if the freeze autovacuum has been asked for never finishes. The cure is a DBA's
`VACUUM (FREEZE)`, once whatever holds the horizon is gone.

## T13 Multixact age

**Warning** 40% of the way from `autovacuum_multixact_freeze_max_age` (400 million by default) to wraparound,
**Critical** two-thirds of the way or at `vacuum_multixact_failsafe_age` (PostgreSQL 14+, 1.6 billion by default),
whichever comes first - and the same forecast as T12, over the multixact counter. The age judged is the oldest of
the store's tables' `mxid_age(relminmxid)`, this database's `mxid_age(datminmxid)` and the server's oldest. A forecast
alone waits, as T12's does, for the age to stay past the forced line for half of what the history holds (29 min 30 s
at the default `HealthInterval`).

A multixact is the id Postgres gives a set of transactions that lock one row at the same time. Multixacts are 32-bit
and frozen by vacuum like transaction ids, and past their own stop limit Postgres refuses every write that needs a
shared row lock. A row lock that two transactions hold at once - `FOR SHARE`, `FOR KEY SHARE`, the lock a
foreign-key check takes on the row it references - is a multixact, so concurrent inserts of documents whose type
declares a foreign key to one parent row make them steadily. **Marten's own event appends do not**, measured at
Marten 9.39 and 9.50.2: an append inserts into `mt_events` under a foreign key to `mt_streams`, but concurrent Quick
appends to one stream serialise on `pk_mt_events_stream_and_version`, and every loser fails there, before its
foreign-key check would share the stream's row.

Multixacts are held back by a long transaction that is itself a member of one, or a prepared transaction; unlike
transaction ids, replication slots do not hold them back. Multixact *member* storage, which can run out before the
ids do (about 20 GB), is not read: Postgres has no view of it.

## T14 Vacuum held back

**Warning** when the oldest thing holding back vacuum of the store's tables has held it for an hour, **Critical** at
24 hours, or at an hour while the database is near T12's line. Whatever its age in time, a holder further back than
`autovacuum_freeze_max_age` is a **Warning** - the forced freeze cannot get past it - and past T12's Warning age
**Critical**.

Vacuum removes a dead row, and freezes a live one, only once no transaction could still see it; the oldest
transaction id or snapshot anything holds is that line, the *horizon*. Four kinds of thing hold it, and the check
reads each where Postgres keeps it and names the most severe holder - with the oldest of all stated beside it, when
that one is further back:

| Holder | Read from | Holds back |
|---|---|---|
| A session's open transaction or snapshot | `pg_stat_activity.backend_xid`, `backend_xmin` | this database's tables |
| A prepared transaction | `pg_prepared_xacts` | this database's tables |
| A replication slot | `pg_replication_slots.xmin` (every table), `catalog_xmin` (the system catalogs) | every database |
| A standby's `hot_standby_feedback` | `pg_stat_replication.backend_xmin` | every database |

A session or a prepared transaction in *another* database, and a logical slot's `catalog_xmin`, hold back only the
system catalogs - but every database's `datfrozenxid` counts the shared ones, so they stop every database's freeze.
They are judged only by how far back they hold it, and another database's holder is never named - nor counted, and a
logical slot of another database is "a logical slot of another database on this server" at every gate.

A lazy `VACUUM` is not a holder, although it shows a `backend_xmin`: Postgres marks it `PROC_IN_VACUUM` and leaves it
out of the horizon, so autovacuum, and a DBA's `VACUUM (FREEZE)` - T12's own cure - are skipped (by their row in
`pg_stat_progress_vacuum`). So are a manual `VACUUM`'s parallel workers, which have no row there: a role that may read
them sees `backend_type = 'parallel worker'`, which is not a client session; a role that may not sees a withheld row,
which is skipped when it holds only a snapshot at a running `VACUUM`'s `backend_xmin`, under that `VACUUM`'s role and in
its database - `leader_pid` would say it outright, but Postgres withholds it from such a role. `VACUUM FULL` and
`ANALYZE` do hold the horizon, and are holders.

An invalidated replication slot is not a holder either: Postgres leaves its `xmin` and `catalog_xmin` in
`pg_replication_slots` and skips the slot when it computes the horizon. One it reports `lost` but has not invalidated
yet - its WAL is gone, no consumer can use it again, and the next checkpoint will invalidate it - still holds, and is
called a lost slot: drop it.

The finding gives the holder's age in xids and in time and joins it to T07: the dead rows in the store's tables,
and how many of them sit in tables vacuumed since the holder began - rows vacuum ran and could not remove.

- **Age in time, never a transaction's age alone.** A read-committed transaction takes a fresh snapshot with every
  statement, so a transaction open for a day can hold the horizon back by a minute. Marten's own HotCold leader is
  one, with its lock transaction-scoped (`Events.UseAdvisoryLockTransaction`, the default at 9.39 and 9.40): it holds
  its leadership lock in a transaction it never ends, and its lock monitor (`Events.UseMonitoredAdvisoryLock`) runs a
  statement in it about once a minute - `pg_sleep(60)` at Marten 9.39 (Weasel 9.33); at 9.50.2 (Weasel 9.45,
  Patched.DistributedLock.Postgres 1.3.3) no sleep at all, but DistributedLock's keepalive (`SELECT 0`) about 61 s
  after the lock and every minute on, the session idle in its transaction in between - or, where the connection string
  sets Npgsql's `Keepalive` or `Multiplexing`, `pg_sleep(60)` again, the session `active`, as at 9.39 - so at read
  committed its `backend_xmin` is never more than about a minute old, measured at both. So the age is a lower bound, from
  Postgres' own timestamps - a running statement's age, the time a session has been idle in its transaction, each
  capped at the transaction's own age (a `DO` that commits in a loop, or a concurrent index build, is one statement
  across many short transactions), a prepared transaction's age, an inactive slot's `inactive_since` (PostgreSQL 17) -
  or, where it is longer, from the advisor's own samples of the transaction-id counter, worded "at least 45 min (by the
  advisor's samples of the transaction-id counter)". Plainly:
  - **Only an id the holder owns is dated from the samples**: a session's own transaction id (`backend_xid`), or a
    prepared transaction's. The date is the first sample already past it - a bound, never an estimate from a rate, so
    a burst of ids cannot make a minute-old hold look old, and a gap in the samples can only make it shorter - and it is
    never more than the holder's transaction has been open.
  - **That dating reaches no further back than the in-memory history**: its oldest sample, 60 samples back - about an
    hour at the default `HealthInterval`. A transaction that has written and stayed open all day is "at least 59 min"
    by the samples until something longer is known - its statement's age, its time idle in its transaction.
  - **A snapshot is dated by Postgres' timestamps only.** A snapshot's `xmin` is the oldest transaction running
    anywhere when it was taken - somebody else's id, which the counter dates for its owner and not for the snapshot.
  - **A replication slot and a standby are judged by how far back they hold**, in transactions: Postgres records no
    time for either, bar an inactive slot's `inactive_since` on PostgreSQL 17.
- **Behind another.** A snapshot whose `xmin` is exactly an id another holder owns is *behind* that holder: it moves on
  the moment the owner ends. It is never the holder named - the owner is, with "Behind it, at the same point" saying
  what waits on it - and the owner takes the reach and the longest age of what is behind it, because a snapshot that saw
  it running was taken while it ran. The usual case is Marten's own HotCold leader, whose minute-old snapshot a
  prepared transaction or a long writer pins at its own id; and a transaction in *another* database holds this
  database's tables the same way, through the snapshots sessions here take while it runs. Among holders equally far
  back the one that owns its id is named, and the daemon's leader is never named over another holder. A slot is read
  in another statement, so it is never matched, and is judged as its own holder.
- **Who it is** - the process id, role, application, client and connection time of a session, a prepared
  transaction's gid and owner, a slot's name, plugin and database, a standby's address - is evidence behind
  `MartenStudioOptions.Capabilities.ViewDatabaseActivity`. The kind and the age are shown at the database-wide gate.
- **Without `pg_read_all_stats`** Postgres still shows a role every session's transaction ids, role and application
  name - measured on PostgreSQL 17 - and withholds their state, timings, client address and statement. So another
  role's session is still found and judged by how far back it holds the horizon; the caveat says how many sessions the
  role cannot see the start of. One that has written is dated from the counter's samples past its own transaction id;
  one that holds only a snapshot has no age in time.
- **Marten's angle.** A long rebuild batch and an open transaction that has written are one mechanism with two
  symptoms: the writer pins the high-water mark (D01) and its transaction pins the horizon - the finding links D01's
  Explain, which names the transaction. A session holding one of the store's daemon locks is named as the store's own
  HotCold leader - but never for another holder's transaction (see *Behind another*), and with the defaults at read
  committed it is never old enough alone to raise. At Marten 9.39 and 9.40 with `Events.UseMonitoredAdvisoryLock` off,
  the leader sits idle in its leadership transaction and its snapshot is never refreshed - measured - so it really
  does hold the horizon for as long as it leads, and T14 says so after an hour (the time idle in a transaction is
  Postgres' own), with `opts.Events.UseAdvisoryLockTransaction = false;` as the remedy - or, for a host that must keep
  the transaction lock behind a pooler, `Events.UseMonitoredAdvisoryLock = true`, which at read committed - Postgres'
  default - bounds the hold to about a minute (measured at 9.39 and 9.50.2). At 9.50.2 the monitor is the keepalive
  (read by decompiling Patched.DistributedLock.Core 1.0.11 and Patched.DistributedLock.Postgres 1.3.3):
  `ConnectionMonitor` starts its worker only when a keepalive cadence is set - infinite, so never, by default - or a
  monitoring handle (`HandleLostToken`) is registered, and a monitoring handle makes it call the connection's
  `SleepAsync` for at most a minute at a time. Postgres 1.3.3's `SleepAsync` waits on the client
  (`NpgsqlConnection.WaitAsync`), the session idle in its transaction meanwhile, then runs
  `SELECT 0 /* DistributedLock connection keepalive */`. On a connection whose connection string sets Npgsql's
  `Keepalive` or `Multiplexing` it falls back to 9.39's shape: `SAVEPOINT`, `pg_sleep(@sleepTimeSeconds)` and
  `ROLLBACK TO SAVEPOINT`, the session `active` in the sleep - the same bound on the hold, read as a running statement
  rather than an idle transaction. (A connection a caller hands DistributedLock is not monitored at all; Weasel's come
  from a data source, so they are its own.) With monitoring off nothing runs in the leader's transaction at all. Under `default_transaction_isolation = 'repeatable read'` or
  `'serializable'` the snapshot is the transaction's own and the monitor does not move it; only the session lock helps.
  From 9.41 the lock is the session's and the leader keeps no transaction open: its lock monitor's statement holds a
  snapshot for at most about a minute at a time.

The cure is in the application or with a DBA, never a button: end the transaction (dispose every session); commit or
roll back the prepared transaction (C07); restart a slot's consumer or drop a slot nothing uses (R01 watches the WAL
it retains); end the long query on the standby.

## The statement checks (T02, T22, T23)

T02, T22 and T23 read `pg_stat_statements`, the extension that counts every statement the server has run - tier 3.
What they have in common:

- **Only behind Run statement checks, and only through the activity gate.** They read other people's statements,
  so they run only when a visitor with `MartenStudioOptions.Capabilities.ViewDatabaseActivity` (and the write
  policy for the database as a whole) asks, and their result is cached for five minutes. A visitor without the gate
  sees the button off, the setting or policy it needs, and none of their findings - not even a count. Every
  statement shown is masked for the visitor reading it with Database › Sessions' own rule: a withheld schema's name
  is masked, and a statement that names a hidden document type's table, a Marten document table no registered store
  declares while types may be hidden, or a table of a store the visitor may not see is withheld outright.
- **Read as the store's own role, and only its own statements.** Every other health read takes `SqlConsoleRole`.
  This one does not, on purpose: Marten's statements run as the store's role, and `pg_stat_statements` shows a role
  that is neither a superuser, a member of `pg_read_all_stats` nor the statement's own user only `<insufficient
  privilege>` for them - so a narrowed console role, the configuration a careful host has, would see nothing of the
  store. The reads are bounded to `userid = current_user` and this database, so what is read is what the store's
  role ran here itself - which includes any other application that connects as the same role, and no other role's
  statements. The plan of a statement still runs as `SqlConsoleRole`, like the Query page's.
- **Not every text is shown.** `pg_stat_statements` replaces a plannable statement's constants with `$n`, but not
  every statement's: a utility statement can keep its own as they were written - `copy (select ... where
  data->>'ssn' = '123-45-6789') to stdout` on PostgreSQL 17, `CALL`, `DECLARE` and `select ... into` on 13 to 15.
  Read as the store's role, such a text could show a value the host's narrowed `SqlConsoleRole` would have hidden on
  Database › Sessions. So a text is withheld when it still carries a string or dollar-quoted constant (`'...'`,
  `E'...'`, `U&'...'`, `$tag$...$tag$`), when its first word is not `select`, `with`, `insert`, `update`, `delete`,
  `merge`, `values` or `table`, or when it is a `select ... into`: the finding stays, with its numbers, and its
  Statement says why the text is not shown. The text is read with the studio's own SQL lexer, so a comment cannot
  hide a literal from the rule. What it does not catch is a **numeric** constant in a text Postgres kept
  un-normalised - an entry re-created after an eviction or a reset can keep every constant as written - because
  `order by 1` and a type's `(10)` look the same; such a number is shown.
- **Comments are never shown.** A comment in a statement is another application's free text - a
  sqlcommenter-style `/* user='alice@example.com' */` tag from any application that connects as the same role - so
  every comment in a shown text is printed as `/* ‹withheld› */`, a `--` comment keeping its line break.
- **Counters first, then text - and what that saves.** The counters are read with
  `pg_stat_statements(showtext := false)`, which never opens the extension's query-text file, and only each check's
  top few are kept, ranked on the server: at most 20 statements, in shares that sum to 20 (10 by time, 4 by mean, 3
  guarded writes, 3 by JIT time). The text call asks for those statements by `queryid` - but the filter bounds only
  the rows returned: the extension loads its whole text file and fetches every entry's text before the filter sees a
  row (PostgreSQL 17's `pg_stat_statements_internal` with `showtext := true`), so one text call costs what reading
  the plain view costs. The studio therefore keeps each text it has read, per database (up to 200 texts of at most
  4,096 characters, in memory, for the life of the process - so the memory grows with the number of databases whose
  statement checks were run), and asks only for the ones it does not hold: a run whose statements are all known makes
  no text call at all.
- **A window, once there is one.** Each check samples its statements' counters into a statement history of their
  own (beside the database's, read only by these checks) and, from the second run of the statement checks on, judges
  the change between runs. Before that it judges the totals since the statement's counters began - the entry's own
  `stats_since` on PostgreSQL 17, the extension's `stats_reset` before it - and the finding says which.
- **What it cannot see.** `pg_stat_statements` records a statement when it completes, so a statement that failed is
  never counted. Another role's statements are not read. When the extension evicted entries in the window
  (`pg_stat_statements.max`, 5,000 by default; `pg_stat_statements_info.dealloc` rose) the finding says that a
  statement's counters may have restarted inside it.
- **Expected absences are values.** The extension not installed in this database, its library not in
  `shared_preload_libraries` (the view then fails with `55000`), a version older than 1.8, or no statement of the
  store's role recorded yet: each is a "not checked" row saying what would enable the checks, and a Debug line. A DBA
  enables it by adding `pg_stat_statements` to `shared_preload_libraries`, restarting, and creating the extension in
  the store's database; managed platforms switch it on in their parameter settings.

## T02 Expensive statements

**Warning** for a statement of the store's own role on one of its tables whose execution time over the window was 10%
of the window's length or more (six minutes an hour), or that averaged 500 ms a call over at least 10 calls; **Info**
from 2% (72 seconds an hour). Several connections running a statement at once can add up to more than the window
itself, and the finding then says how many times the window's length it took. A share is judged only on at least 10
seconds of execution in the window: an entry whose counters began a few seconds ago has spent a large share of its
short life on any burst, and a share of so little is not a cost. At most five findings a run, worst first. The statement must name a table the store's
configuration declares - a hidden document type's table is never one - and be the application's workload: a
migration's DDL, a transaction's `begin` or an `explain`, which `pg_stat_statements` records too, is not judged.

The finding carries the statement (masked for you), its calls, execution time and mean, its share of the role's
statement time, rows per call - a read that returns a thousand rows or more each time is often a `ToListAsync()` with
no `Take()` - the temporary blocks it spilled to disk (C08 counts the database's temporary files), its share of the
role's WAL, and its plan:

```csharp
opts.Schema.For<Order>().Index(x => x.Status);
// or, where it is also sorted on or compared as a column:
opts.Schema.For<Order>().Duplicate(x => x.Status);
```

**The plan, and what it can say.** The five most expensive statements are planned without their values: the text is
prepared as `marten_studio_plan` - always that name, so that the plans add a fixed handful of entries to the host's
`pg_stat_statements` rather than a new one each time - explained under `plan_cache_mode = force_generic_plan` in
`FORMAT JSON`, and deallocated. A prepared statement outlives the transaction it was made in, so a plan that stops
early - refused, cancelled, cut off - takes it back in a transaction of its own, and once more outside any
transaction if that fails. If both fail, that session goes back to the host's pool holding at most the one
`marten_studio_plan` name - a prepared read that runs nothing unless it is executed by name - until Npgsql resets the
session with `DISCARD ALL` (which it does while it has no prepared statements of its own on it) or the studio's next
plan on it meets the name, deallocates it and plans again; a Debug line (9281) says so. The studio never clears the
host's pool, terminates a session or runs `DISCARD ALL` on one. A statement whose text is withheld is never planned,
because its plan would show what the text hides. It is never `ANALYZE`, nothing is executed, and only a plain
`select` is planned: planning an `insert`, `update`, `delete` or a `select ... for update` takes the lock its
execution would, where planning a read takes the
`AccessShareLock` a read takes, under the studio's `lock_timeout`. That is the plan `EXPLAIN (GENERIC_PLAN)` prints on
PostgreSQL 16, read on any version: `GENERIC_PLAN` itself cannot be used from Npgsql, whose extended protocol either
binds no values (and fails) or binds values that the planner then folds in as constants. The plan is of the text
`pg_stat_statements` recorded, in which a plannable statement's constants are placeholders - including the JSON
member names Marten writes into its SQL as constants, so `d.data ->> 'Status'` is recorded as `d.data ->> $2`. A
generic plan can never use an expression index for a filter whose key is a placeholder (measured on PostgreSQL 17:
the same statement is a sequential scan with the key as `$2` and an index scan with the key written out). So a
sequential scan filtering on such a member is **not** taken as a missing index: the finding says the member cannot be
named, gives the line with a placeholder member to add if no index on that member exists already, and links to Query
in Marten mode on the collection, whose index advice names the member and says whether an index serves it. What the
plan does say soundly - a whole-table read filtering on a duplicated column, a containment (`@>`) test, a JSON path
written with literal keys, or no filter at all - goes to the same index advisor the Query page uses, and its line is
the remedy. A statement that was not planned - not a plain `select`, cut, or one
`SqlConsoleRole` may not read - says why.

The studio's own reads run as the store's role too when `SqlConsoleRole` is not set, and a **Run deeper checks**
read of a large event table is a statement on the store's tables; such a statement can appear here.

## T22 Optimistic-concurrency conflicts

**Info** when any of a document type's guarded writes conflicted in the window, **Warning** at a conflict rate of 5%
or more, over at least 1,000 guarded writes. The rate is `1 - rows / calls`, summed over every guarded statement of
one table: Marten writes a document whose type uses `UseOptimisticConcurrency(true)` (or numeric revisions) with
`insert into ... on conflict (...) do update set ... where {table}.mt_version = $n returning mt_version`, or
`update ... where id = $n and mt_version = $m returning mt_version`, and checks a rich append's expected version with
`update {schema}.mt_streams set version = $1 where id = $2 and version = $3 ... returning version`. Each returns its
row when the version matched and none when somebody else wrote first, so the calls that returned nothing are the
conflicts - each a `ConcurrencyException`, or an `EventStreamUnexpectedMaxEventIdException`, thrown from
`SaveChangesAsync`. The shapes were read from Marten's sources at both ends, 9.39.0 and 9.50.2, where the
closed-shape write path that builds them is the only one and is unchanged.

A statement is matched to its table by the table it writes, which must be one of the store's visible tables.
A quick append's conflict is a unique violation that fails its statement, and `pg_stat_statements` never counts a
statement that failed - so quick appends are not counted here; E02 counts the tombstones a failed rich append leaves,
and C08 the rollbacks. A projected document that takes its version from its stream
(`UseVersionFromMatchingStream`) is guarded on the stream's version rather than on `mt_version`, and is not counted.

The remedy is modelling, not configuration: keep aggregates small, so fewer commands touch the same one; serialise
the writers of a hot one rather than letting them collide and retry, which locks the stream's `mt_streams` row until
the session commits; and give a long-lived, busy aggregate a stream per period (a day, a month) rather than one
stream for ever.

```csharp
var stream = await session.Events.FetchForExclusiveWriting<Order>(orderId);
```

## T23 JIT compilation share

**Warning** when JIT compilation takes more than 20% of a store statement's execution time, over 100 calls or more
in the window. PostgreSQL compiles a statement to machine code when its estimated cost passes `jit_above_cost`, and
compiles it again on every execution. That pays off for a long analytical query and costs a short one more than it
saves; Marten's statements are short, and a large table's cost estimates push them over the line. JIT time is what
`pg_stat_statements` records from extension 1.10 (PostgreSQL 15) - generation, inlining, optimisation and emission,
and from 17 deforming - so below 1.10 the check says it has nothing to judge. One finding for the database, naming
the three statements that spend the most time compiling.

```csharp
opts.ConfigureNpgsqlDataSourceBuilder(b =>
    b.ConnectionStringBuilder.Options = ((b.ConnectionStringBuilder.Options ?? string.Empty) + " -c jit=off").Trim());
```

That turns JIT off for the store's own connections - every data source Marten builds, tenant databases included -
and nobody else's (`ConfigureNpgsqlDataSourceBuilder` is on `StoreOptions` at Marten 9.39.0 and 9.50.2, and
`NpgsqlConnectionStringBuilder.Options` passes `-c` settings to each connection at Npgsql 9.0.4). A data source the
host builds itself (`UseNpgsqlDataSource`) takes the same `Options` on its own builder. To keep JIT for the occasional
long query, a DBA can raise `jit_above_cost` for the store's role instead.
