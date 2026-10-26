using System.Text.RegularExpressions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Tests.Storage;

/// <summary>
/// The Storage page's facts, with no Postgres (W5-CAP, issue #8 G3): the growth estimate as the issue writes it - null until the
/// window has counted a day and while it looks reset - the largest fifteen of the judged set, each table's split, ratios and
/// share, and the builder held to D27: no size function over a list, nothing deparsed.
/// </summary>
public class StorageFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static StatisticsWindow Window(TimeSpan length, bool looksReset = false) => new()
    {
        MeasuredAt = Now,
        Since = Now - length,
        Basis = StatisticsBasis.ServerReset,
        LooksReset = looksReset,
    };

    private static StorageTableRow Row(string table, long heap, long index = 0, long toast = 0, long rows = 1_000, long? inserts = null, long? deletes = null) => new()
    {
        Schema = "store",
        Table = table,
        HeapBytes = heap,
        IndexBytes = index,
        ToastBytes = toast,
        EstimatedRows = rows,
        Inserts = inserts,
        Deletes = deletes,
    };

    private static readonly StorageDatabaseRow Database = new(DatabaseBytes: 10_000_000, EstimatedBytes: 9_000_000, BlockSize: 8192, ServerVersion: 170_011);

    [Fact]
    public void Growth_is_net_inserts_over_the_windows_days_times_bytes_per_row()
    {
        // 2 days of counters; 3 000 rows in, 1 000 out; 400 bytes a row over 1 000 rows.
        StorageTableFacts table = new(Table("orders"), Row("orders", heap: 300_000, index: 80_000, toast: 20_000, rows: 1_000, inserts: 3_000, deletes: 1_000));

        table.BytesPerRow.Should().Be(400);
        table.GrowthBytesPerDay(Window(TimeSpan.FromDays(2))).Should().BeApproximately(400_000, 0.001, "(3 000 - 1 000) / 2 days x 400 bytes");
    }

    [Fact]
    public void Bytes_per_row_divides_by_at_least_one_row_as_the_issue_writes_it()
    {
        new StorageTableFacts(Table("t"), Row("t", heap: 8_192, rows: -1)).BytesPerRow.Should().Be(8_192, "max(reltuples, 1): never analysed counts as one");
        new StorageTableFacts(Table("t"), Row("t", heap: 8_192, rows: 0)).BytesPerRow.Should().Be(8_192);
    }

    [Theory]
    [InlineData(23.9, false)]
    [InlineData(24.0, true)]
    [InlineData(240.0, true)]
    public void Growth_waits_for_a_day_of_statistics(double hours, bool shown)
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromHours(hours)), Database, Set(Table("orders")),
            [Row("orders", heap: 400_000, inserts: 2_000, deletes: 0)]);

        (facts.GrowthBytesPerDay is not null).Should().Be(shown);
        (facts.LargestTables.Single().GrowthBytesPerDay(facts.Window!) is not null).Should().Be(shown);
    }

    [Fact]
    public void Without_a_window_the_sizes_and_the_tables_stand_and_there_is_no_growth()
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Now, window: null, Database, Set(Table("orders")),
            [Row("orders", heap: 400_000, inserts: 2_000, deletes: 0)]);

        facts.Window.Should().BeNull();
        facts.MeasuredAt.Should().Be(Now);
        facts.GrowthBytesPerDay.Should().BeNull("growth is a rate over the window");
        facts.DatabaseBytes.Should().Be(10_000_000);
        facts.LargestTables.Should().ContainSingle().Which.Table.Table.Should().Be("orders");
    }

    [Fact]
    public void Growth_is_null_while_the_statistics_look_reset_however_long_the_window()
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromDays(9), looksReset: true), Database, Set(Table("orders")),
            [Row("orders", heap: 400_000, inserts: 2_000)]);

        facts.GrowthBytesPerDay.Should().BeNull();
    }

    [Fact]
    public void The_databases_growth_sums_every_judged_table_not_only_the_largest()
    {
        List<StudioTable> tables = [.. Enumerable.Range(0, 20).Select(static i => Table("t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)))];
        List<StorageTableRow> rows =
        [
            .. Enumerable.Range(0, 20).Select(static i => Row(
                "t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), heap: 1_000_000 - i * 10_000, rows: 1_000, inserts: 100, deletes: 0)),
        ];

        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromDays(1)), Database, Set([.. tables]), rows);

        facts.LargestTables.Should().HaveCount(StorageFacts.LargestCount);
        facts.LargestTables[0].Table.Table.Should().Be("t00");
        facts.TableCount.Should().Be(20);
        facts.GrowthBytesPerDay.Should().BeApproximately(rows.Sum(static x => 100d * x.HeapBytes / 1_000), 1, "all twenty, not the fifteen shown");
    }

    [Fact]
    public void The_largest_are_by_heap_indexes_and_toast_together()
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromHours(1)), Database, Set(Table("wide"), Table("toasty")),
        [
            Row("wide", heap: 3_000_000),
            Row("toasty", heap: 100_000, index: 60_000, toast: 16_000_000),
        ]);

        facts.LargestTables.Select(static x => x.Table.Table).Should().Equal("toasty", "wide");
        facts.LargestTables[0].TotalBytes.Should().Be(16_160_000);
    }

    [Fact]
    public void A_row_the_set_does_not_know_is_not_a_table_of_the_page()
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromHours(1)), Database, Set(Table("known")),
            [Row("known", heap: 1), Row("stranger", heap: 999_999)]);

        facts.LargestTables.Select(static x => x.Table.Table).Should().Equal("known");
    }

    [Fact]
    public void Without_a_table_source_the_page_says_why_and_still_has_the_database()
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromHours(1)), Database, set: null, rows: []);

        facts.TablesNotRead.Should().Be(HealthTables.NoSource);
        facts.DatabaseBytes.Should().Be(10_000_000);
        facts.ShareBase.Should().Be(10_000_000);
        facts.SizesAreEstimates.Should().BeTrue();
    }

    [Fact]
    public void A_share_is_of_the_estimate_when_the_size_could_not_be_measured()
    {
        StorageFacts facts = StorageFacts.Build("default", "db", Window(TimeSpan.FromHours(1)), Database with { DatabaseBytes = null }, set: null, rows: []);

        facts.ShareBase.Should().Be(9_000_000);
    }

    [Fact]
    public void Hot_and_new_page_shares_are_of_the_updates_and_null_without_any()
    {
        StorageTableFacts hot = new(Table("t"), Row("t", heap: 1) with { Updates = 2_000, HotUpdates = 884, NewPageUpdates = 1_116 });
        StorageTableFacts none = new(Table("t"), Row("t", heap: 1) with { Updates = 0, HotUpdates = 0 });
        StorageTableFacts fifteen = new(Table("t"), Row("t", heap: 1) with { Updates = 10, HotUpdates = 5, NewPageUpdates = null });

        hot.HotUpdateRatio.Should().BeApproximately(0.442, 0.0001, "B0's fillfactor-70 table: 884 of 2 000 HOT");
        hot.NewPageShare.Should().BeApproximately(0.558, 0.0001);
        none.HotUpdateRatio.Should().BeNull();
        fifteen.NewPageShare.Should().BeNull("n_tup_newpage_upd is 16's");
    }

    [Fact]
    public void Never_analysed_is_reltuples_minus_one()
    {
        new StorageTableFacts(Table("t"), Row("t", heap: 1, rows: -1)).NeverAnalysed.Should().BeTrue();
        new StorageTableFacts(Table("t"), Row("t", heap: 1, rows: 0)).NeverAnalysed.Should().BeFalse();
    }

    /// <summary>Every size function that waits behind a migration's lock (B0 §3, G3).</summary>
    private static readonly Regex LockingSizeFunction = new(
        @"\bpg_(relation_size|total_relation_size|indexes_size|table_size)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Every deparse that opens what it describes, and every per-object function D27 forbids in a list.</summary>
    private static readonly Regex OpensWhatItLists = new(
        @"\b(pg_get_indexdef|pg_get_viewdef|pg_get_constraintdef|pg_get_expr|pg_partition_tree|pg_sequence_last_value)\b|\bpg_sequences\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [Fact]
    public void No_statement_calls_a_size_function_over_a_table_or_deparses_anything()
    {
        string[] statements = [.. StorageQueries.Statements, StatisticsWindowQueries.CheckpointerSql, StatisticsWindowQueries.BgwriterSql];

        statements.Should().HaveCount(4);
        statements.Should().OnlyContain(x => !LockingSizeFunction.IsMatch(x), "a size function waits behind a migration (B0 G3)");
        statements.Should().OnlyContain(x => !OpensWhatItLists.IsMatch(x), "a list locks nothing it lists (D27)");
        statements.Should().OnlyContain(static x => !x.Contains("pg_locks", StringComparison.Ordinal), "nothing here opens a table, so nothing needs the guard");
        StorageQueries.TablesSql.Should().Contain("relpages").And.Contain("reltoastrelid").And.Contain("pg_catalog.pg_inherits");
        StorageQueries.DatabaseSql.Should().Contain("pg_catalog.pg_database_size(", "the one size function, which takes no relation lock (B0 G3)");
    }

    [Theory]
    [InlineData("select pg_total_relation_size(c.oid) from pg_class c", true)]
    [InlineData("select pg_catalog.pg_relation_size(c.oid)", true)]
    [InlineData("select pg_indexes_size(x)", true)]
    [InlineData("select pg_table_size(x)", true)]
    [InlineData("select pg_catalog.pg_database_size(pg_catalog.current_database())", false)]
    [InlineData("select c.relpages * 8192 from pg_class c", false)]
    public void The_size_function_scan_catches_one_and_leaves_pg_database_size_alone(string sql, bool caught)
    {
        LockingSizeFunction.IsMatch(sql).Should().Be(caught);
    }

    [Fact]
    public void Statistics_are_joined_by_the_rows_own_oid_never_through_a_regclass_cast()
    {
        StorageQueries.TablesSql.Should().Contain("s.relid = d.oid").And.NotContain("::regclass").And.NotContain("::pg_catalog.regclass");
        StorageQueries.TablesSql.Should().Contain("->> 'n_tup_newpage_upd'", "16's column, read by name so one statement runs on 14 too");
    }

    [Fact]
    public void The_table_statement_names_no_table_and_binds_two_arrays()
    {
        var query = StorageQueries.Tables([new HealthTableTarget("store", "orders")]);

        query.Sql.Should().NotContain("orders");
        query.ParameterNames.Should().Equal("@schemas", "@tables");
    }

    [Fact]
    public void The_window_reads_the_checkpointer_from_17_and_the_bgwriter_below()
    {
        StatisticsWindowQueries.CheckpointerSql.Should().Contain("pg_catalog.pg_stat_checkpointer").And.NotContain("pg_stat_bgwriter");
        StatisticsWindowQueries.BgwriterSql.Should().Contain("pg_catalog.pg_stat_bgwriter").And.NotContain("pg_stat_checkpointer");
        StatisticsWindowQueries.BgwriterSql.Should().Contain("has_function_privilege('pg_catalog.pg_control_checkpoint()', 'execute')");
        StatisticsWindowQueries.CheckpointerSql.Should().Contain("pg_catalog.pg_is_in_recovery()", "a standby's timeline follows its upstream's");
    }

    private static StudioTable Table(string name) => new("store", name, StudioTableKind.Relational, StudioTableOwnership.Owned);

    private static FakeSet Set(params StudioTable[] tables) => new(tables);

    private sealed class FakeSet(IReadOnlyList<StudioTable> tables) : IStudioTableSet
    {
        public IReadOnlyList<StudioTable> Tables { get; } = tables;

        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) =>
            Tables.FirstOrDefault(x => x.Schema == schema && x.Table == table);

        public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Host;
    }
}
