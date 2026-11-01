using System.Diagnostics;
using System.Globalization;

using Npgsql;

namespace PostgresStudio.Internal.Sql;

/// <summary>What the studio has itself spent in one database recently.</summary>
/// <param name="Total">The summed wall time of the connections counted.</param>
/// <param name="Connections">
/// How many connections that was - each one the studio opened to the database and closed again - and, for a read-only
/// session on a connection the studio did not open through its sources, that session.
/// </param>
/// <param name="Window">How far back the sum reaches.</param>
internal readonly record struct StudioCost(TimeSpan Total, int Connections, TimeSpan Window);

/// <summary>
/// The studio's own cost in each database - the health advisor's "studio cost" line and the Statements page's "the studio
/// itself" line, one meter for both: how much connection time the studio took in one database over the last five minutes,
/// and, for a check that subtracts the studio's own footprint from a server counter, how many read-only sessions and
/// rollbacks it has run there since the process started.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is counted: every connection the studio opens through its sources</b> (W5-Q1, backlog §C). Both of the studio's
/// databases - MartenStudio's over <c>IMartenDatabase</c> and Postgres Studio's over the host's <see cref="NpgsqlDataSource" />
/// - hand out connections metered by <see cref="StudioConnectionMeter" />, which times each from its open to its close. So
/// every statement the studio issues on them is inside the time: health sweeps, catalog and schema reads, the database
/// browser's lists and rows, the documents and event reads, the projections screen, the SQL console and the Query page, the
/// relationships screen, and the documents' write path's own reads. A read-only session on a connection that was not metered
/// - a caller that opened its own - is still counted, begin to rollback, as it always was.
/// </para>
/// <para>
/// <b>What is not.</b> Marten's own sessions, which the studio opens for its writes (<c>StoreObjects</c>,
/// <c>DeleteObjects</c>, the event store's operations) and to steer the daemon: their connections are Marten's, taken from
/// its own data source by its own session, and nothing of the studio's sits between them and the pool. The time is wall
/// time on the studio's side, round trips and the time between one statement and the next included, so it is an upper bound
/// on what the server spent rather than a server-side measurement.
/// </para>
/// <para>
/// <b>Per database.</b> A database is keyed by what its connection string names - host, port, database -
/// read off the connection (<see cref="KeyOf(NpgsqlConnection)" />), never a password. The health report and the Statements
/// page show only the selected database's total, so a visitor of one database does not see the studio's work in another.
/// </para>
/// <para>
/// <b>The rollbacks stay exact.</b> <see cref="RollbacksEver" /> counts the read-only sessions' rollbacks alone, as it always
/// has - a connection's open and close add none - so C08 subtracts exactly the studio's own from
/// <c>pg_stat_database.xact_rollback</c>.
/// </para>
/// <para>
/// <b>Bounded, and cheap enough to sit on every connection.</b> Thirty ten-second buckets in a ring per database, under one
/// lock taken once per connection and once per session. <see cref="Shared" /> is process-wide because the connections and
/// sessions are created wherever a read is made; a test makes its own on a fake clock.
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

    /// <summary>The process's meter, which every connection and session records into unless told otherwise.</summary>
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

    /// <summary>
    /// Adds one read-only session's time on a connection that was not metered, and counts it - and its rollback, when there
    /// was one.
    /// </summary>
    /// <param name="database">The database's key (<see cref="KeyOf(NpgsqlConnection)" />).</param>
    /// <param name="elapsed">Begin to rollback.</param>
    /// <param name="rolledBack">Whether the session's rollback reached the server.</param>
    public void Record(string database, TimeSpan elapsed, bool rolledBack = true)
    {
        ArgumentNullException.ThrowIfNull(database);

        lock (gate)
        {
            Meter meter = MeterOf(database);
            meter.SessionsEver++;

            if (rolledBack)
            {
                meter.RollbacksEver++;
            }

            AddTime(meter, elapsed);
        }
    }

    /// <summary>Times one session on a connection that was not metered, from <paramref name="startedAt" /> (a <see cref="Stopwatch" /> timestamp).</summary>
    public void RecordSince(string database, long startedAt, bool rolledBack = true) =>
        Record(database, Stopwatch.GetElapsedTime(startedAt), rolledBack);

    /// <summary>
    /// Counts one read-only session on a metered connection, and its rollback, when there was one - and no time: the
    /// connection's own open-to-close time already holds it (<see cref="StudioConnectionMeter" />).
    /// </summary>
    /// <param name="database">The database's key (<see cref="KeyOf(NpgsqlConnection)" />).</param>
    /// <param name="rolledBack">Whether the session's rollback reached the server.</param>
    public void RecordSession(string database, bool rolledBack)
    {
        ArgumentNullException.ThrowIfNull(database);

        lock (gate)
        {
            Meter meter = MeterOf(database);
            meter.SessionsEver++;

            if (rolledBack)
            {
                meter.RollbacksEver++;
            }
        }
    }

    /// <summary>Adds one metered connection's time, open to close, and counts it.</summary>
    /// <param name="database">The database's key (<see cref="KeyOf(NpgsqlConnection)" />).</param>
    /// <param name="elapsed">Open to close.</param>
    public void RecordConnection(string database, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(database);

        lock (gate)
        {
            Meter meter = MeterOf(database);
            meter.ConnectionsEver++;
            AddTime(meter, elapsed);
        }
    }

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
                        count += meter.Counts[i];
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

    /// <summary>How many metered connections the studio has opened and closed against <paramref name="database" /> since this process started.</summary>
    public long ConnectionsEver(string database)
    {
        lock (gate)
        {
            return meters.TryGetValue(database, out Meter? meter) ? meter.ConnectionsEver : 0;
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

    /// <summary>The meter of <paramref name="database" />, made on first use. Called under the lock.</summary>
    private Meter MeterOf(string database)
    {
        if (!meters.TryGetValue(database, out Meter? meter))
        {
            meter = new Meter();
            meters[database] = meter;
        }

        return meter;
    }

    /// <summary>Adds time to the current bucket, and counts it there; nothing for no time. Called under the lock.</summary>
    private void AddTime(Meter meter, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        long slot = SlotOf(timeProvider.GetUtcNow());
        int index = (int) (slot % BucketCount);

        if (meter.Slots[index] != slot)
        {
            meter.Slots[index] = slot;
            meter.Ticks[index] = 0;
            meter.Counts[index] = 0;
        }

        meter.Ticks[index] += elapsed.Ticks;
        meter.Counts[index]++;
    }

    /// <summary>One database's ring of buckets and its running counts.</summary>
    private sealed class Meter
    {
        public Meter() => Array.Fill(Slots, -1);

        public long[] Slots { get; } = new long[BucketCount];

        public long[] Ticks { get; } = new long[BucketCount];

        public int[] Counts { get; } = new int[BucketCount];

        public long SessionsEver { get; set; }

        public long ConnectionsEver { get; set; }

        public long RollbacksEver { get; set; }
    }
}
