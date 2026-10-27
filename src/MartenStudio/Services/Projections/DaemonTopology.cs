using System.Globalization;

using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.Services.Health.Checks;

using PostgresStudio.Services;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Services.Projections;

/// <summary>Which coordinator runs the daemon this process hosts (M7) - what a <see cref="DaemonHostingState.Hosted" /> answer adds.</summary>
/// <remarks>
/// Read at Marten <c>V9.39.0</c> and <c>v9.54.2</c> with JasperFx <c>V2.74.0</c> and <c>V2.95.1</c>, where the
/// coordinators, the distributors and <c>IProjectionSet</c> are byte-identical.
/// </remarks>
internal enum DaemonCoordination
{
    /// <summary>No coordinator was asked: no daemon is hosted here, or an external system runs the projections.</summary>
    None = 0,

    /// <summary>
    /// A coordinator with a leadership loop (<c>ProjectionCoordinatorBase</c>: Marten's <c>ProjectionCoordinator</c>,
    /// which <c>AddAsyncDaemon(Solo | HotCold)</c> registers). Every <c>LeadershipPollingTime</c> - <c>AgentPauseTime</c>
    /// while a shard is paused - it starts every agent of every set it holds the lock for that is not running, and stops
    /// the agents of a set it does not hold. Solo holds every set and takes no lock; HotCold holds a set only while it holds
    /// that set's advisory lock: one per shard on a single database, one per database on several.
    /// </summary>
    LeadershipLoop = 1,

    /// <summary>
    /// <c>ExplicitProjectionCoordinator</c>, which <c>MartenDaemonModeIsSolo()</c> registers: no leadership loop. An agent
    /// runs once something starts it, nothing restarts one that stops, and its pause is its <c>StopAsync</c>.
    /// </summary>
    Explicit = 2,

    /// <summary>A coordinator that is neither, which hands out daemons: how it distributes agents is its own.</summary>
    Other = 3,
}

/// <summary>How the leadership loop's distributor divides the shards between nodes.</summary>
internal enum DaemonDistribution
{
    /// <summary>Not known: no leadership loop, or a distributor of another kind.</summary>
    Unknown = 0,

    /// <summary><c>SoloProjectionDistributor</c>: this node holds every set, with no lock.</summary>
    Solo = 1,

    /// <summary><c>SingleTenantProjectionDistributor</c> (HotCold over one database): one advisory lock per store-global shard.</summary>
    PerShard = 2,

    /// <summary><c>MultiTenantedProjectionDistributor</c> (HotCold over several): one advisory lock per database, <c>DaemonLockId</c>.</summary>
    PerDatabase = 3,
}

/// <summary>
/// Which store-global shards this node's coordinator leads in one database - asked of its distributor in memory, never of
/// the database.
/// </summary>
/// <remarks>
/// <para>
/// <b>The distributor's own answer.</b> <c>IProjectionDistributor.HasLock(set)</c> is what the leadership loop asks
/// before it starts a set's agents, and it reads only <c>set.Database.Identifier</c> and <c>set.LockId</c>: the advisory
/// lock this node holds for that database, kept in memory, and Weasel's <c>AdvisoryLock.HasLock</c>, which reads its
/// handles under its own lock (Weasel 9.33 and 9.47). So a probe set of this studio's own, with the lock id the
/// distributor computes, gets the same answer the loop would, with no connection and no lock taken. The ids are
/// <c>ProjectionLockIds.Compute(eventSchema, storeGlobalShard, DaemonLockId)</c> per shard and <c>DaemonLockId</c> per
/// database (<c>DaemonCheckConfiguration.LockKeysFor</c> names the same set).
/// </para>
/// <para>
/// <b>A studio pause keeps it.</b> <c>PauseAsync</c> stops the loop and the agents and releases no lock - only
/// <c>StopAsync</c> does - so while Marten Studio's pause is in effect the answer is still what this node led when it
/// paused, which is what decides whether an agent started here would run beside another node's.
/// </para>
/// <para>
/// The distributor's dictionary of locks is written by the loop when it first meets a database; a read that races that
/// write answers "not known", never a guess.
/// </para>
/// </remarks>
internal sealed class DaemonLeadership
{
    private readonly IProjectionDistributor distributor;
    private readonly IProjectionDatabase database;
    private readonly string eventSchema;
    private readonly int daemonLockId;

    private DaemonLeadership(IProjectionDistributor distributor, IProjectionDatabase database, string eventSchema, int daemonLockId)
    {
        this.distributor = distributor;
        this.database = database;
        this.eventSchema = eventSchema;
        this.daemonLockId = daemonLockId;

        Distribution = distributor switch
        {
            SoloProjectionDistributor => DaemonDistribution.Solo,
            SingleTenantProjectionDistributor => DaemonDistribution.PerShard,
            MultiTenantedProjectionDistributor => DaemonDistribution.PerDatabase,
            _ => DaemonDistribution.Unknown,
        };
    }

    /// <summary>How the distributor divides the shards.</summary>
    public DaemonDistribution Distribution { get; }

    /// <summary>Whether shards run on whichever node holds their lock - HotCold - rather than on this one by construction.</summary>
    public bool IsHotCold => Distribution is DaemonDistribution.PerShard or DaemonDistribution.PerDatabase;

    /// <summary>
    /// The leadership of a coordinator with a loop over <paramref name="database" />, or <see langword="null" /> for any
    /// other coordinator, and for one constructed with no distributor (<c>Disabled</c>, <c>ExternallyManaged</c>).
    /// </summary>
    /// <param name="coordinator">The coordinator the accessor resolved.</param>
    /// <param name="store">The store, for its event schema and <c>DaemonLockId</c>.</param>
    /// <param name="database">The resolved database.</param>
    public static DaemonLeadership? For(MartenCoordinator coordinator, IDocumentStore store, IMartenDatabase database)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);

        if (coordinator is not ProjectionCoordinatorBase { Distributor: { } distributor } || database is not IProjectionDatabase projectionDatabase)
        {
            return null;
        }

        return new DaemonLeadership(
            distributor,
            projectionDatabase,
            store.Options.Events.DatabaseSchemaName,
            DaemonCheckConfiguration.DaemonLockIdOf(store.Options));
    }

    /// <summary>
    /// Whether this node holds the leadership lock <paramref name="shard" /> runs under - a tenant's shard runs under its
    /// store-global shard's - or <see langword="null" /> when that cannot be told.
    /// </summary>
    /// <param name="shard">The shard, store-global or one tenant's.</param>
    public bool? Leads(ShardName shard)
    {
        ArgumentNullException.ThrowIfNull(shard);

        try
        {
            int lockId;

            switch (Distribution)
            {
                case DaemonDistribution.Solo:
                    return true;

                case DaemonDistribution.PerShard:
                    ShardName neutral = shard.TenantId is null ? shard : ShardName.Compose(shard.Name, shard.ShardKey, null, shard.Version);
                    lockId = ProjectionLockIds.Compute(eventSchema, neutral, daemonLockId);
                    break;

                case DaemonDistribution.PerDatabase:
                    lockId = daemonLockId;
                    break;

                default:
                    return null;
            }

            return distributor.HasLock(new LockProbe(lockId, database));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A dictionary the loop is writing at this moment, or the one hash Compute cannot turn into an id: not known.
            return null;
        }
    }

    /// <summary>The set a probe asks about: the lock id and the database, which is all <c>HasLock</c> reads.</summary>
    private sealed class LockProbe(int lockId, IProjectionDatabase database) : IProjectionSet
    {
        public int LockId { get; } = lockId;

        public IProjectionDatabase Database { get; } = database;

        public IReadOnlyList<ShardName> Names { get; } = [];
    }
}

/// <summary>
/// What the projections screen and the dead-letter page say about the daemon topology - which agents this node runs, under
/// what mode, and why a Start, a Stop or a Rewind does nothing here or is refused (M7). Pure text over a
/// <see cref="DaemonStatus" />, so each sentence is a unit test.
/// </summary>
internal static class DaemonTopologyWording
{
    /// <summary>The card's "This node" sentence, or <see langword="null" /> where the card's explanation already says it.</summary>
    /// <param name="daemon">The daemon card's state.</param>
    public static string? Describe(DaemonStatus daemon)
    {
        ArgumentNullException.ThrowIfNull(daemon);

        if (daemon.IsExternallyManaged)
        {
            return "An external system - Wolverine's managed distribution, typically - decides which node runs each agent, and "
                + "Marten Studio does not ask it, so it cannot say which run on this node. Their progress below is read from the "
                + "database either way.";
        }

        if (!daemon.IsHostedHere)
        {
            return null;
        }

        string running = Agents(daemon.Agents.Count(static x => string.Equals(x.Status, "Running", StringComparison.OrdinalIgnoreCase)));

        return daemon.Coordination switch
        {
            DaemonCoordination.LeadershipLoop when daemon.IsHotCold => HotCold(daemon, running),
            DaemonCoordination.LeadershipLoop =>
                "Solo: this process runs every async shard of this database itself and takes no leadership lock, and while "
                + "its coordinator's loop runs it starts any agent that is not running within "
                + Seconds(daemon.LeadershipPollingTime) + ". It runs " + running + " now.",
            DaemonCoordination.Explicit =>
                "MartenDaemonModeIsSolo(): a coordinator with no leadership loop. An agent runs once something starts it, "
                + "nothing restarts one that stops, and Start and Stop act at once. It runs " + running + " now.",
            _ =>
                "A projection coordinator that is not Marten's own runs this daemon, and Marten Studio cannot tell how it "
                + "distributes agents between nodes. This node's daemon runs " + running + " now.",
        };
    }

    /// <summary>Why Start on <paramref name="shard" /> would do nothing here, or be refused - <see langword="null" /> when neither.</summary>
    /// <param name="daemon">The daemon card's state.</param>
    /// <param name="shard">The shard's identity.</param>
    public static string? StartRefusal(DaemonStatus daemon, string shard)
    {
        ArgumentNullException.ThrowIfNull(daemon);

        if (daemon.IsRunningHere(shard))
        {
            return AlreadyRunning(shard);
        }

        return daemon.LeadsHere(shard) == false ? LedElsewhere(shard) : null;
    }

    /// <summary>Why Stop on <paramref name="shard" /> would do nothing here - <see langword="null" /> when it would stop something.</summary>
    /// <param name="daemon">The daemon card's state.</param>
    /// <param name="shard">The shard's identity.</param>
    public static string? StopRefusal(DaemonStatus daemon, string shard)
    {
        ArgumentNullException.ThrowIfNull(daemon);

        return daemon.RunsHere(shard) ? null : NothingToStop(shard, daemon.IsHotCold);
    }

    // The shard and projection below are what the circuit named - a visitor's text until the daemon has looked it up, and a
    // Stop of a shard no agent runs for never does - so each refusal carries it escaped and bounded, as H1 has every
    // refusal carry a projection or shard name and 9209 log its target (INT-W1).

    /// <summary>Start of a shard whose agent already runs here: the daemon's start is idempotent, and starts nothing.</summary>
    public static string AlreadyRunning(string shard) =>
        StudioLogValue.Cap(shard) + " is already running on this node, so Start would do nothing: nothing was started.";

    /// <summary>Stop of a shard no agent runs for here.</summary>
    /// <param name="shard">The shard.</param>
    /// <param name="hotCold">Whether the daemon runs HotCold, where the shard may run on another node.</param>
    public static string NothingToStop(string shard, bool hotCold) =>
        "No agent for " + StudioLogValue.Cap(shard) + " runs on this node, so Stop would do nothing: nothing was stopped."
        + (hotCold ? " Under HotCold it runs on whichever node holds its leadership lock." : string.Empty);

    /// <summary>Start, under HotCold, of a shard whose leadership lock this node does not hold.</summary>
    public static string LedElsewhere(string shard) =>
        "Under HotCold, " + StudioLogValue.Cap(shard) + " runs on the node that holds its leadership lock, and this node does not hold it: an "
        + "agent started here would run beside that node's, and the two would write the same progression row. Nothing was "
        + "started. Resume the daemon here, and the coordinators start it wherever the lock is held.";

    /// <summary>A rewind, under HotCold, of a projection whose leadership lock this node does not hold.</summary>
    public static string RewindLedElsewhere(string projection) =>
        "Under HotCold, " + StudioLogValue.Cap(projection) + " runs on the node that holds its leadership lock, and this node does not hold it: a "
        + "rewind here would restart its agents on this node, beside that node's. Rewind it from the node that runs it - its "
        + "projections screen says it leads the shards.";

    /// <summary>What a rewind does while Marten Studio's pause is in effect, said in its confirm dialog.</summary>
    public const string RewindWhilePaused =
        " Marten Studio's pause of this store's daemon is in effect, and a rewind restarts this projection's agents on this "
        + "node now: they run while the rest stay paused.";

    private static string HotCold(DaemonStatus daemon, string running)
    {
        int shards = daemon.Leadership.Count;
        int led = daemon.Leadership.Count(static x => x.Value);

        string held = shards == 0
            ? "Marten Studio cannot tell which of this database's shards it leads."
            : led == 0
                ? "This node holds the lock for none of this database's " + Count(shards, "shard") + ": another node runs them, or none has started yet."
                : "This node holds the lock for " + led.ToString(CultureInfo.InvariantCulture) + " of this database's "
                  + Count(shards, "shard") + ".";

        return "HotCold: each shard runs on the one node that holds its leadership lock, and a coordinator starts agents only "
            + "for the locks it holds. " + held + " It runs " + running + " now.";
    }

    private static string Agents(int count) => Count(count, "agent");

    private static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
}
