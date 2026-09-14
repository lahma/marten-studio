using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using GraphModel = MartenStudio.Services.Relationships.RelationshipGraph;
using RelationshipsPage = MartenStudio.Components.Pages.Relationships.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The Relationships screen: the picture, the table beside it, and the states in between.
/// </summary>
/// <remarks>
/// The equivalence assertion is the one that matters. The SVG is <c>role="img"</c> and the table is the
/// accessible and narrow-screen form of it, so the table has to list <em>every</em> edge the picture
/// draws — a screen that showed four arrows and three rows would be telling two different stories
/// depending on who was reading it.
/// </remarks>
public class RelationshipsPageTests
{
    /// <summary>
    /// The studio's own router has to reach the page. The route table is built by reflection over the
    /// <c>@page</c> templates, so there is nothing to register — but "nothing to register" is exactly the
    /// kind of claim that wants an assertion behind it, because a page nobody can navigate to renders
    /// perfectly in every other test here.
    /// </summary>
    [Fact]
    public void The_studios_router_reaches_the_page()
    {
        MartenStudio.Components.StudioRouteTable.Match("relationships")
            .Should().NotBeNull()
            .And.Subject.As<Microsoft.AspNetCore.Components.RouteData>()
            .PageType.Should().Be<RelationshipsPage>();
    }

    [Fact]
    public void The_diagram_draws_one_node_per_document_type_and_one_path_per_key()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        page.FindAll("svg.ms-graph-svg").Should().ContainSingle();
        page.FindAll(".ms-graph-svg .ms-graph-node").Should().HaveCount(4);
        page.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(3);
        page.FindAll(".ms-graph-svg .ms-graph-edge-arrow").Should().HaveCount(3, "every arrow says which way the key points");
    }

    [Fact]
    public void The_sample_stores_order_to_customer_key_is_drawn_and_listed_as_declared_and_physical()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        page.FindAll(".ms-graph-svg .ms-graph-edge-both").Should().ContainSingle();

        string title = page.FindAll(".ms-graph-svg .ms-graph-edge-both title").Single().TextContent;

        title.Should().Contain("order.customer_id");
        title.Should().Contain("customer");
        title.Should().Contain("declared and physical");
    }

    [Fact]
    public void A_declared_only_key_and_a_database_only_key_are_drawn_differently()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        page.FindAll(".ms-graph-svg .ms-graph-edge-declared").Should().ContainSingle("invoice → customer is not applied");
        page.FindAll(".ms-graph-svg .ms-graph-edge-physical").Should().ContainSingle("note → order is not declared");
    }

    [Fact]
    public void Every_edge_the_picture_draws_is_a_row_in_the_table()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        List<string> drawn = [.. page.FindAll(".ms-graph-svg .ms-graph-edge title").Select(Pair)];
        List<string> listed = [.. page.FindAll(".ms-graph-row").Select(RowPair)];

        listed.Should().BeEquivalentTo(drawn,
            "the table is the accessible form of the picture and the only form below 900px");
    }

    [Fact]
    public void The_table_names_the_column_the_member_and_the_state_of_every_key()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        IElement row = page.FindAll(".ms-graph-row")
            .Single(x => RowPair(x) == "order→customer");

        row.TextContent.Should().Contain("customer_id");
        row.TextContent.Should().Contain("CustomerId");
        row.TextContent.Should().Contain("declared and physical");
    }

    [Fact]
    public void A_node_links_to_its_collection_and_carries_the_scope()
    {
        using var context = NewContext(out _);
        context.Catalog.WithStore("second", "Second", databaseIdentities: "localhost.marten");

        var page = context.Render<RelationshipsPage>();

        IElement node = page.FindAll(".ms-graph-svg .ms-graph-node")
            .Single(x => x.QuerySelector(".ms-graph-node-alias")?.TextContent == "order");

        string href = node.GetAttribute("href") ?? string.Empty;

        href.Should().StartWith("documents/order", "the link goes through StudioLink, relative to the studio root");
        href.Should().Contain("store=default", "a pasted link has to reopen the same scope (D9)");
    }

    [Fact]
    public void The_table_rows_link_to_the_same_collections_as_the_picture()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        List<string> fromPicture = [.. page.FindAll(".ms-graph-svg .ms-graph-node").Select(x => x.GetAttribute("href") ?? string.Empty)];
        List<string> fromTable = [.. page.FindAll(".ms-graph-row-link").Select(x => x.GetAttribute("href") ?? string.Empty)];

        fromTable.Should().NotBeEmpty();
        fromTable.Should().BeSubsetOf(fromPicture);
    }

    [Fact]
    public void A_node_the_diagram_draws_is_not_a_tab_stop_because_the_table_carries_the_same_links()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        page.FindAll(".ms-graph-svg .ms-graph-node").Should()
            .OnlyContain(x => x.GetAttribute("tabindex") == "-1",
                "the svg is role=img, so a link inside it is focusable but never announced - the table " +
                "below is where the keyboard goes");

        page.Find("svg.ms-graph-svg").GetAttribute("role").Should().Be("img");
        page.Find("svg.ms-graph-svg").GetAttribute("aria-label").Should().Contain("document types");
    }

    [Fact]
    public void A_key_with_an_end_outside_the_store_is_listed_rather_than_drawn()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        page.Markup.Should().Contain("Not on the diagram");
        page.Markup.Should().Contain("legacy_fkey");
        page.Markup.Should().Contain("studio_sample.legacy_customers");
    }

    [Fact]
    public void A_store_with_no_foreign_keys_says_so_and_shows_how_to_declare_one()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = FakeRelationshipDataService.WithoutKeys();

        var page = context.Render<RelationshipsPage>();

        page.Find(".ms-empty-title").TextContent.Should().Contain("No foreign keys are declared");
        page.Find(".ms-empty-description").TextContent.Should().Contain("ForeignKey<Customer>");
        page.FindAll("svg.ms-graph-svg").Should().BeEmpty();
    }

    [Fact]
    public void A_graph_that_could_not_be_read_says_so_rather_than_rendering_as_an_empty_store()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = GraphModel.Failed("57014: canceling statement due to statement timeout", DateTimeOffset.UtcNow);

        var page = context.Render<RelationshipsPage>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("57014");
        page.FindAll(".ms-empty-title").Should().BeEmpty("'cannot report' is not 'there is nothing here'");
    }

    [Fact]
    public void A_service_that_throws_is_put_on_the_page_rather_than_killing_the_circuit()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Failure = new InvalidOperationException("the store would not build");

        var page = context.Render<RelationshipsPage>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("the store would not build");
    }

    [Fact]
    public void Refresh_reads_again()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);

        var page = context.Render<RelationshipsPage>();

        // Not "one": StudioLayout's own initialization settles the scope, and the page has already
        // subscribed to that by the time it calls EnsureInitializedAsync, so a first render reads twice.
        // Every page in the studio is shaped that way; what this test is about is the button.
        int loaded = relationships.Reads;
        loaded.Should().BeGreaterThan(0);

        page.FindAll("button").Single(x => x.TextContent.Trim() == "Refresh").Click();

        relationships.Reads.Should().Be(loaded + 1);
    }

    [Fact]
    public async Task A_scope_change_re_reads_for_the_new_scope()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        context.Catalog.WithStore("second", "Second", databaseIdentities: "localhost.marten");

        var page = context.Render<RelationshipsPage>();
        int loaded = relationships.Reads;

        await page.InvokeAsync(() => context.State.SetScopeAsync("second", null, null));

        relationships.Reads.Should().BeGreaterThan(loaded);
        relationships.LastScope!.StoreKey.Should().Be("second");
    }

    /// <summary>The "from → to" pair a title on the picture describes.</summary>
    private static string Pair(IElement title)
    {
        string text = title.TextContent;
        int arrow = text.IndexOf('→', StringComparison.Ordinal);

        string from = text[..arrow].Trim();
        string to = text[(arrow + 1)..].Trim();

        // "order.customer_id → customer (declared and physical, on delete NoAction)"
        return from.Split('.')[0] + "→" + to.Split(' ')[0];
    }

    /// <summary>The same pair, read off a row of the table.</summary>
    private static string RowPair(IElement row)
    {
        List<string> aliases = [.. row.QuerySelectorAll(".ms-collection-alias").Select(x => x.TextContent.Trim())];

        return aliases[0] + "→" + aliases[1];
    }

    private static StudioComponentContext NewContext(out FakeRelationshipDataService relationships)
    {
        var fake = new FakeRelationshipDataService();
        var context = new StudioComponentContext(
            services => services.AddSingleton<IRelationshipDataService>(fake));

        context.WithStores("default");

        relationships = fake;
        return context;
    }
}
