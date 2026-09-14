using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Npgsql;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// The sample host, running on real Kestrel in a child process, pointed at the assembly's Postgres.
/// </summary>
/// <remarks>
/// <para>
/// <b>A socket, not a <c>TestServer</c>.</b> Everything else in this repository exercises the studio
/// in-process; a Blazor circuit cannot be. <c>TestServer</c> has no listening socket, so there is nothing
/// for a browser to connect to and nothing for SignalR to negotiate over — and the failures this suite
/// exists to catch (the circuit never attaches, <c>blazor.web.js</c> 404s under a sub-path mount, a
/// click never reaches the server) are all on the far side of that socket.
/// </para>
/// <para>
/// <b>The real sample, not a hand-built host.</b> It is started from its own build output with its own
/// switches, so what the browser drives is the application a reader of <c>samples/</c> would start, and a
/// change to the sample's wiring shows up here rather than in a copy of it that has drifted.
/// </para>
/// <para>
/// <b>The connection string is passed in.</b> Without one the sample starts a throwaway Postgres of its
/// own (D18); a suite that let it do that would start one container per host and never stop paying for
/// it. <c>ConnectionStrings__Marten</c> is how a host configuration reaches it, which is also the shape
/// a real deployment uses.
/// </para>
/// <para>
/// <b>A port of the operating system's choosing.</b> <c>--urls http://127.0.0.1:0</c> lets Kestrel bind
/// whatever is free and log it; the alternative — picking a port, closing the listener and hoping — is a
/// race that fails once a fortnight on a machine running several agent sessions at once.
/// </para>
/// <para>
/// <b>It is stopped by handle, never by image name.</b> <see cref="DisposeAsync" /> kills this object's
/// own <see cref="Process" /> (and the tree under it, for the launcher shape where <c>dotnet</c> forks),
/// which is the only process this class has any claim on.
/// </para>
/// </remarks>
internal sealed class SampleHost : IAsyncDisposable
{
    /// <summary>How long the host gets to bind a socket and answer its landing page.</summary>
    /// <remarks>
    /// Generous because the first start against an empty database is Marten building every table,
    /// function and index the sample domain declares and then running the seeder, on a container that
    /// may itself have started seconds ago.
    /// </remarks>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private static readonly Regex ListeningOn = new(
        @"Now listening on:\s*(?<url>http://\S+)",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private readonly Process process;
    private readonly StringBuilder log = new();
    private readonly Lock logGate = new();
    private readonly TaskCompletionSource<Uri> listening =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Uri? baseAddress;

    private SampleHost(Process process, string studioPath)
    {
        this.process = process;
        StudioPath = studioPath;
    }

    /// <summary>Where the host is listening, as an absolute URI with a trailing slash.</summary>
    public Uri BaseAddress => baseAddress
        ?? throw new InvalidOperationException("The sample host has not reported a listening address yet.");

    /// <summary>The path the studio is mounted at, with a leading slash and no trailing one.</summary>
    public string StudioPath { get; }

    /// <summary>The child process id, so a failure can say which process it was talking about.</summary>
    public int ProcessId => process.Id;

    /// <summary>Everything the host has written to stdout and stderr, for a failure message.</summary>
    public string Log
    {
        get
        {
            lock (logGate)
            {
                return log.ToString();
            }
        }
    }

    /// <summary>
    /// The tail of <see cref="Log" />, which is the part a failure is about.
    /// </summary>
    /// <param name="lines">How many lines to keep.</param>
    /// <remarks>
    /// The whole log of a host that has served a Blazor page is thousands of lines of request logging,
    /// and a test failure that prints all of it buries the assertion that failed.
    /// </remarks>
    public string LogTail(int lines = 60)
    {
        string[] all = Log.Split('\n');

        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>An absolute URL under this host.</summary>
    /// <param name="relative">A root-relative path, with or without a leading slash.</param>
    public string Url(string relative) => new Uri(BaseAddress, relative.TrimStart('/')).AbsoluteUri;

    /// <summary>An absolute URL under the studio's mount path.</summary>
    /// <param name="relative">A studio-relative path such as <c>documents</c>, or empty for the root.</param>
    public string StudioUrl(string relative = "") =>
        Url(relative.Length == 0 ? StudioPath : StudioPath + "/" + relative.TrimStart('/'));

    /// <summary>
    /// Starts the sample and waits until it answers.
    /// </summary>
    /// <param name="connectionString">The Postgres the sample store is pointed at.</param>
    /// <param name="studioPath"><c>--path</c>, or <see langword="null" /> for the default <c>/marten</c>.</param>
    /// <param name="allowDataGeneration">Whether to map the demo-data endpoints outside Development.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static async Task<SampleHost> StartAsync(
        string connectionString,
        string? studioPath = null,
        bool allowDataGeneration = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        string assembly = SampleAssemblyPath();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:0");

        if (studioPath is { Length: > 0 })
        {
            // --path is the one switch that really is a --key value pair; the boolean ones below are
            // bare switches and consume nothing (see SampleOptions).
            startInfo.ArgumentList.Add("--path");
            startInfo.ArgumentList.Add(studioPath);
        }

        if (allowDataGeneration)
        {
            startInfo.ArgumentList.Add("--allow-data-generation");
        }

        // Explicit rather than inherited: a suite whose behaviour depends on what was exported in the
        // shell that started it is not a suite.
        //
        // Development, and it has to be. This host is started from `bin/`, not from a publish, and
        // ASP.NET Core only calls StaticWebAssetsLoader.UseStaticWebAssets when the environment is
        // Development - outside it, a non-published application has no static web assets at all. Measured
        // here before it was changed: with ASPNETCORE_ENVIRONMENT=Production the host logged "The
        // application is not running against the published output and Static Web Assets are not enabled",
        // `GET {path}/_content/MartenStudio/css/marten-studio.css` answered 404 and
        // `GET {path}/_framework/blazor.web.js` answered 200 with an empty body. That is a fact about
        // running a build output rather than a publish, not about the studio, and pretending otherwise
        // would make this suite assert the wrong thing.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__Marten"] = connectionString;
        startInfo.Environment["Logging__LogLevel__Default"] = "Information";

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("`dotnet` did not start the sample host.");

        var host = new SampleHost(process, Normalize(studioPath));

        // Synchronous handlers on purpose. An `async` lambda converted to a DataReceivedEventHandler is
        // an `async void` with different spelling, and AGENTS.md hard rule 6 forbids it in every shape.
        process.OutputDataReceived += (_, e) => host.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => host.OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartTimeout);

            host.baseAddress = await host.listening.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            await host.WaitForLandingPageAsync(timeout.Token).ConfigureAwait(false);

            return host;
        }
        catch (Exception exception)
        {
            string captured = host.Log;
            await host.DisposeAsync().ConfigureAwait(false);

            throw new InvalidOperationException(
                "The sample host did not start within " + StartTimeout + ". Its output was:"
                + Environment.NewLine + captured,
                exception);
        }
    }

    /// <summary>
    /// Waits until the sample's seeder has written its documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marten's <c>InitializeWith</c> runs from a hosted service, and so does Kestrel, so the landing
    /// page can answer before the store has any rows in it. A scenario that asserted "the grid has rows"
    /// against a store that was still being seeded would fail about one run in ten, which is the worst
    /// kind of test. Wait on the condition, never on a bare <c>Task.Delay</c>.
    /// </para>
    /// <para>
    /// <b>Both halves, documents and events.</b> The seeder writes its documents before it appends its
    /// streams, and the Overview's tiles read "unknown" rather than a number for a database whose event
    /// tables do not exist yet — which is correct behaviour and was a real intermittent failure here
    /// until this waited for the second half too. It matters more than it looks: the studio caches a
    /// table's physical columns per database for the life of the process, so a page opened before the
    /// event store existed can keep answering "no event storage" long after it does.
    /// </para>
    /// </remarks>
    /// <param name="connectionString">The Postgres the sample store is pointed at.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static async Task WaitForSeedAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartTimeout);

        string[] tables = ["studio_sample.mt_doc_customer", "studio_sample_events.mt_events"];

        foreach (string table in tables)
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();

                try
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync(timeout.Token).ConfigureAwait(false);

                    await using var command = new NpgsqlCommand("select count(*) from " + table, connection);

                    object? count = await command.ExecuteScalarAsync(timeout.Token).ConfigureAwait(false);

                    if (Convert.ToInt64(count, CultureInfo.InvariantCulture) > 0)
                    {
                        break;
                    }
                }
                catch (PostgresException exception) when (
                    exception.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
                {
                    // Marten has not created the schema or the table yet.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                // By handle, which is by pid: this is our own child and nothing else is touched. Never by
                // image name — this machine runs several agent sessions at once and an image-wide sweep
                // would take somebody else's host with it.
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnLine(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (logGate)
        {
            log.AppendLine(line);
        }

        Match match = ListeningOn.Match(line);
        if (match.Success
            && Uri.TryCreate(match.Groups["url"].Value.TrimEnd('/') + "/", UriKind.Absolute, out Uri? address))
        {
            listening.TrySetResult(address);
        }
    }

    private async Task WaitForLandingPageAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using HttpResponseMessage response = await client
                    .GetAsync(new Uri("/", UriKind.Relative), cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The socket is bound but the pipeline is not answering yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Normalize(string? studioPath) =>
        studioPath is { Length: > 0 } path ? "/" + path.Trim('/') : "/marten";

    /// <summary>
    /// The sample's own build output.
    /// </summary>
    /// <remarks>
    /// The project reference copies <c>MartenStudio.Sample.dll</c> into <em>this</em> project's output,
    /// but not the <c>.runtimeconfig.json</c> that tells the host which framework to load, so the copy
    /// next door cannot be executed. The sample's own <c>bin</c> is where a runnable one is, and it is
    /// laid out with the same configuration and target-framework folders this assembly is running from.
    /// Both candidates are probed and the failure names them, because a path guessed wrong three layers
    /// down produces "the host did not start" and nothing else.
    /// </remarks>
    private static string SampleAssemblyPath()
    {
        var output = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string targetFramework = output.Name;
        string configuration = output.Parent?.Name ?? "Debug";
        string root = RepositoryRoot();

        string[] candidates =
        [
            Path.Combine(root, "samples", "MartenStudio.Sample", "bin", configuration, targetFramework, "MartenStudio.Sample.dll"),
            Path.Combine(root, "artifacts", "bin", "MartenStudio.Sample", configuration.ToLowerInvariant(), "MartenStudio.Sample.dll"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "The sample host's build output was not found. Build the solution first. Probed: "
            + string.Join(", ", candidates));
    }

    /// <summary>The repository root, found by walking up from this assembly to the solution file.</summary>
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "marten-studio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "marten-studio.slnx was not found above " + AppContext.BaseDirectory + ", so the repository "
            + "root could not be located.");
    }
}
