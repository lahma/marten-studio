using System.ComponentModel;
using System.Diagnostics;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// Whether there is a Docker daemon to run containers on — and what to do when there is not.
/// </summary>
/// <remarks>
/// <para>
/// The rule is asymmetric on purpose, and it is the same one D19 states for Playwright browsers.
/// <b>Locally</b>, a developer without Docker running gets a skip whose reason says exactly that, because
/// failing their whole run for a tool they may not have started is hostile. <b>On CI</b>, a missing Docker
/// daemon is a build failure, because a suite that quietly did not run and reported green is worse than
/// one that failed: AGENTS.md says so in as many words.
/// </para>
/// <para>
/// The probe is <c>docker info</c> in a child process rather than a Docker API call: it answers the exact
/// question ("is there a daemon that will talk to me"), it needs no package, and it cannot be fooled by a
/// CLI that is installed while the engine is stopped. It runs once per process and the answer is cached.
/// </para>
/// </remarks>
public static class DockerAvailability
{
    /// <summary>The skip reason, which names the thing to do about it.</summary>
    public const string SkipReason =
        "Docker is not available. Start Docker Desktop (or the daemon) so that `docker info` succeeds, then " +
        "run `dotnet test tests/MartenStudio.Integration.Tests` again.";

    private static readonly Lazy<bool> Probe = new(DockerInfoSucceeds, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this run is on GitHub Actions, where a missing daemon must fail rather than skip.</summary>
    public static bool OnContinuousIntegration =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the Docker-backed tests should run. Always <see langword="true"/> on CI: there, a missing
    /// daemon is <see cref="ThrowIfMissingOnContinuousIntegration"/>'s problem, not a reason to skip.
    /// </summary>
    public static bool IsAvailable => OnContinuousIntegration || Probe.Value;

    /// <summary>Fails loudly when CI has no Docker, so that no run can be green without having run.</summary>
    public static void ThrowIfMissingOnContinuousIntegration()
    {
        if (OnContinuousIntegration && !Probe.Value)
        {
            throw new InvalidOperationException(
                "This run is on GitHub Actions and `docker info` failed, so the integration suite cannot " +
                "start its Postgres container. A skipped Docker suite reported as a pass is worse than a " +
                "failure, so this is a failure.");
        }
    }

    private static bool DockerInfoSucceeds()
    {
        var startInfo = new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                // Our own child, stopped by handle. Never by image name: this machine runs several agent
                // sessions at once and an image-wide sweep would kill somebody else's container work.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited between the timeout and the kill.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // There is no `docker` on PATH at all.
            return false;
        }
    }
}
