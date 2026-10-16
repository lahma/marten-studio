using JasperFx.Events;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// E03: one stream takes more than a fifth of the recent appends.
/// </summary>
/// <remarks>
/// <para>
/// Every append bumps its stream's version in <c>mt_streams</c> inside the appending transaction - Rich appends with
/// <c>update ... set version = ... where version = ...</c>, Quick ones inside <c>mt_quick_append_events</c> - so
/// concurrent appends to one stream queue on that row, and <c>FetchForExclusiveWriting</c> takes it
/// <c>for update</c> on purpose. One stream with a large share of the store's appends is where that queue forms.
/// </para>
/// <para>
/// <b>A stream id is data.</b> On a conjoined event store a stream belongs to one tenant, so the finding names that
/// tenant (<see cref="HealthFinding.TenantId" />) and is shown only to a visitor through the activity gate whom the
/// store policy lets see that tenant; elsewhere it is shown at the check's own gate, the store policy for the
/// database as a whole. A stream that has been archived is no longer appended to and is not a finding, and Marten's
/// own tombstone stream is left out of the ranking by the read itself.
/// </para>
/// </remarks>
[HealthCheck("E03", HealthArea.Events, HealthCostTier.BoundedData, "Hot stream")]
internal sealed class HotStreamCheck : HealthCheck<TrafficProfile>
{
    /// <summary>The share of the window's appends above which one stream is hot.</summary>
    internal const double HotShare = 0.20;

    /// <summary>The fewest appends (tombstones aside) a window may have and be judged.</summary>
    internal const long MinimumWindow = 1_000;

    /// <summary>The fewest appends one stream must have taken, whatever its share.</summary>
    internal const long MinimumStreamEvents = 200;

    /// <summary>
    /// The append rate, over the stream's own span in the window, from which writers plausibly overlap on its row:
    /// at or above it the finding is a Warning about contention, below it an Info about a stream that takes a large
    /// share of a quiet store - a long-lived ledger, a settings stream - with nothing queueing behind it.
    /// </summary>
    internal const double ContendedAppendsPerSecond = 1.0;

    /// <summary>The append rate of one stream over its span in the window; a burst at one instant counts as contended.</summary>
    internal static double AppendsPerSecond(TrafficProfileRow stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        double seconds = stream is { FirstAt: { } first, LastAt: { } last } ? (last - first).TotalSeconds : 0;
        return seconds < 1 ? stream.Events : stream.Events / seconds;
    }

    /// <inheritdoc />
    public override Task<TrafficProfile> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        EventHealth.TrafficAsync(context, cancellationToken);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(TrafficProfile facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        long appends = facts.Events - (facts.Totals.Tombstones ?? 0);

        if (appends < MinimumWindow)
        {
            return HealthVerdict.Quiet(
                "The recent window holds " + EventHealth.Count(appends) + " appends; a hot stream is judged over at least "
                + EventHealth.Count(MinimumWindow) + ".");
        }

        List<HealthFinding> findings = [];

        foreach (TrafficProfileRow stream in facts.Streams)
        {
            double share = (double) stream.Events / appends;

            if (share <= HotShare || stream.Events < MinimumStreamEvents || stream.Name is null)
            {
                continue;
            }

            if (stream.Archived is { } archived && archived >= stream.Events)
            {
                // Archived since: nothing appends to it any more, so there is no queue to form.
                continue;
            }

            findings.Add(Finding(facts, stream, appends, share));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        TrafficProfileRow? busiest = facts.Streams.Count > 0 ? facts.Streams[0] : null;
        return HealthVerdict.Quiet(
            busiest is null
                ? null
                : "The busiest stream took " + EventHealth.Percent((double) busiest.Events / appends) + " of the recent window's "
                    + EventHealth.Count(appends) + " appends.");
    }

    private static HealthFinding Finding(TrafficProfile facts, TrafficProfileRow stream, long appends, double share)
    {
        string? tenant = facts.Conjoined ? stream.TenantId : null;

        List<HealthEvidence> evidence =
        [
            new("Stream", stream.Name!),
            new("Its appends in the window", EventHealth.Count(stream.Events) + " of " + EventHealth.Count(appends) + " (" + EventHealth.Percent(share) + ")"),
            new("Window", facts.DescribeWindow()),
        ];

        if (stream is { FirstAt: { } first, LastAt: { } last })
        {
            evidence.Add(new HealthEvidence("Its appends span", EventHealth.Duration(last - first) + ", to " + EventHealth.Time(last)));
        }

        if (tenant is not null)
        {
            evidence.Add(new HealthEvidence("Tenant", tenant));

            if (facts.TenantShare(tenant) is { } tenantShare)
            {
                evidence.Add(new HealthEvidence("The tenant's share of the window", EventHealth.Percent(tenantShare)));
            }
        }

        double rate = AppendsPerSecond(stream);
        bool contended = rate >= ContendedAppendsPerSecond;

        evidence.Add(new HealthEvidence("Its append rate", Rate(rate)));
        evidence.Add(new HealthEvidence("Append mode", facts.AppendMode.ToString()));

        bool rich = facts.AppendMode == EventAppendMode.Rich;

        string queue =
            "Every append bumps its stream's version in mt_streams inside the appending transaction, so appends to one "
            + "stream wait for each other on that row, and FetchForExclusiveWriting locks it for update on purpose. ";

        return new HealthFinding
        {
            Code = "E03",
            Severity = contended ? HealthSeverity.Warning : HealthSeverity.Info,
            Title = contended
                ? "One stream took " + EventHealth.Percent(share) + " of the recent appends, at " + Rate(rate)
                : "One stream took " + EventHealth.Percent(share) + " of the recent appends, though only at " + Rate(rate),
            WhyItMatters = contended
                ? queue
                    + "A stream that takes this much of the store's traffic at this rate is where that queue forms: its appends "
                    + "serialize, "
                    + (rich
                        ? "and under Rich appends every one that loses a version check fails with a concurrency exception and "
                            + "leaves tombstones (E02)."
                        : "and one that loses a race for the same version inside mt_quick_append_events fails, and can leave a "
                            + "gap the daemon later skips (D07).")
                    + " Splitting what the stream models - a stream per order rather than per customer, a new stream per period "
                    + "- spreads the writes; a lock taken once per stream is cheap only while few writers want the same one."
                : queue
                    + "At under one append a second, writers are unlikely to meet on this one, so this is not contention yet - "
                    + "the store is quiet and this stream carries much of what it does: a ledger, a settings stream, a "
                    + "long-lived aggregate. It matters if the traffic grows, and because a stream that only grows is replayed "
                    + "whole by every live aggregation of it (E06).",
            Subject = (tenant is null ? string.Empty : tenant + "/") + stream.Name,
            TenantId = tenant,
            Evidence = evidence,
            Caveat =
                "Counted over the newest " + EventHealth.Count(HealthEventQueries.TrafficWindow) + " events, read once "
                + "when somebody asked for the deeper checks; tombstones are left out of both the stream's count and the total."
                + " The rate is the stream's appends over the span between its first and last in the window."
                + (tenant is null ? string.Empty : " The stream belongs to one tenant, so it is shown only to a visitor who may see that tenant."),
        };
    }

    /// <summary>"3.2 appends a second", "40 appends a minute".</summary>
    private static string Rate(double perSecond) =>
        perSecond >= 1
            ? perSecond.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " appends a second"
            : (perSecond * 60).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " appends a minute";
}
