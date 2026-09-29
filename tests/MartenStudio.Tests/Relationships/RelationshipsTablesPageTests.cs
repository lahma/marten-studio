using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using GraphModel = MartenStudio.Services.Relationships.RelationshipGraph;
using RelationshipsPage = MartenStudio.Components.Pages.Relationships.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The Relationships screen once tables take part in keys: the view switch, the schema chips, the cap,
/// the square table nodes and their links, the dashed NOT VALID key, the composite labels - and, in every
/// view, a table that lists exactly what the picture draws.
/// </summary>
/// <remarks>
/// <see cref="RelationshipsPageTests" /> is the other half, held unchanged: with no table in any key the
/// screen is exactly the one it always was.
/// </remarks>
public class RelationshipsTablesPageTests
{
    [Fact]
    public void Both_is_the_default_view_and_draws_document_types_and_tables()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        IElement both = ViewButton(page, "Both");
        both.GetAttribute("aria-pressed").Should().Be("true");
        ViewButton(page, "Documents").GetAttribute("aria-pressed").Should().Be("false");

        page.FindAll(".ms-graph-svg .ms-graph-node:not(.ms-graph-node-table)").Should().HaveCount(2);
        page.FindAll(".ms-graph-svg .ms-graph-node-table").Should().HaveCount(6, "audit_log takes part in no key");
        page.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(5);

        both.TextContent.Should().Contain("5", "the switch says how many keys each view draws");
        ViewButton(page, "Documents").TextContent.Should().Contain("1");
        ViewButton(page, "Tables").TextContent.Should().Contain("4");
    }

    [Fact]
    public void The_documents_view_draws_only_document_types_and_the_keys_between_them()
    {
        using var context = NewContext(out _);
        context.Navigate("/marten/relationships?view=documents");

        var page = context.Render<RelationshipsPage>();

        ViewButton(page, "Documents").GetAttribute("aria-pressed").Should().Be("true");
        page.FindAll(".ms-graph-svg .ms-graph-node-table").Should().BeEmpty();
        page.FindAll(".ms-graph-svg .ms-graph-edge").Should().ContainSingle();
        page.FindAll(".ms-graph-schemas").Should().BeEmpty("a schema chip filters tables, and this view draws none");
    }

    [Fact]
    public void The_tables_view_draws_keys_with_a_table_end_and_lists_the_tables_with_none()
    {
        using var context = NewContext(out _);
        context.Navigate("/marten/relationships?view=tables");

        var page = context.Render<RelationshipsPage>();

        ViewButton(page, "Tables").GetAttribute("aria-pressed").Should().Be("true");
        page.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(4);
        page.FindAll(".ms-graph-svg .ms-graph-node:not(.ms-graph-node-table)").Should().ContainSingle(
            "customer is reached by customer_credit's key; order is reached by no key with a table end");

        IElement isolated = page.Find(".ms-graph-isolated");
        isolated.TextContent.Should().Contain("audit_log");
        isolated.QuerySelectorAll("a").Should().ContainSingle()
            .Which.GetAttribute("href").Should().StartWith("database/object?schema=legacy&name=audit_log");
    }

    [Fact]
    public void Choosing_a_view_redraws_and_puts_the_view_in_the_url()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        ViewButton(page, "Tables").Click();

        ViewButton(page, "Tables").GetAttribute("aria-pressed").Should().Be("true");
        page.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(4);
        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("view=tables");
    }

    [Fact]
    public void A_schema_chip_filters_the_table_nodes_and_says_which_is_chosen()
    {
        using var context = NewContext(out _);
        context.Navigate("/marten/relationships?schema=quartz");

        var page = context.Render<RelationshipsPage>();

        page.FindAll(".ms-graph-svg .ms-graph-node-table .ms-graph-node-type").Select(static x => x.TextContent)
            .Should().OnlyContain(static x => x == "quartz");
        page.FindAll(".ms-graph-svg .ms-graph-node-table").Should().HaveCount(3);

        page.FindAll(".ms-graph-schema-chip").Single(static x => x.TextContent.Contains("quartz", StringComparison.Ordinal))
            .GetAttribute("aria-pressed").Should().Be("true");
        page.FindAll(".ms-graph-schema-chip").Single(static x => x.TextContent.Contains("All schemas", StringComparison.Ordinal))
            .GetAttribute("aria-pressed").Should().Be("false");

        page.FindAll(".ms-graph-schema-chip").Single(static x => x.TextContent.Contains("legacy", StringComparison.Ordinal)).Click();

        page.FindAll(".ms-graph-svg .ms-graph-node-table .ms-graph-node-type").Select(static x => x.TextContent)
            .Should().OnlyContain(static x => x == "legacy");
        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("schema=legacy");
    }

    [Fact]
    public void Past_the_node_cap_the_diagram_is_replaced_and_the_table_still_lists_every_key()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = Chain(RelationshipViews.NodeCap + 10);

        var page = context.Render<RelationshipsPage>();

        page.FindAll("svg.ms-graph-svg").Should().BeEmpty();
        page.Find(".ms-graph-toomany").TextContent.Should().Contain(
            $"{RelationshipViews.NodeCap + 10} objects are too many to draw — pick a schema or a view");
        page.FindAll(".ms-graph-row").Should().HaveCount(RelationshipViews.NodeCap + 9);
    }

    [Fact]
    public void A_table_node_is_square_names_its_schema_and_links_to_object_detail_with_the_scope()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        IElement node = page.FindAll(".ms-graph-svg .ms-graph-node-table")
            .Single(static x => x.QuerySelector(".ms-graph-node-alias")?.TextContent == "qrtz_triggers");

        node.QuerySelector(".ms-graph-node-box")!.GetAttribute("rx").Should().Be("0", "a table has square corners");
        node.QuerySelector(".ms-graph-node-type")!.TextContent.Should().Be("quartz", "the schema is the second line");

        string href = node.GetAttribute("href") ?? string.Empty;
        href.Should().StartWith("database/object?schema=quartz&name=qrtz_triggers");
        href.Should().Contain("store=default", "a pasted link has to reopen the same scope (D9)");

        page.FindAll(".ms-graph-svg .ms-graph-node:not(.ms-graph-node-table) .ms-graph-node-box")
            .Should().OnlyContain(static x => x.GetAttribute("rx") == "8", "a document type keeps its rounded corners");
    }

    [Fact]
    public void A_not_valid_key_is_drawn_dashed_and_the_legend_explains_only_what_is_drawn()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        IElement edge = page.FindAll(".ms-graph-svg .ms-graph-edge-notvalid").Should().ContainSingle().Subject;
        edge.QuerySelector("title")!.TextContent.Should().Contain("NOT VALID");

        page.FindAll(".ms-graph-legend .ms-graph-legend-item").Should().HaveCount(3,
            "declared-and-enforced, table and NOT VALID are drawn; declared-only and physical-only are not");
        page.FindAll(".ms-graph-legend .ms-graph-edge-declared").Should().BeEmpty();
        page.FindAll(".ms-graph-legend .ms-graph-edge-notvalid").Should().ContainSingle();

        page.FindAll(".ms-graph-row").Single(static x => x.GetAttribute("data-edge")!.Contains("purchase_order_lines", StringComparison.Ordinal))
            .TextContent.Should().Contain("not valid");
    }

    [Fact]
    public void A_composite_key_is_labelled_by_what_tells_it_apart_and_lists_every_column()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        IElement edge = page.FindAll(".ms-graph-svg .ms-graph-edge")
            .Single(static x => x.GetAttribute("data-edge")!.StartsWith("quartz.qrtz_triggers→quartz.qrtz_job_details", StringComparison.Ordinal));

        edge.QuerySelector(".ms-graph-edge-label")!.TextContent.Should().Be("job_name, job_group");
        edge.QuerySelector("title")!.TextContent.Should().Contain("sched_name, job_name, job_group",
            "the tooltip always lists every column");

        IElement row = page.FindAll(".ms-graph-row")
            .Single(x => x.GetAttribute("data-edge") == edge.GetAttribute("data-edge"));

        row.QuerySelector(".ms-graph-row-label")!.TextContent.Should().Be("job_name, job_group");
        row.QuerySelector(".ms-graph-row-columns")!.TextContent.Should().Contain("sched_name, job_name, job_group");

        page.FindAll(".ms-graph-svg .ms-graph-edge")
            .Single(static x => x.GetAttribute("data-edge")!.StartsWith("quartz.qrtz_simple_triggers", StringComparison.Ordinal))
            .QuerySelector(".ms-graph-edge-label")!.TextContent.Should().Contain("sched_name",
                "a detail table keyed by exactly its parent's key strips everything, and so shows everything");
    }

    [Theory]
    [InlineData("both")]
    [InlineData("documents")]
    [InlineData("tables")]
    public void In_every_view_every_edge_drawn_is_a_row_in_the_table_and_the_other_way_round(string view)
    {
        using var context = NewContext(out _);
        context.Navigate("/marten/relationships?view=" + view);

        var page = context.Render<RelationshipsPage>();

        List<string> drawn = [.. page.FindAll(".ms-graph-svg .ms-graph-edge").Select(static x => x.GetAttribute("data-edge") ?? string.Empty)];
        List<string> listed = [.. page.FindAll(".ms-graph-row").Select(static x => x.GetAttribute("data-edge") ?? string.Empty)];

        drawn.Should().NotBeEmpty();
        listed.Should().BeEquivalentTo(drawn, "the table is the accessible form of the picture and the only form below 900px");

        page.ShouldPutEveryTableInALabelledScrollRegion();
    }

    [Fact]
    public void A_row_for_a_table_links_to_the_same_object_detail_as_its_node()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        List<string> fromPicture = [.. page.FindAll(".ms-graph-svg .ms-graph-node").Select(static x => x.GetAttribute("href") ?? string.Empty)];
        List<string> fromTable = [.. page.FindAll(".ms-graph-row-link").Select(static x => x.GetAttribute("href") ?? string.Empty)];

        fromTable.Should().Contain(static x => x.StartsWith("database/object", StringComparison.Ordinal));
        fromTable.Should().BeSubsetOf(fromPicture);

        page.FindAll(".ms-graph-row").Single(static x => x.GetAttribute("data-edge")!.StartsWith("legacy.customer_credit", StringComparison.Ordinal))
            .TextContent.Should().Contain("Cascade", "the table says what a delete does, for every key");
    }

    [Fact]
    public void Keys_reaching_schemas_the_visitor_is_not_shown_are_counted_and_never_named()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        page.Find(".ms-graph-withheld").TextContent.Should().Contain(
            "1 foreign key connects what is shown here to a schema this studio does not show you");
    }

    [Fact]
    public void With_tables_but_no_key_the_empty_state_does_not_only_teach_the_marten_helper()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = new GraphModel(
            [FakeRelationshipDataService.WithTables().Nodes[0]],
            [],
            [],
            DateTimeOffset.UnixEpoch)
        {
            Tables = [FakeRelationshipDataService.Table("legacy", "audit_log")],
        };

        var page = context.Render<RelationshipsPage>();

        page.Find(".ms-empty-title").TextContent.Should().Contain("document types and tables");
        page.Find(".ms-empty-description").TextContent.Should().Contain("REFERENCES");
        page.FindAll(".ms-graph-views").Should().BeEmpty("no table takes part in a key, so there is nothing to switch between");
    }

    [Fact]
    public void A_graph_without_tables_because_the_gate_could_not_be_read_says_so()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = FakeRelationshipDataService.Sample() with
        {
            TablesUnavailable = "The catalog could not be read: 57014 canceling statement due to statement timeout",
        };

        var page = context.Render<RelationshipsPage>();

        page.FindAll(".ms-graph-views").Should().BeEmpty();
        page.Find(".ms-graph-withheld").TextContent.Should().Contain("Only document types are drawn").And.Contain("57014");
    }

    private static IElement ViewButton(IRenderedComponent<RelationshipsPage> page, string label) =>
        page.FindAll(".ms-graph-views button").Single(x => x.TextContent.Trim().StartsWith(label, StringComparison.Ordinal));

    private static GraphModel Chain(int nodes)
    {
        List<RelationshipTableNode> tables = [];
        List<RelationshipEdge> edges = [];

        for (int i = 0; i < nodes; i++)
        {
            tables.Add(FakeRelationshipDataService.Table("wide", "t" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture)));
        }

        for (int i = 1; i < tables.Count; i++)
        {
            edges.Add(new RelationshipEdge(tables[i].Key, tables[i - 1].Key, "parent_id", null, false, true, "NoAction", "fk_" + i)
            {
                FromIsTable = true,
                ToIsTable = true,
            });
        }

        return new GraphModel([], edges, [], DateTimeOffset.UnixEpoch) { Tables = tables };
    }

    private static StudioComponentContext NewContext(out FakeRelationshipDataService relationships)
    {
        var fake = new FakeRelationshipDataService { Graph = FakeRelationshipDataService.WithTables() };
        var context = new StudioComponentContext(
            services => services.AddSingleton<IRelationshipDataService>(fake));

        context.WithStores("default");

        relationships = fake;
        return context;
    }
}
