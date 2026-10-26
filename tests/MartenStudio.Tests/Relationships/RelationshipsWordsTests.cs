using System.Text.RegularExpressions;

using Bunit;

using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Relationships;
using PostgresStudio.Services.Relationships;

using RelationshipsPage = PostgresStudio.Components.Pages.Relationships.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// UI-fix: the Relationships screen's and the Relationships tab's sentences about document types now come from the studio's
/// own words (<c>StudioText</c>), so that Postgres Studio can speak of tables alone - and MartenStudio reads every one of
/// them as the markup used to say it, laid out as a browser lays it out. Each expected value is the old literal, copied.
/// </summary>
public partial class RelationshipsWordsTests
{
    [Fact]
    public void The_table_caption_and_the_unkeyed_list_read_as_they_did()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = FakeRelationshipDataService.WithTablesAndUnkeyedDocuments();

        var page = context.Render<RelationshipsPage>();

        Collapsed(page.Find("table.ms-graph-table caption").TextContent).Should().Be(
            "Every foreign key between the document types and tables on the diagram, with the end that points, the end pointed "
            + "at, the columns, whether the database has the constraint, and what a delete does.");
        page.Find(".ms-graph-isolated-list").GetAttribute("aria-label").Should().Be("Document types and tables with no foreign key");
        page.FindAll(".ms-graph-views .ms-segmented-option").Select(static x => Collapsed(x.TextContent))
            .Should().Equal("Documents 1", "Tables 4", "Both 5");
    }

    [Fact]
    public void The_screens_note_on_a_key_it_cannot_draw_reads_as_it_did()
    {
        using var context = NewContext(out _);

        var page = context.Render<RelationshipsPage>();

        Collapsed(page.Find(".ms-graph-unmatched .ms-schema-note").TextContent).Should().Be(
            "These foreign keys have an end that is not a document type of this store, so there is nothing to draw them between.");
    }

    [Fact]
    public void The_note_on_tables_that_could_not_be_read_reads_as_it_did()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = FakeRelationshipDataService.Sample() with { TablesUnavailable = "The database browser's gate could not be read." };

        var page = context.Render<RelationshipsPage>();

        Collapsed(page.Find(".ms-graph-withheld").TextContent).Should().Be(
            "Only document types are drawn: The database browser's gate could not be read.");
    }

    [Theory]
    [InlineData("legacy", "audit_log", "Its foreign keys have")]
    [InlineData("quartz", "qrtz_triggers", "These foreign keys have")]
    public void The_tabs_note_on_a_key_it_cannot_draw_reads_as_it_did(string schema, string name, string opening)
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = RelationshipViewTests.WithUnmatched();

        var tab = context.Render<NeighbourhoodGraph>(parameters => parameters.Add(x => x.Schema, schema).Add(x => x.Name, name));

        Collapsed(tab.Find(".ms-neighbourhood-unmatched .ms-schema-note").TextContent).Should().Be(
            opening + " an end that is not a document type of this store or a table the studio can place, so there is nothing to "
            + "draw them between.");
    }

    [Fact]
    public void The_diagrams_accessible_name_still_counts_the_document_types()
    {
        using var context = NewContext(out FakeRelationshipDataService relationships);
        relationships.Graph = FakeRelationshipDataService.WithTables();

        var page = context.Render<RelationshipsPage>();

        page.Find("svg.ms-graph-svg").GetAttribute("aria-label").Should().Be(
            "A diagram of 2 document types and 6 tables, and 5 foreign keys between them. The table below lists the same "
            + "relationships.");
    }

    /// <summary>The text as a browser lays it out: every run of whitespace one space.</summary>
    private static string Collapsed(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static StudioComponentContext NewContext(out FakeRelationshipDataService relationships)
    {
        var fake = new FakeRelationshipDataService();
        var context = new StudioComponentContext(services => services.AddSingleton<IRelationshipDataService>(fake));

        context.WithStores("default");

        relationships = fake;
        return context;
    }
}
