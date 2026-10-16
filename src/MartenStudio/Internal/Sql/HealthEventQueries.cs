using System.Globalization;
using System.Text;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>One of the event store's tables, as the catalog describes it.</summary>
/// <param name="Name">The table's name - one of Marten's own constants.</param>
/// <param name="Kind"><c>pg_class.relkind</c>: <c>r</c> for a table, <c>p</c> for a partitioned one.</param>
/// <param name="Columns">Its columns, in order.</param>
/// <param name="Partitions">How many partitions hang directly off it; 0 for a plain table.</param>
internal sealed record EventStoreRelation(string Name, string Kind, IReadOnlyList<string> Columns, int Partitions)
{
    /// <summary>Whether the table is partitioned.</summary>
    public bool IsPartitioned => string.Equals(Kind, "p", StringComparison.Ordinal);

    /// <summary>Whether the table has <paramref name="column" />.</summary>
    public bool Has(string column) => Columns.Contains(column, StringComparer.Ordinal);
}

/// <summary>
/// One relation of the event store's tables and what <c>pg_stats</c> holds for one of its columns.
/// </summary>
/// <param name="Root">Which table this row is about - <c>mt_events</c> or <c>mt_streams</c>.</param>
/// <param name="IsRoot">Whether this is the table itself, rather than one of its partitions.</param>
/// <param name="Relation">The relation's oid - what tells two partitions apart without naming either.</param>
/// <param name="RelationKind"><c>pg_class.relkind</c> of this relation.</param>
/// <param name="EstimatedRows"><c>pg_class.reltuples</c>: <c>-1</c> (PostgreSQL 14+) before the first analyse.</param>
/// <param name="Leaves">How many plain tables the root's tree holds - 1 for an unpartitioned table.</param>
/// <param name="LeafRows">The leaves' <c>reltuples</c>, summed over every leaf rather than only the ones read.</param>
/// <param name="AnalyzedAt">When this relation was last analysed, by hand or by autovacuum.</param>
/// <param name="Readable">Whether the reading role may select from it - <c>pg_stats</c> hides what it may not.</param>
/// <param name="Column">The column, or <see langword="null" /> when <c>pg_stats</c> has nothing for this relation.</param>
/// <param name="Inherited">Whether the statistics cover the relation's partitions too (only a root analysed by hand has these).</param>
/// <param name="NullFraction"><c>null_frac</c>.</param>
/// <param name="DistinctValues"><c>n_distinct</c>: a count, or a negative fraction of the rows.</param>
/// <param name="CommonValues"><c>most_common_vals</c>, as text.</param>
/// <param name="CommonFrequencies"><c>most_common_freqs</c>, one per value.</param>
/// <param name="Histogram"><c>histogram_bounds</c>, as text: equal-population buckets over the rest.</param>
internal sealed record EventStatisticsRow(
    string Root,
    bool IsRoot,
    long Relation,
    string RelationKind,
    double EstimatedRows,
    int Leaves,
    double LeafRows,
    DateTimeOffset? AnalyzedAt,
    bool Readable,
    string? Column,
    bool Inherited,
    double NullFraction,
    double DistinctValues,
    IReadOnlyList<string> CommonValues,
    IReadOnlyList<double> CommonFrequencies,
    IReadOnlyList<string> Histogram);

/// <summary>
/// One row of the recent-traffic profile: the window's totals, one event type, one stream or one tenant.
/// </summary>
/// <param name="Kind"><see cref="HealthEventQueries.TotalRow" />, <c>type</c>, <c>stream</c> or <c>tenant</c>.</param>
/// <param name="Name">The event type, or the stream id as text; <see langword="null" /> for the totals and a tenant.</param>
/// <param name="TenantId">The stream's or the tenant row's tenant; <see langword="null" /> for the totals and a type.</param>
/// <param name="Events">How many events of the window the row counts.</param>
/// <param name="Bytes">Their payloads' stored size, summed.</param>
/// <param name="MedianBytes">The median payload size - totals only.</param>
/// <param name="P95Bytes">The 95th-percentile payload size - totals and types.</param>
/// <param name="MaxBytes">The largest payload.</param>
/// <param name="FirstSequence">The lowest sequence number counted.</param>
/// <param name="LastSequence">The highest sequence number counted.</param>
/// <param name="FirstAt">The earliest append counted - totals and streams.</param>
/// <param name="LastAt">The latest append counted - totals and streams.</param>
/// <param name="Tombstones">How many of them are Marten's tombstones - totals and tenants.</param>
/// <param name="Archived">How many of them belong to archived streams.</param>
/// <param name="Tenants">How many tenants appended in the window - totals only.</param>
/// <param name="Transactions">
/// How many transactions wrote the row's events - streams only: the distinct <c>xmin</c> of its rows in the window.
/// One <c>SaveChanges</c> is one transaction however many events it appends.
/// </param>
internal sealed record TrafficProfileRow(
    string Kind,
    string? Name,
    string? TenantId,
    long Events,
    long Bytes,
    long? MedianBytes,
    long? P95Bytes,
    long? MaxBytes,
    long? FirstSequence,
    long? LastSequence,
    DateTimeOffset? FirstAt,
    DateTimeOffset? LastAt,
    long? Tombstones,
    long? Archived,
    long? Tenants,
    long? Transactions = null);

/// <summary>What the traffic profile's statement has to know about <c>mt_events</c> before it is built.</summary>
/// <param name="EventSchema">The event store's schema.</param>
/// <param name="TombstoneType">The <c>type</c> Marten stamps on a tombstone in this store (a parameter).</param>
/// <param name="HasBinaryData">Whether <c>mt_events</c> has <c>bdata</c>, whose size then counts too.</param>
/// <param name="HasIsArchived">Whether <c>mt_events</c> has <c>is_archived</c>.</param>
/// <param name="ByTenant">Whether to add the per-tenant rows - a conjoined event store only.</param>
internal sealed record TrafficProfileShape(
    string EventSchema,
    string TombstoneType,
    bool HasBinaryData,
    bool HasIsArchived,
    bool ByTenant);

/// <summary>One gap the high-water detector skipped, with the server's clock when it was read.</summary>
/// <param name="ServerNow">The server's <c>transaction_timestamp()</c>, which every age is measured against.</param>
/// <param name="Ending">The mark the detector moved to, or <see langword="null" /> on the row that only carries the clock.</param>
/// <param name="Starting">The mark it moved from.</param>
/// <param name="At">When, in server time.</param>
internal sealed record HighWaterSkipRow(DateTimeOffset ServerNow, long? Ending, long? Starting, DateTimeOffset? At);

/// <summary>One progression row as D08 reads it, with the server's clock.</summary>
/// <param name="ServerNow">The server's <c>transaction_timestamp()</c>.</param>
/// <param name="Name">The row's name, or <see langword="null" /> on the row that only carries the clock.</param>
/// <param name="Sequence"><c>last_seq_id</c>.</param>
/// <param name="LastUpdated"><c>last_updated</c>.</param>
/// <param name="Mode"><c>mode</c>, or <see langword="null" /> when the table has no such column.</param>
internal sealed record ProgressionModeRow(DateTimeOffset ServerNow, string? Name, long? Sequence, DateTimeOffset? LastUpdated, string? Mode);

/// <summary>What the statistics collector says about <c>mt_dcb_tag_version</c>.</summary>
/// <param name="EstimatedRows"><c>pg_class.reltuples</c>.</param>
/// <param name="LiveRows">The collector's live-row count.</param>
/// <param name="Updates">Updates since the statistics were last reset.</param>
/// <param name="HotUpdates">Of them, heap-only (HOT) updates.</param>
/// <param name="StorageOptions"><c>pg_class.reloptions</c> - where a <c>fillfactor</c> shows.</param>
/// <param name="StatsReset">When this database's statistics were last reset, or <see langword="null" /> for never.</param>
internal sealed record DcbTagVersionRow(
    double EstimatedRows,
    long LiveRows,
    long Updates,
    long HotUpdates,
    IReadOnlyList<string> StorageOptions,
    DateTimeOffset? StatsReset);

/// <summary>
/// The statements the event health checks (E02-E08) and the daemon's D07 and D08 read with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where every name comes from.</b> The event store's schema is <c>StoreOptions.Events.DatabaseSchemaName</c>,
/// quoted through <see cref="SqlIdentifier" />; every table name is one of Marten's own constants; every value -
/// a table name the catalog is asked about, a tombstone's type, a shard identity, a window size - is a
/// parameter. Every statement is built fresh on every call, because an <see cref="NpgsqlParameter" /> belongs to
/// one command.
/// </para>
/// <para>
/// <b>What each one reads, and so what it locks.</b> They run inside the health runner's read-only transaction
/// with <c>search_path</c> pinned to <c>pg_catalog</c>, so everything Marten owns is schema-qualified here.
/// <see cref="EventStoreShape" />, <see cref="EventStatistics" /> and <see cref="DcbTagVersion" /> read the
/// catalog, <c>pg_stats</c> and the statistics collector's own functions, and touch no event table - which is what
/// keeps E05-E08 at tier 1. <see cref="HighWaterSkips" /> reads the newest rows of <c>mt_high_water_skips</c> by
/// its primary key and <see cref="ProgressionModes" /> a few dozen rows of <c>mt_event_progression</c>, each taking
/// the <c>AccessShareLock</c> a plain read takes on what it reads. <see cref="TrafficProfile" /> is the one tier-2
/// statement: one backward scan of <c>mt_events</c>' primary key, cut by a <c>limit</c>. None calls a function that locks what it lists
/// (D27: no <c>pg_partition_*</c>, no size function) - a partition tree is walked through <c>pg_inherits</c>.
/// </para>
/// </remarks>
internal static class HealthEventQueries
{
    /// <summary>The events table.</summary>
    public const string EventsTable = EventTableInfo.EventsTable;

    /// <summary>The streams table.</summary>
    public const string StreamsTable = EventTableInfo.StreamsTable;

    /// <summary>The progression table.</summary>
    public const string ProgressionTable = ProjectionProgressQueries.ProgressionTable;

    /// <summary>
    /// Every gap the high-water detector skipped (<c>EventProgressionSkippingTable.Name</c>, verified at
    /// <c>V9.39.0</c> and <c>V9.50.2</c>): <c>ending_sequence</c> (primary key), <c>starting_sequence</c> and a
    /// <c>timestamp</c> defaulting to <c>transaction_timestamp()</c>. Created only under
    /// <c>Events.EnableAdvancedAsyncTracking</c>.
    /// </summary>
    public const string HighWaterSkipsTable = "mt_high_water_skips";

    /// <summary>
    /// The DCB side table (<c>DcbTagVersionTable</c>, #4591): one row per tag value, its <c>version</c> bumped on
    /// every boundary save. Created only when at least one tag type is registered.
    /// </summary>
    public const string DcbTagVersionTable = "mt_dcb_tag_version";

    /// <summary>The store-global high-water mark's progression row.</summary>
    public const string HighWaterMarkRow = "HighWaterMark";

    /// <summary>The traffic profile's <see cref="TrafficProfileRow.Kind" /> for the window's totals.</summary>
    public const string TotalRow = "total";

    /// <summary>The traffic profile's <see cref="TrafficProfileRow.Kind" /> for one event type.</summary>
    public const string TypeRow = "type";

    /// <summary>The traffic profile's <see cref="TrafficProfileRow.Kind" /> for one stream.</summary>
    public const string StreamRow = "stream";

    /// <summary>The traffic profile's <see cref="TrafficProfileRow.Kind" /> for one tenant.</summary>
    public const string TenantRow = "tenant";

    /// <summary>
    /// How many events the recent-traffic profile reads: the newest this many, as one backward scan of the primary
    /// key cut by a <c>limit</c>. Bounded in rows by this and in time by <c>statement_timeout</c>.
    /// </summary>
    public const long TrafficWindow = 100_000;

    /// <summary>How many event types the profile returns, busiest first.</summary>
    public const int MaxTypeRows = 50;

    /// <summary>How many streams the profile returns, busiest first.</summary>
    public const int MaxStreamRows = 10;

    /// <summary>How many tenants the profile returns, busiest first.</summary>
    public const int MaxTenantRows = 20;

    /// <summary>
    /// How many partitions of one table <see cref="EventStatistics" /> reads the statistics of, largest first, so
    /// a store with a partition per tenant stays one bounded answer. The totals still cover every partition.
    /// </summary>
    public const int MaxStatisticsRelations = 200;

    /// <summary>How many skips <see cref="HighWaterSkips" /> returns, newest first.</summary>
    public const int MaxSkipRows = 100;

    /// <summary>How many progression rows <see cref="ProgressionModes" /> returns.</summary>
    public const int MaxProgressionRows = 1_000;

    /// <summary>The <c>mt_events</c> columns <see cref="EventStatistics" /> reads <c>pg_stats</c> for.</summary>
    internal static readonly string[] EventStatisticsColumns = ["type", "mt_dotnet_type", "is_archived"];

    /// <summary>The <c>mt_streams</c> columns <see cref="EventStatistics" /> reads <c>pg_stats</c> for.</summary>
    internal static readonly string[] StreamStatisticsColumns = ["version", "is_archived"];

    /// <summary>The tables <see cref="EventStoreShape" /> asks the catalog about.</summary>
    internal static readonly string[] ShapeTables =
        [EventsTable, StreamsTable, ProgressionTable, HighWaterSkipsTable, DcbTagVersionTable];

    /// <summary>The tables <see cref="EventStatistics" /> reads <c>pg_stats</c> for.</summary>
    internal static readonly string[] StatisticsTables = [EventsTable, StreamsTable];

    /// <summary>
    /// Which of the event store's tables exist, their kind, their columns and how many partitions each has: a
    /// catalog read, so every check can answer "not here" as a value before it builds a statement against a
    /// table or a column that is not there.
    /// </summary>
    /// <param name="eventSchema">The event store's schema.</param>
    /// <remarks>Columns: <c>relname</c>, <c>relkind</c>, <c>columns</c> (<c>text[]</c>), <c>partitions</c>.</remarks>
    public static HealthQuery EventStoreShape(string eventSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);

        const string Sql =
            """
            select c.relname::text as relname,
                   c.relkind::text as relkind,
                   coalesce(
                     (select pg_catalog.array_agg(a.attname::text order by a.attnum)
                        from pg_catalog.pg_attribute a
                       where a.attrelid = c.oid and a.attnum > 0 and not a.attisdropped),
                     '{}'::text[]) as columns,
                   (select pg_catalog.count(*) from pg_catalog.pg_inherits i where i.inhparent = c.oid)::integer as partitions
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where n.nspname = @schema
               and c.relname = any(@tables)
               and c.relkind in ('r', 'p')
             order by c.relname
            """;

        return new HealthQuery(Sql,
        [
            Text("schema", eventSchema),
            TextArray("tables", ShapeTables),
        ]);
    }

    /// <summary>Reads one row of <see cref="EventStoreShape" />.</summary>
    public static EventStoreRelation ReadRelation(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new EventStoreRelation(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? [] : reader.GetFieldValue<string[]>(2),
            reader.GetInt32(3));
    }

    /// <summary>
    /// What <c>pg_stats</c> holds for <c>mt_events</c> (<see cref="EventStatisticsColumns" />) and
    /// <c>mt_streams</c> (<see cref="StreamStatisticsColumns" />), per relation: the table itself and, for a
    /// partitioned one, its largest <see cref="MaxStatisticsRelations" /> partitions - autovacuum analyses the
    /// partitions and never the partitioned parent.
    /// </summary>
    /// <param name="eventSchema">The event store's schema.</param>
    /// <remarks>
    /// <para>
    /// No scan of either table: <c>pg_stats</c> is the planner's sample from the last <c>ANALYZE</c>, and
    /// <c>pg_class.reltuples</c> its estimate of the row count. It is what lets E05, E06 and E07 run at every
    /// interval for the price of a catalog read. A relation with no statistics at all still answers one row,
    /// with a <see langword="null" /> column, so its row count and when it was analysed are known.
    /// </para>
    /// <para>
    /// A partition's name is never returned: under per-tenant partitioning it spells the tenant. A row says only
    /// which table it belongs to, whether it is that table itself, and its size.
    /// </para>
    /// <para>
    /// Columns in order: <c>root</c>, <c>is_root</c>, <c>relation</c>, <c>relkind</c>, <c>reltuples</c>, <c>leaves</c>,
    /// <c>leaf_rows</c>, <c>analyzed_at</c>, <c>readable</c>, <c>attname</c>, <c>inherited</c>,
    /// <c>null_frac</c>, <c>n_distinct</c>, <c>common_values</c>, <c>common_frequencies</c>, <c>histogram</c>.
    /// </para>
    /// </remarks>
    public static HealthQuery EventStatistics(string eventSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);

        const string Sql =
            """
            with recursive roots as (
              select c.oid, c.relname::text as root
                from pg_catalog.pg_class c
                join pg_catalog.pg_namespace n on n.oid = c.relnamespace
               where n.nspname = @schema
                 and c.relname = any(@tables)
                 and c.relkind in ('r', 'p')
            ),
            tree as (
              select r.oid, r.root, true as is_root from roots r
              union all
              select i.inhrelid, t.root, false from pg_catalog.pg_inherits i join tree t on t.oid = i.inhparent
            ),
            rels as (
              select t.root,
                     t.is_root,
                     c.oid,
                     c.relname,
                     c.relkind,
                     c.reltuples::float8 as reltuples,
                     n.nspname,
                     pg_catalog.count(*) filter (where c.relkind = 'r') over (partition by t.root) as leaves,
                     pg_catalog.sum(case when c.relkind = 'r' and c.reltuples > 0 then c.reltuples::float8 else 0 end)
                       over (partition by t.root) as leaf_rows,
                     pg_catalog.row_number() over (partition by t.root, c.relkind = 'r' order by c.reltuples desc, c.oid) as size_rank
                from tree t
                join pg_catalog.pg_class c on c.oid = t.oid
                join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            ),
            kept as (
              select r.* from rels r where r.is_root or (r.relkind = 'r' and r.size_rank <= @max_relations)
            )
            select k.root,
                   k.is_root,
                   k.oid::bigint as relation,
                   k.relkind::text as relkind,
                   k.reltuples,
                   k.leaves::integer as leaves,
                   k.leaf_rows,
                   greatest(pg_catalog.pg_stat_get_last_analyze_time(k.oid), pg_catalog.pg_stat_get_last_autoanalyze_time(k.oid)) as analyzed_at,
                   pg_catalog.has_table_privilege(k.oid, 'SELECT') as readable,
                   s.attname::text as attname,
                   coalesce(s.inherited, false) as inherited,
                   coalesce(s.null_frac, 0)::float8 as null_frac,
                   coalesce(s.n_distinct, 0)::float8 as n_distinct,
                   s.most_common_vals::text::text[] as common_values,
                   s.most_common_freqs::float8[] as common_frequencies,
                   s.histogram_bounds::text::text[] as histogram
              from kept k
              left join pg_catalog.pg_stats s
                on s.schemaname = k.nspname
               and s.tablename = k.relname
               and s.attname = any(case when k.root = @events then @event_columns else @stream_columns end)
             order by k.root, k.is_root desc, k.reltuples desc, k.oid, s.attname, s.inherited
            """;

        return new HealthQuery(Sql,
        [
            Text("schema", eventSchema),
            TextArray("tables", StatisticsTables),
            Text("events", EventsTable),
            TextArray("event_columns", EventStatisticsColumns),
            TextArray("stream_columns", StreamStatisticsColumns),
            Integer("max_relations", MaxStatisticsRelations),
        ]);
    }

    /// <summary>Reads one row of <see cref="EventStatistics" />.</summary>
    public static EventStatisticsRow ReadStatistics(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new EventStatisticsRow(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetDouble(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
            NullableTimestamp(reader, 7),
            !reader.IsDBNull(8) && reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetBoolean(10),
            reader.GetDouble(11),
            reader.GetDouble(12),
            reader.IsDBNull(13) ? [] : reader.GetFieldValue<string[]>(13),
            reader.IsDBNull(14) ? [] : reader.GetFieldValue<double[]>(14),
            reader.IsDBNull(15) ? [] : reader.GetFieldValue<string[]>(15));
    }

    /// <summary>
    /// The recent-traffic profile: the newest <see cref="TrafficWindow" /> events of <c>mt_events</c>, read once,
    /// and folded in the database into the window's totals, its event types, its busiest streams and - on a
    /// conjoined store - its busiest tenants.
    /// </summary>
    /// <param name="shape">What the table has, and the store's tombstone type.</param>
    /// <remarks>
    /// <para>
    /// <b>One bounded read.</b> <c>order by seq_id desc limit @window</c> is an Index Scan Backward of the primary
    /// key on a plain table, and a Merge Append of one per partition on an archive-partitioned one, stopped after
    /// <see cref="TrafficWindow" /> rows: the planner sees the bound. It must not be written as
    /// <c>seq_id &gt; (select max) - window</c>, whose bound is an InitPlan value the planner cannot see - once
    /// archiving in place had lowered the table's correlation, that form was planned as a Seq Scan of the whole of
    /// <c>mt_events</c> (measured by the H6 review on PostgreSQL 17 at 3M rows, and pinned by
    /// <c>EventTrafficPlanLiveTests</c>, which fails on any Seq Scan). The window is a <c>materialized</c> CTE read
    /// by every branch, so the table is read once, and the statement runs under the runner's
    /// <c>statement_timeout</c>. Payload sizes are <c>pg_column_size</c> - the stored, possibly compressed, size -
    /// with the percentiles computed in SQL, so no payload leaves the database.
    /// </para>
    /// <para>
    /// <b>Not for a store that partitions its events by tenant</b>, where every tenant numbers its events from one,
    /// so the newest sequence numbers are not the newest events (and the read would reach every partition); the
    /// checks refuse that shape before building this.
    /// </para>
    /// <para>
    /// Tombstones (<c>@tombstone</c>) are counted in the totals and kept out of the stream ranking: Marten files
    /// every tombstone under one stream of its own, which would otherwise always look like the hottest one.
    /// </para>
    /// <para>
    /// <b>Transactions, not events, for a stream's rate.</b> Each stream row counts the distinct <c>xmin</c> of its
    /// rows - the transaction that wrote each one, kept when a row is frozen - so a bulk import that appends a
    /// thousand events in one <c>SaveChanges</c> is one transaction, not a thousand writers. <c>xid</c> has no
    /// ordering operator for <c>count(distinct)</c> to sort by, so it is counted as its number
    /// (<c>xmin::text::bigint</c>). A row rewritten in place since - archiving does that - carries the rewriting
    /// transaction's id instead, and such a stream is archived, which E03 does not report.
    /// </para>
    /// </remarks>
    public static HealthQuery TrafficProfile(TrafficProfileShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentException.ThrowIfNullOrWhiteSpace(shape.EventSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(shape.TombstoneType);

        string events = SqlIdentifier.Qualify(shape.EventSchema, EventsTable);

        StringBuilder sql = new();
        sql.Append("with win as materialized (\n");
        sql.Append("  select e.seq_id,\n");
        sql.Append("         e.type::text as type,\n");
        sql.Append("         e.stream_id::text as stream,\n");
        sql.Append("         e.tenant_id::text as tenant_id,\n");
        sql.Append(shape.HasIsArchived ? "         e.is_archived as archived,\n" : "         false as archived,\n");
        sql.Append("         (pg_catalog.pg_column_size(e.data)");
        if (shape.HasBinaryData)
        {
            sql.Append(" + coalesce(pg_catalog.pg_column_size(e.bdata), 0)");
        }

        sql.Append(")::bigint as bytes,\n");
        sql.Append("         e.\"timestamp\" as appended_at,\n");
        sql.Append("         e.xmin::text::bigint as xact\n");
        sql.Append("    from ").Append(events).Append(" e\n");
        sql.Append("   order by e.seq_id desc\n");
        sql.Append("   limit @window\n");
        sql.Append(")\n");

        sql.Append(
            """
            select 'total'::text as kind,
                   null::text as name,
                   null::text as tenant_id,
                   pg_catalog.count(*) as events,
                   coalesce(pg_catalog.sum(w.bytes), 0)::bigint as bytes,
                   (pg_catalog.percentile_disc(0.5::float8) within group (order by w.bytes))::bigint as median_bytes,
                   (pg_catalog.percentile_disc(0.95::float8) within group (order by w.bytes))::bigint as p95_bytes,
                   pg_catalog.max(w.bytes) as max_bytes,
                   pg_catalog.min(w.seq_id) as first_seq,
                   pg_catalog.max(w.seq_id) as last_seq,
                   pg_catalog.min(w.appended_at) as first_at,
                   pg_catalog.max(w.appended_at) as last_at,
                   pg_catalog.count(*) filter (where w.type = @tombstone) as tombstones,
                   pg_catalog.count(*) filter (where w.archived) as archived,
                   pg_catalog.count(distinct w.tenant_id) as tenants,
                   null::bigint as transactions
              from win w
            union all
            (select 'type', w.type, null::text, pg_catalog.count(*), coalesce(pg_catalog.sum(w.bytes), 0)::bigint, null::bigint,
                    (pg_catalog.percentile_disc(0.95::float8) within group (order by w.bytes))::bigint, pg_catalog.max(w.bytes),
                    pg_catalog.min(w.seq_id), pg_catalog.max(w.seq_id), null::timestamptz, null::timestamptz,
                    pg_catalog.count(*) filter (where w.type = @tombstone), pg_catalog.count(*) filter (where w.archived), null::bigint,
                    null::bigint
               from win w
              group by w.type
              order by 4 desc, 2
              limit @max_types)
            union all
            (select 'stream', w.stream,
            """);

        // A stream is one tenant's on a conjoined store, so it is grouped with its tenant there. Elsewhere every row
        // of one stream is one group whatever tenant_id the rows carry, so one stream is never two rows - and never
        // two findings under one key.
        sql.Append(shape.ByTenant ? " w.tenant_id," : " null::text,");

        sql.Append(
            """
             pg_catalog.count(*), coalesce(pg_catalog.sum(w.bytes), 0)::bigint, null::bigint,
                    null::bigint, pg_catalog.max(w.bytes), pg_catalog.min(w.seq_id), pg_catalog.max(w.seq_id),
                    pg_catalog.min(w.appended_at), pg_catalog.max(w.appended_at), null::bigint,
                    pg_catalog.count(*) filter (where w.archived), null::bigint, pg_catalog.count(distinct w.xact)
               from win w
              where w.type <> @tombstone
            """);

        sql.Append(shape.ByTenant
            ? "\n  group by w.tenant_id, w.stream\n  order by 4 desc, 3, 2\n  limit @max_streams)"
            : "\n  group by w.stream\n  order by 4 desc, 2\n  limit @max_streams)");

        if (shape.ByTenant)
        {
            sql.Append(
                """

                union all
                (select 'tenant', null::text, w.tenant_id, pg_catalog.count(*), coalesce(pg_catalog.sum(w.bytes), 0)::bigint, null::bigint,
                        null::bigint, pg_catalog.max(w.bytes), pg_catalog.min(w.seq_id), pg_catalog.max(w.seq_id),
                        null::timestamptz, null::timestamptz, pg_catalog.count(*) filter (where w.type = @tombstone),
                        pg_catalog.count(*) filter (where w.archived), null::bigint, null::bigint
                   from win w
                  group by w.tenant_id
                  order by 4 desc, 3
                  limit @max_tenants)
                """);
        }

        List<NpgsqlParameter> parameters =
        [
            new NpgsqlParameter("window", NpgsqlDbType.Bigint) { Value = TrafficWindow },
            Text("tombstone", shape.TombstoneType),
            Integer("max_types", MaxTypeRows),
            Integer("max_streams", MaxStreamRows),
        ];

        if (shape.ByTenant)
        {
            parameters.Add(Integer("max_tenants", MaxTenantRows));
        }

        return new HealthQuery(sql.ToString(), parameters);
    }

    /// <summary>Reads one row of <see cref="TrafficProfile" />.</summary>
    public static TrafficProfileRow ReadTraffic(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new TrafficProfileRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
            NullableInt64(reader, 5),
            NullableInt64(reader, 6),
            NullableInt64(reader, 7),
            NullableInt64(reader, 8),
            NullableInt64(reader, 9),
            NullableTimestamp(reader, 10),
            NullableTimestamp(reader, 11),
            NullableInt64(reader, 12),
            NullableInt64(reader, 13),
            NullableInt64(reader, 14),
            NullableInt64(reader, 15));
    }

    /// <summary>
    /// The newest <see cref="MaxSkipRows" /> rows of <c>mt_high_water_skips</c>, by its primary key, and the
    /// server's clock - always at least one row, so the clock is there when no gap was ever skipped.
    /// </summary>
    /// <param name="eventSchema">The event store's schema. The table must exist.</param>
    /// <remarks>
    /// <c>ending_sequence</c> only ever grows - it is the mark the detector moved to, and Marten's
    /// <c>mt_mark_progression_with_skip</c> inserts it with <c>on conflict do nothing</c> - so the newest skips
    /// are the last in its primary key, and this is one backward index scan of at most
    /// <see cref="MaxSkipRows" /> entries however long the table has grown. Columns: <c>server_now</c>,
    /// <c>ending_sequence</c>, <c>starting_sequence</c>, <c>skipped_at</c>.
    /// </remarks>
    public static HealthQuery HighWaterSkips(string eventSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);

        string sql =
            "select pg_catalog.transaction_timestamp() as server_now, k.ending_sequence, k.starting_sequence, k.skipped_at\n"
            + "from (values (1)) as one(x)\n"
            + "left join lateral (\n"
            + "  select s.ending_sequence, s.starting_sequence, s.\"timestamp\" as skipped_at\n"
            + "    from " + SqlIdentifier.Qualify(eventSchema, HighWaterSkipsTable) + " s\n"
            + "   order by s.ending_sequence desc\n"
            + "   limit @limit\n"
            + ") k on true\n"
            + "order by k.ending_sequence desc nulls last";

        return new HealthQuery(sql, [Integer("limit", MaxSkipRows)]);
    }

    /// <summary>Reads one row of <see cref="HighWaterSkips" />.</summary>
    public static HighWaterSkipRow ReadSkip(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HighWaterSkipRow(
            reader.GetFieldValue<DateTimeOffset>(0),
            NullableInt64(reader, 1),
            NullableInt64(reader, 2),
            NullableTimestamp(reader, 3));
    }

    /// <summary>
    /// The progression rows of the named shards and of the store-global high-water mark - position, last write
    /// and <c>mode</c> where the table has it - with the server's clock; always at least one row.
    /// </summary>
    /// <param name="eventSchema">The event store's schema. The progression table must exist.</param>
    /// <param name="hasModeColumn">Whether the table has <c>mode</c> (every table Marten has created since 9.31, #5309).</param>
    /// <param name="shardIdentities">The shards, by <c>ShardName.Identity</c>.</param>
    /// <remarks>Columns: <c>server_now</c>, <c>name</c>, <c>last_seq_id</c>, <c>last_updated</c>, <c>mode</c>.</remarks>
    public static HealthQuery ProgressionModes(string eventSchema, bool hasModeColumn, IReadOnlyCollection<string> shardIdentities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);
        ArgumentNullException.ThrowIfNull(shardIdentities);

        string sql =
            "select pg_catalog.transaction_timestamp() as server_now, p.name::text as name, p.last_seq_id, p.last_updated, "
            + (hasModeColumn ? "p.mode::text as mode" : "null::text as mode") + "\n"
            + "from (values (1)) as one(x)\n"
            + "left join " + SqlIdentifier.Qualify(eventSchema, ProgressionTable) + " p on p.name = any(@names)\n"
            + "order by p.name nulls first\n"
            + "limit " + (MaxProgressionRows + 1).ToString(CultureInfo.InvariantCulture);

        return new HealthQuery(sql, [TextArray("names", [.. shardIdentities, HighWaterMarkRow])]);
    }

    /// <summary>Reads one row of <see cref="ProgressionModes" />.</summary>
    public static ProgressionModeRow ReadProgressionMode(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new ProgressionModeRow(
            reader.GetFieldValue<DateTimeOffset>(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            NullableInt64(reader, 2),
            NullableTimestamp(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    /// <summary>
    /// What the statistics collector says about <c>mt_dcb_tag_version</c>: its size, its update counters and its
    /// storage parameters, with the database's statistics-reset time - a catalog read.
    /// </summary>
    /// <param name="eventSchema">The event store's schema.</param>
    /// <remarks>
    /// The counters come from the collector's own functions (<c>pg_stat_get_tuples_updated</c>,
    /// <c>pg_stat_get_tuples_hot_updated</c>, <c>pg_stat_get_live_tuples</c>), which read shared memory and lock
    /// nothing. No row when the table does not exist. Columns: <c>reltuples</c>, <c>live_rows</c>,
    /// <c>updates</c>, <c>hot_updates</c>, <c>reloptions</c>, <c>stats_reset</c>.
    /// </remarks>
    public static HealthQuery DcbTagVersion(string eventSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);

        const string Sql =
            """
            select c.reltuples::float8 as reltuples,
                   pg_catalog.pg_stat_get_live_tuples(c.oid) as live_rows,
                   pg_catalog.pg_stat_get_tuples_updated(c.oid) as updates,
                   pg_catalog.pg_stat_get_tuples_hot_updated(c.oid) as hot_updates,
                   coalesce(c.reloptions, '{}'::text[]) as reloptions,
                   (select d.stats_reset from pg_catalog.pg_stat_database d where d.datname = pg_catalog.current_database()) as stats_reset
              from pg_catalog.pg_class c
              join pg_catalog.pg_namespace n on n.oid = c.relnamespace
             where n.nspname = @schema
               and c.relname = @table
               and c.relkind in ('r', 'p')
            """;

        return new HealthQuery(Sql, [Text("schema", eventSchema), Text("table", DcbTagVersionTable)]);
    }

    /// <summary>Reads one row of <see cref="DcbTagVersion" />.</summary>
    public static DcbTagVersionRow ReadDcbTagVersion(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DcbTagVersionRow(
            reader.GetDouble(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
            reader.IsDBNull(4) ? [] : reader.GetFieldValue<string[]>(4),
            NullableTimestamp(reader, 5));
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };

    private static NpgsqlParameter TextArray(string name, string[] values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values };

    private static NpgsqlParameter Integer(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };
}
