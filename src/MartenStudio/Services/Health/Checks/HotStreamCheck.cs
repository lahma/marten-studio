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
/// <b>A share is not a queue.</b> Only writers that overlap queue, so the Warning is for a stream written by at least
/// <see cref="MinimumContendingTransactions" /> transactions at <see cref="ContendedTransactionsPerSecond" /> or more -
/// transactions, read as the distinct <c>xmin</c> of its rows, never events: one <c>SaveChanges</c> of a thousand
/// events is one writer, and a bulk import must not read as contention for as long as it stays in the window. A large
/// share from fewer or slower transactions is an Info.
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
    /// The rate of appending <em>transactions</em>, over the stream's own span in the window, from which writers
    /// plausibly overlap on its row: at or above it (and with <see cref="MinimumContendingTransactions" />) the finding
    /// is a Warning about contention, below it an Info about a stream that takes a large share of the store without
    /// anybody waiting on anybody - a long-lived ledger, a settings stream, a bulk import.
    /// </summary>
    internal const double ContendedTransactionsPerSecond = 1.0;

    /// <summary>
    /// The fewest transactions a stream must have been written by before its rate can say that writers met on it: a
    /// bulk import in a handful of <c>SaveChanges</c> calls is one writer, however fast it went.
    /// </summary>
    internal const long MinimumContendingTransactions = 10;

    /// <summary>How many transactions wrote the stream's events in the window (each event counted as one if unknown).</summary>
    internal static long TransactionsOf(TrafficProfileRow stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.Transactions ?? stream.Events;
    }

    /// <summary>
    /// The stream's appending transactions a second, over the span between its first and last append in the window,
    /// taken as at least a second - so a burst inside one second is counted as that many transactions in that second.
    /// </summary>
    internal static double TransactionsPerSecond(TrafficProfileRow stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        double seconds = stream is { FirstAt: { } first, LastAt: { } last } ? (last - first).TotalSeconds : 0;
        return TransactionsOf(stream) / Math.Max(seconds, 1);
    }

    /// <summary>Whether the stream's writers plausibly queued on its row: enough transactions, fast enough.</summary>
    internal static bool Contended(TrafficProfileRow stream) =>
        TransactionsOf(stream) >= MinimumContendingTransactions && TransactionsPerSecond(stream) >= ContendedTransactionsPerSecond;

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

        long transactions = TransactionsOf(stream);
        double rate = TransactionsPerSecond(stream);
        bool contended = Contended(stream);

        evidence.Add(new HealthEvidence("Transactions that appended to it", EventHealth.Count(transactions)));
        evidence.Add(new HealthEvidence("Their rate", Rate(rate)));
        evidence.Add(new HealthEvidence("Append mode", facts.AppendMode.ToString()));

        bool rich = facts.AppendMode == EventAppendMode.Rich;
        string percent = EventHealth.Percent(share);

        string queue =
            "Every append bumps its stream's version in mt_streams inside the appending transaction, so concurrent appends "
            + "to one stream wait for each other on that row, and FetchForExclusiveWriting locks it for update on purpose. ";

        string title;
        string why;

        if (contended)
        {
            title = "One stream took " + percent + " of the recent appends, from " + Rate(rate);
            why = queue
                + "A stream written by this many transactions at this rate is where that queue forms: its appends serialize, "
                + (rich
                    ? "and under Rich appends every one that loses a version check fails with a concurrency exception and "
                        + "leaves tombstones (E02)."
                    : "and one that loses a race for the same version inside mt_quick_append_events fails, and can leave a "
                        + "gap the daemon later skips (D07).")
                + " Splitting what the stream models - a stream per order rather than per customer, a new stream per period "
                + "- spreads the writes; a lock taken once per stream is cheap only while few writers want the same one.";
        }
        else
        {
            title = transactions == 1
                ? "One stream took " + percent + " of the recent appends, all in one transaction"
                : transactions < MinimumContendingTransactions
                    ? "One stream took " + percent + " of the recent appends, in only " + EventHealth.Count(transactions) + " transactions"
                    : "One stream took " + percent + " of the recent appends, though from only " + Rate(rate);
            why = queue
                + (transactions < MinimumContendingTransactions
                    ? "Its appends came in " + (transactions == 1 ? "one transaction" : EventHealth.Count(transactions) + " transactions")
                        + " - a bulk import, or one writer saving in batches - so nobody waited on anybody: a share is not a "
                        + "queue. "
                    : "At under one transaction a second, writers are unlikely to meet on this one, so this is not contention "
                        + "yet - the store is quiet and this stream carries much of what it does: a ledger, a settings stream, a "
                        + "long-lived aggregate. ")
                + "It matters if many writers start appending to it, and because a stream that only grows is replayed whole by "
                + "every live aggregation of it (E06).";
        }

        return new HealthFinding
        {
            Code = "E03",
            Severity = contended ? HealthSeverity.Warning : HealthSeverity.Info,
            Title = title,
            WhyItMatters = why,
            Subject = (tenant is null ? string.Empty : tenant + "/") + stream.Name,
            TenantId = tenant,
            Evidence = evidence,
            Caveat =
                "Counted over the newest " + EventHealth.Count(HealthEventQueries.TrafficWindow) + " events, read once "
                + "when somebody asked for the deeper checks; tombstones are left out of both the stream's count and the total."
                + " The rate counts transactions, not events - the distinct transaction ids (xmin) that wrote the stream's "
                + "events, over the span between its first and last append in the window, taken as at least a second - so one "
                + "SaveChanges of a thousand events is one writer. Several fast transactions from one writer look the same as "
                + "several writers."
                + (tenant is null ? string.Empty : " The stream belongs to one tenant, so it is shown only to a visitor who may see that tenant."),
        };
    }

    /// <summary>"3.2 transactions a second", "40 transactions a minute".</summary>
    private static string Rate(double perSecond) =>
        perSecond >= 1
            ? perSecond.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " transactions a second"
            : (perSecond * 60).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " transactions a minute";
}
