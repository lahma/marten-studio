# Health: tables and indexes (T)

These checks answer: **why is the app slow, and what is going to bite us?** They read the catalog and the
statistics views (`pg_class`, `pg_index`, `pg_inherits`, `pg_depend`, `pg_stats`, `pg_stat_user_tables`,
`pg_stat_user_indexes`, `pg_stat_progress_create_index`) - tier 1, at every sweep - except T09, which reads a bounded sample of the data behind
**Run deeper checks**. Back to the [index](../health.md).

What every check here has in common:

- **A "Marten table" is one the store's configuration declares**: a document type's table, the event store's
  tables, a projection's or an `ExtendedSchemaObjects` table, and `mt_hilo`. The list comes from the
  configuration alone, never from `AllObjects()` or `AllSchemaNames()` (which migrate), and a document type
  `IsDocumentTypeVisible` hides is left out of it - its table is not even a parameter of a statement.
- **One read, shared.** The ten regular checks share one read of the tables and one of their indexes per
  sweep, so the whole area costs a regular sweep five catalog statements on the sample store - those two, T10's
  `pg_stats` read and T11's two.
- **A list locks nothing it lists.** Sizes are `relpages` estimates, never `pg_relation_size`, which opens the
  table and queues behind any migration's lock on it; partitions are walked through `pg_inherits`, never
  `pg_partition_tree()`, which holds a lock on every partition. A tenant-partitioned table is one row, its
  partitions' figures rolled in, and a partition is never named: Marten names a tenant's partition after the
  tenant. The only row of a user table a regular sweep reads is `mt_hilo`'s (T11), and only once the catalog
  says it exists.
- **Rates, not totals.** The `pg_stat_*` counters run from the last statistics reset, which may be a year ago,
  so T01 and T06 sample them into the in-memory history and judge the change over the window it holds, and say
  "watching" until two samples exist. A counter that goes down is a reset, and the window restarts there.
- **Held, not glimpsed.** A batch delete or a bulk load makes any table look bloated or unanalysed for the
  minute before autovacuum wakes up, so T07 and T08 speak only when the condition has held for ten minutes of
  samples - unless autovacuum is switched off for the table or the server, when there is nothing to wait for.
- **Remedies are `StoreOptions` lines.** `FillFactor(n)` and `StorageParameter(name, value)` need Marten 9.47
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
cost, and it grows with the table. Which statement it was is T02's to say (a later check); until then the remedy
is the index line, and the finding links to Query in Marten mode on the collection, whose index advice says
whether an index serves a where clause and which member it needs:

```csharp
opts.Schema.For<Order>().Index(x => x.Status);
// or, where it is also sorted on or compared as a column:
opts.Schema.For<Order>().Duplicate(x => x.Status);
```

The studio's own exact counts, `pg_dump` and a one-off report are sequential scans too.

**A partitioned table is judged by its partitions.** A tenant-partitioned table counts one scan per partition
read, and a per-tenant query that reads its own small partition whole is often the right plan. So the 100,000
line applies to the table's largest partition, and "an average scan read a good part of one scan's worth" is
measured against its mean partition, both rolled up from the catalog without opening a partition. Fifty tenants
of 4,000 rows, each query scanning its own tenant's partition, is no finding; a finding on a partitioned table is
worded "partition-wide".

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
(which takes no lock) beside `pg_index`: an index a build is working on, or a partitioned table one of whose
partitions is being indexed, is no finding - the check's note says "being built" and the phase. Postgres hides
the table of another role's build from a role without `pg_read_all_stats`; while any such build is running, an
invalid index is a **Warning** whose caveat says so, never a Critical.

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

The finding is only **Info** while "never scanned" is unproven: counters reset less than a day ago; counters that
were never reset, whose age is unknown; and a multi-host data source, whose reads may go to a standby that counts
its own index scans - the primary's zero says nothing about them. The advisor's own samples are the proof it can
offer: once they have seen the index unscanned, without a break, for a day, the finding is a Warning.

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

## T12 Transaction-id age

**Warning** 40% of the way from the server's `autovacuum_freeze_max_age` to wraparound, **Critical** two-thirds
of the way - about 1.0 and 1.5 billion at the default of 200 million. The oldest `age(relfrozenxid)` among the
store's tables, their TOAST tables and their partitions.

Postgres must freeze every row before its 32-bit transaction counter wraps, and refuses writes to the whole
database at about 2.1 billion. Autovacuum forces a freeze at `autovacuum_freeze_max_age`, so a table this far
past it is one autovacuum has not managed to freeze - usually a transaction, a replication slot or a prepared
transaction holding the horizon back. The lines are measured from the server's own setting, read with the
tables, because a DBA who raised it to a billion and a half has a server on which a table at 1.2 billion has not
yet been asked to freeze. One finding for the database, naming the oldest tables and both lines. The cure is a
DBA's `VACUUM (FREEZE)`, once whatever holds the horizon is gone.
