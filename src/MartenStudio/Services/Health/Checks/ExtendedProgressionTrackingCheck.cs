namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// K02: extended progression tracking is off, so the daemon writes only each shard's position.
/// </summary>
/// <remarks>
/// <para>
/// <c>Events.EnableExtendedProgressionTracking</c> decides whether the daemon writes the heartbeat, agent
/// status, pause reason, node and failure columns of <c>mt_event_progression</c> on every shard state change.
/// It is off by default, deliberately - turning it on changes what every existing deployment writes - and
/// since Marten 9.31 the columns exist either way; the flag only decides whether they are written (verified
/// in <c>EventGraph.EnableExtendedProgressionTracking</c>'s remarks at V9.31.0).
/// </para>
/// <para>
/// Info, and only for a store whose daemon has something to run: without async projections or subscriptions
/// there is no shard to report on.
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
        if (!facts.HasAsyncWork)
        {
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
            Title = "Extended progression tracking is off, so the daemon reports only how far each shard got",
            WhyItMatters =
                "With it off, the daemon writes each shard's position to mt_event_progression and nothing else. With it on, it "
                + "also writes a heartbeat, the agent's status, a pause reason, the node it runs on and the failure columns on "
                + "every shard state change - which is what lets the advisor tell a shard that is alive and idle from one that "
                + "has stopped, use a projection's own lag thresholds, and say why a shard failed.",
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
                "Off by default because turning it on changes what every deployment writes on each shard state change; the cost "
                + "is a handful of small columns per shard row, and the columns already exist since Marten 9.31."),
        });
    }
}
