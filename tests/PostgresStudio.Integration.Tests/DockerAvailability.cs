using System.ComponentModel;
using System.Diagnostics;

namespace PostgresStudio.Integration.Tests;

/// <summary>
/// Whether there is a Docker daemon to run containers on - and what to do when there is not. A copy of
/// <c>MartenStudio.Integration.Tests</c>' rule, which is D19's applied to Docker.
/// </summary>
/// <remarks>
/// <b>Locally</b>, no Docker is a skip whose reason says so; <b>on CI</b> it is a failure, because a suite that quietly
/// did not run and reported green is worse than one that failed. The probe is <c>docker info</c> in a child process,
/// once per process.
/// </remarks>
public static class DockerAvailability
{
    /// <summary>The skip reason, which names the thing to do about it.</summary>
    public const string SkipReason =
        "Docker is not available. Start Docker Desktop (or the daemon) so that `docker info` succeeds, then " +
        "run `dotnet test tests/PostgresStudio.Integration.Tests` again.";

    private static readonly Lazy<bool> Probe = new(DockerInfoSucceeds, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this run is on GitHub Actions, where a missing daemon must fail rather than skip.</summary>
    public static bool OnContinuousIntegration =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the Docker-backed tests should run: always on CI, where a missing daemon is a failure instead.</summary>
    public static bool IsAvailable => OnContinuousIntegration || Probe.Value;

    /// <summary>Fails loudly when CI has no Docker, so that no run can be green without having run.</summary>
    public static void ThrowIfMissingOnContinuousIntegration()
    {
        if (OnContinuousIntegration && !Probe.Value)
        {
            throw new InvalidOperationException(
                "This run is on GitHub Actions and `docker info` failed, so PostgresStudio.Integration.Tests cannot start " +
                "its Postgres container. A skipped Docker suite reported as a pass is worse than a failure, so this is a " +
                "failure.");
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

            // Drained, so a chatty daemon cannot fill a pipe and hang the probe.
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                // Our own child, stopped by handle - never by image name.
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
