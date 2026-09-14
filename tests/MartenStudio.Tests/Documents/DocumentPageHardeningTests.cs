using Bunit;

using MartenStudio.Components.Pages.Documents;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Support;

using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using ListPage = MartenStudio.Components.Pages.Documents.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The three ways the documents pages could be driven into a state nobody intended, found by the P2
/// review.
/// </summary>
public class DocumentPageHardeningTests
{
    private const string Id = "8f1d5a6e-0000-0000-0000-000000000001";

    // --------------------------------------------------------------------------------------------
    // Duplicate search terms
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>SearchChip</c> is a record, so two identical terms are equal to each other. Keyed by the chip,
    /// the renderer threw "more than one sibling has the same key value" - which on a Blazor Server
    /// circuit is a 500 and a dead page, produced by typing the same word twice into a search box.
    /// </summary>
    [Theory]
    [InlineData("foo foo")]
    [InlineData("is:deleted is:deleted")]
    [InlineData("..")]
    public void The_same_search_term_twice_renders_rather_than_killing_the_circuit(string search)
    {
        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail(documents: [FakeDocuments.Collection("customer")]);
        context.Data.Page = FakeDocuments.Page(
            rows: [FakeDocuments.Row("id-1")],
            verdict: Verdict(search));

        context.Navigate("marten/documents/customer?q=" + Uri.EscapeDataString(search));
        var page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        // Rendered twice on purpose. Blazor only compares keys when it *diffs* two renders of the same
        // tree, so a first render with duplicate keys is quietly fine and the second one throws - which
        // is why the bug showed up as a page that worked until something on it changed.
        page.Invoking(x => x.Render(parameters => parameters.Add(y => y.Alias, "customer")))
            .Should().NotThrow("two identical search terms are a search, not a crash");

        page.FindAll(".ms-search-chip").Should().HaveCount(2, "both terms are drawn, identical or not");
        page.FindAll(".ms-verdict-reason").Should().HaveCount(2);
    }

    /// <summary>Two chips that are equal as values but are two different terms on the screen.</summary>
    private static SearchVerdict Verdict(string text)
    {
        string[] terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string term = terms.Length > 0 ? terms[0] : text;

        SearchChip chip = new(
            SearchChipKind.Predicate,
            term,
            IndexVerdictLevel.Red,
            "This reads every row of the collection.",
            "opts.Schema.For<Customer>().Duplicate(x => x.Name);");

        return SearchVerdict.Empty with { Chips = [chip, chip] };
    }

    // --------------------------------------------------------------------------------------------
    // The return link
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>?from=</c> is reflected into an <c>href</c>, so it is an injection point. The studio's own
    /// value is a relative path under the mount; anything else is a link somebody else wrote.
    /// </summary>
    [Theory]
    [InlineData("javascript:alert(document.domain)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("/\\evil.example/steal")]
    [InlineData("\\\\evil.example\\steal")]
    [InlineData("/marten/documents/customer")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("documents\\..\\..\\evil")]
    [InlineData("query?mode=sql")]
    public void A_return_link_the_studio_did_not_write_is_not_rendered(string from)
    {
        SafeReturnLink.Sanitize(from, new MartenStudioOptions()).Should().BeNull();
    }

    [Theory]
    [InlineData("documents/customer")]
    [InlineData("documents/customer?q=is:deleted&sort=id&dir=desc")]
    [InlineData("documents")]
    [InlineData("marten/documents/customer?page=2")]
    public void A_return_link_into_the_documents_area_is_kept_exactly_as_it_was(string from)
    {
        SafeReturnLink.Sanitize(from, new MartenStudioOptions()).Should().Be(from);
    }

    [Fact]
    public void The_breadcrumb_falls_back_to_the_collection_when_the_return_link_is_poisoned()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        context.Navigate(
            $"marten/documents/customer/doc?id={Id}&from=" +
            Uri.EscapeDataString("javascript:alert(document.domain)"));

        var page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        string href = page.Find(".ms-breadcrumb a:nth-of-type(2)").GetAttribute("href")!;

        href.Should().NotContain("javascript");
        href.Should().StartWith("documents/customer", "the fallback is where back was going anyway");
    }

    [Fact]
    public void A_return_link_the_studio_wrote_still_works()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        const string from = "documents/customer?q=ada&page=2";
        context.Navigate($"marten/documents/customer/doc?id={Id}&from=" + Uri.EscapeDataString(from));

        var page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        page.Find(".ms-breadcrumb a:nth-of-type(2)").GetAttribute("href").Should().Be(from);
    }

    /// <summary>
    /// The previous/next links carry the return link onward, so a poisoned one must not survive one hop
    /// either - otherwise the fix only moves where the payload lands.
    /// </summary>
    [Fact]
    public void A_poisoned_return_link_is_not_carried_onto_the_neighbour_links()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Browser.RememberPage("customer", ["other-id", Id, "next-id"], "documents/customer");

        context.Navigate(
            $"marten/documents/customer/doc?id={Id}&from=" + Uri.EscapeDataString("javascript:alert(1)"));

        var page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        foreach (var link in page.FindAll(".ms-doc-detail-nav a"))
        {
            link.GetAttribute("href").Should().NotContain("javascript");
        }
    }

    // --------------------------------------------------------------------------------------------
    // Interop that is simply gone
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>JSDisconnectedException</c> derives from <see cref="Exception" />, not from
    /// <c>JSException</c>, so every <c>catch (JSException)</c> in the studio missed the commonest case
    /// of all: the circuit closing while a call was in flight.
    /// </summary>
    [Fact]
    public void A_closed_circuit_is_an_interop_failure_and_so_is_a_prerender()
    {
        DocumentsInterop.IsUnavailable(new Microsoft.JSInterop.JSDisconnectedException("gone")).Should().BeTrue();
        DocumentsInterop.IsUnavailable(new Microsoft.JSInterop.JSException("no such function")).Should().BeTrue();
        DocumentsInterop.IsUnavailable(new InvalidOperationException("prerendering")).Should().BeTrue();
        DocumentsInterop.IsUnavailable(new TaskCanceledException()).Should().BeTrue();
        DocumentsInterop.IsUnavailable(new System.Text.Json.JsonException("bad shape")).Should().BeTrue();

        DocumentsInterop.IsUnavailable(new NotSupportedException()).Should().BeFalse(
            "a real bug has to keep reaching the error handler");
    }

    [Fact]
    public void A_copy_whose_circuit_has_gone_does_not_take_the_page_with_it()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true)
            .SetException(new Microsoft.JSInterop.JSDisconnectedException("the circuit closed"));

        context.Navigate($"marten/documents/customer/doc?id={Id}");
        var page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        page.Invoking(x => x.FindAll("button").First(b => b.TextContent.Trim() == "Copy JSON").Click())
            .Should().NotThrow();
    }

    private static DetailModel Detail() => new()
    {
        Alias = "customer",
        Id = Id,
        Json = """{"Name":"Customer 01"}""",
        TextBytes = 22,
        StoredBytes = 22,
        Columns = [new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity)],
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        IdColumnType = DocumentIdColumnType.Uuid,
    };
}
