using System.Diagnostics;
using System.Globalization;

using Npgsql;

namespace PostgresStudio.Internal.Sql;

/// <summary>What the studio has itself spent in one database recently.</summary>
/// <param name="Total">The summed wall time of the sessions counted.</param>
/// <param name="Sessions">How many sessions that was.</param>
/// <param name="Window">How far back the sum reaches.</param>
internal readonly record struct StudioCost(TimeSpan Total, int Sessions, TimeSpan Window);

/// <summary>
/// The health advisor's "studio cost" line: how much database time the studio's own read-only sessions
/// took in one database over the last five minutes - and, for a check that subtracts the studio's own
/// footprint from a server counter, how many sessions and rollbacks it has run there since the process
/// started.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is counted.</b> Every <see cref="ReadOnlySqlSession" /> transaction, measured client side from
/// <c>BEGIN</c> to <c>ROLLBACK</c>: the SQL console, the Query page's Marten mode and its <c>EXPLAIN</c>, the
/// schema screen's statistics reads, the relationships screen, the database browser's catalog and row
/// reads, and every health check. The time is wall time on the studio's side, round trips included, so it
/// is an upper bound on what the server spent rather than a server-side measurement.
/// </para>
/// <para>
/// <b>What is not.</b> Reads that open their own command without the session: the documents browser's lists
/// and counts, the event store's reads, the projections screen, the column and index catalogs, Marten's own
/// sessions for writes and daemon control. Metering those needs a hook on every read path, which is a
/// follow-up rather than something this line pretends to cover.
/// </para>
/// <para>
/// <b>Per database.</b> A database is keyed by what its connection string names - host, port, database -
/// read off the connection (<see cref="KeyOf(NpgsqlConnection)" />), never a password. The health report
/// shows only the selected database's total, so a visitor of one database does not see the studio's work
/// in another.
/// </para>
/// <para>
/// <b>Bounded, and cheap enough to sit on every session.</b> Thirty ten-second buckets in a ring per database,
/// under one lock taken once per session. <see cref="Shared" /> is process-wide because the sessions are
/// created with <c>new</c> wherever a read is made; a test makes its own on a fake clock.
/// </para>
/// </remarks>
internal sealed class StudioDatabaseTime
{
    /// <summary>How far back the total reaches.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan BucketWidth = TimeSpan.FromSeconds(10);

    private static readonly int BucketCount = (int) (Window.Ticks / BucketWidth.Ticks);

    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, Meter> meters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    /// <summary>A meter on <paramref name="timeProvider" />.</summary>
    public StudioDatabaseTime(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    /// <summary>The process's meter, which every session records into unless told otherwise.</summary>
    public static StudioDatabaseTime Shared { get; } = new(TimeProvider.System);

    /// <summary>
    /// The key a connection's database is metered under: host, port and database as its connection string
    /// names them. Read from the connection string, so it is the same for an open connection and an unopened
    /// one - <see cref="NpgsqlConnection.Host" /> and <see cref="NpgsqlConnection.Port" /> answer only once the
    /// connection is open - and never carries a password.
    /// </summary>
    public static string KeyOf(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        NpgsqlConnectionStringBuilder settings = new(connection.ConnectionString);

        return (settings.Host ?? string.Empty) + ":"
            + settings.Port.ToString(CultureInfo.InvariantCulture) + "/"
            + (settings.Database ?? string.Empty);
    }

    /// <summary>Adds one session's time, and counts it - and its rollback, when there was one.</summary>
    /// <param name="database">The database's key (<see cref="KeyOf(NpgsqlConnection)" />).</param>
    /// <param name="elapsed">Begin to rollback.</param>
    /// <param name="rolledBack">Whether the session's rollback reached the server.</param>
    public void Record(string database, TimeSpan elapsed, bool rolledBack = true)
    {
        ArgumentNullException.ThrowIfNull(database);

        long slot = SlotOf(timeProvider.GetUtcNow());
        int index = (int) (slot % BucketCount);

        lock (gate)
        {
            if (!meters.TryGetValue(database, out Meter? meter))
            {
                meter = new Meter();
                meters[database] = meter;
            }

            meter.SessionsEver++;
            if (rolledBack)
            {
                meter.RollbacksEver++;
            }

            if (elapsed <= TimeSpan.Zero)
            {
                return;
            }

            if (meter.Slots[index] != slot)
            {
                meter.Slots[index] = slot;
                meter.Ticks[index] = 0;
                meter.Sessions[index] = 0;
            }

            meter.Ticks[index] += elapsed.Ticks;
            meter.Sessions[index]++;
        }
    }

    /// <summary>Times one session from <paramref name="startedAt" /> (a <see cref="Stopwatch" /> timestamp).</summary>
    public void RecordSince(string database, long startedAt, bool rolledBack = true) =>
        Record(database, Stopwatch.GetElapsedTime(startedAt), rolledBack);

    /// <summary>The total for <paramref name="database" /> over the last <see cref="Window" />.</summary>
    public StudioCost Total(string database)
    {
        ArgumentNullException.ThrowIfNull(database);

        long newest = SlotOf(timeProvider.GetUtcNow());
        long oldest = newest - BucketCount + 1;

        long total = 0;
        int count = 0;

        lock (gate)
        {
            if (meters.TryGetValue(database, out Meter? meter))
            {
                for (int i = 0; i < BucketCount; i++)
                {
                    if (meter.Slots[i] >= oldest && meter.Slots[i] <= newest)
                    {
                        total += meter.Ticks[i];
                        count += meter.Sessions[i];
                    }
                }
            }
        }

        return new StudioCost(TimeSpan.FromTicks(total), count, Window);
    }

    /// <summary>
    /// How many read-only sessions the studio has run against <paramref name="database" /> since this process
    /// started - for a check that reads a cumulative server counter and must not count the studio's own work.
    /// </summary>
    public long SessionsEver(string database)
    {
        lock (gate)
        {
            return meters.TryGetValue(database, out Meter? meter) ? meter.SessionsEver : 0;
        }
    }

    /// <summary>
    /// How many of those sessions ended in a rollback that reached the server - every one of them, normally:
    /// a read-only session always rolls back - so that a check reading <c>pg_stat_database.xact_rollback</c>
    /// can subtract the studio's own exactly.
    /// </summary>
    public long RollbacksEver(string database)
    {
        lock (gate)
        {
            return meters.TryGetValue(database, out Meter? meter) ? meter.RollbacksEver : 0;
        }
    }

    private static long SlotOf(DateTimeOffset at) => at.UtcTicks / BucketWidth.Ticks;

    /// <summary>One database's ring of buckets and its running counts.</summary>
    private sealed class Meter
    {
        public Meter() => Array.Fill(Slots, -1);

        public long[] Slots { get; } = new long[BucketCount];

        public long[] Ticks { get; } = new long[BucketCount];

        public int[] Sessions { get; } = new int[BucketCount];

        public long SessionsEver { get; set; }

        public long RollbacksEver { get; set; }
    }
}
