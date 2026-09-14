using AngleSharp.Dom;

using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using ListPage = MartenStudio.Components.Pages.Documents.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// What the documents browser says when it is looking at a real store rather than a demo one: an id that
/// is a GUID, a collection Postgres has never analysed, and a sort nothing indexes.
/// </summary>
/// <remarks>
/// Every case here came off a real browser or off the P2-fix review, and each was a screen telling
/// somebody something that was not true — a red FULL SCAN banner over a page nobody had filtered, a
/// collection that looked empty because its count was unknown, four lines of wrapped GUID per row.
/// </remarks>
public class DocumentListPolishTests
{
    private const string Guid1 = "8f1d5a6e-1a2b-4c3d-9e8f-000000000001";

    // ------------------------------------------------------------------------------------------------
    // Id columns
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The clip is a stylesheet's job, but it only works on markup that names the cell and the text. The
    /// title is what keeps the whole id available once the cell stops showing all of it.
    /// </summary>
    [Fact]
    public void An_id_cell_names_itself_and_carries_the_whole_id_in_its_title()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row(Guid1)]);

        var page = Render(context, "customer");

        IElement link = page.Find(".ms-doc-id");
        link.ClassList.Should().Contain("ms-id-text", "the stylesheet clips the id through this class");
        link.GetAttribute("title").Should().Be(Guid1, "the clip must not be the only copy of the id");

        page.Find("td.ms-doc-cell-id").ClassList.Should().Contain(
            "ms-id-cell",
            "the cell is what keeps the badges beside an id on the same line as it");
    }

    /// <summary>The Recent table shows ids too, and wrapped them in exactly the same way.</summary>
    [Fact]
    public void The_recent_table_clips_its_ids_the_same_way()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Recent =
        [
            new RecentDocument("customer", Guid1, DateTimeOffset.UnixEpoch, Hue: 0),
        ];

        var page = Render(context, CollectionAliases.Recent);

        IElement link = page.Find(".ms-doc-id");
        link.ClassList.Should().Contain("ms-id-text");
        link.GetAttribute("title").Should().Be(Guid1);
        link.ParentElement!.ClassList.Should().Contain("ms-id-cell");
    }

    // ------------------------------------------------------------------------------------------------
    // Verdict placement
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marten declares no index on <c>mt_last_modified</c>, so the default sort is red on its own and the
    /// strip was red above every collection in the store. A banner that is always there is one nobody
    /// reads, and it drowned out the filter verdicts the strip exists for.
    /// </summary>
    [Fact]
    public void A_sort_only_verdict_marks_the_column_and_leaves_the_strip_alone()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row(Guid1)], verdict: SortOnly());

        var page = Render(context, "customer");

        page.Find(".ms-search-strip").ClassList.Should().NotContain(
            "ms-verdict-red",
            "nothing was filtered, so there is no filter to call a full scan");
        page.Find(".ms-verdict-badge").TextContent.Trim().Should().Be("no filter");
        page.FindAll(".ms-verdict-reason").Should().BeEmpty("the sort's reason belongs beside its column");

        IElement hint = page.Find(".ms-doc-sort-hint");
        hint.ClassList.Should().Contain("ms-doc-sort-hint-red");
        hint.GetAttribute("title").Should().Contain("mt_last_modified");
    }

    /// <summary>The hint sits on the column that is actually sorted, and on no other.</summary>
    [Fact]
    public void The_sort_hint_is_rendered_once_beside_the_sorted_column()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row(Guid1)], verdict: SortOnly());

        var page = Render(context, "customer");

        page.FindAll(".ms-doc-sort-hint").Should().ContainSingle();
        page.Find("th:has(.ms-doc-sort-hint) .ms-doc-sort").ClassList.Should().Contain("ms-doc-sort-on");
    }

    /// <summary>A filter that reads every row still gets the banner: that is what the strip is for.</summary>
    [Fact]
    public void A_filter_verdict_still_paints_the_strip_red_and_states_its_reason()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row(Guid1)], verdict: new SearchVerdict
        {
            Level = IndexVerdictLevel.Red,
            FilterLevel = IndexVerdictLevel.Red,
            Chips =
            [
                new SearchChip(SearchChipKind.Predicate, "Nickname = bob", IndexVerdictLevel.Red,
                    "Nickname is only in the JSON.", "options.Schema.For<Customer>().Duplicate(x => x.Nickname);"),
                SortChip(),
            ],
        });

        var page = Render(context, "customer");

        page.Find(".ms-search-strip").ClassList.Should().Contain("ms-verdict-red");
        page.Find(".ms-verdict-badge").TextContent.Trim().Should().Be("full scan");
        page.TextOfAll(".ms-search-chip").Should().Equal(
            ["Nickname = bob"],
            "the sort is not a term somebody typed into the search box");
        page.FindAll(".ms-verdict-reason").Should().ContainSingle();

        page.FindAll(".ms-doc-sort-hint").Should().ContainSingle(
            "the sort's own verdict is still said, beside the column it is about");
    }

    /// <summary>A sort an index can serve says nothing: "this is indexed" is not news beside a header.</summary>
    [Fact]
    public void A_green_sort_renders_no_hint_at_all()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row(Guid1)], verdict: new SearchVerdict
        {
            Chips = [new SearchChip(SearchChipKind.Sort, "sort", IndexVerdictLevel.Green, "id is the primary key.", null)],
        });

        var page = Render(context, "customer");

        page.FindAll(".ms-doc-sort-hint").Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // Unknown counts
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>reltuples</c> is -1 for a table Postgres has never analysed, which is not "no rows". Reporting
    /// it as <c>~0</c> is the studio saying "empty" about a collection it has not looked at.
    /// </summary>
    [Fact]
    public void An_unknown_count_is_a_question_mark_in_the_rail_and_says_why()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents:
        [
            FakeDocuments.Collection("customer") with { Count = DocumentCount.Unknown },
        ]);

        var page = context.Render<ListPage>();

        IElement badge = page.Find(".ms-rail-badge-count");
        badge.TextContent.Trim().Should().Be("?");
        badge.GetAttribute("title").Should().Be("Postgres has never analysed this table");
    }

    /// <summary>
    /// A "?" nobody can resolve is a dead end, and an unanalysed table is precisely the one where the
    /// exact count is the only number there is.
    /// </summary>
    [Fact]
    public void An_unknown_count_still_offers_the_exact_count_button()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents:
        [
            FakeDocuments.Collection("customer") with { Count = DocumentCount.Unknown },
        ]);

        var page = context.Render<ListPage>();

        IElement? button = page.Nodes.QuerySelector(".ms-rail-item .ms-rail-action");
        button.Should().NotBeNull("an unknown count has to be resolvable");
        button!.HasAttribute("disabled").Should().BeFalse();
        button.GetAttribute("title").Should().Contain("Count the rows exactly");
    }

    /// <summary>A read that was refused draws nothing, which is a different fact and looks like one.</summary>
    [Fact]
    public void An_unavailable_count_still_draws_no_badge_at_all()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents:
        [
            FakeDocuments.Collection("customer") with { Count = DocumentCount.Unavailable },
        ]);

        var page = context.Render<ListPage>();

        page.FindAll(".ms-rail-badge-count").Should().BeEmpty();
    }

    [Fact]
    public void The_list_header_says_unknown_size_rather_than_nothing()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page() with { Estimate = DocumentCount.Unknown };

        var page = Render(context, "customer");

        IElement subtitle = page
            .FindAll(".ms-page-header .ms-doc-subtitle")
            .Single(static x => x.TextContent.Contains("unknown size", StringComparison.Ordinal));

        subtitle.GetAttribute("title").Should().Contain("never analysed");
    }

    [Fact]
    public void A_known_estimate_still_reads_as_a_count()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page() with { Estimate = DocumentCount.Estimate(1234) };

        var page = Render(context, "customer");

        page.TextOfAll(".ms-page-header .ms-doc-subtitle").Should().Contain("~1,234 documents");
    }

    private static SearchChip SortChip() => new(
        SearchChipKind.Sort,
        "sort",
        IndexVerdictLevel.Red,
        "Nothing indexes mt_last_modified, so ordering by it sorts the whole collection.",
        null);

    /// <summary>
    /// The verdict very nearly every collection in a real store produces: nothing filtered, and a sort
    /// key no index serves.
    /// </summary>
    private static SearchVerdict SortOnly() => new()
    {
        Level = IndexVerdictLevel.Red,
        FilterLevel = IndexVerdictLevel.Green,
        Chips = [SortChip()],
    };

    private static IRenderedComponent<ListPage> Render(DocumentsComponentContext context, string alias) =>
        context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, alias));
}
