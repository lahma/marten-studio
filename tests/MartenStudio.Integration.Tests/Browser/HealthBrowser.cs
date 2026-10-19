using System.Globalization;

using MartenStudio.Sample.Generation;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// What the health scenarios and the Health screenshot share: the sample's "make it unhealthy" switches pressed on its
/// landing page, the Health page's finding cards, a wait that presses Refresh until a sweep shows what it waits for,
/// and a clean-up that neither destroys a failure's evidence nor hides its exception.
/// </summary>
internal static class HealthBrowser
{
    /// <summary>A sweep cached for a minute, plus the sweep itself and a slow runner.</summary>
    public static readonly TimeSpan NextSweep = TimeSpan.FromSeconds(150);

    /// <summary>A minute of a threshold - C04's idleness, D01's pin - then <see cref="NextSweep" />.</summary>
    public static readonly TimeSpan AfterAMinute = TimeSpan.FromSeconds(240);

    /// <summary>A finding's card in the unmuted list, by code.</summary>
    /// <remarks>The card itself, by element: its title's id starts the same way (<c>finding-t07-…-title</c>).</remarks>
    public static string Visible(string code) => ".ms-health-area article[id^='finding-" + code + "']";

    /// <summary>A finding's card in the muted list - drawn inside a closed <c>details</c>, so attached rather than visible.</summary>
    public static string Muted(string code) => ".ms-health-muted article[id^='finding-" + code + "']";

    /// <summary>The Health page's Refresh.</summary>
    public static ILocator HealthRefresh(IPage page) =>
        page.Locator(".ms-health-actions button", new PageLocatorOptions { HasTextString = "Refresh" });

    /// <summary>Database › Sessions' Refresh.</summary>
    public static ILocator SessionsRefresh(IPage page) =>
        page.Locator(".ms-sessions-actions button", new PageLocatorOptions { HasTextString = "Refresh" });

    /// <summary>
    /// Turns a demo switch on from the sample's landing page, and waits until its panel says it is on - failing with
    /// the panel's own words if it says the switch failed, rather than waiting out the timeout.
    /// </summary>
    /// <remarks>
    /// The form posts, the endpoint redirects back to <c>/</c>, and the panel's own script reads the switch's state from
    /// the status endpoint every two seconds and stamps it on the card - so the wait is on that attribute, in the page.
    /// </remarks>
    public static async Task TurnOnAsync(IPage page, SampleHost host, string slug)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(host);

        ILocator card = await OpenPanelAsync(page, host, slug);

        if (await card.GetAttributeAsync("data-state") == "On")
        {
            return;
        }

        await card.Locator("form[data-when='off'] button[type=submit]").ClickAsync();
        await WaitForStateAsync(page, slug, "On", "Failed");

        if (await card.GetAttributeAsync("data-state") == "Failed")
        {
            throw new InvalidOperationException(
                "The " + slug + " switch failed to turn on: " + await card.Locator("[data-field='detail']").InnerTextAsync());
        }
    }

    /// <summary>
    /// Turns a demo switch off from the sample's landing page, and waits until it is no longer running.
    /// </summary>
    /// <remarks>
    /// A card that already reads <c>Off</c> or <c>Failed</c> before the click is left alone: a run that ended in an error
    /// has stopped, and pressing anything would not change that. A card that reads <c>Failed</c> <em>after</em> the click
    /// is the switch's own clean-up failing - whatever the switch held may still be held - so that throws, with the
    /// panel's own words, as <see cref="TurnOnAsync" /> does.
    /// </remarks>
    public static async Task TurnOffAsync(IPage page, SampleHost host, string slug)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(host);

        ILocator card = await OpenPanelAsync(page, host, slug);

        if (await card.GetAttributeAsync("data-state") is "Off" or "Failed")
        {
            return;
        }

        await card.Locator("form[data-when='on'] button[type=submit]").ClickAsync();
        await WaitForStateAsync(page, slug, "Off", "Failed");

        if (await card.GetAttributeAsync("data-state") == "Failed")
        {
            throw new InvalidOperationException(
                "The " + slug + " switch failed to turn off, so what it held may still be held: "
                + await card.Locator("[data-field='detail']").InnerTextAsync());
        }
    }

    /// <summary>
    /// Presses <paramref name="refresh" /> until <paramref name="target" /> is on the page: the sweep behind it is cached
    /// for a minute, so the first press after that is the one that shows it.
    /// </summary>
    public static async Task UntilShownAsync(ILocator target, ILocator refresh, TimeSpan timeout, string what)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(refresh);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (await target.CountAsync() == 0)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Gave up after " + timeout + " waiting for " + what + ".");
            }

            if (await refresh.IsEnabledAsync())
            {
                await refresh.ClickAsync();
            }

            try
            {
                await target.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                // Not on this sweep; the next press may run the one that has it.
            }
        }
    }

    /// <summary>
    /// The health badge's count once it agrees with the Warnings and Criticals the page shows unmuted - the badge reads
    /// the glance on the sidebar's own poll, so it can be a few seconds behind the page. No badge counts as 0.
    /// </summary>
    public static async Task<int> BadgeAgreesWithThePageAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        IJSHandle agreed = await page.WaitForFunctionAsync(
            """
            () => {
              const page = document.querySelectorAll('.ms-health-area .ms-health-finding-warning, .ms-health-area .ms-health-finding-critical').length;
              const link = [...document.querySelectorAll('.ms-nav-link')].find(a => a.textContent.includes('Health'));
              const badge = link ? link.querySelector('.ms-nav-badge') : null;
              const shown = badge ? parseInt(badge.textContent.trim(), 10) : 0;
              return page === shown ? String(page) : null;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        return int.Parse(await agreed.JsonValueAsync<string>(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Runs a scenario's clean-up steps on a page of their own, each in its own <c>try</c>.
    /// </summary>
    /// <param name="studio">The scenario's page, which is left exactly where the scenario left it.</param>
    /// <param name="scenarioPassed">Whether the scenario's own body ran to the end.</param>
    /// <param name="steps">What to undo, in order, each with a name for the report.</param>
    /// <remarks>
    /// <para>
    /// <b>A page of its own, in the scenario's browser context</b> - so it is signed in as the scenario's user - because
    /// a clean-up that navigates the scenario's page to the sample's landing page destroys the evidence
    /// <see cref="BrowserScenario.RunAsync" /> collects when a scenario fails: the screenshot and the URL are taken after
    /// this <c>finally</c> has run.
    /// </para>
    /// <para>
    /// <b>Each step in its own <c>try</c>, and every step run.</b> A step that fails neither skips the steps after it nor
    /// replaces the exception the scenario is failing with: its failure is written to the test output. A page that could
    /// not be opened is one more failure, not a reason to skip the steps that need none - restoring a row through a
    /// connection of the test's own - so each step is handed the page or <see langword="null" />, and one that needs it
    /// says so through <see cref="OnPage" />. When the scenario itself passed, a failed step fails it, because a green
    /// scenario that leaves a switch on has left the next one an unhealthy database.
    /// </para>
    /// </remarks>
    public static async Task CleanUpAsync(StudioPage studio, bool scenarioPassed, params (string What, Func<IPage?, Task> Step)[] steps)
    {
        ArgumentNullException.ThrowIfNull(studio);
        ArgumentNullException.ThrowIfNull(steps);

        List<string> failures = [];
        IPage? page = null;

        try
        {
            page = await studio.Page.Context.NewPageAsync();
        }
        catch (PlaywrightException exception)
        {
            failures.Add("opening a page to clean up on: " + exception.Message);
        }

        foreach ((string what, Func<IPage?, Task> step) in steps)
        {
            try
            {
                await step(page);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(what + ": " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        if (page is not null)
        {
            try
            {
                await page.CloseAsync();
            }
            catch (PlaywrightException exception)
            {
                failures.Add("closing the clean-up page: " + exception.Message);
            }
        }

        foreach (string failure in failures)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("Clean-up failed - " + failure);
        }

        if (scenarioPassed && failures.Count > 0)
        {
            throw new InvalidOperationException("The scenario passed, and its clean-up did not: " + string.Join(" | ", failures));
        }
    }

    /// <summary>
    /// A clean-up step that needs the clean-up page: it fails, as a step, when the page could not be opened, and the
    /// steps after it still run.
    /// </summary>
    public static Func<IPage?, Task> OnPage(Func<IPage, Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return page => page is null
            ? throw new InvalidOperationException("this step needs the clean-up page, which could not be opened")
            : step(page);
    }

    private static async Task<ILocator> OpenPanelAsync(IPage page, SampleHost host, string slug)
    {
        await page.GotoAsync(host.Url("/"), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator card = page.Locator("#unhealthy-" + slug);
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

        return card;
    }

    /// <summary>
    /// Waits, in the page, for the landing page the switch's form redirected to - it carries the endpoint's message in
    /// its query string - to stamp one of <paramref name="states" /> on the switch's card. The redirect is part of the
    /// condition so that the card of the page the click left, which may still say <c>Failed</c> from an earlier run,
    /// never answers for the new one.
    /// </summary>
    private static async Task WaitForStateAsync(IPage page, string slug, params string[] states) =>
        await page.WaitForFunctionAsync(
            """
            ([slug, states, key]) => {
              if (!new URLSearchParams(window.location.search).has(key)) { return false; }
              const card = document.getElementById('unhealthy-' + slug);
              return card !== null && states.includes(card.dataset.state);
            }
            """,
            new object[] { slug, states, UnhealthySwitchEndpoints.MessageQueryKey },
            new PageWaitForFunctionOptions { Timeout = 90_000 });
}
