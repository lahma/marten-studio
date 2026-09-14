using System.Globalization;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// One browser page, with everything the page said and everything the server answered recorded beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Console and network cleanliness is an assertion, not an eyeball.</b> A Blazor Server page that has
/// lost its circuit, failed to load its script, or thrown inside a JS initializer still <em>renders</em>:
/// the prerendered HTML is there and a screenshot looks right. What it does is write to the console and
/// leave a 404 in the network log. So every scenario asserts zero <c>console.error</c>, zero
/// <c>console.warn</c>, zero uncaught page errors and no response of 400 or more — which is what turns
/// "it looked fine" into a test.
/// </para>
/// <para>
/// <b>An expected failure is named by URL.</b> Under a sub-path mount the sample answers 404 for
/// <c>/marten</c>, and that is the point rather than an annoyance. It is allowed by its exact URL through
/// <see cref="ExpectStatus" />, never by ignoring the class — an allowance that said "404s are fine"
/// would also have hidden the missing <c>blazor.web.js</c> this suite exists to catch.
/// </para>
/// <para>
/// <b>Every handler here is synchronous.</b> Playwright's page events are <c>EventHandler&lt;T&gt;</c>,
/// and an <c>async</c> lambda converted to one is an <c>async void</c> with different spelling —
/// forbidden in every shape by AGENTS.md hard rule 6. They append to lists under a lock, because
/// Playwright raises them on its own dispatch loop.
/// </para>
/// </remarks>
internal sealed class StudioPage : IAsyncDisposable
{
    /// <summary>The viewport every scenario and every screenshot uses.</summary>
    public const int ViewportWidth = 1440;

    /// <summary>The viewport every scenario and every screenshot uses.</summary>
    public const int ViewportHeight = 900;

    private readonly IBrowserContext context;
    private readonly List<string> consoleProblems = [];
    private readonly List<string> pageErrors = [];
    private readonly List<string> failedResponses = [];
    private readonly List<string> webSockets = [];
    private readonly Dictionary<string, int> expected = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    private StudioPage(IBrowserContext context, IPage page, SampleHost host)
    {
        this.context = context;
        Page = page;
        Host = host;

        // Synchronous by construction; see the note on the type.
        page.Console += OnConsole;
        page.PageError += OnPageError;
        page.Response += OnResponse;
        page.WebSocket += OnWebSocket;
    }

    /// <summary>The Playwright page.</summary>
    public IPage Page { get; }

    /// <summary>The host it is pointed at.</summary>
    public SampleHost Host { get; }

    /// <summary>Every <c>console.error</c> and <c>console.warn</c>, in order.</summary>
    public IReadOnlyList<string> ConsoleProblems
    {
        get
        {
            lock (gate)
            {
                return [.. consoleProblems];
            }
        }
    }

    /// <summary>Every uncaught exception the page reported.</summary>
    public IReadOnlyList<string> PageErrors
    {
        get
        {
            lock (gate)
            {
                return [.. pageErrors];
            }
        }
    }

    /// <summary>Every response of 400 or more that was not expected by URL.</summary>
    public IReadOnlyList<string> FailedResponses
    {
        get
        {
            lock (gate)
            {
                return [.. failedResponses];
            }
        }
    }

    /// <summary>Every WebSocket the page opened, by URL.</summary>
    public IReadOnlyList<string> WebSockets
    {
        get
        {
            lock (gate)
            {
                return [.. webSockets];
            }
        }
    }

    /// <summary>
    /// Opens a page against <paramref name="host" />, signed in as <paramref name="user" />.
    /// </summary>
    /// <param name="browser">The shared browser.</param>
    /// <param name="host">The host to drive.</param>
    /// <param name="user">One of the demo users; the password is the user name.</param>
    /// <remarks>
    /// A browser context of its own per scenario, so cookies, <c>localStorage</c> (the studio keeps the
    /// theme, the time zone and the pinned collections there) and the recorded console never leak from
    /// one scenario into the next.
    /// </remarks>
    public static async Task<StudioPage> SignInAsync(IBrowser browser, SampleHost host, string user)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(host);

        IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
            BaseURL = host.BaseAddress.AbsoluteUri,
            ColorScheme = ColorScheme.Light,
            TimezoneId = "UTC",
            Locale = "en-GB",
        });

        IPage page = await context.NewPageAsync();
        var studio = new StudioPage(context, page, host);

        await page.GotoAsync(host.Url("/login"), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.FillAsync("#username", user);
        await page.FillAsync("#password", user);
        await page.ClickAsync("form[action='/login'] button[type=submit]");
        await page.WaitForURLAsync(host.Url("/"), new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        return studio;
    }

    /// <summary>
    /// Allows one exact URL to answer <paramref name="status" /> without failing the scenario.
    /// </summary>
    /// <param name="absoluteUrl">The URL, exactly as the browser will request it.</param>
    /// <param name="status">The status it is allowed to answer.</param>
    /// <remarks>
    /// It covers the console too, because Chromium writes its own <c>console.error</c> ("Failed to load
    /// resource: the server responded with a status of 404") for a failed navigation. Allowing the
    /// response and not the message would only move the failure one assertion along — and relaxing the
    /// console assertion to "ignore 404 messages" would hide the missing script this suite exists to
    /// catch. So the allowance is by exact URL on both.
    /// </remarks>
    public void ExpectStatus(string absoluteUrl, int status)
    {
        lock (gate)
        {
            expected[absoluteUrl] = status;
        }
    }

    /// <summary>
    /// Navigates to <paramref name="url" /> and waits until the Blazor circuit has attached.
    /// </summary>
    /// <param name="url">An absolute URL.</param>
    public async Task GoAsync(string url)
    {
        await Page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForCircuitAsync();
        await WaitForScopeInUrlAsync();
    }

    /// <summary>
    /// Waits until the studio has written its active scope into the query string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the studio's own proof that the circuit attached: <c>StudioLayout.SyncScopeToUrl</c>
    /// returns without doing anything unless <c>OnAfterRenderAsync(firstRender)</c> has run, which only
    /// happens on a live circuit — during prerendering a <c>NavigateTo</c> would be a redirect (D9).
    /// </para>
    /// <para>
    /// It is also a navigation, which is the reason a scenario has to wait for it rather than for the
    /// WebSocket alone. It was measured racing a click: driving the theme picker immediately after the
    /// socket opened left the control moved and the page re-rendered from the server's copy of the older
    /// state, so the assertion failed about half the time and only on the slower of the two hosts. Wait
    /// on the condition, never on a delay.
    /// </para>
    /// </remarks>
    public Task WaitForScopeInUrlAsync() =>
        Page.WaitForURLAsync(
            static url => url.Contains("store=", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 30_000 });

    /// <summary>
    /// Waits until the page is interactive, not merely prerendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The studio is Interactive Server (D2), which means the server renders the whole page once over
    /// HTTP and <em>then</em> the browser opens a circuit and takes over. Every assertion in this suite
    /// has to be made after the second step, because the first one happens even when the circuit can
    /// never attach — which is exactly the failure the sub-path scenario is about.
    /// </para>
    /// <para>
    /// The signal is the circuit's own WebSocket. <c>window.Blazor</c> is defined by the script whether
    /// or not it connects, and a rendered element is rendered by the prerender, so neither says anything;
    /// a WebSocket to <c>{path}/_blazor</c> is the circuit and nothing else.
    /// </para>
    /// </remarks>
    public async Task WaitForCircuitAsync()
    {
        await Page.WaitForFunctionAsync(
            "() => window.Blazor !== undefined",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        string marker = Host.StudioPath + "/_blazor";

        // Wait on the condition, never on a delay: the socket usually exists by the time the script has
        // defined window.Blazor, but on a cold circuit it is a round trip behind.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!WebSockets.Any(x => x.Contains(marker, StringComparison.Ordinal)))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    /// <summary>
    /// Proves the circuit is live by making the server re-render, and puts the page in a known theme.
    /// </summary>
    /// <remarks>
    /// The theme picker is the cheapest universal interactivity probe the studio has: it is in the layout
    /// so it is on every page, its <c>@onchange</c> runs on the server, and the result is an attribute on
    /// an element the browser can read back. A prerendered page has the picker and does nothing when it
    /// is moved.
    /// </remarks>
    /// <param name="theme"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
    public async Task SetThemeAsync(string theme)
    {
        await Page.SelectOptionAsync("#ms-theme-select", theme);
        await Page.Locator(".ms-studio[data-theme='" + theme + "']").WaitForAsync(
            new LocatorWaitForOptions { Timeout = 15_000 });
    }

    /// <summary>Writes a screenshot of the whole page to <paramref name="path" />.</summary>
    /// <remarks>
    /// The focus is dropped first. <c>&lt;FocusOnNavigate Selector="h1" /&gt;</c> focuses the page heading
    /// after every navigation, which is right for a screen reader - and Chromium counts a scripted
    /// navigation as keyboard-shaped, so <c>h1:focus-visible</c> draws its ring and every README image
    /// came out with a box round its title. A person clicking a nav link with a mouse does not see it.
    /// </remarks>
    /// <param name="path">Where to write it.</param>
    /// <param name="fullPage">Whether to capture past the viewport.</param>
    public async Task ScreenshotAsync(string path, bool fullPage = true)
    {
        await Page.EvaluateAsync("() => (document.activeElement instanceof HTMLElement) && document.activeElement.blur()");

        await Page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = fullPage });
    }

    /// <summary>
    /// Fails the scenario if the page complained or the server refused anything.
    /// </summary>
    /// <param name="because">What the scenario was doing, for the failure message.</param>
    public void AssertClean(string because)
    {
        PageErrors.Should().BeEmpty(
            "an uncaught exception in the page means the circuit is broken or a script threw, while " +
            because + "; the page reported: " + string.Join(" | ", PageErrors));

        ConsoleProblems.Should().BeEmpty(
            "a studio page must produce no console error and no console warning while " + because +
            "; the page said: " + string.Join(" | ", ConsoleProblems));

        FailedResponses.Should().BeEmpty(
            "no request a studio page makes may answer 400 or more while " + because +
            "; these did: " + string.Join(" | ", FailedResponses));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Page.Console -= OnConsole;
        Page.PageError -= OnPageError;
        Page.Response -= OnResponse;
        Page.WebSocket -= OnWebSocket;

        await context.CloseAsync();
    }

    private void OnConsole(object? sender, IConsoleMessage message)
    {
        if (message.Type is not ("error" or "warning"))
        {
            return;
        }

        lock (gate)
        {
            // Chromium reports a failed request as a console error located at the request's own URL, so
            // an allowance made by URL covers it here as well.
            if (expected.Keys.Any(url => message.Location.StartsWith(url, StringComparison.Ordinal)))
            {
                return;
            }

            consoleProblems.Add(message.Type + ": " + message.Text + " @ " + message.Location);
        }
    }

    private void OnPageError(object? sender, string error)
    {
        lock (gate)
        {
            pageErrors.Add(error);
        }
    }

    private void OnResponse(object? sender, IResponse response)
    {
        if (response.Status < 400)
        {
            return;
        }

        lock (gate)
        {
            if (expected.TryGetValue(response.Url, out int allowed) && allowed == response.Status)
            {
                return;
            }

            failedResponses.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1}",
                response.Status,
                response.Url));
        }
    }

    private void OnWebSocket(object? sender, IWebSocket socket)
    {
        lock (gate)
        {
            webSockets.Add(socket.Url);
        }
    }
}
