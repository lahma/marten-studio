using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The documents browser: the rail, the list, the parse strip, the chooser and the pager.
/// </summary>
public class DocumentsPageTests
{
    [Fact]
    public void The_rail_draws_the_bands_in_order_with_pinned_first()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(
            documents: [FakeDocuments.Collection("customer"), FakeDocuments.Collection("order")],
            discovered: [FakeDocuments.Collection("orphan", registered: false)]);

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        page.TextOfAll(".ms-rail-name").Should().Equal(
        [
            "Pinned",
            "Recent",
            "Documents",
            "Discovered (unregistered)",
        ]);
        page.TextOfAll(".ms-rail-count").Should().Equal("0", "1", "2", "1");
    }

    [Fact]
    public void A_subclass_is_nested_under_its_root_rather_than_listed_beside_it()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents:
        [
            FakeDocuments.Collection(
                "vehicle",
                hierarchy: true,
                subclasses: [FakeDocuments.Collection("car"), FakeDocuments.Collection("truck")]),
        ]);

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        page.FindAll(".ms-rail-sublist .ms-collection-alias").Select(x => x.TextContent.Trim())
            .Should().Equal("car", "truck");
    }

    [Fact]
    public void An_estimated_count_is_prefixed_with_a_tilde_and_an_exact_one_is_not()
    {
        // D8: a number that looks exact when it is not is how a dashboard loses somebody's trust the first
        // time they check it.
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents:
        [
            FakeDocuments.Collection("customer", count: 1234),
            FakeDocuments.Collection("order", count: 5, estimate: false),
        ]);

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        page.TextOfAll(".ms-rail-badge-count").Should().Equal("~1,234", "5");
    }

    [Fact]
    public async Task Asking_for_an_exact_count_replaces_the_estimate_in_place()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents: [FakeDocuments.Collection("customer", count: 1234)]);
        context.Data.ExactCount = DocumentCount.Exact(1200);

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        await page.Find(".ms-rail-action").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        page.TextOfAll(".ms-rail-badge-count").Should().Equal("1,200");
    }

    [Fact]
    public void The_flags_say_what_is_true_about_a_collection()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents:
        [
            FakeDocuments.Collection("order", softDeleted: true, conjoined: true, hierarchy: true),
        ]);

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        page.FindAll(".ms-rail-flag").Select(x => x.TextContent.Trim())
            .Should().Contain(["D", "T", "H"]);
    }

    [Fact]
    public void A_discovered_table_is_marked_as_one_nothing_claims()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(discovered: [FakeDocuments.Collection("orphan", registered: false)]);

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        page.FindAll(".ms-rail-flag-discovered").Should().NotBeEmpty();
    }

    [Fact]
    public void A_rail_that_could_not_be_read_says_so_rather_than_rendering_an_empty_store()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = CollectionRail.Failed("the database refused the read");

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("the database refused the read");
    }

    [Fact]
    public void With_no_collection_chosen_the_page_says_to_pick_one_and_still_draws_the_rail()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();

        var page = context.Render<MartenStudio.Components.Pages.Documents.Documents>();

        page.Find(".ms-empty-title").TextContent.Should().Be("Pick a collection");
        page.FindAll(".ms-rail-group").Should().NotBeEmpty();
    }

    [Fact]
    public void A_list_renders_a_row_per_document_with_a_link_that_carries_the_id_in_the_query_string()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows:
        [
            FakeDocuments.Row("8f1d5a6e-0000-0000-0000-000000000001"),
            FakeDocuments.Row("8f1d5a6e-0000-0000-0000-000000000002"),
        ]);

        var page = Render(context, "customer");

        page.FindAll(".ms-doc-row").Should().HaveCount(2);
        page.Find(".ms-doc-id").GetAttribute("href")
            .Should().Contain("documents/customer/doc?")
            .And.Contain("id=8f1d5a6e-0000-0000-0000-000000000001");
    }

    [Fact]
    public void A_deleted_row_and_a_tenanted_row_carry_badges()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows:
        [
            FakeDocuments.Row("1", deleted: true),
            FakeDocuments.Row("2", tenant: "acme"),
        ]);

        var page = Render(context, "customer");

        page.FindAll(".ms-badge-deleted").Should().ContainSingle();
        page.Find(".ms-badge-tenant").TextContent.Trim().Should().Be("acme");
    }

    [Fact]
    public void An_empty_result_says_so_rather_than_rendering_a_headerless_table()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page();

        var page = Render(context, "customer");

        page.Find(".ms-table-empty").TextContent.Should().Contain("No documents match");
    }

    [Fact]
    public void A_failed_read_blanks_only_the_list_region()
    {
        // Plan §4.8: a database being down breaks its own region, never navigation.
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = DocumentPage.Failed("57014: canceling statement due to statement timeout", "57014");

        var page = Render(context, "customer");

        page.Find(".ms-error-alert").TextContent.Should().Contain("57014");
        page.FindAll(".ms-rail-group").Should().NotBeEmpty("the rail is a different region");
    }

    [Fact]
    public void The_parse_strip_echoes_every_term_and_shows_the_worst_verdict()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(verdict: new SearchVerdict
        {
            Level = IndexVerdictLevel.Red,
            Chips =
            [
                new SearchChip(SearchChipKind.Predicate, "id:1", IndexVerdictLevel.Green, "id is the primary key.", null),
                new SearchChip(SearchChipKind.Predicate, "Nickname = bob", IndexVerdictLevel.Red,
                    "Nickname is only in the JSON.", "options.Schema.For<Customer>().Duplicate(x => x.Nickname);"),
            ],
            Suggestion = "options.Schema.For<Customer>().Duplicate(x => x.Nickname);",
        });

        var page = Render(context, "customer");

        page.Find(".ms-verdict-badge").TextContent.Trim().Should().Be("full scan");
        page.TextOfAll(".ms-search-chip").Should().Equal("id:1", "Nickname = bob");
        page.Find(".ms-verdict-reason .ms-code-inline").TextContent
            .Should().Be("options.Schema.For<Customer>().Duplicate(x => x.Nickname);");
    }

    [Fact]
    public void The_show_sql_disclosure_carries_the_statement_and_its_parameter_names()
    {
        // D14: there is no live SQL tail, and this is what answers the question people actually have.
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page();

        var page = Render(context, "customer");

        page.Find(".ms-code-sql").TextContent.Should().Contain("mt_doc_customer");
        page.Find(".ms-sql-parameters").TextContent.Should().Contain("@limit");
    }

    [Fact]
    public async Task A_red_verdict_is_described_but_not_run_until_run_anyway_is_pressed()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(
            state: DocumentListState.BlockedByVerdict,
            verdict: new SearchVerdict { Level = IndexVerdictLevel.Red });

        var page = Render(context, "customer");

        page.FindAll(".ms-doc-table").Should().BeEmpty("the read was withheld");
        context.Data.Requests.Should().ContainSingle().Which.RunAnyway.Should().BeFalse();

        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("1")]);
        await page.Find(".ms-verdict-blocked .ms-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        context.Data.Requests[^1].RunAnyway.Should().BeTrue();
        page.FindAll(".ms-doc-row").Should().ContainSingle();
    }

    [Fact]
    public void The_deleted_tri_state_is_offered_only_where_there_is_something_to_include()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents:
        [
            FakeDocuments.Collection("customer"),
            FakeDocuments.Collection("order", softDeleted: true),
        ]);
        context.Data.Page = FakeDocuments.Page();

        Render(context, "customer").FindAll(".ms-segmented").Should().ContainSingle("only the paging toggle");

        using var second = new DocumentsComponentContext();
        second.Data.Rail = context.Data.Rail;
        second.Data.Page = FakeDocuments.Page();

        Render(second, "order").FindAll(".ms-segmented").Should().HaveCount(2);
    }

    [Fact]
    public void The_column_chooser_offers_the_table_columns_and_the_json_keys_sampled_from_the_page()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page() with
        {
            JsonSuggestions = [new JsonPropertySuggestion("Name", 3), new JsonPropertySuggestion("Email", 1)],
        };

        var page = Render(context, "customer");

        page.TextOfAll(".ms-column-chooser-label").Should().Contain(["id", "last modified", "Name", "Email"]);
    }

    [Fact]
    public async Task Choosing_a_column_mirrors_it_into_the_url_and_remembers_it_in_the_browser()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page() with
        {
            JsonSuggestions = [new JsonPropertySuggestion("Name", 3)],
        };

        var page = Render(context, "customer");

        var checkbox = page.FindAll(".ms-column-chooser-option input")
            .First(x => x.ParentElement!.TextContent.Contains("Name", StringComparison.Ordinal));

        await checkbox.ChangeAsync(new ChangeEventArgs { Value = true });

        context.CurrentUri.Should().Contain("cols=");
        context.CurrentUri.Should().Contain("json%3AName");
        context.JSInterop.Invocations.Should().Contain(x => x.Identifier == "martenStudio.prefs.set");
    }

    [Fact]
    public async Task Sorting_by_a_column_puts_the_key_and_the_direction_in_the_url()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("1")]);

        var page = Render(context, "customer");

        await page.FindAll(".ms-doc-sort")[1].ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        context.CurrentUri.Should().Contain("sort=meta%3ALastModified");
        context.CurrentUri.Should().Contain("dir=desc");
    }

    [Fact]
    public async Task Next_is_only_offered_when_there_is_another_page_and_it_carries_the_cursor()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("1")], hasMore: true);

        var page = Render(context, "customer");

        var next = page.FindAll(".ms-pager .ms-btn").First(x => x.TextContent.Trim() == "Next");
        next.HasAttribute("disabled").Should().BeFalse();

        await next.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        context.CurrentUri.Should().Contain("cursor=");
    }

    [Fact]
    public void The_pager_says_why_offset_paging_stops_rather_than_silently_returning_nothing()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("1")], hasMore: true);
        context.Navigate("marten/documents/customer?paging=offset&page=500&size=50");

        var page = Render(context, "customer");

        page.Find(".ms-pager-note").TextContent.Should().Contain("Offset paging stops at");
    }

    [Fact]
    public async Task A_row_expands_into_a_compact_json_preview_and_never_needs_a_hover()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("1", """{"Name":"Customer 01"}""")]);

        var page = Render(context, "customer");

        page.FindAll(".ms-doc-preview-row").Should().BeEmpty();

        await page.Find(".ms-doc-expander").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        page.Find(".ms-doc-preview-row").TextContent.Should().Contain("Customer 01");
    }

    [Fact]
    public async Task A_document_too_large_to_inline_says_so_instead_of_showing_an_empty_one()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("1", json: null, size: 2_000_000)]);

        var page = Render(context, "customer");
        await page.Find(".ms-doc-expander").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        page.Find(".ms-doc-preview-empty").TextContent.Should().Contain("over the studio's inline budget");
    }

    [Fact]
    public void The_recent_pseudo_collection_lists_documents_of_every_type_with_their_colour()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Recent =
        [
            new RecentDocument("customer", "1", new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero), 200),
            new RecentDocument("order", "2", new DateTimeOffset(2026, 9, 14, 11, 0, 0, TimeSpan.Zero), 100),
        ];

        var page = Render(context, CollectionAliases.Recent);

        page.TextOfAll(".ms-doc-table tbody .ms-collection-alias").Should().Equal("customer", "order");
    }

    [Fact]
    public void An_unregistered_collection_is_marked_read_only()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(discovered: [FakeDocuments.Collection("orphan", registered: false)]);
        context.Data.Page = FakeDocuments.Page();

        var page = Render(context, "orphan");

        page.Find(".ms-page-header .ms-badge-warning").TextContent.Trim().Should().Be("read-only");
    }

    private static IRenderedComponent<MartenStudio.Components.Pages.Documents.Documents> Render(
        DocumentsComponentContext context,
        string alias) =>
        context.Render<MartenStudio.Components.Pages.Documents.Documents>(
            parameters => parameters.Add(x => x.Alias, alias));
}
