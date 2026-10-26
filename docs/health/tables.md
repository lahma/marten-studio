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
  migration of the store's or of any library's: T03-T06 then judge every other table without waiting, leave that one
  out for the sweep and say how many they left out ("1 table was locked for a schema change and not judged this
  sweep"), and read its indexes again at the next; T15 and T25, which judge by the same read, do the same. T02's index
  advice skips such a table too. A
  lock taken in the instant between that look and a call still waits out `lock_timeout`; the read is then asked once
  more, and leaves the table out the same way - and should that wait too, the index checks say they waited too long for
  a lock, as any check does. Nothing is held past a call.
- **Rates, not totals.** The `pg_stat_*` counters run from the last statistics reset, which may be a year ago,
  so T01 and T06 sample them into the in-memory history and judge the change over the window it holds, and say
  "watching" until two samples exist. A counter that goes down is a reset, and the window restarts there.
- **No usage verdict over young or reset counters** (0.5, issue #8 G4). T01, T05 and T06 give no finding while the
  statistics have counted for less than seven days or look reset, and their rows say why.
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

**Never over a short or reset window** (0.5, issue #8 G4). While the statistics have counted for less than seven days, or
look reset - a crash, a restore, a pre-18 `pg_upgrade`, a failover - T01 makes no finding, and its row says why:
"Withheld: statistics 3 days old (since ..., basis ...)" or "Withheld: the counters look reset (...)". The window is the
[statistics window](connections.md#the-statistics-window) C08 and C11 read, and, while this database's own `stats_reset` is null (PostgreSQL 15
and later, until somebody resets it), no older than the oldest moment its statistics record - so a database created after
the server's statistics began is not taken for one as old as they are.

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

**Warning** (Info on a multi-host data source). A valid, non-unique index of at least 1 MiB that nothing has scanned
since the counters began - or, on PostgreSQL 16 and later, whose `last_idx_scan` is more than 30 days ago - on a table
that has taken at least 100,000 inserts, updates and deletes in that time.

Every insert, and every update that is not HOT, writes an entry into every index. The finding states the size and the
write volume, the last scan's date on 16+ (`last_idx_scan`, set at the statistics flush after a scan and readable by
every role), and how long the counters have counted. Unique indexes, primary keys, exclusion constraints, Marten's own
structural indexes and the event store's are never reported.

**Never over a short or reset window** (0.5, issue #8 G4). `pg_stat_reset()`, a crash, a restore, a pre-18 `pg_upgrade`
and a failover zero `idx_scan`, and an index used by a weekly job looks unused for most of the week. So while the
statistics have counted for less than seven days, or look reset, there is no finding at all, and the row says why
("Withheld: statistics 3 days old (since ..., basis ...)", "Withheld: the counters look reset (...)"). The window is the
one T01 reads, narrowed the same way for a database newer than the server's statistics; on PostgreSQL 19 an index whose
own counters were reset less than seven days ago is not judged either, and counted. **What changed in 0.5:** 0.4 made
the finding Info while the counters were under a day old or had never been reset, and a Warning once the advisor's own
samples had seen the index unscanned for a day (`MinCounterAge`). That is folded into the guard: the finding is a
Warning over a covering window or nothing, and T05 keeps no samples of its own - so it is never Info for its first day,
and on PostgreSQL 15 or later a long-running database that was never reset, whose statistics record moments weeks old,
can be a Warning from the first sweep. After a reset, a restore or a failover, T01, T05 and T06 say nothing for 7 days.

On a **multi-host data source** the finding is Info always. Its reads may go to a standby, which counts its own
index scans, and nothing read on the primary says anything about a standby's. The finding says to check
`pg_stat_user_indexes` on every standby before removing the index.

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

**Never over a short or reset window** (0.5, issue #8 G4): as T01, no finding while the statistics have counted for less
than seven days or look reset, and the row says why.

## Autovacuum's own triggers (T07, T08, T16, T24)

Since 0.5, T07 and T08 judge each table the way autovacuum decides about it (issue #8, G2), and the
[Maintenance page](#the-maintenance-page) draws the same numbers. Autovacuum acts on a table when a counter is
**strictly past** its trigger:

| Decision | Counter | Trigger |
|---|---|---|
| vacuum | `n_dead_tup` | `autovacuum_vacuum_threshold + autovacuum_vacuum_scale_factor × reltuples`, capped on PostgreSQL 18 by `autovacuum_vacuum_max_threshold` |
| vacuum (inserts, 13+) | `n_ins_since_vacuum` | `autovacuum_vacuum_insert_threshold + autovacuum_vacuum_insert_scale_factor × reltuples`, and on 18 also × the share of pages not all-frozen (`1 - relallfrozen / relpages`); a threshold of `-1` switches it off |
| analyze | `n_mod_since_analyze` | `autovacuum_analyze_threshold + autovacuum_analyze_scale_factor × reltuples` |

Each threshold and scale factor is the table's own storage parameter where it has one and the server's setting
where it does not; `reltuples = -1` (never vacuumed or analysed) counts as 0. Both formulas were proved against
autovacuum itself on 17 and 18, with the comparison strict: the same 3,500 appends to an append-only table
trigger autovacuum on 18, whose unfrozen share brings its insert trigger down to 2,577, and not on 17, where it
is 7,494. `relallfrozen` and the cap are read through `to_jsonb` and `pg_settings`, so one lock-free statement
runs on 14 to 19. A partitioned table is judged by its partitions, as autovacuum judges them: each decision is
the partition's whose counter is furthest past twice its own trigger, never named, and the counts say how many
partitions are past theirs.

**Counters read just after a manual `VACUUM` can be inflated.** A backend's pending counts reach the statistics
after its `VACUUM` reset them (PostgreSQL 15 and later flush at most once a second, when the backend is idle), so a
table vacuumed by hand a moment ago can show its inserts or dead rows again. Every finding's caveat says so.

## T07 Vacuum falling behind

**Warning; Critical** past ten times the trigger, or with autovacuum switched off by the table's own
`autovacuum_enabled = false`. A server whose `autovacuum = off` keeps each table behind at Warning: S02 is that
Critical. Behind means a counter at least **10,000 past twice its trigger**, the last vacuum of either
kind more than **an hour** ago or never, and the table **not being vacuumed now** - held for ten minutes of
samples, unless autovacuum is switched off for the table or the server, when there is nothing to wait for. The
fixed 20% dead and one-day rules of 0.4 are gone: a table tuned to `scale_factor 0.01` is caught long before it
is a fifth dead. Studios: both.

Variants:

- `dead-rows` - `n_dead_tup` against the dead-row trigger;
- `inserts` - `n_ins_since_vacuum` against the insert trigger, which an append-only table such as `mt_events` or
  a library's history table depends on: it is the pass that sets the visibility map index-only scans read and
  freezes pages early, so the anti-wraparound vacuum that comes for every table later is cheap.

A finding's key is `T07|<variant>|<table>` (0.4's `T07|<table>` keys, and their mutes, reset once). A table behind
on both is one finding, the `dead-rows` one, with both counters and both triggers in its evidence - so a finding
that began as `inserts` takes the `dead-rows` key once the dead rows have been behind for their own ten minutes: its
key, and a mute of it, change mid-incident. That is deliberate (one table, one thing to do), and a mute of the
`inserts` key does not hide the `dead-rows` finding.

**"Not being vacuumed now"** is `pg_stat_progress_vacuum.relid`. A role without `pg_read_all_stats` sees only the
process id and database of a `VACUUM` it did not run - autovacuum's on its own tables included - so while such a
one runs in the database, the finding still speaks, and its caveat says it may be under way now and which grant
would tell (`pg_read_all_stats`, and who can grant it on this platform).

**The remedy is for what keeps autovacuum away.** A table behind is already past its trigger, so a lower scale
factor changes nothing; the finding's evidence ("What keeps autovacuum away") and its remedy name, from what the
sweep read, the first of:

- **a lock autovacuum gives way to** - one a session or a prepared transaction holds or waits for, in a mode
  autovacuum's `SHARE UPDATE EXCLUSIVE` conflicts with (a long migration, a `LOCK TABLE`, a `CREATE INDEX` without
  `CONCURRENTLY`, an orphaned two-phase commit, whose locks have no process). Autovacuum takes its lock only if it can
  have it at once, so it passes the table by while such a lock is held and while a stronger request waits in the
  queue. No setting answers it; Database › Sessions shows the sessions, and C07 a prepared transaction;
- **every worker busy** (T16, now or in its latest sample) - `autovacuum_max_workers`, doubled. A role without
  `pg_read_all_stats` counts maintenance processes rather than workers, as T16 does: the finding says "at least as
  many maintenance processes as workers, manual runs included" and offers no setting, since the count may be of
  manual VACUUMs;
- **throttling** - an `autovacuum_vacuum_cost_delay` of 10 ms or more, or an `autovacuum_vacuum_cost_limit` of 100
  or less, the table's own storage parameter or the server's setting - back to Postgres' default (2 ms, 200);
- otherwise nothing the sweep can see, and the finding says so and points at `log_autovacuum_min_duration`.

A horizon held back is not on the list: under one autovacuum keeps running and dating its `last_autovacuum`, so the
table is not behind by T07's rule - its dead rows stay because no vacuum may remove them, which T14 says.

A scale factor is offered only when nothing above applies and the next rung below the one in force (0.2, 0.1,
0.05, 0.02, 0.01, 0.005, 0.002, 0.001) would at least halve the trigger - "so autovacuum starts sooner next time",
which is all it can do for a table already past its trigger. For a document table:

```csharp
opts.Schema.For<Order>().StorageParameter("autovacuum_vacuum_scale_factor", 0.02);   // Marten 9.47+
```

For an `inserts` finding it is `autovacuum_vacuum_insert_scale_factor`; a host's own relational table gets the
`ALTER TABLE ... SET (...)` for its migrations. On a table autovacuum is switched off for, the remedy is
`StorageParameter("autovacuum_enabled", true)`; with the server's `autovacuum = off` it is a DBA's (S02). A DBA's
`ALTER TABLE ... SET (...)` on a table is not reset by an apply unless the apply rebuilds the table, as a changed
partition scheme does.

## T08 Never analysed, or stale statistics

**Warning** (Info for the two cases below), held for ten minutes of samples unless autovacuum is off. Studios:
both. Variants:

- `stale` - `n_mod_since_analyze` at least **10,000 past twice the table's own analyze trigger**, with no
  analyze of either kind in **an hour**. As T07's, the remedy is for what keeps autoanalyze away - a lock, every
  worker busy, the table's own throttling - and the next rung of `autovacuum_analyze_scale_factor` only where it
  would at least halve the trigger; a DBA's `ANALYZE` fixes the statistics now.
- `never-analysed` - `reltuples = -1` and no analyze of either kind, with at least **1,000 live rows - or 1,000
  rows read since the counters began** (`seq_tup_read + idx_tup_fetch`) **by sequential scans that read 1,000 rows
  on average** - the one sign rows read give of a table's size. Rows read alone are not one: a table whose indexes
  were built while it was empty keeps `reltuples = -1` - every Marten document table - and five rows read two
  thousand times by their key are two thousand rows read. That is also why no live rows counted is not a sign on its
  own: it is the shape every table has after `pg_upgrade` from 14-17, which carries no statistics over, but also
  after a crash, a failover or `pg_stat_reset()`, when a small table read by its key would be named for nothing. A
  table that is scanned is caught by its scans; the reset itself is `since-restart`'s. Autovacuum analyses a table
  only after enough modifications, so a read-mostly table with no statistics is planned on defaults indefinitely.
  The remedy is in words, for whoever runs the server: `vacuumdb --all --analyze-in-stages` (17 and before), or
  on 18 `vacuumdb --all --analyze-in-stages --missing-stats-only` followed by `vacuumdb --all --analyze-only` -
  or the table's own `ANALYZE`. A partitioned table is flagged for its partitions that were never analysed and
  are, each on its own, past the same lines - never a sum, so many small tenants are not one large table - and -
  as **Info**, with nothing to wait for - when the parent itself never was: autovacuum does not analyse partitioned
  tables, so the parent's statistics, which a query across partitions plans from, exist only once somebody runs
  `ANALYZE` on it.
- `since-restart` - one database-level **Info** while the statistics window says autovacuum's counters began
  again (a crash, a point-in-time recovery, a failover, `pg_upgrade`, a reset) less than a day ago - the day the
  checks that judge rates per day wait for - and the tables of at least 10,000 estimated rows that have not been
  analysed since hold at least half of the judged tables' estimated rows. It
  names the largest few, says that rows that died before are invisible to autovacuum - and to T07 - until each
  table crosses its trigger again, clears itself once they are analysed, and points at `vacuumdb --all
  --analyze-only`, the manual's own post-upgrade step. It is shown even while other checks withhold verdicts that
  depend on the counters, because it is what explains their quiet.

The planner chooses every plan from these statistics, and the studio's `~` counts are `reltuples`: both are wrong
until the table is analysed. A partitioned table is otherwise judged by its partitions' own statistics.

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
| A standby's `hot_standby_feedback` | `pg_stat_replication.backend_xmin` - only for a standby that streams without a slot | every database |

A standby that streams through a replication slot sends its feedback to the **slot's `xmin`**, and its own row in
`pg_stat_replication` then shows no `backend_xmin` - for `pg_monitor` too (measured by B0). So T14 reads it from the
slot, and words an active physical slot as "a standby, through a physical replication slot", with the slot's name in
the gated evidence.

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
  in another statement, so its age is never matched; its raw `xmin` is (`xmin::text`, 0.5): a slot whose `xmin` or
  `catalog_xmin` is exactly a writer's own `backend_xid` is behind that writer, which is the one named.
- **Honest time beyond the ring** (0.5). Every holder with an identity of its own - a writer's process id, backend
  start and transaction id; a slot's name and `xmin`; a standby's WAL-sender process id and `xmin` - is remembered
  in a per-database first-seen map that outlives the 60-sample ring, at T14's gate and never shown (only a hash of
  the identity is kept). A hold is then at least as old as the first sweep that saw that very identity, worded "at
  least 3 h (the advisor has seen it hold this horizon, unchanged, that long)": the bound a writer whose timings are
  withheld, or a slot or a standby with no `inactive_since`, could not have before. A slot or a standby is dated so only
  once it holds at least a thousand transactions back - one whose `xmin` stays put because nothing hands out ids holds
  nothing back, however long. The map is bounded (512 holders per database, the one seen longest ago going first)
  and goes with the database's history; a holder no sweep in the ring's window saw is forgotten.
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

## T16 Autovacuum workers saturated

**Warning** when every autovacuum worker on the server has been busy in **three samples running** - so a table that
crosses its trigger now waits for one to come free, however far past it it goes (T07). Studios: both. The server's
`autovacuum = off` is S02's, and T16 then says only that no worker runs.

How busy they are is counted as the reading role can count them:

- **With `pg_read_all_stats`** (or `pg_monitor`): the rows of `pg_stat_activity` with `backend_type =
  'autovacuum worker'`, server-wide, against `autovacuum_max_workers` - held on PostgreSQL 18 to
  `autovacuum_worker_slots`.
- **Without it**, a role is shown nothing of autovacuum's processes but their process ids in the progress views -
  even on its own tables - so the count is every process running a `VACUUM` or an `ANALYZE` on the server
  (`pg_stat_progress_vacuum` and `pg_stat_progress_analyze`, whose `datid` every role sees). That is at least the busy
  workers, manual `VACUUM`s included, and the finding says so.

The remedy is `autovacuum_max_workers` - twice the workers - through the platform's own place for a server setting.
Before PostgreSQL 18 it needs a restart; on 18 it changes with a reload, but only up to `autovacuum_worker_slots`,
which itself needs a restart. More workers share the same `autovacuum_vacuum_cost_limit`, so raising the count alone
spreads the same I/O budget thinner: raise the cost limit with it where the hardware allows.

T16 also keeps the history the Maintenance page dates progress by: each running command's blocks done, sampled by
process id and relation number - numbers only, never a name.

## T24 Autovacuum switched off for a table

**Warning; Critical** once the table's oldest transaction-id age - its own, its TOAST table's and its partitions'
`age(relfrozenxid)`, the age T12 reads - is past half of `autovacuum_freeze_max_age`. Studios: both.

A table, its TOAST table (`toast.autovacuum_enabled`) or some of its partitions have the storage parameter
`autovacuum_enabled = false`: autovacuum leaves it alone - dead rows are never cleared, statistics never refreshed,
the visibility map never set - and the only vacuum that still comes is the forced anti-wraparound one, which scans the
whole table at once and cannot be cancelled. A table switched off for a bulk load is a common one to forget to switch
back. Partitions are counted, never named.

The remedy switches it back on: for a document table `opts.Schema.For<Order>().StorageParameter("autovacuum_enabled",
true);` (Marten 9.47+), and for a host's own relational table `ALTER TABLE ... SET (autovacuum_enabled = true)` for its
migrations. A TOAST table's setting has no `StoreOptions` line - Marten's `StorageParameter` does not manage `toast.*`
parameters - so for a Marten table it is a DBA's `ALTER TABLE ... RESET (toast.autovacuum_enabled)`, and for a host's
own table that statement for its migrations.

## The Maintenance page

**Operations › Maintenance** (both studios) shows what autovacuum makes of each table, and reads nothing the health
sweep did not: its facts are the regular sweep's own reads, once per `HealthInterval` for everybody watching. It is
shown to a visitor the store policy passes for the database as a whole (`TenantId = null`).

- **Triggers** - every judged table, worst first: dead rows against the vacuum trigger, inserts against the insert
  trigger, changes against the analyze trigger, as bars of each counter against twice its trigger; the last vacuum and
  analyze of each kind; the table's own `autovacuum_*` and `toast.*` overrides; badges for *due*, *behind* and
  *autovacuum off*; and on PostgreSQL 18 the time spent vacuuming and analysing (`total_*vacuum_time`). A partitioned
  table shows its partition furthest behind. A table in a schema outside the source's own is listed only past the
  database browser's gate for that schema, and counted otherwise; a hidden document type's table is never in the set.
- **Running now** - with `pg_read_all_stats`, every `VACUUM`, `ANALYZE`, index build and `CLUSTER` in this database:
  its phase, blocks and (PostgreSQL 17+) indexes, whether it is autovacuum's and why, and an ETA at the rate its blocks
  went between T16's samples. Without it, the commands the role cannot see the table of are counted, with the grant
  that would show them. A command on a table the page does not name to this visitor - withheld, hidden, or not one it
  judges - is counted too, with no phase, blocks or ETA, which would say how big the table is.
- **Workers** - `autovacuum_max_workers`, how many are busy (as T16 counts them), `autovacuum_naptime`.
- **Freeze forecast** - T12's and T13's own facts and samples: the oldest ages against the forced-freeze and
  write-refusal lines, and the time to each at the history's rate.
- **Vacuum horizon** - T14's finding, its names behind the activity gate as on the Health page.
- **Measure bloat** - one table at a time, on demand: `pgstattuple_approx` for the heap (the exact `pgstattuple`
  where the installed extension is older than 1.3) and `pgstatindex`, `pgstatginindex` or `pgstathashindex` for the
  five largest indexes, the rest counted. The heap is measured in a statement of its own, then the indexes smallest
  first, each under a savepoint and what is left of `QueryTimeout`, so an index that runs out of time costs only
  itself: the heap's figures and the indexes measured so far stand, and the result says how many were - in a
  read-only transaction as `SqlConsoleRole` under `lock_timeout`, one measure per database at once. It needs the
  `pgstattuple` extension (not trusted: a superuser or the platform's administrator installs it) and `pg_stat_scan_tables` (in `pg_monitor`); without either it says which,
  and who can grant or install it. It is never part of a sweep or a list - it reads the table, under an
  `AccessShareLock` - and every press is audited as "Measure bloat", with the table and nothing else.

## T15 Foreign key without a covering index

**Warning** (variant `uncovered`) or **Info** (variant `partly-covered`). Studios: both. Gate: Database. A foreign key on a
judged table of 8 MiB or more (its heap, from `relpages`) that no index covers:

- `uncovered` - no usable index leads with any of the key's columns: the lookup reads the whole table.
- `partly-covered` - an index leads with some of them: the lookup reads the rows matching those and filters the rest,
  which is narrower than the whole table and wider than an index on all of them. The evidence names the index.

A column the table's own source adds to every key need not lead: a conjoined Marten document table's `ForeignKey<TRef>` to
another conjoined type keys `(member, tenant_id)` and indexes the member alone (`TenancyScope.Global` by default), and that
index covers it. A key into a document table no store shows the studio - a hidden type's, an orphan's - is not
described at all, as the database browser drops it.

Postgres creates an index for a primary key and a unique constraint, never for a foreign key. So every delete from the
referenced table - and every update of its key - looks up the rows that point at it, and with no index that lookup reads
the whole referencing table, once per row deleted; a join from the referenced table on the key does too. The evidence
names the key, its columns, the referenced table, the size, and how many deletes the referenced table has taken since
the counters began.

**Covered** (B0 §9's rule, the same pure function the database browser's Keys tab uses): a valid, non-partial b-tree - or
a one-column hash - whose leading key columns are exactly the key's, in any order. An index on `(b, a)` covers a key on
`(a, b)`; one on `(a)` alone does not; a partial index does not; an expression among the leading columns does not. A
partitioned table's key is judged on the table itself (never a partition's clone, `conparentid = 0`) against its
partitioned indexes, and where every partition-key column is one of the key's, those are left to partition pruning: the
key needs an index only on the rest.

Catalog only: the keys and the indexes come from `pg_constraint` and `pg_index` as attribute numbers, never deparsed. A
table locked for a schema change is left out for the sweep and counted, as every index check does. A library's table is
exempt (index design is the library's). The remedy is `CREATE INDEX CONCURRENTLY ON ... (columns)` for the host's own
table (`CREATE INDEX` on a partitioned one, where `CONCURRENTLY` is not allowed - or `ON ONLY` and each partition's);
for a Marten document table whose key is on a duplicated field, `opts.Schema.For<T>().Duplicate(x => x.Member)` (with
`notNull: true` where the field is declared NOT NULL, which a bare `Duplicate` would undo), which declares the column's
index as `ForeignKey<TReference>(...)` does - or, for a conjoined key `ForeignKey<TRef>` made, that declaration with
`indexConfiguration: i => i.TenancyScope = TenancyScope.PerTenant`, which indexes `(member, tenant_id)`; and for a table
Marten creates itself, nothing - its
next apply drops an index nobody declares. Marten's DCB tag tables (`mt_event_tag_<suffix>`) are the case in point: their
primary key leads with `value`, so a delete of an event reads every tag table whole. marten#5694 declares
`idx_mt_event_tag_<suffix>_seq_id` from Marten 9.51.0: on 9.51 or later an apply builds it, and the tag table is covered;
below, it arrives with the upgrade.

## T17 Orphaned document tables

**Info.** Tables in the store's own schemas named like Marten document tables (`mt_doc_*`) that no store in this process
declares, counted, with their estimated size together (heap, TOAST and indexes, from `relpages`). Studios: Marten Studio.

Marten creates a document type's table and never drops it: a type renamed, moved to another schema or removed leaves its
old table on disk and in every backup. **Never named**, because a document type Marten has not loaded yet - one never
registered with `Schema.For<T>()` that nothing in the process has touched - is unknown to `AllKnownDocumentTypes()` and
indistinguishable here from an orphan, and may be a type the host hides; the finding says so ("or a document type this
process has not loaded yet"). A hidden type the process knows is declared and never counted. Every registered store's
declarations are compared, read with no connection; when one cannot be read, the check says it cannot tell and counts
nothing.

## T19 Partitions against the lock table

**Info** (variant `per-transaction`) or **Warning** (variant `capacity`). Studios: both. For each partitioned table:

`locks = 1 + intermediate partitioned tables + Σ leaf partitions (1 + the indexes on it)`, plus its partitioned indexes
from PostgreSQL 16 - what one query that is not pruned takes, and a generic plan of a query that filters on the partition
key too: B0 measured 601 locks for 200 partitions of two indexes each (603 from 16) on every release, a generic plan of a
tenant-scoped prepared statement included. Only a custom plan prunes at plan time.

- `per-transaction` - Info when `locks` passes `max_locks_per_transaction`: one such query takes more than the average
  the lock table is sized for per connection.
- `capacity` - Warning when `locks` passes half the documented capacity, `max_locks_per_transaction × (max_connections +
  max_prepared_transactions)`: two such queries, or one beside ordinary traffic, can fill the shared lock table, and
  every session then fails with "out of shared memory".

Up to 18 the documented capacity is a floor - one backend measured about twice it (B0 §8b) - and the evidence says so;
from PostgreSQL 19 the lock table keeps no such margin and `max_locks_per_transaction` defaults to 128, so a value carried
over from an older release holds about half the locks it did. The evidence shows the formula with its numbers and the
fast-path slots a backend has (16 up to 17; from 18 the smallest power of two at or above `max_locks_per_transaction`,
at least 16 - 64 at its default, 128 at 100). The
remedy is `max_locks_per_transaction`, sized from the formula (the smallest power of two that holds one such query
within the line, and at least double the setting in force), which takes effect at the server's restart. Counted from the
catalog, never by locking anything.

## T20 Range partitions running out

**Critical** or **Warning**. Studios: both. For a table partitioned by range on one `date`, `timestamp` or `timestamptz`
column, judged by the newest partition's upper bound against now and the newest partition's own width:

- `running-out` - **Critical** when there is no `DEFAULT` partition and the bound has passed or is within one width of
  now: inserts for the next period then fail ("no partition of relation found for row"). **Warning** when it is ahead by
  fewer than two widths, or - with a `DEFAULT` partition, which catches the rows - when it has passed. A `MAXVALUE` bound
  never runs out. A Marten document table on `ByRollingRange(...)` (Marten Studio) is warned of only within one width:
  Marten provisions its periods ahead and always gives it a `DEFAULT` partition, and the recommended `periodsAhead: 1`
  leaves the newest bound more than one width ahead whenever the roll-forward has run - so a finding means the
  roll-forward has fallen behind.
- `default-partition-rows` - **Warning** when the `DEFAULT` partition holds rows (`reltuples`, or `n_live_tup` where
  higher): rows no range took, read whole by every query for them, and blocking the creation of the range they belong to
  until they are moved out. A range on an integer key is judged on this alone.

Read without a lock: the hierarchy through `pg_inherits` (never `pg_partition_tree`), the key from
`pg_partitioned_table`, and each bound through `pg_get_expr(relpartbound, 0)`. `pg_get_expr(relpartbound, relid)` opens
the partition and queues behind a migration's `ACCESS EXCLUSIVE`, and `pg_get_partkeydef` behind one on the parent; with
no relation, a bound - which holds no column reference - is deparsed without opening anything (measured on 14.24 and
18.6, 2026-10-26). A `timestamp` key is read as UTC; a `timestamptz` range's month boundaries are looked for in the
session's `TimeZone` (read with the bounds, lock-free), so a range cut at local midnights is still months - and where that
zone is one the studio's host does not know, the caveat says the months were looked for in UTC. The remedy is the next
period's `CREATE TABLE ... PARTITION OF ... FOR VALUES FROM (...) TO (...)` for the host's own table - a calendar month (or
several) where the newest partition spans whole months - and, when the newest bound passed more than a period ago, it
says to create each period up to now as well. For a Marten document table on `ByRollingRange(...)` it is `await
store.Advanced.RollPartitionsForwardAsync(CancellationToken.None)` on a cadence shorter than one period (present at
9.39.0 and 9.54.1); on `ByExternallyManagedRangePartitions()` the same `CREATE TABLE`, since Marten's apply neither creates
nor drops those partitions; a static `ByRange()` is a configuration line the finding describes.

## T25 Bloated index

**Warning** (variant `issue-rule`) or **Info** (variant `key-width`). Studios: both. A b-tree index of 64 MiB or more that
is mostly empty space, by a lock-free estimate from the catalog:

- `issue-rule` - at least 10 times `reltuples × 64` bytes: the issue's own rule.
- `key-width` - at least 4 times what its entries take packed: each entry an 8-byte tuple header and its columns - the
  keys and any `INCLUDE` columns, which a leaf entry stores too - aligned to 8 bytes, plus a 4-byte line pointer, at the
  index's fillfactor (90 by default). A fixed-width column counts its `attlen`, a
  variable-width one its `pg_stats.avg_width` where the reading role may read it and 32 bytes where not. The issue's rule
  misses narrow keys: a 95%-empty `int4` primary key measures 7.1 times its rule and 20 times this one.

Calibrated on 17: a dense `int4`, `int8` or `(int4, int4)` primary key of 200,000 rows measured 22.6 bytes an entry
against 22.2 expected; random `uuid` keys 43.7 against 31.1 and random 36-character text keys 79.9 against 48.9 (page
splits leave leaves part-full, which the factor of 4 allows for); a duplicate-heavy index deduplicates and reads small.
Both are estimates - `relpages` and `reltuples` as of the last `VACUUM` or `ANALYZE` - and the finding says to measure
with `pgstatindex` (the Maintenance page's bloat measure) before rebuilding. A library's table is judged too: bloat is
not index design. The remedy is `REINDEX INDEX CONCURRENTLY` for the host's own table (and a library's); for a table a
Marten store declares there is no line - the finding says what a `REINDEX INDEX CONCURRENTLY` does, which keeps the name
and the definition, so the next apply sees no change.

## T26 Integer key running out

**Warning** at half the range, **Critical** at four fifths (T11's lines). Studios: both. A `smallint` or `integer` column
of a judged table - a library's included - that a sequence feeds (an identity column, a `serial`, or a default that calls
one, as `pg_depend` records it, never matched as text), or a `bigint` column fed by a `smallint` or `integer` sequence or
by one whose `MAXVALUE` is set below `bigint`'s:

- `column` - the column's own range is the limit;
- `sequence` - the sequence's `seqmax` is narrower than the column's (a `bigint` column on an `AS integer` sequence, or a
  sequence capped by its own `MAXVALUE`).

The estimate never reads the sequence, whose value is read only under a lock (B0 §3): it is the greater of the table's
`reltuples` past the sequence's start - for a column a one-column unique index holds, whose values are distinct - and the
largest value the last `ANALYZE` sampled (`pg_stats`' last histogram bound or common value), which only a role with
`SELECT` on the column sees; without it the row count is the only floor, and the finding says which grant would add the
other. A cycling sequence, a descending one and one that starts below zero are not judged. A rolled-back insert and
`INSERT ... ON CONFLICT DO NOTHING` take a value and store nothing, so the sequence can be further on: the database
browser's Sequences list reads one sequence's value when asked, with the share of its column's range used and, from two
reads, how long it has left. The remedy is `ALTER TABLE ... ALTER COLUMN ... TYPE bigint` - which rewrites the table under
an `ACCESS EXCLUSIVE` lock - and `ALTER SEQUENCE ... AS bigint` where the sequence is narrower too (an identity column's
follows its column), for the host's own table. A `MAXVALUE` set below the type's survives both, so the remedy lifts it:
`ALTER TABLE ... ALTER COLUMN ... SET NO MAXVALUE` for an identity, `ALTER SEQUENCE ... [AS bigint] NO MAXVALUE`
otherwise (measured on 14 and 18). Marten declares no such column (`mt_events.seq_id` is `bigint`; HiLo is T11's).
