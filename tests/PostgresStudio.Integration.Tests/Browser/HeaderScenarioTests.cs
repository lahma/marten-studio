using Microsoft.Playwright;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-HEADER in a real browser: the environment badge, the "Back to application" link and the copy-link button.
/// </summary>
/// <remarks>
/// The sample runs as <c>Development</c> and sets <c>ApplicationPath = "/"</c>, so the header carries the success-coloured badge and a
/// link to the sample's own landing page, which is outside the mount: the one navigation here that is not the circuit's.
/// </remarks>
[Collection(BrowserSuite.Name)]
public class HeaderScenarioTests(BrowserSuiteFixture fixture)
{
    private static readonly string[] HeaderControls = [".ms-env-badge", ".ms-copy-link-button", ".ms-back-link", ".ms-preferences-button"];

    private Task RunAsync(string name, Func<StudioPage, Task> body, int viewportWidth = StudioPage.ViewportWidth) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Host, "admin", body, viewportWidth);

    /// <summary>
    /// The header at 768 and 390 px with everything the sample draws in it: no part overlaps another or is cut off, and the Find box
    /// opens, takes focus and fits (1.2-UX-FIX). <b>No host here can draw the sweep's three pickers</b> (store, database and tenant):
    /// the sample's header drew fewer than two pickers when measured, and the Marten sample registers one store with one database, so
    /// this checks the header around them. A host that registers a second store with a multi-tenant database would extend it; none
    /// was built for it.
    /// </summary>
    [BrowserFact]
    public Task The_header_has_no_overlap_and_nothing_cut_off_at_768_px() => HeaderLaidOutAsync(768);

    /// <inheritdoc cref="The_header_has_no_overlap_and_nothing_cut_off_at_768_px" />
    [BrowserFact]
    public Task The_header_has_no_overlap_and_nothing_cut_off_at_390_px() => HeaderLaidOutAsync(390);

    private Task HeaderLaidOutAsync(int width) =>
        RunAsync(
            "header-layout-" + width,
            async studio =>
            {
                IPage page = studio.Page;

                await studio.GoAsync(fixture.Host.StudioUrl());
                await page.Locator(".ms-header-left > .ms-env-badge").WaitForAsync();

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

                problems.Should().BeEmpty("the header with its pickers is laid out at " + width + " px");

                // The Find box is usable: on a phone or a narrow window it is the magnifier's popover; it opens, takes the typing and fits the screen.
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

                studio.AssertClean("showing the header with its pickers at " + width + " px");
            },
            viewportWidth: width);

    [BrowserFact]
    public Task Copy_link_writes_the_address_of_the_page_to_the_clipboard_and_says_so() =>
        RunAsync("header-copy-link", async studio =>
        {
            IPage page = studio.Page;
            await page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new BrowserContextGrantPermissionsOptions { Origin = fixture.Host.BaseAddress.GetLeftPart(UriPartial.Authority) });

            await studio.GoAsync(fixture.Host.StudioUrl("database"));

            await page.Locator(".ms-copy-link-button").ClickAsync();
            await page.Locator(".ms-copy-link-status", new PageLocatorOptions { HasTextString = "Link copied" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            string clipboard = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
            clipboard.Should().Be(page.Url, "the button copies the address as the bar shows it");
            clipboard.Should().Contain("store=").And.Contain("db=", "and the address carries the scope (D9)");
            (await page.Locator(".ms-copy-link-fallback").CountAsync()).Should().Be(0);

            studio.AssertClean("copying the page's link");
        });

    [BrowserFact]
    public Task When_the_clipboard_is_refused_the_address_is_offered_in_a_selected_text_box() =>
        RunAsync("header-copy-link-fallback", async studio =>
        {
            IPage page = studio.Page;

            // No secure context, in effect: neither the async clipboard nor the legacy command takes anything.
            await page.AddInitScriptAsync(
                "(() => { Object.defineProperty(navigator, 'clipboard', { value: { writeText: () => Promise.reject(new Error('refused')) }, configurable: true });"
                + " document.execCommand = () => false; })();");

            await studio.GoAsync(fixture.Host.StudioUrl("database"));

            await page.Locator(".ms-copy-link-button").ClickAsync();

            ILocator box = page.Locator(".ms-copy-link-fallback input.ms-copy-link-text");
            await box.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await box.InputValueAsync()).Should().Be(page.Url);

            // Focused and selected, so the next key is Ctrl+C.
            await page.WaitForFunctionAsync(
                "() => { const e = document.activeElement; return e && e.id === 'ms-copy-link-text' && e.selectionStart === 0 && e.selectionEnd === e.value.length && e.value.length > 0; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            (await page.Locator(".ms-copy-link-status").InnerTextAsync()).Trim().Should().BeEmpty("nothing was copied, so nothing is claimed");

            await page.Locator(".ms-copy-link-fallback button", new PageLocatorOptions { HasTextString = "Close" }).ClickAsync();
            await box.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 30_000 });

            studio.AssertClean("falling back to the text box");
        });

    [BrowserFact]
    public Task The_badge_names_the_environment_in_the_success_colour_for_Development() =>
        RunAsync("header-env-badge", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());

            ILocator badge = page.Locator(".ms-header-left > .ms-env-badge");
            await badge.WaitForAsync();
            (await badge.InnerTextAsync()).Trim().Should().Be("Development");

            string[] colours = await page.EvaluateAsync<string[]>(
                """
                () => {
                  const studio = document.querySelector('.ms-studio');
                  const probe = document.createElement('span');
                  probe.style.color = 'var(--ms-color-success)';
                  studio.appendChild(probe);
                  const success = getComputedStyle(probe).color;
                  probe.remove();
                  const badge = document.querySelector('.ms-env-badge');
                  return [getComputedStyle(badge).borderTopColor, success, getComputedStyle(badge, '::before').backgroundColor];
                }
                """);

            colours[0].Should().Be(colours[1], "Development is drawn in the success token's colour");
            colours[2].Should().Be(colours[1], "and so is its dot");

            studio.AssertClean("showing the environment badge");
        });

    [BrowserFact]
    public Task The_header_with_all_three_additions_fits_a_phone_without_a_horizontal_scroll() =>
        RunAsync(
            "header-phone",
            async studio =>
            {
                IPage page = studio.Page;

                await studio.GoAsync(fixture.Host.StudioUrl());
                await page.Locator(".ms-header-left > .ms-env-badge").WaitForAsync();

                string layout = await page.EvaluateAsync<string>(
                    """
                    () => JSON.stringify({
                      width: document.documentElement.clientWidth,
                      header: Math.round(document.querySelector('.ms-header').getBoundingClientRect().height),
                      parts: [...document.querySelectorAll('.ms-header-left > *, .ms-header-right > *, .ms-scope-selector > *')].map(e => {
                        const r = e.getBoundingClientRect();
                        return e.className.toString().split(' ')[0] + ':' + Math.round(r.left) + '-' + Math.round(r.right) + ' y' + Math.round(r.top) + '+' + Math.round(r.height);
                      }),
                    })
                    """);

                bool[] fits = await page.EvaluateAsync<bool[]>(
                    """
                    () => {
                      const width = document.documentElement.clientWidth;
                      const outside = [...document.querySelectorAll('.ms-header *')].filter(e => {
                        const r = e.getBoundingClientRect();
                        return r.width > 0 && r.height > 0 && getComputedStyle(e).position !== 'absolute' && (r.right > width + 0.5 || r.left < -0.5);
                      });
                      const doc = document.documentElement;
                      return [doc.scrollWidth <= doc.clientWidth, document.querySelector('.ms-main').scrollWidth <= document.querySelector('.ms-main').clientWidth, outside.length === 0];
                    }
                    """);

                fits[0].Should().BeTrue("the page does not scroll sideways at 390 px: " + layout);
                fits[1].Should().BeTrue("and neither does the content column, which hides its overflow: " + layout);
                fits[2].Should().BeTrue("and no part of the header is clipped by it: " + layout);

                foreach (string selector in HeaderControls)
                {
                    (await page.Locator(selector).First.IsVisibleAsync()).Should().BeTrue(selector + " is shown at 390 px");
                }

                studio.AssertClean("showing the header at 390 px");
            },
            viewportWidth: 390);

    [BrowserFact]
    public Task Back_to_application_loads_the_hosts_own_page_as_a_normal_navigation() =>
        RunAsync("header-back-to-application", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());

            ILocator link = page.Locator(".ms-header-right a.ms-back-link");
            await link.WaitForAsync();
            (await link.GetAttributeAsync("href")).Should().Be("/", "the option as written, not re-rooted under /postgres");

            // A document that survives a circuit navigation keeps this; a full load does not.
            await page.EvaluateAsync("() => { window.__msBeforeBack = 1; }");

            await link.ClickAsync();

            await page.WaitForFunctionAsync("() => location.pathname === '/'", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
            await page.Locator("p", new PageLocatorOptions { HasTextString = "Signed in as" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            (await page.EvaluateAsync<bool>("() => window.__msBeforeBack === undefined")).Should().BeTrue("the host's page was loaded afresh, not rendered by the studio's circuit");
            studio.EnhancedNavigations.Should().BeEmpty("leaving the studio is not an enhanced navigation");
            page.Url.Should().Be(fixture.Host.Url("/"));
        });
}
