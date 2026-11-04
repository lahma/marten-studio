using Microsoft.Playwright;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-SCOPE-3 in Postgres Studio's own shell: the header's Find box takes a table or view name under the database browser's gate, so a
/// host with no Marten has it too. <c>/</c> goes to it except where a character is being typed (the SQL console's editor), a name that
/// matches opens the object, and the box is a magnifier and a popover on a phone.
/// </summary>
[Collection(BrowserSuite.Name)]
public class FindBrowserTests(BrowserSuiteFixture fixture)
{
    private static readonly PageWaitForFunctionOptions Wait = new() { Timeout = 30_000 };

    private Task RunAsync(string name, Func<StudioPage, Task> body, int viewportWidth = StudioPage.ViewportWidth) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Host, "admin", body, viewportWidth);

    private static Task<bool> FocusIsInTheBoxAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => document.activeElement && document.activeElement.id === 'ms-find-input'");

    private static async Task WaitForTheBoxAsync(StudioPage studio)
    {
        await studio.WaitForInteractiveAsync();
        await studio.Page.WaitForFunctionAsync("() => { const f = document.getElementById('ms-find-input'); return !!f && !!f.msFindHandlers; }", null, Wait);
    }

    [BrowserFact]
    public Task A_table_name_opens_the_table_and_Escape_clears_a_search_that_found_nothing() =>
        RunAsync("pg-find-table", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());
            await WaitForTheBoxAsync(studio);

            await page.Locator("body").PressAsync("/");
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue("/ goes to the box");

            await page.Keyboard.TypeAsync("no_such_table_anywhere");
            await page.Keyboard.PressAsync("Enter");

            ILocator panel = page.Locator("[data-ms-find-panel]");
            await panel.WaitForAsync();
            (await page.Locator(".ms-find-status").InnerTextAsync()).Should().Be("Nothing found");

            await page.Keyboard.PressAsync("Escape");
            await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 30_000 });
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue();

            await page.Locator("#ms-find-input").FillAsync("quartz.qrtz_triggers");
            await page.Keyboard.PressAsync("Enter");

            await page.WaitForURLAsync(static url => url.Contains("name=qrtz_triggers", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
            page.Url.Should().Contain("database/object");

            studio.AssertNoPageFetch("opening a find result");
            studio.AssertClean("finding a table by its name");
        });

    [BrowserFact]
    public Task A_slash_typed_into_the_SQL_editor_is_a_character_and_leaves_the_focus_alone() =>
        RunAsync("pg-find-slash-in-editor", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("query"));
            await studio.WaitForInteractiveAsync();

            ILocator editor = page.Locator("textarea").First;
            await editor.WaitForAsync();
            await editor.FocusAsync();
            await page.Keyboard.TypeAsync("select 4 / 2");
            await page.Keyboard.PressAsync("Control+k");

            (await editor.InputValueAsync()).Should().Contain("4 / 2");
            (await FocusIsInTheBoxAsync(page)).Should().BeFalse("the editor keeps the keyboard while a character is being typed in it");

            studio.AssertClean("typing a slash in the editor");
        });

    [BrowserFact]
    public Task On_a_phone_the_box_is_a_magnifier_whose_popover_the_slash_key_opens() =>
        RunAsync(
            "pg-find-phone",
            async studio =>
            {
                IPage page = studio.Page;

                await studio.GoAsync(fixture.Host.StudioUrl());
                await WaitForTheBoxAsync(studio);

                (await page.Locator(".ms-find-toggle").IsVisibleAsync()).Should().BeTrue();
                (await page.Locator("#ms-find-input").IsVisibleAsync()).Should().BeFalse();

                await page.Locator("a.ms-nav-link").First.FocusAsync();
                await page.Keyboard.PressAsync("Slash");
                await page.WaitForFunctionAsync("() => document.getElementById('ms-find-popover').matches(':popover-open')", null, Wait);
                (await FocusIsInTheBoxAsync(page)).Should().BeTrue();

                bool fits = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth");
                fits.Should().BeTrue("the page does not scroll sideways at 390 px");

                studio.AssertClean("opening the Find popover at 390 px");
            },
            viewportWidth: 390);
}
