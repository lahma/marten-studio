using System.Runtime.CompilerServices;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>What the browser suite does when Chromium is not installed.</summary>
public enum BrowserGate
{
    /// <summary>Chromium is there; the scenarios run.</summary>
    Run,

    /// <summary>It is not, and this is a developer's machine: skip, and say how to install it.</summary>
    Skip,

    /// <summary>It is not, and this is CI: fail, because a green run that did not run is a lie.</summary>
    Throw,
}

/// <summary>
/// Whether there is a Playwright Chromium to drive — and what to do when there is not (D19).
/// </summary>
/// <remarks>
/// <para>
/// The rule is asymmetric, and it is deliberately the same one <see cref="DockerAvailability" /> states
/// for the Postgres container. <b>Locally</b> a missing browser is a skip whose reason carries the exact
/// command that installs it: a developer must not pay a 150 MB Chromium download for
/// <c>dotnet fallout Test</c>, and failing their run for a tool they were never asked to install is
/// hostile. <b>On CI</b> a missing browser is a failure, because the whole value of this suite is that it
/// runs — a run that silently skipped its browser tests and reported green is worse than a red one.
/// </para>
/// <para>
/// <b>The command in the reason is the one that works.</b> <c>Browsers</c> is
/// <c>OnlyWhenDynamic(() =&gt; ShouldInstallBrowsers)</c>, so before this packet
/// <c>dotnet fallout Browsers</c> on its own printed <c>Skipped // OnlyWhen: ShouldInstallBrowsers</c>
/// and installed nothing — a command AGENTS.md documents that quietly did nothing, which is the same
/// failure this class exists to prevent one layer up. <c>Build.Browsers.cs</c> now also runs the target
/// when it is the one that was asked for by name, so both <c>dotnet fallout Browsers</c> and
/// <c>dotnet fallout Test --playwright</c> install; the switch is still what turns the install on when
/// <c>Test</c> is the invoked target.
/// </para>
/// <para>
/// <b>The probe is the bundled executable's path, not a launch.</b> <c>Playwright.CreateAsync()</c> only
/// starts the node driver that ships in the package and is therefore always present after a build;
/// <see cref="IBrowserType.ExecutablePath" /> is then where Playwright expects this exact version's
/// Chromium, and <c>File.Exists</c> answers the real question without paying for a browser start. It
/// runs once per process and the answer is cached.
/// </para>
/// </remarks>
public static class BrowserAvailability
{
    /// <summary>The command that installs the browser this suite drives.</summary>
    public const string InstallCommand = "dotnet fallout Browsers";

    /// <summary>The same install without the orchestrator, for a machine that has only the SDK.</summary>
    public const string RawInstallCommand =
        "pwsh -NoProfile -File tests/MartenStudio.Integration.Tests/bin/Debug/net10.0/playwright.ps1 install chromium";

    /// <summary>
    /// Why a browser scenario skipped. It names both things a scenario needs, because one skip reason has
    /// to cover both gates: the scenarios drive a real browser against a real sample host on a real
    /// Postgres, and either one missing is a reason not to run.
    /// </summary>
    public const string SkipReason =
        "The browser suite needs Docker and Playwright's Chromium. Start Docker Desktop (or the daemon) so "
        + "that `docker info` succeeds, and install the browser with `" + InstallCommand + "` (or "
        + "`dotnet fallout Test --playwright`, which installs it and then runs everything). Without the "
        + "orchestrator: `" + RawInstallCommand + "`.";

    private static readonly Lazy<bool> Probe = new(ChromiumIsInstalled, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The decision, as a function of the two facts it is made from.</summary>
    /// <param name="chromiumInstalled">Whether this version's Chromium is on disk.</param>
    /// <param name="onContinuousIntegration">Whether this run is on GitHub Actions.</param>
    /// <remarks>
    /// Separated from the environment so that both branches can be asserted by a test that changes
    /// nothing about the machine it runs on. A gate whose CI branch is only ever exercised on CI is a
    /// gate nobody has checked.
    /// </remarks>
    public static BrowserGate Decide(bool chromiumInstalled, bool onContinuousIntegration) =>
        chromiumInstalled ? BrowserGate.Run
        : onContinuousIntegration ? BrowserGate.Throw
        : BrowserGate.Skip;

    /// <summary>Whether this version's Chromium is on disk.</summary>
    public static bool ChromiumInstalled => Probe.Value;

    /// <summary>
    /// Whether the browser scenarios run at all: both gates, so that a machine without Docker skips for
    /// the reason it actually has rather than failing inside a fixture.
    /// </summary>
    public static bool CanRun =>
        DockerAvailability.IsAvailable
        && Decide(ChromiumInstalled, DockerAvailability.OnContinuousIntegration) is not BrowserGate.Skip;

    /// <summary>Fails loudly when CI has no browser, so that no run can be green without having run.</summary>
    public static void ThrowIfMissingOnContinuousIntegration() =>
        ThrowIfMissing(ChromiumInstalled, DockerAvailability.OnContinuousIntegration);

    /// <summary>
    /// The same rule against two facts rather than against the machine, so the CI branch — the one that
    /// by definition never runs on a developer's box — is assertable without setting a process-wide
    /// environment variable that every other test in the run shares.
    /// </summary>
    /// <param name="chromiumInstalled">Whether this version's Chromium is on disk.</param>
    /// <param name="onContinuousIntegration">Whether this run is on GitHub Actions.</param>
    internal static void ThrowIfMissing(bool chromiumInstalled, bool onContinuousIntegration)
    {
        if (Decide(chromiumInstalled, onContinuousIntegration) is BrowserGate.Throw)
        {
            throw new InvalidOperationException(
                "This run is on GitHub Actions and Playwright's Chromium is not installed, so the browser "
                + "suite cannot start. A skipped browser suite reported as a pass is worse than a failure, "
                + "so this is a failure. CI installs the browser through the build's Browsers target, "
                + "which Test depends on — check that step rather than this one.");
        }
    }

    private static bool ChromiumIsInstalled()
    {
        try
        {
            using IPlaywright playwright = Playwright.CreateAsync().GetAwaiter().GetResult();

            string path = playwright.Chromium.ExecutablePath;

            return path.Length > 0 && File.Exists(path);
        }
        catch (PlaywrightException)
        {
            // The driver itself would not start. Treated as "no browser", which routes to the same skip
            // locally and the same failure on CI.
            return false;
        }
    }
}

/// <summary>
/// A fact that drives a real browser against a real sample host, and skips with a reason when either the
/// Docker daemon or Chromium is missing. Never skips on CI — see <see cref="BrowserAvailability" />.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BrowserFactAttribute : FactAttribute
{
    /// <summary>Wires up xunit's conditional skip against the two probes.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public BrowserFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = BrowserAvailability.SkipReason;
        SkipType = typeof(BrowserAvailability);
        SkipUnless = nameof(BrowserAvailability.CanRun);
    }
}
