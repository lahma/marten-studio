using JasperFx.Events.Daemon;

using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// D01's judgement, over stated facts - every threshold and every "not a gap" on its own, and the rule the H2 review
/// made: a pin is a finding only while a committed event waits behind it, timed by the advisor's own samples.
/// </summary>
public class HighWaterPinnedCheckTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    internal static DaemonCheckConfiguration DaemonConfiguration(
        IReadOnlyList<string>? shards = null,
        DaemonMode mode = DaemonMode.Solo,
        bool extended = false,
        TimeSpan? skipCap = null,
        bool transactionEvidence = true) => new(
        "studio_events",
        TenantPartitionedEvents: false,
        ExtendedProgressionTracking: extended,
        AsyncMode: mode,
        SlowPollingTime: TimeSpan.FromSeconds(1),
        StaleSequenceThreshold: TimeSpan.FromSeconds(3),
        StopAndDrainTimeout: TimeSpan.FromSeconds(5),
        DaemonLockId: 4444,
        SkipStaleGapsCap: skipCap,
        UsesTransactionEvidence: transactionEvidence,
        HasAsyncWork: true,
        AsyncShards: shards ?? ["DailySales:All", "ShipmentTracker:All"],
        LockKeys: [4444, 123_456, 789_012]);

    /// <summary>
    /// A reading of a mark pinned at 100 with 101 handed out and not committed, Marten's stuck row written
    /// <paramref name="pinnedFor" /> ago, and event 102 committed and waiting above it unless <paramref name="nextCommitted" /> says otherwise.
    /// </summary>
    internal static HighWaterReading Pinned(TimeSpan pinnedFor, long? nextCommitted = 102) => new(
        Now,
        HighestSequence: 110,
        Mark: 100,
        MarkUpdated: Now - pinnedFor,
        StuckMark: 100,
        StuckSince: Now - pinnedFor,
        FenceMark: 100,
        FenceUpdated: Now - pinnedFor - TimeSpan.FromSeconds(1),
        LatestWrite: Now - TimeSpan.FromSeconds(1),
        NextCommitted: nextCommitted);

    /// <summary>A sample <paramref name="ago" /> before now in which D01 saw a committed event waiting behind <paramref name="mark" /> (or none: -1).</summary>
    internal static HealthSample Waiting(TimeSpan ago, double mark = 100) =>
        new(Now - ago, new Dictionary<string, double> { [DaemonSeries.WaitingMark] = mark, [DaemonSeries.HighWaterMark] = 100 });

    private static HealthVerdict Evaluate(HighWaterReading reading, DaemonCheckConfiguration? configuration = null, params HealthSample[] history) =>
        new HighWaterPinnedCheck().Evaluate(
            new HighWaterPinnedFacts(configuration ?? DaemonConfiguration(), reading),
            HealthEvaluation.At(Now, history: new HealthHistoryView([.. history, Waiting(TimeSpan.Zero)])));

    [Fact]
    public void A_committed_event_waiting_behind_the_gap_for_more_than_a_minute_is_a_warning_and_names_the_gap()
    {
        HealthFinding finding = Evaluate(Pinned(TimeSpan.FromMinutes(2)), null, Waiting(TimeSpan.FromMinutes(2)))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D01");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("High-water mark", "100"));
        finding.Evidence.Should().Contain(x => x.Label == "Not committed" && x.Value.StartsWith("sequence number 101;", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(x => x.Label == "Waiting for" && x.Value.StartsWith("at least 2 min", StringComparison.Ordinal));
        finding.Remedy!.Code.Should().Contain("SkipStaleGapsDespiteLiveTransactionsAfter");
        finding.Link!.Route.Should().Be("projections", "Explain is on the projections screen's D01 banner");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ten_minutes_of_waiting_is_critical()
    {
        Evaluate(Pinned(TimeSpan.FromMinutes(11)), null, Waiting(TimeSpan.FromMinutes(11)))
            .Findings.Single().Severity.Should().Be(HealthSeverity.Critical);
    }

    /// <summary>
    /// The H2 review's blocker: severity is the wait the advisor saw, not Marten's stuck-row time - which can predate
    /// anything waiting by hours (a bulk import pinned the mark long before an event committed above it).
    /// </summary>
    [Fact]
    public void An_old_pin_with_a_freshly_waiting_event_is_judged_by_the_wait_not_by_the_pin()
    {
        HealthVerdict verdict = Evaluate(Pinned(TimeSpan.FromHours(3)), null, Waiting(TimeSpan.FromMinutes(2)));

        verdict.Findings.Single().Severity.Should().Be(HealthSeverity.Warning, "the event has waited two minutes, whatever the row says");
    }

    /// <summary>Marten holds every gap for StaleSequenceThreshold before it asks; a fresh wait is the daemon's ordinary work.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    public void A_wait_under_a_minute_is_quiet(int seconds)
    {
        HealthVerdict verdict = Evaluate(Pinned(TimeSpan.FromHours(1)), null, Waiting(TimeSpan.FromSeconds(seconds)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("within the 1 min the daemon is given");
    }

    /// <summary>The first sweep that sees the wait has nothing to measure it from, and says so.</summary>
    [Fact]
    public void A_wait_seen_for_the_first_time_is_only_watched()
    {
        HealthVerdict verdict = Evaluate(Pinned(TimeSpan.FromHours(1)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("only just seen it");
    }

    /// <summary>A sample of another mark, of no wait, or without D01 ends the run: the wait is measured since then.</summary>
    [Theory]
    [InlineData(95)]
    [InlineData(-1)]
    public void A_run_broken_by_another_mark_or_by_no_wait_starts_again(double between)
    {
        HealthVerdict verdict = Evaluate(
            Pinned(TimeSpan.FromHours(1)),
            null,
            Waiting(TimeSpan.FromMinutes(20)),
            Waiting(TimeSpan.FromMinutes(5), between),
            Waiting(TimeSpan.FromMinutes(2)));

        verdict.Findings.Single().Severity.Should().Be(HealthSeverity.Warning, "the run since the break is two minutes, not twenty");
    }

    [Fact]
    public void A_sample_in_which_D01_did_not_read_ends_the_run()
    {
        HealthSample withoutD01 = new(Now - TimeSpan.FromMinutes(5), new Dictionary<string, double> { [DaemonSeries.ShardPosition("DailySales:All")] = 1 });

        Evaluate(Pinned(TimeSpan.FromHours(1)), null, Waiting(TimeSpan.FromMinutes(20)), withoutD01, Waiting(TimeSpan.FromMinutes(2)))
            .Findings.Single().Severity.Should().Be(HealthSeverity.Warning);
    }

    /// <summary>
    /// The stuck-gap row is never deleted (Marten V9.39.0, V9.50.2): one whose mark is behind the mark's own row is an
    /// old pin, however old its time.
    /// </summary>
    [Fact]
    public void An_old_stuck_row_left_behind_by_a_mark_that_moved_is_quiet()
    {
        HighWaterReading moved = Pinned(TimeSpan.FromHours(3)) with { Mark = 105, NextCommitted = 106 };

        HealthVerdict verdict = Evaluate(moved, null, Waiting(TimeSpan.FromHours(1), 105));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("has not recorded the mark as pinned at its current value");
    }

    /// <summary>The next event is committed: nothing holds the mark, so if it is not moving, that is D02's or D03's.</summary>
    [Fact]
    public void A_committed_event_just_above_the_mark_is_not_a_gap()
    {
        HealthVerdict verdict = Evaluate(Pinned(TimeSpan.FromMinutes(30), nextCommitted: 101), null, Waiting(TimeSpan.FromMinutes(30)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("D02").And.Contain("D03");
    }

    /// <summary>
    /// The H2 review's blocker, flipped: numbers handed out and not committed with nothing committed above them hold
    /// nothing back - a long append on an idle store, a long transaction beside a rolled-back append - and Marten
    /// records that pin all the same.
    /// </summary>
    [Fact]
    public void Nothing_committed_above_the_mark_holds_nothing_back_and_is_quiet()
    {
        HealthVerdict verdict = Evaluate(Pinned(TimeSpan.FromHours(2), nextCommitted: null));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("sequence numbers 101 to 110 were handed out and not committed")
            .And.Contain("no committed event waits above them, so nothing is held up");
    }

    /// <summary>
    /// <c>UseTransactionEvidenceForGapSkipping = false</c> never skips a tail gap under 32 (#3865): the pin is permanent,
    /// and still holds nothing back - quiet, and said so.
    /// </summary>
    [Fact]
    public void A_legacy_tail_gap_that_is_never_skipped_is_quiet_and_named()
    {
        HealthVerdict verdict = Evaluate(Pinned(TimeSpan.FromDays(3), nextCommitted: null), DaemonConfiguration(transactionEvidence: false));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("UseTransactionEvidenceForGapSkipping = false, a tail gap Marten never skips");
    }

    /// <summary>The legacy mode skips a gap that holds a committed event by age; one still held is reported, and called unusual.</summary>
    [Fact]
    public void A_legacy_store_with_a_committed_event_held_is_still_reported()
    {
        HealthFinding finding = Evaluate(Pinned(TimeSpan.FromMinutes(5)), DaemonConfiguration(transactionEvidence: false), Waiting(TimeSpan.FromMinutes(5)))
            .Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Projections.UseTransactionEvidenceForGapSkipping" && x.Value.Contains("unusual", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mark_at_the_ceiling_is_quiet()
    {
        Evaluate(Pinned(TimeSpan.FromHours(1)) with { HighestSequence = 100 }, null, Waiting(TimeSpan.FromHours(1))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_database_whose_daemon_never_wrote_a_mark_is_quiet()
    {
        HealthVerdict verdict = Evaluate(Pinned(TimeSpan.FromHours(1)) with { Mark = null });

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("has not written a high-water mark");
    }

    [Fact]
    public void The_cap_when_set_is_named_in_the_evidence()
    {
        HealthFinding finding = Evaluate(Pinned(TimeSpan.FromMinutes(5)), DaemonConfiguration(skipCap: TimeSpan.FromMinutes(30)), Waiting(TimeSpan.FromMinutes(5)))
            .Findings.Single();

        finding.Evidence.Should().Contain(new HealthEvidence("Projections.SkipStaleGapsDespiteLiveTransactionsAfter", "30 min"));
    }

    [Fact]
    public void It_samples_the_sequence_the_mark_and_whether_anything_waits_behind_it()
    {
        new HighWaterPinnedCheck().Sample(new HighWaterPinnedFacts(DaemonConfiguration(), Pinned(TimeSpan.Zero))).Should().BeEquivalentTo(new[]
        {
            new HealthSamplePart(DaemonSeries.HighestSequence, 110),
            new HealthSamplePart(DaemonSeries.HighWaterMark, 100),
            new HealthSamplePart(DaemonSeries.WaitingMark, 100),
        });

        new HighWaterPinnedCheck().Sample(new HighWaterPinnedFacts(DaemonConfiguration(), Pinned(TimeSpan.Zero, nextCommitted: null)))
            .Should().Contain(new HealthSamplePart(DaemonSeries.WaitingMark, DaemonSeries.NotWaiting), "written every time, so no value means not read");
    }
}
