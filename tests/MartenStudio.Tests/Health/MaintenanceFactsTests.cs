using PostgresStudio.Components.Pages.Maintenance;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Maintenance;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The Maintenance page's pure parts (W5-V1): its facts are the sweep's own reads, a table's row and badges, the ETA from
/// T16's samples, the bars, the bloat measure's parse and its one-per-database flight.
/// </summary>
public class MaintenanceFactsTests
{
    [Fact]
    public void The_facts_are_the_sweep_s_own_reads_and_its_T14_result_never_a_read_of_their_own()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IReadOnlyList<TableFacts> tables = [Table(orders)];
        MaintenanceWorkersRow workers = new() { MaxWorkers = 5 };
        IReadOnlyList<MaintenanceProgressRow> running = [new MaintenanceProgressRow { Kind = "vacuum", ProcessId = 1 }];

        HealthSweep sweep = new("default", "db", HealthSweepKind.Regular, Now, TimeSpan.FromMilliseconds(5), [])
        {
            Shared = new Dictionary<string, object>
            {
                [HealthTables.TablesKey] = tables,
                [MaintenanceQueries.WorkersKey] = workers,
                [MaintenanceQueries.ProgressKey] = running,
            },
        };

        MaintenanceFacts facts = MaintenanceFactsReader.From(sweep, HealthHistoryView.Empty);

        facts.MeasuredAt.Should().Be(Now);
        facts.Tables.Should().BeSameAs(tables);
        facts.Workers.Should().BeSameAs(workers);
        facts.Running.Should().BeSameAs(running);
        facts.Wraparound.Should().BeNull("the sweep did not finish that read, and the facts never read it again");
        facts.Horizon.Should().BeNull();
    }

    [Fact]
    public void A_deeper_or_statement_sweep_keeps_no_shared_reads_for_a_page()
    {
        HealthSweep sweep = new("default", "db", HealthSweepKind.Statements, Now, TimeSpan.Zero, []);

        sweep.Shared.Should().BeEmpty();
        sweep.TryShared(HealthTables.TablesKey, out IReadOnlyList<TableFacts>? _).Should().BeFalse();
    }

    [Fact]
    public void A_table_past_twice_its_trigger_is_behind_and_due_and_ordered_by_how_far_past_it_is()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            StorageOptions = ["autovacuum_vacuum_scale_factor=0.01", "fillfactor=90"],
            ToastStorageOptions = ["autovacuum_enabled=false"],
            ToastAutovacuumOffLeaves = 1,
            Vacuum = Unit(40_000, 10_050, Now.AddDays(-3)),
            Analyze = Unit(100, 1_050, Now.AddHours(-1)),
            VacuumCount = 2,
            AutovacuumCount = 7,
        });

        MaintenanceTableView view = MaintenanceService.ViewOf(table, Now);

        view.Due.Should().BeTrue();
        view.Behind.Should().BeTrue();
        view.Off.Should().Be("switched off for its TOAST table");
        view.Overrides.Should().Equal("autovacuum_vacuum_scale_factor=0.01", "toast.autovacuum_enabled=false");
        view.Vacuums.Should().Be(9);
        view.Measurable.Should().BeTrue();
        view.Pressure.Should().BeApproximately(40_000 / 10_050d, 0.0001);
    }

    [Fact]
    public void A_partitioned_table_is_measured_per_partition_and_its_switched_off_partitions_counted()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { Kind = "p", PartitionLeaves = 4, AutovacuumOffLeaves = 2 });

        MaintenanceTableView view = MaintenanceService.ViewOf(table, Now);

        view.Measurable.Should().BeFalse();
        view.Off.Should().Be("switched off for 2 partitions");
        view.Partitions.Should().Be(4);
    }

    [Fact]
    public void An_ETA_is_the_rate_between_T16_s_samples_of_the_command_s_blocks_never_a_guess()
    {
        MaintenanceProgressRow row = new() { Kind = "vacuum", ProcessId = 42, RelationId = 16_384, BlocksDone = 6_000, BlocksTotal = 10_000 };
        string series = AutovacuumWorkersCheck.ProgressSeriesOf(row)!;

        // 4,000 blocks in the last two minutes: 2,000 a minute, and 4,000 to go.
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(2), [(series, 2_000)]),
            (TimeSpan.Zero, [(series, 6_000)]));

        MaintenanceService.Eta(row, history).Should().BeCloseTo(TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(1));
        MaintenanceService.Eta(row, HealthHistoryView.Empty).Should().BeNull("one sample is no rate");
        MaintenanceService.Eta(row with { BlocksDone = 10_000 }, history).Should().BeNull("a phase through has nothing left");
    }

    [Theory]
    [InlineData(500, 1_000, 25, "ms-maint-bar")]
    [InlineData(1_500, 1_000, 75, "ms-maint-bar ms-maint-bar-due")]
    [InlineData(5_000, 1_000, 100, "ms-maint-bar ms-maint-bar-behind")]
    public void A_bar_is_the_counter_against_twice_its_trigger_with_the_trigger_at_the_middle(long counter, double trigger, double fill, string css)
    {
        AutovacuumUnit unit = Unit(counter, trigger);

        MaintenancePresentation.Fill(unit).Should().Be(fill);
        MaintenancePresentation.BarClass(unit).Should().Be(css);
        MaintenancePresentation.Against(unit).Should().Be(TableText.Count(counter) + " of " + TableText.Count(trigger));
    }

    [Fact]
    public void The_measure_s_two_json_values_parse_into_one_row_with_each_index_s_own_measure()
    {
        BloatMeasureRow row = MaintenanceQueries.ParseBloat(
            """{"table_len": 8192000, "scanned_percent": 12.5, "approx_tuple_count": 9000, "approx_tuple_len": 1, "approx_tuple_percent": 70, "dead_tuple_count": 1000, "dead_tuple_len": 1, "dead_tuple_percent": 9.5, "approx_free_space": 819200, "approx_free_percent": 10}""",
            """[{"name": "pk", "am": "btree", "bytes": 245760, "measure": {"avg_leaf_density": 45.2, "leaf_fragmentation": 3}}, {"name": "gin_data", "am": "gin", "bytes": 81920, "measure": {"version": 2, "pending_pages": 7, "pending_tuples": 70}}, {"name": "brin_ts", "am": "brin", "bytes": 8192, "measure": null}]""");

        row.TableBytes.Should().Be(8_192_000);
        row.DeadTuples.Should().Be(1_000);
        row.DeadPercent.Should().Be(9.5);
        row.FreePercent.Should().Be(10);
        row.Indexes.Should().HaveCount(3);
        row.Indexes[0].LeafDensity.Should().Be(45.2);
        row.Indexes[1].PendingPages.Should().Be(7);
        row.Indexes[2].LeafDensity.Should().BeNull("BRIN has no pgstattuple measure, and is listed with its size alone");
    }

    [Fact]
    public void The_measure_names_the_extension_s_functions_by_the_schema_it_is_installed_in_quoted()
    {
        string sql = MaintenanceQueries.BloatSql("Ext Schema");

        sql.Should().Contain("\"Ext Schema\".\"pgstattuple_approx\"").And.Contain("@schema").And.Contain("@table");
        FluentActions.Invoking(static () => MaintenanceQueries.BloatSql("ext\"s")).Should().Throw<ArgumentException>("a name with a quote is never spliced");
        sql.Should().NotContain("pg_relation_size", "a size is relpages, never a function that waits behind a migration");
    }

    /// <summary>
    /// The measure is bounded (W5-V1 review): one statement, the five largest indexes and a count of the rest, and the exact
    /// <c>pgstattuple</c> only for an extension older than 1.3, which has no <c>pgstattuple_approx</c>.
    /// </summary>
    [Fact]
    public void The_measure_reads_the_five_largest_indexes_and_the_approximate_heap_where_the_extension_has_it()
    {
        string approximate = MaintenanceQueries.BloatSql("public");
        approximate.Should().Contain("\"public\".\"pgstattuple_approx\"").And.Contain("order by i.relpages desc").And.Contain("limit 5");

        string exact = MaintenanceQueries.BloatSql("public", approximate: false);
        exact.Should().Contain("\"public\".\"pgstattuple\"(").And.NotContain("pgstattuple_approx");

        MaintenanceQueries.HasApproximate("1.5").Should().BeTrue();
        MaintenanceQueries.HasApproximate("1.3").Should().BeTrue();
        MaintenanceQueries.HasApproximate("1.2").Should().BeFalse();

        BloatMeasureRow row = MaintenanceQueries.ParseBloat(
            """{"table_len": 8192, "tuple_count": 90, "tuple_len": 1, "tuple_percent": 70, "dead_tuple_count": 10, "dead_tuple_len": 1, "dead_tuple_percent": 9, "free_space": 800, "free_percent": 10}""",
            "[]");

        row.LiveTuples.Should().Be(90, "the exact function's names are read where the approximate one's are absent");
        row.FreeBytes.Should().Be(800);
        row.ScannedPercent.Should().Be(100, "the exact function reads every page");
    }

    [Fact]
    public async Task One_measure_per_database_at_once_the_same_table_joins_it_and_another_is_refused()
    {
        BloatMeasureFlights flights = new();
        TaskCompletionSource<BloatMeasureResult> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;

        Task<BloatMeasureResult> Measure()
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        }

        Task<BloatMeasureResult>? first = flights.Join("s|db", "public.orders", Measure);
        Task<BloatMeasureResult>? same = flights.Join("s|db", "public.orders", Measure);
        Task<BloatMeasureResult>? other = flights.Join("s|db", "public.lines", Measure);
        Task<BloatMeasureResult>? elsewhere = flights.Join("s|db2", "public.lines", () => Task.FromResult(new BloatMeasureResult("public.lines", null, "x", Now)));

        first.Should().NotBeNull();
        same.Should().BeSameAs(first);
        other.Should().BeNull("one measure runs on a database at a time");
        elsewhere.Should().NotBeNull("another database is another flight");

        gate.SetResult(new BloatMeasureResult("public.orders", null, "done", Now));
        (await first!).Refusal.Should().Be("done");
        started.Should().Be(1);

        // Once it is over, the database is free again.
        await Task.Yield();
        SpinWait.SpinUntil(() => flights.Join("s|db", "public.lines", () => Task.FromResult(new BloatMeasureResult("public.lines", null, "next", Now))) is not null, TimeSpan.FromSeconds(5))
            .Should().BeTrue();
    }
}
