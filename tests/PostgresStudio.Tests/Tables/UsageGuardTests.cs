using static PostgresStudio.Tests.Tables.LintBuilders;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// Issue #8 G4's usage guards on T01, T05 and T06: no verdict over statistics under seven days old or looking reset - a crash,
/// a restore, a failover - and the window narrowed, where a database's own reset time is null, to what its statistics prove.
/// </summary>
public class UsageGuardTests
{
    private static StudioTable Orders { get; } = Host("orders");

    private static UsageWindowReading Reading(DateTimeOffset? databaseReset, DateTimeOffset? oldestRecorded, DateTimeOffset sharedReset) =>
        new(new StatisticsReading
        {
            ReadAt = Now,
            ServerVersion = 170_000,
            DatabaseReset = databaseReset,
            SharedReset = sharedReset,
            ArchiverReset = sharedReset,
            ServerStartedAt = sharedReset.AddDays(-1),
        }, oldestRecorded);

    [Fact]
    public void Six_days_withholds_and_seven_days_covers()
    {
        UsageGuard.For(null, Evaluation(Window(TimeSpan.FromDays(6))))!.Withheld(UsageGuard.MinimumWindow)
            .Should().StartWith("Withheld: statistics 6 days old (since 2026-10-20 09:00 UTC, pg_stat_checkpointer.stats_reset)");
        UsageGuard.For(null, Evaluation(Window(TimeSpan.FromDays(7))))!.Withheld(UsageGuard.MinimumWindow).Should().BeNull();
    }

    [Theory]
    [InlineData((int) StatisticsResetReason.Crash, "the server restarted at 2026-10-26 08:00 UTC without its statistics")]
    [InlineData((int) StatisticsResetReason.Restore, "the timeline went from 1 to 2 and the server started after the last check")]
    [InlineData((int) StatisticsResetReason.Failover, "the timeline went from 2 to 3 on a server that was already running at the last check")]
    [InlineData((int) StatisticsResetReason.FreshCluster, "the cluster's system identifier changed")]
    public void Counters_that_look_reset_withhold_whatever_the_windows_length(int reason, string signal)
    {
        string? withheld = UsageGuard.For(null, Evaluation(Window(TimeSpan.FromDays(400), looksReset: true, (StatisticsResetReason) reason, signal)))!
            .Withheld(UsageGuard.MinimumWindow);

        withheld.Should().Be("Withheld: the counters look reset (" + signal + "), so usage is judged again once they have counted for 7 days (issue #8 G4).");
    }

    [Fact]
    public void A_database_whose_own_reset_time_is_null_is_judged_from_the_oldest_moment_its_statistics_record()
    {
        // The server's statistics began 40 days ago; the database's own counters provably only 3 (CAP's documented limit).
        UsageWindowReading young = Reading(null, Now.AddDays(-3), Now.AddDays(-40));
        UsageWindow narrowed = UsageGuard.Judge(young, Evaluation());

        narrowed.NarrowedByRecords.Should().BeTrue();
        narrowed.Since.Should().Be(Now.AddDays(-3));
        narrowed.Withheld(UsageGuard.MinimumWindow).Should().StartWith("Withheld: this database's counters are proven only 3 days old")
            .And.Contain("From PostgreSQL 15 a database's counters begin when it is first used");

        UsageGuard.Judge(Reading(null, Now.AddDays(-10), Now.AddDays(-40)), Evaluation()).Withheld(UsageGuard.MinimumWindow)
            .Should().BeNull("ten days of the database's own records prove seven");
        UsageGuard.Judge(Reading(null, null, Now.AddDays(-40)), Evaluation()).Withheld(UsageGuard.MinimumWindow)
            .Should().Contain("its statistics record no moment yet");

        UsageWindow own = UsageGuard.Judge(Reading(Now.AddDays(-20), Now.AddHours(-1), Now.AddDays(-40)), Evaluation());
        own.NarrowedByRecords.Should().BeFalse("a database's own reset time says when its counters began");
        own.Since.Should().Be(Now.AddDays(-20));
        own.Withheld(UsageGuard.MinimumWindow).Should().BeNull();
    }

    [Fact]
    public void A_window_nobody_measured_withholds()
    {
        UsageGuard.For(null, Evaluation()).Should().BeNull();
    }

    // ---- T01 and T06 ---------------------------------------------------------------------------------------------------

    private static TableFacts LargeTable => Table(Orders, 64 * 1024 * 1024, static row => row with { LiveRows = 500_000 });

    private static HealthHistoryView ScanRates => History(
        (TimeSpan.FromMinutes(20), [(SequentialScansCheck.ScansSeries + "app.orders", 0), (SequentialScansCheck.RowsReadSeries + "app.orders", 0)]),
        (TimeSpan.Zero, [(SequentialScansCheck.ScansSeries + "app.orders", 10), (SequentialScansCheck.RowsReadSeries + "app.orders", 5_000_000)]));

    [Fact]
    public void T01_is_withheld_over_six_days_and_judged_over_seven()
    {
        SequentialScanFacts facts = new(Now, [LargeTable], DatabaseStatistics.Unknown);

        HealthVerdict young = new SequentialScansCheck().Evaluate(facts, Evaluation(Window(TimeSpan.FromDays(6)), ScanRates));
        young.Findings.Should().BeEmpty();
        young.Note.Should().StartWith("Withheld: statistics 6 days old");

        new SequentialScansCheck().Evaluate(facts, Evaluation(Window(TimeSpan.FromDays(7)), ScanRates))
            .Findings.Should().ContainSingle().Which.Code.Should().Be("T01");

        new SequentialScansCheck().Evaluate(facts, Evaluation(Window(TimeSpan.FromDays(30), looksReset: true), ScanRates))
            .Note.Should().StartWith("Withheld: the counters look reset");
    }

    [Fact]
    public void T06_is_withheld_over_a_short_window_on_a_document_table()
    {
        StudioTable documents = new("app", "mt_doc_order", StudioTableKind.DocumentTable, StudioTableOwnership.Owned) { TypeName = "Order" };
        HotUpdateFacts facts = new([Table(documents)], [], DatabaseStatistics.Unknown);
        HealthHistoryView updates = History(
            (TimeSpan.FromMinutes(20), [(HotUpdateCheck.UpdatesSeries + documents.Key, 0), (HotUpdateCheck.HotUpdatesSeries + documents.Key, 0)]),
            (TimeSpan.Zero, [(HotUpdateCheck.UpdatesSeries + documents.Key, 20_000), (HotUpdateCheck.HotUpdatesSeries + documents.Key, 0)]));

        HealthVerdict young = new HotUpdateCheck().Evaluate(facts, Evaluation(Window(TimeSpan.FromDays(2)), updates));
        young.Findings.Should().BeEmpty();
        young.Note.Should().StartWith("Withheld: statistics 2 days old");

        new HotUpdateCheck().Evaluate(facts, Evaluation(Window(TimeSpan.FromDays(8)), updates)).Findings.Should().ContainSingle();
    }

    // ---- T05: last_idx_scan on 16, as today on 15 -----------------------------------------------------------------------

    private static UnusedIndexFacts Unused(HealthIndexRow row) =>
        new(Now, [Index(Orders, row)], [Table(Orders, shape: static r => r with { Inserts = 500_000 })], DatabaseStatistics.Unknown);

    private static HealthIndexRow Scanned(int serverVersion, DateTimeOffset? lastScan) =>
        IndexRow(Orders, "by_status", 2) with { Scans = 42, Bytes = 4 * 1024 * 1024, ServerVersion = serverVersion, LastScan = lastScan };

    [Fact]
    public void On_16_an_index_last_scanned_over_thirty_days_ago_is_unused_with_the_date_in_the_evidence()
    {
        HealthFinding finding = new UnusedIndexCheck().Evaluate(Unused(Scanned(160_004, Now.AddDays(-31))), Covered()).Findings.Single();

        finding.Title.Should().Contain("has not been scanned for 31 days");
        finding.Evidence.Should().Contain(new HealthEvidence("Last scan", "2026-09-25 09:00 UTC"));
        finding.Evidence.Should().Contain(new HealthEvidence("Index scans", "42"));
        finding.WhyItMatters.Should().Contain("No query has used it since 2026-09-25 09:00 UTC.");

        new UnusedIndexCheck().Evaluate(Unused(Scanned(160_004, Now.AddDays(-29))), Covered()).Findings.Should().BeEmpty("29 days is inside the line");
    }

    [Fact]
    public void On_15_last_idx_scan_is_never_read_and_only_a_never_scanned_index_is_unused()
    {
        new UnusedIndexCheck().Evaluate(Unused(Scanned(150_008, Now.AddDays(-90))), Covered()).Findings
            .Should().BeEmpty("before 16 there is no last_idx_scan, and 42 scans is used");

        HealthFinding never = new UnusedIndexCheck().Evaluate(Unused(Scanned(150_008, null) with { Scans = 0 }), Covered()).Findings.Single();
        never.Title.Should().Contain("has never been scanned");
        never.Evidence.Select(static x => x.Label).Should().NotContain("Last scan", "15 has no such column to quote");
    }

    [Fact]
    public void On_16_a_never_scanned_index_says_never_and_the_guard_still_withholds_a_young_window()
    {
        HealthFinding never = new UnusedIndexCheck().Evaluate(Unused(Scanned(160_004, null) with { Scans = 0 }), Covered()).Findings.Single();
        never.Evidence.Should().Contain(new HealthEvidence("Last scan", "never, since the counters began"));

        new UnusedIndexCheck().Evaluate(Unused(Scanned(160_004, Now.AddDays(-31))), Evaluation(Window(TimeSpan.FromDays(3)))).Findings
            .Should().BeEmpty("even a dated last scan waits for the window");
    }

    [Fact]
    public void On_19_an_index_whose_own_counters_were_reset_inside_the_window_is_counted_not_judged()
    {
        HealthVerdict verdict = new UnusedIndexCheck().Evaluate(
            Unused(Scanned(190_000, null) with { Scans = 0, CountersReset = Now.AddDays(-2) }),
            Covered());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("1 index's own counters were reset less than 7 days ago and was not judged.");
    }

    // ---- 1.1-PG19: a table's and an index's own reset, judged over their own window and said so --------------------------------

    [Fact]
    public void On_19_an_unused_index_reset_ten_days_ago_is_judged_over_its_own_ten_days_and_says_since_when()
    {
        HealthFinding finding = new UnusedIndexCheck()
            .Evaluate(Unused(Scanned(190_000, null) with { Scans = 0, CountersReset = Now.AddDays(-10) }), Covered())
            .Findings.Single();

        finding.Evidence.Should().Contain(new HealthEvidence("Counters since", "since 2026-10-16 09:00 UTC (this index's own reset, PostgreSQL 19)"));
        finding.Caveat.Should().Contain("Judged over 10 days of statistics, since 2026-10-16 09:00 UTC (this index's own reset, PostgreSQL 19)");
        finding.WhyItMatters.Should().Contain("No query has used it in 10 days of statistics.");
    }

    [Fact]
    public void On_19_an_index_reset_before_the_window_began_and_every_index_below_19_keep_the_windows_own_sentence()
    {
        foreach (HealthIndexRow row in new[]
                 {
                     Scanned(190_000, null) with { Scans = 0, CountersReset = Now.AddDays(-40) },
                     Scanned(180_006, null) with { Scans = 0 },
                 })
        {
            HealthFinding finding = new UnusedIndexCheck().Evaluate(Unused(row), Covered()).Findings.Single();

            finding.Evidence.Single(static x => x.Label == "Counters since").Value.Should().NotContain("own reset");
            finding.Evidence.Select(static x => x.Label).Should().NotContain("The table's counters since");
            finding.Caveat.Should().Contain("Judged over 30 days of statistics");
        }
    }

    [Fact]
    public void On_19_a_table_reset_on_its_own_says_its_writes_are_counted_from_then()
    {
        UnusedIndexFacts facts = new(
            Now,
            [Index(Orders, Scanned(190_000, null) with { Scans = 0 })],
            [Table(Orders, shape: static r => r with { Inserts = 500_000, CountersReset = Now.AddDays(-12) })],
            DatabaseStatistics.Unknown);

        new UnusedIndexCheck().Evaluate(facts, Covered()).Findings.Single().Evidence
            .Should().Contain(new HealthEvidence("The table's counters since", "2026-10-14 09:00 UTC (its own reset, PostgreSQL 19): its writes are counted from then"));
    }

    private static TableFacts ResetOn(TableFacts table, DateTimeOffset? reset) => table with { Row = table.Row with { CountersReset = reset } };

    [Fact]
    public void On_19_a_table_reset_on_its_own_two_days_ago_is_counted_not_judged_by_T01()
    {
        HealthVerdict verdict = new SequentialScansCheck().Evaluate(
            new SequentialScanFacts(Now, [ResetOn(LargeTable, Now.AddDays(-2))], DatabaseStatistics.Unknown),
            Evaluation(Window(TimeSpan.FromDays(30)), ScanRates));

        verdict.Findings.Should().BeEmpty("a table reset two days ago has not counted for the seven the guard asks");
        verdict.Note.Should().Be("1 table's own counters were reset less than 7 days ago and it was not judged.");

        new SequentialScansCheck().Evaluate(
                new SequentialScanFacts(Now, [ResetOn(LargeTable, null)], DatabaseStatistics.Unknown),
                Evaluation(Window(TimeSpan.FromDays(30)), ScanRates))
            .Findings.Should().ContainSingle("the same table, never reset on its own, is judged on the same samples");
    }

    [Fact]
    public void On_19_a_table_reset_ten_days_ago_is_judged_by_T01_and_the_finding_says_since_when()
    {
        HealthFinding finding = new SequentialScansCheck().Evaluate(
                new SequentialScanFacts(Now, [ResetOn(LargeTable, Now.AddDays(-10))], DatabaseStatistics.Unknown),
                Evaluation(Window(TimeSpan.FromDays(30)), ScanRates))
            .Findings.Single();

        finding.Evidence.Should().Contain(new HealthEvidence("Counters since", "2026-10-16 09:00 UTC (this table's own reset, PostgreSQL 19)"));
        finding.Caveat.Should().StartWith("Counters since 2026-10-16 09:00 UTC (this table's own reset, PostgreSQL 19); a usage verdict");
    }

    [Fact]
    public void A_span_of_samples_that_began_before_a_tables_own_reset_is_not_a_rate_T01_reads()
    {
        HealthHistoryView nineDays = History(
            (TimeSpan.FromDays(9), [(SequentialScansCheck.ScansSeries + "app.orders", 0), (SequentialScansCheck.RowsReadSeries + "app.orders", 0)]),
            (TimeSpan.Zero, [(SequentialScansCheck.ScansSeries + "app.orders", 10), (SequentialScansCheck.RowsReadSeries + "app.orders", 5_000_000)]));

        new SequentialScansCheck().Evaluate(
                new SequentialScanFacts(Now, [ResetOn(LargeTable, null)], DatabaseStatistics.Unknown),
                Evaluation(Window(TimeSpan.FromDays(30)), nineDays))
            .Findings.Should().ContainSingle("without a reset the nine days of samples are a rate");

        new SequentialScansCheck().Evaluate(
                new SequentialScanFacts(Now, [ResetOn(LargeTable, Now.AddDays(-8))], DatabaseStatistics.Unknown),
                Evaluation(Window(TimeSpan.FromDays(30)), nineDays))
            .Findings.Should().BeEmpty("the table was reset eight days ago, inside the nine days the samples span: a rate across it mixes two runs of the counter");
    }
}
