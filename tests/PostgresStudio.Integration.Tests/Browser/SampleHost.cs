using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// <c>samples/PostgresStudio.Sample</c>, running on real Kestrel in a child process - a copy of
/// <c>MartenStudio.Integration.Tests</c>' <c>SampleHost</c>, for the one sample this suite starts.
/// </summary>
/// <remarks>
/// <para>
/// <b>A socket, not a <c>TestServer</c>.</b> A Blazor circuit needs something for a browser to connect to and for SignalR
/// to negotiate over, and the failures the browser suite exists for are on the far side of that socket.
/// </para>
/// <para>
/// <b>The real sample, from its own build output</b>, so what the browser drives is the application a reader of
/// <c>samples/</c> would start. A connection string is passed in through <c>ConnectionStrings__Postgres</c>, the shape a
/// real deployment uses; without one the sample starts a throwaway Postgres of its own (D18), and a fresh one -
/// <see cref="PostgresFixture.ReuseEnvironmentVariable" /> is set to <c>false</c> for the child, so it can never attach
/// to a container of this suite's.
/// </para>
/// <para>
/// <b>A port of the operating system's choosing</b> (<c>--urls http://127.0.0.1:0</c>), and <b>stopped by handle</b>,
/// never by image name: <see cref="DisposeAsync" /> kills this object's own <see cref="Process" /> and the tree under it.
/// </para>
/// </remarks>
internal sealed class SampleHost : IAsyncDisposable
{
    /// <summary>The sample's project folder and assembly name under <c>samples/</c>.</summary>
    public const string Project = "PostgresStudio.Sample";

    /// <summary>Where the sample mounts the studio.</summary>
    public const string DefaultStudioPath = "/postgres";

    /// <summary>The <c>ConnectionStrings:</c> key the sample reads its admin database from.</summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>How long the host gets to bind a socket and answer its landing page: a cold container, seeding included.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private static readonly Regex ListeningOn = new(@"Now listening on:\s*(?<url>http://\S+)", RegexOptions.None, TimeSpan.FromSeconds(5));

    private readonly Process process;
    private readonly StringBuilder log = new();
    private readonly Lock logGate = new();
    private readonly TaskCompletionSource<Uri> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Uri? baseAddress;

    private SampleHost(Process process) => this.process = process;

    /// <summary>Where the host is listening, as an absolute URI with a trailing slash.</summary>
    public Uri BaseAddress => baseAddress ?? throw new InvalidOperationException("The sample host has not reported a listening address yet.");

    /// <summary>The path the studio is mounted at, with a leading slash and no trailing one.</summary>
    public string StudioPath { get; } = DefaultStudioPath;

    /// <summary>The child process id, so a failure can say which process it was talking about.</summary>
    public int ProcessId => process.Id;

    /// <summary>Everything the host has written to stdout and stderr.</summary>
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

    /// <summary>The tail of <see cref="Log" />, which is the part a failure is about.</summary>
    /// <param name="lines">How many lines to keep.</param>
    public string LogTail(int lines = 60)
    {
        string[] all = Log.Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>An absolute URL under this host.</summary>
    /// <param name="relative">A root-relative path, with or without a leading slash.</param>
    public string Url(string relative) => new Uri(BaseAddress, relative.TrimStart('/')).AbsoluteUri;

    /// <summary>An absolute URL under the studio's mount path.</summary>
    /// <param name="relative">A studio-relative path such as <c>database</c>, or empty for the root.</param>
    public string StudioUrl(string relative = "") =>
        Url(relative.Length == 0 ? StudioPath : StudioPath + "/" + relative.TrimStart('/'));

    /// <summary>Starts the sample and waits until it answers its landing page.</summary>
    /// <param name="connectionString">
    /// The admin database, or <see langword="null" /> for none - the zero-config <c>dotnet run</c>, in which the sample
    /// starts a fresh throwaway Postgres of its own (D18).
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static async Task<SampleHost> StartAsync(string? connectionString, CancellationToken cancellationToken = default)
    {
        Process process = Process.Start(CreateStartInfo(connectionString))
            ?? throw new InvalidOperationException("`dotnet` did not start the sample host.");

        var host = new SampleHost(process);

        // Synchronous handlers on purpose: an `async` lambda converted to a DataReceivedEventHandler is an `async void`
        // with different spelling (AGENTS.md hard rule 6).
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
                "The sample host did not start within " + StartTimeout + ". Its output was:" + Environment.NewLine + captured,
                exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                // By handle, which is by pid: our own child and nothing else.
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
        if (match.Success && Uri.TryCreate(match.Groups["url"].Value.TrimEnd('/') + "/", UriKind.Absolute, out Uri? address))
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
                using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative), cancellationToken).ConfigureAwait(false);

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

    private static ProcessStartInfo CreateStartInfo(string? connectionString)
    {
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

        // Development, and it has to be: a build output (not a publish) has static web assets only in Development - see
        // MartenStudio.Integration.Tests' SampleHost, which measured it.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["Logging__LogLevel__Default"] = "Information";

        // Never a reused container, whatever the shell exported: a zero-config start gets a fresh one of its own.
        startInfo.Environment[PostgresFixture.ReuseEnvironmentVariable] = "false";

        string variable = "ConnectionStrings__" + ConnectionStringName;

        if (connectionString is { Length: > 0 })
        {
            startInfo.Environment[variable] = connectionString;
        }
        else
        {
            startInfo.Environment.Remove(variable);
        }

        return startInfo;
    }

    /// <summary>
    /// The sample's own build output: the project reference copies its dll here but not the <c>.runtimeconfig.json</c>
    /// that makes it runnable, so the sample's own <c>bin</c> is used, laid out like this assembly's.
    /// </summary>
    private static string SampleAssemblyPath()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string targetFramework = output.Name;
        string configuration = output.Parent?.Name ?? "Debug";
        string root = RepositoryRoot();
        string file = Project + ".dll";

        string[] candidates =
        [
            Path.Combine(root, "samples", Project, "bin", configuration, targetFramework, file),
            Path.Combine(root, "artifacts", "bin", Project, configuration.ToLowerInvariant(), file),
        ];

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "The sample host's build output was not found. Build the solution first. Probed: " + string.Join(", ", candidates));
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

        throw new DirectoryNotFoundException("marten-studio.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
