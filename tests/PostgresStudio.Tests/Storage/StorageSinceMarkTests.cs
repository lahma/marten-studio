using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Storage;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Storage;

/// <summary>
/// The Storage page counted from a mark (1.2-H-A, X6): the database's size change, each named table's size and rows written, a
/// withheld table never marked or counted, one table's counters going down, and the statistics window beginning after the mark.
/// </summary>
public class StorageSinceMarkTests
{
    private static readonly DateTimeOffset MarkAt = new(2026, 10, 26, 14, 2, 5, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = MarkAt.AddMinutes(30);

    private static StorageTableView Named(string name, long total, long? inserts = 0, long? updates = 0, long? deletes = 0) =>
        new("public", name, "/postgres/database/" + name, null, null, total, total, 0, 0, 1_000, 0.1, null, null, null)
        {
            Inserts = inserts,
            Updates = updates,
            Deletes = deletes,
        };

    /// <summary>A withheld table: no name, and - as the service builds it - no counters either.</summary>
    private static StorageTableView Withheld(long total) =>
        new(null, null, null, null, null, total, total, 0, 0, 1_000, 0.1, null, null, null);

    private static StorageView View(DateTimeOffset at, long? database, params StorageTableView[] tables) => new()
    {
        MeasuredAt = at,
        DatabaseBytes = database,
        Window = Window(MarkAt.AddDays(-1)),
        Tables = tables,
    };

    private static StatisticsWindow Window(DateTimeOffset since) => new()
    {
        MeasuredAt = Later,
        Since = since,
        Basis = StatisticsBasis.ServerReset,
        Reason = StatisticsResetReason.ManualReset,
        SharedView = "pg_stat_checkpointer",
    };

    [Fact]
    public void A_mark_holds_the_named_tables_numbers_and_never_a_withheld_ones()
    {
        // The withheld row carries counters here on purpose: the mark must still leave it out.
        StorageTableView secret = Withheld(5L << 20) with { Inserts = 999, Updates = 999, Deletes = 999 };

        StorageMark mark = StorageSinceMark.Take(View(MarkAt, 400L << 20, Named("orders", 100L << 20, 10, 20, 30), secret))!;

        mark.At.Should().Be(MarkAt);
        mark.DatabaseBytes.Should().Be(400L << 20);
        mark.Tables.Keys.Should().Equal("public.orders");
        mark.Tables["public.orders"].Should().Be(new StorageMarkRow(100L << 20, 10, 20, 30));
        typeof(StorageMark).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(string));
    }

    [Fact]
    public void A_refused_view_has_nothing_to_remember()
    {
        StorageSinceMark.Take(StorageView.Refused(DatabaseRefusal.StorePolicy, "no")).Should().BeNull();
        StorageSinceMark.Take(new StorageView()).Should().BeNull("a view with no read time has no moment to count from");
    }

    [Fact]
    public void Each_named_table_shows_its_size_change_and_the_rows_written_since_the_mark()
    {
        StorageMark mark = StorageSinceMark.Take(View(MarkAt, 400L << 20, Named("orders", 100L << 20, 1_000, 200, 100)))!;
        StorageView now = View(Later, 434L << 20, Named("orders", 112L << 20, 3_000, 500, 200));

        StorageSince since = StorageSinceMark.Apply(now, mark);

        since.Invalid.Should().BeNull();
        since.DatabaseChange.Should().Be("+34.0 MiB");
        since.Rows.Should().ContainSingle();
        since.Rows[0].Text.Should().Be("+12.0 MiB · 2,400 rows written");
        since.Rows[0].Title.Should().Be("2,000 inserted, 300 updated, 100 deleted since your mark");
    }

    [Fact]
    public void A_size_that_has_not_moved_says_so_and_a_shrunk_one_has_a_sign()
    {
        StorageMark mark = StorageSinceMark.Take(View(MarkAt, 400L << 20, Named("orders", 100L << 20), Named("audit", 50L << 20)))!;
        StorageView now = View(Later, 380L << 20, Named("orders", 100L << 20, 5), Named("audit", 45L << 20));

        StorageSince since = StorageSinceMark.Apply(now, mark);

        since.DatabaseChange.Should().Be("-20.0 MiB");
        since.Rows[0].Text.Should().Be("no size change counted yet · 5 rows written");
        since.Rows[1].Text.Should().Be("-5.0 MiB · no rows written");

        StorageSinceMark.Apply(View(Later, 400L << 20, Named("orders", 100L << 20)), mark).DatabaseChange.Should().Be("no change");
    }

    [Fact]
    public void A_withheld_table_is_a_dash_and_a_table_the_mark_lacked_is_not_listed_at_it()
    {
        StorageMark mark = StorageSinceMark.Take(View(MarkAt, 400L << 20, Named("orders", 100L << 20), Withheld(5L << 20)))!;
        StorageView now = View(Later, 400L << 20, Withheld(5L << 20) with { Inserts = 12 }, Named("orders", 100L << 20), Named("newcomer", 1L << 20));

        StorageSince since = StorageSinceMark.Apply(now, mark);

        since.Rows.Select(static x => x.Text).Should().Equal("–", "no size change counted yet · no rows written", "not listed at your mark");
        since.Rows[0].Title.Should().BeNull();
    }

    [Fact]
    public void One_tables_counters_going_down_is_shown_as_reset_for_that_table_alone()
    {
        StorageMark mark = StorageSinceMark.Take(View(MarkAt, 400L << 20, Named("orders", 100L << 20, 5_000, 0, 0), Named("audit", 10L << 20, 10, 0, 0)))!;
        StorageView now = View(Later, 400L << 20, Named("orders", 101L << 20, 100, 0, 0), Named("audit", 10L << 20, 40, 0, 0));

        StorageSince since = StorageSinceMark.Apply(now, mark);

        since.Rows[0].Text.Should().Be("+1.0 MiB · counters reset since your mark");
        since.Rows[1].Text.Should().Be("no size change counted yet · 30 rows written");
    }

    [Fact]
    public void A_window_that_began_after_the_mark_ends_it()
    {
        StorageMark mark = StorageSinceMark.Take(View(MarkAt, 400L << 20, Named("orders", 100L << 20)))!;

        StorageSince since = StorageSinceMark.Apply(View(Later, 400L << 20, Named("orders", 100L << 20)) with { Window = Window(MarkAt.AddMinutes(5)) }, mark);

        since.Invalid.Should().StartWith("the statistics began again at ");
        since.Rows.Should().BeEmpty("nothing is counted from a mark that no longer holds");
        since.DatabaseChange.Should().BeNull();

        StorageSinceMark.Apply(View(Later, 400L << 20, Named("orders", 100L << 20)) with { Window = Window(MarkAt) }, mark).Invalid
            .Should().BeNull("a window that began at the mark's own moment is the mark's own");
    }

    [Fact]
    public void A_window_that_could_not_be_read_still_counts_with_a_note()
    {
        StorageMark mark = StorageSinceMark.Take(View(MarkAt, 400L << 20, Named("orders", 100L << 20)))!;

        StorageSince since = StorageSinceMark.Apply(View(Later, 400L << 20, Named("orders", 100L << 20)) with { Window = null }, mark);

        since.Invalid.Should().BeNull();
        since.Note.Should().Be("Whether the counters were reset since your mark could not be read; a counter that went down is shown as reset.");
        since.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task The_service_gives_a_table_in_a_withheld_schema_no_counters_at_all()
    {
        StorageFacts facts = StorageFacts.Build(
            "default",
            "app",
            Window(MarkAt.AddDays(-1)),
            new StorageDatabaseRow(64L << 20, 60L << 20, 8192, 170_011),
            StatementsTestFacts.Tables,
            [
                new StorageTableRow { Schema = "public", Table = "orders", HeapBytes = 8L << 20, Inserts = 7, Updates = 8, Deletes = 9 },
                new StorageTableRow { Schema = "hr", Table = "salaries", HeapBytes = 4L << 20, Inserts = 70, Updates = 80, Deletes = 90 },
            ]);

        StorageService service = new(
            new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
            PostgresStudioDescriptor.Instance,
            authorization: null!,
            resolver: null!,
            facts: null!,
            new StudioLogThrottle(TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageService>.Instance);

        StorageView view = await service.ShowAsync(facts, new StudioScope("default", "app", null), static () => Task.FromResult<DatabaseGate?>(null));

        StorageTableView named = view.Tables.Single(static x => !x.Withheld);
        (named.Inserts, named.Updates, named.Deletes).Should().Be((7L, 8L, 9L));

        StorageTableView withheld = view.Tables.Single(static x => x.Withheld);
        (withheld.Inserts, withheld.Updates, withheld.Deletes).Should().Be(((long?)null, (long?)null, (long?)null));

        StorageSinceMark.Take(view)!.Tables.Keys.Should().Equal("public.orders");
    }
}
