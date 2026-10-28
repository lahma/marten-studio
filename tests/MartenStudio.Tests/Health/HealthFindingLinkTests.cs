using Bunit;

using Microsoft.JSInterop;

using PostgresStudio.Components.Pages.Health;
using MartenStudio.Tests.Components;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// A finding's address ends in its card's fragment (<c>health?store=x#finding-c08-temp-files</c>), and the browser resolves
/// that against the prerendered document, before the circuit has read the sweep and drawn the cards (issue #33). The page
/// scrolls to the card itself once the report is on screen - through <c>martenStudio.health.revealFinding</c> in
/// <c>studio.js</c> - or says the finding is no longer reported.
/// </summary>
public class HealthFindingLinkTests
{
    private const string Reveal = "martenStudio.health.revealFinding";

    private const string Gone = "This finding is no longer reported.";

    private static async Task<StudioComponentContext> ReadyAsync(string address, params HealthFinding[] findings)
    {
        StudioComponentContext context = new();
        await context.ReadyAsync();
        context.Health.WithFindings(findings);
        context.Navigate(address);
        return context;
    }

    private static HealthFinding K01 => FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production");

    private static HealthFinding D01 => FakeHealthService.Finding("D01", HealthSeverity.Critical, "The high-water mark is pinned", HealthArea.Daemon);

    private static IReadOnlyList<JSRuntimeInvocation> Reveals(StudioComponentContext context) =>
        [.. context.JSInterop.Invocations.Where(static x => x.Identifier == Reveal)];

    [Fact]
    public async Task The_page_asks_the_script_to_reveal_the_card_the_fragment_names_once_the_cards_are_drawn()
    {
        await using StudioComponentContext context = await ReadyAsync("marten/health#finding-d01", K01, D01);
        context.JSInterop.Setup<bool>(Reveal, "finding-d01").SetResult(true);

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => Reveals(context).Should().ContainSingle(), TimeSpan.FromSeconds(5));
        Reveals(context).Single().Arguments.Should().Equal("finding-d01");

        // The card the script is asked for is in the markup the call follows: it is called after the render that drew it.
        page.FindAll("#finding-d01").Should().ContainSingle();
        page.FindAll(".ms-health-finding-gone").Should().BeEmpty();
    }

    [Fact]
    public async Task A_refresh_after_it_does_not_scroll_the_visitor_back()
    {
        await using StudioComponentContext context = await ReadyAsync("marten/health#finding-d01", K01, D01);
        context.JSInterop.Setup<bool>(Reveal, "finding-d01").SetResult(true);

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => Reveals(context).Should().ContainSingle(), TimeSpan.FromSeconds(5));
        int requests = context.Health.Requests.Count;

        page.Find(".ms-health-actions .ms-button").Click();
        page.WaitForAssertion(() => context.Health.Requests.Count.Should().BeGreaterThan(requests), TimeSpan.FromSeconds(5));
        page.WaitForAssertion(() => page.FindAll("#finding-d01").Should().ContainSingle());

        Reveals(context).Should().ContainSingle("the address is dealt with once, not on every poll");
    }

    [Fact]
    public async Task A_card_the_report_does_not_hold_is_said_to_be_gone_and_the_script_is_not_asked_for_it()
    {
        await using StudioComponentContext context = await ReadyAsync("marten/health#finding-c08-temp-files", K01);

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-finding-gone").TextContent.Trim().Should().Be(Gone), TimeSpan.FromSeconds(5));
        page.Find(".ms-health-finding-gone").GetAttribute("role").Should().Be("status");
        Reveals(context).Should().BeEmpty("only an id the report holds goes to the script");
        page.FindAll("#finding-k01").Should().ContainSingle("the rest of the page is untouched");
    }

    [Fact]
    public async Task A_card_the_script_cannot_find_is_said_to_be_gone_too()
    {
        await using StudioComponentContext context = await ReadyAsync("marten/health#finding-d01", D01);
        context.JSInterop.Setup<bool>(Reveal, "finding-d01").SetResult(false);

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-finding-gone").TextContent.Trim().Should().Be(Gone), TimeSpan.FromSeconds(5));
    }

    /// <summary>The percent-encoded form of a fragment, as a browser may write it in the address, names the same card.</summary>
    [Fact]
    public async Task An_encoded_fragment_names_the_same_card()
    {
        await using StudioComponentContext context = await ReadyAsync("marten/health#finding%2Dd01", D01);
        context.JSInterop.Setup<bool>(Reveal, "finding-d01").SetResult(true);

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => Reveals(context).Should().ContainSingle(), TimeSpan.FromSeconds(5));
        page.FindAll(".ms-health-finding-gone").Should().BeEmpty();
    }

    [Theory]
    [InlineData("marten/health")]
    [InlineData("marten/health#")]
    [InlineData("marten/health#top")]
    [InlineData("marten/health?store=default#what-was-checked")]
    public async Task An_address_that_names_no_finding_card_costs_nothing(string address)
    {
        await using StudioComponentContext context = await ReadyAsync(address, K01);

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll("#finding-k01").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        Reveals(context).Should().BeEmpty();
        page.FindAll(".ms-health-finding-gone").Should().BeEmpty();
    }

    /// <summary>
    /// A closed circuit, or a browser that refused: the script is a courtesy, the page is right without it, and the
    /// visitor is not told that a finding that is on screen is gone.
    /// </summary>
    [Fact]
    public async Task A_circuit_that_closed_under_the_call_says_nothing_and_breaks_nothing()
    {
        await using StudioComponentContext context = await ReadyAsync("marten/health#finding-d01", D01);
        context.JSInterop.Setup<bool>(Reveal, "finding-d01").SetException(new JSDisconnectedException("the circuit is gone"));

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => Reveals(context).Should().ContainSingle(), TimeSpan.FromSeconds(5));
        page.WaitForAssertion(() => page.FindAll("#finding-d01").Should().ContainSingle());
        page.FindAll(".ms-health-finding-gone").Should().BeEmpty();
        page.FindAll(".ms-error-alert").Should().BeEmpty();
    }

    /// <summary>
    /// A muted finding's card moves into the closed list (the script opens it); muting the finding that was linked is not a
    /// new address, so the page does not reveal it a second time.
    /// </summary>
    [Fact]
    public async Task Muting_the_linked_finding_does_not_reveal_it_again()
    {
        await using StudioComponentContext context = await ReadyAsync("marten/health#finding-k01", K01);
        context.JSInterop.Setup<bool>(Reveal, "finding-k01").SetResult(true);

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => Reveals(context).Should().ContainSingle(), TimeSpan.FromSeconds(5));

        page.Find(".ms-health-mute").Click();
        page.WaitForAssertion(() => page.FindAll(".ms-health-muted #finding-k01").Should().ContainSingle());

        Reveals(context).Should().ContainSingle();
    }
}
