using System.Globalization;

using JasperFx.Events.Projections;

using MartenStudio.Services.Configuration;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Projections;

/// <summary>
/// What the Advance high water mark button and its confirm dialog say the advance does, for the Marten this host runs -
/// pure text, so each version's words are a unit test.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does at every version</b> (v9.51.0 <c>HighWaterDetector.AdvanceHighWaterMarkToLatest</c>, V9.54.1
/// <c>AdvanceToCommittedCeilingAsync</c>): the mark moves to the highest <em>committed</em> <c>seq_id</c> (#4953), and
/// every sequence number below it with no committed event - a gap, or an append still in flight that commits later - is
/// skipped for good by every async projection on that mark. Events that are committed and not yet processed are still
/// processed: the shards' own progress does not move.
/// </para>
/// <para>
/// <b>What changes from Marten 9.52</b> (#5738, <see cref="RuntimeFeature.PerDatabaseHighWaterAdvance" />): the advance
/// opens with <c>EnsureStorageExistsAsync(typeof(IEvent))</c>, so under the database's <c>AutoCreate</c> the first press in
/// a process may create or migrate the event store's tables; and under <c>UseTenantPartitionedEvents</c> it moves each
/// tenant's own mark - the selected tenant's, or every managed tenant's - where below 9.52 it moved only the store-global
/// <c>HighWaterMark</c> row, which the daemon does not read on such a store.
/// </para>
/// </remarks>
internal static class HighWaterAdvanceWording
{
    /// <summary>The confirm dialog's message, before the sentence about which databases it reaches.</summary>
    /// <param name="runtime">The Marten this host runs.</param>
    /// <param name="tenantPartitionedEvents"><c>Events.UseTenantPartitionedEvents</c>.</param>
    /// <param name="tenantId">The tenant the scope selector is on, or <see langword="null" />.</param>
    /// <param name="autoCreate">The resolved database's <c>AutoCreate</c>, by name, or <see langword="null" /> when not read.</param>
    public static string Message(MartenRuntime runtime, bool tenantPartitionedEvents, string? tenantId, string? autoCreate)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        bool from952 = RuntimeFeature.PerDatabaseHighWaterAdvance.IsAvailable(runtime);

        string what =
            "This moves the high water mark to the highest committed event sequence. Every sequence number below the new "
            + "mark that has no committed event is skipped for good by every async projection - an append still in flight "
            + "that commits afterwards included. This exists for retrofitting projections onto a very large event store, and "
            + "it is not reversible.";

        string marks = !tenantPartitionedEvents
            ? string.Empty
            : !from952
                ? " This store numbers its events per tenant (UseTenantPartitionedEvents), and below Marten 9.52 the advance "
                  + "writes only the store-global mark, which the daemon does not read on such a store: no tenant's mark moves."
                : tenantId is { Length: > 0 } tenant
                    ? " This store numbers its events per tenant (UseTenantPartitionedEvents): Marten moves tenant "
                      + tenant + "'s own mark, and no other tenant's."
                    : " This store numbers its events per tenant (UseTenantPartitionedEvents): Marten moves every tenant's own "
                      + "mark, in every database it reaches.";

        return what + marks + (from952 ? StorageSentence(autoCreate, tenantId) : string.Empty);
    }

    /// <summary>
    /// What the enabled button's hover text says it may create, or the empty string below Marten 9.52, where the advance
    /// ensures nothing.
    /// </summary>
    /// <param name="runtime">The Marten this host runs.</param>
    /// <param name="autoCreate">The resolved database's <c>AutoCreate</c>, by name.</param>
    /// <param name="tenantId">The tenant the scope selector is on, or <see langword="null" />: without one the advance reaches every database.</param>
    public static string ButtonNote(MartenRuntime runtime, string? autoCreate, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        return RuntimeFeature.PerDatabaseHighWaterAdvance.IsAvailable(runtime)
            ? StorageSentence(autoCreate, tenantId).TrimStart()
            : string.Empty;
    }

    /// <summary>
    /// The reach sentence for an advance with a tenant selected on a store that numbers its events per tenant, from Marten
    /// 9.52 - where it moves that tenant's mark alone - or <see langword="null" />, where the shared progression-correction
    /// sentence applies.
    /// </summary>
    /// <param name="runtime">The Marten this host runs.</param>
    /// <param name="tenantPartitionedEvents"><c>Events.UseTenantPartitionedEvents</c>.</param>
    /// <param name="tenantId">The tenant the scope selector is on, or <see langword="null" />.</param>
    /// <remarks>
    /// The authorization does not narrow with it: the service still asks about the tenant's database as a whole, as for
    /// every correction (D27), and a per-tenant authorization for the advance is 0.6's, with the per-database overload.
    /// </remarks>
    public static string? TenantReach(MartenRuntime runtime, bool tenantPartitionedEvents, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        return tenantPartitionedEvents && tenantId is { Length: > 0 } tenant && RuntimeFeature.PerDatabaseHighWaterAdvance.IsAvailable(runtime)
            ? " With tenant " + tenant + " selected, Marten does this in that tenant's database and moves only that "
              + "tenant's own mark. It is still refused unless you may change that database as a whole, or it holds this "
              + "tenant and no other."
            : null;
    }

    /// <summary>
    /// What the advance's storage check may do. With a tenant selected it runs in that tenant's database alone, under
    /// that database's <c>AutoCreate</c>. Without one it runs in every database of the store
    /// (<c>Tenancy.BuildDatabases()</c>), each under its own <c>AutoCreate</c>, and the selected database's mode says
    /// nothing about the others - so it is quoted as this database's, and "creates nothing" is never said for all of them.
    /// </summary>
    private static string StorageSentence(string? autoCreate, string? tenantId)
    {
        string mode = autoCreate is { Length: > 0 } named ? named : "not reported";
        bool none = string.Equals(autoCreate, nameof(JasperFx.AutoCreate.None), StringComparison.Ordinal);

        if (tenantId is { Length: > 0 })
        {
            return none
                ? " Marten first ensures the event store's storage, which under this database's AutoCreate (None) creates nothing."
                : " Marten first ensures the event store's storage under this database's AutoCreate (" + mode + "): the first "
                  + "time this process asks, that may create the event store's tables, or migrate them to what the store "
                  + "declares, before the mark is written.";
        }

        return " Marten first ensures the event store's storage in each database it reaches, under each database's own "
            + "AutoCreate (this one's is " + mode + "): the first time this process asks, wherever that is not None, it may "
            + "create the event store's tables, or migrate them to what the store declares, before the mark is written.";
    }
}

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

    /// <summary>
    /// A composite projection's members, by the shard identity each records its progress under
    /// (<c>{Member}:All</c>) - empty for every other projection.
    /// </summary>
    /// <remarks>
    /// A composite runs as one agent, on its own shard, and each member records a progression row of its own in the same
    /// batch (<c>ExecutionStage.ExecuteDownstreamAsync</c>, JasperFx 2.74.0 and 2.95.1): rows the composite's
    /// <c>ShardNames()</c> does not list. They are the composite's, so they are drawn under it - not as "Unregistered
    /// shards" that nothing advances - and they are not agents: nothing starts or stops one alone.
    /// </remarks>
    public IReadOnlyList<string> MemberShardNames { get; init; } = [];

    /// <summary>
    /// Whether this is a subscription registered with <c>Events.Subscribe(...)</c>, which <c>Projections()</c> does not
    /// list and the daemon's own <c>AllShards()</c> does (<see cref="ProgressionRowClassifier.SubscriptionsOf" />). Its
    /// shards are started and stopped like any other; it has no projection to rebuild, and the studio does not rewind it.
    /// </summary>
    public bool IsSubscription { get; init; }
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

    /// <summary>The severity of one lag against a shard's own thresholds, where its progression row carries them.</summary>
    public static LagSeverity Severity(long lag, long? warning, long? critical) =>
        lag > (critical ?? Red) ? LagSeverity.Critical
        : lag > (warning ?? Amber) ? LagSeverity.Warning
        : LagSeverity.Ok;
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
/// <param name="LastProgress">When the shard last advanced, from its row's <c>last_updated</c> (server time).</param>
/// <param name="SinceLastProgress">How long ago that was, in server time, at the moment of the read.</param>
/// <param name="WarningThreshold">The row's own <c>warning_behind_threshold</c>, when one is written.</param>
/// <param name="CriticalThreshold">The row's own <c>critical_behind_threshold</c>, when one is written.</param>
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
    bool IsRegistered = true,
    DateTimeOffset? LastProgress = null,
    TimeSpan? SinceLastProgress = null,
    long? WarningThreshold = null,
    long? CriticalThreshold = null)
{
    /// <summary>How many events this shard has still to process. Never negative.</summary>
    public long Lag => Math.Max(0, HighWater - Sequence);

    /// <summary>The colour this row wears: against the row's own thresholds when it carries them, the studio's otherwise.</summary>
    public LagSeverity Severity => ProjectionLagThresholds.Severity(Lag, WarningThreshold, CriticalThreshold);

    /// <summary>The amber threshold that applies to this row.</summary>
    public long EffectiveWarningThreshold => WarningThreshold ?? ProjectionLagThresholds.Amber;

    /// <summary>The red threshold that applies to this row.</summary>
    public long EffectiveCriticalThreshold => CriticalThreshold ?? ProjectionLagThresholds.Red;

    /// <summary>Whether the shard is paused, by whatever name the daemon gave it.</summary>
    public bool IsPaused =>
        PauseReason is { Length: > 0 }
        || (AgentStatus is { Length: > 0 } status
            && status.Contains("pause", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the shard reported a failure.</summary>
    public bool HasFailed => Failure is { Length: > 0 };

    /// <summary>
    /// Whether this row is a composite projection's member (<see cref="ProjectionInfo.MemberShardNames" />): advanced by
    /// the composite's own agent, with no agent of its own to start or stop.
    /// </summary>
    public bool IsCompositeMember { get; init; }

    /// <summary>
    /// What the store's configuration says the row is (<see cref="ProgressionRowClassifier" />), for a tenant's row and for
    /// a row nothing registered claims - <see langword="null" /> for a projection's own shard, and where no classifier was asked.
    /// </summary>
    public ProgressionRowClass? Classification { get; init; }
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
    NotHostedInThisProcess,

    /// <summary>
    /// The store's <c>Projections.AsyncMode</c> is <c>DaemonMode.ExternallyManaged</c>: an external
    /// system - Wolverine's managed event-subscription distribution, typically - runs its async
    /// projections, and the studio never asks that system's coordinator anything.
    /// </summary>
    /// <remarks>
    /// Its own state rather than a flavour of <see cref="NotHostedInThisProcess" />, because the card says
    /// something different - "External", and that starting, stopping and rebuilding belong to that
    /// system - and because the projections page hides the daemon controls outright instead of disabling
    /// them: there is no option a host could set to make them work here.
    /// </remarks>
    ExternallyManaged
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

    /// <summary>
    /// <c>DaemonSettings.AgentPauseTime</c>'s default: a <c>TimeSpan</c> of one second. Verified against
    /// JasperFx.Events 2.69.3.
    /// </summary>
    public const int AgentPauseMilliseconds = 1_000;

    /// <summary>
    /// <c>DaemonSettings.StopAndDrainTimeout</c>'s default: five seconds. Verified at JasperFx.Events 2.74.0 and 2.87.1.
    /// </summary>
    public const int StopAndDrainMilliseconds = 5_000;
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

    /// <summary>What a resume reaches, which is the same set the pause did.</summary>
    /// <remarks>
    /// Its own sentence rather than <see cref="PauseIsProcessWide" /> reused: an audit entry that told
    /// the reader a resume "stops the projection agents … until somebody resumes them" describes the
    /// opposite of what happened, and the ring is read long after the button was pressed.
    /// </remarks>
    public const string ResumeIsProcessWide =
        "Resuming restarts the projection agents of every database this process hosts for this store.";

    /// <summary>
    /// Why a per-agent start or stop is pointless while the coordinator's leadership loop is running.
    /// </summary>
    /// <remarks>
    /// <b>Two intervals, and the loop uses whichever applies.</b> <c>ProjectionCoordinatorBase</c>'s loop
    /// ends each pass with <c>Task.Delay(leadershipPollingTime)</c> - unless any resolved daemon reports
    /// a paused agent, in which case it is <c>Task.Delay(agentPauseTime)</c>, which defaults to one
    /// second and is therefore five times shorter (verified against JasperFx.Events 2.69.3). Quoting the
    /// leadership interval alone told somebody watching a paused shard that they had five seconds when
    /// they had one, so the sentence names the smaller and says when the other applies.
    /// </remarks>
    /// <param name="leadershipPollingTime">The store's own <c>LeadershipPollingTime</c>.</param>
    /// <param name="agentPauseTime">The store's own <c>AgentPauseTime</c>.</param>
    public static string AgentControlNeedsPause(TimeSpan leadershipPollingTime, TimeSpan agentPauseTime) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The projection coordinator restarts agents as often as every {Sooner(leadershipPollingTime, agentPauseTime).TotalSeconds:0.#} s (LeadershipPollingTime {leadershipPollingTime.TotalSeconds:0.#} s, or AgentPauseTime {agentPauseTime.TotalSeconds:0.#} s while any shard is paused); pause the daemon first.");

    /// <summary>
    /// Why per-agent controls are refused although the studio's pause is still on the record.
    /// </summary>
    /// <remarks>
    /// The studio recorded a pause and the daemon says it is running, which means somebody resumed the
    /// coordinator without going through this studio - the host's own code, another process, a restart.
    /// The leadership loop is therefore back, and a stop issued now would be undone by it; saying so is
    /// the only honest thing to render, because the recorded pause is real and so is the running daemon.
    /// </remarks>
    /// <param name="leadershipPollingTime">The store's own <c>LeadershipPollingTime</c>.</param>
    /// <param name="agentPauseTime">The store's own <c>AgentPauseTime</c>.</param>
    public static string PauseNoLongerInEffect(TimeSpan leadershipPollingTime, TimeSpan agentPauseTime) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Marten Studio's pause is still on the record, but this daemon reports that it is running - something outside the studio resumed it. Its leadership loop restarts agents as often as every {Sooner(leadershipPollingTime, agentPauseTime).TotalSeconds:0.#} s, so pause it again before starting or stopping one.");

    private static TimeSpan Sooner(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}

/// <summary>
/// What the daemon card draws.
/// </summary>
/// <param name="Hosting">Whether a daemon is reachable from this process.</param>
/// <param name="IsRunning">Whether that daemon is running. Meaningless when it is not hosted here.</param>
/// <param name="Mode">The configured <c>DaemonMode</c> - Disabled, Solo, HotCold or ExternallyManaged.</param>
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
/// Every database of this store that a pause would reach <em>and</em> that this visitor is allowed to
/// see. Named in the confirm dialog because pausing is not scoped to the database the selector is on -
/// and filtered through the store policy for the same reason the operation itself is: a dialog that
/// listed a database the visitor may not address would be telling them it exists.
/// </param>
/// <param name="AgentPauseMilliseconds">
/// The store's own <c>AgentPauseTime</c>, which is what the coordinator's loop sleeps for instead while
/// any shard is paused - one second by default, against five for the leadership interval. Both are
/// carried because the refusal hint has to name whichever applies.
/// </param>
/// <param name="RebuildingProjections">The projections the daemon here says it is rebuilding right now (JasperFx 2.86+).</param>
/// <param name="StopAndDrainMilliseconds">
/// The store's <c>StopAndDrainTimeout</c>, which the Pause, Stop and Rebuild wording quotes - <c>0</c> when it is not
/// positive (zero, negative, or <c>Timeout.InfiniteTimeSpan</c>), for which JasperFx applies no bound at all.
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
    IReadOnlyList<string>? CoordinatedDatabases = null,
    int AgentPauseMilliseconds = DaemonDefaults.AgentPauseMilliseconds,
    IReadOnlyList<string>? RebuildingProjections = null,
    int StopAndDrainMilliseconds = DaemonDefaults.StopAndDrainMilliseconds)
{
    /// <summary>
    /// Whether the daemon here says it is rebuilding <paramref name="projection" /> (JasperFx 2.86+). A no proves nothing:
    /// below 2.86, and for a daemon that does not track it, the list is empty.
    /// </summary>
    public bool IsRebuildingHere(string projection) =>
        (RebuildingProjections ?? []).Contains(projection, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, bool> NoLeadership =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which coordinator runs the daemon here (M7), or <see cref="DaemonCoordination.None" /> when none is hosted here.</summary>
    public DaemonCoordination Coordination { get; init; }

    /// <summary>How its leadership loop divides the shards between nodes, where it has one.</summary>
    public DaemonDistribution Distribution { get; init; }

    /// <summary>
    /// For each registered async shard of this database, by store-global identity, whether this node holds the leadership
    /// lock it runs under (<see cref="DaemonLeadership" />) - under HotCold only; empty otherwise, and where it could not be told.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Leadership { get; init; } = NoLeadership;

    /// <summary>Whether shards run on whichever node holds their lock: Marten's coordinator under HotCold.</summary>
    public bool IsHotCold =>
        Coordination == DaemonCoordination.LeadershipLoop && Distribution is DaemonDistribution.PerShard or DaemonDistribution.PerDatabase;

    /// <summary>Whether the daemon here has an agent for <paramref name="shardName" />, in any status.</summary>
    public bool RunsHere(string shardName) =>
        Agents.Any(x => string.Equals(x.ShardName, shardName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the daemon here has a running agent for <paramref name="shardName" />, which a Start would leave as it is.</summary>
    public bool IsRunningHere(string shardName) =>
        Agents.Any(x => string.Equals(x.ShardName, shardName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Status, "Running", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this node holds the leadership lock <paramref name="shardName" /> runs under - always, under Solo; per lock,
    /// under HotCold - or <see langword="null" /> where there is no such lock to hold or it could not be told.
    /// </summary>
    public bool? LeadsHere(string shardName)
    {
        if (Coordination != DaemonCoordination.LeadershipLoop)
        {
            return null;
        }

        if (Distribution == DaemonDistribution.Solo)
        {
            return true;
        }

        if (!IsHotCold || !ShardName.TryParse(shardName, out ShardName? shard) || shard is null)
        {
            return null;
        }

        string neutral = shard.TenantId is null ? shard.Identity : ShardName.Compose(shard.Name, shard.ShardKey, null, shard.Version).Identity;
        return Leadership.TryGetValue(neutral, out bool leads) ? leads : null;
    }

    /// <summary>How long a stopping shard may take to finish the page it is processing.</summary>
    public TimeSpan StopAndDrainTimeout => TimeSpan.FromMilliseconds(StopAndDrainMilliseconds);

    /// <summary>Whether a stop is bounded at all: JasperFx cancels a drain only after a timeout greater than zero.</summary>
    public bool IsDrainBounded => StopAndDrainMilliseconds > 0;

    /// <summary>Whether this process can be asked to start, stop or rebuild anything.</summary>
    public bool IsHostedHere => Hosting == DaemonHostingState.Hosted;

    /// <summary>
    /// Whether an external system runs this store's async projections, so that starting, stopping and
    /// rebuilding belong to it and are not offered here at all.
    /// </summary>
    public bool IsExternallyManaged => Hosting == DaemonHostingState.ExternallyManaged;

    /// <summary>Whether Marten Studio is the reason this store's agents are not running.</summary>
    public bool IsPausedByStudio => PausedByStudio is not null;

    /// <summary>How often the coordinator restarts agents it finds missing.</summary>
    public TimeSpan LeadershipPollingTime => TimeSpan.FromMilliseconds(LeadershipPollingMilliseconds);

    /// <summary>What that loop sleeps for instead while any shard is paused.</summary>
    public TimeSpan AgentPauseTime => TimeSpan.FromMilliseconds(AgentPauseMilliseconds);

    /// <summary>Every database a pause or resume of this store would reach.</summary>
    public IReadOnlyList<string> Databases => CoordinatedDatabases ?? [];

    /// <summary>
    /// Whether the studio's recorded pause and the daemon's own answer disagree.
    /// </summary>
    /// <remarks>
    /// The studio recorded a pause, and this daemon says it is running. That is not a studio bug and not
    /// a stale record: <c>PauseAsync</c> stops the leadership runner and then every resolved daemon, and
    /// <c>JasperFxAsyncDaemon.IsRunning</c> is its high-water agent's <c>IsRunning</c>, which
    /// <c>StopAllAsync</c> stops - so a pause that is still in effect reports
    /// <see cref="IsRunning" /> <see langword="false" />. Seeing it <see langword="true" /> means
    /// somebody resumed the coordinator outside this studio: the host's own code, another process, a
    /// restart. The leadership loop is back, so the controls that only make sense while it is stopped go
    /// away and the card says why rather than continuing to claim the pause holds.
    /// </remarks>
    public bool PauseNoLongerInEffect => IsHostedHere && IsPausedByStudio && IsRunning;

    /// <summary>
    /// Whether one agent may be started or stopped from here right now.
    /// </summary>
    /// <remarks>
    /// Only while the studio's own pause is in effect <em>and</em> the daemon agrees it is stopped. With
    /// the coordinator running, its leadership loop starts every shard missing from
    /// <c>CurrentAgents()</c> on its next pass, so a stop is undone within
    /// <see cref="LeadershipPollingTime" /> - or within <see cref="AgentPauseTime" /> when any shard is
    /// paused - and the control would be a lie. The recorded pause alone is not enough for that promise,
    /// because the record says what the studio did and not what is true now.
    /// <para>
    /// <b>Except where there is no loop</b> (M7): <c>MartenDaemonModeIsSolo()</c>'s <c>ExplicitProjectionCoordinator</c>
    /// starts an agent only when asked and never restarts one, so a Start or a Stop there means what it says without a
    /// pause (<see cref="DaemonCoordination.Explicit" />).
    /// </para>
    /// </remarks>
    public bool CanControlAgents =>
        IsHostedHere && (Coordination == DaemonCoordination.Explicit || (IsPausedByStudio && !IsRunning));

    /// <summary>
    /// Why a per-agent start or stop is refused right now, or <see langword="null" /> when it is not.
    /// </summary>
    /// <remarks>
    /// <see langword="null" /> when no daemon is hosted here as well, because that case already has its
    /// own explanation on the card and two reasons for one disabled button is one too many.
    /// </remarks>
    public string? AgentControlRefusal => (IsHostedHere, IsPausedByStudio, IsRunning) switch
    {
        (false, _, _) => null,
        _ when Coordination == DaemonCoordination.Explicit => null,
        (true, true, true) => DaemonControlMessages.PauseNoLongerInEffect(LeadershipPollingTime, AgentPauseTime),
        (true, false, _) => DaemonControlMessages.AgentControlNeedsPause(LeadershipPollingTime, AgentPauseTime),
        _ => null,
    };

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
/// <param name="HasEventStore">
/// Whether this database has an event store at all.
/// <para>
/// A third state beside "nothing has happened" and "could not read": the tables are simply not there, so
/// there is no progression to report and no high-water mark to measure against, and every number on this
/// record is zero for that reason rather than because a projection has stalled. The studio answers it
/// from <c>information_schema</c> and never by asking Marten, whose own progress reads would create the
/// event store to answer (hard rule 14) - so a page that draws this as "no event store in this database"
/// is drawing the fact, not a failure.
/// </para>
/// </param>
/// <param name="DatabaseId">The resolved database's identity, which the health advisor's history is keyed by.</param>
internal sealed record ProjectionsView(
    IReadOnlyList<ProjectionInfo> Projections,
    IReadOnlyList<ShardProgress> Progress,
    DaemonStatus Daemon,
    long HighWaterMark,
    DateTimeOffset ReadAt,
    bool HasEventStore = true,
    string? DatabaseId = null)
{
    /// <summary>An empty view, for a scope that could not be read.</summary>
    public static ProjectionsView Empty { get; } = new(
        [],
        [],
        new DaemonStatus(DaemonHostingState.NotHostedInThisProcess, false, "Unknown", [], false, null, "Nothing has been read yet."),
        0,
        DateTimeOffset.MinValue);

    private static readonly IReadOnlyDictionary<string, ShardTrend> NoTrends =
        new Dictionary<string, ShardTrend>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each shard's lag trend from the health advisor's samples, judged as D04 judges it (a pause, a rebuild, an agent
    /// that is not running, a shard or a mark that has not moved withhold it) - only for a visitor the store policy
    /// passes for the database as a whole, because the samples are database-wide numbers the advisor reads at that
    /// gate, and only while the samples are recent. Empty otherwise: no arrow is drawn.
    /// </summary>
    public IReadOnlyDictionary<string, ShardTrend> Trends { get; init; } = NoTrends;

    /// <summary>The advisor's samples as this visitor may read them, for the rebuild dialog's estimate.</summary>
    public HealthHistoryView HealthHistory { get; init; } = HealthHistoryView.Empty;

    /// <summary>
    /// <c>Events.UseTenantPartitionedEvents</c>: each tenant numbers its events and keeps its own high-water mark, which
    /// is what the Advance high water mark dialog has to say from Marten 9.52 (<see cref="HighWaterAdvanceWording" />).
    /// </summary>
    public bool TenantPartitionedEvents { get; init; }

    /// <summary>
    /// The resolved database's <c>AutoCreate</c>, by name - what the advance's storage check runs under from Marten 9.52 -
    /// or <see langword="null" /> when it was not read.
    /// </summary>
    public string? DatabaseAutoCreate { get; init; }

    /// <summary>
    /// Whether to warn that nothing anywhere appears to be running these projections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four things have to be true at once: the store has async projections, no daemon is hosted in this
    /// process, no shard has ever advanced - and there is something to have advanced over. Any one of
    /// them alone is ordinary - a host that runs its daemon in another process is a supported deployment,
    /// and the studio must not call it broken.
    /// </para>
    /// <para>
    /// The last one is <see cref="HighWaterMark" /> above zero. A store that has never appended an event
    /// has projections that have never advanced because there has been nothing to project, and a banner
    /// with <c>role="alert"</c> saying nothing appears to be running them - or, for an externally managed
    /// store, that the external system "has not processed anything yet" - was an alarm about a fresh
    /// install. The page has the progress rows and the high-water tile to say the rest.
    /// </para>
    /// </remarks>
    public bool NoDaemonAnywhere =>
        !Daemon.IsHostedHere
        && HighWaterMark > 0
        && Projections.Any(static x => x.IsAsync)
        && !Progress.Any(static x => x.HasProgressRow && x.Sequence > 0);

    /// <summary>
    /// The progression rows nothing this store registers advances (M4) - counted for everyone the scope admits, named only
    /// for a visitor the store policy allows for the database as a whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projection removed from <c>StoreOptions</c>, renamed, or bumped to a version whose shard names differ, and a
    /// subscription that was removed, leave their rows in <c>mt_event_progression</c>. They are not noise: they are the
    /// reason a shard name in a log does not appear in the table above, and a projection registered again under the same
    /// name and version resumes from them.
    /// </para>
    /// <para>
    /// They replace 0.5's "Unregistered shards", which was every row no projection's <c>ShardNames()</c> listed - and so
    /// also every row of a subscription registered with <c>Events.Subscribe(...)</c> and, under
    /// <c>UseTenantPartitionedEvents</c>, every tenant's row of a registered shard. Those are registered, and are drawn
    /// under their projection or subscription (<see cref="ProgressionRowClassifier" />). They are not in
    /// <see cref="Progress" />, so an orphan's lag - which only grows - is never the worst lag on the screen or the Overview.
    /// </para>
    /// </remarks>
    public OrphanedProgression Orphans { get; init; } = OrphanedProgression.None;
}

/// <summary>The orphaned progression rows of one database, as one visitor may see them.</summary>
/// <param name="Count">How many there are - shown to everyone the scope admits.</param>
/// <param name="Rows">The rows, oldest name first - empty when <paramref name="Withheld" /> is set.</param>
/// <param name="Withheld">Why the names are not shown to this visitor, or <see langword="null" /> when they are.</param>
internal sealed record OrphanedProgression(int Count, IReadOnlyList<OrphanedProgressionRow> Rows, string? Withheld = null)
{
    /// <summary>None at all.</summary>
    public static OrphanedProgression None { get; } = new(0, []);

    /// <summary>Whether this visitor is shown the rows' names.</summary>
    public bool NamesShown => Withheld is null;

    /// <summary>The statements that would remove the rows that may be removed - for the host's own migrations.</summary>
    public IReadOnlyList<string> RemovalStatements => [.. Rows.Select(static x => x.RemovalSql).OfType<string>()];

    /// <summary>Marten's own calls that would remove the same rows - for the host's own code.</summary>
    public IReadOnlyList<string> RemovalCalls => [.. Rows.Select(static x => x.RemovalCall).OfType<string>()];
}

/// <summary>One progression row nothing this store registers advances.</summary>
/// <param name="Name">The row's <c>name</c>.</param>
/// <param name="Sequence">Its <c>last_seq_id</c>: how far it had got when something last advanced it.</param>
/// <param name="LastUpdated">When it was last written (<c>last_updated</c>, server time), when the table has the column.</param>
/// <param name="Reason">Why nothing advances it.</param>
/// <param name="Explanation">The same, as a sentence for the page (<see cref="OrphanWording.Explain" />).</param>
/// <param name="TenantId">The tenant its name carries, or <see langword="null" />.</param>
/// <param name="ReadByDaemon">Whether the daemon may still read it (<see cref="ProgressionRowClass.ReadByDaemon" />).</param>
/// <param name="RemovalSql">
/// The statement that removes it, for the host's own migrations - <see langword="null" /> for a row the daemon may still
/// read, and for a name or schema no statement can print honestly (<c>MigrationSqlText.DeleteRowWhereEquals</c>).
/// </param>
/// <param name="RemovalCall">
/// Marten's own call that removes it, for the host's own code (<see cref="OrphanWording.MartenCall" />) - <see langword="null" />
/// where <paramref name="RemovalSql" /> is.
/// </param>
internal sealed record OrphanedProgressionRow(
    string Name,
    long Sequence,
    DateTimeOffset? LastUpdated,
    OrphanReason Reason,
    string Explanation,
    string? TenantId,
    bool ReadByDaemon,
    string? RemovalSql,
    string? RemovalCall = null);

/// <summary>
/// What the projections screen and D10 say about orphaned progression rows - pure text, so each sentence is a unit test.
/// </summary>
internal static class OrphanWording
{
    /// <summary>The section's heading.</summary>
    public const string Heading = "Orphaned progression rows";

    /// <summary>The hand-over: what the rows are, and that removing one is the host's decision.</summary>
    /// <remarks>
    /// Marten's removal by name is <c>IEventDatabase.DeleteProjectionProgressByShardNameAsync</c> (marten#4785,
    /// jasperfx#473), public at <c>V9.39.0</c> and <c>v9.54.2</c>: it deletes the row whose name is the identity given,
    /// whether or not a projection of that name is registered, after <c>EnsureStorageExistsAsync(typeof(IEvent))</c>.
    /// <c>IEventStore.DeleteProjectionProgressAsync</c> looks a projection's name up among the registered ones and throws
    /// <see cref="ArgumentOutOfRangeException" /> for any other, so it cannot.
    /// </remarks>
    public const string HandOver =
        "These rows are in mt_event_progression, and nothing this store registers advances them: each was written by a "
        + "projection that has since been renamed, removed or given a new version, or by a subscription that was removed. "
        + "Whether to remove one is the host's decision, and Marten Studio never does. Marten's own "
        + "IEventDatabase.DeleteProjectionProgressByShardNameAsync removes a row by its exact name, registered or not - "
        + "DeleteProjectionProgressAsync, which looks a projection's name up among the registered ones, refuses one that is "
        + "not. A projection registered again under the same name and version resumes from its row's position rather than "
        + "replaying from the first event.";

    /// <summary>What introduces Marten's calls in the hand-over block.</summary>
    public const string CallLead = "From the host's own code, through Marten - database is this database's IMartenDatabase:";

    /// <summary>What introduces the SQL in the hand-over block.</summary>
    public const string SqlLead = "Or as SQL, in a migration:";

    /// <summary>Why the names are not shown to a visitor the store policy refuses the database as a whole.</summary>
    public const string NamesWithheld =
        "Their names are shown only to a visitor the store policy allows for this database as a whole, with no tenant "
        + "selected in the policy's resource: they are the database's, not one tenant's.";

    /// <summary>The heading the removal statements are drawn under - the health advisor's own (<c>HealthRemedy.MigrationHeading</c>).</summary>
    public const string SqlHeading = "For your own migrations - the studio never runs this";

    /// <summary>What the removal statements do and do not do.</summary>
    public const string SqlNote =
        "Each removes one progression row in this database and nothing else: the documents and tables the projection wrote "
        + "stay. Marten's call first ensures the event store's storage, as every write to it does. Run either once the "
        + "rename or removal has shipped, in the database this page is about. A row the daemon may still read has neither here.";

    /// <summary>
    /// <c>await ((IEventDatabase) database).DeleteProjectionProgressByShardNameAsync("name");</c> - Marten's own removal of
    /// one progression row by its exact name, as C# a person pastes into the host's code - or <see langword="null" /> for a
    /// name that would not read on screen as what it is (a control, format or line-separator character).
    /// </summary>
    /// <param name="name">The row's name, as the table holds it.</param>
    public static string? MartenCall(string name) =>
        PostgresStudio.Internal.Sql.SqlLiteralText.IsPrintableText(name)
            ? "await ((IEventDatabase) database).DeleteProjectionProgressByShardNameAsync(\""
              + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
              + "\");"
            : null;

    /// <summary>"1 row" or "3 rows".</summary>
    public static string Count(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " orphaned progression row" : " orphaned progression rows");

    /// <summary>Why nothing advances one row, in a sentence.</summary>
    /// <param name="name">The row's name.</param>
    /// <param name="row">What the classifier said of it.</param>
    public static string Explain(string name, ProgressionRowClass row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string projection = row.Projection ?? ProjectionPart(name);

        return row.Reason switch
        {
            OrphanReason.NoSuchProjection =>
                "No projection or subscription named " + ProjectionPart(name) + " is registered: it was renamed or removed.",
            OrphanReason.OtherVersion when row.RowVersion < row.RegisteredVersion =>
                projection + " is registered at version " + Version(row.RegisteredVersion) + ", and this row is version "
                + Version(row.RowVersion) + "'s, which nothing advances any more."
                + (row.ReadByDaemon
                    ? " Version " + Version(row.RegisteredVersion) + " gates its side effects behind the prior version "
                      + "(GateSideEffectsBehindPriorVersion): the daemon reads this row each time that version starts, and "
                      + "suppresses side effects for events up to its position, so it is not offered for removal."
                    : string.Empty),
            OrphanReason.OtherVersion =>
                projection + " is registered at version " + Version(row.RegisteredVersion) + ", and this row is version "
                + Version(row.RowVersion) + "'s - a later version this deployment does not run.",
            OrphanReason.ShardNotRegistered =>
                projection + " is registered, and no longer runs this shard.",
            OrphanReason.NotAsync =>
                projection + " is registered inline or live: the async daemon does not run it, so nothing advances this row.",
            OrphanReason.TenantRowWithoutPartitioning =>
                "One tenant's row of " + projection + ", and this store does not number its events per tenant "
                + "(Events.UseTenantPartitionedEvents is off): its daemon runs the store-global agent only.",
            _ => "Not a shard name Marten writes, so nothing this store registers claims it.",
        };
    }

    /// <summary>A name's first segment - the projection or subscription it was written by.</summary>
    private static string ProjectionPart(string name)
    {
        int separator = name.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 ? name[..separator] : name;
    }

    private static string Version(uint? version) => (version ?? 1).ToString(CultureInfo.InvariantCulture);
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
/// <param name="EventsToReplay">
/// The events a rebuild replays: the high-water mark - every event up to it - below Marten 9.52, and from 9.52 the
/// high-water mark less the projection's stored start floor, where one is stored (<see cref="Floor" />). A floor
/// configured in code is invisible to the studio and is not taken off.
/// </param>
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
    TimeSpan ShardTimeout)
{
    /// <summary>
    /// What the rebuild replays from, on a Marten that replays from start floors (9.52+,
    /// <c>RuntimeFeature.ProjectionStartFloors</c>); <see langword="null" /> below it, where every rebuild replays from
    /// the first event.
    /// </summary>
    public RebuildFloor? Floor { get; init; }
}

/// <summary>
/// The start floor a rebuild replays from, from Marten 9.52 (JasperFx.Events 2.93's <c>DetermineRebuildFloorAsync</c>): a
/// stored floor first, then a floor the projection's code configures (<c>SubscribeFromSequence</c>,
/// <c>SubscribeFromTime</c>), else the first event.
/// </summary>
/// <remarks>
/// <para>
/// The teardown is still the whole database's, so a rebuild of a floored projection deletes every document it wrote and
/// rebuilds only those the events above the floor reach. That is what the dialog has to say.
/// </para>
/// <para>
/// Per-tenant floors are counted, never named: the dialog is shown to a visitor whose scope may be one tenant, and the
/// floors' keys are the tenant list (D27's rule for per-tenant partitions and sequences).
/// </para>
/// </remarks>
/// <param name="Stored">
/// The store-global floor stored for the projection (<c>tenant_id</c> the empty string), or <see langword="null" /> when
/// none is stored.
/// </param>
/// <param name="TableExists">
/// Whether <c>mt_event_projection_floors</c> exists in this database. A store migrated below 9.52 has none, so it stores no
/// floor - and the first start of a <c>SubscribeFromFirstRun</c> projection there fails with <c>42P01</c>.
/// </param>
/// <param name="TenantFloorCount">How many tenants have a floor of their own (under <c>UseTenantPartitionedEvents</c>).</param>
/// <param name="LowestTenantFloor">The lowest of those floors, or <see langword="null" />.</param>
/// <param name="HighestTenantFloor">The highest of those floors, or <see langword="null" />.</param>
internal sealed record RebuildFloor(
    long? Stored,
    bool TableExists = true,
    int TenantFloorCount = 0,
    long? LowestTenantFloor = null,
    long? HighestTenantFloor = null)
{
    /// <summary>No floor stored, in a database that has the table.</summary>
    public static RebuildFloor None { get; } = new(Stored: null);

    /// <summary>The floors read from <c>mt_event_projection_floors</c>, or <see langword="null" /> when it does not exist.</summary>
    /// <param name="rows">The rows for one projection, every tenant's.</param>
    /// <param name="tenantPartitionedEvents">
    /// <c>Events.UseTenantPartitionedEvents</c>, which decides which rows the daemon reads: a rebuild asks for the floor of
    /// each agent's own <c>ShardName.TenantId</c> - every tenant's under per-tenant partitioning, and otherwise none, so
    /// the store-global row. The other kind is never read by this store's rebuild, and is not counted.
    /// </param>
    public static RebuildFloor From(IReadOnlyList<MartenStudio.Internal.Sql.ProjectionFloorRow>? rows, bool tenantPartitionedEvents = false)
    {
        if (rows is null)
        {
            return new RebuildFloor(null, TableExists: false);
        }

        long? stored = tenantPartitionedEvents ? null : rows.FirstOrDefault(static x => x.TenantId.Length == 0)?.Floor;
        long[] tenants = tenantPartitionedEvents ? [.. rows.Where(static x => x.TenantId.Length > 0).Select(static x => x.Floor)] : [];

        return new RebuildFloor(
            stored,
            TableExists: true,
            tenants.Length,
            tenants.Length > 0 ? tenants.Min() : null,
            tenants.Length > 0 ? tenants.Max() : null);
    }

    /// <summary>The events a rebuild replays below <paramref name="highWaterMark" />: all of them less the stored floor.</summary>
    /// <param name="highWaterMark">The database's high-water mark.</param>
    public long EventsToReplay(long highWaterMark) =>
        Stored is { } floor ? Math.Max(0, highWaterMark - floor) : highWaterMark;
}

/// <summary>
/// What the rebuild dialog says about where a replay starts - pure text, from the <see cref="RebuildFloor" /> the service
/// read, so each Marten's wording is a unit test rather than a render.
/// </summary>
/// <remarks>
/// Below Marten 9.52 there is no floor and the words are the ones the dialog always had: "from the beginning", and no
/// row or note of its own.
/// </remarks>
internal static class RebuildWording
{
    /// <summary>How the dialog's one-sentence summary ends: where the replay starts.</summary>
    /// <param name="floor">The floor, or <see langword="null" /> below 9.52.</param>
    public static string ReplayPhrase(RebuildFloor? floor) => floor switch
    {
        null => "from the beginning",
        { Stored: { } stored } => "from its stored start floor, after sequence " + Number(stored) + " - not from the beginning",
        { TenantFloorCount: > 0 } => "from each tenant's stored start floor, where it has one",
        _ => "from the beginning, unless its code configures a start floor",
    };

    /// <summary>The "Replays from" row, or <see langword="null" /> below 9.52, where the dialog has none.</summary>
    /// <param name="floor">The floor, or <see langword="null" />.</param>
    public static string? ReplaysFrom(RebuildFloor? floor) => floor switch
    {
        null => null,
        { Stored: { } stored } => "after sequence " + Number(stored) + ": the start floor stored for it",
        { TenantFloorCount: > 0 and var count } =>
            "each tenant's own start floor: " + count.ToString(CultureInfo.InvariantCulture)
            + (count == 1 ? " tenant has one" : " tenants have one")
            + (floor.LowestTenantFloor == floor.HighestTenantFloor
                ? ", after sequence " + Number(floor.LowestTenantFloor ?? 0)
                : ", after sequences " + Number(floor.LowestTenantFloor ?? 0) + " to " + Number(floor.HighestTenantFloor ?? 0)),
        { TableExists: false } =>
            "the first event, unless its code configures a start floor: this database has no mt_event_projection_floors, "
            + "so none is stored",
        _ => "the first event, unless its code configures a start floor",
    };

    /// <summary>What a floor does to the projection's documents, or <see langword="null" /> below 9.52.</summary>
    /// <param name="floor">The floor, or <see langword="null" />.</param>
    public static string? FloorNote(RebuildFloor? floor) => floor switch
    {
        null => null,
        { Stored: not null } or { TenantFloorCount: > 0 } =>
            "The tables are emptied for the whole database, and only the events above the floor are replayed: a document "
            + "that only events at or below it built is deleted and not rebuilt. A projection whose code subscribes from the "
            + "present ignores a stored floor and replays from the first event.",
        _ =>
            "Marten replays from a start floor when one is stored for the projection - none is - or when its code subscribes "
            + "from a sequence or a time (SubscribeFromSequence, SubscribeFromTime), which Marten Studio cannot see. Then "
            + "fewer events are replayed, and a document that only events at or below the floor built is deleted and not rebuilt.",
    };

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
