using Bunit;

using MartenStudio.Components.Pages.Relationships;
using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using GraphModel = MartenStudio.Services.Relationships.RelationshipGraph;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The object detail page's Relationships tab: one object and its direct neighbours, in its own loading,
/// failure, refused and empty states - with nothing to go on but a schema, a name and the scope.
/// </summary>
public class NeighbourhoodGraphTests
{
    [Fact]
    public void A_table_is_drawn_with_the_nodes_one_key_away_and_the_same_keys_in_the_table()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);

        var tab = Render(context, "quartz", "qrtz_triggers");

        relationships.LastScope!.StoreKey.Should().Be("default", "the scope comes from StudioState");

        tab.FindAll(".ms-graph-svg .ms-graph-node").Should().HaveCount(3);
        tab.FindAll(".ms-graph-svg .ms-graph-node-emphasis").Should().ContainSingle()
            .Which.QuerySelector(".ms-graph-node-alias")!.TextContent.Should().Be("qrtz_triggers",
                "the object the tab is about is the one drawn emphasised");

        List<string> drawn = [.. tab.FindAll(".ms-graph-svg .ms-graph-edge").Select(static x => x.GetAttribute("data-edge") ?? string.Empty)];
        List<string> listed = [.. tab.FindAll(".ms-graph-row").Select(static x => x.GetAttribute("data-edge") ?? string.Empty)];

        drawn.Should().HaveCount(2);
        listed.Should().BeEquivalentTo(drawn);
        tab.Find("table.ms-graph-table").ClassList.Should().Contain("ms-graph-table-compact");
        tab.ShouldPutEveryTableInALabelledScrollRegion();
    }

    [Fact]
    public void A_document_table_is_found_by_name_and_says_how_many_keys_reach_schemas_not_shown()
    {
        using var context = NewContext(out _);

        var tab = Render(context, "studio_sample", "mt_doc_customer");

        tab.FindAll(".ms-graph-svg .ms-graph-node-emphasis .ms-graph-node-alias").Single().TextContent.Should().Be("customer");
        tab.FindAll(".ms-graph-row").Should().HaveCount(2);
        tab.Find(".ms-graph-withheld").TextContent.Should().Contain("1 more foreign key reaches a schema this studio does not show you");
    }

    [Fact]
    public void A_table_with_no_key_says_so_rather_than_drawing_an_empty_picture()
    {
        using var context = NewContext(out _);

        var tab = Render(context, "legacy", "audit_log");

        tab.Find(".ms-neighbourhood-empty .ms-empty-title").TextContent.Should().Be("No foreign keys");
        tab.Find(".ms-neighbourhood-empty").TextContent.Should().Contain("legacy.audit_log");
        tab.FindAll("svg.ms-graph-svg").Should().BeEmpty();
        tab.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public void A_graph_that_could_not_be_read_is_an_error_with_a_retry()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = GraphModel.Failed("57014: canceling statement due to statement timeout", DateTimeOffset.UtcNow);

        var tab = Render(context, "quartz", "qrtz_triggers");

        tab.Find(".ms-error-alert").TextContent.Should().Contain("57014");
        tab.FindAll(".ms-empty").Should().BeEmpty("'cannot report' is not 'there is nothing here'");

        int reads = relationships.Reads;
        relationships.Graph = FakeRelationshipDataService.WithTables();
        tab.Find(".ms-error-alert button").Click();

        relationships.Reads.Should().Be(reads + 1);
        tab.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(2);
    }

    [Fact]
    public void A_service_that_throws_is_put_on_the_tab_rather_than_killing_the_circuit()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Failure = new InvalidOperationException("the store would not build");

        var tab = Render(context, "quartz", "qrtz_triggers");

        tab.Find(".ms-error-alert").TextContent.Should().Contain("the store would not build");
    }

    [Fact]
    public void An_object_in_a_schema_the_visitor_is_not_shown_is_refused_without_a_single_name()
    {
        using var context = NewContext(out _);

        var tab = Render(context, "secrets", "vault");

        tab.Find(".ms-neighbourhood-refused .ms-empty-title").TextContent.Should().Be("Relationships not shown");
        tab.Find(".ms-neighbourhood-refused").TextContent.Should().Contain("not one of the schemas this studio shows you");
        tab.FindAll("svg.ms-graph-svg").Should().BeEmpty();
        tab.Markup.Should().NotContain("qrtz_", "a refused tab draws nothing from the graph at all");
    }

    [Fact]
    public void Changing_the_object_reads_again_for_the_new_one()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);

        var tab = Render(context, "quartz", "qrtz_triggers");
        int reads = relationships.Reads;

        tab.Render(parameters => parameters
            .Add(x => x.Schema, "legacy")
            .Add(x => x.Name, "purchase_orders"));

        relationships.Reads.Should().BeGreaterThan(reads);
        tab.FindAll(".ms-graph-svg .ms-graph-edge-notvalid").Should().ContainSingle();
    }

    private static IRenderedComponent<NeighbourhoodGraph> Render(StudioComponentContext context, string schema, string name) =>
        context.Render<NeighbourhoodGraph>(parameters => parameters
            .Add(x => x.Schema, schema)
            .Add(x => x.Name, name));

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
