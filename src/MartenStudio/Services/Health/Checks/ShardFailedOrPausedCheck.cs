using System.Globalization;
using System.Reflection;

using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D05 reads.</summary>
/// <param name="Configuration">The store's daemon settings.</param>
/// <param name="Rows">
/// The progression rows the extended tracking says are not running or carry a failure, or <see langword="null" /> when
/// the store does not write that tracking (or the table lacks it) and only the daemon in this process can say.
/// </param>
/// <param name="InProcess">The daemon in this process, its agents, and what the studio recorded.</param>
internal sealed record ShardFailedOrPausedFacts(
    DaemonCheckConfiguration Configuration,
    IReadOnlyList<DaemonShardRow>? Rows,
    DaemonInProcess InProcess)
{
    private static readonly IReadOnlySet<string> NoShards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shards an earlier sample of this database recorded failed or paused (<see cref="DaemonSeries.ShardFailedAt" />),
    /// read from the advisor's history at D05's own gate when the facts are gathered: the shards whose agents' positions D05
    /// samples in a series of its own (<see cref="DaemonSeries.AgentPosition" />), so that its progress rule has a position
    /// where D03 records none.
    /// </summary>
    public IReadOnlySet<string> Remembered { get; init; } = NoShards;
}

/// <summary>
/// D05: a shard has failed or is paused - or keeps failing and being restarted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, and the fresher wins.</b> The daemon hosted in this process says what each agent is doing right now
/// (<c>ISubscriptionAgent.Status</c> and, from JasperFx 2.74, <c>Failure</c>). The progression table says what the
/// node that ran the shard last wrote - under <c>EnableExtendedProgressionTracking</c> only, which writes the status,
/// pause reason and classified failure on every status change (and is off by default: K02). A row that says Paused
/// on a node that has since gone keeps saying it, so the time it was written is evidence.
/// </para>
/// <para>
/// <b>What fires.</b> Stopped with a failure: Critical - the error policy gave up and nothing restarts it. Paused: Warning
/// - Marten pauses a shard after a failure and its coordinator starts it again once <c>HealthCheckPollingTime</c> has
/// passed, so a pause that lasts, or comes back, is one that keeps failing. Stopped with no failure is somebody's stop (the
/// studio's coordinator pause, a host shutting down) and is not reported; whether anything runs the daemon at all is D02's.
/// A shard whose projection is being rebuilt is stopped by the rebuild, on purpose.
/// </para>
/// <para>
/// <b>A shard that keeps failing reads Running between attempts.</b> The coordinator starts a paused agent again once
/// <c>Projections.HealthCheckPollingTime</c> (five seconds by default) has passed since it paused
/// (<c>ProjectionCoordinatorBase.startAgentsIfNecessaryAsync</c>, JasperFx 2.74.0 and 2.87.1). What it starts is a new
/// agent - <c>Running</c>, with no <c>Failure</c> and no <c>PausedTime</c> - until that agent reaches the failing event
/// again, and its <c>Started</c> write clears the progression row's failure columns at the same moment
/// (<c>MartenDatabase.WriteExtendedProgressionAsync</c>, Marten 9.39.0 and 9.50.2). Neither keeps the failure across the
/// restart, so the advisor does, in its history: a sweep that sees a shard failed or paused samples where it stood, when
/// its pause began and how it failed (<see cref="DaemonSeries.ShardFailedAt" /> and its two siblings). A later sweep that
/// finds the agent here Running at exactly that position - the floor a restart resumes from - or at 0, before its start is
/// processed, keeps the shard, as one that keeps failing and being restarted, and so does a sweep that finds it paused
/// again, by a later pause, no further on. Committed progress past the position ends it - any later sample that saw the
/// shard's position (D03's series) beyond it, so a shard that failed once and got past the event is quiet from its next
/// page - and so does <see cref="RestartMemoryFor" /> (10 min, or three sampling intervals) without a sample that saw it
/// fail again or still standing where it failed.
/// </para>
/// <para>
/// <b>A position series of D05's own</b> (0.6, M2). A store D03 does not judge - one that partitions its events by tenant,
/// <c>ShardStalledCheck</c> - samples no positions, so in 0.4 and 0.5 only the failures themselves renewed the memory there,
/// and progress never ended it: a shard that recovered and was then restarted for another reason, reading 0, was reported
/// again until the memory ran out. So D05 samples, for each shard its history remembers failing
/// (<see cref="ShardFailedOrPausedFacts.Remembered" />), where the daemon in this process has that shard's agent now
/// (<see cref="DaemonSeries.AgentPosition" />: <c>ISubscriptionAgent.Position</c>, its last committed sequence, JasperFx
/// 2.74.0 and 2.95.1), and the progress and renewal rules read that series beside D03's. Only remembered shards, so the
/// series costs nothing for a shard that never failed. A studio beside a daemon in another process has no agent to ask,
/// and is where it was.
/// </para>
/// <para>
/// <b>What that cannot see.</b> The first sweep after watching begins, when it lands between a restart and the next
/// failure; and a studio beside a daemon in another process, whose only source is the row - which the restart has
/// cleared, and which the problem-rows statement does not return - in the same moment. It sees such a shard paused, and
/// paused again, like any other.
/// </para>
/// <para>
/// <b>Classified where JasperFx classifies.</b> A start the studio asked for and the daemon refused carries
/// <c>ShardStartException.Reason</c> and whether it is transient (JasperFx 2.76+, F1); a live failure is classified as
/// transient or permanent by <c>TransientFailures.IsTransient</c> where that exists (JasperFx 2.89, first in Marten
/// 9.50.3). A shard of one tenant names that tenant, so its finding is shown only through the activity gate and only to a
/// visitor the store policy lets see the tenant.
/// </para>
/// <para>
/// <b>A database error is not an apply error's remedy.</b> From JasperFx.Events 2.93 (Marten 9.52, jasperfx#1029/#1064 and
/// #5741) a non-transient database exception in projection code - a missing column or table, a permission - is attributed
/// to its event as <c>ApplyEvent</c> and is never dead-lettered, even under <c>SkipApplyErrors</c>: the shard pauses
/// instead (<see cref="RuntimeFeature.SystemicDatabaseFailure" />). So the <c>SkipApplyErrors</c> remedy is dropped where
/// the daemon here says the failure is one - the live exception through JasperFx's own classifier, or its classified failure
/// (permanent, with a database exception at its root) - kept as it was where it says it is not, and kept with that case
/// named where nothing can say, which is every failure read back from <c>mt_event_progression</c>. Below 2.93 such an error
/// escaped unwrapped as <c>Other</c>, and the remedy stands as it always did.
/// </para>
/// </remarks>
[HealthCheck("D05", HealthArea.Daemon, HealthCostTier.Catalog, "Shard failed or paused")]
internal sealed class ShardFailedOrPausedCheck : HealthCheck<ShardFailedOrPausedFacts>
{
    /// <summary>
    /// How long a failure a sweep saw keeps a restarted shard reported while its agent reads Running where it failed: the
    /// daemon checks' trailing window (<see cref="DaemonThresholds.TrendWindow" />, 10 min), or three of the advisor's
    /// sampling intervals (<c>EffectiveHealthInterval</c>, which the facts carry as
    /// <see cref="DaemonCheckConfiguration.HealthInterval" />) where that is longer - so a host that samples every hour still
    /// holds the sample that saw the failure when the next one comes. Each later sample that saw the shard fail again, or
    /// still standing exactly where it failed - which is what a restarted agent short of its failure does - starts it again;
    /// progress past the failure ends it.
    /// </summary>
    internal static TimeSpan RestartMemoryFor(TimeSpan healthInterval)
    {
        TimeSpan threeIntervals = healthInterval * 3;
        return threeIntervals > DaemonThresholds.TrendWindow ? threeIntervals : DaemonThresholds.TrendWindow;
    }

    /// <inheritdoc />
    public override async Task<ShardFailedOrPausedFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        DaemonCheckConfiguration configuration = DaemonCheckConfiguration.Read(context);
        DaemonInProcess inProcess = await DaemonInProcess.ReadAsync(context, configuration.AsyncShards, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> remembered = RememberedOf(context);

        if (!configuration.ExtendedProgressionTracking)
        {
            return new ShardFailedOrPausedFacts(configuration, null, inProcess) { Remembered = remembered };
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);

        if (shape.Progression is null || !shape.ProgressionColumns.Contains("agent_status", StringComparer.Ordinal))
        {
            return new ShardFailedOrPausedFacts(configuration, null, inProcess) { Remembered = remembered };
        }

        IReadOnlyList<DaemonShardRow> rows = await DaemonShardRow
            .ReadProblemsAsync(context, shape, configuration.EventSchema, cancellationToken)
            .ConfigureAwait(false);

        return new ShardFailedOrPausedFacts(configuration, rows, inProcess) { Remembered = remembered };
    }

    /// <summary>D05's own gate, which the sweep stamps on every series it writes and at which it reads them back.</summary>
    private static readonly HealthGate OwnGate = typeof(ShardFailedOrPausedCheck).GetCustomAttribute<HealthCheckAttribute>()!.Gate;

    /// <summary>
    /// The shards this database's regular history recorded failed or paused, read at D05's own gate - memory, not a
    /// statement: the advisor's in-process ring, as this check's evaluator would read it.
    /// </summary>
    private static IReadOnlySet<string> RememberedOf(HealthCheckContext context)
    {
        HealthHistory? history = context.Services.GetService<HealthHistory>();

        return history is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : RememberedIn(history.Read(context.StoreKey, HealthHistory.RingOf(context.DatabaseId, HealthSweepKind.Regular)).VisibleAt(OwnGate));
    }

    /// <summary>Every shard a history recorded failed or paused: each <see cref="DaemonSeries.ShardFailedAt" /> series in it.</summary>
    internal static IReadOnlySet<string> RememberedIn(HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(history);

        HashSet<string> shards = new(StringComparer.OrdinalIgnoreCase);

        foreach (HealthSample sample in history.Samples)
        {
            foreach (string series in sample.Values.Keys)
            {
                if (series.StartsWith(DaemonSeries.ShardFailedAtPrefix, StringComparison.Ordinal))
                {
                    shards.Add(series[DaemonSeries.ShardFailedAtPrefix.Length..]);
                }
            }
        }

        return shards;
    }

    /// <summary>
    /// Where each shard this sweep sees failed or paused stands, when its pause began and how it failed - what a later
    /// sweep, which may find it restarted and reading Running, judges it by.
    /// </summary>
    public override IEnumerable<HealthSamplePart> Sample(ShardFailedOrPausedFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        List<HealthSamplePart> parts = [];

        foreach (Problem problem in Current(facts).Values)
        {
            if (!problem.IsFailedOrPaused || facts.InProcess.IsKnownRebuilding(problem.Shard) || problem.Position is not { } position)
            {
                continue;
            }

            parts.Add(new HealthSamplePart(DaemonSeries.ShardFailedAt(problem.Shard), position));

            if (problem.IsPaused && problem.Since is { } since)
            {
                parts.Add(new HealthSamplePart(DaemonSeries.ShardPausedAt(problem.Shard), since.ToUnixTimeMilliseconds()));
            }

            if (CategoryNumber(problem.FailureCategory) is { } category)
            {
                parts.Add(new HealthSamplePart(DaemonSeries.ShardFailureCategory(problem.Shard), category));
            }
        }

        // D05's own position series: where each remembered shard's agent here stands now - what ends a failure it got past
        // on a store D03 records no positions for. A replay's position says nothing about the continuous agent's failure.
        foreach (DaemonAgentFact agent in facts.InProcess.Agents)
        {
            if (agent.Position is { } position
                && facts.Remembered.Contains(agent.ShardName)
                && !facts.InProcess.IsKnownRebuilding(agent.ShardName))
            {
                parts.Add(new HealthSamplePart(DaemonSeries.AgentPosition(agent.ShardName), position));
            }
        }

        return parts;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ShardFailedOrPausedFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        DaemonInProcess inProcess = facts.InProcess;
        bool hostedHere = inProcess.Hosting == DaemonHostingState.Hosted;

        if (facts.Rows is null && !hostedHere)
        {
            return HealthVerdict.Quiet(
                "Extended progression tracking is off (K02) and no daemon runs in this process, so nothing records whether a shard "
                + "is paused or why it failed.");
        }

        Dictionary<string, Problem> problems = Current(facts);
        TimeSpan memory = RestartMemoryFor(facts.Configuration.HealthInterval);

        // The restart window: the agent here reads Running because the coordinator has just started it again, and it has not
        // got past where an earlier sweep saw it fail. A fresh agent reads position 0 until its start is processed, which is
        // short of anywhere it failed, so it is kept too.
        foreach (DaemonAgentFact agent in inProcess.Agents)
        {
            // Exactly where it failed - the floor a restart resumes from - or 0, before its start is processed. Anywhere else is a
            // rewind, or progress, and not this.
            if (IsRunning(agent.Status)
                && agent.Position is { } position
                && SeenFailing(agent.ShardName, evaluation, memory) is [.., SeenFailure seen]
                && (position == seen.Position || position == 0))
            {
                problems[agent.ShardName] = new Problem(agent.ShardName, agent.Status, seen.Category, null, null, null, null, FromThisProcess: true)
                {
                    Position = position,
                    Restarted = seen,
                };
            }
        }

        List<HealthFinding> findings = [];

        foreach (Problem problem in problems.Values.OrderBy(static x => x.Shard, StringComparer.OrdinalIgnoreCase))
        {
            if (inProcess.IsKnownRebuilding(problem.Shard))
            {
                continue;
            }

            if (!problem.IsFailedOrPaused && problem.Restarted is null)
            {
                // A stop with no failure: the studio's own coordinator pause, a shutdown, an operator's stop.
                continue;
            }

            Problem judged = problem.Restarted is null && problem.IsPaused
                ? problem with { Restarted = EarlierAttempt(problem, evaluation, memory) }
                : problem;

            findings.Add(Finding(judged, inProcess, memory, RuntimeFeature.SystemicDatabaseFailure.IsAvailable(evaluation.Runtime))
                with { DocumentTypes = facts.Configuration.PublishedTypesOf(judged.Shard) });
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return facts.Rows is null
            ? HealthVerdict.Quiet("Every agent the daemon in this process runs is running. Extended progression tracking is off (K02), so shards run elsewhere are not known.")
            : HealthVerdict.Quiet();
    }

    /// <summary>
    /// The shards that are not running, merged from their rows and the daemon here - whose word replaces the row's for
    /// every shard it runs - from this sweep's facts alone, before anything the advisor remembers.
    /// </summary>
    private static Dictionary<string, Problem> Current(ShardFailedOrPausedFacts facts)
    {
        Dictionary<string, Problem> problems = new(StringComparer.OrdinalIgnoreCase);

        foreach (DaemonShardRow row in facts.Rows ?? [])
        {
            if (!ProjectionDataService.IsBookkeepingRow(row.Name))
            {
                problems[row.Name] = new Problem(row.Name, row.AgentStatus ?? "Unknown", row.FailureCategory, row.PauseReason,
                    row.Heartbeat, row.FailureEventSequence, row.FailureEventType, FromThisProcess: false)
                {
                    Position = row.Sequence,
                };
            }
        }

        // The daemon here is fresher than any row it wrote, so its word replaces the row's for the shards it runs.
        foreach (DaemonAgentFact agent in facts.InProcess.Agents)
        {
            if (IsRunning(agent.Status))
            {
                problems.Remove(agent.ShardName);
                continue;
            }

            problems.TryGetValue(agent.ShardName, out Problem? persisted);
            bool agentNamesTheEvent = agent.FailureEventSequence is not null;

            problems[agent.ShardName] = new Problem(
                agent.ShardName,
                agent.Status,
                agent.FailureCategory ?? persisted?.FailureCategory,
                agent.FailureMessage ?? persisted?.Reason,
                agent.PausedAt ?? persisted?.Since,
                agentNamesTheEvent ? agent.FailureEventSequence : persisted?.FailureEventSequence,
                agentNamesTheEvent ? agent.FailureEventType : persisted?.FailureEventType,
                FromThisProcess: true)
            {
                Position = agent.Position ?? persisted?.Position,
                SystemicDatabaseFailure = agent.SystemicDatabaseFailure,
                RootExceptionType = agent.FailureRootExceptionType,
            };
        }

        return problems;
    }

    /// <summary>
    /// What the advisor's samples recorded about a shard they saw failed or paused, oldest first: each failure the shard has
    /// not got past since, and that a sample within <paramref name="memory" /> saw - failing, or still standing exactly where
    /// it failed.
    /// </summary>
    private static List<SeenFailure> SeenFailing(string shard, HealthEvaluation evaluation, TimeSpan memory)
    {
        string failedAt = DaemonSeries.ShardFailedAt(shard);
        string pausedAt = DaemonSeries.ShardPausedAt(shard);
        string category = DaemonSeries.ShardFailureCategory(shard);

        List<SeenFailure> seen = [];

        foreach (HealthSample sample in evaluation.History.Samples)
        {
            if (!sample.Values.TryGetValue(failedAt, out double position))
            {
                continue;
            }

            seen.Add(new SeenFailure(
                sample.At,
                (long) position,
                sample.Values.TryGetValue(pausedAt, out double paused) ? (long?) (long) paused : null,
                sample.Values.TryGetValue(category, out double number) ? CategoryName(number) : null));
        }

        // A failure ends with the shard's progress past it: once any later sample saw the shard's position beyond where it
        // failed - D03's committed row, or D05's own series of its agent here - it got past, and a later restart for another
        // reason, reading 0, is not this failure again.
        string shardPosition = DaemonSeries.ShardPosition(shard);
        string agentPosition = DaemonSeries.AgentPosition(shard);

        seen.RemoveAll(failure => evaluation.History.Samples.Any(sample =>
            sample.At > failure.At
            && (Beyond(sample, shardPosition, failure.Position) || Beyond(sample, agentPosition, failure.Position))));

        // And it lasts the memory from the last sample that saw it: failing, or still standing exactly where it failed, as a
        // restarted shard short of its failure does - so a shard that keeps failing keeps it for as long as the history holds
        // the sample that saw it fail (HealthHistory.Capacity, 60 samples).
        seen.RemoveAll(failure => evaluation.Now - LastSeen(failure) > memory);

        return seen;

        DateTimeOffset LastSeen(SeenFailure failure)
        {
            DateTimeOffset last = failure.At;

            foreach (HealthSample sample in evaluation.History.Samples)
            {
                if (sample.At > last && (StandingAt(sample, shardPosition, failure.Position) || StandingAt(sample, agentPosition, failure.Position)))
                {
                    last = sample.At;
                }
            }

            return last;
        }

        static bool Beyond(HealthSample sample, string series, long position) =>
            sample.Values.TryGetValue(series, out double later) && later > position;

        static bool StandingAt(HealthSample sample, string series, long position) =>
            sample.Values.TryGetValue(series, out double at) && (long) at == position;
    }

    /// <summary>
    /// The latest earlier sweep that saw this shard paused, by an earlier pause, at or beyond where it is paused now: an
    /// attempt that failed, a restart, and another that failed no further on. The same pause seen twice is not one.
    /// </summary>
    private static SeenFailure? EarlierAttempt(Problem problem, HealthEvaluation evaluation, TimeSpan memory)
    {
        if (problem.Since is not { } since || problem.Position is not { } position)
        {
            return null;
        }

        long pausedAt = since.ToUnixTimeMilliseconds();

        return SeenFailing(problem.Shard, evaluation, memory).LastOrDefault(seen =>
            seen.At < evaluation.Now && seen.PausedAt is { } earlier && earlier < pausedAt && position <= seen.Position);
    }

    private static HealthFinding Finding(Problem problem, DaemonInProcess inProcess, TimeSpan memory, bool databaseErrorsPause)
    {
        string? tenant = ShardName.TryParse(problem.Shard, out ShardName? name) ? name?.TenantId : null;
        bool paused = problem.IsPaused;
        bool restarting = problem.Restarted is not null;
        bool critical = !paused && !restarting;

        List<HealthEvidence> evidence =
        [
            new("Status", StatusOf(problem)),
        ];

        if (tenant is not null)
        {
            evidence.Add(new HealthEvidence("Tenant", tenant, HealthGate.Activity));
        }

        if (problem.Since is { } since)
        {
            evidence.Add(new HealthEvidence(paused ? "Paused at" : "Stopped at", since.ToString("u", CultureInfo.InvariantCulture)));
        }

        if (problem.Restarted is { } seen)
        {
            evidence.Add(new HealthEvidence("Seen failing before", SeenText(seen)));

            if (!paused && problem.Position is { } position)
            {
                evidence.Add(new HealthEvidence("Position now", DaemonCheckConfiguration.Number(position)));
            }
        }

        if (problem.FailureCategory is { Length: > 0 } category)
        {
            evidence.Add(new HealthEvidence("Failure", category));
        }

        if (problem.Reason is { Length: > 0 } reason)
        {
            evidence.Add(new HealthEvidence("Reason", reason));
        }

        if (problem.FailureEventSequence is { } sequence)
        {
            evidence.Add(new HealthEvidence("Failed on event", DaemonCheckConfiguration.Number(sequence)
                + (problem.FailureEventType is { Length: > 0 } type ? " (" + type + ")" : string.Empty)));
        }

        Exception? live = inProcess.LiveExceptions?.GetValueOrDefault(problem.Shard);

        if (live is not null && DaemonLightUp.IsTransient(live) is { } transient)
        {
            evidence.Add(new HealthEvidence("Transient or permanent", transient
                ? "transient: JasperFx expects a retry to succeed"
                : "permanent: a retry will fail the same way until the cause is fixed"));
        }

        // JasperFx.Events 2.93 / Marten 9.52: a database error in projection code pauses the shard at any setting and is
        // never dead-lettered - the live exception, where a page watches the tracker, says so exactly; otherwise the
        // daemon's classified failure, where it can.
        bool? systemic = databaseErrorsPause
            ? (live is not null ? DaemonLightUp.IsSystemicDatabaseFailure(live) : null) ?? problem.SystemicDatabaseFailure
            : null;

        if (systemic == true && IsApplyEvent(problem.FailureCategory))
        {
            evidence.Add(new HealthEvidence(
                "Kind of failure",
                "a database error in the projection's code"
                + (problem.RootExceptionType is { Length: > 0 } root ? " (" + root + ")" : string.Empty)
                + ", not the event's: every event would fail the same way, so JasperFx pauses the shard whatever SkipApplyErrors "
                + "says and never dead-letters it. Fix the schema or the grant it ran into, then start the shard again."));
        }

        if (inProcess.StartRefusals.GetValueOrDefault(problem.Shard) is { } refusal)
        {
            evidence.Add(new HealthEvidence(
                "Last start from Marten Studio",
                "refused " + refusal.RefusedAt.ToString("u", CultureInfo.InvariantCulture)
                + (refusal.Reason is { Length: > 0 } classified ? ": " + classified : string.Empty)
                + (refusal.IsTransient switch
                {
                    true => ", which a retry is expected to resolve",
                    false => ", which a retry will not resolve on its own",
                    null => string.Empty,
                })));
        }

        string label = ShardLabel(problem.Shard, tenant);

        return new HealthFinding
        {
            Code = "D05",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Subject = problem.Shard,
            TenantId = tenant,
            Title = restarting
                ? "Shard " + label + " keeps failing and being restarted"
                : critical
                    ? "Shard " + label + " failed and stopped"
                    : "Shard " + label + " is paused",
            WhyItMatters = restarting
                ? "Marten pauses a shard when it fails, and its coordinator starts it again once Projections.HealthCheckPollingTime "
                  + "(five seconds by default) has passed. This one has been started again and has not got past where it failed"
                  + (paused
                      ? ": it failed again, no further on."
                      : ". Between a restart and the next failure it reads Running, which is what it reads now.")
                  + " The projection it feeds is not moving, and every attempt will end the same way until the cause is fixed."
                : critical
                    ? "The daemon's error policy gave up on this shard: it is stopped, and nothing restarts it until somebody does - so the "
                      + "projection it feeds stops changing. The failure below is what stopped it; fix the cause, then start or rebuild it."
                    : "Marten pauses a shard after a failure, and its coordinator starts it again once Projections.HealthCheckPollingTime "
                      + "(five seconds by default) has passed. A pause the advisor sees is one that is still in effect - and one that keeps "
                      + "coming back is a shard failing on every attempt.",
            Evidence = evidence,
            Caveat = CaveatOf(problem, paused, memory),
            Remedy = RemedyFor(problem.FailureCategory, databaseErrorsPause, systemic),
        };
    }

    private static bool IsApplyEvent(string? category) =>
        string.Equals(category, nameof(ShardFailureCategory.ApplyEvent), StringComparison.Ordinal);

    private static string StatusOf(Problem problem)
    {
        string status = problem.Status
            + (problem.FromThisProcess ? " (the daemon in this process)" : " (mt_event_progression, as its node last wrote it)");

        return problem.Restarted is not null && IsRunning(problem.Status)
            ? status + ", started again after a failure and not past it yet"
            : status;
    }

    private static string SeenText(SeenFailure seen) =>
        (seen.PausedAt is { } earlier
            ? "paused at " + DateTimeOffset.FromUnixTimeMilliseconds(earlier).ToString("u", CultureInfo.InvariantCulture) + ", "
            : string.Empty)
        + "at position " + DaemonCheckConfiguration.Number(seen.Position)
        + ", seen by the advisor's check at " + seen.At.ToString("u", CultureInfo.InvariantCulture);

    private static string CaveatOf(Problem problem, bool paused, TimeSpan memory)
    {
        if (problem.Restarted is not null && !paused)
        {
            return "Read from the daemon hosted in this process, which reads Running between a restart and the next failure. The "
                + "failure is the one an earlier check saw; the shard is reported until it commits progress past that position, or "
                + "until no check has seen it fail, or still standing where it failed, for " + DaemonCheckConfiguration.Format(memory) + ".";
        }

        return problem.FromThisProcess
            ? "Read from the daemon hosted in this process."
            : "Read from mt_event_progression, which the node that ran the shard wrote when its status last changed; if that node is gone, "
              + "this is the last thing it said.";
    }

    /// <summary>What <c>SkipApplyErrors</c> does, as the remedy has always said it.</summary>
    internal const string SkipApplyErrorsExplanation =
        "An event the projection's code throws on becomes a dead letter and the shard carries on, instead of stopping. The "
        + "projection then never reflects that event: fix the code and rebuild it, or rewind from the dead letter.";

    /// <summary>
    /// What the remedy adds from Marten 9.52 (JasperFx.Events 2.93, <see cref="RuntimeFeature.SystemicDatabaseFailure" />)
    /// when nothing says whether this failure was a database error - a persisted row carries only a category and a text.
    /// </summary>
    internal const string DatabaseErrorExclusion =
        " Except a database error in that code - a missing column or table, a permission: it pauses the shard at any setting "
        + "and is never dead-lettered, because every event would fail the same way. Fix the schema or the grant, then start "
        + "the shard again.";

    /// <summary>
    /// The <c>StoreOptions</c> switch that turns a failure of this category into a dead letter, when there is one.
    /// </summary>
    /// <param name="category">The failure's category.</param>
    /// <param name="databaseErrorsPause">
    /// JasperFx.Events 2.93+ (Marten 9.52+): a systemic database failure is never dead-lettered, so for an
    /// <c>ApplyEvent</c> failure <c>SkipApplyErrors</c> is the remedy only when the failure is not one.
    /// </param>
    /// <param name="systemic">
    /// Whether this failure is one - <see langword="true" />: no switch helps, and the evidence says what does;
    /// <see langword="false" />: the switch, as it always was; <see langword="null" />: the switch, with the exception named.
    /// </param>
    private static HealthRemedy? RemedyFor(string? category, bool databaseErrorsPause, bool? systemic) => category switch
    {
        nameof(ShardFailureCategory.ApplyEvent) when databaseErrorsPause && systemic == true => null,
        nameof(ShardFailureCategory.ApplyEvent) => new HealthRemedy(
            "opts.Projections.Errors.SkipApplyErrors = true;",
            SkipApplyErrorsExplanation + (databaseErrorsPause && systemic is null ? DatabaseErrorExclusion : string.Empty)),
        nameof(ShardFailureCategory.EventSerialization) => new HealthRemedy(
            "opts.Projections.Errors.SkipSerializationErrors = true;",
            "An event that cannot be deserialized becomes a dead letter instead of stopping the shard - usually an event type whose "
            + "shape changed without an upcaster."),
        nameof(ShardFailureCategory.UnknownEventType) => new HealthRemedy(
            "opts.Projections.Errors.SkipUnknownEvents = true;",
            "An event of a type this process cannot resolve is skipped instead of stopping the shard. Registering the type "
            + "(opts.Events.AddEventType) fixes the cause; the exception's own message names it."),
        _ => null,
    };

    /// <summary>The shard as a title names it: the tenant's shard is named by its projection, the tenant left to the gated evidence.</summary>
    private static string ShardLabel(string shard, string? tenant) =>
        tenant is null ? shard : shard[..shard.LastIndexOf(':')] + " (one tenant's)";

    private static bool IsRunning(string status) => string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>A category as the history holds it: its number, for a name JasperFx defines.</summary>
    private static double? CategoryNumber(string? name) =>
        Enum.TryParse(name, ignoreCase: false, out ShardFailureCategory category) && Enum.IsDefined(category) ? (int) category : null;

    /// <summary>The category a number in the history stands for, or <see langword="null" /> for one JasperFx does not define.</summary>
    private static string? CategoryName(double number)
    {
        if (number is < int.MinValue or > int.MaxValue || number != Math.Floor(number))
        {
            return null;
        }

        ShardFailureCategory category = (ShardFailureCategory) (int) number;
        return Enum.IsDefined(category) ? category.ToString() : null;
    }

    /// <summary>One shard that is not running, merged from its row and the daemon here.</summary>
    private sealed record Problem(
        string Shard,
        string Status,
        string? FailureCategory,
        string? Reason,
        DateTimeOffset? Since,
        long? FailureEventSequence,
        string? FailureEventType,
        bool FromThisProcess)
    {
        /// <summary>Where it stands: the agent's position here, or its row's.</summary>
        public long? Position { get; init; }

        /// <summary>The earlier sweep that saw it fail where it still is, when it has been started again since.</summary>
        public SeenFailure? Restarted { get; init; }

        /// <summary>
        /// Whether the daemon here classified its failure as a systemic database failure (JasperFx.Events 2.93+), or
        /// <see langword="null" /> when nothing said - a persisted row carries only a category and a text.
        /// </summary>
        public bool? SystemicDatabaseFailure { get; init; }

        /// <summary>The failure's innermost exception type, as the daemon here classified it.</summary>
        public string? RootExceptionType { get; init; }

        /// <summary>Whether its status says it is paused.</summary>
        public bool IsPaused => string.Equals(Status, "Paused", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether this sweep sees it paused, or stopped by a failure.</summary>
        public bool IsFailedOrPaused => IsPaused || FailureCategory is { Length: > 0 };
    }

    /// <summary>What one of the advisor's samples recorded about a shard it saw failed or paused.</summary>
    /// <param name="At">When that sweep ran, by the advisor's clock.</param>
    /// <param name="Position">Where the shard stood.</param>
    /// <param name="PausedAt">When its pause began, in Unix milliseconds of the daemon's clock, if it was paused.</param>
    /// <param name="Category">How it failed, when JasperFx classified it.</param>
    private sealed record SeenFailure(DateTimeOffset At, long Position, long? PausedAt, string? Category);
}
