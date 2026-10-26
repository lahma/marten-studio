using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

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

    // ---- per day, over the statistics window (W5-CAP, issue #8 G5) ------------------------------------------------------

    private const long GiB = 1L << 30;

    /// <summary>C08 judged with a statistics window of <paramref name="window" /> ending at the last sample.</summary>
    private static HealthVerdict EvaluatePerDay(TimeSpan window, bool looksReset, params (int Minute, Counters Counters)[] samples)
    {
        HealthHistoryView history = new([.. samples.Select(static x => Sample(x.Minute, x.Counters))]);
        (int minute, Counters last) = samples[^1];
        DateTimeOffset at = Start.AddMinutes(minute);

        DatabaseCounterFacts facts = new(at, last.Commits, last.Rollbacks, last.Deadlocks, last.TempFiles, last.TempBytes, last.StatsReset ?? Reset, last.Studio);
        StatisticsWindow statistics = new()
        {
            MeasuredAt = at,
            Since = at - window,
            Basis = StatisticsBasis.ServerReset,
            LooksReset = looksReset,
        };

        return new DatabaseCounterCheck().Evaluate(facts, HealthEvaluation.At(at, history: history) with { Statistics = statistics });
    }

    [Fact]
    public void A_gibibyte_of_temp_files_a_day_over_the_window_is_a_warning_on_one_sample()
    {
        // Two days of counters, 2 GiB written: one a day.
        HealthFinding finding = EvaluatePerDay(TimeSpan.FromDays(2), looksReset: false, (0, new Counters(100, 0, TempFiles: 40, TempBytes: 2 * GiB)))
            .Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().Be("temp-files", "the variant name is kept exactly");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("Statements write 1.0 GiB of temporary files a day since 2026-10-13 09:00 UTC");
        finding.Evidence.Should().Contain(new HealthEvidence("Written a day", "1.0 GiB"));
        finding.Evidence.Should().Contain(new HealthEvidence("Temporary files a day", "20"));
        finding.Evidence.Select(static x => x.Label).Should().Contain(["Window", "Counting since"]);
    }

    [Fact]
    public void Just_under_a_gibibyte_a_day_leaves_the_hours_info_as_it_was()
    {
        HealthVerdict verdict = EvaluatePerDay(
            TimeSpan.FromDays(2),
            looksReset: false,
            (0, new Counters(100, 0, TempFiles: 2, TempBytes: 2 * GiB - 1_000)),
            (5, new Counters(120, 0, TempFiles: 3, TempBytes: 2 * GiB - 2)));

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Subject.Should().Be("temp-files");
        finding.Severity.Should().Be(HealthSeverity.Info, "under 1 GiB a day the hour's rule stands");
        finding.Evidence.Select(static x => x.Label).Should().Contain("Written a day since 2026-10-13 09:05 UTC");
    }

    [Fact]
    public void Under_a_day_of_statistics_nothing_is_judged_per_day()
    {
        HealthVerdict verdict = EvaluatePerDay(TimeSpan.FromHours(23), looksReset: false, (0, new Counters(100, 0, TempFiles: 900, TempBytes: 500 * GiB)));

        verdict.Findings.Should().BeEmpty("a day's rate needs a day, and one sample is no hour");
        verdict.Note.Should().Contain("Watching: 23 h of statistics so far")
            .And.Contain("the rate per day of deadlocks and temporary files is judged once they have counted for a day");
    }

    [Fact]
    public void While_the_statistics_look_reset_nothing_is_judged_per_day()
    {
        EvaluatePerDay(TimeSpan.FromDays(30), looksReset: true, (0, new Counters(100, 0, TempFiles: 900, TempBytes: 500 * GiB)))
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_deadlock_in_the_hour_adds_the_windows_deadlocks_per_day_and_stays_a_warning()
    {
        HealthFinding finding = EvaluatePerDay(
                TimeSpan.FromDays(4),
                looksReset: false,
                (0, new Counters(1_000, 10, Deadlocks: 6)),
                (5, new Counters(1_200, 12, Deadlocks: 7)))
            .Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().Be("deadlocks");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Deadlocks per day since 2026-10-11 09:05 UTC", "1.75"));
    }

    [Fact]
    public void The_per_day_warning_keeps_the_findings_in_their_order()
    {
        HealthVerdict verdict = EvaluatePerDay(
            TimeSpan.FromDays(1),
            looksReset: false,
            (0, new Counters(10_000, 1_000, Deadlocks: 0, TempFiles: 0)),
            (5, new Counters(12_000, 1_400, Deadlocks: 2, TempFiles: 1, TempBytes: 2 * GiB)));

        verdict.Findings.Select(static x => x.Key).Should().Equal("C08|deadlocks", "C08|rollbacks", "C08|temp-files");
        verdict.Findings.Single(static x => x.Subject == "temp-files").Severity.Should().Be(HealthSeverity.Warning);
    }

    [Fact]
    public void A_quiet_hour_over_a_day_long_window_says_the_rates_per_day()
    {
        HealthVerdict verdict = EvaluatePerDay(TimeSpan.FromDays(2), looksReset: false, (0, new Counters(100, 0, Deadlocks: 3)), (5, new Counters(150, 0, Deadlocks: 3)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Per day since 2026-10-13 09:05 UTC: 1.5 deadlocks and 0 bytes of temporary files.");
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
