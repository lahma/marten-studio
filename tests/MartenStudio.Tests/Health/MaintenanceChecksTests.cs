using MartenStudio.Services.Configuration;

using static MartenStudio.Tests.Health.TableCheckBuilders;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>T06, T07 and T08 over stated facts: HOT updates, vacuum, statistics.</summary>
public class MaintenanceChecksTests
{
    // -----------------------------------------------------------------------------------------------
    // T06 Low HOT-update ratio
    // -----------------------------------------------------------------------------------------------

    private static HealthHistoryView Updates(string key, (double Updates, double Hot) first, (double Updates, double Hot) last) =>
        History(
            (TimeSpan.FromMinutes(20), [(HotUpdateCheck.UpdatesSeries + key, first.Updates), (HotUpdateCheck.HotUpdatesSeries + key, first.Hot)]),
            (TimeSpan.Zero, [(HotUpdateCheck.UpdatesSeries + key, last.Updates), (HotUpdateCheck.HotUpdatesSeries + key, last.Hot)]));

    private static HotUpdateFacts Hot(TableFacts table, params IndexFacts[] indexes) =>
        new([table], indexes, new DatabaseStatistics(Now.AddDays(-30), true, 2));

    [Fact]
    public void Few_HOT_updates_on_a_table_no_index_blocks_is_a_FillFactor_warning_that_needs_Marten_9_47()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthVerdict verdict = new HotUpdateCheck().Evaluate(
            Hot(Table(orders), Index(orders, IndexRow(orders, "pk", "id") with { IsPrimaryKey = true, IsUnique = true }, IndexOrigin.PrimaryKey)),
            Evaluation(Updates(orders.Key, (0, 0), (20_000, 2_000))));

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("T06");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Contain("10%");
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().FillFactor(80);");
        finding.Remedy.Requires.Should().BeSameAs(RuntimeFeature.DocumentFillFactor);
        RuntimeFeature.DocumentFillFactor.Requirement.Should().Be("Marten 9.47+", "the page prints this beside the line");
    }

    [Fact]
    public void An_index_that_reads_data_makes_fillfactor_useless_and_the_finding_says_so_instead()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts computed = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)") with { ReferencedColumns = ["data"] });

        HealthFinding finding = new HotUpdateCheck().Evaluate(Hot(Table(orders), computed), Evaluation(Updates(orders.Key, (0, 0), (20_000, 0))))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().Contain("fillfactor cannot help");
        finding.WhyItMatters.Should().Contain("by_status (reads data)");
        finding.Remedy!.Code.Should().Contain(".Duplicate(x => x.IndexedMember)").And.NotContain("FillFactor");
        finding.Remedy.Requires.Should().BeNull("Duplicate exists at the floor");
    }

    [Theory]
    [InlineData("mt_last_modified")]
    [InlineData("mt_version")]
    public void An_index_on_a_metadata_column_every_upsert_stamps_blocks_HOT_too(string column)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_" + column, column), IndexOrigin.Declared);

        HealthFinding finding = new HotUpdateCheck().Evaluate(Hot(Table(orders), index), Evaluation(Updates(orders.Key, (0, 0), (20_000, 0))))
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Contain("fillfactor cannot help");
        finding.Remedy.Should().BeNull("no duplicated field replaces an index on Marten's own metadata column");
    }

    [Theory]
    [InlineData(160_000)]
    [InlineData(170_004)]
    public void A_BRIN_index_on_a_changing_column_does_not_block_HOT_from_PostgreSQL_16(int serverVersion)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts brin = Index(orders, IndexRow(orders, "brin_modified", "mt_last_modified") with
        {
            AccessMethod = "brin",
            ServerVersion = serverVersion,
        });

        HealthFinding finding = new HotUpdateCheck().Evaluate(Hot(Table(orders), brin), Evaluation(Updates(orders.Key, (0, 0), (20_000, 0))))
            .Findings.Should().ContainSingle().Subject;

        finding.Remedy!.Code.Should().Contain("FillFactor", "a summarizing index no longer blocks HOT (PostgreSQL 16)");
    }

    [Theory]
    [InlineData(140_011)]
    [InlineData(150_008)]
    public void A_BRIN_index_on_a_changing_column_blocks_HOT_before_PostgreSQL_16(int serverVersion)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts brin = Index(orders, IndexRow(orders, "brin_modified", "mt_last_modified") with
        {
            AccessMethod = "brin",
            ServerVersion = serverVersion,
        });

        HealthFinding finding = new HotUpdateCheck().Evaluate(Hot(Table(orders), brin), Evaluation(Updates(orders.Key, (0, 0), (20_000, 0))))
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Contain("fillfactor cannot help", "before 16 every index on a changed column blocks HOT, BRIN included");
        finding.WhyItMatters.Should().Contain("brin_modified");
    }

    [Fact]
    public void Fewer_updates_than_the_minimum_or_a_good_ratio_says_nothing_and_one_sample_is_watching()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        HotUpdateCheck check = new();

        check.Evaluate(Hot(Table(orders)), Evaluation(Updates(orders.Key, (0, 0), (HotUpdateCheck.MinUpdates - 1, 0)))).Findings.Should().BeEmpty();
        check.Evaluate(Hot(Table(orders)), Evaluation(Updates(orders.Key, (0, 0), (20_000, 15_000)))).Findings.Should().BeEmpty();

        HealthVerdict one = check.Evaluate(
            Hot(Table(orders)),
            Evaluation(History((TimeSpan.Zero, [(HotUpdateCheck.UpdatesSeries + orders.Key, 1_000_000), (HotUpdateCheck.HotUpdatesSeries + orders.Key, 0)]))));
        one.Findings.Should().BeEmpty("a lifetime total is not a rate");
        one.Note.Should().Contain("two samples");
    }

    [Fact]
    public void An_event_store_table_is_not_judged_for_HOT()
    {
        MartenTable streams = EventTable("mt_streams");

        new HotUpdateCheck().Evaluate(Hot(Table(streams)), Evaluation(Updates(streams.Key, (0, 0), (50_000, 0))))
            .Findings.Should().BeEmpty("no Schema.For<T>() line sets an event table's fill factor; E08 judges mt_dcb_tag_version");
    }

    // -----------------------------------------------------------------------------------------------
    // T07 Vacuum falling behind
    // -----------------------------------------------------------------------------------------------

    private static TableFacts Bloated(MartenTable table, string[]? options = null, bool serverAutovacuum = true) =>
        Table(table, row => row with
        {
            LiveRows = 40_000,
            DeadRows = 20_000,
            LastVacuum = null,
            LastAutovacuum = Now.AddDays(-3),
            StorageOptions = options ?? [],
            AutovacuumEnabled = serverAutovacuum,
        });

    private static HealthHistoryView Held(string sweptSeries, string conditionSeries, TimeSpan held) =>
        History(
            (held, [(sweptSeries, 1), (conditionSeries, 1)]),
            (TimeSpan.Zero, [(sweptSeries, 1), (conditionSeries, 1)]));

    [Fact]
    public void A_third_dead_and_unvacuumed_for_three_days_is_only_watched_until_it_has_held_for_ten_minutes()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Bloated(orders);
        string condition = VacuumBehindCheck.BehindSeries + orders.Key;

        HealthVerdict glimpsed = new VacuumBehindCheck().Evaluate(new VacuumFacts(Now, [table]), Evaluation(Held(VacuumBehindCheck.SweptSeries, condition, TimeSpan.FromMinutes(3))));
        glimpsed.Findings.Should().BeEmpty("autovacuum usually catches up within a minute of a batch delete");
        glimpsed.Note.Should().Contain("Watching 1 table");

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation(Held(VacuumBehindCheck.SweptSeries, condition, TimeSpan.FromMinutes(12))))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T07");
        finding.Title.Should().Contain("33%").And.Contain("3 days ago");
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().StorageParameter(\"autovacuum_vacuum_scale_factor\", 0.02);");
        finding.Remedy.Requires.Should().BeSameAs(RuntimeFeature.DocumentStorageParameter);
        finding.Remedy.Explanation.Should().Contain("Below Marten 9.47").And.Contain("ALTER TABLE");
        finding.Caveat.Should().Contain("Seen for 12 min");
    }

    /// <summary>
    /// The title is the headline of the README's Health screenshot. A table vacuumed by hand or by autovacuum says how
    /// long ago; one nothing has vacuumed says so in a clause of its own, never "it was last vacuumed never".
    /// </summary>
    [Theory]
    [InlineData(3, null, "it was last vacuumed 3 days ago")]
    [InlineData(null, 2, "it was last vacuumed 2 days ago")]
    [InlineData(null, null, "it has never been vacuumed")]
    public void The_title_says_how_long_ago_the_table_was_last_vacuumed_or_that_it_never_was(
        int? autovacuumedDaysAgo,
        int? vacuumedDaysAgo,
        string clause)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            LiveRows = 40_000,
            DeadRows = 20_000,
            LastVacuum = vacuumedDaysAgo is { } manual ? Now.AddDays(-manual) : null,
            LastAutovacuum = autovacuumedDaysAgo is { } auto ? Now.AddDays(-auto) : null,
            StorageOptions = ["autovacuum_enabled=false"],
        });

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("33% of " + orders.Key + " is dead rows, and " + clause);
        finding.Title.Should().NotContain("vacuumed never");
    }

    [Fact]
    public void A_break_in_the_condition_restarts_the_clock()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        string condition = VacuumBehindCheck.BehindSeries + orders.Key;

        HealthHistoryView broken = History(
            (TimeSpan.FromMinutes(30), [(VacuumBehindCheck.SweptSeries, 1), (condition, 1)]),
            (TimeSpan.FromMinutes(5), [(VacuumBehindCheck.SweptSeries, 1)]),
            (TimeSpan.Zero, [(VacuumBehindCheck.SweptSeries, 1), (condition, 1)]));

        new VacuumBehindCheck().Evaluate(new VacuumFacts(Now, [Bloated(orders)]), Evaluation(broken)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void With_autovacuum_off_for_the_table_there_is_nothing_to_wait_for_and_the_remedy_switches_it_on()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Bloated(orders, ["autovacuum_enabled=false"])]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.WhyItMatters.Should().Contain("switched off for this table");
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().StorageParameter(\"autovacuum_enabled\", true);");
    }

    [Fact]
    public void With_autovacuum_off_for_the_server_the_finding_says_so_and_offers_no_StoreOptions_line()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Bloated(orders, serverAutovacuum: false)]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.WhyItMatters.Should().Contain("autovacuum = off");
        finding.Remedy.Should().BeNull();
    }

    [Fact]
    public void An_event_table_gets_the_DBA_s_setting_in_words_and_no_line()
    {
        MartenTable streams = EventTable("mt_streams");

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Bloated(streams, ["autovacuum_enabled=off"])]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Remedy.Should().BeNull();
        finding.WhyItMatters.Should().Contain("switched off for this table");
    }

    [Theory]
    [InlineData(40_000, 9_999, null)]
    [InlineData(100_000, 20_000, null)]
    [InlineData(40_000, 20_000, 2)]
    public void Below_the_dead_rows_the_share_or_with_a_vacuum_in_the_last_day_a_table_is_not_behind(long live, long dead, int? vacuumedHoursAgo)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            LiveRows = live,
            DeadRows = dead,
            LastVacuum = null,
            LastAutovacuum = vacuumedHoursAgo is { } hours ? Now.AddHours(-hours) : Now.AddDays(-5),
            StorageOptions = ["autovacuum_enabled=false"],
        });

        VacuumBehindCheck.IsBehind(table, Now).Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------------
    // T08 Never analysed, or stale statistics
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Rows_and_reltuples_minus_one_with_autovacuum_off_is_a_never_analysed_warning_at_once()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            EstimatedRows = -1,
            LiveRows = 2_000,
            LastAnalyze = null,
            LastAutoanalyze = null,
            StorageOptions = ["autovacuum_enabled=false"],
        });

        HealthFinding finding = new StaleStatisticsCheck().Evaluate(new StatisticsFacts(Now, [table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T08");
        finding.Title.Should().Be(orders.Key + " has 2,000 rows and has never been analysed");
        finding.WhyItMatters.Should().Contain("reltuples is -1").And.Contain("~ counts");
        finding.Evidence.Should().Contain(new HealthEvidence("Estimated rows (reltuples)", "not analysed"));
    }

    [Fact]
    public void A_freshly_loaded_table_with_autovacuum_on_is_watched_not_reported()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { EstimatedRows = -1, LiveRows = 50_000, LastAnalyze = null, LastAutoanalyze = null });

        HealthVerdict verdict = new StaleStatisticsCheck().Evaluate(new StatisticsFacts(Now, [table]), Evaluation());

        verdict.Findings.Should().BeEmpty("autovacuum analyses a freshly loaded table within a minute or two");
        verdict.Note.Should().Contain("Watching 1 table");
    }

    [Fact]
    public void A_fifth_of_the_table_changed_since_an_analyze_two_days_ago_is_stale_and_held_it_is_a_finding()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            EstimatedRows = 100_000,
            LiveRows = 100_000,
            ModifiedSinceAnalyze = 30_000,
            LastAnalyze = null,
            LastAutoanalyze = Now.AddDays(-2),
        });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(StatisticsProblem.Stale);

        HealthFinding finding = new StaleStatisticsCheck()
            .Evaluate(
                new StatisticsFacts(Now, [table]),
                Evaluation(Held(StaleStatisticsCheck.SweptSeries, StaleStatisticsCheck.WrongSeries + orders.Key, TimeSpan.FromMinutes(15))))
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("30,000 rows of " + orders.Key + " have changed since it was last analysed");
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().StorageParameter(\"autovacuum_analyze_scale_factor\", 0.05);");
        finding.Remedy.Requires.Should().BeSameAs(RuntimeFeature.DocumentStorageParameter);
    }

    [Theory]
    [InlineData(100_000, 9_999, 48)]
    [InlineData(100_000, 15_000, 48)]
    [InlineData(100_000, 30_000, 2)]
    [InlineData(500, 600, 0)]
    public void Too_few_changes_too_small_a_share_or_a_recent_analyze_is_not_stale(long estimate, long modified, int analyzedHoursAgo)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            EstimatedRows = estimate,
            LiveRows = estimate,
            ModifiedSinceAnalyze = modified,
            LastAutoanalyze = analyzedHoursAgo == 0 ? null : Now.AddHours(-analyzedHoursAgo),
        });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(StatisticsProblem.None);
    }

    [Fact]
    public void A_never_analysed_table_below_the_row_line_is_not_worth_a_finding()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { EstimatedRows = -1, LiveRows = 999, LastAnalyze = null, LastAutoanalyze = null });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(StatisticsProblem.None);
    }

    [Fact]
    public void Sampling_writes_the_swept_marker_and_a_flag_only_for_the_tables_that_are_behind()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable events = EventTable();

        VacuumFacts facts = new(Now, [Bloated(orders), Table(events)]);

        new VacuumBehindCheck().Sample(facts).Should().BeEquivalentTo(
        [
            new HealthSamplePart(VacuumBehindCheck.SweptSeries, 1),
            new HealthSamplePart(VacuumBehindCheck.BehindSeries + orders.Key, 1),
        ]);
    }
}
