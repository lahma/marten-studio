using MartenStudio.Components.Pages.Documents;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The links the Rows tab and row detail build and read back, the text they put on the clipboard, and what
/// the tab remembers between pages.
/// </summary>
public class RowLinksTests
{
    private static readonly MartenStudioOptions Options = new();

    private static readonly StudioScope Scope = new("default", "localhost.marten", "acme");

    // ----------------------------------------------------------------------------------------------
    // Links
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_rows_link_opens_the_rows_tab_with_the_filter_and_the_scope()
    {
        string url = DatabaseLinks.ToRows(Options, Scope, "quartz", "qrtz_triggers", "trigger_state = WAITING");

        url.Should().StartWith("database/object?schema=quartz&name=qrtz_triggers&tab=rows&q=");
        Query(url, "q").Should().Be("trigger_state = WAITING");
        Query(url, "store").Should().Be("default");

        DatabaseLinks.ToRows(Options, Scope, "quartz", "qrtz_triggers").Should().NotContain("q=", "no filter is every row");
    }

    [Theory]
    [InlineData("a|b")]
    [InlineData("a,b|c\\d|plain")]
    public void The_chosen_columns_travel_as_one_parameter_and_come_back_as_written(string names)
    {
        string[] columns = names.Split('|');

        DatabaseLinks.ParseColumns(DatabaseLinks.FormatColumns(columns)).Should().Equal(columns);
    }

    [Fact]
    public void Another_stores_document_table_links_to_that_store_with_no_database_or_tenant()
    {
        string? href = DatabaseLinks.ToWhereItLives(
            Options,
            Scope,
            new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "billing", "invoice"));

        href.Should().StartWith("documents/invoice");
        Query(href!, "store").Should().Be("billing", "the alias means something in the store that declares it, not in this one");
        Query(href!, "db").Should().BeNull("the database is this store's; the page it lands on settles its own");
        Query(href!, "tenant").Should().BeNull();

        string? streams = DatabaseLinks.ToWhereItLives(Options, Scope, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenEventStore, "billing"));
        Query(streams!, "store").Should().Be("billing");
        Query(streams!, "db").Should().BeNull();

        string? own = DatabaseLinks.ToWhereItLives(Options, Scope, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "DEFAULT", "customer"));
        Query(own!, "db").Should().Be("localhost.marten", "this store's own object keeps the whole scope, whatever the key's case");
        Query(own!, "tenant").Should().Be("acme");
    }

    [Fact]
    public void A_return_link_into_the_object_page_is_kept_and_one_anywhere_else_is_not()
    {
        SafeReturnLink.Sanitize("database/object?schema=quartz&name=qrtz_triggers&tab=rows", Options, DatabaseLinks.ObjectRoute)
            .Should().Be("database/object?schema=quartz&name=qrtz_triggers&tab=rows");
        SafeReturnLink.Sanitize("marten/database/object?schema=quartz", Options, DatabaseLinks.ObjectRoute).Should().NotBeNull();

        SafeReturnLink.Sanitize("documents/customer", Options, DatabaseLinks.ObjectRoute).Should().BeNull();
        SafeReturnLink.Sanitize("javascript:alert(1)", Options, DatabaseLinks.ObjectRoute).Should().BeNull();
        SafeReturnLink.Sanitize("//evil.example/database/object", Options, DatabaseLinks.ObjectRoute).Should().BeNull();

        SafeReturnLink.Sanitize("documents/customer", Options).Should().Be("documents/customer", "the documents list's rule is unchanged");
    }

    // ----------------------------------------------------------------------------------------------
    // Clipboard text
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_where_clause_quotes_names_and_doubles_the_quotes_in_values()
    {
        SqlLiteralText.WhereClause([new("Name", "O'Brien"), new("id", "7"), new("gone", null)])
            .Should().Be("where \"Name\" = 'O''Brien'\n  and \"id\" = '7'\n  and \"gone\" is null");

        SqlLiteralText.WhereClause([new("bad\"name", "1")]).Should().BeNull("a name that cannot be quoted has no clause");
        SqlLiteralText.WhereClause([]).Should().BeNull();
        SqlLiteralText.Literal("a\\b").Should().Be("'a\\b'", "a backslash is an ordinary character in a standard string");
    }

    [Fact]
    public void A_row_statement_selects_the_row_by_its_key()
    {
        SqlLiteralText.RowStatement("quartz", "qrtz_triggers", [new("sched_name", "S"), new("trigger_name", "t1")])
            .Should().Be("select * from \"quartz\".\"qrtz_triggers\"\nwhere \"sched_name\" = 'S'\n  and \"trigger_name\" = 't1'");
        SqlLiteralText.RowStatement("quartz", "bad\"name", [new("a", "1")]).Should().BeNull();
    }

    // ----------------------------------------------------------------------------------------------
    // What the tab remembers
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_anyway_answer_is_for_one_filter_and_sort_only()
    {
        var state = new DatabaseBrowserState();

        state.AcceptRead("quartz", "qrtz_triggers", "calendar_name ~ x", null, null);

        state.HasAcceptedRead("quartz", "qrtz_triggers", "calendar_name ~ x", null, null).Should().BeTrue();
        state.HasAcceptedRead("quartz", "qrtz_triggers", "calendar_name ~ y", null, null).Should().BeFalse();
        state.HasAcceptedRead("quartz", "qrtz_triggers", "calendar_name ~ x", "next_fire_time", "asc").Should().BeFalse();
        state.HasAcceptedRead("quartz", "qrtz_job_details", "calendar_name ~ x", null, null).Should().BeFalse();
    }

    [Fact]
    public void Neighbours_are_found_by_key_whatever_order_the_link_wrote_it_in()
    {
        var state = new DatabaseBrowserState();
        List<IReadOnlyList<KeyValuePair<string, string>>> keys =
        [
            [new("a", "1"), new("b", "x")],
            [new("a", "1"), new("b", "y")],
            [new("a", "2"), new("b", "x")],
        ];

        state.RememberPage("s", "t", keys, "database/object?schema=s&name=t&tab=rows");

        (IReadOnlyList<KeyValuePair<string, string>>? previous, IReadOnlyList<KeyValuePair<string, string>>? next) =
            state.Neighbours("s", "t", [new("b", "y"), new("a", "1")]);

        previous.Should().BeSameAs(keys[0]);
        next.Should().BeSameAs(keys[2]);

        state.Neighbours("s", "other", [new("a", "1"), new("b", "y")]).Should().Be(((IReadOnlyList<KeyValuePair<string, string>>?) null, (IReadOnlyList<KeyValuePair<string, string>>?) null));
        state.Neighbours("s", "t", [new("a", "9"), new("b", "y")]).Previous.Should().BeNull();
    }

    [Fact]
    public void The_cursor_history_is_one_per_relation()
    {
        var state = new DatabaseBrowserState();

        state.CursorHistory("s", "t").Add("c1");

        state.CursorHistory("s", "t").Should().Equal(["c1"]);
        state.CursorHistory("s", "u").Should().BeEmpty();
    }

    private static string? Query(string url, string name)
    {
        int start = url.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(start < 0 ? string.Empty : url[start..]);

        return query.TryGetValue(name, out StringValues value) ? value.ToString() : null;
    }
}
