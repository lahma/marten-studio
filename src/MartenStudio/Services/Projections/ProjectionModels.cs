using System.Globalization;

using JasperFx.Events.Projections;

namespace MartenStudio.Services.Projections;

/// <summary>
/// One projection as the store was <em>configured</em>, before anything is known about what it has
/// actually done.
/// </summary>
/// <remarks>
/// Read from <c>store.Options.Events.Projections()</c>, which answers
/// <c>JasperFx.Events.Subscriptions.ISubscriptionSource</c>. The static model is the list: a projection
/// that has never run still has a row, and a row with no progress behind it is a fact about the store
/// rather than an empty result.
/// </remarks>
/// <param name="Name">The projection's name, which is also what a rebuild is asked for by.</param>
/// <param name="Lifecycle">Inline, Async or Live - the property of the registration, not of the type.</param>
/// <param name="SubscriptionType">Projection, Subscription, or whatever else JasperFx calls it.</param>
/// <param name="Version">The projection version, bumped by a host to force a rebuild.</param>
/// <param name="ImplementationTypeName">The CLR type the host registered, for the person reading the row.</param>
/// <param name="ShardNames">
/// Every shard this projection runs as, by <c>ShardName.Identity</c> (<c>{Projection}:{Key}</c>). A
/// projection that is not sharded has exactly one, called <c>All</c>; a composite projection bundles its
/// stages and reports one.
/// </param>
internal sealed record ProjectionInfo(
    string Name,
    ProjectionLifecycle Lifecycle,
    string SubscriptionType,
    uint Version,
    string ImplementationTypeName,
    IReadOnlyList<string> ShardNames)
{
    /// <summary>Whether the async daemon is the thing that runs this projection.</summary>
    public bool IsAsync => Lifecycle == ProjectionLifecycle.Async;
}

/// <summary>How far behind a shard is, as the table colours it.</summary>
internal enum LagSeverity
{
    /// <summary>Keeping up, or close enough.</summary>
    Ok,

    /// <summary>Behind by more than <see cref="ProjectionLagThresholds.Amber" /> events.</summary>
    Warning,

    /// <summary>Behind by more than <see cref="ProjectionLagThresholds.Red" /> events.</summary>
    Critical
}

/// <summary>
/// Where the two lag colours change.
/// </summary>
/// <remarks>
/// Constants for now, deliberately. A real threshold depends on how fast the store is written to, and a
/// host-configurable one would need a unit ("events" or "seconds behind") that the studio cannot compute
/// honestly yet. These two numbers are the ones an operator recognises: a thousand events behind is worth
/// looking at, a hundred thousand is worth acting on.
/// </remarks>
internal static class ProjectionLagThresholds
{
    /// <summary>Above this many events behind, the row is amber.</summary>
    public const long Amber = 1_000;

    /// <summary>Above this many events behind, the row is red.</summary>
    public const long Red = 100_000;

    /// <summary>The severity of one lag.</summary>
    public static LagSeverity Severity(long lag) => lag switch
    {
        > Red => LagSeverity.Critical,
        > Amber => LagSeverity.Warning,
        _ => LagSeverity.Ok
    };
}

/// <summary>
/// One shard's progress, as the database records it and the tracker refines it.
/// </summary>
/// <param name="ShardName">The shard identity, <c>{Projection}:{Key}</c>.</param>
/// <param name="ProjectionName">The projection the shard belongs to.</param>
/// <param name="Sequence">The event sequence this shard has processed up to, or zero when it has never run.</param>
/// <param name="HighWater">The store's high-water mark, which is what the lag is measured against.</param>
/// <param name="AgentStatus">What the daemon says the agent is doing, or <see langword="null" /> when nothing said.</param>
/// <param name="PauseReason">Why it is paused, when it is.</param>
/// <param name="Failure">The failure that stopped it, flattened to a sentence.</param>
/// <param name="LastAdvanced">When this shard last moved.</param>
/// <param name="SkippedCount">How many events were skipped, which is how many dead letters this shard wrote.</param>
/// <param name="TenantId">The tenant the row is for, under tenant-partitioned progression.</param>
/// <param name="HasProgressRow">
/// Whether the database has a progression row for this shard at all. <see langword="false" /> is not the
/// same as sequence zero: one means "never started", the other means "started and has processed nothing",
/// and an operations screen that draws them the same way is lying (plan section 4.8).
/// </param>
/// <param name="IsLive">Whether the numbers came from the in-process tracker rather than the database.</param>
/// <param name="IsRegistered">
/// Whether a projection this store currently registers claims this shard. <see langword="false" /> is a
/// progression row left behind by a projection that has been removed from the host's registration or
/// renamed: it still explains why a table is stale, so it is rendered in its own section rather than
/// hidden (plan section 4.8 - "cannot report" and "nobody owns this" are values, not empty results).
/// </param>
internal sealed record ShardProgress(
    string ShardName,
    string ProjectionName,
    long Sequence,
    long HighWater,
    string? AgentStatus,
    string? PauseReason,
    string? Failure,
    DateTimeOffset? LastAdvanced,
    long? SkippedCount,
    string? TenantId,
    bool HasProgressRow,
    bool IsLive,
    bool IsRegistered = true)
{
    /// <summary>How many events this shard has still to process. Never negative.</summary>
    public long Lag => Math.Max(0, HighWater - Sequence);

    /// <summary>The colour this row wears.</summary>
    public LagSeverity Severity => ProjectionLagThresholds.Severity(Lag);

    /// <summary>Whether the shard is paused, by whatever name the daemon gave it.</summary>
    public bool IsPaused =>
        PauseReason is { Length: > 0 }
        || (AgentStatus is { Length: > 0 } status
            && status.Contains("pause", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the shard reported a failure.</summary>
    public bool HasFailed => Failure is { Length: > 0 };
}

/// <summary>Whether the async daemon is running in this process at all.</summary>
internal enum DaemonHostingState
{
    /// <summary>A coordinator is registered here and answered for this database.</summary>
    Hosted,

    /// <summary>
    /// No coordinator is registered for this store, so the daemon - if there is one - is somewhere else.
    /// </summary>
    /// <remarks>
    /// A value, not an error (AGENTS.md hard rule 11). The alternative is
    /// <c>store.BuildProjectionDaemonAsync()</c>, which starts a second daemon beside the host's own and
    /// the two then fight over the same advisory locks until one hangs.
    /// </remarks>
    NotHostedInThisProcess
}

/// <summary>One running agent, as the daemon reports it.</summary>
/// <param name="ShardName">The shard identity the agent runs.</param>
/// <param name="Status">The agent's own status.</param>
/// <param name="Position">The sequence the agent has reached.</param>
/// <param name="HighWaterMark">The high-water mark the agent last saw.</param>
internal sealed record DaemonAgentInfo(string ShardName, string Status, long Position, long HighWaterMark);

/// <summary>
/// What a per-agent control did, or the reason it did nothing.
/// </summary>
/// <remarks>
/// A result rather than an exception, because "the coordinator would undo this within a second" is not
/// a failure and must not be rendered as one. It is not a capability refusal either - the visitor may
/// well hold <c>ControlDaemon</c>, and the operation is available the moment the daemon is paused - so
/// it is neither <see cref="StudioCapabilityDeniedException" /> nor
/// <see cref="StudioDaemonNotHostedException" />. The page renders <see cref="Reason" /> as a hint next
/// to a disabled button; the service refuses regardless of what was rendered (AGENTS.md hard rule 5).
/// </remarks>
/// <param name="Applied">Whether the daemon was actually asked.</param>
/// <param name="Reason">Why it was not, in words a page can show. <see langword="null" /> when it was.</param>
internal sealed record DaemonControlResult(bool Applied, string? Reason)
{
    /// <summary>The daemon was asked and it accepted.</summary>
    public static DaemonControlResult Done { get; } = new(true, null);

    /// <summary>The daemon was not asked, and this is why.</summary>
    public static DaemonControlResult Refused(string reason) => new(false, reason);
}

/// <summary>
/// JasperFx's own daemon defaults, used only where a store's real setting could not be read.
/// </summary>
/// <remarks>
/// Its own type rather than a constant on <see cref="DaemonStatus" />, because a record's primary
/// constructor cannot use a constant declared in its own body as a parameter default (CS0103).
/// </remarks>
internal static class DaemonDefaults
{
    /// <summary>
    /// <c>DaemonSettings.LeadershipPollingTime</c>'s default: an <c>int</c> of milliseconds, 5000.
    /// Verified against JasperFx.Events 2.69.3.
    /// </summary>
    public const int LeadershipPollingMilliseconds = 5_000;
}

/// <summary>
/// The sentences the daemon controls explain themselves with, in one place so the service's refusal and
/// the page's hint cannot drift apart.
/// </summary>
internal static class DaemonControlMessages
{
    /// <summary>
    /// What pausing actually does, said in one sentence because it is bigger than the page it is on.
    /// </summary>
    /// <remarks>
    /// <c>IProjectionCoordinator.PauseAsync()</c> stops the leadership runner and then calls
    /// <c>StopAllAsync()</c> on <em>every</em> daemon that coordinator has resolved - which is every
    /// database of this store that this process is running projections for, not the one the scope
    /// selector names.
    /// </remarks>
    public const string PauseIsProcessWide =
        "Pausing stops the projection agents of every database this process hosts for this store until " +
        "somebody resumes them.";

    /// <summary>Why a per-agent start or stop is pointless while the coordinator is running.</summary>
    /// <param name="leadershipPollingTime">
    /// The real interval, read from the store's own <c>LeadershipPollingTime</c> rather than assumed.
    /// </param>
    public static string AgentControlNeedsPause(TimeSpan leadershipPollingTime) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The projection coordinator restarts agents every {leadershipPollingTime.TotalSeconds:0.#} s (LeadershipPollingTime); pause the daemon first.");
}

/// <summary>
/// What the daemon card draws.
/// </summary>
/// <param name="Hosting">Whether a daemon is reachable from this process.</param>
/// <param name="IsRunning">Whether that daemon is running. Meaningless when it is not hosted here.</param>
/// <param name="Mode">The configured <c>DaemonMode</c> - Disabled, Solo or HotCold.</param>
/// <param name="Agents">The agents the daemon currently has, when it could be asked.</param>
/// <param name="HasAnyPaused">Whether the daemon reports any paused shard.</param>
/// <param name="HighWaterLastPolledAt">When the high-water agent last polled, when the daemon says.</param>
/// <param name="Explanation">Plain words for whoever is reading the card, especially when it is not hosted here.</param>
/// <param name="PausedByStudio">
/// The pause this process issued through Marten Studio, or <see langword="null" /> when it has issued
/// none. The coordinator has no public paused flag at all, so this is the only honest answer available:
/// it says what the studio did, and never guesses at what somebody else did.
/// </param>
/// <param name="LeadershipPollingMilliseconds">
/// The store's own <c>LeadershipPollingTime</c>, which is how often the coordinator restarts every agent
/// it finds missing. The number the refusal hint quotes, so the hint is about this store rather than
/// about the default.
/// </param>
/// <param name="CoordinatedDatabases">
/// Every database of this store that a pause would reach, by identity. Named in the confirm dialog
/// because pausing is not scoped to the database the selector is on.
/// </param>
internal sealed record DaemonStatus(
    DaemonHostingState Hosting,
    bool IsRunning,
    string Mode,
    IReadOnlyList<DaemonAgentInfo> Agents,
    bool HasAnyPaused,
    DateTimeOffset? HighWaterLastPolledAt,
    string Explanation,
    StudioDaemonPause? PausedByStudio = null,
    int LeadershipPollingMilliseconds = DaemonDefaults.LeadershipPollingMilliseconds,
    IReadOnlyList<string>? CoordinatedDatabases = null)
{
    /// <summary>Whether this process can be asked to start, stop or rebuild anything.</summary>
    public bool IsHostedHere => Hosting == DaemonHostingState.Hosted;

    /// <summary>Whether Marten Studio is the reason this store's agents are not running.</summary>
    public bool IsPausedByStudio => PausedByStudio is not null;

    /// <summary>How often the coordinator restarts agents it finds missing.</summary>
    public TimeSpan LeadershipPollingTime => TimeSpan.FromMilliseconds(LeadershipPollingMilliseconds);

    /// <summary>Every database a pause or resume of this store would reach.</summary>
    public IReadOnlyList<string> Databases => CoordinatedDatabases ?? [];

    /// <summary>
    /// Whether one agent may be started or stopped from here right now.
    /// </summary>
    /// <remarks>
    /// Only while the studio's own pause is in effect. With the coordinator running, its leadership loop
    /// starts every shard missing from <c>CurrentAgents()</c> on its next pass, so a stop is undone
    /// within <see cref="LeadershipPollingTime" /> and the control would be a lie.
    /// </remarks>
    public bool CanControlAgents => IsHostedHere && IsPausedByStudio;

    /// <summary>
    /// Why a per-agent start or stop is refused right now, or <see langword="null" /> when it is not.
    /// </summary>
    /// <remarks>
    /// <see langword="null" /> when no daemon is hosted here as well, because that case already has its
    /// own explanation on the card and two reasons for one disabled button is one too many.
    /// </remarks>
    public string? AgentControlRefusal => IsHostedHere && !IsPausedByStudio
        ? DaemonControlMessages.AgentControlNeedsPause(LeadershipPollingTime)
        : null;

    /// <summary>How long ago the high-water mark was polled, against <paramref name="now" />.</summary>
    public TimeSpan? HighWaterAge(DateTimeOffset now) =>
        HighWaterLastPolledAt is { } polled && polled <= now ? now - polled : null;
}

/// <summary>
/// Everything the projections page renders for one scope.
/// </summary>
/// <param name="Projections">The static model, one row per registered projection.</param>
/// <param name="Progress">Shard progress, sorted by lag descending.</param>
/// <param name="Daemon">The daemon card.</param>
/// <param name="HighWaterMark">The store's highest event sequence, which every lag is measured against.</param>
/// <param name="ReadAt">When this was read, so a stale poll can be told apart from a stalled shard.</param>
internal sealed record ProjectionsView(
    IReadOnlyList<ProjectionInfo> Projections,
    IReadOnlyList<ShardProgress> Progress,
    DaemonStatus Daemon,
    long HighWaterMark,
    DateTimeOffset ReadAt)
{
    /// <summary>An empty view, for a scope that could not be read.</summary>
    public static ProjectionsView Empty { get; } = new(
        [],
        [],
        new DaemonStatus(DaemonHostingState.NotHostedInThisProcess, false, "Unknown", [], false, null, "Nothing has been read yet."),
        0,
        DateTimeOffset.MinValue);

    /// <summary>
    /// Whether to warn that nothing anywhere appears to be running these projections.
    /// </summary>
    /// <remarks>
    /// Three things have to be true at once: the store has async projections, no daemon is hosted in this
    /// process, and no shard has ever advanced. Any one of them alone is ordinary - a host that runs its
    /// daemon in another process is a supported deployment, and the studio must not call it broken.
    /// </remarks>
    public bool NoDaemonAnywhere =>
        !Daemon.IsHostedHere
        && Projections.Any(static x => x.IsAsync)
        && !Progress.Any(static x => x.HasProgressRow && x.Sequence > 0);

    /// <summary>
    /// Progression rows no registered projection claims, worst first.
    /// </summary>
    /// <remarks>
    /// A projection removed from <c>StoreOptions</c>, renamed, or bumped to a version whose shard names
    /// differ leaves its rows in <c>mt_event_progression</c>. They are not noise: they are the reason a
    /// table nothing writes to any more still exists, and the reason a shard name in a log does not
    /// appear in the table above.
    /// </remarks>
    public IReadOnlyList<ShardProgress> UnregisteredShards => [.. Progress.Where(static x => !x.IsRegistered)];
}

/// <summary>
/// The projection facts the Overview page's tiles need, without the whole table.
/// </summary>
/// <param name="ProjectionCount">How many projections are registered.</param>
/// <param name="AsyncProjectionCount">How many of them the daemon is responsible for.</param>
/// <param name="MaxLag">The worst lag of any shard.</param>
/// <param name="WorstShardName">The shard that lag belongs to, or <see langword="null" /> when nothing lags.</param>
/// <param name="PausedCount">How many shards are paused.</param>
/// <param name="FailedCount">How many shards reported a failure.</param>
/// <param name="Daemon">The daemon card's state, so the tile can say "not hosted in this process".</param>
/// <param name="Error">What went wrong reading this, or <see langword="null" />. "Cannot report" is a value.</param>
/// <param name="HighWaterMark">
/// The store's highest event sequence, which every lag on this record is measured against. Its own tile
/// on the Overview, because a high-water mark that is not moving is the first thing to look at when a
/// projection is not either.
/// </param>
/// <param name="Shards">
/// Every shard's progress, worst lag first - the same list and the same order the projections page draws,
/// so the Overview's health table is a prefix of that page rather than a second opinion about it.
/// </param>
internal sealed record ProjectionSummary(
    int ProjectionCount,
    int AsyncProjectionCount,
    long MaxLag,
    string? WorstShardName,
    int PausedCount,
    int FailedCount,
    DaemonStatus Daemon,
    string? Error,
    long HighWaterMark = 0,
    IReadOnlyList<ShardProgress>? Shards = null)
{
    /// <summary>The colour the Overview tile wears.</summary>
    public LagSeverity Severity => ProjectionLagThresholds.Severity(MaxLag);

    /// <summary>The shards, worst lag first, or an empty list when nothing could be read.</summary>
    public IReadOnlyList<ShardProgress> Progress => Shards ?? [];

    /// <summary>Whether anything about the projections is worth an amber dot in the navigation.</summary>
    /// <remarks>
    /// Three things, and each of them alone is enough: a shard lagging past the amber threshold, a shard
    /// that is paused, and a shard that reported a failure. Deliberately not "the daemon is not hosted
    /// here" - a host that runs its daemon in another process is a supported deployment and the studio
    /// must not decorate it as a fault.
    /// </remarks>
    public bool NeedsAttention =>
        MaxLag > ProjectionLagThresholds.Amber || PausedCount > 0 || FailedCount > 0;
}

/// <summary>
/// What a rebuild would cost, shown before the confirm box is typed into.
/// </summary>
/// <param name="ProjectionName">The projection that would be rebuilt.</param>
/// <param name="EventsToReplay">The high-water mark: every event up to it is replayed.</param>
/// <param name="ShardNames">The shards that would restart.</param>
/// <param name="TablesAffected">The tables Marten would rewrite, by name.</param>
/// <param name="Lifecycle">The projection's lifecycle, because rebuilding an inline projection is a different question.</param>
/// <param name="ShardTimeout">
/// The per-shard replay budget this rebuild will be given - <c>MartenStudioOptions.RebuildShardTimeout</c>.
/// Stated in the dialog because Marten applies it <em>after</em> the projection's tables have been torn
/// down, so a replay that overruns leaves them empty.
/// </param>
internal sealed record RebuildScope(
    string ProjectionName,
    long EventsToReplay,
    IReadOnlyList<string> ShardNames,
    IReadOnlyList<string> TablesAffected,
    ProjectionLifecycle Lifecycle,
    TimeSpan ShardTimeout);
