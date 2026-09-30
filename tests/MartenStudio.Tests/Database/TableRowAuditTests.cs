using MartenStudio.Services.Database;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Database;

/// <summary>
/// DB-3-fix F1, the rule without a database: a row read is recorded by what it asks for - its filter, its
/// sort and its walk - never by where in the walk it starts, and per store, database and relation.
/// </summary>
public class TableRowAuditTests
{
    private static TableRowPlan Plan(string? sort = null, bool descending = false, TableRowPagingPreference paging = TableRowPagingPreference.Keyset) =>
        TableRowService.Plan(
            TableRowTestData.Relation(), TableRowTestData.QuartzColumns, TableRowTestData.QuartzKey, paging, sort, descending, tidRange: true);

    private static RowFilterParse Filter(string? text) => RowFilterGrammar.Parse(text, TableRowTestData.QuartzColumns);

    private static string Signature(TableRowRequest request) =>
        TableRowService.AuditSignature(
            request,
            Filter(request.Filter),
            Plan(request.SortColumn, request.Direction == SortDirection.Descending, request.Paging));

    [Fact]
    public void Where_a_read_starts_how_many_rows_it_asks_for_and_which_columns_it_shows_are_not_its_signature()
    {
        var first = new TableRowRequest { Filter = "sched_name = nightly", SortColumn = "next_fire_time" };

        string signature = Signature(first);

        Signature(first with { Cursor = new TableRowCursor("next_fire_time", false, null, [""]) })
            .Should().Be(signature, "a cursor in a pasted URL is a position, not a different read");
        Signature(first with { Cursor = new TableRowCursor("next_fire_time", false, "5", ["a", "b", "c"]) })
            .Should().Be(signature, "a page turn repeats the opening");
        Signature(first with { PageSize = 7, Columns = ["sched_name"], RunAnyway = true }).Should().Be(signature);

        first.IsFirstPage.Should().BeTrue();
        (first with { Offset = 1 }).IsFirstPage.Should().BeFalse("which is exactly why the audit no longer asks");
    }

    [Fact]
    public void The_filter_the_sort_the_direction_and_the_walk_each_change_the_signature()
    {
        var baseline = new TableRowRequest { Filter = "sched_name = nightly", SortColumn = "next_fire_time" };
        string signature = Signature(baseline);

        Signature(baseline with { Filter = "sched_name = weekly" }).Should().NotBe(signature, "a filter's values are what it asks");
        Signature(baseline with { Filter = null }).Should().NotBe(signature);
        Signature(baseline with { SortColumn = "trigger_name" }).Should().NotBe(signature);
        Signature(baseline with { Direction = SortDirection.Descending }).Should().NotBe(signature);
        Signature(baseline with { Paging = TableRowPagingPreference.Offset }).Should().NotBe(signature, "offset paging walks differently");
    }

    [Fact]
    public void The_first_contact_is_recorded_and_the_same_signature_again_is_not()
    {
        var ledger = new TableRowAuditLedger();
        string key = TableRowAuditLedger.Key("default", "localhost.app", "legacy", "orders", "rows");

        ledger.TryRecord(key, "a", out string? previous).Should().BeTrue("whatever page it started on, nothing was recorded yet");
        previous.Should().BeNull();

        ledger.TryRecord(key, "a", out _).Should().BeFalse("a page turn or a refresh");
        ledger.TryRecord(key, "b", out previous).Should().BeTrue("a filter or sort change");
        previous.Should().Be("a");

        ledger.TryRecord(key, "a", out previous).Should().BeTrue("going back to the first filter is a change again");
        previous.Should().Be("b");
    }

    [Fact]
    public void The_same_relation_in_another_database_or_store_or_as_a_count_is_its_own_first_contact()
    {
        var ledger = new TableRowAuditLedger();

        ledger.TryRecord(TableRowAuditLedger.Key("default", "server.a", "legacy", "orders", "rows"), "a", out _).Should().BeTrue();

        ledger.TryRecord(TableRowAuditLedger.Key("default", "server.b", "legacy", "orders", "rows"), "a", out string? previous)
            .Should().BeTrue("legacy.orders in another database is other rows");
        previous.Should().BeNull();

        ledger.TryRecord(TableRowAuditLedger.Key("reporting", "server.a", "legacy", "orders", "rows"), "a", out _)
            .Should().BeTrue("and in another store");
        ledger.TryRecord(TableRowAuditLedger.Key("default", "server.a", "legacy", "orders", "count"), "a", out _)
            .Should().BeTrue("a count is its own kind of read");
        ledger.TryRecord(TableRowAuditLedger.Key("default", "server.a", "legacy", "Orders", "rows"), "a", out _)
            .Should().BeTrue("a relation named in another case is another relation");

        ledger.TryRecord(TableRowAuditLedger.Key("default", "server.a", "legacy", "orders", "rows"), "a", out _).Should().BeFalse();
    }

    [Fact]
    public void A_full_ledger_forgets_and_can_only_ever_record_again()
    {
        var ledger = new TableRowAuditLedger();
        string first = TableRowAuditLedger.Key("default", "db", "legacy", "t0", "rows");

        for (int i = 0; i < TableRowAuditLedger.Capacity; i++)
        {
            ledger.TryRecord(TableRowAuditLedger.Key("default", "db", "legacy", "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), "rows"), "a", out _)
                .Should().BeTrue();
        }

        ledger.Count.Should().Be(TableRowAuditLedger.Capacity);
        ledger.TryRecord(first, "a", out _).Should().BeFalse("still remembered while the ledger has room");

        ledger.TryRecord(TableRowAuditLedger.Key("default", "db", "legacy", "one-more", "rows"), "a", out _).Should().BeTrue();
        ledger.Count.Should().Be(1, "past its capacity it starts again");

        ledger.TryRecord(first, "a", out _).Should().BeTrue("a forgotten opening is recorded again - never skipped");
    }
}
