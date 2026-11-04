using PostgresStudio.Services.Health;
using PostgresStudio.Services.Maintenance;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The Maintenance page counted from a mark (1.2-H-A, X6): the vacuums and analyzes each table has had since it - and on
/// PostgreSQL 18 the time vacuuming - a table the mark lacked, one whose counters went down, and the statistics window beginning
/// after the mark.
/// </summary>
public class MaintenanceSinceMarkTests
{
    private static readonly DateTimeOffset MarkAt = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = MarkAt.AddHours(1);

    private static MaintenanceTableView Table(string name, long? vacuums, long? analyzes, double? vacuumMs = null, double? analyzeMs = null) => new()
    {
        Key = "public." + name,
        Schema = "public",
        Table = name,
        Description = "the table public." + name,
        Vacuums = vacuums,
        Analyzes = analyzes,
        TotalVacuumMilliseconds = vacuumMs,
        TotalAnalyzeMilliseconds = analyzeMs,
    };

    private static StatisticsWindow Window(DateTimeOffset since) => new()
    {
        MeasuredAt = Later,
        Since = since,
        Basis = StatisticsBasis.ServerReset,
        Reason = StatisticsResetReason.Crash,
        SharedView = "pg_stat_checkpointer",
    };

    private static MaintenanceView View(DateTimeOffset at, params MaintenanceTableView[] tables) => new()
    {
        MeasuredAt = at,
        TablesRead = true,
        ServerVersion = 180_006,
        Tables = tables,
        Statistics = Window(MarkAt.AddDays(-2)),
    };

    [Fact]
    public void Each_table_shows_the_vacuums_and_analyzes_it_has_had_since_the_mark()
    {
        MaintenanceMark mark = MaintenanceSinceMark.Take(View(MarkAt, Table("orders", 10, 4, 5_000, 1_000), Table("audit", 3, 3)))!;
        MaintenanceView now = View(Later, Table("orders", 12, 5, 6_200, 1_100), Table("audit", 3, 3));

        MaintenanceSince since = MaintenanceSinceMark.Apply(now, mark, withTimes: true);

        since.Invalid.Should().BeNull();
        since.Rows["public.orders"].Should().Be("2 vacuums, 1 analyze · 1.2 s vacuuming");
        since.Rows["public.audit"].Should().Be("none yet");
    }

    [Fact]
    public void The_time_vacuuming_is_only_for_a_server_that_counts_it()
    {
        MaintenanceMark mark = MaintenanceSinceMark.Take(View(MarkAt, Table("orders", 10, 4, 5_000, 1_000)))!;
        MaintenanceView now = View(Later, Table("orders", 11, 4, 6_200, 1_000));

        MaintenanceSinceMark.Apply(now, mark, withTimes: false).Rows["public.orders"].Should().Be("1 vacuum, 0 analyzes");
    }

    [Fact]
    public void A_table_the_mark_lacked_or_whose_counters_fell_or_that_counts_nothing_says_so()
    {
        MaintenanceMark mark = MaintenanceSinceMark.Take(View(MarkAt, Table("orders", 10, 4), Table("blank", null, null)))!;
        MaintenanceView now = View(Later, Table("orders", 2, 1), Table("newcomer", 1, 1), Table("blank", null, null));

        MaintenanceSince since = MaintenanceSinceMark.Apply(now, mark, withTimes: false);

        since.Rows["public.orders"].Should().Be("reset since your mark");
        since.Rows["public.newcomer"].Should().Be("not listed at your mark");
        since.Rows["public.blank"].Should().Be("–");
    }

    [Fact]
    public void A_mark_holds_numbers_only_and_only_of_tables_the_page_shows()
    {
        MaintenanceMark mark = MaintenanceSinceMark.Take(View(MarkAt, Table("orders", 10, 4, 5_000, 1_000)))!;

        mark.Tables.Keys.Should().Equal("public.orders");
        mark.Tables["public.orders"].Should().Be(new MaintenanceMarkRow(10, 4, 5_000, 1_000));
        typeof(MaintenanceMark).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(string));

        MaintenanceSinceMark.Take(MaintenanceView.Refused(MaintenanceRefusal.StorePolicy, "no")).Should().BeNull();
        MaintenanceSinceMark.Take(new MaintenanceView { MeasuredAt = MarkAt, TablesRead = false }).Should().BeNull("the table read did not finish");
    }

    [Fact]
    public void A_window_that_began_after_the_mark_ends_it_and_one_that_could_not_be_read_notes_it()
    {
        MaintenanceMark mark = MaintenanceSinceMark.Take(View(MarkAt, Table("orders", 10, 4)))!;

        MaintenanceSince ended = MaintenanceSinceMark.Apply(View(Later, Table("orders", 11, 4)) with { Statistics = Window(MarkAt.AddMinutes(1)) }, mark, withTimes: false);
        ended.Invalid.Should().StartWith("the statistics began again at ");
        ended.Rows.Should().BeEmpty();

        MaintenanceSince unread = MaintenanceSinceMark.Apply(View(Later, Table("orders", 11, 4)) with { Statistics = null }, mark, withTimes: false);
        unread.Invalid.Should().BeNull();
        unread.Note.Should().Contain("could not be read");
        unread.Rows["public.orders"].Should().Be("1 vacuum, 0 analyzes");
    }
}
