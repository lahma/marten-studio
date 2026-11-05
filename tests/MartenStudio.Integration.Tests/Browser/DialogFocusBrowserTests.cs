using Microsoft.Playwright;

using MartenStudio.SampleDomain;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-A11Y, in a browser against the sample host: a studio dialog is a native modal - it takes focus, keeps Tab inside, makes the
/// page behind it unreachable, runs Cancel on Esc and gives focus back to the control that opened it - and <c>?</c> opens the
/// keyboard shortcut sheet everywhere except where a character is being typed.
/// </summary>
[Collection(BrowserSuite.Name)]
public class DialogFocusBrowserTests(BrowserSuiteFixture fixture)
{
    private const string Dialog = "dialog.ms-confirm-overlay";
    private const string InDialog = "() => document.activeElement !== null && document.activeElement.closest('dialog.ms-confirm-overlay') !== null";

    private static readonly PageWaitForFunctionOptions Wait = new() { Timeout = 30_000 };

    /// <summary>The typed order whose stream folds, so the sample's Compact dialog opens with its phrase to type.</summary>
    private static string TypedStream(BrowserSuiteFixture fixture) =>
        fixture.Root.StudioUrl("events/streams/s?id=" + SampleDataSeeder.TypedOrderStreamId(1));

    [BrowserFact]
    public async Task The_Compact_dialog_takes_focus_keeps_Tab_inside_and_gives_focus_back_on_Esc()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "dialog-compact-focus", fixture.Root, "admin", async studio =>
        {
            IPage page = studio.Page;
            await studio.GoAsync(TypedStream(fixture));

            ILocator invoker = page.Locator(".ms-stream-compact-button");
            await invoker.ClickAsync();

            // The server rendered it open; the script upgraded it. ':modal' is the browser's word for showModal() having run.
            await page.WaitForFunctionAsync("() => document.querySelector('dialog.ms-confirm-overlay') !== null && document.querySelector('dialog.ms-confirm-overlay').matches(':modal')", null, Wait);
            (await page.Locator(Dialog).GetAttributeAsync("role")).Should().Be("alertdialog");

            // The Compact dialog asks for the stream id, so focus starts on the box that takes it.
            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.id === 'ms-confirm-input'", null, Wait);

            // Tab stays inside, forwards past the last control and backwards past the first, and every stop is inside.
            for (int step = 0; step < 6; step++)
            {
                await page.Keyboard.PressAsync("Tab");
                (await page.EvaluateAsync<bool>(InDialog)).Should().BeTrue("Tab number " + (step + 1) + " stays in the dialog");
            }

            for (int step = 0; step < 6; step++)
            {
                await page.Keyboard.PressAsync("Shift+Tab");
                (await page.EvaluateAsync<bool>(InDialog)).Should().BeTrue("Shift+Tab number " + (step + 1) + " stays in the dialog");
            }

            // The page behind is inert: focusing its button does nothing.
            await page.EvaluateAsync("() => document.querySelector('.ms-stream-compact-button').focus()");
            (await page.EvaluateAsync<bool>(InDialog)).Should().BeTrue("the page behind a modal cannot take focus");

            await page.Keyboard.PressAsync("Escape");

            await page.WaitForFunctionAsync("() => document.querySelector('dialog.ms-confirm-overlay') === null", null, Wait);
            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.classList.contains('ms-stream-compact-button')", null, Wait);

            // And again, closed by the Cancel button this time.
            await invoker.ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('dialog.ms-confirm-overlay') !== null && document.querySelector('dialog.ms-confirm-overlay').matches(':modal')", null, Wait);
            await page.Locator(".ms-confirm-actions button", new PageLocatorOptions { HasTextString = "Cancel" }).ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('dialog.ms-confirm-overlay') === null", null, Wait);
            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.classList.contains('ms-stream-compact-button')", null, Wait);

            studio.AssertClean("opening, tabbing in and closing the Compact dialog");
        });
    }

    /// <summary>
    /// 1.2-FINAL-FIX: after a confirmed Rebuild the Rebuild button that opened the dialog is disabled (and its row is re-rendered), and a
    /// browser moves focus to the body when the element that has it goes. Focus goes to the same row's next control, or the row, or the
    /// page's heading, and at no moment in the seconds after is it left on the body.
    /// </summary>
    [BrowserFact]
    public async Task Focus_after_a_confirmed_Rebuild_whose_button_is_gone_is_never_left_on_the_body()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "dialog-rebuild-focus", fixture.Root, "admin", async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(1280, 900);
            await studio.GoAsync(fixture.Root.StudioUrl("projections"));

            ILocator rebuild = page.Locator(".ms-projection-rebuild:not([disabled])").First;
            await rebuild.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            string name = await rebuild.EvaluateAsync<string>("b => b.closest('tr').dataset.projection");
            await rebuild.ClickAsync();

            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.id === 'ms-confirm-input'", null, Wait);
            await page.Keyboard.TypeAsync(name);
            await page.Keyboard.PressAsync("Tab");
            await page.Keyboard.PressAsync("Enter");

            await page.WaitForFunctionAsync("() => document.querySelector('dialog.ms-confirm-overlay') === null", null, Wait);

            // Sampled in the page for two seconds, from the moment the dialog went: the page re-renders while the rebuild starts.
            string[] seen = await page.EvaluateAsync<string[]>(
                """
                () => new Promise(resolve => {
                    const seen = [];
                    const sample = () => { const a = document.activeElement; seen.push(!a ? 'none' : a === document.body ? 'BODY' : a.tagName + '.' + a.className); };
                    sample();
                    let n = 0;
                    const timer = setInterval(() => { sample(); if (++n >= 20) { clearInterval(timer); resolve(seen); } }, 100);
                })
                """);

            seen.Should().NotContain(x => x == "BODY" || x == "none", "focus is on the row, a control in it or the heading, never on the body: " + string.Join(", ", seen.Distinct()));

            studio.AssertClean("confirming a Rebuild");
        });
    }

    [BrowserFact]
    public async Task The_question_mark_opens_the_shortcut_sheet_and_Esc_closes_it_and_returns_focus()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "shortcut-sheet", fixture.Root, "admin", async studio =>
        {
            IPage page = studio.Page;
            await studio.GoAsync(fixture.Root.StudioUrl());

            // Focus a control first, so there is something to give back.
            ILocator link = page.Locator("a.ms-nav-link").First;
            await link.FocusAsync();
            string expected = await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')") ?? string.Empty;

            await page.Keyboard.PressAsync("Shift+Slash");

            await page.WaitForFunctionAsync("() => { const d = document.getElementById('ms-shortcuts'); return d !== null && d.open && d.matches(':modal'); }", null, Wait);
            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.closest('#ms-shortcuts') !== null && document.activeElement.textContent.trim() === 'Close'", null, Wait);

            string text = await page.Locator("#ms-shortcuts").TextContentAsync() ?? string.Empty;
            text.Should().Contain("Keyboard shortcuts").And.Contain("JSON viewer").And.Contain("Document editor").And.Contain("LINQ query").And.Contain("Plan icicle");

            // A second '?' does not stack a second dialog; Tab stays in the sheet.
            await page.Keyboard.PressAsync("Shift+Slash");
            (await page.Locator("dialog[open]").CountAsync()).Should().Be(1);
            for (int step = 0; step < 4; step++)
            {
                await page.Keyboard.PressAsync("Tab");
                (await page.EvaluateAsync<bool>("() => document.activeElement.closest('#ms-shortcuts') !== null")).Should().BeTrue();
            }

            await page.Keyboard.PressAsync("Escape");

            await page.WaitForFunctionAsync("() => !document.getElementById('ms-shortcuts').open", null, Wait);
            (await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')")).Should().Be(expected, "focus returns to the control that was focused");

            // Straight after it closed, with no wait in between: it opens again, and the Close button closes it too.
            await page.Keyboard.PressAsync("Shift+Slash");
            await page.WaitForFunctionAsync("() => document.getElementById('ms-shortcuts').open && document.getElementById('ms-shortcuts').matches(':modal')", null, Wait);
            await page.Locator("#ms-shortcuts button[data-ms-dialog-close]").ClickAsync();
            await page.WaitForFunctionAsync("() => !document.getElementById('ms-shortcuts').open", null, Wait);
            (await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')")).Should().Be(expected, "Close gives focus back too");

            studio.AssertClean("opening and closing the shortcut sheet");
        });
    }

    [BrowserFact]
    public async Task The_question_mark_is_a_character_in_a_text_field_and_opens_nothing_there()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "shortcut-sheet-in-field", fixture.Root, "admin", async studio =>
        {
            IPage page = studio.Page;
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events"));

            ILocator field = page.Locator("#ms-query-linq-where");
            await field.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await field.FocusAsync();
            await page.Keyboard.TypeAsync("Name == ?");

            (await field.InputValueAsync()).Should().Be("Name == ?", "'?' typed into a field is just a character");
            (await page.EvaluateAsync<bool>("() => document.getElementById('ms-shortcuts').open")).Should().BeFalse();

            // Outside the field it opens.
            await page.EvaluateAsync("() => document.activeElement.blur()");
            await page.Keyboard.PressAsync("Shift+Slash");
            await page.WaitForFunctionAsync("() => document.getElementById('ms-shortcuts').open", null, Wait);

            studio.AssertClean("typing a question mark into a field and then outside it");
        });
    }
}
