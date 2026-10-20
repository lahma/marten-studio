using System.Globalization;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D03 reads.</summary>
/// <param name="Configuration">The store's daemon settings.</param>
/// <param name="Reading">The high-water reading.</param>
/// <param name="Shards">The registered async shards' progression rows, with the extended columns under extended tracking.</param>
/// <param name="InProcess">The daemon in this process, if any.</param>
internal sealed record ShardStalledFacts(
    DaemonCheckConfiguration Configuration,
    HighWaterReading Reading,
    IReadOnlyList<DaemonShardRow> Shards,
    DaemonInProcess InProcess);

/// <summary>
/// D03: a shard is alive but not moving - the daemon runs, the mark is ahead of the shard, and the shard has not
/// advanced for longer than a running shard ever waits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Progress versus liveness.</b> A shard's row's <c>last_updated</c> is stamped on every progress write (#4981): it
/// is when the shard last moved, never whether its agent is alive. So "not moving" alone is a shard on an idle store
/// as much as a stuck one, and the check asks three more things. Is the mark ahead of it? Has the mark been ahead of
/// it, with the shard where it is, for the whole window - which the advisor's own history answers, because a mark
/// that moved a second ago gives a healthy shard no time? And is the daemon alive? A daemon hosted here that says it
/// runs is; so is one whose <c>HighWaterMark</c> row or allocation fence was written within the window, or whose
/// mark moved while the advisor watched.
/// </para>
/// <para>
/// <b>The 9.43 rule</b> (<see cref="RuntimeFeature.HighWaterMarkLivenessStamp" />), which changes what is believed,
/// not only what is said. From Marten 9.43 the high-water agent restamps its row on every poll (#5541), so the row's
/// age <em>is</em> the agent's liveness: fresh is alive, old is stopped - D02's question - whatever the fence or the
/// advisor's history of the mark say, because those can only be older news from the same agent. Below 9.43 the row
/// is the mark's last move and freezes on an idle store, so an old row means nothing; a fresh row, a fresh allocation
/// fence or a mark the advisor saw move is all the evidence there is, and without any of them the check says it cannot
/// tell rather than reporting a stall.
/// </para>
/// <para>
/// <b>How long is too long</b> is the shard's own: at least five minutes (and five <c>SlowPollingTime</c>s), and three
/// times its typical page - the median of the runs in which the advisor saw it hold still with work to do before it
/// moved, a run longer than the window then in force being a stall or an outage and dropped - because a page of a slow
/// projection can take minutes and a healthy one must not be called stalled for taking its usual time. And never more
/// than 20 minutes, nor half of what the advisor's history holds: a window the history cannot cover could never be
/// watched whole, and D03 would go blind.
/// </para>
/// <para>
/// <b>Not a heartbeat.</b> <c>mt_event_progression.heartbeat</c> is written at status changes (JasperFx documents it
/// so), and is never read as a sign of life - K02's rule. Even a periodic one would say only that the shard's agent loop
/// runs: a heartbeat, the high-water agent's restamp included, is liveness of a loop and never progress, which is why
/// "alive" is the daemon's here and "not moving" is <c>last_updated</c>'s. A shard the extended tracking or the daemon
/// here says is paused or failed is D05's; one being rebuilt is moving in a replay of its own.
/// </para>
/// </remarks>
[HealthCheck("D03", HealthArea.Daemon, HealthCostTier.Catalog, "Shard alive but not moving")]
internal sealed class ShardStalledCheck : HealthCheck<ShardStalledFacts>
{
    /// <inheritdoc />
    public override async Task<ShardStalledFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        DaemonCheckConfiguration configuration = DaemonCheckConfiguration.Read(context);

        if (configuration.TenantPartitionedEvents)
        {
            throw new HealthCheckUnavailableException(DaemonCheckConfiguration.TenantPartitionedReason);
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);

        if (!shape.HasHighWater)
        {
            throw new HealthCheckUnavailableException(EventStoreRelations.NoEventStoreReason);
        }

        HighWaterReading reading = await HighWaterReading.ReadAsync(context, configuration.EventSchema, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DaemonShardRow> shards = await DaemonShardRow
            .ReadAsync(context, shape, configuration, configuration.ExtendedProgressionTracking, cancellationToken)
            .ConfigureAwait(false);

        DaemonInProcess inProcess = await DaemonInProcess.ReadAsync(context, configuration.AsyncShards, cancellationToken).ConfigureAwait(false);

        return new ShardStalledFacts(configuration, reading, shards, inProcess);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(ShardStalledFacts facts)
    {
        foreach (string shard in facts.Configuration.AsyncShards)
        {
            DaemonShardRow? row = RowOf(facts, shard);

            // A registered shard with no row has processed nothing: position 0, as the projections screen draws it.
            yield return new HealthSamplePart(DaemonSeries.ShardPosition(shard), row?.Sequence ?? 0);

            if (row?.WarningThreshold is { } warning)
            {
                yield return new HealthSamplePart(DaemonSeries.ShardWarningThreshold(shard), warning);
            }

            if (row?.CriticalThreshold is { } critical)
            {
                yield return new HealthSamplePart(DaemonSeries.ShardCriticalThreshold(shard), critical);
            }
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ShardStalledFacts facts, HealthEvaluation evaluation)
    {
        DaemonCheckConfiguration configuration = facts.Configuration;
        HighWaterReading reading = facts.Reading;

        if (configuration.AsyncShards.Count == 0)
        {
            return HealthVerdict.Quiet("The store registers no async projection or subscription.");
        }

        if (reading.Mark is not { } mark)
        {
            return HealthVerdict.Quiet("The daemon has not written a high-water mark in this database yet.");
        }

        TimeSpan livenessWindow = DaemonThresholds.DaemonQuiet(configuration.SlowPollingTime);
        bool stampIsLiveness = RuntimeFeature.HighWaterMarkLivenessStamp.IsAvailable(evaluation.Runtime);
        Liveness liveness = AliveEvidence(facts, evaluation, livenessWindow, stampIsLiveness);

        List<HealthFinding> findings = [];
        List<string> watching = [];
        TimeSpan watchingFor = TimeSpan.Zero;

        foreach (string shard in configuration.AsyncShards)
        {
            DaemonShardRow? row = RowOf(facts, shard);

            if (row is null || IsExplainedElsewhere(facts, shard, row))
            {
                continue;
            }

            TimeSpan? typicalPage = ProjectionTrend.TypicalPage(
                evaluation.History,
                shard,
                page => DaemonThresholds.ShardQuiet(configuration.SlowPollingTime, configuration.HealthInterval, page));
            TimeSpan window = DaemonThresholds.ShardQuiet(configuration.SlowPollingTime, configuration.HealthInterval, typicalPage);

            long behindMark = mark - row.Sequence;
            if (behindMark <= 0 || row.LastUpdated is not { } lastMoved || reading.ServerNow - lastMoved <= window)
            {
                continue;
            }

            if (!AheadForWholeWindow(evaluation, shard, row.Sequence, window))
            {
                watching.Add(shard);
                watchingFor = window > watchingFor ? window : watchingFor;
                continue;
            }

            if (!liveness.Alive)
            {
                continue;
            }

            TimeSpan still = reading.ServerNow - lastMoved;

            findings.Add(new HealthFinding
            {
                Code = "D03",
                Severity = HealthSeverity.Warning,
                Subject = shard,
                DocumentTypes = configuration.PublishedTypesOf(shard),
                Title = "Shard " + shard + " has not moved for " + DaemonCheckConfiguration.Format(still) + " while the daemon runs",
                WhyItMatters =
                    "The high-water mark is " + DaemonCheckConfiguration.Number(behindMark) + " events ahead of this shard, so there "
                    + "are committed events for it to process, and the daemon is alive - its loop is, which is all a sign of life can "
                    + "say, and never that a shard moves - yet the shard has written no progress for "
                    + DaemonCheckConfiguration.Format(still) + ", well past the longest a page of it has been seen to take. One that "
                    + "does not move is usually blocked inside its own handler: a lock it waits on, a call that never returns, a page "
                    + "too large to finish. Everything that reads this projection is that far behind, and nothing says so on its own.",
                Evidence =
                [
                    new HealthEvidence("Position", DaemonCheckConfiguration.Number(row.Sequence)),
                    new HealthEvidence("High-water mark", DaemonCheckConfiguration.Number(mark)),
                    new HealthEvidence("Last progress", lastMoved.ToString("u", CultureInfo.InvariantCulture) + " (server time)"),
                    new HealthEvidence("Quiet window", DaemonCheckConfiguration.Format(window) + (typicalPage is { } page
                        ? " (3 x this shard's typical page as the advisor saw it, " + DaemonCheckConfiguration.Format(page)
                          + ", at least 5 min, at most " + DaemonCheckConfiguration.Format(DaemonThresholds.ShardQuietCeilingFor(configuration.HealthInterval)) + ")"
                        : " (max of 5 x Projections.SlowPollingTime and 5 min; the advisor has not yet seen this shard take a page)")),
                    new HealthEvidence("Daemon alive because", liveness.Because),
                    row.AgentStatus is { Length: > 0 } status
                        ? new HealthEvidence("Agent status (extended tracking)", status)
                        : new HealthEvidence("Agent status", configuration.ExtendedProgressionTracking ? "not recorded" : "not recorded: extended progression tracking is off (K02)"),
                ],
                Caveat =
                    "Progress is measured in the database server's time; that the mark has been ahead of the shard for the whole window "
                    + "comes from the advisor's own samples, which exist only while the Health page or the Overview is open. "
                    + (stampIsLiveness
                        ? string.Empty
                        : "Below Marten 9.43 the HighWaterMark row does not say whether the daemon is alive; this host runs "
                          + evaluation.Runtime.Describe(RuntimePackage.Marten) + ". "),
                Remedy = null,
            });
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        if (!liveness.Alive)
        {
            return HealthVerdict.Quiet(liveness.Because);
        }

        return watching.Count > 0
            ? HealthVerdict.Quiet(
                watching.Count.ToString(CultureInfo.InvariantCulture) + " shard(s) have not moved recently with the mark ahead; the advisor "
                + "reports one only after it has seen the mark ahead of it for " + DaemonCheckConfiguration.Format(watchingFor) + ".")
            : HealthVerdict.Quiet();
    }

    /// <summary>Why the daemon is believed alive, or why it cannot be.</summary>
    private sealed record Liveness(bool Alive, string Because);

    private static Liveness AliveEvidence(ShardStalledFacts facts, HealthEvaluation evaluation, TimeSpan window, bool stampIsLiveness)
    {
        HighWaterReading reading = facts.Reading;

        if (facts.InProcess.IsHealthyHere)
        {
            return new Liveness(true, "the daemon is hosted in this process and says it is running");
        }

        if (reading.SinceMarkUpdated is { } mark && mark <= window)
        {
            return new Liveness(true, stampIsLiveness
                ? "the high-water agent restamped its mark " + DaemonCheckConfiguration.Format(mark) + " ago, which from Marten 9.43 it does on every poll"
                : "the high-water agent wrote its mark " + DaemonCheckConfiguration.Format(mark) + " ago");
        }

        if (stampIsLiveness)
        {
            // From 9.43 the row is the agent's own pulse: the fence and the advisor's history are older news from the same
            // agent, and cannot outvote it.
            return new Liveness(false,
                "The high-water agent has not restamped its mark within " + DaemonCheckConfiguration.Format(window)
                + "; from Marten 9.43 a running one does on every poll, so whether anything runs the daemon is D02's question.");
        }

        if (reading.SinceFenceUpdated is { } fence && fence <= window)
        {
            return new Liveness(true, "the high-water agent restamped its allocation fence " + DaemonCheckConfiguration.Format(fence) + " ago");
        }

        HealthTrend marks = evaluation.History.Trend(DaemonSeries.HighWaterMark);
        if (marks.Points.Count >= 2 && marks.Points[^1].Value > marks.Points[0].Value
            && evaluation.Now - marks.Points[0].At <= DaemonThresholds.TrendWindow)
        {
            return new Liveness(true, "the high-water mark moved while the advisor watched");
        }

        return new Liveness(false,
            "Marten below 9.43 writes the HighWaterMark row only when the mark moves, and the mark has not moved recently, so the "
            + "advisor cannot tell an idle daemon from a stopped one and reports no stall.");
    }

    /// <summary>
    /// Whether the advisor has a sample at least <paramref name="window" /> old in which the shard was already where it is
    /// now and the mark was already ahead of it.
    /// </summary>
    private static bool AheadForWholeWindow(HealthEvaluation evaluation, string shard, long position, TimeSpan window)
    {
        DateTimeOffset cutoff = evaluation.Now - window;
        string series = DaemonSeries.ShardPosition(shard);

        for (int i = evaluation.History.Samples.Count - 1; i >= 0; i--)
        {
            HealthSample sample = evaluation.History.Samples[i];

            if (sample.At > cutoff)
            {
                continue;
            }

            if (sample.Values.TryGetValue(series, out double then) && sample.Values.TryGetValue(DaemonSeries.HighWaterMark, out double markThen))
            {
                return (long) then == position && markThen > then;
            }
        }

        return false;
    }

    /// <summary>Paused, failed or being rebuilt: the shard's state is D05's (or a rebuild's) to report, not a stall.</summary>
    private static bool IsExplainedElsewhere(ShardStalledFacts facts, string shard, DaemonShardRow row)
    {
        if (facts.InProcess.IsKnownRebuilding(shard))
        {
            return true;
        }

        if (facts.InProcess.Agent(shard) is { } agent && !string.Equals(agent.Status, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return facts.Configuration.ExtendedProgressionTracking && (row.IsNotRunning || row.HasFailure);
    }

    private static DaemonShardRow? RowOf(ShardStalledFacts facts, string shard) =>
        facts.Shards.FirstOrDefault(x => string.Equals(x.Name, shard, StringComparison.OrdinalIgnoreCase));
}
