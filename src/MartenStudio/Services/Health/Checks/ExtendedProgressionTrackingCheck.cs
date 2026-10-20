using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// K02: extended progression tracking is off, so the progression table records only how far each shard got.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the flag really does</b>, read at both ends of the range the studio supports (Marten V9.39.0 and
/// V9.50.2, JasperFx V2.74.0 and V2.87.1) on 2026-10-16, and nothing more is claimed:
/// </para>
/// <list type="bullet">
/// <item><c>Events.EnableExtendedProgressionTracking</c> makes the daemon write <c>heartbeat</c>,
/// <c>agent_status</c>, <c>pause_reason</c>, <c>running_on_node</c> and the four <c>failure_*</c> columns of
/// <c>mt_event_progression</c> on every shard state transition, and makes progression reads hydrate them
/// (<c>EventGraph.EnableExtendedProgressionTracking</c>'s summary;
/// <c>MartenDatabase.EventStorage</c>'s extended write). Off by default, deliberately, and the columns exist
/// either way since 9.31 (#5309).</item>
/// <item>It does <b>not</b> make anything write <c>warning_behind_threshold</c> or <c>critical_behind_threshold</c>:
/// those two columns are created and are neither written nor read by anything (#5173,
/// <c>Bug_5173_behind_threshold_columns_retained</c> at both tags). So the check does not promise a projection's
/// own lag thresholds.</item>
/// <item>It does <b>not</b> make the heartbeat a liveness signal. Since jasperfx#622 the heartbeat is written at
/// status transitions only; a periodic beat needs <c>opts.Projections.ExtendedProgressionHeartbeatInterval</c>,
/// which JasperFx documents as a compatibility hatch rather than a recommendation, saying that nothing in
/// JasperFx, Marten or CritterWatch reads the persisted heartbeat (<c>IReadOnlyDaemonSettings</c>, the same
/// words at both JasperFx tags). So the
/// check does not promise to tell an idle shard from a stopped one, and does not suggest the interval.</item>
/// </list>
/// <para>
/// What it does buy is real and is what the finding says: a shard's last status, why it was paused, the node
/// it ran on, and a failed shard's classified failure, recorded in the database - which is where the
/// Projections screen reads them from, and the only place they outlive the process that ran the shard.
/// </para>
/// <para>
/// <b>Info, and only for a store whose daemon has something to run</b> - an async projection or a subscription
/// (<see cref="AsyncTrackingFacts.AsyncWorkOf" />). A store whose projections could not be read gets no
/// finding. It fires on Marten's defaults, the sample store's included, on purpose: the sample stays at
/// Marten's defaults so that what the advisor says about it is what it says about a new host.
/// </para>
/// </remarks>
[HealthCheck("K02", HealthArea.Configuration, HealthCostTier.InProcess, "Extended progression tracking", Gate = HealthGate.None)]
internal sealed class ExtendedProgressionTrackingCheck : HealthCheck<AsyncTrackingFacts>
{
    /// <inheritdoc />
    public override Task<AsyncTrackingFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        Task.FromResult(AsyncTrackingFacts.Read(context, static options => options.Events.EnableExtendedProgressionTracking));

    /// <inheritdoc />
    public override HealthVerdict Evaluate(AsyncTrackingFacts facts, HealthEvaluation evaluation)
    {
        switch (facts.HasAsyncWork)
        {
            case null:
                return HealthVerdict.Quiet(AsyncTrackingFacts.UnreadableNote);
            case false:
                return HealthVerdict.Quiet("The store registers no async projection or subscription, so there is no shard to track.");
        }

        if (facts.Enabled)
        {
            return HealthVerdict.Quiet();
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K02",
            Severity = HealthSeverity.Info,
            Title = "Extended progression tracking is off, so the database records only how far each shard got",
            WhyItMatters =
                "With it off, the daemon writes each shard's position to mt_event_progression and nothing else. With it on, "
                + "it also writes, whenever a shard changes state, the shard's status, why it was paused, the node running it "
                + "and - for a shard that failed - what kind of failure it was and the event it failed on. That is what the "
                + "Projections screen shows in a shard's status column, and the only record of why a shard stopped that "
                + "outlives the process that ran it.",
            Evidence =
            [
                new HealthEvidence("Events.EnableExtendedProgressionTracking", "false"),
                facts.AsyncWorkEvidence(),
            ],
            Caveat =
                "Read from this process's configuration. Another process running the daemon over the same database writes what "
                + "its own configuration says.",
            Remedy = new HealthRemedy(
                "opts.Events.EnableExtendedProgressionTracking = true;",
                "Marten leaves it off so that no deployment starts writing more on upgrade without asking. The columns already "
                + "exist (since Marten 9.31), so nothing is migrated; the cost is a few small values written on each shard's "
                + "state transitions, not on every batch."),
        });
    }
}
