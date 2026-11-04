using Microsoft.Playwright;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-A11Y in Postgres Studio's own shell: <c>?</c> opens the keyboard shortcut sheet as a native modal, Esc closes it and focus
/// goes back to the control that was focused, <c>?</c> does nothing while a character is being typed - the SQL console's editor
/// is a text area - and the list holds no key only a Marten page answers to.
/// </summary>
[Collection(BrowserSuite.Name)]
public class ShortcutSheetBrowserTests(BrowserSuiteFixture fixture)
{
    private static readonly PageWaitForFunctionOptions Wait = new() { Timeout = 30_000 };

    [BrowserFact]
    public Task The_question_mark_opens_the_sheet_as_a_modal_and_Esc_returns_focus() =>
        BrowserScenario.RunAsync(fixture.Browser, "pg-shortcut-sheet", fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;
            await studio.GoAsync(fixture.Host.StudioUrl());

            await page.Locator("a.ms-nav-link").First.FocusAsync();
            string expected = await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')") ?? string.Empty;

            await page.Keyboard.PressAsync("Shift+Slash");

            await page.WaitForFunctionAsync("() => { const d = document.getElementById('ms-shortcuts'); return d !== null && d.open && d.matches(':modal'); }", null, Wait);
            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.closest('#ms-shortcuts') !== null && document.activeElement.textContent.trim() === 'Close'", null, Wait);

            string text = await page.Locator("#ms-shortcuts").TextContentAsync() ?? string.Empty;
            text.Should().Contain("Keyboard shortcuts").And.Contain("JSON viewer").And.Contain("SQL editor");
            text.Should().NotContain("Document editor", "a Postgres Studio host has no document pages");

            // Tab stays in the sheet, the page behind it is unreachable.
            for (int step = 0; step < 4; step++)
            {
                await page.Keyboard.PressAsync("Tab");
                (await page.EvaluateAsync<bool>("() => document.activeElement.closest('#ms-shortcuts') !== null")).Should().BeTrue();
            }

            await page.Keyboard.PressAsync("Escape");

            await page.WaitForFunctionAsync("() => !document.getElementById('ms-shortcuts').open", null, Wait);
            (await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')")).Should().Be(expected);

            studio.AssertClean("opening and closing the shortcut sheet");
        });

    /// <summary>1.2-A11Y-FU: the sheet's panel sits in the middle of the viewport, not in its top-left corner.</summary>
    [BrowserFact]
    public Task The_sheet_is_centred_in_a_desktop_viewport() => CentredAsync(1280, 800);

    /// <summary>1.2-A11Y-FU: and on a phone.</summary>
    [BrowserFact]
    public Task The_sheet_is_centred_at_390_px() => CentredAsync(390, 844);

    private Task CentredAsync(int width, int height) =>
        BrowserScenario.RunAsync(fixture.Browser, "pg-shortcut-sheet-centred-" + width, fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(width, height);
            await studio.GoAsync(fixture.Host.StudioUrl());

            await page.Keyboard.PressAsync("Shift+Slash");
            await page.WaitForFunctionAsync("() => { const d = document.getElementById('ms-shortcuts'); return d !== null && d.open && d.matches(':modal'); }", null, Wait);

            // The panel slides in (ms-slide-up); its box is only the final one once the animations have finished.
            await page.WaitForFunctionAsync("() => document.getAnimations().every(a => a.playState !== 'running')", null, Wait);

            LocatorBoundingBoxResult? box = await page.Locator("#ms-shortcuts .ms-shortcuts").BoundingBoxAsync();
            box.Should().NotBeNull();

            float centreX = box!.X + (box.Width / 2f);
            float centreY = box.Y + (box.Height / 2f);
            centreX.Should().BeApproximately(width / 2f, 4f, "the panel is centred across the " + width + " px viewport");
            centreY.Should().BeApproximately(height / 2f, 4f, "and down the " + height + " px one");
            box.X.Should().BeGreaterThanOrEqualTo(0f);
            (box.X + box.Width).Should().BeLessThanOrEqualTo(width + 0.5f, "it does not scroll the page sideways");

            studio.AssertClean("showing the shortcut sheet at " + width + " px");
        });

    [BrowserFact]
    public Task The_question_mark_typed_into_the_SQL_editor_is_a_character_and_opens_nothing() =>
        BrowserScenario.RunAsync(fixture.Browser, "pg-shortcut-sheet-in-editor", fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;
            await studio.GoAsync(fixture.Host.StudioUrl("query"));

            ILocator editor = page.Locator("#ms-query-sql");
            await editor.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await editor.FillAsync(string.Empty);
            await editor.FocusAsync();
            await page.Keyboard.TypeAsync("select '?'");

            (await editor.InputValueAsync()).Should().Be("select '?'");
            (await page.EvaluateAsync<bool>("() => document.getElementById('ms-shortcuts').open")).Should().BeFalse();

            studio.AssertClean("typing a question mark into the SQL editor");
        });
}
