using JasperFx.Events;

using Marten;
using Marten.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One gap the high-water detector skipped.</summary>
/// <param name="Starting">The mark it moved from.</param>
/// <param name="Ending">The mark it moved to.</param>
/// <param name="At">When, in server time.</param>
internal sealed record SkippedGap(long Starting, long Ending, DateTimeOffset At)
{
    /// <summary>How many sequence numbers the move spans - the skipped ones and the committed ones between them.</summary>
    public long Spans => Math.Max(0, Ending - Starting);
}

/// <summary>What D07 read.</summary>
/// <param name="HasAsyncWork">Whether the store registers an async projection or a subscription.</param>
/// <param name="Skips">The newest skips, newest first - at most <see cref="HealthEventQueries.MaxSkipRows" />.</param>
/// <param name="ServerNow">The server's clock when they were read.</param>
/// <param name="TransactionEvidence"><c>Projections.UseTransactionEvidenceForGapSkipping</c> (on by default).</param>
/// <param name="SkipDespiteLiveAfter"><c>Projections.SkipStaleGapsDespiteLiveTransactionsAfter</c> (unset by default).</param>
/// <param name="AppendMode">The store's append mode.</param>
/// <param name="ExclusiveLockOnConcurrentAppends"><c>Events.UseExclusiveLockOnConcurrentAppends</c>.</param>
/// <param name="TenantPartitioned">Whether events are partitioned by tenant, whose per-tenant marks record no skips.</param>
internal sealed record SkippedGapsFacts(
    bool HasAsyncWork,
    IReadOnlyList<SkippedGap> Skips,
    DateTimeOffset ServerNow,
    bool TransactionEvidence,
    TimeSpan? SkipDespiteLiveAfter,
    EventAppendMode AppendMode,
    bool ExclusiveLockOnConcurrentAppends,
    bool TenantPartitioned)
{
    /// <summary>Whether the read was cut at its limit, so a count is "at least".</summary>
    public bool Truncated => Skips.Count >= HealthEventQueries.MaxSkipRows;

    /// <summary>
    /// Whether this configuration lets the detector skip a gap whose reserving transaction may still be alive: the
    /// transaction evidence switched off, or the escape hatch set.
    /// </summary>
    public bool MaySkipLiveAppends => !TransactionEvidence || SkipDespiteLiveAfter is not null;
}

/// <summary>
/// D07: the async daemon's high-water detector skipped gaps in the event sequence, recently.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a skip is</b>, at <c>V9.39.0</c> and <c>V9.50.2</c> (<c>HighWaterDetector.DetectInSafeZone</c> and
/// <c>mt_mark_progression_with_skip</c>): when the mark has been held under a gap for
/// <c>StaleSequenceThreshold</c> and the transaction evidence shows nobody alive who could still fill it - or the
/// <c>SkipStaleGapsDespiteLiveTransactionsAfter</c> cap has run out, or the evidence is switched off - the detector
/// moves the mark past it and, under <c>EnableAdvancedAsyncTracking</c>, records the move in
/// <c>mt_high_water_skips</c>. Only the store-global mark records them; per-tenant marks never do.
/// </para>
/// <para>
/// <b>Severity follows the configuration.</b> Under the defaults a skip is a range of numbers that rolled-back
/// appends drew - lost numbers, not lost events - and is an Info. Where the configuration lets the detector skip past
/// a live append, a skip in the last hour is a Warning: if that append committed afterwards, every async projection
/// has already passed its events and will never see them. Below Marten 9.47 the evidence's in-progress-xid clause
/// (<c>pg_snapshot_xip(pg_current_snapshot())</c>) counts every transaction on the server, prepared ones included;
/// #5605 scoped it to this database and had to add <c>pg_prepared_xacts</c> back. So an older host errs toward
/// holding the mark, never toward skipping - which the caveat says.
/// </para>
/// <para>
/// Tier 1: the newest <see cref="HealthEventQueries.MaxSkipRows" /> rows by primary key. The table exists only
/// under advanced async tracking, so its absence is an expected state - K03 already suggests turning it on.
/// </para>
/// </remarks>
[HealthCheck("D07", HealthArea.Daemon, HealthCostTier.Catalog, "Gaps were skipped")]
internal sealed class SkippedGapsCheck : HealthCheck<SkippedGapsFacts>
{
    /// <summary>A skip this recent, under a configuration that may skip live appends, is a Warning.</summary>
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    /// <summary>A skip this recent is worth saying anything about.</summary>
    internal static readonly TimeSpan DayWindow = TimeSpan.FromDays(1);

    /// <summary>The projections screen, where D01's Explain sits beside a held mark.</summary>
    internal const string ProjectionsRoute = "projections";

    /// <summary>The first Marten whose gap evidence counts only this database's transactions (#5605).</summary>
    internal static readonly Version EvidenceScopedToDatabaseFrom = new(9, 47, 0);

    /// <inheritdoc />
    public override async Task<SkippedGapsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyEventStoreOptions events = context.StoreOptions.Events;

        (bool evidence, TimeSpan? cap) = context.Store.Options is StoreOptions concrete
            ? (concrete.Projections.UseTransactionEvidenceForGapSkipping, concrete.Projections.SkipStaleGapsDespiteLiveTransactionsAfter)
            : (true, null);

        bool exclusive = events is EventGraph graph && graph.UseExclusiveLockOnConcurrentAppends;

        SkippedGapsFacts facts = new(
            DaemonAccessor.HasAsyncWork(context.Store),
            [],
            context.Now,
            evidence,
            cap,
            events.AppendMode,
            exclusive,
            events.UseTenantPartitionedEvents);

        if (!facts.HasAsyncWork)
        {
            return facts;
        }

        if (!events.EnableAdvancedAsyncTracking)
        {
            throw new HealthCheckUnavailableException(
                "Advanced async tracking is off, so Marten keeps no record of the gaps the daemon skips (K03 says how to turn it on).");
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        if (shape.Skips is null)
        {
            throw new HealthCheckUnavailableException(
                "mt_high_water_skips does not exist in this database yet; Marten creates it with the event store's next schema apply.");
        }

        IReadOnlyList<HighWaterSkipRow> rows = await context.Sql
            .QueryAsync(HealthEventQueries.HighWaterSkips(shape.Schema), HealthEventQueries.ReadSkip, cancellationToken)
            .ConfigureAwait(false);

        List<SkippedGap> skips = [];
        foreach (HighWaterSkipRow row in rows)
        {
            if (row is { Ending: { } ending, Starting: { } starting, At: { } at })
            {
                skips.Add(new SkippedGap(starting, ending, at));
            }
        }

        return facts with
        {
            Skips = skips,
            ServerNow = rows.Count > 0 ? rows[0].ServerNow : context.Now,
        };
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(SkippedGapsFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!facts.HasAsyncWork)
        {
            return HealthVerdict.Quiet("The store registers no async projection or subscription, so the daemon has no mark to move.");
        }

        string tenantNote = facts.TenantPartitioned
            ? " This store partitions its events by tenant, and the per-tenant marks record no skips; only the store-global mark's are here."
            : string.Empty;

        if (facts.Skips.Count == 0)
        {
            return HealthVerdict.Quiet("mt_high_water_skips is empty: the daemon has never skipped a gap here." + tenantNote);
        }

        List<SkippedGap> day = [.. facts.Skips.Where(x => facts.ServerNow - x.At <= DayWindow)];
        List<SkippedGap> hour = [.. day.Where(x => facts.ServerNow - x.At <= RecentWindow)];
        SkippedGap latest = facts.Skips.MaxBy(static x => x.At)!;

        if (day.Count == 0)
        {
            return HealthVerdict.Quiet(
                "The latest skip was at " + EventHealth.Time(latest.At) + ", more than a day ago." + tenantNote);
        }

        bool recent = hour.Count > 0;
        bool risky = facts.MaySkipLiveAppends;
        HealthSeverity severity = recent && risky ? HealthSeverity.Warning : HealthSeverity.Info;
        string more = facts.Truncated && day.Count == facts.Skips.Count ? " or more" : string.Empty;

        List<HealthEvidence> evidence =
        [
            new("Skips in the last hour", EventHealth.Count(hour.Count) + (hour.Count == facts.Skips.Count ? more : string.Empty)),
            new("Skips in the last day", EventHealth.Count(day.Count) + more),
            new("Latest skip", "#" + EventHealth.Count(latest.Starting) + " to #" + EventHealth.Count(latest.Ending)
                + " (" + EventHealth.Count(latest.Spans) + " sequence numbers) at " + EventHealth.Time(latest.At)),
            new("Projections.UseTransactionEvidenceForGapSkipping", facts.TransactionEvidence ? "true" : "false"),
            new("Projections.SkipStaleGapsDespiteLiveTransactionsAfter", facts.SkipDespiteLiveAfter is { } cap ? EventHealth.Duration(cap) : "not set"),
            new("Append mode", facts.AppendMode.ToString()),
        ];

        string count = EventHealth.Count(recent ? hour.Count : day.Count) + (recent ? (hour.Count == facts.Skips.Count ? more : string.Empty) : more);
        string span = recent ? "hour" : "day";

        bool serverWideEvidence = evaluation.Runtime.VersionOf(RuntimePackage.Marten) is { } marten && marten < EvidenceScopedToDatabaseFrom;

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "D07",
            Severity = severity,
            Title = "The daemon skipped past gaps in the event sequence " + count + (count == "1" ? " time" : " times") + " in the last " + span,
            WhyItMatters = risky
                ? "A skip moves the high-water mark past sequence numbers that were handed out and not committed, so the async "
                    + "projections can go on. This store lets the detector do that while a transaction that could still fill the gap "
                    + "may be alive - " + (facts.TransactionEvidence
                        ? "the SkipStaleGapsDespiteLiveTransactionsAfter cap is set, and past it the evidence is overruled"
                        : "UseTransactionEvidenceForGapSkipping is off, so a gap is skipped on the clock alone")
                    + ". If such a transaction committed afterwards, its events sit inside a range every async projection and "
                    + "subscription has already passed, and none of them will ever see those events. Explain, on the projections "
                    + "screen while the mark is held, names the transactions that could be holding it."
                : "A skip moves the high-water mark past sequence numbers that were handed out and not committed, so the async "
                    + "projections can go on. With the transaction evidence on and no cap, Marten skips a gap only once nothing alive "
                    + "could still fill it, so each of these is a range of numbers drawn by appends that rolled back - lost numbers, "
                    + "not lost events. Many of them mean many failed appends: "
                    + (facts.AppendMode == EventAppendMode.Rich
                        ? "under Rich appends a failed save normally leaves tombstones (E02) rather than a gap, so these are failures "
                            + "the tombstones did not cover."
                        : "under " + facts.AppendMode + " appends an append that fails inside mt_quick_append_events after drawing "
                            + "its numbers - two appends racing past one version check, most often - leaves a gap rather than a "
                            + "tombstone."),
            Evidence = evidence,
            Caveat =
                "Read from mt_high_water_skips, which records each move of the store-global mark past a gap, in server time; a skip "
                + "spans the committed numbers between the old mark and the new one as well as the missing ones."
                + (serverWideEvidence
                    ? " Below Marten 9.47 the gap evidence counts in-progress transactions in every database on the server "
                        + "(#5605), so it errs toward holding the mark rather than skipping."
                    : string.Empty)
                + tenantNote,
            Remedy = Remedy(facts),
            Link = new HealthLink("Open the projections screen (Explain, while the mark is held)", ProjectionsRoute),
        });
    }

    private static HealthRemedy? Remedy(SkippedGapsFacts facts)
    {
        if (!facts.TransactionEvidence)
        {
            return new HealthRemedy(
                "opts.Projections.UseTransactionEvidenceForGapSkipping = true;",
                "Marten's default: a gap is skipped only once nothing alive could still fill it, and a slow append is waited out "
                + "rather than passed. The cost is that a session left idle in transaction holds the mark until it ends - "
                + "PostgreSQL's idle_in_transaction_session_timeout is the backstop.");
        }

        if (facts.SkipDespiteLiveAfter is not null)
        {
            return new HealthRemedy(
                "opts.Projections.SkipStaleGapsDespiteLiveTransactionsAfter = null;",
                "Without the cap a gap a live transaction may still fill is never skipped, at the price of a mark that can stay "
                + "pinned behind a forgotten transaction (D01) - which the cap exists to bound. Keep it if that is the trade you "
                + "chose, and make it longer than your longest legitimate append.");
        }

        if (facts.AppendMode != EventAppendMode.Rich && !facts.ExclusiveLockOnConcurrentAppends)
        {
            return new HealthRemedy(
                "opts.Events.UseExclusiveLockOnConcurrentAppends = true;",
                "For appends with an expected version: the loser of a race waits at the version check and fails before it draws a "
                + "sequence number, so it leaves no gap. Appends without an expected version are unaffected; contended streams pay "
                + "for it in throughput.");
        }

        return null;
    }
}
