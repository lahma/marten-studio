using MartenStudio.Integration.Tests.Browser;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The Health page on real Kestrel, in Chromium, over the sample host: it renders through the real services
/// once the circuit is attached, and "mute in this browser" survives a reload.
/// </summary>
/// <remarks>
/// The sample host runs in Development with the sample domain's store as it ships, which starts healthy - no K01, and
/// no K02, since the sample turns extended progression tracking on - against the suite's Testcontainers Postgres, which
/// runs with <c>synchronous_commit = off</c>. So S02's Info about that setting is the finding it is guaranteed to show
/// (docs/health/server.md says so). Like every browser scenario, it skips locally when Chromium is not installed and
/// throws on CI (D19).
/// </remarks>
[Collection(BrowserSuite.Name)]
public class HealthBrowserScenarioTests(BrowserSuiteFixture fixture)
{
    private const string SynchronousCommit = ".ms-health-area #finding-s02-synchronous_commit";

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
}
