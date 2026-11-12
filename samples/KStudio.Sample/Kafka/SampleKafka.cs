using System.Diagnostics;

using Confluent.Kafka;

using Testcontainers.Kafka;

namespace KStudio.Sample.Kafka;

/// <summary>
/// The Kafka the sample reads (AGENTS.md D18): the one <c>ConnectionStrings:Kafka</c> names, or a throwaway <c>apache/kafka:4.4.0</c> started
/// through Testcontainers when nothing configured one, and never a fall back to <c>localhost:9092</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>No silent fallback.</b> Either a bootstrap was configured, or a container was started, or the host stops with
/// <see cref="NoDockerMessage" />. Connecting to a cluster nobody asked for is how a demo writes into something that mattered.
/// </para>
/// <para>
/// <b>Reuse is opt-in</b> (<c>KSTUDIO_SAMPLE_REUSE=true</c>): the container is fresh each run unless the variable asks for it, and then it is
/// the container labelled <c>kstudio.sample.reuse=kstudio-sample-kafka</c>, which no other container of this repository carries.
/// </para>
/// </remarks>
internal sealed class SampleKafka : IAsyncDisposable
{
    /// <summary>The configuration key that names a cluster of the host's own.</summary>
    public const string ConnectionStringName = "Kafka";

    /// <summary>The environment variable that asks for the throwaway containers to be reused across runs.</summary>
    public const string ReuseEnvironmentVariable = "KSTUDIO_SAMPLE_REUSE";

    /// <summary>The environment variable an agent packet sets so its containers carry <c>kstudio.packet</c>.</summary>
    public const string PacketEnvironmentVariable = "KSTUDIO_PACKET";

    /// <summary>The image of the throwaway broker.</summary>
    public const string Image = "apache/kafka:4.4.0";

    /// <summary>The label every container the sample starts carries.</summary>
    public const string SampleLabel = "kstudio.sample";

    /// <summary>The label that gives a reused broker its identity.</summary>
    public const string ReuseLabel = "kstudio.sample.reuse";

    /// <summary>The label's value.</summary>
    public const string ReuseLabelValue = "kstudio-sample-kafka";

    /// <summary>The sentence the host stops with when it needs a throwaway Kafka and Docker is not there.</summary>
    public const string NoDockerMessage =
        "KStudio sample: no ConnectionStrings:Kafka is configured and Docker is not available to start a throwaway Kafka. Start Docker, or set ConnectionStrings:Kafka to a cluster you own (the sample creates and seeds topics there only with --allow-data-generation).";

    private readonly KafkaContainer? container;
    private readonly bool reused;
    private string[] volumes = [];

    private SampleKafka(string bootstrapServers, KafkaContainer? container, bool reused)
    {
        BootstrapServers = bootstrapServers;
        this.container = container;
        this.reused = reused;
    }

    /// <summary>The bootstrap servers the sample reads and seeds.</summary>
    public string BootstrapServers { get; }

    /// <summary>Whether the sample started this broker (as opposed to the host configuring one).</summary>
    public bool IsThrowaway => container is not null;

    /// <summary>Starts the broker, or takes the configured one.</summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="start">How the throwaway container is started; a test injects one that fails, to prove the refusal path. <see langword="null" /> starts it.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <exception cref="InvalidOperationException">Nothing is configured and Docker did not start a broker (<see cref="NoDockerMessage" />).</exception>
    public static async Task<SampleKafka> StartAsync(
        IConfiguration configuration,
        Func<KafkaContainer, CancellationToken, Task>? start = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetConnectionString(ConnectionStringName) is { Length: > 0 } configured && !string.IsNullOrWhiteSpace(configured))
        {
            return new SampleKafka(configured.Trim(), null, reused: false);
        }

        bool reuse = ReuseRequested();
        KafkaContainer container = CreateContainer(reuse, Environment.GetEnvironmentVariable(PacketEnvironmentVariable));

        try
        {
            await (start ?? StartContainerAsync)(container, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(NoDockerMessage, exception);
        }

        SampleKafka started = new($"{container.Hostname}:{container.GetMappedPublicPort(KafkaBuilder.KafkaPort)}", container, reuse);
        started.volumes = await DockerVolumes.OfAsync(container.Id).ConfigureAwait(false);
        return started;
    }

    private static Task StartContainerAsync(KafkaContainer container, CancellationToken cancellationToken) => container.StartAsync(cancellationToken);

    /// <summary>The throwaway broker, built and not started.</summary>
    /// <param name="reuse">Whether to ask for reuse.</param>
    /// <param name="packet">The agent packet for the <c>kstudio.packet</c> label, or <see langword="null" /> for none.</param>
    internal static KafkaContainer CreateContainer(bool reuse, string? packet)
    {
        KafkaBuilder builder = new KafkaBuilder(Image)

            // Testcontainers 4.15.0's startup script ends KAFKA_ADVERTISED_LISTENERS with a comma when the container has no additional
            // advertised listener, and Kafka 4.4 refuses an empty entry ("advertised.listeners values must not be empty"). One more
            // listener fixes it. It is bound to the container's own loopback, so nothing outside the container can reach it. (The same
            // workaround KStudio's live suite carries, KafkaFixture.)
            .WithListener("localhost:9095")
            .WithLabel(SampleLabel, "true");

        if (reuse)
        {
            builder = builder.WithLabel(ReuseLabel, ReuseLabelValue);
        }

        if (!string.IsNullOrWhiteSpace(packet))
        {
            builder = builder.WithLabel("kstudio.packet", packet);
        }

        return builder.WithReuse(reuse).Build();
    }

    /// <summary>Whether <c>KSTUDIO_SAMPLE_REUSE</c> asks for reuse: only a literal <c>true</c> does.</summary>
    /// <param name="value">The variable's value.</param>
    internal static bool IsReuseRequested(string? value) => string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The loud banner (D-SHELL 11), logged at Warning on start.</summary>
    /// <param name="kafkaBootstrap">The throwaway Kafka's bootstrap servers, or <see langword="null" /> when the host configured its own.</param>
    /// <param name="postgresHost">The throwaway Postgres' <c>host:port</c>, or <see langword="null" /> when the host configured its own.</param>
    /// <param name="reused">Whether <c>KSTUDIO_SAMPLE_REUSE=true</c> asked for reuse: the containers are then kept for the next run, and the banner says so.</param>
    /// <returns>The sentence, or <see langword="null" /> when neither is a container.</returns>
    public static string? Banner(string? kafkaBootstrap, string? postgresHost, bool reused = false)
    {
        if (kafkaBootstrap is null && postgresHost is null)
        {
            return null;
        }

        string what = (kafkaBootstrap, postgresHost) switch
        {
            ({ } kafka, { } postgres) => $"a throwaway Kafka at {kafka} and a throwaway Postgres at {postgres}; both are",
            ({ } kafka, null) => $"a throwaway Kafka at {kafka}; it is",
            (null, { } postgres) => $"a throwaway Postgres at {postgres}; it is",
            _ => string.Empty,
        };

        string configure = (kafkaBootstrap, postgresHost) switch
        {
            ({ }, { }) => "Set ConnectionStrings:Kafka and ConnectionStrings:Postgres to use your own.",
            ({ }, null) => "Set ConnectionStrings:Kafka to use your own.",
            _ => "Set ConnectionStrings:Postgres to use your own.",
        };

        string fate = reused
            ? $"kept for the next run ({ReuseEnvironmentVariable}=true); docker rm -f removes a container when you are done"
            : "deleted when this process stops";

        return $"KStudio sample: started {what} {fate}. {configure}";
    }

    /// <summary>
    /// Judges the cluster, and seeds it when the judge and the host allow it: a throwaway broker always, a configured cluster only with
    /// <c>--allow-data-generation</c>.
    /// </summary>
    /// <param name="mayConfiguredBeSeeded">Whether <c>--allow-data-generation</c> was given.</param>
    /// <param name="output">Where progress is written.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns><see langword="null" /> when the host may start; otherwise the one line to stop with.</returns>
    public async Task<string?> PrepareAsync(bool mayConfiguredBeSeeded, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        ClientConfig config = new() { BootstrapServers = BootstrapServers };

        string[] topics;
        using (IAdminClient admin = new AdminClientBuilder(config).Build())
        {
            topics = [.. admin.GetMetadata(TimeSpan.FromSeconds(30)).Topics.Select(static x => x.Topic)];
        }

        SampleKafkaJudgement judgement = SampleKafkaJudge.Judge(topics);

        if (judgement.Verdict == SampleKafkaVerdict.Refuse)
        {
            return judgement.Message;
        }

        if (judgement.Verdict == SampleKafkaVerdict.AlreadySeeded)
        {
            await output.WriteLineAsync("KStudio sample: this Kafka was seeded by an earlier run; leaving it as it is.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!IsThrowaway && !mayConfiguredBeSeeded)
        {
            await output.WriteLineAsync("KStudio sample: the configured Kafka is not seeded without --allow-data-generation; browsing what is there.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return null;
        }

        KafkaSeedPlan plan = KafkaSeedPlan.Create(DateTimeOffset.UtcNow);
        await output.WriteLineAsync($"KStudio sample: seeding {plan.Records.Count:N0} records into {plan.Topics.Count} topics...".AsMemory(), cancellationToken).ConfigureAwait(false);
        await KafkaSeeder.SeedAsync(new ConfluentSeedTarget(config), plan, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("KStudio sample: seeded.".AsMemory(), cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Stops the broker this process started and removes its anonymous volumes, or leaves both when reuse was asked for.</summary>
    public async ValueTask DisposeAsync()
    {
        if (container is null)
        {
            return;
        }

        await container.DisposeAsync().ConfigureAwait(false);

        if (!reused)
        {
            await DockerVolumes.RemoveAsync(volumes).ConfigureAwait(false);
        }
    }

    private static bool ReuseRequested() => IsReuseRequested(Environment.GetEnvironmentVariable(ReuseEnvironmentVariable));
}

/// <summary>
/// The few <c>docker</c> calls the broker's anonymous volumes need: the image declares six, and Docker leaves them behind with the container
/// unless they are removed with it (the lesson of KStudio's live fixture).
/// </summary>
internal static class DockerVolumes
{
    /// <summary>The names of a container's volumes. A failure to ask is no volumes: this is cleanup, not a requirement.</summary>
    /// <param name="containerId">The container.</param>
    public static async Task<string[]> OfAsync(string containerId)
    {
        string output = await RunAsync("inspect", "--format", "{{range .Mounts}}{{if .Name}}{{.Name}} {{end}}{{end}}", containerId).ConfigureAwait(false);
        return output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Removes volumes; one that is already gone is not an error.</summary>
    /// <param name="volumes">The volumes' names.</param>
    public static async Task RemoveAsync(IReadOnlyList<string> volumes)
    {
        if (volumes.Count > 0)
        {
            await RunAsync(["volume", "rm", "--force", .. volumes]).ConfigureAwait(false);
        }
    }

    private static async Task<string> RunAsync(params string[] arguments)
    {
        try
        {
            ProcessStartInfo startInfo = new("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No docker CLI on the path: the volumes stay, which is the cost of not having one.
            return string.Empty;
        }
    }
}
