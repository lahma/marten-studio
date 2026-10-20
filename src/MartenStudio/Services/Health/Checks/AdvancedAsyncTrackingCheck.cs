using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// K03: advanced async tracking is off, so a skipped gap leaves no record.
/// </summary>
/// <remarks>
/// <para>
/// With <c>Events.EnableAdvancedAsyncTracking</c> on, the high-water detector moves the mark past a gap it has
/// given up on through <c>TryMarkHighWaterSkippingAsync</c>, which writes the skipped range to
/// <c>mt_high_water_skips</c>; with it off, the mark simply moves (<c>HighWaterDetector.persistDetectedMarkAsync</c>
/// at V9.31.0). Marten 9 turns it on by default (<c>EventGraph</c>'s V9 default flips), so a store that has
/// it off turned it off - or called <c>StoreOptions.RestoreV8Defaults()</c>.
/// </para>
/// <para>Info, and only for a store whose daemon has something to run.</para>
/// </remarks>
[HealthCheck("K03", HealthArea.Configuration, HealthCostTier.InProcess, "Advanced async tracking", Gate = HealthGate.None)]
internal sealed class AdvancedAsyncTrackingCheck : HealthCheck<AsyncTrackingFacts>
{
    /// <inheritdoc />
    public override Task<AsyncTrackingFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        Task.FromResult(AsyncTrackingFacts.Read(context, static options => options.Events.EnableAdvancedAsyncTracking));

    /// <inheritdoc />
    public override HealthVerdict Evaluate(AsyncTrackingFacts facts, HealthEvaluation evaluation)
    {
        switch (facts.HasAsyncWork)
        {
            case null:
                return HealthVerdict.Quiet(AsyncTrackingFacts.UnreadableNote);
            case false:
                return HealthVerdict.Quiet("The store registers no async projection or subscription, so no gap is ever skipped for one.");
        }

        if (facts.Enabled)
        {
            return HealthVerdict.Quiet();
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K03",
            Severity = HealthSeverity.Info,
            Title = "Advanced async tracking is off, so a gap the daemon skips leaves no record",
            WhyItMatters =
                "When the high-water mark is held up by a gap in the event sequence for long enough, the daemon decides the gap "
                + "will never fill and moves past it. With advanced async tracking on, Marten writes every skipped range to "
                + "mt_high_water_skips - the only way to find out later that an async projection never saw events whose "
                + "transaction committed after the daemon gave up on them. With it off, the mark simply moves and nothing says so. "
                + "Marten 9 turns it on by default; this store turned it off, or restored the Marten 8 defaults.",
            Evidence =
            [
                new HealthEvidence("Events.EnableAdvancedAsyncTracking", "false"),
                facts.AsyncWorkEvidence(),
            ],
            Caveat = "Read from this process's configuration.",
            Remedy = new HealthRemedy(
                "opts.Events.EnableAdvancedAsyncTracking = true;",
                "Adds mt_high_water_skips and its bookkeeping to the event schema, created by the next schema apply."),
        });
    }
}
