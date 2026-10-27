using System.Text.Json;

using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>One table the tables-and-indexes checks read: a schema and a name, both values, never SQL text.</summary>
/// <param name="Schema">The schema, as the store's configuration names it.</param>
/// <param name="Table">The table.</param>
internal sealed record HealthTableTarget(string Schema, string Table);

/// <summary>One table a plain data source's checks judge, as <see cref="HealthTableQueries.RelationalTables" /> listed it.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Table">The table.</param>
/// <param name="Kind"><c>relkind</c>: <c>r</c>, or <c>p</c> for a partitioned table.</param>
/// <param name="EstimatedPages"><c>relpages</c>, summed over the partitions of a partitioned table - an estimate.</param>
/// <param name="Total">How many tables the admitted schemas hold in all, the ones past the cap included.</param>
internal sealed record HealthRelationalTableRow(string Schema, string Table, string Kind, long EstimatedPages, int Total)
{
    /// <summary>
    /// How many of <see cref="HealthRelationalTableRow.Total" /> are in the source's own schemas (<c>@own</c>), past the cap
    /// included - what the coverage sentence counts for a visitor the database browser keeps out of every other schema
    /// (G1M F6, H1). <see langword="null" /> for a row built by hand, which counts the listed rows instead.
    /// </summary>
    public int? OwnTotal { get; init; }
}

/// <summary>
/// One Marten table as the catalog and the statistics collector see it, its partitions rolled in - what
/// <see cref="HealthTableQueries.Tables" /> reads.
/// </summary>
/// <remarks>
/// Init properties rather than a positional record, so a unit test states only the facts its case is about.
/// Every counter is <see langword="null" /> when the statistics collector has nothing for the table.
/// </remarks>
internal sealed record HealthTableRow
{
    /// <summary>The schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table.</summary>
    public required string Table { get; init; }

    /// <summary><c>relkind</c>: <c>r</c> for an ordinary table, <c>p</c> for a partitioned one.</summary>
    public string Kind { get; init; } = "r";

    /// <summary>Whether it is a partitioned table, whose partitions are rolled into this row.</summary>
    public bool IsPartitioned => Kind == "p";

    /// <summary>
    /// <c>reltuples</c>, summed over the leaf partitions of a partitioned table; <c>-1</c> before the first
    /// analyze (or, for a partitioned table, while none of its partitions has been analysed).
    /// </summary>
    public long EstimatedRows { get; init; } = -1;

    /// <summary>The main fork, from <c>relpages</c> - an estimate that <c>VACUUM</c> and <c>ANALYZE</c> refresh.</summary>
    public long HeapBytes { get; init; }

    /// <summary>The TOAST table's main fork, from its <c>relpages</c>.</summary>
    public long ToastBytes { get; init; }

    /// <summary>Every index on it, from their <c>relpages</c>.</summary>
    public long IndexBytes { get; init; }

    /// <summary><c>n_live_tup</c>.</summary>
    public long? LiveRows { get; init; }

    /// <summary><c>n_dead_tup</c>.</summary>
    public long? DeadRows { get; init; }

    /// <summary><c>seq_scan</c>: one per participant of a parallel scan, and one per partition scanned.</summary>
    public long? SequentialScans { get; init; }

    /// <summary><c>seq_tup_read</c>: the live rows sequential scans fetched.</summary>
    public long? SequentialRowsRead { get; init; }

    /// <summary><c>idx_scan</c>.</summary>
    public long? IndexScans { get; init; }

    /// <summary><c>n_tup_ins</c>.</summary>
    public long? Inserts { get; init; }

    /// <summary><c>n_tup_upd</c>, HOT updates included.</summary>
    public long? Updates { get; init; }

    /// <summary><c>n_tup_del</c>.</summary>
    public long? Deletes { get; init; }

    /// <summary><c>n_tup_hot_upd</c>: the updates that needed no index entry.</summary>
    public long? HotUpdates { get; init; }

    /// <summary><c>n_mod_since_analyze</c>.</summary>
    public long? ModifiedSinceAnalyze { get; init; }

    /// <summary><c>last_vacuum</c> (a manual one), the latest over the partitions.</summary>
    public DateTimeOffset? LastVacuum { get; init; }

    /// <summary><c>last_autovacuum</c>, the latest over the partitions.</summary>
    public DateTimeOffset? LastAutovacuum { get; init; }

    /// <summary><c>last_analyze</c> (a manual one), the latest over the partitions.</summary>
    public DateTimeOffset? LastAnalyze { get; init; }

    /// <summary><c>last_autoanalyze</c>, the latest over the partitions.</summary>
    public DateTimeOffset? LastAutoanalyze { get; init; }

    /// <summary>
    /// The oldest <c>age(relfrozenxid)</c> of the table, its TOAST table and every partition's - or
    /// <see langword="null" /> for a partitioned table with no partition yet. A partitioned parent's own
    /// <c>relfrozenxid</c> is never read: it is zero, and <c>age()</c> of zero is <c>int.MaxValue</c>.
    /// </summary>
    public long? OldestXidAge { get; init; }

    /// <summary>
    /// How many indexes on its partitions Postgres has marked invalid and no build is working on - see
    /// <see cref="BuildingPartitionIndexes" />. Never their names.
    /// </summary>
    public int InvalidPartitionIndexes { get; init; }

    /// <summary>
    /// How many invalid indexes on its partitions a build running right now is working on: its own index
    /// (<c>index_relid</c>), a <c>REINDEX CONCURRENTLY</c>'s <c>_ccnew</c> or <c>_ccold</c> copy of the index it
    /// reports, or a partitioned index a <c>CREATE INDEX</c> in the tree is completing.
    /// </summary>
    public int BuildingPartitionIndexes { get; init; }

    /// <summary><c>reloptions</c>: the storage parameters set on the table, as <c>name=value</c>.</summary>
    public IReadOnlyList<string> StorageOptions { get; init; } = [];

    /// <summary>When the database's statistics were last reset, or <see langword="null" /> for never.</summary>
    public DateTimeOffset? StatsReset { get; init; }

    /// <summary>The server's <c>autovacuum</c> setting.</summary>
    public bool AutovacuumEnabled { get; init; } = true;

    /// <summary>The server's <c>max_parallel_workers_per_gather</c>: how many participants one scan may count as.</summary>
    public int MaxParallelWorkersPerGather { get; init; } = 2;

    /// <summary>How many leaf partitions a partitioned table has; zero for an ordinary table. Never their names.</summary>
    public int PartitionLeaves { get; init; }

    /// <summary>The largest leaf partition's <c>n_live_tup</c>, or <see langword="null" /> for an ordinary table.</summary>
    public long? LargestLeafRows { get; init; }

    /// <summary>
    /// For a partitioned table, the storage parameters its leaf partitions do not all have at one value - by name
    /// only. <see cref="StorageOptions" /> then holds the ones they agree on, because Postgres keeps none on a
    /// partitioned parent.
    /// </summary>
    public IReadOnlyList<string> DifferingStorageOptions { get; init; } = [];

    /// <summary>
    /// The phase of a build working on one of <see cref="BuildingPartitionIndexes" />
    /// (<c>pg_stat_progress_create_index</c>), or <see langword="null" />.
    /// </summary>
    public string? PartitionBuildPhase { get; init; }

    /// <summary>
    /// The phase of any index build running right now on the table or one of its partitions, whatever index it
    /// is building - for the caveat that a re-apply would queue behind it - or <see langword="null" />.
    /// </summary>
    public string? TreeBuildPhase { get; init; }

    /// <summary>
    /// How many index builds in this database the reading role cannot see the table of - another role's, without
    /// <c>pg_read_all_stats</c>.
    /// </summary>
    public int UnseenIndexBuilds { get; init; }

    /// <summary>The server's <c>autovacuum_freeze_max_age</c>, where autovacuum forces a freeze.</summary>
    public long FreezeMaxAge { get; init; } = 200_000_000;

    /// <summary>The server's <c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; } = 170_000;

    /// <summary>
    /// <c>idx_tup_fetch</c>: the live rows index scans fetched - which, beside <see cref="SequentialRowsRead" />, says a
    /// table whose statistics never existed is read (T08's <c>never-analysed</c>, issue #8 G2).
    /// </summary>
    public long? IndexRowsFetched { get; init; }

    /// <summary>
    /// Autovacuum's dead-row decision (<c>n_dead_tup</c> against its trigger) for the table - or, for a partitioned table,
    /// for the partition furthest past its own - or <see langword="null" /> when the statistics collector has no row.
    /// </summary>
    public AutovacuumUnit? Vacuum { get; init; }

    /// <summary>
    /// Autovacuum's insert decision (<c>n_ins_since_vacuum</c> against its trigger), likewise - or <see langword="null" />
    /// also when the insert trigger is switched off (<c>autovacuum_vacuum_insert_threshold = -1</c>).
    /// </summary>
    public AutovacuumUnit? Insert { get; init; }

    /// <summary>Autovacuum's analyze decision (<c>n_mod_since_analyze</c> against its trigger), likewise.</summary>
    public AutovacuumUnit? Analyze { get; init; }

    /// <summary>
    /// <c>autovacuum_vacuum_max_threshold</c> as it applies to the table (its storage parameter, else the setting) on
    /// PostgreSQL 18 and later - the cap on the dead-row trigger - or <see langword="null" /> where it does not exist or is
    /// <c>-1</c>; for a partitioned table, the furthest-behind partition's.
    /// </summary>
    public double? VacuumMaxThreshold { get; init; }

    /// <summary>
    /// The share of the table's pages not all-frozen (<c>1 - relallfrozen / relpages</c>, PostgreSQL 18), which scales the
    /// insert trigger there; 1 before 18 and while nothing is frozen. The insert decision's own partition for a partitioned
    /// table.
    /// </summary>
    public double UnfrozenShare { get; init; } = 1;

    /// <summary>How many of its leaves - itself, for an ordinary table - have more dead rows than their vacuum trigger.</summary>
    public int PastVacuumTrigger { get; init; }

    /// <summary>How many of its leaves have more inserts since their last vacuum than their insert trigger.</summary>
    public int PastInsertTrigger { get; init; }

    /// <summary>How many of its leaves have more modifications since their last analyze than their analyze trigger.</summary>
    public int PastAnalyzeTrigger { get; init; }

    /// <summary>How many of its leaves - itself, for an ordinary table - have <c>autovacuum_enabled = false</c>. Never their names.</summary>
    public int AutovacuumOffLeaves { get; init; }

    /// <summary>How many of its leaves' TOAST tables have <c>autovacuum_enabled = false</c> (<c>toast.autovacuum_enabled</c>).</summary>
    public int ToastAutovacuumOffLeaves { get; init; }

    /// <summary>
    /// How many of its leaves have never been analysed (<c>reltuples &lt; 0</c>, no analyze of either kind) and are, each on
    /// its own, big enough for that to matter (<see cref="HealthTableQueries.UnanalyzedMinRows" />): never a sum over small
    /// partitions, so a store partitioned by many small tenants is not one big table never analysed (W5-V1 review).
    /// </summary>
    public int UnanalyzedLeaves { get; init; }

    /// <summary>The most live rows (<c>n_live_tup</c>) of any one of those leaves.</summary>
    public long UnanalyzedLiveRows { get; init; }

    /// <summary>The most rows read from any one of those leaves since the counters began (<c>seq_tup_read + idx_tup_fetch</c>).</summary>
    public long UnanalyzedRowsRead { get; init; }

    /// <summary>
    /// A partitioned table's own <c>reltuples</c> - <c>-1</c> until somebody runs <c>ANALYZE</c> on the parent itself,
    /// which autovacuum never does (B0 §8c) - or <see langword="null" /> for an ordinary table.
    /// </summary>
    public long? ParentEstimatedRows { get; init; }

    /// <summary>The TOAST table's storage parameters (<c>toast.autovacuum_enabled</c> and the rest), for an ordinary table.</summary>
    public IReadOnlyList<string> ToastStorageOptions { get; init; } = [];

    /// <summary><c>vacuum_count</c>, summed over the partitions.</summary>
    public long? VacuumCount { get; init; }

    /// <summary><c>autovacuum_count</c>, summed over the partitions.</summary>
    public long? AutovacuumCount { get; init; }

    /// <summary><c>analyze_count</c>, summed over the partitions.</summary>
    public long? AnalyzeCount { get; init; }

    /// <summary><c>autoanalyze_count</c>, summed over the partitions.</summary>
    public long? AutoanalyzeCount { get; init; }

    /// <summary>
    /// <c>total_vacuum_time</c> plus <c>total_autovacuum_time</c> in milliseconds, summed over the partitions (PostgreSQL 18),
    /// or <see langword="null" /> before 18.
    /// </summary>
    public double? TotalVacuumMilliseconds { get; init; }

    /// <summary><c>total_analyze_time</c> plus <c>total_autoanalyze_time</c>, likewise.</summary>
    public double? TotalAnalyzeMilliseconds { get; init; }

    /// <summary>The server's <c>track_counts</c>: without it autovacuum has nothing to decide by.</summary>
    public bool TrackCounts { get; init; } = true;

    /// <summary>
    /// How many <c>VACUUM</c>s running in this database the reading role cannot see the table of
    /// (<c>pg_stat_progress_vacuum.relid</c> null): every autovacuum, without <c>pg_read_all_stats</c> (B0 §7c), so "not
    /// being vacuumed now" cannot be told while there is one.
    /// </summary>
    public int UnseenVacuums { get; init; }

    /// <summary>The key the history and the findings name this table by.</summary>
    public string Key => Schema + "." + Table;
}

/// <summary>
/// One of autovacuum's three decisions about one table, as <c>relation_needs_vacanalyze</c> makes it (B0 §7a, proved live in
/// §7b): a counter against a trigger, compared strictly (<c>&gt;</c>).
/// </summary>
/// <remarks>
/// For a partitioned table it is the decision about the leaf partition whose counter is furthest past twice its own trigger
/// - autovacuum judges each partition alone and never the parent - and that partition is never named.
/// </remarks>
internal sealed record AutovacuumUnit
{
    /// <summary><c>n_dead_tup</c>, <c>n_ins_since_vacuum</c> or <c>n_mod_since_analyze</c>.</summary>
    public long Counter { get; init; }

    /// <summary>The trigger the counter must pass: <c>threshold + scale_factor × reltuples</c>, with 18's cap and unfrozen share.</summary>
    public double Trigger { get; init; }

    /// <summary>The base threshold in force: the table's storage parameter, else the server's setting.</summary>
    public double Threshold { get; init; }

    /// <summary>The scale factor in force, likewise.</summary>
    public double ScaleFactor { get; init; }

    /// <summary>The <c>reltuples</c> the trigger scales by - <c>-1</c> (never vacuumed or analysed) counted as 0.</summary>
    public double Tuples { get; init; }

    /// <summary>The unit's own latest vacuum of either kind (for the two vacuum decisions) or analyze (for the third).</summary>
    public DateTimeOffset? Last { get; init; }

    /// <summary>Whether <c>pg_stat_progress_vacuum</c> names the unit: a <c>VACUUM</c> of it is running now.</summary>
    public bool Running { get; init; }

    /// <summary>
    /// Whether another session - or a prepared transaction, whose locks have no process - holds or waits for a lock on the
    /// unit that autovacuum's <c>SHARE UPDATE EXCLUSIVE</c> conflicts with, a running VACUUM or ANALYZE apart: autovacuum
    /// takes its lock only if it can have it at once, so it passes the table by while one is held, and while a stronger
    /// request waits in the queue in front of it (W5-V1 review).
    /// </summary>
    public bool Locked { get; init; }

    /// <summary>Whether autovacuum would act now: the counter strictly past the trigger.</summary>
    public bool Due => Counter > Trigger;
}

/// <summary>One index on a Marten table - what <see cref="HealthTableQueries.Indexes" /> reads.</summary>
internal sealed record HealthIndexRow
{
    /// <summary>The schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table it is on.</summary>
    public required string Table { get; init; }

    /// <summary>The index.</summary>
    public required string Name { get; init; }

    /// <summary>The access method: <c>btree</c>, <c>gin</c>, <c>gist</c>, <c>hash</c>, <c>brin</c>...</summary>
    public string AccessMethod { get; init; } = "btree";

    /// <summary><c>pg_get_indexdef</c>: the statement Postgres reconstructs, read with <c>search_path</c> pinned.</summary>
    public string Definition { get; init; } = string.Empty;

    /// <summary>
    /// Each column's definition, key columns first and then <c>INCLUDE</c> ones -
    /// <c>pg_get_indexdef(oid, k, true)</c>: a column name, or an expression.
    /// </summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary><c>indnkeyatts</c>: how many of <see cref="Columns" /> are key columns.</summary>
    public int KeyColumnCount { get; init; }

    /// <summary><c>indclass</c>: each key column's operator class.</summary>
    public IReadOnlyList<uint> OperatorClasses { get; init; } = [];

    /// <summary><c>indcollation</c>: each key column's collation, zero where it has none.</summary>
    public IReadOnlyList<uint> Collations { get; init; } = [];

    /// <summary><c>indoption</c>: each key column's sort flags (descending, nulls first).</summary>
    public IReadOnlyList<short> Options { get; init; } = [];

    /// <summary>Whether it enforces uniqueness.</summary>
    public bool IsUnique { get; init; }

    /// <summary>Whether it backs the primary key.</summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary><c>indisvalid</c>: <see langword="false" /> after a failed concurrent build, or for a partitioned
    /// index some partition has no attached index for.</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>Its size, from <c>relpages</c> - its partitions' indexes' included.</summary>
    public long Bytes { get; init; }

    /// <summary><c>idx_scan</c>, its partitions' indexes' included; <see langword="null" /> when the collector has nothing.</summary>
    public long? Scans { get; init; }

    /// <summary>
    /// Every column of the table it reads: its plain key and <c>INCLUDE</c> columns, and every column its
    /// expressions and its predicate reference (from <c>pg_depend</c>). An update that changes one of these
    /// cannot be a HOT update.
    /// </summary>
    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    /// <summary>
    /// The phase of an index build running right now that is building this index
    /// (<c>pg_stat_progress_create_index</c>), or <see langword="null" />: one whose <c>index_relid</c> is this
    /// index; a <c>REINDEX CONCURRENTLY</c> on the table whose reported index this is the <c>_ccnew</c> or
    /// <c>_ccold</c> copy of (its first phase reports the index it copies, not the copy); or, for a partitioned
    /// index, a <c>CREATE INDEX</c> on a partition, which the index waits for to be attached. Never a build of
    /// another index on the same table, which says nothing about why this one is invalid.
    /// </summary>
    public string? BuildPhase { get; init; }

    /// <summary>
    /// The phase of any index build running right now on the table or one of its partitions, or
    /// <see langword="null" /> - for the caveat that a re-apply would queue behind it.
    /// </summary>
    public string? TableBuildPhase { get; init; }

    /// <summary>
    /// Whether somebody holds or awaits an <c>ACCESS EXCLUSIVE</c> lock on the table - a migration's <c>ALTER</c> - so the
    /// index was not deparsed and <see cref="Definition" /> and <see cref="Columns" /> are empty: <c>pg_get_indexdef</c>
    /// opens the table and would queue behind it (<see cref="HealthTableQueries.IndexesSql" />).
    /// </summary>
    public bool TableLocked { get; init; }

    /// <summary>How many index builds in this database the reading role cannot see the table of.</summary>
    public int UnseenIndexBuilds { get; init; }

    /// <summary>The server's <c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; } = 170_000;

    /// <summary><c>indisexclusion</c>: it backs an exclusion constraint.</summary>
    public bool IsExclusion { get; init; }

    /// <summary><c>indimmediate</c>: its uniqueness is checked at once rather than deferred.</summary>
    public bool IsImmediate { get; init; } = true;

    /// <summary><c>indnullsnotdistinct</c> (PostgreSQL 15+): a unique index that treats nulls as equal.</summary>
    public bool NullsNotDistinct { get; init; }

    // ---- W5-B1: what the foreign-key, bloat and usage judgements read - catalog values, present for a locked table too ----

    /// <summary>
    /// <c>indkey</c>'s key columns, as attribute numbers of the table, in index order - zero for an expression - and never
    /// an <c>INCLUDE</c> column: what T15 matches a foreign key's columns against (<c>ForeignKeyCoverage</c>). Read from the
    /// catalog, never deparsed, so a locked table's indexes carry it too.
    /// </summary>
    public IReadOnlyList<short> KeyAttributes { get; init; } = [];

    /// <summary>Whether it has a predicate (<c>indpred</c>): a partial index covers only the rows its <c>WHERE</c> admits.</summary>
    public bool IsPartial { get; init; }

    /// <summary>
    /// Each column the index stores - its key columns and then its <c>INCLUDE</c> columns, attributes 1 to <c>indnatts</c>,
    /// since a leaf entry carries both (W5-B1 review F6) - as its <c>attlen</c> on the index relation: its width in bytes, or
    /// <c>-1</c> (and <c>-2</c>) for a variable-width type.
    /// </summary>
    public IReadOnlyList<short> KeyWidths { get; init; } = [];

    /// <summary>Each column the index stores, <c>INCLUDE</c> columns too, as <c>format_type</c> prints its type.</summary>
    public IReadOnlyList<string> KeyTypes { get; init; } = [];

    /// <summary>
    /// Each column the index stores, <c>INCLUDE</c> columns too, as its <c>pg_stats.avg_width</c> - the table column's, for a
    /// plain column the reading role may read the statistics of - or <c>-1</c> where there is none: an expression, a column
    /// never analysed, or a role without column <c>SELECT</c> (B0 §8c).
    /// </summary>
    public IReadOnlyList<int> KeyAverageWidths { get; init; } = [];

    /// <summary>
    /// The index's own <c>reltuples</c> - its entries, as of the last <c>VACUUM</c> or <c>ANALYZE</c> - summed over its
    /// partitions' indexes; <see langword="null" /> while any is unknown (<c>-1</c>, never vacuumed or analysed).
    /// </summary>
    public double? EstimatedTuples { get; init; }

    /// <summary>
    /// <c>last_idx_scan</c> (PostgreSQL 16+): when it was last scanned, at the statistics flush after the scan - the latest of
    /// its partitions' indexes' - or <see langword="null" /> for never, and before 16.
    /// </summary>
    public DateTimeOffset? LastScan { get; init; }

    /// <summary>
    /// <c>pg_stat_all_indexes.stats_reset</c> (PostgreSQL 19+): since when this one index's counters count, or
    /// <see langword="null" /> - never reset on its own, or before 19.
    /// </summary>
    public DateTimeOffset? CountersReset { get; init; }

    /// <summary><c>reloptions</c> of the index: its storage parameters (<c>fillfactor</c>, <c>deduplicate_items</c>), as <c>name=value</c>.</summary>
    public IReadOnlyList<string> IndexOptions { get; init; } = [];

    /// <summary>The key the findings name this index by: schema, table, index.</summary>
    public string Key => Schema + "." + Table + "." + Name;

    /// <summary>The table's key.</summary>
    public string TableKey => Schema + "." + Table;
}

/// <summary>What the statistics say of one soft-deleted table's <c>mt_deleted</c> column.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Table">The table.</param>
/// <param name="EstimatedRows">The rows Postgres estimates, over the leaf partitions.</param>
/// <param name="RowsWithStatistics">How many of those rows sit in tables or partitions <c>pg_stats</c> has a row for.</param>
/// <param name="DeletedShare">
/// The share of rows with <c>mt_deleted = true</c>, weighted over the partitions that have statistics, or
/// <see langword="null" /> when none has: never analysed, or a role that may not read the column.
/// </param>
internal sealed record SoftDeleteShareRow(string Schema, string Table, double EstimatedRows, double RowsWithStatistics, double? DeletedShare);

/// <summary>One row of <c>mt_hilo</c>.</summary>
/// <param name="EntityName">The sequence's name: the document type's name, or its <c>HiloSettings.SequenceName</c>.</param>
/// <param name="HiValue">The last "hi" handed out; the ids up to <c>(hi + 1) × MaxLo</c> may have been issued.</param>
internal sealed record HiloRow(string EntityName, long HiValue);

/// <summary>What one bounded sample of a document table's <c>data</c> column measured.</summary>
/// <param name="Sampled">How many rows the sample read.</param>
/// <param name="P95Bytes">The 95th percentile of <c>pg_column_size(data)</c> - the stored, compressed size.</param>
/// <param name="MaxBytes">The largest stored document the sample saw.</param>
internal sealed record DocumentSizeSampleRow(int Sampled, double? P95Bytes, long? MaxBytes);

/// <summary>
/// The statements behind the tables-and-indexes health checks (T01, T03-T12).
/// </summary>
/// <remarks>
/// <para>
/// <b>A list locks nothing it lists</b> (AGENTS.md D27). Every list statement here reads <c>pg_class</c>,
/// <c>pg_index</c>, <c>pg_inherits</c>, <c>pg_depend</c>, <c>pg_stats</c> and the statistics views - the
/// catalog, which costs catalog <c>AccessShareLock</c>s and nothing else. Sizes are <c>relpages</c> estimates,
/// never a size function, because <c>pg_relation_size</c> opens the relation and queues behind any migration
/// holding a lock on it; partitions are walked through <c>pg_inherits</c>, never <c>pg_partition_tree()</c>,
/// which holds a lock on every partition until the transaction ends. A tenant-partitioned table has thousands
/// of partitions and the lock table is shared by the whole server. <c>HealthTableQueriesTests</c> holds every
/// list statement to it, and the live sweep test measures it.
/// </para>
/// <para>
/// <b>Names are values.</b> The tables come from <c>SchemaDeclarationReader</c> - never from
/// <c>AllObjects()</c> or <c>AllSchemaNames()</c>, which migrate (hard rule 14) - and travel as two
/// <c>text[]</c> parameters, so a statement shown under "Show SQL" names no table at all, and a hidden
/// document type's table is never in a statement's text. Only the two statements that must read one table's
/// rows - <c>mt_hilo</c>, and the deeper checks' sample of one visible document table - put a name in the
/// text, and that name goes through <see cref="SqlIdentifier" />.
/// </para>
/// <para>
/// <b>Fresh every call.</b> A <see cref="HealthQuery" /> carries its <see cref="NpgsqlParameter" />s, and a
/// parameter belongs to one command.
/// </para>
/// </remarks>
internal static class HealthTableQueries
{
    /// <summary>The bookkeeping table Marten's HiLo ids are drawn from, in the store's document schema.</summary>
    internal const string HiloTable = "mt_hilo";

    /// <summary>The soft-delete flag column Marten adds to a soft-deleted document table.</summary>
    internal const string DeletedColumn = "mt_deleted";

    /// <summary>
    /// The listed tables, rolled up: sizes from <c>relpages</c>, the collector's counters summed over the
    /// partitions, the latest maintenance times, the oldest transaction-id age, the storage parameters, and
    /// the database-wide facts the evaluators need beside each row - and autovacuum's own three decisions about each
    /// table (issue #8 G2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The triggers are autovacuum's own</b> (B0 §7a, proved against autovacuum itself in §7b): for every ordinary table
    /// and every leaf partition, the dead-row trigger <c>threshold + scale_factor × reltuples</c> - capped on PostgreSQL 18
    /// by <c>autovacuum_vacuum_max_threshold</c> - the insert trigger <c>threshold + scale_factor × reltuples</c>, scaled
    /// on 18 by the share of pages not all-frozen, and the analyze trigger, each from the table's own storage parameter
    /// where it has one and the server's setting where it does not, with <c>reltuples = -1</c> counted as 0.
    /// <c>relallfrozen</c> and the cap are read through <c>to_jsonb(c)</c> and <c>pg_settings</c>, so one text runs on 14
    /// to 19. A partitioned table is judged by its leaves, as autovacuum judges them: each decision is the leaf's whose
    /// counter is furthest past twice its own trigger, and the counts say how many leaves are past theirs.
    /// <c>Autovacuum.Triggers</c> is the same arithmetic in C#, which the unit tests hold to B0's numbers.
    /// </para>
    /// <para>
    /// <b>What runs now</b> is <c>pg_stat_progress_vacuum</c> of this database by <c>relid</c>, which a role without
    /// <c>pg_read_all_stats</c> sees null for every <c>VACUUM</c> it did not run - autovacuum's on its own tables included
    /// (B0 §7c) - so the count of those comes back beside each row. Every read here is the catalog's or shared memory's.
    /// </para>
    /// </remarks>
    internal const string TablesSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind, c.reltuples, c.relpages, c.reltoastrelid, c.reloptions,
                   case when c.relkind = 'r' then pg_catalog.age(c.relfrozenxid) end as xid_age
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        tree (root, relid) as (
            select i.inhparent, i.inhrelid
            from pg_catalog.pg_inherits i
            join listed l on l.oid = i.inhparent
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where l.relkind = 'p' and ch.relispartition
            union all
            select t.root, i.inhrelid
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where ch.relispartition
        ),
        rolled as (
            select t.root,
                   pg_catalog.sum(d.relpages::bigint) as heap_pages,
                   pg_catalog.sum(coalesce(toast.relpages, 0)::bigint) as toast_pages,
                   pg_catalog.sum((select coalesce(pg_catalog.sum(ic.relpages::bigint), 0)
                                   from pg_catalog.pg_index x
                                   join pg_catalog.pg_class ic on ic.oid = x.indexrelid
                                   where x.indrelid = d.oid)) as index_pages,
                   pg_catalog.bool_and(d.reltuples < 0) filter (where d.relkind = 'r') as unanalyzed,
                   pg_catalog.sum(greatest(d.reltuples, 0)) filter (where d.relkind = 'r') as tuples,
                   pg_catalog.sum(s.n_live_tup) as live,
                   pg_catalog.sum(s.n_dead_tup) as dead,
                   pg_catalog.sum(s.seq_scan) as seq,
                   pg_catalog.sum(s.seq_tup_read) as seq_read,
                   pg_catalog.sum(s.idx_scan) as idx,
                   pg_catalog.sum(s.n_tup_ins) as ins,
                   pg_catalog.sum(s.n_tup_upd) as upd,
                   pg_catalog.sum(s.n_tup_del) as del,
                   pg_catalog.sum(s.n_tup_hot_upd) as hot,
                   pg_catalog.sum(s.n_mod_since_analyze) as modified,
                   pg_catalog.max(s.last_vacuum) as vacuumed,
                   pg_catalog.max(s.last_autovacuum) as autovacuumed,
                   pg_catalog.max(s.last_analyze) as analyzed,
                   pg_catalog.max(s.last_autoanalyze) as autoanalyzed,
                   pg_catalog.max(greatest(
                       case when d.relkind = 'r' then pg_catalog.age(d.relfrozenxid) end,
                       case when toast.oid is not null then pg_catalog.age(toast.relfrozenxid) end)) as xid_age,
                   pg_catalog.count(*) filter (where d.relkind = 'r') as leaves,
                   pg_catalog.max(s.n_live_tup) filter (where d.relkind = 'r') as largest_leaf,
                   pg_catalog.sum(s.idx_tup_fetch) as idx_fetch,
                   pg_catalog.sum(s.vacuum_count) as vacuum_count,
                   pg_catalog.sum(s.autovacuum_count) as autovacuum_count,
                   pg_catalog.sum(s.analyze_count) as analyze_count,
                   pg_catalog.sum(s.autoanalyze_count) as autoanalyze_count,
                   pg_catalog.sum((pg_catalog.to_jsonb(s) ->> 'total_vacuum_time')::float8
                                  + (pg_catalog.to_jsonb(s) ->> 'total_autovacuum_time')::float8) as vacuum_ms,
                   pg_catalog.sum((pg_catalog.to_jsonb(s) ->> 'total_analyze_time')::float8
                                  + (pg_catalog.to_jsonb(s) ->> 'total_autoanalyze_time')::float8) as analyze_ms
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            left join pg_catalog.pg_class toast on toast.oid = d.reltoastrelid
            left join pg_catalog.pg_stat_user_tables s on s.relid = d.oid
            group by t.root
        ),
        leaf_options as (
            select t.root,
                   pg_catalog.split_part(o.opt, '=', 1) as name,
                   pg_catalog.substr(o.opt, pg_catalog.strpos(o.opt, '=') + 1) as value
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            cross join lateral pg_catalog.unnest(d.reloptions) as o(opt)
            where d.relkind = 'r'
        ),
        option_agreement as (
            select o.root,
                   o.name,
                   pg_catalog.min(o.value) as value,
                   pg_catalog.count(distinct o.value) = 1 and pg_catalog.count(*) = r.leaves as agreed
            from leaf_options o
            join rolled r on r.root = o.root
            group by o.root, o.name, r.leaves
        ),
        options_rolled as (
            select a.root,
                   pg_catalog.array_agg(a.name || '=' || a.value order by a.name) filter (where a.agreed) as agreed,
                   pg_catalog.array_agg(a.name order by a.name) filter (where not a.agreed) as differing
            from option_agreement a
            group by a.root
        ),
        builds as (
            select p.relid, p.index_relid, p.command::text as command, p.phase::text as phase
            from pg_catalog.pg_stat_progress_create_index p
            where p.datid = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
        ),
        tree_invalid as (
            select t.root,
                   (select b.phase
                    from builds b
                    where b.index_relid = x.indexrelid
                       or (b.command = 'REINDEX CONCURRENTLY'
                           and b.relid = d.oid
                           and xc.relname ~ '_cc(new|old)[0-9]*$'
                           and exists (
                               select 1
                               from pg_catalog.pg_class bi
                               where bi.oid = b.index_relid
                                 and pg_catalog.left(bi.relname::text,
                                         pg_catalog.length(pg_catalog.regexp_replace(xc.relname::text, '_cc(new|old)[0-9]*$', '')))
                                     = pg_catalog.regexp_replace(xc.relname::text, '_cc(new|old)[0-9]*$', '')))
                       or (xc.relkind = 'I'
                           and b.command not like 'REINDEX%'
                           and b.relid in (select t2.relid from tree t2 where t2.root = t.root))
                    limit 1) as phase
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            join pg_catalog.pg_index x on x.indrelid = d.oid and not x.indisvalid
            join pg_catalog.pg_class xc on xc.oid = x.indexrelid
        ),
        invalid_rolled as (
            select v.root,
                   pg_catalog.count(*) filter (where v.phase is null) as invalid,
                   pg_catalog.count(*) filter (where v.phase is not null) as building,
                   pg_catalog.min(v.phase) as building_phase
            from tree_invalid v
            group by v.root
        ),
        vacuums as (
            select p.relid
            from pg_catalog.pg_stat_progress_vacuum p
            where p.datid = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
        ),
        locks as (
            select distinct l.relation as relid
            from pg_catalog.pg_locks l
            where l.locktype = 'relation'
              and l.database = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
              and l.mode in ('ShareUpdateExclusiveLock', 'ShareLock', 'ShareRowExclusiveLock', 'ExclusiveLock', 'AccessExclusiveLock')
              and (l.pid is null
                   or (l.pid <> pg_catalog.pg_backend_pid()
                       and l.pid not in (select v.pid from pg_catalog.pg_stat_progress_vacuum v
                                         union all
                                         select z.pid from pg_catalog.pg_stat_progress_analyze z)))
        ),
        settings as (
            select pg_catalog.current_setting('block_size')::bigint as block_size,
                   (select db.stats_reset
                    from pg_catalog.pg_stat_database db
                    where db.datname = pg_catalog.current_database()) as stats_reset,
                   pg_catalog.current_setting('autovacuum')::boolean as autovacuum,
                   pg_catalog.current_setting('max_parallel_workers_per_gather')::int as parallel_workers,
                   (select pg_catalog.count(*) from builds b where b.relid is null)::int as unseen_builds,
                   pg_catalog.current_setting('autovacuum_freeze_max_age')::bigint as freeze_max_age,
                   pg_catalog.current_setting('server_version_num')::int as server_version,
                   pg_catalog.current_setting('track_counts')::boolean as track_counts,
                   pg_catalog.current_setting('autovacuum_vacuum_threshold')::float8 as vac_threshold,
                   pg_catalog.current_setting('autovacuum_vacuum_scale_factor')::float8 as vac_scale,
                   (select ps.setting::float8 from pg_catalog.pg_settings ps where ps.name = 'autovacuum_vacuum_max_threshold') as vac_max,
                   pg_catalog.current_setting('autovacuum_vacuum_insert_threshold')::float8 as ins_threshold,
                   pg_catalog.current_setting('autovacuum_vacuum_insert_scale_factor')::float8 as ins_scale,
                   pg_catalog.current_setting('autovacuum_analyze_threshold')::float8 as anl_threshold,
                   pg_catalog.current_setting('autovacuum_analyze_scale_factor')::float8 as anl_scale,
                   (select pg_catalog.count(*) from vacuums v where v.relid is null)::int as unseen_vacuums
        ),
        units (root, relid) as (
            select l.oid, l.oid
            from listed l
            where l.relkind = 'r'
            union all
            select t.root, t.relid
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            where d.relkind = 'r'
        ),
        judged as (
            select u.root,
                   s.relid is not null as counted,
                   coalesce(s.n_dead_tup, 0) as dead,
                   coalesce(s.n_ins_since_vacuum, 0) as inserted,
                   coalesce(s.n_mod_since_analyze, 0) as modified,
                   coalesce(s.n_live_tup, 0) as live,
                   coalesce(s.seq_tup_read, 0) + coalesce(s.idx_tup_fetch, 0) as rows_read,
                   d.reltuples < 0 and s.last_analyze is null and s.last_autoanalyze is null as unanalyzed,
                   d.reltuples < 0 and s.last_analyze is null and s.last_autoanalyze is null
                       and (coalesce(s.n_live_tup, 0) >= @min_rows
                            or (coalesce(s.seq_tup_read, 0) + coalesce(s.idx_tup_fetch, 0) >= @min_rows
                                and coalesce(s.seq_scan, 0) > 0 and coalesce(s.seq_tup_read, 0) >= @min_rows * s.seq_scan)) as unanalyzed_sized,
                   exists (select 1 from locks k where k.relid = u.relid) as locked,
                   coalesce(ro.enabled, 'true')::boolean as enabled,
                   coalesce(tro.enabled, 'true')::boolean as toast_enabled,
                   greatest(s.last_vacuum, s.last_autovacuum) as vacuumed,
                   greatest(s.last_analyze, s.last_autoanalyze) as analyzed,
                   exists (select 1 from vacuums v where v.relid = u.relid) as running,
                   z.tuples,
                   z.unfrozen,
                   e.vt, e.vsf, e.vmax, e.it, e.isf, e.at, e.asf,
                   case when e.vmax >= 0 then least(e.vt + e.vsf * z.tuples, e.vmax) else e.vt + e.vsf * z.tuples end as vac_trigger,
                   case when e.it >= 0 then e.it + e.isf * z.tuples * z.unfrozen end as ins_trigger,
                   e.at + e.asf * z.tuples as anl_trigger
            from units u
            join pg_catalog.pg_class d on d.oid = u.relid
            left join pg_catalog.pg_class toast on toast.oid = d.reltoastrelid
            left join pg_catalog.pg_stat_user_tables s on s.relid = u.relid
            cross join settings st
            cross join lateral (
                select pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_enabled') as enabled,
                       (pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_vacuum_threshold'))::float8 as vt,
                       (pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_vacuum_scale_factor'))::float8 as vsf,
                       (pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_vacuum_max_threshold'))::float8 as vmax,
                       (pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_vacuum_insert_threshold'))::float8 as it,
                       (pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_vacuum_insert_scale_factor'))::float8 as isf,
                       (pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_analyze_threshold'))::float8 as at,
                       (pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_analyze_scale_factor'))::float8 as asf
                from pg_catalog.pg_options_to_table(d.reloptions) as x
            ) ro
            cross join lateral (
                select pg_catalog.max(x.option_value) filter (where x.option_name = 'autovacuum_enabled') as enabled
                from pg_catalog.pg_options_to_table(toast.reloptions) as x
            ) tro
            cross join lateral (
                select greatest(d.reltuples, 0)::float8 as tuples,
                       case when d.relpages > 0 and coalesce((pg_catalog.to_jsonb(d) ->> 'relallfrozen')::int4, 0) > 0
                            then 1 - least((pg_catalog.to_jsonb(d) ->> 'relallfrozen')::float8, d.relpages::float8) / d.relpages
                            else 1 end as unfrozen
            ) z
            cross join lateral (
                select coalesce(ro.vt, st.vac_threshold) as vt,
                       coalesce(ro.vsf, st.vac_scale) as vsf,
                       coalesce(ro.vmax, st.vac_max, -1) as vmax,
                       coalesce(ro.it, st.ins_threshold) as it,
                       coalesce(ro.isf, st.ins_scale) as isf,
                       coalesce(ro.at, st.anl_threshold) as at,
                       coalesce(ro.asf, st.anl_scale) as asf
            ) e
        ),
        unit_json as (
            select j.root,
                   j.dead - 2 * j.vac_trigger as vacuum_excess,
                   j.inserted - 2 * j.ins_trigger as insert_excess,
                   j.modified - 2 * j.anl_trigger as analyze_excess,
                   j.dead / nullif(j.vac_trigger, 0) as vacuum_ratio,
                   j.inserted / nullif(j.ins_trigger, 0) as insert_ratio,
                   j.modified / nullif(j.anl_trigger, 0) as analyze_ratio,
                   case when j.counted then pg_catalog.jsonb_build_object(
                       'counter', j.dead, 'trigger', j.vac_trigger, 'threshold', j.vt, 'scale', j.vsf, 'tuples', j.tuples,
                       'last', extract(epoch from j.vacuumed), 'running', j.running, 'locked', j.locked,
                       'max', case when j.vmax >= 0 then j.vmax end) end as vacuum_unit,
                   case when j.counted and j.ins_trigger is not null then pg_catalog.jsonb_build_object(
                       'counter', j.inserted, 'trigger', j.ins_trigger, 'threshold', j.it, 'scale', j.isf, 'tuples', j.tuples,
                       'last', extract(epoch from j.vacuumed), 'running', j.running, 'locked', j.locked, 'unfrozen', j.unfrozen) end as insert_unit,
                   case when j.counted then pg_catalog.jsonb_build_object(
                       'counter', j.modified, 'trigger', j.anl_trigger, 'threshold', j.at, 'scale', j.asf, 'tuples', j.tuples,
                       'last', extract(epoch from j.analyzed), 'running', false, 'locked', j.locked) end as analyze_unit
            from judged j
        ),
        worst_vacuum as (
            select distinct on (w.root) w.root, w.vacuum_unit
            from unit_json w
            order by w.root, w.vacuum_excess >= 0 desc, case when w.vacuum_excess >= 0 then w.vacuum_excess end desc nulls last, w.vacuum_ratio desc nulls last
        ),
        worst_insert as (
            select distinct on (w.root) w.root, w.insert_unit
            from unit_json w
            where w.insert_unit is not null
            order by w.root, w.insert_excess >= 0 desc, case when w.insert_excess >= 0 then w.insert_excess end desc nulls last, w.insert_ratio desc nulls last
        ),
        worst_analyze as (
            select distinct on (w.root) w.root, w.analyze_unit
            from unit_json w
            order by w.root, w.analyze_excess >= 0 desc, case when w.analyze_excess >= 0 then w.analyze_excess end desc nulls last, w.analyze_ratio desc nulls last
        ),
        unit_counts as (
            select j.root,
                   pg_catalog.count(*) filter (where j.dead > j.vac_trigger)::int as past_vacuum,
                   pg_catalog.count(*) filter (where j.inserted > j.ins_trigger)::int as past_insert,
                   pg_catalog.count(*) filter (where j.modified > j.anl_trigger)::int as past_analyze,
                   pg_catalog.count(*) filter (where not j.enabled)::int as autovacuum_off,
                   pg_catalog.count(*) filter (where not j.toast_enabled)::int as toast_autovacuum_off,
                   pg_catalog.count(*) filter (where j.unanalyzed_sized)::int as unanalyzed,
                   coalesce(pg_catalog.max(j.live) filter (where j.unanalyzed_sized), 0)::bigint as unanalyzed_live,
                   coalesce(pg_catalog.max(j.rows_read) filter (where j.unanalyzed_sized), 0)::bigint as unanalyzed_read
            from judged j
            group by j.root
        )
        select l.nspname::text,
               l.relname::text,
               l.relkind::text,
               case
                   when l.relkind <> 'p' then l.reltuples::bigint
                   when coalesce(r.unanalyzed, true) then -1
                   else r.tuples::bigint
               end,
               (l.relpages::bigint + coalesce(r.heap_pages, 0)) * st.block_size,
               (coalesce(lt.relpages, 0)::bigint + coalesce(r.toast_pages, 0)) * st.block_size,
               ((select coalesce(pg_catalog.sum(ic.relpages::bigint), 0)
                 from pg_catalog.pg_index x
                 join pg_catalog.pg_class ic on ic.oid = x.indexrelid
                 where x.indrelid = l.oid) + coalesce(r.index_pages, 0)) * st.block_size,
               case when l.relkind = 'p' then r.live::bigint else s.n_live_tup end,
               case when l.relkind = 'p' then r.dead::bigint else s.n_dead_tup end,
               case when l.relkind = 'p' then r.seq::bigint else s.seq_scan end,
               case when l.relkind = 'p' then r.seq_read::bigint else s.seq_tup_read end,
               case when l.relkind = 'p' then r.idx::bigint else s.idx_scan end,
               case when l.relkind = 'p' then r.ins::bigint else s.n_tup_ins end,
               case when l.relkind = 'p' then r.upd::bigint else s.n_tup_upd end,
               case when l.relkind = 'p' then r.del::bigint else s.n_tup_del end,
               case when l.relkind = 'p' then r.hot::bigint else s.n_tup_hot_upd end,
               case when l.relkind = 'p' then r.modified::bigint else s.n_mod_since_analyze end,
               greatest(s.last_vacuum, r.vacuumed),
               greatest(s.last_autovacuum, r.autovacuumed),
               greatest(s.last_analyze, r.analyzed),
               greatest(s.last_autoanalyze, r.autoanalyzed),
               greatest(l.xid_age, case when lt.oid is not null then pg_catalog.age(lt.relfrozenxid) end, r.xid_age)::bigint,
               coalesce(ir.invalid, 0)::int,
               case
                   when l.relkind = 'p' then coalesce(orl.agreed, '{}'::text[])
                   else coalesce(l.reloptions, '{}'::text[])
               end,
               st.stats_reset,
               st.autovacuum,
               st.parallel_workers,
               coalesce(r.leaves, 0)::int,
               r.largest_leaf::bigint,
               coalesce(orl.differing, '{}'::text[]),
               ir.building_phase,
               st.unseen_builds,
               st.freeze_max_age,
               st.server_version,
               coalesce(ir.building, 0)::int,
               (select b.phase
                from builds b
                where b.relid = l.oid or b.relid in (select t.relid from tree t where t.root = l.oid)
                limit 1),
               case when l.relkind = 'p' then r.idx_fetch::bigint else s.idx_tup_fetch end,
               wv.vacuum_unit::text,
               wi.insert_unit::text,
               wa.analyze_unit::text,
               coalesce(uc.past_vacuum, 0),
               coalesce(uc.past_insert, 0),
               coalesce(uc.past_analyze, 0),
               coalesce(uc.autovacuum_off, 0),
               coalesce(uc.toast_autovacuum_off, 0),
               coalesce(uc.unanalyzed, 0),
               coalesce(uc.unanalyzed_live, 0),
               coalesce(uc.unanalyzed_read, 0),
               case when l.relkind = 'p' then l.reltuples::bigint end,
               case when l.relkind = 'p' then '{}'::text[] else coalesce(lt.reloptions, '{}'::text[]) end,
               case when l.relkind = 'p' then r.vacuum_count::bigint else s.vacuum_count end,
               case when l.relkind = 'p' then r.autovacuum_count::bigint else s.autovacuum_count end,
               case when l.relkind = 'p' then r.analyze_count::bigint else s.analyze_count end,
               case when l.relkind = 'p' then r.autoanalyze_count::bigint else s.autoanalyze_count end,
               case when l.relkind = 'p' then r.vacuum_ms
                    else (pg_catalog.to_jsonb(s) ->> 'total_vacuum_time')::float8 + (pg_catalog.to_jsonb(s) ->> 'total_autovacuum_time')::float8 end,
               case when l.relkind = 'p' then r.analyze_ms
                    else (pg_catalog.to_jsonb(s) ->> 'total_analyze_time')::float8 + (pg_catalog.to_jsonb(s) ->> 'total_autoanalyze_time')::float8 end,
               st.track_counts,
               st.unseen_vacuums
        from listed l
        cross join settings st
        left join pg_catalog.pg_class lt on lt.oid = l.reltoastrelid
        left join rolled r on r.root = l.oid
        left join invalid_rolled ir on ir.root = l.oid
        left join options_rolled orl on orl.root = l.oid
        left join pg_catalog.pg_stat_user_tables s on s.relid = l.oid
        left join worst_vacuum wv on wv.root = l.oid
        left join worst_insert wi on wi.root = l.oid
        left join worst_analyze wa on wa.root = l.oid
        left join unit_counts uc on uc.root = l.oid
        order by 1, 2
        """;

    /// <summary>
    /// Every index on the listed tables - never an index on a partition, whose pages and scans are added into
    /// its partitioned parent index's - with what the redundancy, validity, use and HOT judgements need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>pg_get_indexdef</c> deparses each index, as Weasel's own index read does and as the Schema screen's always
    /// has - opening its table to do so (below); it is called for the listed tables' indexes only, never a partition's. The
    /// referenced columns come from <c>pg_depend</c>, where Postgres records the columns an expression or a
    /// predicate reads, and from <c>indkey</c> for the plain ones - an index backing a constraint depends on the
    /// constraint rather than on its columns, so <c>pg_depend</c> alone would miss them.
    /// </para>
    /// <para>
    /// <b>Never deparsed behind a migration.</b> <c>pg_get_indexdef</c>, in every form, builds its deparse context with
    /// <c>deparse_context_for</c>, which opens the indexed table with <c>AccessShareLock</c> and closes it again
    /// (<c>set_relation_column_names</c>, PG 14-19): nothing is held once a call returns, but behind somebody's
    /// <c>ACCESS EXCLUSIVE</c> lock on the table the call queues, like a size function (B0 §3) - measured by
    /// <c>VersionMatrixLiveTests</c> on 14 to 19, where the regular sweep's index checks all waited out the
    /// <c>lock_timeout</c>. So <c>locked</c> reads <c>pg_locks</c> once (a shared-memory snapshot, no relation lock) for
    /// any <c>AccessExclusiveLock</c> held or awaited on a listed table, and such a table's indexes come back undeparsed,
    /// marked in the last column (<see cref="HealthIndexRow.TableLocked" />); the reader leaves that table's indexes out and
    /// counts it, so every other table is still judged and each index check says how many were not
    /// (<c>HealthTables.ReadIndexesAsync</c>). A lock taken between the snapshot and a call still waits, bounded by
    /// <c>lock_timeout</c>; the reader then asks once more, when the snapshot sees it.
    /// </para>
    /// <para>
    /// <b>What W5-B1 reads beside them, all from the catalog and the statistics views, none of it deparsed</b> - so a locked
    /// table's indexes carry it too: the key columns as attribute numbers (<c>indkey</c>, for T15's foreign-key coverage),
    /// whether the index is partial, each key column's width and type from the index relation's own <c>pg_attribute</c>
    /// rows and its <c>pg_stats.avg_width</c> where the reading role may read it (for T25's size estimate), the index's own
    /// <c>reltuples</c>, <c>last_idx_scan</c> (16+) and <c>stats_reset</c> (19+) read by name through <c>to_jsonb</c> - each
    /// rolled up over a partitioned index's partitions - and its storage parameters.
    /// </para>
    /// </remarks>
    internal const string IndexesSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        locked as (
            select distinct k.relation as oid
            from pg_catalog.pg_locks k
            where k.locktype = 'relation'
              and k.mode = 'AccessExclusiveLock'
              and k.database = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
              and k.relation in (select l.oid from listed l)
        ),
        indexes as (
            select l.nspname, l.relname as table_name, l.oid as table_oid,
                   ic.oid, ic.relname as index_name, ic.relkind, ic.relpages,
                   am.amname,
                   x.indisunique, x.indisprimary, x.indisvalid, x.indnatts, x.indnkeyatts,
                   x.indkey, x.indclass, x.indcollation, x.indoption,
                   x.indisexclusion, x.indimmediate,
                   coalesce((pg_catalog.to_jsonb(x) ->> 'indnullsnotdistinct')::boolean, false) as nulls_not_distinct,
                   x.indpred is not null as partial,
                   ic.reltuples,
                   ic.reloptions as index_options
            from listed l
            join pg_catalog.pg_index x on x.indrelid = l.oid
            join pg_catalog.pg_class ic on ic.oid = x.indexrelid
            join pg_catalog.pg_am am on am.oid = ic.relam
        ),
        table_tree (root, relid) as (
            select l.oid, l.oid
            from listed l
            union all
            select t.root, inh.inhrelid
            from table_tree t
            join pg_catalog.pg_inherits inh on inh.inhparent = t.relid
        ),
        builds as (
            select p.relid, p.index_relid, p.command::text as command, p.phase::text as phase
            from pg_catalog.pg_stat_progress_create_index p
            where p.datid = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
        ),
        tree (root, relid) as (
            select inh.inhparent, inh.inhrelid
            from pg_catalog.pg_inherits inh
            join indexes i on i.oid = inh.inhparent
            where i.relkind = 'I'
            union all
            select t.root, inh.inhrelid
            from tree t
            join pg_catalog.pg_inherits inh on inh.inhparent = t.relid
        ),
        rolled as (
            select t.root,
                   pg_catalog.sum(d.relpages::bigint) as pages,
                   pg_catalog.sum(s.idx_scan) as scans,
                   case when pg_catalog.bool_or(d.reltuples < 0) filter (where d.relkind = 'i') then null
                        else pg_catalog.sum(d.reltuples::float8) filter (where d.relkind = 'i') end as tuples,
                   pg_catalog.max((pg_catalog.to_jsonb(s) ->> 'last_idx_scan')::timestamptz) as last_scan,
                   pg_catalog.max((pg_catalog.to_jsonb(s) ->> 'stats_reset')::timestamptz) as counters_reset
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            left join pg_catalog.pg_stat_user_indexes s on s.indexrelid = d.oid
            group by t.root
        )
        select i.nspname::text,
               i.table_name::text,
               i.index_name::text,
               i.amname::text,
               case when i.table_oid in (select lk.oid from locked lk) then null
                    else pg_catalog.pg_get_indexdef(i.oid) end,
               case when i.table_oid in (select lk.oid from locked lk) then null
                    else array(select pg_catalog.pg_get_indexdef(i.oid, k, true)
                               from pg_catalog.generate_series(1, i.indnatts::int) as k
                               order by k) end,
               i.indnkeyatts::int,
               i.indclass::pg_catalog.oid[],
               i.indcollation::pg_catalog.oid[],
               i.indoption::pg_catalog.int2[],
               i.indisunique,
               i.indisprimary,
               i.indisvalid,
               (i.relpages::bigint + coalesce(r.pages, 0)) * pg_catalog.current_setting('block_size')::bigint,
               case when i.relkind = 'I' then r.scans::bigint else s.idx_scan end,
               array(select a.attname::text
                     from pg_catalog.pg_attribute a
                     where a.attrelid = i.table_oid
                       and a.attnum > 0
                       and not a.attisdropped
                       and (a.attnum = any(i.indkey::pg_catalog.int2[])
                            or exists (
                                select 1
                                from pg_catalog.pg_depend d
                                where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                                  and d.objid = i.oid
                                  and d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
                                  and d.refobjid = i.table_oid
                                  and d.refobjsubid = a.attnum))
                     order by a.attnum),
               (select b.phase
                from builds b
                where b.index_relid = i.oid
                   or (b.command = 'REINDEX CONCURRENTLY'
                       and b.relid = i.table_oid
                       and i.index_name ~ '_cc(new|old)[0-9]*$'
                       and exists (
                           select 1
                           from pg_catalog.pg_class bi
                           where bi.oid = b.index_relid
                             and pg_catalog.left(bi.relname::text,
                                     pg_catalog.length(pg_catalog.regexp_replace(i.index_name::text, '_cc(new|old)[0-9]*$', '')))
                                 = pg_catalog.regexp_replace(i.index_name::text, '_cc(new|old)[0-9]*$', '')))
                   or (i.relkind = 'I'
                       and b.command not like 'REINDEX%'
                       and b.relid in (select t.relid from table_tree t where t.root = i.table_oid))
                limit 1),
               (select pg_catalog.count(*) from builds b where b.relid is null)::int,
               pg_catalog.current_setting('server_version_num')::int,
               i.indisexclusion,
               i.indimmediate,
               i.nulls_not_distinct,
               (select b.phase
                from builds b
                where b.relid in (select t.relid from table_tree t where t.root = i.table_oid)
                limit 1),
               i.table_oid in (select lk.oid from locked lk),
               (pg_catalog.string_to_array(i.indkey::text, ' ')::pg_catalog.int2[])[1:i.indnkeyatts],
               i.partial,
               array(select a.attlen
                     from pg_catalog.pg_attribute a
                     where a.attrelid = i.oid and a.attnum between 1 and i.indnatts
                     order by a.attnum),
               array(select pg_catalog.format_type(a.atttypid, a.atttypmod)
                     from pg_catalog.pg_attribute a
                     where a.attrelid = i.oid and a.attnum between 1 and i.indnatts
                     order by a.attnum),
               array(select coalesce((select st.avg_width
                                      from pg_catalog.pg_attribute ta
                                      join pg_catalog.pg_stats st
                                        on st.schemaname = i.nspname and st.tablename = i.table_name
                                       and st.attname = ta.attname and not st.inherited
                                      where ta.attrelid = i.table_oid and ta.attnum = k.attnum and k.attnum > 0), -1)
                     from pg_catalog.unnest((pg_catalog.string_to_array(i.indkey::text, ' ')::pg_catalog.int2[])[1:i.indnatts])
                          with ordinality as k(attnum, ord)
                     order by k.ord),
               case when i.relkind = 'I' then r.tuples
                    when i.reltuples < 0 then null
                    else i.reltuples::float8 end,
               case when i.relkind = 'I' then r.last_scan else (pg_catalog.to_jsonb(s) ->> 'last_idx_scan')::timestamptz end,
               case when i.relkind = 'I' then r.counters_reset else (pg_catalog.to_jsonb(s) ->> 'stats_reset')::timestamptz end,
               coalesce(i.index_options, '{}'::text[])
        from indexes i
        left join rolled r on r.root = i.oid
        left join pg_catalog.pg_stat_user_indexes s on s.indexrelid = i.oid
        order by 1, 2, 3
        """;

    /// <summary>
    /// The share of <c>mt_deleted = true</c> in each listed soft-deleted table, from <c>pg_stats</c>'
    /// most-common-value frequencies, weighted over the leaf partitions by their <c>reltuples</c>.
    /// </summary>
    /// <remarks>
    /// <c>pg_stats</c> shows only the columns the reading role may select, so a role without that privilege
    /// reads no statistics and the check says "cannot see" rather than "none". A partitioned parent is
    /// judged by its leaves' own statistics (<c>inherited = false</c>): autovacuum never analyses a
    /// partitioned parent, so its inherited row is usually missing.
    /// </remarks>
    internal const string SoftDeleteSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        tree (root, relid, relkind) as (
            select l.oid, l.oid, l.relkind
            from listed l
            union all
            select t.root, i.inhrelid, ch.relkind
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where t.relkind = 'p' and ch.relispartition
        ),
        leaves as (
            select t.root,
                   greatest(c.reltuples, 0)::float8 as tuples,
                   (select coalesce(
                               s.most_common_freqs[pg_catalog.array_position(s.most_common_vals::text::boolean[], true)],
                               0)::float8
                    from pg_catalog.pg_stats s
                    where s.schemaname = ln.nspname
                      and s.tablename = c.relname
                      and s.attname = 'mt_deleted'
                      and not s.inherited) as deleted_share
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
            join pg_catalog.pg_namespace ln on ln.oid = c.relnamespace
            where t.relkind = 'r'
        )
        select l.nspname::text,
               l.relname::text,
               coalesce(pg_catalog.sum(v.tuples), 0)::float8,
               coalesce(pg_catalog.sum(v.tuples) filter (where v.deleted_share is not null), 0)::float8,
               (pg_catalog.sum(v.tuples * v.deleted_share) filter (where v.deleted_share is not null)
                   / nullif(pg_catalog.sum(v.tuples) filter (where v.deleted_share is not null), 0))::float8
        from listed l
        left join leaves v on v.root = l.oid
        group by l.nspname, l.relname
        order by 1, 2
        """;

    /// <summary>Whether the store's document schema has an <c>mt_hilo</c> table - read first, so a missing one is never a <c>42P01</c>.</summary>
    internal const string HiloExistsSql =
        """
        select exists (
            select 1
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where n.nspname = @schema
              and c.relname = @table
              and c.relkind in ('r', 'p'))
        """;

    /// <summary>
    /// The ordinary and partitioned tables of the schemas a plain data source's <c>BrowsableSchemas</c> admits - its own,
    /// named exactly, and, under <c>"*"</c>, every other schema the reading role has <c>USAGE</c> on that no extension
    /// owns - largest first by estimated size, at most <c>@cap</c> of them, each row carrying how many there are in all
    /// (issue #8 G1; design R5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same admission as the database browser's</b> (<c>BrowsableSchemaMatcher.Match</c>): never a system schema,
    /// never one without <c>USAGE</c>, and <c>"*"</c> never an extension's schema while an exact name may be. A table an
    /// extension owns is no host's. A partition is judged through its partitioned table, so it is not a row of its own.
    /// </para>
    /// <para>
    /// <b>Estimates only, and locks nothing it lists</b> (D27; B0: a size function waits behind a migration's lock).
    /// The size is <c>relpages</c> - summed over the partition tree, walked through <c>pg_inherits</c>, for a partitioned
    /// table - never <c>pg_relation_size</c>. A database of ten thousand tables is capped at the largest
    /// <c>@cap</c>, and the total (<c>count(*) over ()</c>, computed before the limit) is what the coverage sentence
    /// says was left out. So is the total in the source's own schemas alone (<c>@own</c>): a visitor the database browser
    /// keeps out of the rest is told only how many of <em>those</em> there are (G1M F6, H1).
    /// </para>
    /// </remarks>
    internal const string RelationalTablesSql =
        """
        with recursive admitted as (
            select n.oid, n.nspname
            from pg_catalog.pg_namespace n
            where pg_catalog.left(n.nspname::text, 3) <> 'pg_'
              and not (n.nspname = any(@system))
              and coalesce(pg_catalog.has_schema_privilege(n.oid, 'USAGE'), false)
              and (n.nspname = any(@names)
                   or (@everything and not exists (
                       select 1
                       from pg_catalog.pg_depend d
                       where d.classid = 'pg_catalog.pg_namespace'::pg_catalog.regclass
                         and d.objid = n.oid
                         and d.deptype = 'e')))
        ),
        candidates as (
            select c.oid, a.nspname, c.relname, c.relkind, c.relpages
            from pg_catalog.pg_class c
            join admitted a on a.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and not c.relispartition
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
        ),
        tree (root, relid) as (
            select t.oid, i.inhrelid
            from candidates t
            join pg_catalog.pg_inherits i on i.inhparent = t.oid
            where t.relkind = 'p'
            union all
            select tree.root, i.inhrelid
            from tree
            join pg_catalog.pg_inherits i on i.inhparent = tree.relid
        ),
        partitioned (root, pages) as (
            select tree.root, pg_catalog.sum(x.relpages::bigint)
            from tree
            join pg_catalog.pg_class x on x.oid = tree.relid
            group by tree.root
        )
        select t.nspname::text,
               t.relname::text,
               t.relkind::text,
               case when t.relkind = 'p' then coalesce(p.pages, 0)::bigint else t.relpages::bigint end,
               (pg_catalog.count(*) over ())::int,
               (pg_catalog.count(*) filter (where t.nspname = any(@own)) over ())::int
        from candidates t
        left join partitioned p on p.root = t.oid
        order by 4 desc, 1, 2
        limit @cap
        """;

    /// <summary>
    /// <see cref="RelationalTablesSql" /> for a source that declares tables of its own (issue #8, G1 for a Marten store):
    /// the same admission, the same estimates, the same order and cap, and two more lines in <c>candidates</c> - a table
    /// the configuration of a registered source declares is left out (<c>@declared_schemas</c>, <c>@declared_tables</c>,
    /// compared ignoring case as the source's own sets compare them), and, with <c>@skip_documents</c>, so is every table
    /// named like a document table (<c>@document_prefix</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Left out before the count and the cap</b>, so the total the coverage sentence says is the number of tables that
    /// could have been judged, and the cap is spent on them alone: a declared table is judged from the configuration, never
    /// from this list, and another source's is not this sweep's to judge.
    /// </para>
    /// <para>
    /// <b>A hidden document type's table is never a value here.</b> The caller passes <c>@skip_documents</c> whenever a
    /// document type may be hidden, so such a table never comes back from the catalog at all and its name is not in any
    /// parameter: only visible declarations are ever bound.
    /// </para>
    /// <para>
    /// Kept the same as <see cref="RelationalTablesSql" /> but for those two predicates, which
    /// <c>HealthTableQueriesTests</c> holds it to: removing them gives back that statement byte for byte.
    /// </para>
    /// </remarks>
    internal const string UndeclaredTablesSql =
        """
        with recursive admitted as (
            select n.oid, n.nspname
            from pg_catalog.pg_namespace n
            where pg_catalog.left(n.nspname::text, 3) <> 'pg_'
              and not (n.nspname = any(@system))
              and coalesce(pg_catalog.has_schema_privilege(n.oid, 'USAGE'), false)
              and (n.nspname = any(@names)
                   or (@everything and not exists (
                       select 1
                       from pg_catalog.pg_depend d
                       where d.classid = 'pg_catalog.pg_namespace'::pg_catalog.regclass
                         and d.objid = n.oid
                         and d.deptype = 'e')))
        ),
        candidates as (
            select c.oid, a.nspname, c.relname, c.relkind, c.relpages
            from pg_catalog.pg_class c
            join admitted a on a.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and not c.relispartition
              and not (@skip_documents and pg_catalog.starts_with(pg_catalog.lower(c.relname::text), @document_prefix))
              and not exists (
                  select 1
                  from rows from (pg_catalog.unnest(@declared_schemas), pg_catalog.unnest(@declared_tables)) as x(schema_name, table_name)
                  where pg_catalog.lower(x.schema_name) = pg_catalog.lower(a.nspname::text)
                    and pg_catalog.lower(x.table_name) = pg_catalog.lower(c.relname::text))
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
        ),
        tree (root, relid) as (
            select t.oid, i.inhrelid
            from candidates t
            join pg_catalog.pg_inherits i on i.inhparent = t.oid
            where t.relkind = 'p'
            union all
            select tree.root, i.inhrelid
            from tree
            join pg_catalog.pg_inherits i on i.inhparent = tree.relid
        ),
        partitioned (root, pages) as (
            select tree.root, pg_catalog.sum(x.relpages::bigint)
            from tree
            join pg_catalog.pg_class x on x.oid = tree.relid
            group by tree.root
        )
        select t.nspname::text,
               t.relname::text,
               t.relkind::text,
               case when t.relkind = 'p' then coalesce(p.pages, 0)::bigint else t.relpages::bigint end,
               (pg_catalog.count(*) over ())::int,
               (pg_catalog.count(*) filter (where t.nspname = any(@own)) over ())::int
        from candidates t
        left join partitioned p on p.root = t.oid
        order by 4 desc, 1, 2
        limit @cap
        """;

    /// <summary>
    /// The prefix every Marten document table's name carries - <c>DatabaseObjectClassifier.DocumentTablePrefix</c>, bound
    /// rather than spelt in <see cref="UndeclaredTablesSql" />, and held equal to it by a test.
    /// </summary>
    internal const string DocumentTablePrefix = "mt_doc_";

    /// <summary>Every list statement - the ones that must lock nothing they list - for the tests that hold them to it.</summary>
    internal static IReadOnlyList<string> ListStatements { get; } =
        [TablesSql, IndexesSql, SoftDeleteSql, HiloExistsSql, RelationalTablesSql, UndeclaredTablesSql];

    /// <summary>
    /// The tables a plain data source's health checks judge (<see cref="RelationalTablesSql" />): every table in
    /// <paramref name="names" />, and with <paramref name="everything" /> every schema <c>"*"</c> admits, the largest
    /// <paramref name="cap" /> by estimated size.
    /// </summary>
    /// <param name="names">The schemas named exactly.</param>
    /// <param name="everything">Whether <c>"*"</c> is configured.</param>
    /// <param name="cap">How many tables at most.</param>
    public static HealthQuery RelationalTables(IReadOnlyList<string> names, bool everything, int cap)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);

        string[] named = names as string[] ?? [.. names];

        // The schemas named exactly are the source's own (D27 in Postgres Studio): the second total counts them.
        return new HealthQuery(
            RelationalTablesSql,
            [
                new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = named },
                new NpgsqlParameter("everything", NpgsqlDbType.Boolean) { Value = everything },
                new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = cap },
                new NpgsqlParameter("system", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = SystemSchemasBeyondPrefix },
                new NpgsqlParameter("own", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = named },
            ]);
    }

    /// <summary>
    /// The tables of the admitted schemas but the ones a registered source declares (<see cref="UndeclaredTablesSql" />):
    /// the schemas named in <paramref name="names" /> and, with <paramref name="everything" />, every schema <c>"*"</c>
    /// admits, the largest <paramref name="cap" /> by estimated size.
    /// </summary>
    /// <param name="names">The schemas named exactly.</param>
    /// <param name="everything">Whether <c>"*"</c> is configured and in force.</param>
    /// <param name="declared">
    /// The tables registered sources declare, left out of the list and its count. Never a hidden document type's: those are
    /// left out by <paramref name="skipDocumentTables" />.
    /// </param>
    /// <param name="skipDocumentTables">Whether to leave out every table named like a document table.</param>
    /// <param name="cap">How many tables at most.</param>
    /// <param name="own">
    /// The source's own schemas - the ones every visitor its gate admits may see - which the second total counts (G1M F6,
    /// H1); <see langword="null" /> for <paramref name="names" />.
    /// </param>
    public static HealthQuery UndeclaredTables(
        IReadOnlyList<string> names,
        bool everything,
        IReadOnlyList<HealthTableTarget> declared,
        bool skipDocumentTables,
        int cap,
        IReadOnlyList<string>? own = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);

        NpgsqlParameter[] targets = TargetParameters(declared);
        string[] named = names as string[] ?? [.. names];

        return new HealthQuery(
            UndeclaredTablesSql,
            [
                new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = named },
                new NpgsqlParameter("everything", NpgsqlDbType.Boolean) { Value = everything },
                new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = cap },
                new NpgsqlParameter("system", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = SystemSchemasBeyondPrefix },
                new NpgsqlParameter("skip_documents", NpgsqlDbType.Boolean) { Value = skipDocumentTables },
                new NpgsqlParameter("document_prefix", NpgsqlDbType.Text) { Value = DocumentTablePrefix },
                new NpgsqlParameter("declared_schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = targets[0].Value },
                new NpgsqlParameter("declared_tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = targets[1].Value },
                new NpgsqlParameter("own", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = own is null ? named : own as string[] ?? [.. own] },
            ]);
    }

    /// <summary>
    /// The system schemas whose names do not start <c>pg_</c> - which the statement refuses by prefix - as
    /// <c>BrowsableSchemaMatcher.IsSystemSchema</c> names them: bound, so the statement and the browser cannot disagree.
    /// </summary>
    internal static string[] SystemSchemasBeyondPrefix => ["information_schema"];

    /// <summary>Maps one row of <see cref="RelationalTablesSql" />.</summary>
    public static HealthRelationalTableRow ReadRelationalTable(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthRelationalTableRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4))
        {
            OwnTotal = reader.GetInt32(5),
        };
    }

    /// <summary>The listed tables, rolled up (<see cref="TablesSql" />).</summary>
    public static HealthQuery Tables(IReadOnlyList<HealthTableTarget> tables) =>
        new(TablesSql, [.. TargetParameters(tables), new NpgsqlParameter("min_rows", NpgsqlDbType.Bigint) { Value = UnanalyzedMinRows }]);

    /// <summary>
    /// T08's line for a table never analysed (<c>StaleStatisticsCheck.MinRows</c>): that many live rows, or that many read
    /// by sequential scans that read that many on average - the one sign rows read give of the table's size (W5-V1 review).
    /// Bound into <see cref="TablesSql" />, which judges each partition against it alone.
    /// </summary>
    public const long UnanalyzedMinRows = 1_000;

    /// <summary>Every index on the listed tables (<see cref="IndexesSql" />).</summary>
    public static HealthQuery Indexes(IReadOnlyList<HealthTableTarget> tables) => new(IndexesSql, TargetParameters(tables));

    /// <summary>The deleted share of each listed soft-deleted table (<see cref="SoftDeleteSql" />).</summary>
    public static HealthQuery SoftDeleteShares(IReadOnlyList<HealthTableTarget> tables) => new(SoftDeleteSql, TargetParameters(tables));

    /// <summary>Whether <c>mt_hilo</c> exists in <paramref name="schema" /> (<see cref="HiloExistsSql" />).</summary>
    public static HealthQuery HiloExists(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        return new HealthQuery(
            HiloExistsSql,
            [
                new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = schema },
                new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = HiloTable },
            ]);
    }

    /// <summary>
    /// The <c>mt_hilo</c> rows of the named sequences - asked only once <see cref="HiloExists" /> said the
    /// table is there, because reading a table that is not is a <c>42P01</c>, and creating it is exactly hard
    /// rule 14's trap.
    /// </summary>
    /// <remarks>
    /// The one list-path statement that reads a row of a user table, and so takes an <c>AccessShareLock</c> on
    /// <c>mt_hilo</c> - and on its primary key, which the planner opens to plan it - for its moment: a table of one
    /// row per HiLo sequence, under the session's three-second <c>lock_timeout</c>. The live sweep test names it
    /// as the only lock beyond the catalog's.
    /// </remarks>
    public static HealthQuery HiloValues(string schema, IReadOnlyList<string> entityNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(entityNames);

        return new HealthQuery(
            "select h.entity_name::text, h.hi_value::bigint\nfrom " + SqlIdentifier.Qualify(schema, HiloTable) + " as h\n"
            + "where h.entity_name = any(@names)\norder by 1",
            [new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = entityNames as string[] ?? [.. entityNames] }]);
    }

    /// <summary>
    /// A bounded sample of one document table's stored document sizes, for the deeper checks (T09):
    /// <c>TABLESAMPLE SYSTEM</c> over the percentage of its pages, and never more than
    /// <paramref name="limit" /> rows.
    /// </summary>
    /// <remarks>
    /// A data read, so tier 2 only - behind "Run deeper checks" and cached for five minutes - and it holds an
    /// <c>AccessShareLock</c> on the one table while it runs. Never asked of a partitioned table, whose sample
    /// would lock every partition at once, and never of a hidden type's table, whose name would then be in
    /// the statement's text. <c>pg_column_size</c> reads the stored size from the TOAST pointer and
    /// decompresses nothing.
    /// </remarks>
    public static HealthQuery DocumentSizeSample(string schema, string table, float percent, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100f);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return new HealthQuery(
            "select pg_catalog.count(*)::int,\n"
            + "       (pg_catalog.percentile_cont(0.95) within group (order by pg_catalog.pg_column_size(d.data)))::float8,\n"
            + "       pg_catalog.max(pg_catalog.pg_column_size(d.data))::bigint\n"
            + "from (select t.data from " + SqlIdentifier.Qualify(schema, table) + " as t tablesample system (@percent) limit @limit) as d",
            [
                new NpgsqlParameter("percent", NpgsqlDbType.Real) { Value = percent },
                new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit },
            ]);
    }

    /// <summary>Maps one row of <see cref="TablesSql" />.</summary>
    public static HealthTableRow ReadTable(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthTableRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Kind = reader.GetString(2),
            EstimatedRows = reader.GetInt64(3),
            HeapBytes = reader.GetInt64(4),
            ToastBytes = reader.GetInt64(5),
            IndexBytes = reader.GetInt64(6),
            LiveRows = NullableInt64(reader, 7),
            DeadRows = NullableInt64(reader, 8),
            SequentialScans = NullableInt64(reader, 9),
            SequentialRowsRead = NullableInt64(reader, 10),
            IndexScans = NullableInt64(reader, 11),
            Inserts = NullableInt64(reader, 12),
            Updates = NullableInt64(reader, 13),
            Deletes = NullableInt64(reader, 14),
            HotUpdates = NullableInt64(reader, 15),
            ModifiedSinceAnalyze = NullableInt64(reader, 16),
            LastVacuum = NullableTimestamp(reader, 17),
            LastAutovacuum = NullableTimestamp(reader, 18),
            LastAnalyze = NullableTimestamp(reader, 19),
            LastAutoanalyze = NullableTimestamp(reader, 20),
            OldestXidAge = NullableInt64(reader, 21),
            InvalidPartitionIndexes = reader.GetInt32(22),
            StorageOptions = reader.IsDBNull(23) ? [] : reader.GetFieldValue<string[]>(23),
            StatsReset = NullableTimestamp(reader, 24),
            AutovacuumEnabled = reader.IsDBNull(25) || reader.GetBoolean(25),
            MaxParallelWorkersPerGather = reader.IsDBNull(26) ? 0 : reader.GetInt32(26),
            PartitionLeaves = reader.GetInt32(27),
            LargestLeafRows = NullableInt64(reader, 28),
            DifferingStorageOptions = reader.IsDBNull(29) ? [] : reader.GetFieldValue<string[]>(29),
            PartitionBuildPhase = reader.IsDBNull(30) ? null : reader.GetString(30),
            UnseenIndexBuilds = reader.GetInt32(31),
            FreezeMaxAge = reader.GetInt64(32),
            ServerVersion = reader.GetInt32(33),
            BuildingPartitionIndexes = reader.GetInt32(34),
            TreeBuildPhase = reader.IsDBNull(35) ? null : reader.GetString(35),
            IndexRowsFetched = NullableInt64(reader, 36),
            Vacuum = Unit(reader, 37),
            Insert = Unit(reader, 38),
            Analyze = Unit(reader, 39),
            VacuumMaxThreshold = UnitNumber(reader, 37, "max"),
            UnfrozenShare = UnitNumber(reader, 38, "unfrozen") ?? 1,
            PastVacuumTrigger = reader.GetInt32(40),
            PastInsertTrigger = reader.GetInt32(41),
            PastAnalyzeTrigger = reader.GetInt32(42),
            AutovacuumOffLeaves = reader.GetInt32(43),
            ToastAutovacuumOffLeaves = reader.GetInt32(44),
            UnanalyzedLeaves = reader.GetInt32(45),
            UnanalyzedLiveRows = reader.GetInt64(46),
            UnanalyzedRowsRead = reader.GetInt64(47),
            ParentEstimatedRows = NullableInt64(reader, 48),
            ToastStorageOptions = reader.IsDBNull(49) ? [] : reader.GetFieldValue<string[]>(49),
            VacuumCount = NullableInt64(reader, 50),
            AutovacuumCount = NullableInt64(reader, 51),
            AnalyzeCount = NullableInt64(reader, 52),
            AutoanalyzeCount = NullableInt64(reader, 53),
            TotalVacuumMilliseconds = reader.IsDBNull(54) ? null : reader.GetDouble(54),
            TotalAnalyzeMilliseconds = reader.IsDBNull(55) ? null : reader.GetDouble(55),
            TrackCounts = reader.IsDBNull(56) || reader.GetBoolean(56),
            UnseenVacuums = reader.GetInt32(57),
        };
    }

    /// <summary>One of <see cref="TablesSql" />'s autovacuum decisions, from the <c>jsonb</c> object it builds, or <see langword="null" />.</summary>
    private static AutovacuumUnit? Unit(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseUnit(reader.GetString(ordinal));

    /// <summary>
    /// A decision as <see cref="TablesSql" /> writes it: <c>counter</c>, <c>trigger</c>, <c>threshold</c>, <c>scale</c>,
    /// <c>tuples</c>, <c>last</c> (epoch seconds, or null) and <c>running</c>.
    /// </summary>
    internal static AutovacuumUnit ParseUnit(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new AutovacuumUnit
        {
            Counter = (long) Number(root, "counter"),
            Trigger = Number(root, "trigger"),
            Threshold = Number(root, "threshold"),
            ScaleFactor = Number(root, "scale"),
            Tuples = Number(root, "tuples"),
            Last = root.TryGetProperty("last", out JsonElement last) && last.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.UnixEpoch.AddTicks((long) Math.Round(last.GetDouble() * TimeSpan.TicksPerSecond))
                : null,
            Running = root.TryGetProperty("running", out JsonElement running) && running.ValueKind == JsonValueKind.True,
            Locked = root.TryGetProperty("locked", out JsonElement locked) && locked.ValueKind == JsonValueKind.True,
        };

        static double Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : 0;
    }

    /// <summary>One number of a decision's object - <c>max</c>, <c>unfrozen</c> - or <see langword="null" /> when it is absent or null.</summary>
    private static double? UnitNumber(NpgsqlDataReader reader, int ordinal, string name)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(reader.GetString(ordinal));

        return document.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    /// <summary>Maps one row of <see cref="IndexesSql" />.</summary>
    public static HealthIndexRow ReadIndex(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthIndexRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Name = reader.GetString(2),
            AccessMethod = reader.GetString(3),
            Definition = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            Columns = reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
            KeyColumnCount = reader.GetInt32(6),
            OperatorClasses = reader.IsDBNull(7) ? [] : reader.GetFieldValue<uint[]>(7),
            Collations = reader.IsDBNull(8) ? [] : reader.GetFieldValue<uint[]>(8),
            Options = reader.IsDBNull(9) ? [] : reader.GetFieldValue<short[]>(9),
            IsUnique = reader.GetBoolean(10),
            IsPrimaryKey = reader.GetBoolean(11),
            IsValid = reader.GetBoolean(12),
            Bytes = reader.GetInt64(13),
            Scans = NullableInt64(reader, 14),
            ReferencedColumns = reader.IsDBNull(15) ? [] : reader.GetFieldValue<string[]>(15),
            BuildPhase = reader.IsDBNull(16) ? null : reader.GetString(16),
            UnseenIndexBuilds = reader.GetInt32(17),
            ServerVersion = reader.GetInt32(18),
            IsExclusion = reader.GetBoolean(19),
            IsImmediate = reader.GetBoolean(20),
            NullsNotDistinct = reader.GetBoolean(21),
            TableBuildPhase = reader.IsDBNull(22) ? null : reader.GetString(22),
            TableLocked = !reader.IsDBNull(23) && reader.GetBoolean(23),
            KeyAttributes = reader.IsDBNull(24) ? [] : reader.GetFieldValue<short[]>(24),
            IsPartial = !reader.IsDBNull(25) && reader.GetBoolean(25),
            KeyWidths = reader.IsDBNull(26) ? [] : reader.GetFieldValue<short[]>(26),
            KeyTypes = reader.IsDBNull(27) ? [] : reader.GetFieldValue<string[]>(27),
            KeyAverageWidths = reader.IsDBNull(28) ? [] : reader.GetFieldValue<int[]>(28),
            EstimatedTuples = reader.IsDBNull(29) ? null : reader.GetDouble(29),
            LastScan = NullableTimestamp(reader, 30),
            CountersReset = NullableTimestamp(reader, 31),
            IndexOptions = reader.IsDBNull(32) ? [] : reader.GetFieldValue<string[]>(32),
        };
    }

    /// <summary>Maps one row of <see cref="SoftDeleteSql" />.</summary>
    public static SoftDeleteShareRow ReadSoftDeleteShare(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new SoftDeleteShareRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.IsDBNull(4) ? null : reader.GetDouble(4));
    }

    /// <summary>Maps one row of <see cref="HiloValues" />.</summary>
    public static HiloRow ReadHilo(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HiloRow(reader.GetString(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
    }

    /// <summary>Maps the one row of <see cref="DocumentSizeSample" />.</summary>
    public static DocumentSizeSampleRow ReadDocumentSizeSample(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DocumentSizeSampleRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static NpgsqlParameter[] TargetParameters(IReadOnlyList<HealthTableTarget> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        string[] schemas = new string[tables.Count];
        string[] names = new string[tables.Count];

        for (int i = 0; i < tables.Count; i++)
        {
            schemas[i] = tables[i].Schema;
            names[i] = tables[i].Table;
        }

        return
        [
            new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas },
            new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names },
        ];
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
