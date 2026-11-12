using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KStudio.Integration.Tests.Sample;

/// <summary>
/// The KStudio sample host running on real Kestrel in a child process, started from its own build output with no connection string, so that it
/// starts the throwaway Kafka and Postgres it would for a plain <c>dotnet run</c> (D18).
/// </summary>
/// <remarks>
/// <para>
/// <b>The real sample, as a reader of <c>samples/</c> would start it</b>, the way <c>MartenStudio.Integration.Tests</c> starts its own: a socket, a
/// port of the operating system's choosing, and the child stopped by handle (never by image name).
/// </para>
/// <para>
/// <b>Its containers are this run's own.</b> <c>KSTUDIO_SAMPLE_REUSE</c> is removed from the child's environment, so both containers are fresh and
/// removed with the process; <c>KSTUDIO_PACKET</c>, when set, is inherited and labels them.
/// </para>
/// </remarks>
internal sealed class KStudioSampleProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private static readonly Regex ListeningOn = new(@"Now listening on:\s*(?<url>http://\S+)", RegexOptions.None, TimeSpan.FromSeconds(5));

    private readonly Process process;
    private readonly StringBuilder log = new();
    private readonly Lock logGate = new();
    private readonly TaskCompletionSource<Uri> listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private KStudioSampleProcess(Process process) => this.process = process;

    /// <summary>Where the host listens, with a trailing slash.</summary>
    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1/");

    /// <summary>Everything the host wrote to stdout and stderr.</summary>
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

    /// <summary>The throwaway Kafka's bootstrap servers, as the host's banner printed them.</summary>
    public string KafkaBootstrap
    {
        get
        {
            Match match = Regex.Match(Log, @"throwaway Kafka at (?<bootstrap>\S+?)( and|;)", RegexOptions.None, TimeSpan.FromSeconds(5));
            return match.Success ? match.Groups["bootstrap"].Value : throw new InvalidOperationException("The banner did not name a Kafka. Log:" + Environment.NewLine + Log);
        }
    }

    /// <summary>The throwaway Postgres' connection string, from the banner's <c>host:port</c> and the Testcontainers module's defaults.</summary>
    public string PostgresConnectionString
    {
        get
        {
            Match match = Regex.Match(Log, @"throwaway Postgres at (?<hostport>[^\s;]+);", RegexOptions.None, TimeSpan.FromSeconds(5));

            if (!match.Success)
            {
                throw new InvalidOperationException("The banner did not name a Postgres. Log:" + Environment.NewLine + Log);
            }

            string[] parts = match.Groups["hostport"].Value.Split(':');
            return $"Host={parts[0]};Port={parts[1]};Database=postgres;Username=postgres;Password=postgres";
        }
    }

    /// <summary>Starts the sample with the given extra environment and waits until it answers its landing page.</summary>
    /// <param name="environment">Environment variables to set on the child (a <see langword="null" /> value removes one).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public static async Task<KStudioSampleProcess> StartAsync(IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        KStudioSampleProcess host = new(Process.Start(CreateStartInfo(environment, ["--urls", "http://127.0.0.1:0"]))
            ?? throw new InvalidOperationException("`dotnet` did not start the sample host."));
        host.Attach();

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartTimeout);

            host.BaseAddress = await host.listening.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            await host.WaitForLandingPageAsync(timeout.Token).ConfigureAwait(false);
            return host;
        }
        catch (Exception exception)
        {
            string captured = host.Log;
            await host.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The sample host did not start within " + StartTimeout + ". Its output was:" + Environment.NewLine + captured, exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                // By handle, which is by pid: this is our own child and nothing else is touched.
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

    private static ProcessStartInfo CreateStartInfo(IReadOnlyDictionary<string, string?>? environment, string[] arguments)
    {
        string assembly = SampleAssemblyPath();

        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add(assembly);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Development, because a build output has no static web assets outside it (see the Marten suite's SampleHost).
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["Logging__LogLevel__Default"] = "Information";

        // Nothing configured: the zero-config start, with fresh containers of this run's own.
        startInfo.Environment.Remove("ConnectionStrings__Kafka");
        startInfo.Environment.Remove("ConnectionStrings__Postgres");
        startInfo.Environment.Remove("KSTUDIO_SAMPLE_REUSE");

        foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    private void Attach()
    {
        // Synchronous handlers on purpose (hard rule 6: an async lambda converted to these is an async void).
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
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
        using HttpClient client = new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

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

    private static string SampleAssemblyPath()
    {
        DirectoryInfo output = new(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string targetFramework = output.Name;
        string configuration = output.Parent?.Name ?? "Debug";
        string root = RepositoryRoot();

        string[] candidates =
        [
            Path.Combine(root, "samples", "KStudio.Sample", "bin", configuration, targetFramework, "KStudio.Sample.dll"),
            Path.Combine(root, "artifacts", "bin", "KStudio.Sample", configuration.ToLowerInvariant(), "KStudio.Sample.dll"),
        ];

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The sample host's build output was not found. Build the solution first. Probed: " + string.Join(", ", candidates));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "marten-studio.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("marten-studio.slnx was not found above " + AppContext.BaseDirectory);
    }
}
