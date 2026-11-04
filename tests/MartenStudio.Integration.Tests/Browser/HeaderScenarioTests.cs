using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-HEADER in Marten Studio in a real browser: the shell is PostgresStudio's, so the longer set is in its suite; this proves the same
/// header under Marten Studio's own mount (<c>/marten</c>), the sample's <c>Development</c> badge, the back link out of the mount and
/// the copied address.
/// </summary>
[Collection(BrowserSuite.Name)]
public class HeaderScenarioTests(BrowserSuiteFixture fixture)
{
    private Task RunAsync(string name, Func<StudioPage, Task> body) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Root, "admin", body);

    [BrowserFact]
    public Task The_header_shows_the_environment_and_copies_the_address_with_its_scope() =>
        RunAsync("marten-header-copy-link", async studio =>
        {
            IPage page = studio.Page;
            await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new BrowserContextGrantPermissionsOptions { Origin = fixture.Root.BaseAddress.GetLeftPart(UriPartial.Authority) });

            await studio.GoAsync(fixture.Root.StudioUrl());

            ILocator badge = page.Locator(".ms-header-left > .ms-env-badge");
            await badge.WaitForAsync();
            (await badge.InnerTextAsync()).Trim().Should().Be("Development");
            (await badge.GetAttributeAsync("class")).Should().Contain("ms-env-badge-development");

            await page.Locator(".ms-copy-link-button").ClickAsync();
            await page.Locator(".ms-copy-link-status", new PageLocatorOptions { HasTextString = "Link copied" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            string clipboard = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
            clipboard.Should().Be(page.Url);
            clipboard.Should().Contain("/marten").And.Contain("store=");

            studio.AssertClean("copying the page's link");
        });

    [BrowserFact]
    public Task Back_to_application_leaves_the_mount_as_a_normal_navigation() =>
        RunAsync("marten-header-back-to-application", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl());

            ILocator link = page.Locator(".ms-header-right a.ms-back-link");
            await link.WaitForAsync();
            (await link.GetAttributeAsync("href")).Should().Be("/", "the option as written, not re-rooted under /marten");

            await page.EvaluateAsync("() => { window.__msBeforeBack = 1; }");
            await link.ClickAsync();

            await page.WaitForFunctionAsync("() => location.pathname === '/'", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
            await page.Locator("p", new PageLocatorOptions { HasTextString = "Signed in as" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            (await page.EvaluateAsync<bool>("() => window.__msBeforeBack === undefined")).Should().BeTrue("the host's page was loaded afresh");
            studio.EnhancedNavigations.Should().BeEmpty();
            page.Url.Should().Be(fixture.Root.Url("/"));
        });
}
