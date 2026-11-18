using AngleSharp.Dom;

using Bunit;

using Microsoft.JSInterop;

using DataStudio.Marten.Tests.Components;
using DataStudio.Postgres.Components.Pages.Health;
using DataStudio.Postgres.Services.Health;

namespace DataStudio.Marten.Tests.Health;

/// <summary>
/// Issue #87: the Health page keeps its areas and their order, so a Critical finding can be the 130th card of the page, below every Warning
/// of the areas before it. One compact region above the first area names each Critical finding the visitor may see - its code and its title -
/// and links its card; with none to show it is not drawn, and a Critical finding the gates withhold is counted by the notice and never named.
/// </summary>
public class HealthCriticalListTests
{
    private const string Reveal = "martenStudio.health.revealFinding";

    private const string Focus = "Blazor._internal.domWrapper.focus";

    private static async Task<StudioComponentContext> ReadyAsync(params HealthFinding[] findings)
    {
        StudioComponentContext context = new();
        await context.ReadyAsync();
        context.Health.WithFindings(findings);
        return context;
    }

    private static HealthFinding Warning(string code, HealthArea area = HealthArea.Tables) =>
        FakeHealthService.Finding(code, HealthSeverity.Warning, "Warning " + code, area);

    private static HealthFinding Critical(string code, string title, HealthArea area = HealthArea.Configuration) =>
        FakeHealthService.Finding(code, HealthSeverity.Critical, title, area);

    [Fact]
    public async Task One_critical_finding_is_named_above_the_first_area_with_its_code_and_title()
    {
        await using StudioComponentContext context = await ReadyAsync(Warning("T04"), Warning("T05"), Critical("S01", "PostgreSQL 14 is past its end of life"));

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-critical").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        page.Find(".ms-health-critical .ms-section-title").TextContent.Trim().Should().Be("1 critical finding");

        IElement link = page.Find(".ms-health-critical-link");
        link.QuerySelector(".ms-health-code")!.TextContent.Trim().Should().Be("S01");
        link.QuerySelector(".ms-health-critical-title")!.TextContent.Trim().Should().Be("PostgreSQL 14 is past its end of life");

        // Above the first area, in the document's order.
        string markup = page.Markup;
        markup.IndexOf("ms-health-critical\"", StringComparison.Ordinal).Should().BePositive().And
            .BeLessThan(markup.IndexOf("ms-health-area\"", StringComparison.Ordinal),
                "the region is drawn before any area, so a Critical finding is never reached by scrolling past the Warnings");
    }

    [Fact]
    public async Task Several_critical_findings_are_each_named_and_a_warning_is_not()
    {
        await using StudioComponentContext context = await ReadyAsync(
            Warning("T04"),
            Critical("S01", "PostgreSQL 14 is past its end of life"),
            Critical("D01", "The high-water mark is pinned", HealthArea.Daemon),
            FakeHealthService.Finding("K01", HealthSeverity.Info, "Information only"));

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-critical-link").Should().HaveCount(2), TimeSpan.FromSeconds(5));
        page.Find(".ms-health-critical .ms-section-title").TextContent.Trim().Should().Be("2 critical findings");
        page.TextOfAll(".ms-health-critical-link .ms-health-code").Should().BeEquivalentTo("S01", "D01");
    }

    [Fact]
    public async Task With_no_critical_finding_the_region_is_not_drawn()
    {
        await using StudioComponentContext context = await ReadyAsync(Warning("T04"), FakeHealthService.Finding("K01", HealthSeverity.Info, "Information only"));

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-area").Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        page.FindAll(".ms-health-critical").Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_finding_at_all_the_region_is_not_drawn()
    {
        await using StudioComponentContext context = await ReadyAsync();

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-empty").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        page.FindAll(".ms-health-critical").Should().BeEmpty();
    }

    /// <summary>
    /// The report holds only what the gates let this visitor see: a Critical finding withheld from them is a count in the notice and is not in
    /// the list - not by code, not by title - and a visitor who is shown a Warning alone gets no region.
    /// </summary>
    [Fact]
    public async Task A_critical_finding_the_visitor_is_not_shown_is_counted_and_never_named()
    {
        await using StudioComponentContext context = await ReadyAsync(Warning("T04"));
        context.Health.Report = context.Health.Report with
        {
            WithheldCount = 1,
            WithheldReason = "needs the store policy to allow this database as a whole",
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-withheld").TextContent.Should().Contain("1 finding not shown to you"), TimeSpan.FromSeconds(5));
        page.FindAll(".ms-health-critical").Should().BeEmpty("the one Critical finding is withheld, so nothing here could name it");
        page.Markup.Should().NotContain("S01");
    }

    /// <summary>
    /// A host that accepted a check (<c>AcceptedChecks</c>, #35) has said its Critical finding is not one: it is shown in its check's row, out of
    /// every count, and so out of the list that asks for attention. The one that is not accepted is listed beside it, so the list is not empty for
    /// another reason.
    /// </summary>
    [Fact]
    public async Task An_accepted_critical_finding_is_not_in_the_list_and_one_that_is_not_accepted_is()
    {
        await using StudioComponentContext context = await ReadyAsync(
            Warning("T04"),
            Critical("K05", "The console reads as the data source's role") with { Accepted = true, AcceptedReason = "Roles are managed by the platform team." },
            Critical("S01", "PostgreSQL 14 is past its end of life"));

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-critical-link").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        page.Find(".ms-health-critical .ms-section-title").TextContent.Trim().Should().Be("1 critical finding");
        page.TextOfAll(".ms-health-critical-link .ms-health-code").Should().Equal("S01");
        page.Find(".ms-health-critical").TextContent.Should().NotContain("K05").And.NotContain("The console reads as the data source's role");
    }

    [Fact]
    public async Task An_accepted_critical_finding_alone_draws_no_region()
    {
        await using StudioComponentContext context = await ReadyAsync(
            Warning("T04"),
            Critical("K05", "The console reads as the data source's role") with { Accepted = true, AcceptedReason = "Roles are managed by the platform team." });

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-area").Should().NotBeEmpty(), TimeSpan.FromSeconds(5));
        page.FindAll(".ms-health-critical").Should().BeEmpty("an accepted Critical finding is not one the page asks attention for");
    }

    /// <summary>A finding the visitor muted in this browser is out of every count, the list included, and is kept under "muted in this browser".</summary>
    [Fact]
    public async Task A_muted_critical_finding_leaves_the_list_and_is_kept_under_the_muted_ones()
    {
        await using StudioComponentContext context = await ReadyAsync(Warning("T04"), Critical("S01", "PostgreSQL 14 is past its end of life"));

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-critical-link").Should().ContainSingle(), TimeSpan.FromSeconds(5));

        page.Find(".ms-health-finding-critical .ms-health-mute").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-health-critical").Should().BeEmpty("the one Critical finding is muted"), TimeSpan.FromSeconds(5));
        page.Find(".ms-health-muted summary").TextContent.Should().Contain("1 finding muted in this browser");
        page.Find(".ms-health-muted").TextContent.Should().Contain("S01");
    }

    [Fact]
    public async Task Each_links_target_is_a_card_on_the_page_and_is_the_address_of_the_page_with_that_card_as_its_fragment()
    {
        await using StudioComponentContext context = await ReadyAsync(
            Warning("T04"),
            Critical("S01", "PostgreSQL 14 is past its end of life"),
            Critical("D01", "The high-water mark is pinned", HealthArea.Daemon));

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-critical-link").Should().HaveCount(2), TimeSpan.FromSeconds(5));

        foreach (IElement link in page.FindAll(".ms-health-critical-link"))
        {
            string href = link.GetAttribute("href")!;
            int hash = href.IndexOf('#', StringComparison.Ordinal);

            hash.Should().BePositive("the link names a card");
            href[..hash].Should().StartWith("postgres/health", "it is the Health page's own address, so it works whichever page the visitor is on");

            string id = href[(hash + 1)..];
            IElement card = page.Find("#" + id);
            card.TagName.Should().Be("ARTICLE");
            card.GetAttribute("tabindex").Should().Be("-1", "a card can take focus");
            card.ClassList.Should().Contain("ms-health-finding-critical");
            page.FindAll("#" + id).Should().ContainSingle("an element id is one per page");
        }
    }

    [Fact]
    public async Task Following_a_link_scrolls_to_its_card_and_moves_focus_to_it()
    {
        await using StudioComponentContext context = await ReadyAsync(Warning("T04"), Critical("D01", "The high-water mark is pinned", HealthArea.Daemon));
        context.JSInterop.Setup<bool>(Reveal, "finding-d01").SetResult(true);

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-health-critical-link").Should().ContainSingle(), TimeSpan.FromSeconds(5));

        page.Find(".ms-health-critical-link").Click();

        page.WaitForAssertion(() =>
        {
            context.JSInterop.Invocations.Where(static x => x.Identifier == Reveal).Should().ContainSingle().Which.Arguments.Should().Equal("finding-d01");
            context.JSInterop.Invocations.Where(static x => x.Identifier == Focus).Should().ContainSingle("focus lands on the card the link names");
        }, TimeSpan.FromSeconds(5));
    }

    /// <summary>A circuit that closed under the call: the link is right without the scroll, and nothing is thrown at the visitor.</summary>
    [Fact]
    public async Task A_circuit_that_closed_under_the_click_breaks_nothing()
    {
        await using StudioComponentContext context = await ReadyAsync(Critical("D01", "The high-water mark is pinned", HealthArea.Daemon));
        context.JSInterop.Setup<bool>(Reveal, "finding-d01").SetException(new JSDisconnectedException("closed"));

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-health-critical-link").Should().ContainSingle(), TimeSpan.FromSeconds(5));

        page.Find(".ms-health-critical-link").Click();

        page.FindAll(".ms-health-critical-link").Should().ContainSingle();
        page.FindAll(".ms-alert-error").Should().BeEmpty();
    }
}
