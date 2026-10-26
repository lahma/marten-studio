using MartenStudio.Services.Configuration;

using PostgresStudio.Internal.Sql;

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
    // T07 Vacuum falling behind - against the table's own triggers (W5-V1, issue #8 G2)
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A million rows tuned to autovacuum_vacuum_scale_factor 0.01: its trigger is 10,050, so 40,000 dead rows - four per
    /// cent of the table, nowhere near the fifth 0.4 waited for - are past twice it by more than ten thousand.
    /// </summary>
    private static TableFacts Tuned(MartenTable table, long dead = 40_000, DateTimeOffset? last = null, string[]? options = null, bool serverAutovacuum = true) =>
        Table(table, row => row with
        {
            EstimatedRows = 1_000_000,
            LiveRows = 1_000_000,
            DeadRows = dead,
            LastVacuum = null,
            LastAutovacuum = last ?? Now.AddDays(-3),
            StorageOptions = options ?? ["autovacuum_vacuum_scale_factor=0.01"],
            AutovacuumEnabled = serverAutovacuum,
            Vacuum = Unit(dead, 10_050, last ?? Now.AddDays(-3), scale: 0.01, tuples: 1_000_000),
        });

    private static HealthHistoryView Held(string sweptSeries, string conditionSeries, TimeSpan held) =>
        History(
            (held, [(sweptSeries, 1), (conditionSeries, 1)]),
            (TimeSpan.Zero, [(sweptSeries, 1), (conditionSeries, 1)]));

    [Fact]
    public void A_table_tuned_to_one_percent_is_behind_long_before_a_fifth_of_it_is_dead()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Tuned(orders);
        string condition = VacuumBehindCheck.BehindSeries + orders.Key;

        table.DeadShare.Should().BeLessThan(0.05, "0.4's fixed 20% would have said nothing");

        HealthVerdict glimpsed = new VacuumBehindCheck().Evaluate(new VacuumFacts(Now, [table]), Evaluation(Held(VacuumBehindCheck.SweptSeries, condition, TimeSpan.FromMinutes(3))));
        glimpsed.Findings.Should().BeEmpty("a batch delete is caught up within a minute, so the condition must hold");
        glimpsed.Note.Should().Contain("Watching 1 table");

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation(Held(VacuumBehindCheck.SweptSeries, condition, TimeSpan.FromMinutes(12))))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T07");
        finding.Variant.Should().Be(VacuumBehindCheck.DeadRowsVariant);
        finding.Key.Should().Be("T07|dead-rows|" + orders.Key);
        finding.Severity.Should().Be(HealthSeverity.Warning, "four times its trigger is not ten");
        finding.Title.Should().Be(orders.Key + " has 40,000 dead rows, 4 times the 10,050 that trigger its autovacuum, and was last vacuumed 3 days ago");
        finding.Evidence.Should().Contain(x => x.Label == "Its dead-row trigger"
            && x.Value.Contains("autovacuum_vacuum_scale_factor 0.01, the table's own storage parameter", StringComparison.Ordinal)
            && x.Value.Contains("autovacuum_vacuum_threshold 50, the server's setting", StringComparison.Ordinal));
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().StorageParameter(\"autovacuum_vacuum_scale_factor\", 0.005);");
        finding.Remedy.Requires.Should().BeSameAs(RuntimeFeature.DocumentStorageParameter);
        finding.Remedy.Explanation.Should().Contain("is now 10,050").And.Contain("at 0.005 it would be 5,050").And.Contain("Below Marten 9.47");
        finding.Caveat.Should().Contain("Seen for 12 min").And.Contain("inflated");
    }

    [Theory]
    [InlineData(30_099, 2, true, false, false)]
    [InlineData(30_100, 2, true, false, true)]
    [InlineData(40_000, 0.9, true, false, false)]
    [InlineData(40_000, 1.1, true, false, true)]
    [InlineData(40_000, 2, false, false, true)]
    [InlineData(40_000, 2, true, true, false)]
    public void Behind_is_ten_thousand_past_twice_the_trigger_an_hour_since_the_last_vacuum_and_none_running(
        long dead,
        double hoursSinceVacuum,
        bool vacuumedBefore,
        bool running,
        bool behind)
    {
        AutovacuumUnit unit = Unit(dead, 10_050, vacuumedBefore ? Now.AddHours(-hoursSinceVacuum) : null, running);

        VacuumBehindCheck.IsBehind(unit, Now).Should().Be(behind);
    }

    /// <summary>
    /// B0's PostgreSQL 18 shape: an append-only table whose insert trigger the unfrozen share has brought down to 1,000. Twenty
    /// thousand appends are twenty times it - the inserts variant, Critical past ten times.
    /// </summary>
    [Fact]
    public void An_append_only_table_past_its_insert_trigger_is_the_inserts_variant()
    {
        MartenTable events = EventTable();
        TableFacts table = Table(events, row => row with
        {
            EstimatedRows = 10_000,
            LiveRows = 30_000,
            LastAutovacuum = Now.AddHours(-5),
            Insert = Unit(20_000, 1_000, Now.AddHours(-5), threshold: 1_000, scale: 0.5),
            UnfrozenShare = 0,
        });

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation(Held(VacuumBehindCheck.SweptSeries, VacuumBehindCheck.InsertsBehindSeries + events.Key, TimeSpan.FromMinutes(15))))
            .Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be(VacuumBehindCheck.InsertsVariant);
        finding.Key.Should().Be("T07|inserts|" + events.Key);
        finding.Severity.Should().Be(HealthSeverity.Critical, "twenty times the trigger is past ten");
        finding.Title.Should().Contain("20,000 rows inserted since its last vacuum, 20 times the 1,000");
        finding.WhyItMatters.Should().Contain("append-only").And.Contain("mt_events");
        finding.Evidence.Should().Contain(x => x.Label == "Its insert trigger" && x.Value.Contains("0% of its pages not all-frozen", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_decisions_behind_are_two_findings_with_keys_of_their_own()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts tuned = Tuned(orders, options: ["autovacuum_enabled=false"]);
        TableFacts table = tuned with { Row = tuned.Row with { Insert = Unit(25_000, 3_000, Now.AddDays(-3)) } };

        HealthVerdict verdict = new VacuumBehindCheck().Evaluate(new VacuumFacts(Now, [table]), Evaluation());

        verdict.Findings.Select(static x => x.Key).Should().BeEquivalentTo(["T07|dead-rows|" + orders.Key, "T07|inserts|" + orders.Key]);
    }

    [Fact]
    public void Autovacuum_switched_off_for_the_table_is_Critical_at_once_and_the_remedy_switches_it_on()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Tuned(orders, options: ["autovacuum_enabled=false"])]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.WhyItMatters.Should().Contain("switched off for this table");
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().StorageParameter(\"autovacuum_enabled\", true);");
    }

    [Fact]
    public void Autovacuum_switched_off_for_the_server_is_Critical_and_offers_no_StoreOptions_line()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Tuned(orders, serverAutovacuum: false)]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.WhyItMatters.Should().Contain("autovacuum = off");
        finding.Remedy.Should().BeNull();
    }

    [Fact]
    public void Past_ten_times_its_trigger_is_Critical_with_autovacuum_on()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Tuned(orders, dead: 101_000);

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation(Held(VacuumBehindCheck.SweptSeries, VacuumBehindCheck.BehindSeries + orders.Key, TimeSpan.FromMinutes(11))))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
    }

    [Fact]
    public void A_role_that_cannot_see_autovacuum_s_progress_still_gets_the_finding_with_its_caveat()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        const string Hidden = "1 VACUUM running in this database this role cannot see the table of. Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role app.";

        HealthFinding hidden = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Tuned(orders, options: ["autovacuum_enabled=false"])], Hidden), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        hidden.Caveat.Should().Contain("It may be under way now: " + Hidden);
        hidden.Evidence.Should().Contain(new HealthEvidence("Being vacuumed now", "not known - this role cannot see autovacuum's progress"));

        HealthFinding seen = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Tuned(orders, options: ["autovacuum_enabled=false"])]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        seen.Caveat.Should().NotContain("under way");
        seen.Evidence.Should().Contain(new HealthEvidence("Being vacuumed now", "no"));
    }

    [Fact]
    public void A_scale_factor_already_at_the_bottom_of_the_ladder_is_not_the_remedy()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts tuned = Tuned(orders);
        TableFacts table = tuned with { Row = tuned.Row with { Vacuum = Unit(40_000, 1_050, Now.AddDays(-3), scale: 0.001, tuples: 1_000_000) } };

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation(Held(VacuumBehindCheck.SweptSeries, VacuumBehindCheck.BehindSeries + orders.Key, TimeSpan.FromMinutes(11))))
            .Findings.Should().ContainSingle().Subject;

        finding.Remedy.Should().BeNull();
        finding.WhyItMatters.Should().Contain("is already 0.001").And.Contain("T16").And.Contain("T14");
    }

    [Theory]
    [InlineData(3, null, "was last vacuumed 3 days ago")]
    [InlineData(null, 2, "was last vacuumed 2 days ago")]
    [InlineData(null, null, "has never been vacuumed")]
    public void The_title_says_how_long_ago_the_table_was_last_vacuumed_or_that_it_never_was(
        int? autovacuumedDaysAgo,
        int? vacuumedDaysAgo,
        string clause)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        DateTimeOffset? auto = autovacuumedDaysAgo is { } a ? Now.AddDays(-a) : null;
        DateTimeOffset? manual = vacuumedDaysAgo is { } m ? Now.AddDays(-m) : null;

        TableFacts table = Table(orders, row => row with
        {
            LiveRows = 40_000,
            DeadRows = 20_000,
            LastVacuum = manual,
            LastAutovacuum = auto,
            StorageOptions = ["autovacuum_enabled=false"],
            Vacuum = Unit(20_000, 50, auto ?? manual, tuples: 0),
        });

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().EndWith(clause);
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

        new VacuumBehindCheck().Evaluate(new VacuumFacts(Now, [Tuned(orders)]), Evaluation(broken)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void An_event_table_gets_the_DBA_s_setting_in_words_and_no_line()
    {
        MartenTable streams = EventTable("mt_streams");

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [Tuned(streams, options: ["autovacuum_enabled=off"])]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Remedy.Should().BeNull();
        finding.WhyItMatters.Should().Contain("switched off for this table");
    }

    [Fact]
    public void A_partitioned_table_is_judged_by_its_partition_furthest_behind_and_never_names_it()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            Kind = "p",
            PartitionLeaves = 40,
            PastVacuumTrigger = 3,
            StorageOptions = ["autovacuum_enabled=false"],
            Vacuum = Unit(30_000, 2_050, Now.AddDays(-2)),
        });

        HealthFinding finding = new VacuumBehindCheck()
            .Evaluate(new VacuumFacts(Now, [table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().StartWith("A partition of " + orders.Key + " has 30,000 dead rows");
        finding.Evidence.Should().Contain(new HealthEvidence("Partitions past their trigger", "3 of 40"));
        finding.Evidence.Should().Contain(x => x.Label == "Its dead-row trigger" && x.Value.Contains("the partition's", StringComparison.Ordinal));
    }

    [Fact]
    public void Sampling_writes_the_swept_marker_and_a_flag_per_variant_only_for_the_tables_that_are_behind()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable events = EventTable();
        TableFacts appended = Table(events, row => row with { Insert = Unit(20_000, 1_000, Now.AddHours(-5)) });

        VacuumFacts facts = new(Now, [Tuned(orders), appended, Table(EventTable("mt_streams"))]);

        new VacuumBehindCheck().Sample(facts).Should().BeEquivalentTo(
        [
            new HealthSamplePart(VacuumBehindCheck.SweptSeries, 1),
            new HealthSamplePart(VacuumBehindCheck.BehindSeries + orders.Key, 1),
            new HealthSamplePart(VacuumBehindCheck.InsertsBehindSeries + events.Key, 1),
        ]);
    }

    // -----------------------------------------------------------------------------------------------
    // T08 Never analysed, or stale statistics - against the table's own analyze trigger (W5-V1)
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
        finding.Variant.Should().Be(StaleStatisticsCheck.NeverAnalysedVariant);
        finding.Title.Should().Be(orders.Key + " has 2,000 rows and has never been analysed");
        finding.WhyItMatters.Should().Contain("reltuples is -1").And.Contain("~ counts").And.Contain("--analyze-in-stages");
        finding.Evidence.Should().Contain(new HealthEvidence("Estimated rows (reltuples)", "not analysed"));
    }

    /// <summary>
    /// The pg_upgrade shape (2026-10-26 health-gap analysis): reltuples -1 and n_live_tup 0, so only the reads say the table
    /// is in use - and a read-mostly table is never modified enough for autovacuum to analyse it.
    /// </summary>
    [Theory]
    [InlineData(600, 400, true)]
    [InlineData(600, 399, false)]
    public void A_table_with_no_statistics_that_is_read_is_never_analysed_even_with_no_live_rows_counted(long seqRead, long indexFetched, bool flagged)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            EstimatedRows = -1,
            LiveRows = 0,
            SequentialRowsRead = seqRead,
            IndexRowsFetched = indexFetched,
            LastAnalyze = null,
            LastAutoanalyze = null,
        });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(flagged ? StatisticsProblem.NeverAnalysed : StatisticsProblem.None);

        if (flagged)
        {
            HealthFinding finding = new StaleStatisticsCheck()
                .Evaluate(new StatisticsFacts(Now, [table]), Evaluation(Held(StaleStatisticsCheck.SweptSeries, StaleStatisticsCheck.WrongSeries + orders.Key, TimeSpan.FromMinutes(11))))
                .Findings.Should().ContainSingle().Subject;

            finding.Title.Should().Be(orders.Key + " has been read 1,000 rows deep and has never been analysed");
        }
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
    public void Ten_thousand_past_twice_its_analyze_trigger_with_no_analyze_in_an_hour_is_stale()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            EstimatedRows = 100_000,
            LiveRows = 100_000,
            ModifiedSinceAnalyze = 30_200,
            LastAutoanalyze = Now.AddHours(-2),
            Analyze = Unit(30_200, 10_050, Now.AddHours(-2), scale: 0.1, tuples: 100_000),
        });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(StatisticsProblem.Stale);

        HealthFinding finding = new StaleStatisticsCheck()
            .Evaluate(
                new StatisticsFacts(Now, [table]),
                Evaluation(Held(StaleStatisticsCheck.SweptSeries, StaleStatisticsCheck.WrongSeries + orders.Key, TimeSpan.FromMinutes(15))))
            .Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be(StaleStatisticsCheck.StaleVariant);
        finding.Key.Should().Be("T08|stale|" + orders.Key);
        finding.Title.Should().Be("30,200 rows of " + orders.Key + " have changed since it was last analysed, 3 times the 10,050 that trigger its autoanalyze");
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().StorageParameter(\"autovacuum_analyze_scale_factor\", 0.05);");
        finding.Remedy.Requires.Should().BeSameAs(RuntimeFeature.DocumentStorageParameter);
        finding.Remedy.Explanation.Should().Contain("is now 10,050").And.Contain("it would be 5,050");
    }

    [Theory]
    [InlineData(30_099, 2)]
    [InlineData(30_200, 0.9)]
    [InlineData(5_000, 48)]
    public void Too_few_changes_past_twice_the_trigger_or_an_analyze_within_the_hour_is_not_stale(long modified, double analyzedHoursAgo)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            EstimatedRows = 100_000,
            LiveRows = 100_000,
            ModifiedSinceAnalyze = modified,
            Analyze = Unit(modified, 10_050, Now.AddHours(-analyzedHoursAgo), scale: 0.1, tuples: 100_000),
        });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(StatisticsProblem.None);
    }

    [Fact]
    public void A_never_analysed_table_below_the_row_line_and_little_read_is_not_worth_a_finding()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { EstimatedRows = -1, LiveRows = 999, LastAnalyze = null, LastAutoanalyze = null });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(StatisticsProblem.None);
    }

    [Fact]
    public void Partitions_never_analysed_are_a_warning_about_the_table_that_names_none_of_them()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            Kind = "p",
            PartitionLeaves = 12,
            EstimatedRows = 40_000,
            UnanalyzedLeaves = 2,
            UnanalyzedLiveRows = 3_000,
            StorageOptions = ["autovacuum_enabled=false"],
            ParentEstimatedRows = -1,
        });

        HealthFinding finding = new StaleStatisticsCheck().Evaluate(new StatisticsFacts(Now, [table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Variant.Should().Be(StaleStatisticsCheck.NeverAnalysedVariant);
        finding.Title.Should().Be("2 partitions of " + orders.Key + " have never been analysed");
    }

    /// <summary>Autovacuum never analyses a partitioned parent (B0 §8c): Info, at once, with nothing to wait for.</summary>
    [Fact]
    public void A_partitioned_parent_nobody_analysed_is_Info_at_once_and_says_autovacuum_never_does()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            Kind = "p",
            PartitionLeaves = 12,
            EstimatedRows = 40_000,
            LiveRows = 40_000,
            ParentEstimatedRows = -1,
            Analyze = Unit(0, 1_050, Now.AddMinutes(-5)),
        });

        StaleStatisticsCheck.ProblemOf(table, Now).Should().Be(StatisticsProblem.ParentNeverAnalysed);

        HealthFinding finding = new StaleStatisticsCheck().Evaluate(new StatisticsFacts(Now, [table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Variant.Should().Be(StaleStatisticsCheck.NeverAnalysedVariant);
        finding.WhyItMatters.Should().Contain("autovacuum does not analyse partitioned tables");

        StaleStatisticsCheck.ProblemOf(table with { Row = table.Row with { ParentEstimatedRows = 40_000 } }, Now).Should().Be(StatisticsProblem.None);
    }

    /// <summary>
    /// since-restart (2026-10-26 health-gap analysis): the counters began again, and the large tables nothing analysed since
    /// hold at least half the judged rows - one database-level Info that names the largest and clears once they are analysed.
    /// </summary>
    [Fact]
    public void Counters_begun_again_under_most_of_the_rows_is_one_since_restart_info()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable events = EventTable();
        MartenTable streams = EventTable("mt_streams");
        DateTimeOffset since = Now.AddHours(-3);

        TableFacts big = Table(orders, row => row with { EstimatedRows = 500_000, LastAutoanalyze = Now.AddDays(-2) });
        TableFacts other = Table(events, row => row with { EstimatedRows = 400_000, LastAutoanalyze = Now.AddHours(-1) });
        TableFacts small = Table(streams, row => row with { EstimatedRows = 5_000, LastAutoanalyze = Now.AddDays(-9) });

        StatisticsRestart restart = new(since, "a crash");

        HealthFinding finding = StaleStatisticsCheck.SinceRestart(new StatisticsFacts(Now, [big, other, small], restart))!;

        finding.Variant.Should().Be(StaleStatisticsCheck.SinceRestartVariant);
        finding.Subject.Should().BeNull("it is the database's");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().Contain("began again at 2026-10-15 06:00 UTC (a crash)").And.Contain("invisible to it, and to T07");
        finding.Evidence.Should().Contain(new HealthEvidence("The largest of them", orders.Key));
        finding.WhyItMatters.Should().Contain("vacuumdb --all --analyze-only");

        StaleStatisticsCheck.SinceRestart(new StatisticsFacts(Now, [big with { Row = big.Row with { LastAutoanalyze = Now.AddMinutes(-30) } }, other, small], restart))
            .Should().BeNull("once the large table is analysed, less than half the rows are unanalysed since");
        StaleStatisticsCheck.SinceRestart(new StatisticsFacts(Now, [big, other, small])).Should().BeNull("no reset, nothing to say");
    }

    [Fact]
    public void Sampling_writes_the_swept_marker_and_a_flag_only_for_the_tables_whose_statistics_are_wrong()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts never = Table(orders, row => row with { EstimatedRows = -1, LiveRows = 5_000, LastAnalyze = null, LastAutoanalyze = null });

        new StaleStatisticsCheck().Sample(new StatisticsFacts(Now, [never, Table(EventTable())])).Should().BeEquivalentTo(
        [
            new HealthSamplePart(StaleStatisticsCheck.SweptSeries, 1),
            new HealthSamplePart(StaleStatisticsCheck.WrongSeries + orders.Key, 1),
        ]);
    }
}
