using System.Globalization;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D01 reads.</summary>
/// <param name="Configuration">The store's daemon settings.</param>
/// <param name="Reading">The high-water reading.</param>
internal sealed record HighWaterPinnedFacts(DaemonCheckConfiguration Configuration, HighWaterReading Reading);

/// <summary>
/// D01: the high-water mark is pinned behind a gap in the event sequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>What Marten records, and what this reads.</b> The daemon's high-water mark only moves over contiguous committed
/// sequence numbers, so it stops below any number that was handed out and not committed. Since 9.39 (#5109) Marten
/// keeps a durable clock for that: the <c>HighWaterStuckGap</c> progression row, whose <c>last_seq_id</c> is the mark
/// the daemon first saw pinned and whose <c>last_updated</c> is when, in server time. Read at V9.39.0 and V9.50.2:
/// the row is upserted only when the mark differs and <b>nothing ever deletes it</b> - it self-invalidates by mark.
/// So the mark is pinned now exactly when the row's mark equals the <c>HighWaterMark</c> row's, the sequence has
/// handed out more than that, and the next number is not committed (one primary-key probe of <c>mt_events</c>).
/// </para>
/// <para>
/// <b>Only a pin that holds something back.</b> Marten records the pin whether or not anything committed waits above
/// it - its <c>GapDetector</c> returns the mark itself when nothing above is committed and <c>trackStuckGap</c> still
/// upserts the row (V9.39.0, V9.50.2). That is every long append transaction on an idle store (a bulk import), every
/// long transaction beside a rolled-back append, and, under <c>UseTransactionEvidenceForGapSkipping = false</c>,
/// every tail gap under 32 forever (#3865) - and in none of them is any event late. So D01 fires only when a
/// committed event waits behind the gap (<see cref="HighWaterReading.IsHoldingCommittedEvents" />). Critical is the
/// delay measured from the advisor's first sample that saw one waiting behind this mark
/// (<see cref="DaemonSeries.WaitingMark" />), never the stuck row's age, which can predate anything waiting by hours. A
/// Warning comes sooner: on first sight, when the row says the mark has already been held for more than a minute - so a
/// stall that has lasted all morning is reported by the first sweep that sees a committed event behind it.
/// </para>
/// <para>
/// <b>Why it is not reported sooner.</b> Most gaps fill within milliseconds - they are appends that drew a number
/// and have not committed - and Marten holds every gap for <c>StaleSequenceThreshold</c> (3 s) before it asks whether
/// any transaction could still fill it, skipping it as soon as none could. A committed event still waiting after a
/// minute is held by a transaction that may yet commit (Warning); after ten, Critical.
/// </para>
/// <para>
/// <b>Not for a store that partitions its events by tenant</b>: every tenant has its own mark there and Marten keeps
/// no durable stuck clock per tenant. And a host below Marten 9.38 in Quick append mode could pin a gap permanently
/// with one failed <c>StartStream</c> (#5454) - which cannot happen here: the studio's floor is 9.39.
/// </para>
/// <para>
/// <b>Explain</b> is on the projections screen, on demand: it asks Marten's own <c>GapLivenessProbe</c> questions
/// (open transactions older than the stall, <c>RowExclusiveLock</c> holders on <c>mt_events</c>, write transactions
/// in this database) plus <c>pg_prepared_xacts</c>.
/// </para>
/// </remarks>
[HealthCheck("D01", HealthArea.Daemon, HealthCostTier.Catalog, "High-water mark pinned behind a gap")]
internal sealed class HighWaterPinnedCheck : HealthCheck<HighWaterPinnedFacts>
{
    /// <inheritdoc />
    public override async Task<HighWaterPinnedFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
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
        return new HighWaterPinnedFacts(configuration, reading);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(HighWaterPinnedFacts facts)
    {
        yield return new HealthSamplePart(DaemonSeries.HighestSequence, facts.Reading.HighestSequence);

        if (facts.Reading.Mark is { } mark)
        {
            yield return new HealthSamplePart(DaemonSeries.HighWaterMark, mark);
        }

        yield return new HealthSamplePart(
            DaemonSeries.WaitingMark,
            facts.Reading.IsHoldingCommittedEvents && facts.Reading.Mark is { } held ? held : DaemonSeries.NotWaiting);
    }

    /// <summary>
    /// How long the advisor has seen a committed event waiting behind <paramref name="mark" />: from the oldest sample
    /// of the unbroken run, newest first, that recorded a wait at this mark. A sample of another mark, of no wait, or in
    /// which D01 did not read ends the run - so a gap that held, filled and came back starts again.
    /// </summary>
    internal static TimeSpan WaitingFor(HealthHistoryView history, long mark, DateTimeOffset now)
    {
        DateTimeOffset? since = null;

        for (int i = history.Samples.Count - 1; i >= 0; i--)
        {
            HealthSample sample = history.Samples[i];

            if (sample.At > now)
            {
                continue;
            }

            if (!sample.Values.TryGetValue(DaemonSeries.WaitingMark, out double waiting) || (long) waiting != mark || waiting < 0)
            {
                break;
            }

            since = sample.At;
        }

        return since is { } first ? now - first : TimeSpan.Zero;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(HighWaterPinnedFacts facts, HealthEvaluation evaluation)
    {
        HighWaterReading reading = facts.Reading;
        DaemonCheckConfiguration configuration = facts.Configuration;

        if (reading.Mark is not { } mark)
        {
            return HealthVerdict.Quiet("The daemon has not written a high-water mark in this database yet.");
        }

        if (reading.HighestSequence <= mark)
        {
            return HealthVerdict.Quiet("The high-water mark is at the latest sequence number handed out; there is no gap above it.");
        }

        if (!reading.StuckRowIsCurrent)
        {
            return HealthVerdict.Quiet(
                "Marten has not recorded the mark as pinned at its current value, so the daemon has seen it move or has not looked yet.");
        }

        if (!reading.GapAboveMark)
        {
            return HealthVerdict.Quiet(
                "The event just above the high-water mark is committed, so no gap holds the mark. If the mark is not moving, "
                + "nothing is advancing it: see D02 and D03.");
        }

        (long from, long to) = reading.GapRange ?? (mark + 1, reading.HighestSequence);

        string range = from == to
            ? "sequence number " + DaemonCheckConfiguration.Number(from)
            : "sequence numbers " + DaemonCheckConfiguration.Number(from) + " to " + DaemonCheckConfiguration.Number(to);

        if (reading.NextCommitted is not { } next)
        {
            return HealthVerdict.Quiet(
                "The mark is held at " + DaemonCheckConfiguration.Number(mark) + ": " + range + " " + (from == to ? "was" : "were")
                + " handed out and not committed, but no committed event waits above " + (from == to ? "it" : "them")
                + ", so nothing is held up. A long append transaction, a rolled-back append"
                + (configuration.UsesTransactionEvidence
                    ? string.Empty
                    : " or, under UseTransactionEvidenceForGapSkipping = false, a tail gap Marten never skips")
                + " looks like this; it is reported once a committed event is waiting behind it.");
        }

        TimeSpan waited = WaitingFor(evaluation.History, mark, evaluation.Now);
        TimeSpan pinned = reading.PinnedFor ?? TimeSpan.Zero;

        // On first sight the advisor has no wait of its own to measure, but Marten's record of the pin does say how long
        // the mark has been held: past a minute, with a committed event behind it, that is a Warning now - not two sweeps
        // from now. Critical stays the advisor's measured wait, because the pin can predate the event by hours.
        bool seenLong = waited > DaemonThresholds.PinnedWarning;
        bool pinnedLong = pinned > DaemonThresholds.PinnedWarning;

        if (!seenLong && !pinnedLong)
        {
            return HealthVerdict.Quiet(
                "A committed event (" + DaemonCheckConfiguration.Number(next) + ") is waiting behind a gap at the mark; the advisor has "
                + (waited > TimeSpan.Zero ? "seen it waiting for " + DaemonCheckConfiguration.Format(waited) : "only just seen it")
                + ", within the " + DaemonCheckConfiguration.Format(DaemonThresholds.PinnedWarning) + " the daemon is given before this is reported.");
        }

        HealthSeverity severity = waited > DaemonThresholds.PinnedCritical ? HealthSeverity.Critical : HealthSeverity.Warning;

        List<HealthEvidence> evidence =
        [
            new("High-water mark", DaemonCheckConfiguration.Number(mark)),
            new("Highest sequence handed out", DaemonCheckConfiguration.Number(reading.HighestSequence)),
            new("Not committed", range + "; the next committed event is " + DaemonCheckConfiguration.Number(next)),
            new("Waiting for", waited > TimeSpan.Zero
                ? "at least " + DaemonCheckConfiguration.Format(waited) + " (since the advisor first saw a committed event behind this mark)"
                : "seen in this sweep for the first time; Critical is measured from here"),
            new("Pinned since", reading.StuckSince!.Value.ToString("u", CultureInfo.InvariantCulture) + " (server time, Marten's HighWaterStuckGap row)"),
            new("Pinned for", DaemonCheckConfiguration.Format(pinned) + " (server time)"),
            new("Projections.StaleSequenceThreshold", DaemonCheckConfiguration.Format(configuration.StaleSequenceThreshold)),
            new("Projections.SkipStaleGapsDespiteLiveTransactionsAfter", configuration.SkipStaleGapsCap is { } cap
                ? DaemonCheckConfiguration.Format(cap)
                : "not set: the daemon never knowingly skips past a transaction that may still commit"),
        ];

        if (!configuration.UsesTransactionEvidence)
        {
            evidence.Add(new HealthEvidence(
                "Projections.UseTransactionEvidenceForGapSkipping",
                "false: the daemon skips by age alone, so a gap held this long is unusual"));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "D01",
            Severity = severity,
            Title = seenLong
                ? "Committed events have waited behind a gap in the event sequence for at least " + DaemonCheckConfiguration.Format(waited)
                : "A committed event is waiting behind a gap that has held the high-water mark for " + DaemonCheckConfiguration.Format(pinned),
            WhyItMatters =
                "No async projection or subscription in this database processes an event above the high-water mark, and the "
                + "mark cannot pass a sequence number that was handed out and not committed. Marten holds it while any "
                + "transaction that may have drawn that number is still open - an append that has not committed yet, a "
                + "session left idle in transaction, a prepared transaction - and skips the gap once none is. Event "
                + DaemonCheckConfiguration.Number(next) + " is committed and has been waiting behind it this long, so one of them "
                + "is still open, and every async projection here is that far behind.",
            Evidence = evidence,
            Caveat =
                "The wait is measured from the advisor's own first sample that saw a committed event behind this mark, so it is at "
                + "least this long: the advisor samples only while the Health page or the Overview is open. If no daemon has run "
                + "since Marten recorded the pin, this is the last thing one saw (see D02). Explain, on the projections screen, "
                + "asks the questions Marten's own gap-liveness probe asks and names the sessions to anyone allowed to see them.",
            Link = new HealthLink("Explain it on the projections screen", "projections"),
            Remedy = new HealthRemedy(
                "// Optional: bound how long a transaction that may still commit can hold the mark.\n"
                + "opts.Projections.SkipStaleGapsDespiteLiveTransactionsAfter = TimeSpan.FromMinutes(30);",
                "To clear this one now, find the open transaction with Explain on the projections screen and end it - or use "
                + "Advance high water mark there, which moves the mark to the latest committed event: anything that commits "
                + "inside the gap afterwards is never projected. The cap above does the same automatically once a gap has been "
                + "held that long, with the same cost, so set it only if a leaked session is likelier than a slow append. "
                + "PostgreSQL's idle_in_transaction_session_timeout removes the cause instead."),
        });
    }
}
