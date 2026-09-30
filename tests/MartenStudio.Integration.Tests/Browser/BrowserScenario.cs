using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// What every browser scenario shares: the runner that leaves evidence behind when one fails, and the
/// assertions more than one screen has to pass.
/// </summary>
/// <remarks>
/// One runner rather than a copy per test class, so that the database browser's scenarios fail the way the
/// original six do - a screenshot, the page's console and network, and the sample host's own log - and a
/// fix to what a failure leaves behind lands everywhere at once.
/// </remarks>
internal static class BrowserScenario
{
    /// <summary>
    /// Runs one scenario in a browser context of its own, and on failure leaves a screenshot and the host's
    /// log behind.
    /// </summary>
    /// <param name="browser">The shared browser.</param>
    /// <param name="name">The scenario's name, which is the screenshot's file name.</param>
    /// <param name="host">The sample host to drive.</param>
    /// <param name="user">Which demo user to sign in as.</param>
    /// <param name="body">The scenario.</param>
    public static async Task RunAsync(IBrowser browser, string name, SampleHost host, string user, Func<StudioPage, Task> body)
    {
        await using StudioPage studio = await StudioPage.SignInAsync(browser, host, user);

        try
        {
            await body(studio);
        }
        catch
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "browser-screenshots");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name + ".png");

            try
            {
                await studio.ScreenshotAsync(path);
                Write("Screenshot: " + path);
            }
            catch (PlaywrightException exception)
            {
                Write("No screenshot could be taken: " + exception.Message);
            }

            Write("URL:              " + studio.Page.Url);
            Write("Page errors:      " + Join(studio.PageErrors));
            Write("Console problems: " + Join(studio.ConsoleProblems));
            Write("Failed responses: " + Join(studio.FailedResponses));
            Write("WebSockets:       " + Join(studio.WebSockets));
            Write("Sample host (pid " + host.ProcessId + ") log, last 60 lines:");
            Write(host.LogTail());

            throw;
        }
    }

    /// <summary>
    /// Every arrow is a row and every row an arrow, compared by the <c>data-edge</c> identity both carry: a
    /// table that listed fewer rows than the picture has arrows is a screen telling two stories.
    /// </summary>
    /// <param name="edges">The diagram's edges, scoped to the diagram's own <c>svg</c> - never the legend's.</param>
    /// <param name="rows">The accessible table's rows.</param>
    /// <param name="where">Which picture this is, for the failure message.</param>
    public static async Task AssertTheTableListsTheDiagramAsync(ILocator edges, ILocator rows, string where)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(rows);

        (await edges.CountAsync()).Should().BeGreaterThan(0, where + " draws something");

        List<string> drawn = [];
        foreach (ILocator edge in await edges.AllAsync())
        {
            drawn.Add(await edge.GetAttributeAsync("data-edge") ?? string.Empty);
        }

        List<string> listed = [];
        foreach (ILocator row in await rows.AllAsync())
        {
            listed.Add(await row.GetAttributeAsync("data-edge") ?? string.Empty);
        }

        drawn.Should().NotContain(string.Empty, "every arrow in " + where + " carries the identity its row is matched by");

        listed.Should().BeEquivalentTo(drawn,
            "in " + where + " every row of the accessible table is an arrow on the diagram and every arrow a row");
    }

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static string Join(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "(none)" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lines);
}
