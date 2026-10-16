using MartenStudio.Services.Projections;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D04 reads: configuration and the daemon in this process - the numbers it judges are the sweep's samples.</summary>
/// <param name="Configuration">The store's daemon settings, with what each projection writes.</param>
/// <param name="InProcess">The daemon here, its agents, and the studio's own pause and rebuilds.</param>
internal sealed record ProjectionFallingBehindFacts(DaemonCheckConfiguration Configuration, DaemonInProcess InProcess)
{
    /// <summary>The registered async shards, by identity.</summary>
    public IReadOnlyList<string> AsyncShards => Configuration.AsyncShards;
}

/// <summary>
/// D04: a shard is past its lag threshold and still falling behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 0, over the history.</b> D01 records the sequence's handed-out ceiling and D03 every shard's position, in
/// the same sweep; this judges the lag between them over the last ten minutes of samples (<see cref="ProjectionTrend" />,
/// which the projections screen draws its arrow and ETA from as well). It reads nothing itself, so it costs nothing.
/// </para>
/// <para>
/// <b>What fires.</b> The latest lag is past the shard's warning threshold and the lag grew over the window, and had
/// not started shrinking at the latest sample: Warning. A lag that is high and draining is not a finding - it is
/// "draining at N ev/s, caught up in ~T", which the projections screen shows and this check's status row says. A burst
/// of appends grows every lag for a moment; two samples a minute apart is the least the advisor believes.
/// </para>
/// <para>
/// <b>Only a shard that is running and slower than its arrivals.</b> A lag grows for reasons that have nothing to do
/// with speed, and each is somebody else's finding (<see cref="ProjectionTrend.Judged" />): the studio's own coordinator
/// pause (every lag grows, by design), a rebuild (the position starts again from zero), an agent here that is paused
/// or stopped (D05), a shard that has not moved at all (D03, or no daemon - D02) and a mark that has not moved (held
/// behind a gap - D01). D04 says "falling behind" only of a shard that moved while the advisor watched, while the mark
/// moved too.
/// </para>
/// <para>
/// <b>Thresholds.</b> A progression row's <c>warning_behind_threshold</c> and <c>critical_behind_threshold</c> win when
/// set. Marten creates those columns and writes nothing to them (#5173, at V9.39.0 and V9.50.2), so in practice it is
/// the projections screen's 1,000 and 100,000; a host that writes its own is honoured.
/// </para>
/// </remarks>
[HealthCheck("D04", HealthArea.Daemon, HealthCostTier.InProcess, "Falling behind, with an ETA")]
internal sealed class ProjectionFallingBehindCheck : HealthCheck<ProjectionFallingBehindFacts>
{
    /// <inheritdoc />
    public override async Task<ProjectionFallingBehindFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        DaemonCheckConfiguration configuration = DaemonCheckConfiguration.Read(context);

        if (configuration.TenantPartitionedEvents)
        {
            throw new HealthCheckUnavailableException(DaemonCheckConfiguration.TenantPartitionedReason);
        }

        // In-process only: the accessor, the tracker and the studio's own records. No statement - this is tier 0.
        DaemonInProcess inProcess = await DaemonInProcess.ReadAsync(context, configuration.AsyncShards, cancellationToken).ConfigureAwait(false);

        return new ProjectionFallingBehindFacts(configuration, inProcess);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ProjectionFallingBehindFacts facts, HealthEvaluation evaluation)
    {
        if (facts.AsyncShards.Count == 0)
        {
            return HealthVerdict.Quiet("The store registers no async projection or subscription.");
        }

        List<HealthFinding> findings = [];
        List<string> notes = [];
        bool anyCurrent = false;

        DaemonInProcess inProcess = facts.InProcess;

        foreach (string shard in facts.AsyncShards)
        {
            ShardTrend trend = ProjectionTrend.Judged(
                ProjectionTrend.For(evaluation.History, shard),
                pausedByStudio: inProcess.PausedByStudio is not null,
                rebuilding: inProcess.IsKnownRebuilding(shard),
                agentStatusHere: inProcess.Agent(shard)?.Status);

            // Only a trend this sweep read: a sample from a sweep in which D01 or D03 could not read is not "now".
            if (trend.MeasuredAt != evaluation.Now || trend.Lag is not { } lag)
            {
                continue;
            }

            anyCurrent = true;

            // A projection of a hidden document type is named nowhere - not in a finding, not in a note.
            bool hidden = facts.Configuration.IsHidden(shard);

            if (trend.WithheldBecause is null && trend.Direction == LagDirection.Growing && lag > trend.EffectiveWarning)
            {
                findings.Add(Finding(trend, lag, facts.Configuration.PublishedTypesOf(shard)));
                continue;
            }

            if (!hidden && lag > 0 && (trend.WithheldBecause is not null || trend.Direction is LagDirection.Draining or LagDirection.Steady))
            {
                notes.Add(shard + ": " + trend.Describe());
            }
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings, notes.Count > 0 ? string.Join("; ", notes) : null);
        }

        if (!anyCurrent)
        {
            return HealthVerdict.Quiet(
                "No shard positions were read in this sweep (D01 and D03 record them), so there is no lag to judge.");
        }

        return notes.Count > 0
            ? HealthVerdict.Quiet(string.Join("; ", notes))
            : HealthVerdict.Quiet(evaluation.History.Describe(evaluation.Now));
    }

    private static HealthFinding Finding(ShardTrend trend, long lag, IReadOnlyList<Type> documentTypes)
    {
        bool pastCritical = lag > trend.EffectiveCritical;

        return new HealthFinding
        {
            Code = "D04",
            Severity = HealthSeverity.Warning,
            Subject = trend.ShardName,
            DocumentTypes = documentTypes,
            Title = "Shard " + trend.ShardName + " is " + DaemonCheckConfiguration.Number(lag) + " events behind and falling further behind",
            WhyItMatters =
                "Events are arriving faster than this shard processes them, so everything that reads its projection is getting "
                + "staler by the minute - it is " + trend.Describe() + ". It catches up only when appends slow down or it speeds up. "
                + "A shard that is paused, failed or not moving at all is D05's or D03's, and its own finding says so.",
            Evidence =
            [
                new HealthEvidence("Behind", DaemonCheckConfiguration.Number(lag) + " events"),
                new HealthEvidence("Lag changing by", ShardTrend.Rate(trend.LagPerSecond ?? 0)),
                new HealthEvidence("Processing", ShardTrend.Rate(trend.ProcessedPerSecond ?? 0)),
                new HealthEvidence("Arriving", ShardTrend.Rate(trend.AppendedPerSecond ?? 0)),
                new HealthEvidence("Measured over", DaemonCheckConfiguration.Format(trend.Watched) + ", "
                    + trend.Samples.ToString(System.Globalization.CultureInfo.InvariantCulture) + " samples"),
                new HealthEvidence("Warning threshold", DaemonCheckConfiguration.Number(trend.EffectiveWarning)
                    + (trend.WarningThreshold is null ? " (the studio's default; Marten writes no per-shard threshold)" : " (warning_behind_threshold)")),
                new HealthEvidence("Critical threshold", DaemonCheckConfiguration.Number(trend.EffectiveCritical)
                    + (pastCritical ? ", passed" : string.Empty)
                    + (trend.CriticalThreshold is null ? " (the studio's default)" : " (critical_behind_threshold)")),
            ],
            Caveat =
                "Rates are measured between the advisor's own samples, which exist only while the Health page or the Overview is "
                + "open, and lag is measured against the highest sequence number handed out, as the projections screen measures it.",
            Remedy = null,
        };
    }
}
