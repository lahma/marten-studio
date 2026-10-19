using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>C08's judgement over a stated history, and no Postgres.</summary>
public class DatabaseCounterCheckTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>One sample's counters; <paramref name="Studio" /> is the studio's own rollbacks so far, cumulative.</summary>
    private sealed record Counters(long Commits, long Rollbacks, long Deadlocks = 0, long TempFiles = 0, long TempBytes = 0, long Studio = 0, DateTimeOffset? StatsReset = null);

    private static HealthSample Sample(int minute, Counters counters)
    {
        DatabaseCounterFacts facts = new(
            Start.AddMinutes(minute),
            counters.Commits,
            counters.Rollbacks,
            counters.Deadlocks,
            counters.TempFiles,
            counters.TempBytes,
            counters.StatsReset ?? Reset,
            counters.Studio);

        return new HealthSample(
            facts.ReadAt,
            new DatabaseCounterCheck().Sample(facts).ToDictionary(static x => x.Series, static x => x.Value, StringComparer.Ordinal));
    }

    private static HealthVerdict Evaluate(params (int Minute, Counters Counters)[] samples)
    {
        HealthHistoryView history = new([.. samples.Select(static x => Sample(x.Minute, x.Counters))]);
        (int minute, Counters last) = samples[^1];

        DatabaseCounterFacts facts = new(
            Start.AddMinutes(minute), last.Commits, last.Rollbacks, last.Deadlocks, last.TempFiles, last.TempBytes, last.StatsReset ?? Reset, last.Studio);

        return new DatabaseCounterCheck().Evaluate(facts, HealthEvaluation.At(facts.ReadAt, history: history));
    }

    [Fact]
    public void One_sample_is_watching_not_judging()
    {
        HealthVerdict verdict = Evaluate((0, new Counters(100, 100, Deadlocks: 5)));

        verdict.Findings.Should().BeEmpty("a counter since the last reset is not a rate");
        verdict.Note.Should().Contain("Watching").And.Contain("2026-10-01 00:00:00Z");
    }

    [Fact]
    public void A_deadlock_between_two_samples_is_a_warning()
    {
        HealthVerdict verdict = Evaluate((0, new Counters(1_000, 10, Deadlocks: 4)), (5, new Counters(1_200, 12, Deadlocks: 5)));

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Subject.Should().Be("deadlocks");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("1 deadlock in the last 5 min");
        finding.Caveat.Should().Contain("2026-10-01 00:00:00Z");
    }

    [Fact]
    public void A_high_rollback_share_is_a_warning_once_the_studios_own_are_taken_out()
    {
        // 2 000 commits and 400 rollbacks in five minutes, of which the studio's own sessions sent 60.
        HealthVerdict verdict = Evaluate((0, new Counters(10_000, 1_000, Studio: 500)), (5, new Counters(12_000, 1_400, Studio: 560)));

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Subject.Should().Be("rollbacks");
        finding.Evidence.Should().Contain(new HealthEvidence("Rolled back", "340 of 2,340"));
        finding.Evidence.Should().Contain(new HealthEvidence("The studio's own, subtracted", "60"));
        finding.Title.Should().StartWith("15% of this database's transactions rolled back");
    }

    [Fact]
    public void An_idle_store_watched_by_the_studio_is_quiet_however_its_rollbacks_look()
    {
        // Nothing but the studio's own reads, two thousand of them over the window: every one of them rolls back.
        HealthVerdict verdict = Evaluate(
            (0, new Counters(50, 2_000, Studio: 1_900)),
            (1, new Counters(52, 3_000, Studio: 2_900)),
            (2, new Counters(54, 4_000, Studio: 3_900)));

        verdict.Findings.Should().BeEmpty("the studio's own rollbacks are subtracted exactly, leaving nothing to judge");
        verdict.Note.Should().Contain("0 of 4 transactions rolled back");
    }

    [Fact]
    public void A_share_is_judged_only_over_enough_transactions()
    {
        Evaluate((0, new Counters(0, 0)), (5, new Counters(500, 400))).Findings.Should().BeEmpty(
            "nine hundred transactions are below the minimum the share is judged on");
    }

    [Fact]
    public void Temp_files_are_info()
    {
        HealthFinding finding = Evaluate((0, new Counters(100, 0, TempFiles: 2, TempBytes: 10)), (5, new Counters(120, 0, TempFiles: 5, TempBytes: 3 * 1024 * 1024 + 10)))
            .Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().Be("temp-files");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(new HealthEvidence("Written", "3.0 MiB"));
    }

    [Fact]
    public void Three_conditions_are_three_findings_with_distinct_keys()
    {
        HealthVerdict verdict = Evaluate(
            (0, new Counters(10_000, 1_000, Deadlocks: 0, TempFiles: 0)),
            (5, new Counters(12_000, 1_400, Deadlocks: 2, TempFiles: 1, TempBytes: 2048)));

        verdict.Findings.Select(static x => x.Key).Should().Equal("C08|deadlocks", "C08|rollbacks", "C08|temp-files");
    }

    [Fact]
    public void A_reset_starts_the_run_again()
    {
        DateTimeOffset later = Reset.AddDays(10);

        HealthVerdict verdict = Evaluate(
            (0, new Counters(10_000, 10, Deadlocks: 1)),
            (5, new Counters(5, 0, Deadlocks: 0, StatsReset: later)));

        verdict.Findings.Should().BeEmpty("after pg_stat_reset() there is one sample of the new run, and nothing to compare it with");
        verdict.Note.Should().Contain("Watching");
    }

    [Fact]
    public void A_counter_that_went_down_without_a_new_reset_time_starts_the_run_again()
    {
        DatabaseCounterCheck.Run(new HealthHistoryView(
        [
            Sample(0, new Counters(10_000, 10)),
            Sample(5, new Counters(9, 1)),
            Sample(10, new Counters(20, 2)),
        ])).Should().HaveCount(2);
    }

    [Fact]
    public void A_studio_count_the_server_has_not_caught_up_with_never_makes_the_rollbacks_negative()
    {
        // The studio counted 300 rollbacks the server has so far reported only 200 of.
        HealthVerdict verdict = Evaluate((0, new Counters(10_000, 100, Studio: 0)), (5, new Counters(11_200, 300, Studio: 300)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("0 of 1,200 transactions rolled back");
    }
}
