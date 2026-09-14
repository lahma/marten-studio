using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

using MartenStudio.Integration.Tests.Generation;
using MartenStudio.Sample.Auth;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>Whether the large-data browser pass runs: both gates, plus the opt-in.</summary>
/// <remarks>
/// Opt-in for the same reason the service-level large suite is: generating a hundred thousand documents
/// and a hundred thousand events takes minutes, and a pass nobody runs measures nothing. The difference
/// from <see cref="LargeDataSet" /> is only that this one also needs a browser.
/// </remarks>
public static class LargeBrowserData
{
    /// <summary>Why the large browser pass skipped.</summary>
    public const string SkipReason =
        "The large-data browser pass is opt-in: set " + LargeDataSet.EnvironmentVariable + "=1, have Docker "
        + "running, and install Chromium (`" + BrowserAvailability.InstallCommand + "`). It generates the "
        + "Medium preset through the sample's own demo-data endpoints, which takes a few minutes.";

    /// <summary>Whether the pass runs.</summary>
    public static bool IsEnabled => LargeDataSet.IsEnabled && BrowserAvailability.CanRun;
}

/// <summary>A fact that needs a browser, Docker and the large-data opt-in.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LargeBrowserFactAttribute : FactAttribute
{
    /// <summary>Wires up xunit's conditional skip against <see cref="LargeBrowserData.IsEnabled" />.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public LargeBrowserFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = LargeBrowserData.SkipReason;
        SkipType = typeof(LargeBrowserData);
        SkipUnless = nameof(LargeBrowserData.IsEnabled);
    }
}

/// <summary>
/// A sample host of its own with the Medium preset already generated into it, through the sample's own
/// demo-data endpoints.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the endpoints, not around them.</b> The generator could be called in-process, and the
/// service-level suite does exactly that. Driving the host's own <c>POST /sample/generate</c> instead
/// proves the demo panel a reader of the README is told to use, and it puts the rows in through the same
/// path a person would — including the authorization and the antiforgery check on the way in.
/// </para>
/// <para>
/// <b>A database and a host of its own.</b> A hundred thousand generated documents in the database the
/// six scenarios read would change what those scenarios see and how long they take, which is exactly the
/// coupling that makes a suite unreadable when one test fails.
/// </para>
/// </remarks>
public sealed class LargeDataBrowserFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The database this pass owns.</summary>
    public const string Database = "browser_large";

    /// <summary>How long the Medium generation gets.</summary>
    private static readonly TimeSpan GenerationTimeout = TimeSpan.FromMinutes(20);

    private static readonly Regex RequestToken = new(
        "name=\"" + LoginEndpoints.AntiforgeryFieldName + "\" value=\"(?<token>[^\"]+)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private SampleHost? host;

    /// <summary>The host, with the Medium preset in its store.</summary>
    internal SampleHost Host => host
        ?? throw new InvalidOperationException(
            "The large-data host was not started. A test that needs it must be a [LargeBrowserFact].");

    /// <summary>How long the generation took, for the test output.</summary>
    public TimeSpan GenerationTime { get; private set; }

    /// <summary>What the sample said it wrote.</summary>
    public long Documents { get; private set; }

    /// <summary>What the sample said it appended.</summary>
    public long Events { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!LargeBrowserData.IsEnabled)
        {
            return;
        }

        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, Database);

        host = await SampleHost.StartAsync(connectionString, allowDataGeneration: true);

        await SampleHost.WaitForSeedAsync(connectionString);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        await GenerateMediumAsync();
        GenerationTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    private async Task GenerateMediumAsync()
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        using var client = new HttpClient(handler) { BaseAddress = Host.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };

        await SignInAsync(client);

        string token = await TokenAsync(client, "/");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = token,
            ["size"] = "Medium",
        });

        using HttpResponseMessage started = await client.PostAsync(new Uri("/sample/generate", UriKind.Relative), form);
        started.IsSuccessStatusCode.Should().BeTrue(
            "POST /sample/generate has to be accepted for the large pass to have anything to measure");

        await WaitForCompletionAsync(client);
    }

    private static async Task SignInAsync(HttpClient client)
    {
        string token = await TokenAsync(client, "/login");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = token,
            ["username"] = "admin",
            ["password"] = "admin",
            ["returnUrl"] = "/",
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri("/login", UriKind.Relative), form);

        response.IsSuccessStatusCode.Should().BeTrue("the demo signs admin in with the user name as the password");
    }

    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        using HttpResponseMessage page = await client.GetAsync(new Uri(path, UriKind.Relative));
        page.EnsureSuccessStatusCode();

        string html = await page.Content.ReadAsStringAsync();
        Match match = RequestToken.Match(html);

        match.Success.Should().BeTrue("the sample's forms carry a request token, and " + path + " must render one");

        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private async Task WaitForCompletionAsync(HttpClient client)
    {
        using var timeout = new CancellationTokenSource(GenerationTimeout);

        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();

            using HttpResponseMessage response = await client.GetAsync(
                new Uri("/sample/generate/status", UriKind.Relative), timeout.Token);

            response.EnsureSuccessStatusCode();

            using JsonDocument status = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(timeout.Token));

            string state = status.RootElement.GetProperty("state").GetString() ?? "Idle";

            if (state is "Completed")
            {
                Documents = status.RootElement.GetProperty("documents").GetInt64();
                Events = status.RootElement.GetProperty("events").GetInt64();
                return;
            }

            if (state is "Failed" or "Cancelled")
            {
                throw new InvalidOperationException(
                    "The demo-data generation ended as " + state + ": "
                    + (status.RootElement.GetProperty("error").GetString() ?? "(no reason given)"));
            }

            // Wait on the condition, never on a fixed delay that pretends to know how long it takes.
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
    }
}

/// <summary>
/// The studio in a browser against a hundred thousand documents and a hundred thousand events.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds to the service-level large suite.</b> That one proves no read on a navigation path
/// is proportional to the collection. This one proves the <em>screen</em> is not: a page that reads in
/// 200 ms and then renders ten thousand rows into the DOM, or holds a circuit open while it serializes a
/// megabyte of render tree, is slow in a way no service measurement can see.
/// </para>
/// <para>
/// The budgets are <see cref="PageResponsivenessBudgets" />, which are the service budgets plus one
/// stated allowance for everything between the navigation and the paint. See that type for why they are
/// not the same numbers and why there is still only one set.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class LargeDataBrowserTests(BrowserSuiteFixture browsers, LargeDataBrowserFixture data)
    : IClassFixture<LargeDataBrowserFixture>
{
    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>Every screen a person opens first, painted inside its budget at a hundred thousand rows.</summary>
    [LargeBrowserFact]
    public async Task Every_screens_first_paint_stays_inside_its_budget()
    {
        Write(string.Format(
            CultureInfo.InvariantCulture,
            "Generated {0:N0} documents and {1:N0} events through POST /sample/generate in {2:F1} s",
            data.Documents,
            data.Events,
            data.GenerationTime.TotalSeconds));

        data.Documents.Should().BeGreaterThan(50_000, "the Medium preset is about a hundred thousand documents");

        SampleHost host = data.Host;

        await using StudioPage studio = await StudioPage.SignInAsync(browsers.Browser, host, "admin");

        var report = new BudgetReport(Write);

        report.Check(
            "documents rail",
            await MeasureAsync(studio, host.StudioUrl("documents"), ".ms-rail-list"),
            PageResponsivenessBudgets.DocumentsIndex);

        report.Check(
            "customer list page",
            await MeasureAsync(studio, host.StudioUrl("documents/customer"), ".ms-doc-table tbody tr.ms-doc-row"),
            PageResponsivenessBudgets.DocumentsPage);

        report.Check(
            "event feed page",
            await MeasureAsync(studio, host.StudioUrl("events/feed"), ".ms-page"),
            PageResponsivenessBudgets.FeedPage);

        report.Check(
            "stream list page",
            await MeasureAsync(studio, host.StudioUrl("events/streams"), ".ms-page"),
            PageResponsivenessBudgets.StreamsPage);

        report.Check(
            "projections page",
            await MeasureAsync(studio, host.StudioUrl("projections"), ".ms-page"),
            PageResponsivenessBudgets.ProjectionsPage);

        // The circuit still attaches at this size, which is the other half of "the screen works": a page
        // that paints fast and never becomes interactive is not a page anybody can use.
        await studio.WaitForCircuitAsync();

        studio.AssertClean("walking documents, the feed, the streams and projections at a hundred thousand rows");

        report.Unexpected.Should().BeEmpty(report.Because);
    }

    /// <summary>
    /// The median of <see cref="ResponsivenessBudgets.Samples" /> navigations to first paint.
    /// </summary>
    /// <remarks>
    /// The same measurement shape the service-level suite uses, for the same reason: a mean is dragged by
    /// the first navigation's connection and code generation, and a best-of hides the tail that makes a
    /// UI feel slow.
    /// </remarks>
    private static async Task<TimeSpan> MeasureAsync(StudioPage studio, string url, string selector)
    {
        (TimeSpan median, _) = await ResponsivenessBudgets.MeasureAsync(async () =>
        {
            await studio.Page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await studio.Page.Locator(selector).First.WaitForAsync();
            return true;
        });

        return median;
    }
}
