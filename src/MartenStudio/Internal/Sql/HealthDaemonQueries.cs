using System.Text;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The statements the daemon and high-water health checks (D01-D06, D09) and the high-water Explain read with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where every name comes from.</b> The event store's schema is <c>StoreOptions.Events.DatabaseSchemaName</c>,
/// quoted through <see cref="SqlIdentifier" />; every table name is one of Marten's own constants; every value -
/// a shard identity, a lock key, a timestamp, a JSON property name - is a parameter. Every statement is built
/// fresh on every call, because an <see cref="NpgsqlParameter" /> belongs to one command.
/// </para>
/// <para>
/// <b>What each one locks.</b> They run inside the health runner's read-only transaction with
/// <c>search_path</c> pinned to <c>pg_catalog</c>, so everything Marten owns is schema-qualified here and every
/// function is resolved from <c>pg_catalog</c>. Which of these objects exist is asked first, of the catalog alone,
/// by the statement the event checks share (<see cref="HealthEventQueries.EventStoreShape" />, CL1 item 6). These
/// read <c>mt_event_progression</c> (a few dozen rows, by primary key or whole), the
/// <c>mt_events_sequence</c> sequence, two primary-key probes of <c>mt_events</c> (<c>min(seq_id)</c> above the
/// mark and, for D09, <c>max(seq_id)</c> - a single index descent each) and, for D06, the dead-letter table where the
/// store says it is: each takes the
/// <c>AccessShareLock</c> a plain read takes on what it reads, and nothing it does not read. None calls a
/// function that locks what it lists (D27). <see cref="DaemonLocks" /> and the Explain statements read
/// <c>pg_locks</c> and <c>pg_stat_activity</c>, which are proc-array and lock-table scans: they run at the
/// health cadence or on a click, never on a page's poll.
/// </para>
/// <para>
/// <b>Not for a store that partitions its events by tenant.</b> There the high-water bookkeeping is per tenant
/// and the store-global rows these statements read are not maintained; the checks refuse that shape before
/// building anything (see <c>DaemonCheckConfiguration</c>).
/// </para>
/// </remarks>
internal static class HealthDaemonQueries
{
    /// <summary>The progression table.</summary>
    public const string ProgressionTable = ProjectionProgressQueries.ProgressionTable;

    /// <summary>The event sequence.</summary>
    public const string EventsSequence = ProjectionProgressQueries.EventsSequence;

    /// <summary>The store-global high-water mark's progression row (<c>HighWaterShardIdentity.StoreGlobal</c>).</summary>
    public const string HighWaterMarkRow = "HighWaterMark";

    /// <summary>
    /// Marten's durable stuck-gap clock (<c>HighWaterStuckGap.ProgressionName</c>, #5109): <c>last_seq_id</c> is the
    /// mark the daemon first saw pinned and <c>last_updated</c> when, in server time. Upserted only when the mark
    /// differs, and never deleted - so it describes the current pin only while its mark equals the mark's own row.
    /// </summary>
    public const string StuckGapRow = "HighWaterStuckGap";

    /// <summary>
    /// Marten's durable allocation fence (<c>HighWaterAllocationFence.ProgressionName</c>, #5108): restamped on every
    /// high-water poll while nothing above the mark has been handed out - so, while the store is caught up, a stamp
    /// of the high-water agent being alive.
    /// </summary>
    public const string AllocationFenceRow = "HighWaterAllocationFence";

    /// <summary>The longest list of shard rows one statement returns, so a store of thousands of tenants stays bounded.</summary>
    public const int MaxShardRows = 1_000;

    /// <summary>The most sessions the Explain lists.</summary>
    public const int MaxExplainedSessions = 200;

    /// <summary>The most prepared transactions the Explain lists.</summary>
    public const int MaxExplainedPreparedTransactions = 100;

    /// <summary>How much of a session's current statement the Explain shows (behind <c>ViewDatabaseActivity</c>).</summary>
    public const int ExplainedQueryLength = 200;

    /// <summary>The columns <see cref="ShardRows" /> and <see cref="ProblemShardRows" /> read beside name and position.</summary>
    private static readonly string[] ExtendedColumns =
    [
        "heartbeat",
        "agent_status",
        "pause_reason",
        "failure_category",
        "failure_event_sequence",
        "failure_event_type",
    ];

    /// <summary>The per-row thresholds - created by Marten, written by nothing in Marten (#5173), honoured when set.</summary>
    private static readonly string[] ThresholdColumns = ["warning_behind_threshold", "critical_behind_threshold"];

    /// <summary>
    /// The high-water facts, from one snapshot: the server's clock, the sequence's handed-out ceiling, the mark's
    /// own row, the stuck-gap and allocation-fence rows, the latest write to the table, and the first committed
    /// sequence above the mark.
    /// </summary>
    /// <param name="eventSchema">The event store's schema. The progression table, the sequence and <c>mt_events</c> must exist.</param>
    /// <remarks>
    /// <para>
    /// Columns: <c>server_now</c>, <c>highest_sequence</c>, <c>mark</c>, <c>mark_updated</c>, <c>stuck_mark</c>,
    /// <c>stuck_since</c>, <c>fence_mark</c>, <c>fence_updated</c>, <c>latest_write</c>, <c>next_committed</c>,
    /// <c>allocated_high</c>, <c>highest_committed</c>, <c>sequence_last_value</c>.
    /// </para>
    /// <para>
    /// The last three are D09's. <c>allocated_high</c> is Marten's own reading of the highest number the sequence has handed
    /// out (#5091's <c>AllocatedSequenceHigh</c>, <c>HighWaterStatisticsDetector</c> at V9.39.0, v9.51.0 and V9.54.1):
    /// <c>last_value</c> once called, <c>last_value - 1</c> before - which a sequence restarted at <c>n</c> reads as
    /// <c>n - 1</c>, where <c>highest_sequence</c> reads 0 and would call the next number already passed.
    /// <c>highest_committed</c> is <c>max(seq_id)</c>, the other end of the same primary key the <c>next_committed</c> probe
    /// descends: one backward index descent, under the <c>AccessShareLock</c> that probe already takes.
    /// <c>sequence_last_value</c> is the raw <c>last_value</c>, which is what Marten's own correction compares the mark with
    /// and moves progression to (its <c>HighestSequence</c>), so D09 can tell when that correction has nothing left to do.
    /// </para>
    /// <para>
    /// <c>highest_sequence</c> is <c>case when is_called then last_value else 0 end</c>, the projections screen's
    /// own reading: a fresh sequence's <c>last_value</c> of 1 is the value the next <c>nextval</c> returns, not one
    /// handed out. <c>next_committed</c> is <c>min(seq_id)</c> above the mark, which Postgres answers with one descent
    /// of <c>mt_events</c>' primary key: <c>mark + 1</c> means the next event is committed and the mark is not held
    /// by a gap; anything higher, or nothing, means the numbers just above the mark were handed out and are not (yet)
    /// committed - the gap the daemon holds before.
    /// </para>
    /// </remarks>
    public static HealthQuery HighWater(string eventSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);

        string progression = SqlIdentifier.Qualify(eventSchema, ProgressionTable);
        string sequence = SqlIdentifier.Qualify(eventSchema, EventsSequence);
        string events = SqlIdentifier.Qualify(eventSchema, EventTableInfo.EventsTable);

        // The sequence is read once, as one row: highest_sequence, allocated_high and sequence_last_value are all derived from
        // the same last_value and is_called, so they cannot disagree with one another.
        string sql =
            "select\n"
            + "  pg_catalog.transaction_timestamp() as server_now,\n"
            + "  case when s.is_called then s.last_value else 0 end as highest_sequence,\n"
            + "  h.last_seq_id as mark,\n"
            + "  h.last_updated as mark_updated,\n"
            + "  g.last_seq_id as stuck_mark,\n"
            + "  g.last_updated as stuck_since,\n"
            + "  f.last_seq_id as fence_mark,\n"
            + "  f.last_updated as fence_updated,\n"
            + "  (select pg_catalog.max(p.last_updated) from " + progression + " p) as latest_write,\n"
            + "  (select pg_catalog.min(e.seq_id) from " + events + " e where e.seq_id > h.last_seq_id) as next_committed,\n"
            + "  case when s.is_called then s.last_value else s.last_value - 1 end as allocated_high,\n"
            + "  (select pg_catalog.max(e.seq_id) from " + events + " e) as highest_committed,\n"
            + "  s.last_value as sequence_last_value\n"
            + "from (values (1)) as one(x)\n"
            + "cross join (select q.last_value, q.is_called from " + sequence + " q) as s\n"
            + "left join " + progression + " h on h.name = @mark_row\n"
            + "left join " + progression + " g on g.name = @stuck_row\n"
            + "left join " + progression + " f on f.name = @fence_row";

        return new HealthQuery(sql,
        [
            Text("mark_row", HighWaterMarkRow),
            Text("stuck_row", StuckGapRow),
            Text("fence_row", AllocationFenceRow),
        ]);
    }

    /// <summary>
    /// D09's repair of the event sequence, as text for a DBA to run - never run by the studio: move
    /// <c>mt_events_sequence</c> to the greater of the <c>HighWaterMark</c> row and <c>max(seq_id)</c>, both read when it
    /// runs, and only forward - so the next append takes a number above every committed event and above the mark. Or
    /// <see langword="null" /> for a schema whose name it will not print.
    /// </summary>
    /// <param name="eventSchema">The event store's schema.</param>
    /// <param name="onlyWhileNeverCalled">
    /// Whether it also does nothing once anything has drawn from the sequence (<c>is_called</c>): for the repair after a
    /// <c>RESTART</c>, whose point is the one number nothing has taken yet.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Computed when it runs, not when the sweep read.</b> A number from the snapshot pasted later would wind the
    /// sequence back below events committed in between; the statement reads the mark and the highest event itself, and
    /// <c>t.target &gt; position</c> keeps it from ever moving the sequence back.
    /// </para>
    /// <para>
    /// <b>Names as everywhere here.</b> Each goes through <see cref="SqlIdentifier" />; the sequence is also handed to
    /// <c>setval</c> as a string literal, so a schema whose name holds a single quote or a backslash - which a literal reads
    /// differently under <c>standard_conforming_strings = off</c> - gets no statement, and the remedy says it in words.
    /// </para>
    /// </remarks>
    public static string? SequenceRepair(string eventSchema, bool onlyWhileNeverCalled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);

        if (!MigrationSqlText.IsPrintableName(eventSchema)
            || eventSchema.Contains('\'', StringComparison.Ordinal)
            || eventSchema.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        string sequence = SqlIdentifier.Qualify(eventSchema, EventsSequence);

        return "select pg_catalog.setval('" + sequence + "', t.target)\n"
            + "from " + sequence + " q\n"
            + "cross join lateral (\n"
            + "    select greatest(\n"
            + "        coalesce((select h.last_seq_id from " + SqlIdentifier.Qualify(eventSchema, ProgressionTable)
            + " h where h.name = '" + HighWaterMarkRow + "'), 0),\n"
            + "        coalesce((select pg_catalog.max(e.seq_id) from " + SqlIdentifier.Qualify(eventSchema, EventTableInfo.EventsTable)
            + " e), 0)) as target) t\n"
            + "where t.target > (case when q.is_called then q.last_value else q.last_value - 1 end)"
            + (onlyWhileNeverCalled ? "\n  and not q.is_called;" : ";");
    }

    /// <summary>
    /// The progression rows of the named shards: position, last write and, where the table has them and the store
    /// writes them, the extended-tracking columns and the per-row thresholds.
    /// </summary>
    /// <param name="eventSchema">The event store's schema.</param>
    /// <param name="physicalColumns">What the catalog says <c>mt_event_progression</c> has (<see cref="HealthEventQueries.EventStoreShape" />).</param>
    /// <param name="extendedProgressionTracking">
    /// <c>Events.EnableExtendedProgressionTracking</c>. Marten creates the extended columns on every migrated table
    /// and writes them only under the flag, so without it they are not asked for (the projections screen's rule).
    /// </param>
    /// <param name="shardIdentities">The shards, by <c>ShardName.Identity</c>.</param>
    /// <remarks>
    /// Columns in order: <c>name</c>, <c>last_seq_id</c>, <c>last_updated</c>, then
    /// <see cref="SelectedShardColumns" /> in that order.
    /// </remarks>
    public static HealthQuery ShardRows(
        string eventSchema,
        IReadOnlyCollection<string> physicalColumns,
        bool extendedProgressionTracking,
        IReadOnlyList<string> shardIdentities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);
        ArgumentNullException.ThrowIfNull(physicalColumns);
        ArgumentNullException.ThrowIfNull(shardIdentities);

        StringBuilder sql = SelectShardColumns(eventSchema, physicalColumns, extendedProgressionTracking);
        sql.Append("where p.name = any(@shards)\n");
        sql.Append("order by p.name\n");
        sql.Append("limit ").Append(MaxShardRows.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new HealthQuery(sql.ToString(),
        [
            new NpgsqlParameter("shards", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = shardIdentities.ToArray() },
        ]);
    }

    /// <summary>
    /// The progression rows the extended tracking says are not running, or carry a failure - what D05 reports, for
    /// every shard the table holds, a tenant's included.
    /// </summary>
    /// <param name="eventSchema">The event store's schema.</param>
    /// <param name="physicalColumns">What the catalog says <c>mt_event_progression</c> has. Must include <c>agent_status</c>.</param>
    /// <remarks>
    /// Only for a store with <c>EnableExtendedProgressionTracking</c> on: without it nothing writes the status. The
    /// high-water bookkeeping rows carry no status and never match. Same columns as <see cref="ShardRows" />.
    /// </remarks>
    public static HealthQuery ProblemShardRows(string eventSchema, IReadOnlyCollection<string> physicalColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);
        ArgumentNullException.ThrowIfNull(physicalColumns);

        StringBuilder sql = SelectShardColumns(eventSchema, physicalColumns, extendedProgressionTracking: true);

        sql.Append("where (p.agent_status is not null and p.agent_status <> @running)");

        if (physicalColumns.Contains("failure_category", StringComparer.Ordinal))
        {
            sql.Append(" or p.failure_category is not null");
        }

        sql.Append('\n');
        sql.Append("order by p.name\n");
        sql.Append("limit ").Append(MaxShardRows.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new HealthQuery(sql.ToString(), [Text("running", "Running")]);
    }

    /// <summary>
    /// The optional columns <see cref="ShardRows" /> and <see cref="ProblemShardRows" /> select after
    /// <c>name</c>, <c>last_seq_id</c> and <c>last_updated</c>, in select order.
    /// </summary>
    internal static IReadOnlyList<string> SelectedShardColumns(IReadOnlyCollection<string> physicalColumns, bool extendedProgressionTracking)
    {
        ArgumentNullException.ThrowIfNull(physicalColumns);

        List<string> selected = [];

        if (extendedProgressionTracking)
        {
            selected.AddRange(ExtendedColumns.Where(x => physicalColumns.Contains(x, StringComparer.Ordinal)));
        }

        selected.AddRange(ThresholdColumns.Where(x => physicalColumns.Contains(x, StringComparer.Ordinal)));
        return selected;
    }

    /// <summary>
    /// The granted advisory locks in this database whose key is one of the daemon's leadership lock ids, and who
    /// holds them.
    /// </summary>
    /// <param name="lockKeys">
    /// Every lock id this store's daemon could hold, whichever distributor runs it: <c>DaemonLockId</c> (HotCold over
    /// several databases) and <c>ProjectionLockIds.Compute(eventSchema, shard, DaemonLockId)</c> per shard (HotCold
    /// over one). Solo takes none.
    /// </param>
    /// <remarks>
    /// <para>
    /// Weasel's <c>AdvisoryLock</c> takes <c>PostgresAdvisoryLockKey(int)</c>, a single 64-bit key, so Postgres
    /// records it with <c>objsubid = 1</c> and the key split over <c>classid</c> (high 32 bits) and <c>objid</c>
    /// (low 32 bits). The key is put back together here, so a lock id the hash made negative is matched too -
    /// Marten's own gap probe matches only <c>classid = 0</c>, which is every non-negative id.
    /// </para>
    /// <para>
    /// Columns: <c>pid</c>, <c>lock_key</c>, <c>usename</c>, <c>application_name</c>, <c>client_addr</c>,
    /// <c>backend_start</c>, <c>state</c>, <c>xact_start</c>. <c>pg_locks</c> is visible to every role;
    /// <c>pg_stat_activity</c>'s details of another role's session are null without <c>pg_read_all_stats</c>.
    /// </para>
    /// </remarks>
    public static HealthQuery DaemonLocks(IReadOnlyCollection<long> lockKeys)
    {
        ArgumentNullException.ThrowIfNull(lockKeys);

        const string Sql =
            """
            select
              l.pid,
              ((l.classid::bigint << 32) | l.objid::bigint) as lock_key,
              a.usename::text as usename,
              a.application_name,
              a.client_addr::text as client_addr,
              a.backend_start,
              a.state,
              a.xact_start
            from pg_catalog.pg_locks l
            left join pg_catalog.pg_stat_activity a on a.pid = l.pid
            where l.locktype = 'advisory'
              and l.granted
              and l.objsubid = 1
              and l.database = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())
              and ((l.classid::bigint << 32) | l.objid::bigint) = any(@lock_keys)
            order by l.pid
            """;

        return new HealthQuery(Sql,
        [
            new NpgsqlParameter("lock_keys", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = lockKeys.ToArray() },
        ]);
    }

    /// <summary>
    /// D10's read: the progression rows that are not a registered shard by name, nor - under per-tenant partitioning - a
    /// tenant's row of one, at most <see cref="MaxShardRows" />, by name.
    /// </summary>
    /// <param name="eventSchema">The event store's schema. The progression table must exist.</param>
    /// <param name="registered">Every registered shard's and composite member's store-global identity, and Marten's bookkeeping rows'.</param>
    /// <param name="tenantPrefixes">
    /// <c>{identity}:</c> for each of those whose tenants' rows are registered too - every one under
    /// <c>UseTenantPartitionedEvents</c>, none otherwise.
    /// </param>
    /// <remarks>
    /// <para>
    /// The filter is a bound, not the decision: it leaves out the rows that are certainly registered, so a store with
    /// thousands of tenants' rows stays one short read, and the classifier decides every row it returns
    /// (<c>ProgressionRowClassifier</c>). Both lists are parameters; the prefix is compared as a value
    /// (<c>left(name, length(prefix)) = prefix</c>), never as a <c>LIKE</c> pattern a <c>_</c> in a name would widen.
    /// </para>
    /// <para>Columns: <c>name</c>, <c>last_seq_id</c>, <c>last_updated</c>. One read of the progression table, by its primary key's order.</para>
    /// </remarks>
    public static HealthQuery OrphanCandidates(string eventSchema, IReadOnlyCollection<string> registered, IReadOnlyCollection<string> tenantPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(tenantPrefixes);

        string sql =
            "select p.name, p.last_seq_id, p.last_updated\n"
            + "from " + SqlIdentifier.Qualify(eventSchema, ProgressionTable) + " p\n"
            + "where p.name <> all(@registered)\n"
            + "  and not exists (select 1 from pg_catalog.unnest(@tenant_prefixes) as t(prefix)\n"
            + "                  where pg_catalog.left(p.name, pg_catalog.length(t.prefix)) = t.prefix)\n"
            + "order by p.name\n"
            + "limit " + MaxShardRows.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new HealthQuery(sql,
        [
            new NpgsqlParameter("registered", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = registered.ToArray() },
            new NpgsqlParameter("tenant_prefixes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tenantPrefixes.ToArray() },
        ]);
    }

    /// <summary>How many dead letters each projection has.</summary>
    /// <param name="deadLetterSchema">
    /// The dead-letter table's schema, as <c>DeadLetterQueries.TableFor</c> finds it - the event store's, where Marten
    /// keeps <c>DeadLetterEvent</c>.
    /// </param>
    /// <param name="deadLetterTable">Its name, likewise: <c>mt_doc_deadletterevent</c>, unless the host re-aliased the type.</param>
    /// <param name="projectionNameKey">
    /// The JSON property <c>DeadLetterEvent.ProjectionName</c> is stored under - the serializer's casing decides it
    /// (<c>ProjectionName</c>, <c>projectionName</c>, <c>projection_name</c>). A parameter, never text.
    /// </param>
    /// <remarks>
    /// <para>
    /// A count of the whole table, grouped: the same scan the navigation badge's dead-letter count already runs, and
    /// the only honest way to a per-projection number - Marten's own <c>FetchDeadLetterCountsAsync</c> applies the
    /// table's migration first, which a health read never does (hard rule 14). The caller reads it only when the
    /// type is visible: a hidden document type's table name never goes into a statement.
    /// </para>
    /// <para>Columns: <c>projection</c> (text, possibly null), <c>dead_letters</c> (bigint).</para>
    /// </remarks>
    public static HealthQuery DeadLettersByProjection(string deadLetterSchema, string deadLetterTable, string projectionNameKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deadLetterSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadLetterTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionNameKey);

        string sql =
            "select d.data ->> @projection_key as projection, pg_catalog.count(*) as dead_letters\n"
            + "from " + SqlIdentifier.Qualify(deadLetterSchema, deadLetterTable) + " d\n"
            + "group by 1\n"
            + "order by 1\n"
            + "limit " + MaxShardRows.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new HealthQuery(sql, [Text("projection_key", projectionNameKey)]);
    }

    /// <summary>
    /// The Explain's first question: every client session in this database whose transaction began before the mark
    /// was pinned, with what Marten's <c>GapLivenessProbe</c> asks of each.
    /// </summary>
    /// <param name="eventSchema">The event store's schema, for the <c>mt_events</c> lineage the write-lock test names.</param>
    /// <param name="lockKeys">The daemon's lock ids, as for <see cref="DaemonLocks" />: a session holding one is the daemon's own.</param>
    /// <param name="pinnedSince">When the mark was first seen pinned (the stuck-gap row's <c>last_updated</c>).</param>
    /// <param name="allocationFence">
    /// The persisted allocation fence's time, when its sequence is at or below the mark - every number in the gap was
    /// handed out after it - or <see langword="null" />.
    /// </param>
    /// <remarks>
    /// <para>
    /// Marten's questions, read at <c>V9.39.0</c> and <c>V9.50.2</c>: a transaction open since before the gap could
    /// have drawn its numbers and not committed yet; one holding <c>RowExclusiveLock</c> on <c>mt_events</c> (or a
    /// partition) is mid-append; one with a transaction id is writing. Two kinds are ruled out, as Marten rules them
    /// out: a session idle in transaction since before the fence (it has run nothing since the gap's numbers were
    /// handed out) and a session holding the daemon's own leadership lock that has written nothing (#5125). The
    /// pinned time is the durable first sighting, which is no later than the in-memory one Marten's probe uses -
    /// a reserver of this gap began before either.
    /// </para>
    /// <para>
    /// Columns: <c>pid</c>, <c>usename</c>, <c>application_name</c>, <c>client_addr</c>, <c>xact_start</c>,
    /// <c>state</c>, <c>state_change</c>, <c>wait_event_type</c>, <c>wait_event</c>, <c>query</c> (the first
    /// <see cref="ExplainedQueryLength" /> characters), <c>has_xid</c>, <c>holds_events_write_lock</c>,
    /// <c>ruled_out_quiescent</c>, <c>ruled_out_daemon_lock</c>, <c>query_truncated</c> (whether the read cut the
    /// statement, so a half-cut name can be dropped before it is masked). A session of another role is invisible here
    /// without <c>pg_read_all_stats</c>, because its <c>xact_start</c> is null: <see cref="ExplainHiddenWriters" />
    /// counts what this cannot see.
    /// </para>
    /// </remarks>
    public static HealthQuery ExplainSessions(
        string eventSchema,
        IReadOnlyCollection<long> lockKeys,
        DateTimeOffset pinnedSince,
        DateTimeOffset? allocationFence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);
        ArgumentNullException.ThrowIfNull(lockKeys);

        string sql =
            """
            with quiescent as (
              select a.pid
                from pg_catalog.pg_stat_activity a
               where a.pid <> pg_catalog.pg_backend_pid()
                 and a.state in ('idle in transaction', 'idle in transaction (aborted)')
                 and a.state_change < @fence
            ),
            own_daemon_locks as (
              select a.pid
                from pg_catalog.pg_locks l
                join pg_catalog.pg_stat_activity a on a.pid = l.pid
               where l.locktype = 'advisory'
                 and l.granted
                 and l.pid <> pg_catalog.pg_backend_pid()
                 and l.objsubid = 1
                 and ((l.classid::bigint << 32) | l.objid::bigint) = any(@lock_keys)
                 and l.database = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())
                 and a.backend_xid is null
            ),
            events_lineage as (
              select c.oid
                from pg_catalog.pg_class c
                join pg_catalog.pg_namespace n on n.oid = c.relnamespace
               where n.nspname = @schema
                 and c.relname like 'mt_events%'
                 and c.relkind in ('r', 'p')
            )
            select
              a.pid,
              a.usename::text as usename,
              a.application_name,
              a.client_addr::text as client_addr,
              a.xact_start,
              a.state,
              a.state_change,
              a.wait_event_type,
              a.wait_event,
              pg_catalog.left(a.query, @query_length) as query,
              a.backend_xid is not null as has_xid,
              exists (select 1 from pg_catalog.pg_locks l
                       where l.pid = a.pid and l.locktype = 'relation' and l.granted and l.mode = 'RowExclusiveLock'
                         and l.relation in (select e.oid from events_lineage e)) as holds_events_write_lock,
              a.pid in (select q.pid from quiescent q) as ruled_out_quiescent,
              a.pid in (select o.pid from own_daemon_locks o) as ruled_out_daemon_lock,
              coalesce(pg_catalog.length(a.query) > @query_length, false) as query_truncated
            from pg_catalog.pg_stat_activity a
            where a.datname = pg_catalog.current_database()
              and a.pid <> pg_catalog.pg_backend_pid()
              and a.backend_type = 'client backend'
              and a.xact_start is not null
              and a.xact_start <= @pinned_since
            order by a.xact_start
            limit @limit
            """;

        return new HealthQuery(sql,
        [
            Text("schema", eventSchema),
            new NpgsqlParameter("lock_keys", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = lockKeys.ToArray() },
            new NpgsqlParameter("pinned_since", NpgsqlDbType.TimestampTz) { Value = pinnedSince.ToUniversalTime() },
            // No fence: nothing is quiescent - exactly Marten's unfenced behaviour.
            new NpgsqlParameter("fence", NpgsqlDbType.TimestampTz) { Value = (allocationFence ?? DateTimeOffset.MinValue).ToUniversalTime() },
            new NpgsqlParameter("query_length", NpgsqlDbType.Integer) { Value = ExplainedQueryLength },
            new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxExplainedSessions },
        ]);
    }

    /// <summary>
    /// The Explain's second question: the prepared transactions of this database - the one possible reserver no
    /// session view can show, because it has no backend (#5605's <c>pg_prepared_xacts</c> clause).
    /// </summary>
    /// <remarks>Columns: <c>gid</c>, <c>owner</c>, <c>prepared</c>.</remarks>
    public static HealthQuery ExplainPreparedTransactions()
    {
        const string Sql =
            """
            select p.gid, p.owner::text as owner, p.prepared
            from pg_catalog.pg_prepared_xacts p
            where p.database = pg_catalog.current_database()
            order by p.prepared
            limit @limit
            """;

        return new HealthQuery(Sql, [new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxExplainedPreparedTransactions }]);
    }

    /// <summary>
    /// The Explain's third question: how many write transactions of this database this role cannot see the start of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every transaction id holder takes an <c>ExclusiveLock</c> on its own id, and <c>pg_locks</c> and
    /// <c>pg_stat_activity.datname</c> are visible to every role - the same mapping Marten's #5605 clause uses to keep
    /// the xid question to this database. What a role without <c>pg_read_all_stats</c> cannot see is
    /// <c>xact_start</c>, so these are counted rather than judged: any of them might be the reserver.
    /// </para>
    /// <para>Columns: <c>hidden_writers</c>.</para>
    /// </remarks>
    public static HealthQuery ExplainHiddenWriters()
    {
        const string Sql =
            """
            select pg_catalog.count(*) as hidden_writers
            from pg_catalog.pg_locks l
            join pg_catalog.pg_stat_activity a on a.pid = l.pid
            where l.locktype = 'transactionid'
              and l.granted
              and l.mode = 'ExclusiveLock'
              and a.datname = pg_catalog.current_database()
              and a.pid <> pg_catalog.pg_backend_pid()
              and a.xact_start is null
            """;

        return new HealthQuery(Sql);
    }

    private static StringBuilder SelectShardColumns(
        string eventSchema,
        IReadOnlyCollection<string> physicalColumns,
        bool extendedProgressionTracking)
    {
        StringBuilder sql = new();

        sql.Append("select p.name, p.last_seq_id, ");

        // Present on every table Marten has created since long before the floor; read as null rather than refused
        // if a hand-built table lacks it.
        sql.Append(physicalColumns.Contains("last_updated", StringComparer.Ordinal)
            ? "p.last_updated"
            : "null::timestamptz as last_updated");

        foreach (string column in SelectedShardColumns(physicalColumns, extendedProgressionTracking))
        {
            sql.Append(", p.").Append(SqlIdentifier.Quote(column));
        }

        sql.Append("\nfrom ").Append(SqlIdentifier.Qualify(eventSchema, ProgressionTable)).Append(" p\n");
        return sql;
    }

    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };
}
