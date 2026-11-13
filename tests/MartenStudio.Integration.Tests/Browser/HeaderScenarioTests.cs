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

    /// <summary>
    /// The header with the tenant picker the Marten sample draws (<c>?tenant=acme</c>) at 768 and 390 px: no part overlaps another or is
    /// cut off, and the Find box opens, takes focus and fits (1.2-UX-FIX-FU). The sample has one store with one database, so the store and
    /// database pickers are absent; no host here registers more.
    /// </summary>
    [BrowserFact]
    public Task The_header_with_the_tenant_picker_has_no_overlap_and_nothing_cut_off_at_768_px() => HeaderLaidOutAsync(768);

    /// <inheritdoc cref="The_header_with_the_tenant_picker_has_no_overlap_and_nothing_cut_off_at_768_px" />
    [BrowserFact]
    public Task The_header_with_the_tenant_picker_has_no_overlap_and_nothing_cut_off_at_390_px() => HeaderLaidOutAsync(390);

    private Task HeaderLaidOutAsync(int width) =>
        RunAsync("marten-header-tenant-" + width, async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(width, 800);

            await studio.GoAsync(fixture.Root.StudioUrl() + "?tenant=acme");
            await page.Locator(".ms-header-left > .ms-env-badge").WaitForAsync();
            await page.Locator("#ms-tenant-select").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
            (await page.EvaluateAsync<string>("() => document.querySelector('#ms-tenant-select').value")).Should().Be("acme");

            string[] problems = await page.EvaluateAsync<string[]>(
                """
                () => {
                  const width = document.documentElement.clientWidth;
                  const parts = [...document.querySelectorAll('.ms-header select, .ms-header button, .ms-header a, .ms-header input, .ms-header .ms-env-badge')]
                    .filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(e).position !== 'absolute'; })
                    .map(e => ({ e, r: e.getBoundingClientRect() }));
                  const problems = [];
                  for (const p of parts) {
                    if (p.r.right > width + 0.5 || p.r.left < -0.5) problems.push('outside the screen: ' + (p.e.className || p.e.tagName) + ' ' + Math.round(p.r.left) + '-' + Math.round(p.r.right));
                  }
                  for (let i = 0; i < parts.length; i++) {
                    for (let j = i + 1; j < parts.length; j++) {
                      const a = parts[i], b = parts[j];
                      if (a.e.contains(b.e) || b.e.contains(a.e)) continue;
                      const w = Math.min(a.r.right, b.r.right) - Math.max(a.r.left, b.r.left);
                      const h = Math.min(a.r.bottom, b.r.bottom) - Math.max(a.r.top, b.r.top);
                      if (w > 1 && h > 1) problems.push('overlap: ' + (a.e.className || a.e.tagName) + ' and ' + (b.e.className || b.e.tagName));
                    }
                  }
                  const doc = document.documentElement;
                  if (doc.scrollWidth > doc.clientWidth) problems.push('the page scrolls sideways');
                  return problems;
                }
                """);

            problems.Should().BeEmpty("the header with its tenant picker is laid out at " + width + " px");

            ILocator toggle = page.Locator(".ms-find-toggle");
            if (await toggle.IsVisibleAsync())
            {
                await toggle.ClickAsync();
            }

            ILocator find = page.Locator(".ms-find input").First;
            await find.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
            await find.FocusAsync();

            LocatorBoundingBoxResult box = (await find.BoundingBoxAsync())!;
            box.Width.Should().BeGreaterThanOrEqualTo(120, "the Find box is wide enough to type an id into");
            ((double) (box.X + box.Width)).Should().BeLessThanOrEqualTo(width + 0.5);

            studio.AssertClean("showing the header with the tenant picker at " + width + " px");
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

            // Anything the document fetched while it started is discounted, so only what the click itself does is judged.
            int enhancedBefore = studio.EnhancedNavigations.Count;

            await page.EvaluateAsync("() => { window.__msBeforeBack = 1; }");
            await link.ClickAsync();

            await page.WaitForFunctionAsync("() => location.pathname === '/'", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
            await page.Locator("p", new PageLocatorOptions { HasTextString = "Signed in as" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            (await page.EvaluateAsync<bool>("() => window.__msBeforeBack === undefined")).Should().BeTrue("the host's page was loaded afresh");
            studio.EnhancedNavigations.Skip(enhancedBefore).Should().BeEmpty("leaving the studio is not an enhanced navigation");
            page.Url.Should().Be(fixture.Root.Url("/"));
        });
}
