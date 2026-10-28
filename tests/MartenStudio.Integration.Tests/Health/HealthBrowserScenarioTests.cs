using MartenStudio.Integration.Tests.Browser;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The Health page on real Kestrel, in Chromium, over the sample host: it renders through the real services
/// once the circuit is attached - its findings in area groups, and the "What was checked" table - and "mute in this
/// browser" survives a reload.
/// </summary>
/// <remarks>
/// <para>
/// The sample host runs in Development with the sample domain's store as it ships, which starts healthy - no K01, and
/// no K02, since the sample turns extended progression tracking on - against the suite's Testcontainers Postgres, which
/// runs with <c>synchronous_commit = off</c>. So S02's Info about that setting is the finding it is guaranteed to show
/// (docs/health/server.md says so). Like every browser scenario, it skips locally when Chromium is not installed and
/// throws on CI (D19).
/// </para>
/// <para>
/// What the demo switches make the page show - a finding a switch raises, the badge, the toast, Database › Sessions, the
/// Projections screen's banners and what a viewer is not shown - is <see cref="HealthAdvisorScenarioTests" />'.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class HealthBrowserScenarioTests(BrowserSuiteFixture fixture)
{
    private const string SynchronousCommit = ".ms-health-area #finding-s02-setting-synchronous_commit";

    /// <summary>
    /// The muted list's own summary. A child combinator, because a muted finding that ran SQL - S02 does, where K02 read
    /// only the configuration - carries "Show SQL" disclosures of its own inside the list.
    /// </summary>
    private const string MutedSummary = ".ms-health-muted > summary";

    [BrowserFact]
    public async Task The_health_page_renders_the_sample_stores_findings_and_a_mute_survives_a_reload()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "health", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("health"));

            ILocator s02 = studio.Page.Locator(SynchronousCommit);
            await s02.WaitForAsync();

            (await s02.Locator(".ms-health-severity").InnerTextAsync()).Trim().Should().Be("Info");
            (await s02.Locator(".ms-health-remedy-code").InnerTextAsync()).Should().Contain("synchronous_commit = on");
            (await studio.Page.Locator(".ms-health-check-table tbody tr").CountAsync()).Should().BeGreaterThanOrEqualTo(3);

            // The findings are drawn in area groups, each a labelled section with its own heading, and the status table
            // is under its own: what was checked, whether or not it found anything.
            ILocator areas = studio.Page.Locator("section.ms-health-area");
            (await areas.CountAsync()).Should().BePositive();
            foreach (ILocator area in await areas.AllAsync())
            {
                // The text, not the rendered text: a section title is drawn in capitals by the stylesheet.
                string? label = await area.GetAttributeAsync("aria-label");
                label.Should().NotBeNullOrWhiteSpace();
                (await area.Locator("h2.ms-section-title").TextContentAsync())!.Trim().Should().Be(label);
            }

            (await areas.Filter(new LocatorFilterOptions { Has = studio.Page.Locator("#finding-s02-setting-synchronous_commit") }).GetAttributeAsync("aria-label"))
                .Should().Be("Server", "S02 is a Server finding");
            (await studio.Page.Locator(".ms-health-checks h2.ms-section-title").TextContentAsync())!.Trim().Should().Be("What was checked");
            (await studio.Page.Locator("#finding-k01").CountAsync()).Should().Be(0, "the sample host runs in Development");
            (await studio.Page.Locator("#finding-k02").CountAsync()).Should().Be(0, "the sample turns extended progression tracking on");

            await s02.Locator(".ms-health-mute").ClickAsync();
            await studio.Page.Locator(MutedSummary).WaitForAsync();

            // The mute lives in this browser's localStorage, so a fresh page load - a new circuit - still has it.
            await studio.GoAsync(fixture.Root.StudioUrl("health"));
            await studio.Page.Locator(MutedSummary).WaitForAsync();
            (await studio.Page.Locator(SynchronousCommit).CountAsync()).Should().Be(0);

            // And it comes back with one click, for the next scenario's sake as much as this one's.
            await studio.Page.Locator(MutedSummary).ClickAsync();
            await studio.Page.Locator(".ms-health-muted .ms-health-mute").First.ClickAsync();
            await studio.Page.Locator(SynchronousCommit).WaitForAsync();

            studio.AssertClean("opening the Health page, muting a finding and unmuting it");
        });
    }

    /// <summary>
    /// Issue #33: a finding's address ends in its card's fragment, and the browser resolves that against the prerendered
    /// document, before the circuit has drawn the cards, and leaves the page at the top. The page scrolls to the card itself
    /// once the report is on screen, marks it for two seconds and takes the mark off.
    /// </summary>
    /// <remarks>
    /// The window is short, so the card is below the fold without any scrolling and "it is in view" says something; the
    /// scenario asserts as much from the card's own layout position. The mark is recorded by an observer installed before
    /// the page's scripts run, because it is on the card for two seconds and a poll could come late on a slow runner.
    /// A fresh page, so the fragment is the address the document arrives at, as a host's link in a new tab is. Like every
    /// browser scenario it skips locally without Chromium and throws on CI (D19).
    /// </remarks>
    [BrowserFact]
    public async Task A_finding_address_scrolls_to_its_card_and_marks_it_briefly_and_the_studio_asks_the_host_for_no_icon()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "health-finding-link", fixture.Root, "admin", async studio =>
        {
            const string anchor = "finding-s02-setting-synchronous_commit";

            await studio.Page.SetViewportSizeAsync(StudioPage.ViewportWidth, 360);
            await studio.Page.AddInitScriptAsync(
                """
                window.__msLinked = { added: false, removed: false };
                new MutationObserver(function () {
                    const card = document.getElementById('finding-s02-setting-synchronous_commit');
                    if (!card) { return; }
                    const marked = card.classList.contains('ms-health-finding-linked');
                    if (marked) { window.__msLinked.added = true; }
                    else if (window.__msLinked.added) { window.__msLinked.removed = true; }
                }).observe(document, { subtree: true, attributes: true, attributeFilter: ['class'] });
                """);

            await studio.GoAsync(fixture.Root.StudioUrl("health") + "#" + anchor);

            ILocator card = studio.Page.Locator("#" + anchor);
            await card.WaitForAsync();
            await studio.Page.WaitForFunctionAsync("() => window.__msLinked.added", null, new PageWaitForFunctionOptions { Timeout = 30_000 });

            // Where the card sits in the page, whatever the scroll: it is further down than the window is tall.
            double natural = await card.EvaluateAsync<double>(
                "e => { let y = 0; for (let n = e; n; n = n.offsetParent) { y += n.offsetTop; } return y; }");
            natural.Should().BeGreaterThan(360, "otherwise the card was in view without a scroll, and the scenario proves nothing");

            LocatorBoundingBoxResult? box = await card.BoundingBoxAsync();
            box.Should().NotBeNull();
            box!.Y.Should().BeInRange(0, 360 - 1, "the card's top edge is in the window: the page scrolled to it");

            // Marked, then not: the class is the brief mark, and it is gone about two seconds later.
            await studio.Page.WaitForFunctionAsync("() => window.__msLinked.removed", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
            (await card.GetAttributeAsync("class")).Should().NotContain("ms-health-finding-linked");

            // A host that serves no /favicon.ico is asked for nothing: the document names its own icon, so AssertClean
            // (no response of 400 or more) holds without the sample's workaround for it.
            studio.AssertClean("opening a finding's address");
        });
    }
}
