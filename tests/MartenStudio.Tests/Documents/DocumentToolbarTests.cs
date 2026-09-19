using AngleSharp.Dom;

using Bunit;

using MartenStudio.Tests.Support;

using ListPage = MartenStudio.Components.Pages.Documents.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The row above the document list, and the one thing on it that used to break it: the search grammar
/// hint.
/// </summary>
/// <remarks>
/// It was a <c>&lt;details&gt;</c> inside the search <c>&lt;form&gt;</c>, which is a flex row. Opening it
/// put a 170-px column of wrapped bullets into that row, the input collapsed to about 150 px, and the
/// toolbar reflowed around it — the help got in the way of the thing it was helping with. The fix is the
/// platform's own popover, which renders in the top layer and so is not in the row, not in the flex
/// container and not in layout at all. These tests hold the shape that makes that true: the help is
/// <em>not</em> a child that layout can see, the input is not inside it, and the button says whether it
/// is open.
/// </remarks>
public class DocumentToolbarTests
{
    /// <summary>
    /// The one structural fact the whole fix rests on: the help is a popover, so the browser takes it out
    /// of the flow, and the search input is not inside it.
    /// </summary>
    [Fact]
    public void The_syntax_help_is_a_popover_and_not_a_sibling_of_the_search_input()
    {
        using var context = Loaded();

        var page = Render(context);

        IElement help = page.Find(".ms-doc-syntax-popover");

        help.GetAttribute("popover").Should().Be(
            "auto",
            "the top layer is what keeps the help out of the search row, and `auto` is what gives it " +
            "Escape and light dismiss without a handler of ours");

        help.QuerySelector("#ms-doc-search-input").Should().BeNull(
            "the search box must never be inside the thing that opens over it");

        page.Find(".ms-doc-search #ms-doc-search-input").Should().NotBeNull();

        page.FindAll(".ms-doc-search details").Should().BeEmpty(
            "a <details> in the search row is a flex sibling, and opening one moves everything beside it");
    }

    /// <summary>The button is the invoker, and it names the element it opens.</summary>
    [Fact]
    public void The_syntax_button_points_at_the_help_it_opens()
    {
        using var context = Loaded();

        var page = Render(context);

        IElement button = page.Find(".ms-doc-syntax-button");

        button.TextContent.Trim().Should().Be("Syntax");
        button.GetAttribute("type").Should().Be("button", "a bare button inside a form submits it");
        button.GetAttribute("popovertarget").Should().Be(page.Find(".ms-doc-syntax-popover").Id);
        button.GetAttribute("aria-controls").Should().Be(page.Find(".ms-doc-syntax-popover").Id);
    }

    /// <summary>
    /// The browser opens and closes a popover on its own — Escape, a click anywhere else — so the flag
    /// behind <c>aria-expanded</c> is driven by the toggle event rather than by the click that opened it.
    /// </summary>
    [Fact]
    public async Task The_syntax_button_reports_whether_the_help_is_open()
    {
        using var context = Loaded();

        var page = Render(context);

        page.Find(".ms-doc-syntax-button").GetAttribute("aria-expanded").Should().Be("false");

        await page.Find(".ms-doc-syntax-popover").TriggerEventAsync("ontoggle", EventArgs.Empty);
        page.Find(".ms-doc-syntax-button").GetAttribute("aria-expanded").Should().Be("true");

        await page.Find(".ms-doc-syntax-popover").TriggerEventAsync("ontoggle", EventArgs.Empty);
        page.Find(".ms-doc-syntax-button").GetAttribute("aria-expanded").Should().Be(
            "false",
            "a dismiss the circuit never heard about still has to leave the button telling the truth");
    }

    /// <summary>
    /// Every line of the old bullet list is still there, as a definition list: the syntax on the left and
    /// what it means on the right. Nothing was dropped in the move.
    /// </summary>
    [Fact]
    public void The_help_still_explains_every_term_the_grammar_understands()
    {
        using var context = Loaded();

        var page = Render(context);

        IElement list = page.Find(".ms-doc-syntax-list");

        list.TagName.Should().Be("DL");
        list.QuerySelectorAll("dt").Length.Should().Be(list.QuerySelectorAll("dd").Length);

        var text = list.TextContent;

        foreach (var term in new[] { "id:", "~", "@>", "is:deleted", "is:not-deleted", "tenant:acme" })
        {
            text.Should().Contain(term);
        }

        text.Should().Contain("substring of the whole document");
        text.Should().Contain("GIN index");
    }

    private static DocumentsComponentContext Loaded()
    {
        var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("8f1d5a6e-1a2b-4c3d-9e8f-000000000001")]);
        return context;
    }

    private static IRenderedComponent<ListPage> Render(DocumentsComponentContext context) =>
        context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));
}
