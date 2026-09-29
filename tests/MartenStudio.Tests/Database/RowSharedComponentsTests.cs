using Bunit;

using MartenStudio.Components.Pages.Relationships;
using MartenStudio.Components.Shared;
using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using ReferencedByModel = MartenStudio.Services.Relationships.ReferencedBy;
using ReferencedByPanel = MartenStudio.Components.Pages.Documents.ReferencedBy;

namespace MartenStudio.Tests.Database;

/// <summary>
/// What the Rows tab took out of other screens and gave back: the cell, the Show SQL disclosure and the
/// pager, each still drawing exactly what its first home drew - and the links DB-5 moved: a neighbour on
/// the object page's Relationships tab, and "referenced by" a table on document detail.
/// </summary>
public class RowSharedComponentsTests
{
    [Fact]
    public void The_console_cell_is_unchanged_by_the_extraction()
    {
        using var context = new StudioComponentContext();

        context.Render<SqlCellView>(parameters => parameters.Add(x => x.Kind, "null"))
            .Markup.Should().Contain("<span class=\"ms-query-null\" title=\"SQL NULL\">NULL</span>");

        var json = context.Render<SqlCellView>(parameters => parameters.Add(x => x.Kind, "json").Add(x => x.Text, "{\"a\":1}"));
        json.Find("button.ms-chip.ms-query-json-chip").TextContent.Trim().Should().Be("{ … } 7 chars");

        var binary = context.Render<SqlCellView>(parameters => parameters.Add(x => x.Kind, "binary").Add(x => x.Text, "\\x7b7d (2 bytes)"));
        binary.Find(".ms-query-cell-text").TextContent.Should().Be("\\x7b7d (2 bytes)", "only the row grid sniffs bytes");

        var time = context.Render<SqlCellView>(parameters => parameters
            .Add(x => x.Kind, "timestamp")
            .Add(x => x.Type, "timestamp with time zone")
            .Add(x => x.Text, "2026-09-29T14:03:00.0000000Z"));
        time.Find(".ms-query-cell-text").TextContent.Should().Be("2026-09-29T14:03:00.0000000Z", "the console shows what Postgres sent");
    }

    [Fact]
    public void Show_SQL_draws_nothing_without_a_statement_and_never_a_value()
    {
        using var context = new StudioComponentContext();

        context.Render<SqlDisclosure>().Markup.Trim().Should().BeEmpty();

        var disclosure = context.Render<SqlDisclosure>(parameters => parameters
            .Add(x => x.Sql, "select 1 where a = @p1")
            .Add(x => x.ParameterNames, ["@p1"]));

        disclosure.Find("details.ms-sql-disclosure summary").TextContent.Should().Be("Show SQL");
        disclosure.Find("pre.ms-code-sql code").TextContent.Should().Be("select 1 where a = @p1");
        disclosure.Find(".ms-sql-parameters").TextContent.Should().Be("Parameters: @p1");
    }

    [Fact]
    public void The_pager_keeps_the_documents_list_s_defaults()
    {
        using var context = new StudioComponentContext();

        var pager = context.Render<KeysetPager>(parameters => parameters.Add(x => x.HasMore, true));

        pager.Find(".ms-pager").GetAttribute("aria-label").Should().Be("Document pages");
        pager.Find("#ms-pager-jump-input").GetAttribute("placeholder").Should().Be("Jump to id");

        var rows = context.Render<KeysetPager>(parameters => parameters
            .Add(x => x.AriaLabel, "Row pages")
            .Add(x => x.ShowJump, false)
            .Add(x => x.Busy, true)
            .Add(x => x.HasMore, true));

        rows.Find(".ms-pager").GetAttribute("aria-label").Should().Be("Row pages");
        rows.FindAll(".ms-pager-jump").Should().BeEmpty();
        rows.FindAll(".ms-pager button").Should().OnlyContain(static x => x.HasAttribute("disabled"), "nothing is pressed twice while a page is read");
    }

    [Fact]
    public void A_neighbour_opens_on_its_own_relationships_tab()
    {
        var relationships = new FakeRelationshipDataService { Graph = FakeRelationshipDataService.WithTables() };
        using var context = new StudioComponentContext(services => services.AddSingleton<IRelationshipDataService>(relationships));
        context.WithStores("default");

        var tab = context.Render<NeighbourhoodGraph>(parameters => parameters
            .Add(x => x.Schema, "quartz")
            .Add(x => x.Name, "qrtz_triggers"));

        List<string> tableLinks = [.. tab.FindAll("a")
            .Select(static x => x.GetAttribute("href") ?? string.Empty)
            .Where(static x => x.StartsWith("database/object?", StringComparison.Ordinal))];

        tableLinks.Should().NotBeEmpty();
        tableLinks.Should().OnlyContain(static x => x.Contains("&tab=relationships", StringComparison.Ordinal),
            "hopping from neighbour to neighbour keeps the visitor on the Relationships tab");
    }

    [Fact]
    public void A_table_that_references_a_document_links_to_the_rows_it_counted()
    {
        using var context = new StudioComponentContext();

        ReferencedByEntry counted = new("legacy.customer_credit", "customer_id", null, RelationshipTableNode.SchemaHue("legacy"), 2, false, false, true)
        {
            Schema = "legacy",
            Table = "customer_credit",
            OnDelete = "Cascade",
            ChildFilter = "customer_id = 6f9619ff-8b86-d011-b42d-00cf4fc964ff",
        };

        var panel = context.Render<ReferencedByPanel>(parameters => parameters
            .Add(x => x.Model, (object?) new ReferencedByModel([counted]))
            .Add(x => x.DocumentId, "6f9619ff-8b86-d011-b42d-00cf4fc964ff"));

        string href = panel.Find(".ms-referenced-table .ms-referenced-link").GetAttribute("href")!;
        href.Should().StartWith("database/object?schema=legacy&name=customer_credit&tab=rows&q=customer_id%20%3D%206f9619ff");

        // Not counted - the gate said no - is its object page, where the Rows tab would only say no again.
        panel = context.Render<ReferencedByPanel>(parameters => parameters
            .Add(x => x.Model, (object?) new ReferencedByModel([counted with { NotCounted = "The gate is shut." }]))
            .Add(x => x.DocumentId, "6f9619ff-8b86-d011-b42d-00cf4fc964ff"));

        panel.Find(".ms-referenced-table .ms-referenced-link").GetAttribute("href").Should().Be("database/object?schema=legacy&name=customer_credit");
    }
}
