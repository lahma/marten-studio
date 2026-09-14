using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Components;

using Serilog;

/// <summary>
/// The Playwright browser install (D19). Kept in its own partial so <c>Build.cs</c> stays the
/// version-and-release file and this stays the one place that knows how the browser suite is provisioned.
/// </summary>
partial class Build
{
    [Parameter("Install the Playwright browsers before Test. Implied on GitHub Actions.")]
    readonly bool Playwright;

    /// <summary>
    /// Local runs install browsers only when asked; CI always does.
    /// </summary>
    /// <remarks>
    /// This is the build-side half of D19. The test-side half is the opposite rule: locally the browser
    /// suite <em>skips with a reason</em> when Chromium is missing, on CI it <em>throws</em>. Together they
    /// mean a developer never pays a 150 MB download for `dotnet fallout Test`, and CI can never report a
    /// green run whose browser tests silently did not happen.
    /// </remarks>
    bool ShouldInstallBrowsers => Playwright || GitHubActions.Instance != null;

    /// <summary>
    /// Where the Playwright CLI bootstrapper lands. Microsoft.Playwright emits <c>playwright.ps1</c> into
    /// the referencing project's output directory at build time, so this path only exists after Compile.
    /// </summary>
    AbsolutePath PlaywrightScript =>
        TestsDirectory / "MartenStudio.Integration.Tests" / "bin" /
        ((IHasConfiguration)this).Configuration / "net10.0" / "playwright.ps1";

    Target Browsers => _ => _
        .Description("Installs the Chromium build the browser test suite drives")
        .DependsOn<ICompile>()
        .OnlyWhenDynamic(() => ShouldInstallBrowsers)
        .Executes(() =>
        {
            Assert.True(PlaywrightScript.FileExists(),
                $"'{PlaywrightScript}' does not exist. Microsoft.Playwright writes it into the test " +
                "project's output directory during build, so Compile has to run before Browsers - run " +
                "`dotnet fallout Test --playwright` (or Browsers on its own, which depends on Compile) " +
                "rather than invoking this target against a clean tree.");

            // pwsh, not powershell: playwright.ps1 is a PowerShell 7 script and Windows PowerShell 5.1
            // cannot run it. pwsh is on the GitHub runners and on this project's development machines.
            //
            // The two command lines are written out in full rather than composed from an `arguments`
            // variable, because Fallout's argument handling quotes every interpolation hole that contains
            // a space: `$"... {arguments}"` with arguments = "install chromium" reaches pwsh as the single
            // token "install chromium" and the Playwright CLI answers `unknown command 'install chromium'`.
            // Literal text in the format string is passed through word by word, which is what is wanted.
            //
            // --with-deps installs the Linux shared libraries headless Chromium needs, and is deliberately
            // CI-only: on a runner the image is disposable, on a developer machine it would ask for sudo
            // and change the machine. Chromium only - the suite drives one browser, and pulling Firefox
            // and WebKit as well would triple the download for nothing.
            Log.Information("Installing Playwright browsers from {Script}", PlaywrightScript);

            var process = GitHubActions.Instance != null
                ? ProcessTasks.StartProcess("pwsh", $"-NoProfile -File \"{PlaywrightScript}\" install chromium --with-deps")
                : ProcessTasks.StartProcess("pwsh", $"-NoProfile -File \"{PlaywrightScript}\" install chromium");

            process.AssertWaitForExit().AssertZeroExitCode();
        });

    /// <summary>
    /// Test depends on Browsers so that the one command anybody runs is enough.
    /// </summary>
    /// <remarks>
    /// <c>Inherit&lt;ITest&gt;()</c> keeps every bit of the component's own definition - the TRX and
    /// GitHub Actions loggers, the results directory, the produced artifacts - and only adds the
    /// dependency. Writing the target out by hand instead would silently drop the reporting.
    /// </remarks>
    Target ITest.Test => _ => _
        .DependsOn(Browsers)
        .Inherit<ITest>();
}
