using Bunit;

using MartenStudio.Tests.Support;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Components.Pages.Relationships;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Relationships;

using GraphModel = PostgresStudio.Services.Relationships.RelationshipGraph;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// UI-fix: Postgres Studio's Relationships screen and the object detail's Relationships tab speak of tables alone. A graph
/// Postgres Studio reads has no document type - it has no node source - so the Documents · Tables · Both switch is not
/// drawn (Documents would draw nothing, and Tables is Both), and no caption, label, note or empty state names a document
/// type, a store or Marten.
/// </summary>
public class PostgresRelationshipsWordsTests
{
    [Fact]
    public async Task The_screen_draws_no_view_switch_and_names_no_document_type()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(FakeRelationshipDataService.PlainTables());

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(3));
        page.FindAll(".ms-graph-views").Should().BeEmpty("with no document type, Documents draws nothing and Tables is Both");
        page.FindAll(".ms-segmented-option").Should().BeEmpty();
        page.FindAll(".ms-graph-schema-chip").Should().NotBeEmpty("the schema chips still narrow the picture");

        page.Find("table.ms-graph-table caption").TextContent.Should().Be(PostgresStudioText.Instance.RelationshipTableCaption);
        page.Find(".ms-graph-isolated-list").GetAttribute("aria-label").Should().Be("Tables with no foreign key");
        page.Find("svg.ms-graph-svg").GetAttribute("aria-label").Should().Be(
            "A diagram of 5 tables and 3 foreign keys between them. The table below lists the same relationships.");

        ShouldNameNoDocumentType(page.Markup);
    }

    [Fact]
    public async Task A_documents_view_in_the_url_draws_every_table_rather_than_nothing()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(FakeRelationshipDataService.PlainTables());
        context.Navigate("postgres/relationships?view=documents");

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(3,
            "there is no switch to leave the Documents view by, so the picture is Both whatever the URL says"));
        page.FindAll(".ms-graph-svg .ms-graph-node-table").Should().HaveCount(5);
        page.FindAll(".ms-graph-schema-chip").Should().NotBeEmpty();
        page.FindAll(".ms-graph-row").Should().HaveCount(3);
    }

    [Fact]
    public async Task Past_the_cap_it_asks_for_a_schema_and_not_for_a_view_it_has_none_of()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(Chain(RelationshipViews.NodeCap + 10));

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.Find(".ms-graph-toomany-title").TextContent.Should().Be(
            $"{RelationshipViews.NodeCap + 10} objects are too many to draw — pick a schema."));
        page.FindAll(".ms-graph-views").Should().BeEmpty();
    }

    [Fact]
    public async Task A_database_with_no_foreign_key_says_so_in_terms_of_tables()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(
            new GraphModel([], [], [], DateTimeOffset.UnixEpoch) { VisibleSchemas = ["public"] });

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.Find(".ms-empty-title").TextContent.Should().Be("No foreign keys connect the tables you can see"));
        Collapsed(page.Find(".ms-empty-description").TextContent).Should().Be(
            "A table's keys are its own: REFERENCES in its DDL, which this screen draws as soon as Postgres has one.");
        page.FindAll(".ms-graph-views").Should().BeEmpty();

        ShouldNameNoDocumentType(page.Markup);
    }

    [Fact]
    public async Task Tables_that_could_not_be_read_are_said_not_to_be_drawn()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(
            new GraphModel([], [], [], DateTimeOffset.UnixEpoch) { TablesUnavailable = "The database browser's gate could not be read." });

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.Find(".ms-graph-withheld").TextContent.Should().Be(
            "No table is drawn: The database browser's gate could not be read."));

        ShouldNameNoDocumentType(page.Markup);
    }

    [Fact]
    public async Task A_key_the_screen_cannot_draw_is_explained_without_a_document_type()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(WithUnmatched());

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.Find(".ms-graph-unmatched .ms-schema-note").TextContent.Should().Be(
            PostgresStudioText.Instance.RelationshipUnmatchedNote));
    }

    [Fact]
    public async Task The_object_details_tab_names_no_document_type_either()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(FakeRelationshipDataService.PlainTables());

        var tab = context.Render<DatabaseRelationshipsSlot>(parameters => parameters
            .Add(x => x.Schema, "quartz")
            .Add(x => x.Name, "qrtz_triggers"));

        tab.WaitForAssertion(() => tab.FindAll(".ms-graph-svg .ms-graph-edge").Should().HaveCount(2));
        tab.Find("table.ms-graph-table caption").TextContent.Should().Be(PostgresStudioText.Instance.RelationshipTableCaption);
        tab.FindAll(".ms-graph-views").Should().BeEmpty();

        ShouldNameNoDocumentType(tab.Markup);
    }

    [Fact]
    public async Task The_tabs_key_it_cannot_draw_is_explained_without_a_document_type()
    {
        await using PostgresStudioComponentContext context = await NewContextAsync(WithUnmatched());

        var tab = context.Render<DatabaseRelationshipsSlot>(parameters => parameters
            .Add(x => x.Schema, "legacy")
            .Add(x => x.Name, "audit_log"));

        tab.WaitForAssertion(() => Collapsed(tab.Find(".ms-neighbourhood-unmatched .ms-schema-note").TextContent).Should().Be(
            "Its foreign keys have an end in a table this studio does not draw, so there is nothing to draw them between."));
    }

    /// <summary>The text as a browser lays it out: every run of whitespace one space.</summary>
    private static string Collapsed(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>The words a page of Postgres Studio's must not say of a graph it read: it has no document type and no store.</summary>
    private static void ShouldNameNoDocumentType(string markup)
    {
        markup.Should().NotContainEquivalentOf("document", "a graph Postgres Studio reads has no document type")
            .And.NotContainEquivalentOf("Marten")
            .And.NotContain("this store")
            .And.NotContain("StoreOptions");
    }

    /// <summary>
    /// <see cref="FakeRelationshipDataService.PlainTables" /> and one key into a table named like a Marten store's document
    /// table, which Postgres Studio does not draw. Its reason is the graph builder's own sentence - a residual of the service
    /// layer, outside these components - so the tests above read the notes the components write, and not the reason cell.
    /// </summary>
    private static GraphModel WithUnmatched() => FakeRelationshipDataService.PlainTables() with
    {
        Unmatched =
        [
            new UnmatchedForeignKey(
                "audit_log_ref_fkey",
                "legacy.audit_log",
                "ref_id",
                "legacy.mt_doc_discovered",
                Declared: false,
                Physical: true,
                "It points at a table no document type of this store maps.")
            {
                FromSchema = "legacy",
                FromTable = "audit_log",
                ToSchema = "legacy",
                ToTable = "mt_doc_discovered",
            },
        ],
    };

    /// <summary>A chain of tables, each pointing at the one before it: too many to draw.</summary>
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

        return new GraphModel([], edges, [], DateTimeOffset.UnixEpoch) { Tables = tables, VisibleSchemas = ["wide"] };
    }

    private static async Task<PostgresStudioComponentContext> NewContextAsync(GraphModel graph)
    {
        PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.RelationshipData.Graph = graph;
        return context;
    }
}
