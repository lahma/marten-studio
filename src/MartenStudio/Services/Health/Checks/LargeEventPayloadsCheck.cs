using MartenStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// E04: the recent events' payloads are large - the 95th percentile of their stored size is over 64 KB.
/// </summary>
/// <remarks>
/// <para>
/// The size is <c>pg_column_size(data)</c> (plus <c>bdata</c> where the store has binary events): what the row
/// stores, after PostgreSQL's TOAST compression - so the JSON an application serialises is usually larger still. The
/// percentiles are computed in the database over the recent window; no payload leaves it.
/// </para>
/// <para>
/// Every rebuild replays every event, and a live aggregation replays a stream's events on every fetch, so the payload
/// size multiplies into both. Tier 2, the shared recent-traffic read; a window of fewer than
/// <see cref="MinimumWindow" /> events is too small for a percentile to mean anything.
/// </para>
/// </remarks>
[HealthCheck("E04", HealthArea.Events, HealthCostTier.BoundedData, "Large event payloads")]
internal sealed class LargeEventPayloadsCheck : HealthCheck<TrafficProfile>
{
    /// <summary>The 95th-percentile stored size above which the payloads are a finding.</summary>
    internal const long MaxP95Bytes = 64 * 1024;

    /// <summary>The fewest events a window may have for its percentile to be judged.</summary>
    internal const long MinimumWindow = 100;

    /// <summary>The fewest events of one type for its own percentile to be named as evidence.</summary>
    internal const long MinimumTypeEvents = 20;

    /// <summary>How many of the largest types the evidence names.</summary>
    internal const int TypesNamed = 3;

    /// <inheritdoc />
    public override Task<TrafficProfile> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        EventHealth.TrafficAsync(context, cancellationToken);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(TrafficProfile facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Events < MinimumWindow || facts.Totals.P95Bytes is not { } p95)
        {
            return HealthVerdict.Quiet(
                "The recent window holds " + EventHealth.Count(facts.Events) + " events; payload sizes are judged over at least "
                + EventHealth.Count(MinimumWindow) + ".");
        }

        if (p95 <= MaxP95Bytes)
        {
            return HealthVerdict.Quiet(
                "The recent events' payloads: median " + EventHealth.Bytes(facts.Totals.MedianBytes ?? 0) + ", 95th percentile "
                + EventHealth.Bytes(p95) + ".");
        }

        List<HealthEvidence> evidence =
        [
            new("95th percentile", EventHealth.Bytes(p95)),
            new("Median", EventHealth.Bytes(facts.Totals.MedianBytes ?? 0)),
            new("Largest", EventHealth.Bytes(facts.Totals.MaxBytes ?? 0)),
            new("Stored in the window", EventHealth.Bytes(facts.Totals.Bytes)),
            new("Window", facts.DescribeWindow()),
        ];

        foreach (TrafficProfileRow type in facts.Types
                     .Where(static x => x.Events >= MinimumTypeEvents && x.P95Bytes is > MaxP95Bytes && x.Name is not null)
                     .OrderByDescending(static x => x.P95Bytes)
                     .Take(TypesNamed))
        {
            evidence.Add(new HealthEvidence(
                "Event type " + type.Name,
                "95th percentile " + EventHealth.Bytes(type.P95Bytes ?? 0) + " over " + EventHealth.Count(type.Events) + " events"));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "E04",
            Severity = HealthSeverity.Warning,
            Title = "The recent events are large: the 95th percentile is " + EventHealth.Bytes(p95),
            WhyItMatters =
                "An event is read again every time anything replays it: every projection rebuild reads every event, every live "
                + "aggregation reads the whole stream on each fetch, and the async daemon reads each one once per projection. At "
                + "this size each of those reads is mostly payload - TOAST decompression, network and deserialisation - and the "
                + "event store grows by the same amount for good. Events that carry documents, files or whole aggregates are "
                + "usually better as a reference to them, with the large part stored once beside the stream.",
            Evidence = evidence,
            Caveat =
                "pg_column_size: the size PostgreSQL stores, after compression, over the newest "
                + EventHealth.Count(HealthEventQueries.TrafficWindow) + " events, read once when somebody asked for the "
                + "deeper checks. The JSON your application serialises is usually larger.",
        });
    }
}
