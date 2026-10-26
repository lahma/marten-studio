using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

using Npgsql;

namespace PostgresStudio.Tests.Support;

/// <summary>
/// A throwaway <c>postgres:17-alpine</c> of this run's own, for the few live classes in this otherwise Docker-free
/// suite - started with the <c>docker</c> command line, because this project references no Testcontainers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a live class is here at all.</b> The Npgsql pass (<c>build/Build.Npgsql.cs</c>) restores, builds and tests
/// <c>postgres-studio.slnf</c> at the latest-tested Npgsql, and until <c>tests/PostgresStudio.Integration.Tests</c>
/// exists this is the only test project in that filter: a live test that has to run against Npgsql 10 runs here, or
/// nowhere. When that project arrives, the classes that use this move onto its own fixture and this file goes.
/// </para>
/// <para>
/// <b>Never reused, and gone on its own.</b> The container is this fixture's alone and is removed with it. One a killed
/// run leaves behind stops itself after <see cref="Lifetime" /> - busybox <c>timeout</c> runs in front of the image's own
/// entrypoint - and <c>--rm</c> removes it then, so nothing outlives a crashed run for long. It publishes on
/// <c>127.0.0.1</c> alone, with a password of this run's own, and it is stopped by its id - never by image name.
/// </para>
/// <para>
/// <b>D19's rule.</b> Locally, no Docker is a skip whose reason says so (<see cref="DockerFactAttribute" />); on CI
/// (<c>GITHUB_ACTIONS=true</c>) it is a failure, because a live class reported green without having run is worse than a
/// red one. The probe is <c>docker info</c>, as <c>MartenStudio.Integration.Tests</c>' <c>DockerAvailability</c> runs it.
/// </para>
/// </remarks>
public sealed class DockerPostgres : IAsyncLifetime
{
    /// <summary>The image: the one every other live suite here runs against.</summary>
    public const string Image = "postgres:17-alpine";

    /// <summary>What a skipped live test says.</summary>
    public const string SkipReason =
        "Docker is not available. Start Docker Desktop (or the daemon) so that `docker info` succeeds, then run " +
        "`dotnet test tests/PostgresStudio.Tests` again.";

    /// <summary>How long a container may outlive the run that started it, should that run be killed.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    private static readonly Lazy<bool> Probe = new(DockerInfoSucceeds, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly string password = Guid.NewGuid().ToString("N");
    private string? containerId;
    private string? connectionString;

    /// <summary>Whether this run is on GitHub Actions, where a missing daemon fails rather than skips.</summary>
    public static bool OnContinuousIntegration =>
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the live tests run: always on CI, where a missing daemon is <see cref="InitializeAsync" />'s failure
    /// rather than a reason to skip.
    /// </summary>
    public static bool IsAvailable => OnContinuousIntegration || Probe.Value;

    /// <summary>The superuser's connection string to the container's <c>postgres</c> database, once it accepts one.</summary>
    public string ConnectionString => connectionString ?? throw new InvalidOperationException(
        "The Postgres container is not running. A test that needs it must be a [DockerFact], so that it skips rather than " +
        "fails when Docker is absent.");

    /// <summary>Starts the container and waits until Postgres accepts a connection over TCP.</summary>
    public async ValueTask InitializeAsync()
    {
        if (OnContinuousIntegration && !Probe.Value)
        {
            throw new InvalidOperationException(
                "This run is on GitHub Actions and `docker info` failed, so the live classes of PostgresStudio.Tests cannot " +
                "start their Postgres container. A skipped live class reported as a pass is worse than a failure.");
        }

        if (!IsAvailable)
        {
            return;
        }

        CancellationToken token = TestContext.Current.CancellationToken;

        (int started, string id, string startError) = await DockerAsync(
            TimeSpan.FromMinutes(5),
            token,
            "run", "--detach", "--rm",
            "--label", "marten-studio.tests=PostgresStudio.Tests",
            "--env", "POSTGRES_PASSWORD=" + password,
            "--publish", "127.0.0.1::5432",
            "--entrypoint", "timeout",
            Image,
            "-s", "INT", ((int) Lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            "docker-entrypoint.sh", "postgres");

        if (started != 0 || id.Length == 0)
        {
            throw new InvalidOperationException("`docker run " + Image + "` failed (exit " + started + "): " + startError);
        }

        containerId = id;

        try
        {
            (int mapped, string port, string portError) = await DockerAsync(TimeSpan.FromSeconds(30), token, "port", id, "5432/tcp");

            // "127.0.0.1:49153", one line per binding: the first is ours, the only one asked for.
            string first = port.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

            if (mapped != 0 || !int.TryParse(first[(first.LastIndexOf(':') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int hostPort))
            {
                throw new InvalidOperationException("`docker port " + id + " 5432/tcp` said '" + port + "' (exit " + mapped + "): " + portError);
            }

            string candidate = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = hostPort,
                Username = "postgres",
                Password = password,
                Database = "postgres",
            }.ConnectionString;

            await WaitUntilAcceptingAsync(candidate, id, token);
            connectionString = candidate;
        }
        catch
        {
            // A fixture whose start failed is not always disposed: the container goes now rather than after Lifetime.
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>Removes the container, by its id.</summary>
    public async ValueTask DisposeAsync()
    {
        if (containerId is { } id)
        {
            containerId = null;
            await DockerAsync(TimeSpan.FromMinutes(1), CancellationToken.None, "rm", "--force", "--volumes", id);
        }
    }

    /// <summary>
    /// Asks every quarter of a second, until Postgres answers over TCP: the image's first-run initialisation listens on
    /// its Unix socket alone, so the first connection that succeeds is the real server's. Never a fixed delay.
    /// </summary>
    private static async Task WaitUntilAcceptingAsync(string candidate, string id, CancellationToken token)
    {
        string probe = new NpgsqlConnectionStringBuilder(candidate) { Pooling = false, Timeout = 3 }.ConnectionString;

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));

        Exception? last = null;

        while (true)
        {
            try
            {
                await using NpgsqlConnection connection = new(probe);
                await connection.OpenAsync(deadline.Token);
                return;
            }
            catch (Exception exception) when (
                exception is NpgsqlException or IOException or TimeoutException or OperationCanceledException
                && !token.IsCancellationRequested)
            {
                // Refused, reset or not yet ready; or the deadline, which the tick below turns into the failure.
                last = exception;
            }

            try
            {
                await timer.WaitForNextTickAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                (_, string logs, _) = await DockerAsync(TimeSpan.FromSeconds(30), CancellationToken.None, "logs", "--tail", "40", id);

                throw new TimeoutException(
                    "Postgres in container " + id + " did not accept a connection within two minutes. Last error: "
                    + last?.Message + Environment.NewLine + "Its log:" + Environment.NewLine + logs);
            }
        }
    }

    /// <summary>
    /// One <c>docker</c> command, its arguments passed as a list (never through a shell), its output read whole. A command
    /// that outlives <paramref name="timeout" /> is stopped by its own handle.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> DockerAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        ProcessStartInfo start = new("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("`docker` did not start.");

        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and the kill.
            }

            throw new TimeoutException("`docker " + arguments[0] + "` did not finish within " + timeout + ".");
        }

        return (process.ExitCode, (await output).Trim(), (await error).Trim());
    }

    private static bool DockerInfoSucceeds()
    {
        ProcessStartInfo start = new("docker", "info --format {{.ServerVersion}}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using Process? process = Process.Start(start);

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
            // No `docker` on PATH at all.
            return false;
        }
    }
}

/// <summary>A fact that needs <see cref="DockerPostgres" />: it skips with a reason without Docker, and never on CI.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    /// <summary>Wires xunit's conditional skip to the Docker probe.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public DockerFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = DockerPostgres.SkipReason;
        SkipType = typeof(DockerPostgres);
        SkipUnless = nameof(DockerPostgres.IsAvailable);
    }
}
