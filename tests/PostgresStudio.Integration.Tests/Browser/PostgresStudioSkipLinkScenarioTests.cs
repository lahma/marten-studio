using Microsoft.Playwright;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// The skip link (1.1-UX-FIX-B) in Postgres Studio's own shell: the first Tab stop of a page, drawn only while it has focus,
/// and it moves focus to the content region without navigating (the document's <c>&lt;base href&gt;</c> would otherwise send a
/// bare fragment to the studio's front page).
/// </summary>
[Collection(BrowserSuite.Name)]
public class PostgresStudioSkipLinkScenarioTests(BrowserSuiteFixture fixture)
{
    /// <summary>
    /// Focus is put back at the start of the document the way a fresh load has it: <c>FocusOnNavigate</c> has put it on the page
    /// heading, and Chromium's sequential focus starting point would otherwise be there.
    /// </summary>
    private const string ResetFocus =
        "() => { document.activeElement && document.activeElement.blur(); document.body.setAttribute('tabindex', '-1'); "
        + "document.body.focus(); document.body.removeAttribute('tabindex'); }";

    [BrowserFact]
    public Task The_skip_link_is_the_first_Tab_stop_and_moves_focus_to_the_content_without_navigating() =>
        BrowserScenario.RunAsync(fixture.Browser, "skip-link", fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());
            await studio.WaitForCircuitAsync();
            string before = page.Url;

            ILocator link = page.Locator("a.ms-skip-link");
            (await link.BoundingBoxAsync())!.Y.Should().BeLessThan(0, "it is off screen until it has focus");

            await page.EvaluateAsync(ResetFocus);
            await page.Keyboard.PressAsync("Tab");

            (await page.EvaluateAsync<string>("() => document.activeElement.className")).Should().Contain("ms-skip-link");
            (await link.BoundingBoxAsync())!.Y.Should().BeGreaterThanOrEqualTo(0, "it is drawn once it has focus");

            await page.Keyboard.PressAsync("Enter");

            (await page.EvaluateAsync<string>("() => document.activeElement.id")).Should().Be("ms-main-content");
            page.Url.Should().Be(before);

            studio.AssertClean("tabbing to the skip link and following it");
        });
}
