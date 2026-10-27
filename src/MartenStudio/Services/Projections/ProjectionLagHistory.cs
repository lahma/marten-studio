using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Projections;

/// <summary>
/// The projections screen's own samples: each shard's position and the high water, kept from the reads the screen
/// already makes, so that its lag chart, its arrow and a running rebuild's estimate are drawn from memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>No read of its own.</b> A sample is recorded by the projections service's single-flight snapshot read
/// (<c>StudioSnapshotCache</c>, D10) - the one read every projections page and the Overview already share, once per
/// time-to-live however many circuits poll - and by nothing else. A page that is not open, or a tab that is hidden, takes
/// no sample; nothing here has a timer.
/// </para>
/// <para>
/// <b>Why not the health advisor's history.</b> <see cref="HealthHistory" /> is filled only by a health sweep, which runs
/// only while the Health page or the Overview is open, once a <c>HealthInterval</c> (a minute by default): a projections
/// page opened on its own had no arrow and no estimate (the 0.4 UI sweep's finding), a rebuild shorter than three
/// minutes could never get an estimate, and a store that partitions its events by tenant has no positions there at all
/// (D03 does not run on one). Its series are also the whole database's numbers, read at the database gate; these are
/// exactly the rows the screen shows the visitor for the scope it resolved, so drawing their history shows nobody
/// anything the screen did not.
/// </para>
/// <para>
/// <b>What it costs.</b> A sample is three numbers: <see cref="Capacity" /> per shard, at least
/// <see cref="SpacingFor" /> apart - ten minutes at the default <c>RefreshInterval</c>, and 24 bytes each, about 2.9 KB a
/// shard. A database keeps at most <see cref="MaxShards" /> shards' series (about 0.6 MB): past that the shards the
/// screen reads furthest behind are kept first, and the rest are drawn without a chart. A series nobody has read for twice
/// its window is dropped. Databases are bounded by <see cref="HealthDatabaseBound" /> (64 by default) like the advisor's
/// state: a read touches the database, and an evicted one loses its series with its health history.
/// </para>
/// <para>
/// Singleton. Two circuits watching one database are watching one history.
/// </para>
/// </remarks>
internal sealed class ProjectionLagHistory : IPerDatabaseState
{
    /// <summary>How many samples a shard keeps.</summary>
    public const int Capacity = 120;

    /// <summary>How many shards a database keeps samples for.</summary>
    public const int MaxShards = 200;

    /// <summary>
    /// The least time between two samples, whatever the refresh interval: the daemon checks' ten-minute trend window over
    /// <see cref="Capacity" /> samples.
    /// </summary>
    public static readonly TimeSpan DefaultSpacingFloor = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, Database> databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private readonly HealthDatabaseBound? bound;

    /// <param name="bound">
    /// The process's bound on databases with state (<see cref="HealthDatabaseBound" />), which forgets a database's series
    /// with its health history; <see langword="null" /> for none, in a test.
    /// </param>
    public ProjectionLagHistory(HealthDatabaseBound? bound = null)
        : this(bound, DefaultSpacingFloor)
    {
    }

    /// <summary>A history whose samples may be closer together than the default allows - for a live test, whose daemon runs in seconds.</summary>
    internal ProjectionLagHistory(HealthDatabaseBound? bound, TimeSpan spacingFloor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(spacingFloor, TimeSpan.Zero);

        SpacingFloor = spacingFloor;
        this.bound = bound;
        bound?.Register(this);
    }

    /// <summary>The least time between two samples of a shard.</summary>
    public TimeSpan SpacingFloor { get; }

    /// <summary>How far apart samples are kept for a page that refreshes every <paramref name="refreshInterval" />.</summary>
    public TimeSpan SpacingFor(TimeSpan refreshInterval) => refreshInterval > SpacingFloor ? refreshInterval : SpacingFloor;

    /// <summary>How many databases have samples, for a test and for nothing else.</summary>
    internal int DatabaseCount
    {
        get
        {
            lock (gate)
            {
                return databases.Count;
            }
        }
    }

    /// <summary>
    /// Records one read: every shard's position against the high water at <paramref name="at" />. A shard's sample is kept
    /// only if it is newer than its newest one by at least four-fifths of <paramref name="spacing" /> - so several circuits
    /// polling out of step keep one sample per spacing, not one per poll.
    /// </summary>
    /// <param name="storeKey">The store's registered key.</param>
    /// <param name="databaseId">The database's identity.</param>
    /// <param name="at">When the read finished.</param>
    /// <param name="highWater">The highest event sequence the read saw.</param>
    /// <param name="shards">Each shard the read saw, with its position.</param>
    /// <param name="spacing">How far apart samples are kept (<see cref="SpacingFor" />).</param>
    public void Append(
        string storeKey,
        string databaseId,
        DateTimeOffset at,
        long highWater,
        IEnumerable<(string Shard, long Position)> shards,
        TimeSpan spacing)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);
        ArgumentNullException.ThrowIfNull(shards);

        // Before the write, as the sweeper touches before it writes: this database is the one in use now.
        bound?.Touch(storeKey, databaseId);

        long ticks = at.UtcTicks;
        long minimumStep = (long) (spacing.Ticks * 0.8);
        long forgottenBefore = ticks - (spacing.Ticks * Capacity * 2);

        lock (gate)
        {
            string key = KeyFor(storeKey, databaseId);

            if (!databases.TryGetValue(key, out Database? database))
            {
                database = new Database();
                databases[key] = database;
            }

            // Furthest behind first, so a database with more shards than it keeps charts the ones worth watching.
            foreach ((string shard, long position) in shards
                         .OrderByDescending(x => highWater - x.Position)
                         .ThenBy(static x => x.Shard, StringComparer.Ordinal))
            {
                if (database.Series.TryGetValue(shard, out ShardSeries? series))
                {
                    if (ticks <= series.NewestTicks || ticks - series.NewestTicks < minimumStep)
                    {
                        continue;
                    }
                }
                else if (database.Series.Count < MaxShards)
                {
                    series = new ShardSeries();
                    database.Series[shard] = series;
                }
                else
                {
                    continue;
                }

                series.Add(new Sample(ticks, position, highWater));
            }

            foreach (string gone in database.Series.Where(x => x.Value.NewestTicks < forgottenBefore).Select(static x => x.Key).ToList())
            {
                database.Series.Remove(gone);
            }
        }
    }

    /// <summary>One shard's samples, oldest first - empty when there are none.</summary>
    public IReadOnlyList<LagPoint> Read(string storeKey, string databaseId, string shard)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);
        ArgumentNullException.ThrowIfNull(shard);

        lock (gate)
        {
            return databases.TryGetValue(KeyFor(storeKey, databaseId), out Database? database)
                && database.Series.TryGetValue(shard, out ShardSeries? series)
                    ? [.. series.Samples.Select(static x => new LagPoint(new DateTimeOffset(x.Ticks, TimeSpan.Zero), x.Position, x.HighWater))]
                    : [];
        }
    }

    /// <inheritdoc />
    public void Forget(string storeKey, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        lock (gate)
        {
            databases.Remove(KeyFor(storeKey, databaseId));
        }
    }

    private static string KeyFor(string storeKey, string databaseId) => storeKey + "|" + databaseId;

    /// <summary>One sample, as it is kept: 24 bytes.</summary>
    private readonly record struct Sample(long Ticks, long Position, long HighWater);

    /// <summary>One shard's samples, oldest first, at most <see cref="Capacity" />.</summary>
    private sealed class ShardSeries
    {
        public Queue<Sample> Samples { get; } = new(Capacity);

        /// <summary>The newest sample's time, in UTC ticks.</summary>
        public long NewestTicks { get; private set; } = long.MinValue;

        public void Add(Sample sample)
        {
            Samples.Enqueue(sample);
            NewestTicks = sample.Ticks;

            while (Samples.Count > Capacity)
            {
                Samples.Dequeue();
            }
        }
    }

    /// <summary>One database's series, by shard identity - which carries the tenant on a store that partitions by tenant.</summary>
    private sealed class Database
    {
        public Dictionary<string, ShardSeries> Series { get; } = new(StringComparer.Ordinal);
    }
}
