using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>T01's judgement over stated facts: rates over the history, never totals.</summary>
public class SequentialScansCheckTests
{
    private static readonly SequentialScansCheck Check = new();

    private static SequentialScanFacts Facts(TableFacts table, int parallelWorkers = 2) =>
        new(Now, [table], new DatabaseStatistics(Now.AddDays(-30), true, parallelWorkers));

    private static TableFacts Large(MartenTable table, long rows = 200_000, string kind = "r") =>
        Table(table, row => row with { LiveRows = rows, EstimatedRows = rows, Kind = kind });

    private static HealthHistoryView Window(string key, (double Scans, double Read) first, (double Scans, double Read) last) =>
        History(
            (TimeSpan.FromMinutes(30), [(SequentialScansCheck.ScansSeries + key, first.Scans), (SequentialScansCheck.RowsReadSeries + key, first.Read)]),
            (TimeSpan.Zero, [(SequentialScansCheck.ScansSeries + key, last.Scans), (SequentialScansCheck.RowsReadSeries + key, last.Read)]));

    [Fact]
    public void Full_table_scans_read_the_table_three_times_over_in_the_window_is_a_warning_with_the_index_line()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Large(orders);

        HealthVerdict verdict = Check.Evaluate(Facts(table), Evaluation(Window(orders.Key, (10, 1_000_000), (14, 1_800_000))));

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("T01");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be(orders.Key);
        finding.DocumentTypes.Should().Equal(typeof(HealthOrderDoc));
        finding.Title.Should().Contain("4 times").And.Contain("30 min");
        finding.Evidence.Should().Contain(new HealthEvidence("Sequential scans in the window", "4"));
        finding.Remedy!.Code.Should().Contain("opts.Schema.For<HealthOrderDoc>().Index(x => x.FilteredMember);");
        finding.Remedy.Explanation.Should().Contain("Query").And.Contain(orders.Alias!);
        finding.Remedy.Requires.Should().BeNull("Index and Duplicate exist at the floor");
        finding.Caveat.Should().Contain("statistics were last reset").And.Contain("parallel scan");

        finding.Link.Should().NotBeNull("the index advisor is the screen that helps");
        finding.Link!.Route.Should().Be("query");
        finding.Link.Query.Should().Equal(
            new KeyValuePair<string, string?>("mode", "marten"),
            new KeyValuePair<string, string?>("type", orders.Alias));
    }

    [Fact]
    public void One_sample_is_not_a_rate_and_the_note_says_it_is_watching()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthHistoryView one = History((TimeSpan.Zero, [(SequentialScansCheck.ScansSeries + orders.Key, 50), (SequentialScansCheck.RowsReadSeries + orders.Key, 9_000_000)]));

        HealthVerdict verdict = Check.Evaluate(Facts(Large(orders)), Evaluation(one));

        verdict.Findings.Should().BeEmpty("a lifetime total of scans is last spring's bulk load, not this hour's query");
        verdict.Note.Should().Contain("Watching 1 large table").And.Contain("two samples");
    }

    [Fact]
    public void A_table_below_the_size_line_is_never_judged()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts small = Large(orders, rows: SequentialScansCheck.LargeTableRows - 1);

        HealthVerdict verdict = Check.Evaluate(Facts(small), Evaluation(Window(orders.Key, (0, 0), (1_000, 99_999_000))));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("100,000 live rows");
    }

    [Fact]
    public void Many_cheap_scans_that_each_read_a_few_rows_are_not_whole_table_scans()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        // A million "limit 1"-shaped scans reading ten rows each: 10M rows, 50 table-equivalents, but no scan read
        // anything like the table.
        HealthVerdict verdict = Check.Evaluate(Facts(Large(orders)), Evaluation(Window(orders.Key, (0, 0), (1_000_000, 10_000_000))));

        verdict.Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_parallel_scan_counts_once_per_worker_and_still_reads_the_whole_table()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        // Four full scans of 200,000 rows, each by a leader and two workers: twelve scans of about 66,667 rows.
        HealthHistoryView window = Window(orders.Key, (0, 0), (12, 800_000));

        Check.Evaluate(Facts(Large(orders), parallelWorkers: 2), Evaluation(window)).Findings.Should().ContainSingle();
        Check.Evaluate(Facts(Large(orders), parallelWorkers: 0), Evaluation(window)).Findings
            .Should().BeEmpty("with no parallel workers a scan of a third of the table is not a whole-table scan");
    }

    private static TableFacts Partitioned(MartenTable table, int leaves, long rowsPerLeaf) =>
        Table(table, row => row with
        {
            Kind = "p",
            LiveRows = leaves * rowsPerLeaf,
            EstimatedRows = leaves * rowsPerLeaf,
            PartitionLeaves = leaves,
            LargestLeafRows = rowsPerLeaf,
        });

    [Fact]
    public void A_partitioned_table_with_large_partitions_scanned_whole_is_a_partition_wide_warning()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        // Four partitions of 250,000 rows; four unpruned scans of the whole table count a scan per partition: sixteen
        // scans of 250,000 rows each, four times the table's worth.
        HealthHistoryView window = Window(orders.Key, (0, 0), (16, 4_000_000));

        HealthFinding finding = Check.Evaluate(Facts(Partitioned(orders, 4, 250_000)), Evaluation(window)).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().StartWith("Partition-wide sequential scans of " + orders.Key).And.Contain("4 times");
    }

    /// <summary>
    /// The false alarm the rollup exists to prevent: fifty tenants of four thousand rows, every query scanning its
    /// own tenant's partition. Each scan reads a small table whole, which is the right plan, however many of them
    /// add up to the partitioned table's worth.
    /// </summary>
    [Fact]
    public void Many_small_partitions_each_scanned_by_its_own_tenant_s_queries_say_nothing()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        // 10,000 per-tenant scans of one 4,000-row partition each: 40 million rows, 200 times the table's 200,000.
        HealthHistoryView window = Window(orders.Key, (0, 0), (10_000, 40_000_000));

        HealthVerdict verdict = Check.Evaluate(Facts(Partitioned(orders, 50, 4_000)), Evaluation(window));

        verdict.Findings.Should().BeEmpty("no partition is large, and a sequential scan of a small table is often the right plan");
        verdict.Note.Should().Contain("no partition of one");
        Check.Sample(Facts(Partitioned(orders, 50, 4_000))).Should().BeEmpty("a table that is never judged is never sampled");
    }

    [Fact]
    public void A_partitioned_table_s_scan_is_measured_against_its_mean_partition_not_the_whole_table()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        // Ten partitions, the largest 150,000 rows, 1,000,000 in all - a mean of 100,000. Forty scans of 100,000 rows
        // each read four tables' worth, and each read a whole mean partition: a finding, although no scan read half
        // the table.
        HealthHistoryView whole = Window(orders.Key, (0, 0), (40, 4_000_000));
        TableFacts table = Table(orders, row => row with
        {
            Kind = "p",
            LiveRows = 1_000_000,
            EstimatedRows = 1_000_000,
            PartitionLeaves = 10,
            LargestLeafRows = 150_000,
        });

        Check.Evaluate(Facts(table, parallelWorkers: 0), Evaluation(whole)).Findings.Should().ContainSingle();

        // The same rows read by four hundred thousand scans of ten rows each are index-shaped lookups, not whole scans.
        HealthHistoryView cheap = Window(orders.Key, (0, 0), (400_000, 4_000_000));
        Check.Evaluate(Facts(table, parallelWorkers: 0), Evaluation(cheap)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_statistics_reset_inside_the_window_restarts_the_window_rather_than_counting_negative_scans()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        string scans = SequentialScansCheck.ScansSeries + orders.Key;
        string read = SequentialScansCheck.RowsReadSeries + orders.Key;

        HealthHistoryView window = History(
            (TimeSpan.FromMinutes(40), [(scans, 900), (read, 90_000_000)]),
            (TimeSpan.FromMinutes(20), [(scans, 0), (read, 0)]),
            (TimeSpan.Zero, [(scans, 1), (read, 200_000)]));

        HealthVerdict verdict = Check.Evaluate(Facts(Large(orders)), Evaluation(window));

        verdict.Findings.Should().BeEmpty("one whole-table read since the reset is not three");
    }

    [Fact]
    public void An_event_store_table_gets_the_finding_without_a_Schema_For_line()
    {
        MartenTable events = EventTable();

        HealthVerdict verdict = Check.Evaluate(Facts(Large(events)), Evaluation(Window(events.Key, (0, 0), (5, 1_000_000))));

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Remedy.Should().BeNull("no StoreOptions line indexes an event table for an arbitrary query");
        finding.DocumentTypes.Should().BeEmpty();
    }

    [Fact]
    public void Sampling_writes_both_counters_of_every_large_table_and_nothing_for_a_small_one()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable events = EventTable();

        SequentialScanFacts facts = new(
            Now,
            [
                Table(orders, row => row with { LiveRows = 500_000, SequentialScans = 7, SequentialRowsRead = 70 }),
                Table(events, row => row with { LiveRows = 10, SequentialScans = 3, SequentialRowsRead = 30 }),
            ],
            DatabaseStatistics.Unknown);

        Check.Sample(facts).Should().BeEquivalentTo(
        [
            new HealthSamplePart(SequentialScansCheck.ScansSeries + orders.Key, 7),
            new HealthSamplePart(SequentialScansCheck.RowsReadSeries + orders.Key, 70),
        ]);
    }

    [Fact]
    public void A_table_with_no_rows_read_to_judge_says_nothing()
    {
        HealthVerdict verdict = Check.Evaluate(new SequentialScanFacts(Now, [], DatabaseStatistics.Unknown), Evaluation());

        verdict.Findings.Should().BeEmpty();
    }

    /// <summary>The row the evaluator judges from: the collector's live count before the planner's estimate.</summary>
    [Fact]
    public void Rows_are_the_live_count_and_the_estimate_only_when_the_collector_has_none()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        Table(orders, row => row with { LiveRows = 5, EstimatedRows = 9 }).Rows.Should().Be(5);
        Table(orders, row => row with { LiveRows = null, EstimatedRows = 9 }).Rows.Should().Be(9);
        Table(orders, row => row with { LiveRows = null, EstimatedRows = -1 }).Rows.Should().Be(0);
    }

    [Fact]
    public void The_row_key_is_schema_dot_table()
    {
        new HealthTableRow { Schema = "s", Table = "t" }.Key.Should().Be("s.t");
    }
}
