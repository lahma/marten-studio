using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Documents;

using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using ReferencedByPanel = MartenStudio.Components.Pages.Documents.ReferencedBy;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// "Referenced by" across schemas: a table the studio does not map that points at the document, what a
/// delete does to its rows, and a key from a schema the visitor is not shown - counted, never named.
/// </summary>
/// <remarks>
/// The case these exist for: a studio customer delete cascades into <c>legacy.customer_credit</c>, and
/// before this the panel read only the store's own schemas, so nothing on the page said so.
/// </remarks>
public class ReferencedByTablesTests
{
    private const string CustomerId = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

    [Fact]
    public void A_table_row_names_the_table_links_to_its_object_detail_and_says_what_a_delete_does()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy([Credit(count: 1)]));

        var item = panel.Find(".ms-referenced-table");

        item.QuerySelector(".ms-graph-table-chip")!.TextContent.Should().Contain("legacy.").And.Contain("customer_credit");
        item.QuerySelector(".ms-referenced-link")!.GetAttribute("href")
            .Should().Be("database/object?schema=legacy&name=customer_credit");
        item.QuerySelector(".ms-referenced-count")!.TextContent.Should().Be("1");
        item.QuerySelector(".ms-referenced-ondelete")!.TextContent.Should().Be("on delete cascade");
    }

    [Theory]
    [InlineData("Cascade", "on delete cascade")]
    [InlineData("Restrict", "on delete restrict")]
    [InlineData("SetNull", "on delete set null")]
    [InlineData("NoAction", "on delete no action")]
    public void Every_row_shows_its_on_delete_action(string action, string expected)
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy(
        [
            Credit(count: 2) with { OnDelete = action },
            new ReferencedByEntry("order", "customer_id", "CustomerId", 215, 3, false, true, true) { OnDelete = action },
        ]));

        panel.FindAll(".ms-referenced-ondelete").Select(static x => x.TextContent).Should().Equal([expected, expected]);
    }

    [Fact]
    public void A_count_the_gate_does_not_allow_is_not_made_and_the_row_says_why()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy(
        [
            Credit(count: 0) with { NotCounted = "Schema 'legacy' is not one MartenStudioOptions.BrowsableSchemas admits." },
        ]));

        panel.Find(".ms-referenced-count").TextContent.Should().Be("?");
        panel.Find(".ms-referenced-notcounted").TextContent.Should().Be("not counted");
        panel.Find(".ms-referenced-notcounted").GetAttribute("title").Should().Contain("BrowsableSchemas");
        panel.Find(".ms-referenced-ondelete").TextContent.Should().Be("on delete cascade",
            "the key and what it does are structure, and are shown whether or not its rows may be counted");
    }

    [Fact]
    public void Keys_from_schemas_the_visitor_is_not_shown_are_a_count_and_nothing_else()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy([]) { Withheld = 2 });

        panel.Find(".ms-referenced-withheld").TextContent.Should().Contain(
            "2 more foreign keys point here from schemas this studio does not show you");
        panel.FindAll(".ms-referenced-item").Should().BeEmpty();
        panel.Markup.Should().NotContain("legacy");
    }

    // ----------------------------------------------------------------------------------------------
    // What a delete does
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_cascade_into_a_table_is_the_delete_consequence()
    {
        new ReferencedBy([Credit(count: 1)]).DeleteConsequence().Should().Be(
            "Deleting this document also deletes 1 row in legacy.customer_credit (ON DELETE CASCADE).");

        new ReferencedBy([Credit(count: 1000, capped: true), Credit(count: 3) with { Table = "reminders", OnDelete = "SetNull", AllColumns = "customer_id" }])
            .DeleteConsequence().Should().Be(
                "Deleting this document also deletes 1,000+ rows in legacy.customer_credit (ON DELETE CASCADE), " +
                "and sets customer_id to null in 3 rows of legacy.reminders (ON DELETE SET NULL).");
    }

    [Fact]
    public void Nothing_that_is_not_known_or_not_a_table_is_a_delete_consequence()
    {
        new ReferencedBy([Credit(count: 0)]).DeleteConsequence().Should().BeNull("no row points here");
        new ReferencedBy([Credit(count: 4) with { NotCounted = "the gate says no" }]).DeleteConsequence().Should().BeNull(
            "a count this visitor may not read is not said in a sentence either");
        new ReferencedBy([Credit(count: 4, error: "57014: canceling statement")]).DeleteConsequence().Should().BeNull();
        new ReferencedBy([Credit(count: 4) with { OnDelete = "NoAction" }]).DeleteConsequence().Should().BeNull(
            "NO ACTION changes no row - Postgres refuses the delete instead, and says so");
        new ReferencedBy([new ReferencedByEntry("order", "customer_id", "CustomerId", 1, 5, false, true, true) { OnDelete = "Cascade" }])
            .DeleteConsequence().Should().BeNull(
                "a collection's count leaves out soft-deleted and other tenants' rows, so it is not a count of what a cascade deletes");
    }

    [Fact]
    public void The_hard_delete_dialog_says_what_the_cascade_into_a_table_will_delete()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(softDelete: false));
        context.RelationshipData.Referenced = new ReferencedBy([Credit(count: 1)]);

        var page = RenderDetail(context);
        page.Find(".ms-doc-delete-btn").Click();

        string message = page.Find(".ms-confirm-dialog p").TextContent;

        message.Should().Contain("There is no undo.");
        message.Should().Contain("Deleting this document also deletes 1 row in legacy.customer_credit (ON DELETE CASCADE).");
    }

    [Fact]
    public void A_soft_delete_dialog_says_nothing_about_a_cascade_because_an_update_fires_none()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(softDelete: true));
        context.RelationshipData.Referenced = new ReferencedBy([Credit(count: 1)]);

        var page = RenderDetail(context);
        page.Find(".ms-doc-delete-btn").Click();

        page.Find(".ms-confirm-dialog p").TextContent.Should().NotContain("ON DELETE");
    }

    private static ReferencedByEntry Credit(long count, bool capped = false, string? error = null) =>
        new("legacy.customer_credit", "customer_id", null, RelationshipTableNode.SchemaHue("legacy"), count, capped, false, true, error)
        {
            Schema = "legacy",
            Table = "customer_credit",
            OnDelete = "Cascade",
            AllColumns = "customer_id",
        };

    private static IRenderedComponent<ReferencedByPanel> Render(StudioComponentContext context, ReferencedBy model) =>
        context.Render<ReferencedByPanel>(parameters => parameters
            .Add(x => x.Model, model)
            .Add(x => x.DocumentId, CustomerId));

    private static IRenderedComponent<DetailPage> RenderDetail(DocumentsComponentContext context)
    {
        context.Navigate($"marten/documents/customer/doc?id={CustomerId}");
        return context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
    }

    private static DetailModel Detail(bool softDelete)
    {
        List<PhysicalColumnValue> columns =
        [
            new PhysicalColumnValue("id", CustomerId, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
        ];

        if (softDelete)
        {
            columns.Add(new PhysicalColumnValue("mt_deleted", "False", PhysicalColumnRole.Metadata));
        }

        return new DetailModel
        {
            Alias = "customer",
            Id = CustomerId,
            Json = """{"Name":"Customer 01"}""",
            TextBytes = 22,
            StoredBytes = 22,
            Columns = columns,
            TableName = "\"studio_sample\".\"mt_doc_customer\"",
            ClrType = typeof(ReferencedByTablesTests),
            IsRegistered = true,
            IsDeleted = false,
            IdColumnType = DocumentIdColumnType.Uuid,
        };
    }
}
