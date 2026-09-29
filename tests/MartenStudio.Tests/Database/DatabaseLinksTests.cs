using MartenStudio.Components;
using MartenStudio.Services;
using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The URL is the whole state of the database browser (D9), so these are the tests that keep a pasted link
/// reopening the same screen - with the names Postgres allows and a router does not: quotes, spaces,
/// slashes, Unicode, and a key column whose own name starts with the prefix the link uses.
/// </summary>
public class DatabaseLinksTests
{
    private static readonly MartenStudioOptions Options = new();

    private static readonly StudioScope Scope = new("default", "localhost.marten", "acme");

    public static TheoryData<string, string> OddNames => new()
    {
        { "quartz", "qrtz_triggers" },
        { "legacy", "bad\"name" },
        { "my schema", "a b/c" },
        { "tëst", "ünïcødé_表" },
        { "we&ird=schema", "q?x#frag+plus%20" },
    };

    [Fact]
    public void The_browser_link_carries_the_schema_kind_owner_filter_and_scope()
    {
        string url = DatabaseLinks.ToBrowser(Options, Scope, "quartz", DatabaseObjectCategory.Views, DatabaseOwnerFilter.Other, "trig");

        url.Should().StartWith("database?");
        url.Should().Contain("schema=quartz").And.Contain("kind=views").And.Contain("owner=other").And.Contain("q=trig");
        url.Should().Contain("store=default").And.Contain("db=localhost.marten").And.Contain("tenant=acme");
    }

    [Fact]
    public void The_browser_link_leaves_out_what_is_the_default_or_blank()
    {
        DatabaseLinks.ToBrowser(Options, null).Should().Be("database");

        string url = DatabaseLinks.ToBrowser(Options, null, schema: null, kind: DatabaseObjectCategory.Tables, owner: DatabaseOwnerFilter.All, nameFilter: "  ");

        url.Should().Be("database?kind=tables", "owner=all is the default and a blank filter is no filter");
    }

    [Theory]
    [MemberData(nameof(OddNames))]
    public void An_object_link_round_trips_any_name(string schema, string name)
    {
        string url = DatabaseLinks.ToObject(Options, Scope, schema, name, DatabaseObjectTab.Keys);

        url.Should().StartWith("database/object?", "the name travels in the query string, never as a segment");

        DatabaseRowLink read = DatabaseLinks.ReadRow(url);
        read.Schema.Should().Be(schema);
        read.Name.Should().Be(name);

        Parameter(url, "tab").Should().Be("keys");
        StudioScopeQuery.Read(url).Should().Be(("default", "localhost.marten", "acme"));
    }

    [Theory]
    [MemberData(nameof(OddNames))]
    public void A_row_link_round_trips_its_key_in_order(string schema, string name)
    {
        KeyValuePair<string, string>[] key =
        [
            new("sched_name", "QRTZ/Scheduler 1"),
            new("key.x", "a column named like the prefix"),
            new("trigger_group", string.Empty),
            new("naïve \"col\"", "v=1&w=2 + 3 % # ?"),
        ];

        string url = DatabaseLinks.ToRow(Options, Scope, schema, name, key);

        url.Should().StartWith("database/row?");

        DatabaseRowLink read = DatabaseLinks.ReadRow("http://localhost/marten/" + url);
        read.Schema.Should().Be(schema);
        read.Name.Should().Be(name);
        Pairs(read.Key).Should().Equal(Pairs(key), "every key column comes back, in order, with its value exactly - an empty one included");

        StudioScopeQuery.Read(url).Should().Be(("default", "localhost.marten", "acme"));
    }

    [Fact]
    public void Each_key_column_is_its_own_readable_parameter()
    {
        string url = DatabaseLinks.ToRow(
            Options,
            null,
            "quartz",
            "qrtz_triggers",
            [new("sched_name", "QRTZ"), new("trigger_name", "nightly")]);

        url.Should().Be("database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QRTZ&key.trigger_name=nightly");
    }

    [Fact]
    public void The_key_prefix_is_stripped_exactly_once()
    {
        string url = DatabaseLinks.ToRow(Options, null, "s", "t", [new("key.x", "1")]);

        url.Should().Contain("key.key.x=1");
        Pairs(DatabaseLinks.ReadKey(url)).Should().Equal(("key.x", "1"));
    }

    [Fact]
    public void A_hand_edited_link_keeps_the_first_value_of_a_repeated_key_column_and_ignores_the_rest()
    {
        IReadOnlyList<KeyValuePair<string, string>> key =
            DatabaseLinks.ReadKey("database/row?schema=s&name=t&key.a=1&key.=x&other=y&key.a=2&key.b=3+4");

        Pairs(key).Should().Equal(("a", "1"), ("b", "3 4"));
    }

    [Fact]
    public void A_url_with_no_query_names_nothing()
    {
        DatabaseRowLink read = DatabaseLinks.ReadRow("database/row");

        read.Schema.Should().BeNull();
        read.Name.Should().BeNull();
        read.Key.Should().BeEmpty();
    }

    [Fact]
    public void The_extra_parameters_ride_along_before_the_scope()
    {
        string url = DatabaseLinks.ToObject(
            Options,
            Scope,
            "quartz",
            "qrtz_triggers",
            DatabaseObjectTab.Rows,
            [new("sort", "next_fire_time"), new("dir", "asc"), new("q", null)]);

        url.Should().Be(
            "database/object?schema=quartz&name=qrtz_triggers&tab=rows&sort=next_fire_time&dir=asc&store=default&db=localhost.marten&tenant=acme");
    }

    [Theory]
    [InlineData("Tables", "tables")]
    [InlineData("Views", "views")]
    [InlineData("Functions", "functions")]
    [InlineData("Triggers", "triggers")]
    [InlineData("Sequences", "sequences")]
    [InlineData("Types", "types")]
    public void Every_kind_round_trips(string name, string token)
    {
        DatabaseObjectCategory category = Enum.Parse<DatabaseObjectCategory>(name);

        DatabaseLinks.KindToken(category).Should().Be(token);
        DatabaseLinks.ParseKind(token).Should().Be(category);
        DatabaseLinks.ParseKind(token.ToUpperInvariant()).Should().Be(category);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("indexes")]
    public void An_unknown_kind_is_the_tables_tab(string? token) =>
        DatabaseLinks.ParseKind(token).Should().Be(DatabaseObjectCategory.Tables);

    [Theory]
    [InlineData("All", "all")]
    [InlineData("Marten", "marten")]
    [InlineData("Other", "other")]
    public void Every_owner_filter_round_trips(string name, string token)
    {
        DatabaseOwnerFilter owner = Enum.Parse<DatabaseOwnerFilter>(name);

        DatabaseLinks.OwnerToken(owner).Should().Be(token);
        DatabaseLinks.ParseOwner(token).Should().Be(owner);
    }

    [Fact]
    public void Every_tab_round_trips_and_an_unknown_one_is_no_tab()
    {
        foreach (DatabaseObjectTab tab in Enum.GetValues<DatabaseObjectTab>())
        {
            DatabaseLinks.ParseTab(DatabaseLinks.TabToken(tab)).Should().Be(tab);
        }

        DatabaseLinks.ParseTab(null).Should().BeNull();
        DatabaseLinks.ParseTab("rowz").Should().BeNull("the page picks its own default rather than guessing");
    }

    [Fact]
    public void A_document_table_lives_in_its_documents_collection()
    {
        string? href = DatabaseLinks.ToWhereItLives(
            Options, Scope, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "customer"));

        href.Should().StartWith("documents/customer?").And.Contain("store=default").And.Contain("tenant=acme");
    }

    [Fact]
    public void The_event_store_lives_in_streams_and_marten_s_bookkeeping_on_the_schema_screen()
    {
        DatabaseLinks.ToWhereItLives(Options, Scope, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenEventStore, "default"))
            .Should().StartWith("events/streams?").And.Contain("store=default");

        DatabaseLinks.ToWhereItLives(Options, Scope, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure, "default"))
            .Should().StartWith("schema?tab=tables&").And.Contain("store=default");
    }

    [Fact]
    public void Relational_data_lives_right_here()
    {
        DatabaseLinks.ToWhereItLives(Options, Scope, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenProjectionOrExtended, "default"))
            .Should().BeNull("a flat-table projection's rows are browsable in the database browser itself");

        DatabaseLinks.ToWhereItLives(Options, Scope, new DatabaseObjectOwnership(DatabaseObjectOwner.Other, RecognisedAs: "Quartz.NET"))
            .Should().BeNull();
    }

    [Fact]
    public void The_console_link_fills_the_editor_with_a_quoted_select()
    {
        string? href = DatabaseLinks.ToQueryConsole(Options, Scope, "quartz", "qrtz_triggers");

        href.Should().StartWith("query?mode=sql&sql=");
        Parameter(href!, "sql").Should().Be("select * from \"quartz\".\"qrtz_triggers\" limit 100");
        StudioScopeQuery.Read(href!).Should().Be(("default", "localhost.marten", "acme"));
    }

    [Fact]
    public void A_name_that_cannot_be_quoted_gets_no_statement_at_all()
    {
        DatabaseLinks.SelectStatement("legacy", "bad\"name").Should().BeNull();
        DatabaseLinks.ToQueryConsole(Options, Scope, "legacy", "bad\"name").Should().BeNull();
        DatabaseLinks.SelectStatement("my schema", "Mixed Case").Should().Be("select * from \"my schema\".\"Mixed Case\" limit 100");
    }

    /// <summary>A key as a list of pairs, which compares in order - a dictionary assertion would not.</summary>
    private static List<(string Column, string Value)> Pairs(IEnumerable<KeyValuePair<string, string>> key) =>
        [.. key.Select(static x => (x.Key, x.Value))];

    /// <summary>One query parameter's decoded value, read the way Blazor's own supplier reads it.</summary>
    private static string? Parameter(string url, string name)
    {
        int start = url.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query =
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url[start..]);

        return query.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value) ? value.ToString() : null;
    }
}
