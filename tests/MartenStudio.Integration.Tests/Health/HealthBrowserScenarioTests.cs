using MartenStudio.Integration.Tests.Browser;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The Health page on real Kestrel, in Chromium, over the sample host: it renders through the real services
/// once the circuit is attached, and "mute in this browser" survives a reload.
/// </summary>
/// <remarks>
/// The sample host runs in Development with the sample domain's store as it ships, so K02 - extended
/// progression tracking off, an Info - is the finding it is guaranteed to show, and K01 is quiet. Like every
/// browser scenario, it skips locally when Chromium is not installed and throws on CI (D19).
/// </remarks>
[Collection(BrowserSuite.Name)]
public class HealthBrowserScenarioTests(BrowserSuiteFixture fixture)
{
    [BrowserFact]
    public async Task The_health_page_renders_the_sample_stores_findings_and_a_mute_survives_a_reload()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "health", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("health"));

            ILocator k02 = studio.Page.Locator(".ms-health-area #finding-k02");
            await k02.WaitForAsync();

            (await k02.Locator(".ms-health-severity").InnerTextAsync()).Trim().Should().Be("Info");
            (await k02.Locator(".ms-health-remedy-code").InnerTextAsync()).Should().Contain("EnableExtendedProgressionTracking = true");
            (await studio.Page.Locator(".ms-health-check-table tbody tr").CountAsync()).Should().BeGreaterThanOrEqualTo(3);
            (await studio.Page.Locator("#finding-k01").CountAsync()).Should().Be(0, "the sample host runs in Development");

            await k02.Locator(".ms-health-mute").ClickAsync();
            await studio.Page.Locator(".ms-health-muted summary").WaitForAsync();

            // The mute lives in this browser's localStorage, so a fresh page load - a new circuit - still has it.
            await studio.GoAsync(fixture.Root.StudioUrl("health"));
            await studio.Page.Locator(".ms-health-muted summary").WaitForAsync();
            (await studio.Page.Locator(".ms-health-area #finding-k02").CountAsync()).Should().Be(0);

            // And it comes back with one click, for the next scenario's sake as much as this one's.
            await studio.Page.Locator(".ms-health-muted summary").ClickAsync();
            await studio.Page.Locator(".ms-health-muted .ms-health-mute").First.ClickAsync();
            await studio.Page.Locator(".ms-health-area #finding-k02").WaitForAsync();

            studio.AssertClean("opening the Health page, muting a finding and unmuting it");
        });
    }
}
