using System.Runtime.CompilerServices;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>Whether the README screenshot set is retaken by this run.</summary>
/// <remarks>
/// Opt-in, and emphatically not part of an ordinary run: this writes nine tracked PNGs into
/// <c>docs/screenshots/</c>. A suite that rewrote checked-in binaries on every <c>dotnet test</c> would
/// put a diff in front of everybody who ran the tests and make "is this change intentional" unanswerable.
/// </remarks>
public static class ScreenshotCapture
{
    /// <summary>The variable that turns the capture on. Set it to <c>1</c>.</summary>
    public const string EnvironmentVariable = "MARTENSTUDIO_SCREENSHOTS";

    /// <summary>Why the capture skipped.</summary>
    public const string SkipReason =
        "Retaking the README screenshots is opt-in: set " + EnvironmentVariable + "=1 with Docker running "
        + "and Chromium installed (`" + BrowserAvailability.InstallCommand + "`). It overwrites the "
        + "tracked PNGs under docs/screenshots/, so an ordinary test run must not do it.";

    /// <summary>Whether the capture runs.</summary>
    public static bool IsEnabled =>
        BrowserAvailability.CanRun
        && string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim(),
            "1",
            StringComparison.Ordinal);
}

/// <summary>A fact that retakes the screenshot set, and skips with a reason otherwise.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ScreenshotFactAttribute : FactAttribute
{
    /// <summary>Wires up xunit's conditional skip against <see cref="ScreenshotCapture.IsEnabled" />.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public ScreenshotFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = ScreenshotCapture.SkipReason;
        SkipType = typeof(ScreenshotCapture);
        SkipUnless = nameof(ScreenshotCapture.IsEnabled);
    }
}

/// <summary>
/// Retakes the nine screenshots <c>README.md</c> links to, from the real studio in a real browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a test and not a person with a screen-grab tool.</b> The set was taken by hand during
/// the P2 UI verification run, and by P9 three of the screens had changed shape and a fourth did not
/// exist. A screenshot nobody can retake is a screenshot that goes stale silently; one command that
/// retakes all nine at the same size, in the same theme, against the same seeded demo store makes the
/// set reproducible and the diff between two runs meaningful.
/// </para>
/// <para>
/// <b>The same shape as the existing set:</b> a 1440×900 viewport, the light theme chosen explicitly
/// rather than inherited from the machine, and a full-page capture — which is why several of the files
/// are taller than 900 pixels, exactly as the originals are.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class ScreenshotCaptureTests(BrowserSuiteFixture fixture)
{
    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [ScreenshotFact]
    public async Task Retake_the_readme_screenshots()
    {
        string directory = Path.Combine(SampleHost.RepositoryRoot(), "docs", "screenshots");
        Directory.CreateDirectory(directory);

        SampleHost host = fixture.Root;

        await using StudioPage studio = await StudioPage.SignInAsync(fixture.Browser, host, "admin");

        // Overview, both themes. The theme is set through the studio's own picker rather than through the
        // browser's colour-scheme preference, because that is the state a reader of the README is looking
        // at: an explicit `data-theme`, not whatever the machine that took the picture preferred.
        await studio.GoAsync(host.StudioUrl());
        await studio.Page.Locator(".ms-overview-tiles .ms-stat-card").First.WaitForAsync();
        await studio.SetThemeAsync("light");
        await CaptureAsync(studio, directory, "overview-light");

        await studio.SetThemeAsync("dark");
        await CaptureAsync(studio, directory, "overview-dark");
        await studio.SetThemeAsync("light");

        // The documents browser: the rail, the grid and a row's preview.
        await studio.GoAsync(host.StudioUrl("documents/customer"));
        await studio.Page.Locator(".ms-doc-table tbody tr.ms-doc-row").First.WaitForAsync();
        await CaptureAsync(studio, directory, "documents-list");

        await studio.Page.Locator(".ms-doc-table tbody tr.ms-doc-row a.ms-doc-id").First.ClickAsync();
        await studio.Page.Locator(".ms-json-row").First.WaitForAsync();
        await CaptureAsync(studio, directory, "document-detail");

        // One event stream, opened from the stream list.
        await studio.GoAsync(host.StudioUrl("events/streams"));
        await studio.Page.Locator(".ms-table tbody tr.ms-table-row .ms-id-cell a").First.ClickAsync();
        await studio.Page.Locator(".ms-page").First.WaitForAsync();
        await CaptureAsync(studio, directory, "stream-detail");

        // Dead letters. The demo poisons one stream on purpose and the async daemon is what writes the
        // entry, so this waits for the daemon rather than for a fixed delay - and captures the page
        // either way, because an empty dead-letter list is also a true picture of this screen.
        await studio.GoAsync(host.StudioUrl("events/dead-letters"));
        await WaitForRowsAsync(studio, ".ms-page table.ms-table tbody tr.ms-table-row", TimeSpan.FromSeconds(60));
        await CaptureAsync(studio, directory, "dead-letters");

        await studio.GoAsync(host.StudioUrl("projections"));
        await studio.Page.Locator(".ms-page").First.WaitForAsync();
        await CaptureAsync(studio, directory, "projections");

        // Schema, on its Drift tab, after pressing the button that runs the check. Nothing on that page
        // reads the database until somebody presses it (hard rule 14), so a capture that did not press it
        // would be a picture of the "Not checked yet" empty state.
        await studio.GoAsync(host.StudioUrl("schema"));
        await studio.Page.Locator(".ms-schema-toolbar .ms-button-primary").ClickAsync();
        await studio.Page.Locator(".ms-kv-row").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        await CaptureAsync(studio, directory, "schema-drift");

        // The screen the set never had.
        await studio.GoAsync(host.StudioUrl("relationships"));
        await studio.Page.Locator("svg.ms-graph-svg").WaitForAsync();
        await CaptureAsync(studio, directory, "relationships");

        studio.AssertClean("retaking the README screenshots");
    }

    private static async Task CaptureAsync(StudioPage studio, string directory, string name)
    {
        string path = Path.Combine(directory, name + ".png");

        await studio.ScreenshotAsync(path);

        Write(name + ".png -> " + path);
    }

    private static async Task WaitForRowsAsync(StudioPage studio, string selector, TimeSpan budget)
    {
        try
        {
            await studio.Page.Locator(selector).First.WaitForAsync(
                new LocatorWaitForOptions { Timeout = (float)budget.TotalMilliseconds });
        }
        catch (TimeoutException)
        {
            Write("No rows appeared for " + selector + " within " + budget + "; capturing the empty state.");
        }
    }
}
