using System.Diagnostics;
using System.Reflection;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

using Testcontainers.Kafka;

namespace KStudio.Integration.Tests.Support;

/// <summary>
/// One <c>apache/kafka</c> broker, started once per image for the whole run (decision 12.2): KRaft, SASL/PLAIN and ACLs, so that "no topic
/// created" is proved against a principal that could have created one, and a restricted principal's refusals are real.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reuse</b> is on locally and off on CI and when <c>MARTENSTUDIO_PG_REUSE</c> is <c>false</c> or <c>0</c> (the one variable every container
/// here reads; <c>TESTCONTAINERS_REUSE_ENABLE</c> is not read by Testcontainers 4.x). The label
/// <c>marten-studio.tests=KStudio.Integration.Tests.{image tag}</c> gives each image its own reuse identity, and <c>KSTUDIO_PACKET</c>, when
/// set, adds <c>kstudio.packet={value}</c>, part of the hash, so two agent packets never attach to each other's broker.
/// </para>
/// <para>
/// <b>Volumes.</b> The image declares six anonymous volumes; Docker leaves them behind with the container unless they are removed with it.
/// The fixture records them when it starts and removes them when it disposes a container it does not reuse
/// (<c>KafkaFixtureTests</c> proves none is left).
/// </para>
/// </remarks>
public abstract class KafkaFixture : IAsyncLifetime
{
    /// <summary>The variable that turns container reuse off for one run; the same name every container here reads.</summary>
    public const string ReuseEnvironmentVariable = "MARTENSTUDIO_PG_REUSE";

    /// <summary>The variable an agent packet sets so its containers carry <c>kstudio.packet</c>.</summary>
    public const string PacketEnvironmentVariable = "KSTUDIO_PACKET";

    /// <summary>The label that gives each image's container a reuse identity of its own.</summary>
    public const string ReuseLabel = "marten-studio.tests";

    /// <summary>The newest 3.9 release.</summary>
    public const string Image39 = "apache/kafka:3.9.2";

    /// <summary>The newest 4.x release.</summary>
    public const string Image4 = "apache/kafka:4.4.0";

    private static readonly string[] UserNames = ["admin", "reader", "configreader", "nogroup", "describeonly"];

    private KafkaContainer? container;
    private string[] volumes = [];

    /// <summary>Names the image this fixture runs.</summary>
    /// <param name="image">The image, repository and tag.</param>
    protected KafkaFixture(string image)
    {
        Image = image;
        RunId = Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>The image.</summary>
    public string Image { get; }

    /// <summary>The image's tag, <c>3.9.2</c> or <c>4.4.0</c>.</summary>
    public string Tag => Image[(Image.IndexOf(':', StringComparison.Ordinal) + 1)..];

    /// <summary>Eight hex characters unique to this fixture instance: part of every topic name it hands out.</summary>
    public string RunId { get; }

    /// <summary>Whether the broker is running; <see langword="false" /> when there is no Docker (the tests skip).</summary>
    public bool Started { get; private set; }

    /// <summary><c>host:port</c> of the broker, once it is running.</summary>
    public string Bootstrap { get; private set; } = string.Empty;

    /// <summary>
    /// Whether this run shares a container with the ones around it: never on CI, and not when <see cref="ReuseEnvironmentVariable" /> is
    /// <c>false</c> or <c>0</c>.
    /// </summary>
    public static bool ShouldReuse =>
        !DockerAvailability.OnContinuousIntegration && !IsReuseDisabled(Environment.GetEnvironmentVariable(ReuseEnvironmentVariable));

    /// <summary>The label value of an image's reuse identity.</summary>
    /// <param name="image">The image.</param>
    public static string ReuseLabelValue(string image) => "KStudio.Integration.Tests." + image[(image.IndexOf(':', StringComparison.Ordinal) + 1)..];

    /// <summary>
    /// Whether a <see cref="ReuseEnvironmentVariable" /> value turns reuse off: only <c>false</c> and <c>0</c> do, so an unset or misspelt value
    /// leaves the default alone.
    /// </summary>
    /// <param name="value">The variable's value.</param>
    public static bool IsReuseDisabled(string? value) =>
        value?.Trim() is { } trimmed
        && (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, "0", StringComparison.Ordinal));

    /// <summary>The password of a principal: <c>{user}-secret</c>.</summary>
    /// <param name="user">The principal.</param>
    public static string Password(string user) => user + "-secret";

    /// <summary>The container this fixture starts, built but not started, with decision 12.2's environment.</summary>
    /// <param name="image">The image.</param>
    /// <param name="reuse">Whether to ask for reuse; the reuse hash does not depend on it.</param>
    /// <param name="packet">The agent packet, for the <c>kstudio.packet</c> label; <see langword="null" /> adds none.</param>
    public static KafkaContainer CreateContainer(string image, bool reuse, string? packet)
    {
        string users = string.Concat(UserNames.Select(static x => $" user_{x}=\"{Password(x)}\""));
        string jaas = "org.apache.kafka.common.security.plain.PlainLoginModule required username=\"admin\" password=\"admin-secret\"" + users + ";";

        KafkaBuilder builder = new KafkaBuilder(image)
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "BROKER:PLAINTEXT,CONTROLLER:PLAINTEXT,PLAINTEXT:SASL_PLAINTEXT")
            .WithEnvironment("KAFKA_SASL_ENABLED_MECHANISMS", "PLAIN")
            .WithEnvironment("KAFKA_LISTENER_NAME_PLAINTEXT_SASL_ENABLED_MECHANISMS", "PLAIN")
            .WithEnvironment("KAFKA_LISTENER_NAME_PLAINTEXT_PLAIN_SASL_JAAS_CONFIG", jaas)
            .WithEnvironment("KAFKA_AUTHORIZER_CLASS_NAME", "org.apache.kafka.metadata.authorizer.StandardAuthorizer")
            .WithEnvironment("KAFKA_SUPER_USERS", "User:admin;User:ANONYMOUS")
            .WithEnvironment("KAFKA_ALLOW_EVERYONE_IF_NO_ACL_FOUND", "false")
            .WithEnvironment("KAFKA_AUTO_CREATE_TOPICS_ENABLE", "true")
            .WithEnvironment("KAFKA_HEAP_OPTS", "-Xmx512m -Xms256m")

            // Testcontainers 4.15.0's startup script ends KAFKA_ADVERTISED_LISTENERS with a comma when the container has no additional
            // advertised listener, and Kafka 4.4 refuses an empty entry ("advertised.listeners values must not be empty"; 3.9 tolerates it).
            // One more listener fixes it. It is bound to the container's own loopback, so nothing outside the container can reach it.
            .WithListener("localhost:9095")
            .WithLabel(ReuseLabel, ReuseLabelValue(image))
            .WithReuse(reuse);

        if (!string.IsNullOrWhiteSpace(packet))
        {
            builder = builder.WithLabel("kstudio.packet", packet);
        }

        return builder.Build();
    }

    /// <summary>A client configuration for a principal, pointing at this broker.</summary>
    /// <param name="user">The principal: <c>admin</c>, <c>reader</c>, <c>configreader</c>, <c>nogroup</c> or <c>describeonly</c>.</param>
    public ClientConfig Client(string user) => ClientFor(Bootstrap, user, Password(user));

    /// <summary>A client configuration with a given user and password, for a wrong-password test.</summary>
    /// <param name="bootstrap">The broker.</param>
    /// <param name="user">The principal.</param>
    /// <param name="password">The password.</param>
    public static ClientConfig ClientFor(string bootstrap, string user, string password) => new()
    {
        BootstrapServers = bootstrap,
        SecurityProtocol = SecurityProtocol.SaslPlaintext,
        SaslMechanism = SaslMechanism.Plain,
        SaslUsername = user,
        SaslPassword = password,
    };

    /// <summary>Starts the broker, waits until SASL answers and creates the ACLs. Does nothing when there is no Docker.</summary>
    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        container = CreateContainer(Image, Reuse, Environment.GetEnvironmentVariable(PacketEnvironmentVariable));
        await container.StartAsync();
        Bootstrap = $"{container.Hostname}:{container.GetMappedPublicPort(KafkaBuilder.KafkaPort)}";
        volumes = await DockerCli.VolumesOfAsync(container.Id);

        await WaitUntilReadyAsync();
        await CreateAclsAsync();

        if (WarmUpCoordinator)
        {
            await WarmUpGroupCoordinatorAsync();
        }

        Started = true;
    }

    /// <summary>Stops the broker and removes its volumes, or leaves both when reuse is on.</summary>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (container is null)
        {
            return;
        }

        await container.DisposeAsync();

        if (!Reuse)
        {
            await DockerCli.RemoveVolumesAsync(volumes);
        }
    }

    /// <summary>Whether this instance asks for a reusable container; <see cref="ShouldReuse" /> unless a derived fixture needs a broker of its own.</summary>
    protected virtual bool Reuse => ShouldReuse;

    /// <summary>
    /// Whether the fixture waits for the group coordinator before it is ready. A broker answers a group request with <c>NOT_COORDINATOR</c>
    /// until it has created and elected <c>__consumer_offsets</c>, which the first group request on a pristine broker starts; the fixture
    /// does it with a consumer so that no test is the first. Only the fresh-broker test turns this off, because it is that first request.
    /// </summary>
    protected virtual bool WarmUpCoordinator => true;

    /// <summary>The anonymous volumes the container declared, recorded when it started.</summary>
    public IReadOnlyList<string> Volumes => volumes;

    /// <summary>
    /// Waits until the broker's group coordinator answers an OffsetFetch. The first group lookup on a pristine broker auto-creates
    /// <c>__consumer_offsets</c>, and an admin client that asks for a group's offsets before the coordinator is elected gets
    /// <c>NOT_COORDINATOR</c> or <c>COORDINATOR_NOT_AVAILABLE</c>, which librdkafka up to 2.16.0 turns into a NULL dereference (issue #5040, PR
    /// #5611). A consumer's own OffsetFetch takes the path that has a consumer group, so it is the safe way to wait. The engine itself reads on
    /// a consumer-backed handle (D36) and needs none of this; the fixture does it for the tests' other clients.
    /// </summary>
    private async Task WarmUpGroupCoordinatorAsync()
    {
        ConsumerConfig config = new(Client("admin")) { GroupId = "kstudio-warmup-" + RunId, EnableAutoCommit = false };
        using IConsumer<byte[], byte[]> consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);

        while (true)
        {
            try
            {
                consumer.Committed([new TopicPartition("ks.warmup", 0)], TimeSpan.FromSeconds(5));
                return;
            }
            catch (KafkaException) when (DateTime.UtcNow < deadline)
            {
                // A poll of a condition, not a delay: the call itself waits up to five seconds.
                await Task.Yield();
            }
        }
    }

    private async Task WaitUntilReadyAsync()
    {
        using IAdminClient admin = new AdminClientBuilder(Client("admin")).Build();
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);

        while (true)
        {
            try
            {
                admin.GetMetadata(TimeSpan.FromSeconds(5));
                return;
            }
            catch (KafkaException) when (DateTime.UtcNow < deadline)
            {
                // The broker has logged that it runs; the SASL listener can lag it by a moment (a poll of a condition, not a delay).
                await Task.Yield();
            }
        }
    }

    /// <summary>ACLs, all ALLOW and PREFIXED: <c>reader</c> READ; <c>configreader</c> READ plus DESCRIBE_CONFIGS; <c>nogroup</c> READ with no group ACL (SPIKE S1); <c>describeonly</c> DESCRIBE on the topics and READ on the groups, but no READ on a topic.</summary>
    private async Task CreateAclsAsync()
    {
        using IAdminClient admin = new AdminClientBuilder(Client("admin")).Build();

        List<AclBinding> bindings = [];
        Allow(bindings, "reader", ResourceType.Topic, "ks.", AclOperation.Read);
        Allow(bindings, "reader", ResourceType.Group, "kstudio-", AclOperation.Read);
        Allow(bindings, "configreader", ResourceType.Topic, "ks.", AclOperation.Read);
        Allow(bindings, "configreader", ResourceType.Topic, "ks.", AclOperation.DescribeConfigs);
        Allow(bindings, "configreader", ResourceType.Group, "kstudio-", AclOperation.Read);
        Allow(bindings, "nogroup", ResourceType.Topic, "ks.", AclOperation.Read);
        Allow(bindings, "describeonly", ResourceType.Topic, "ks.", AclOperation.Describe);
        Allow(bindings, "describeonly", ResourceType.Group, "kstudio-", AclOperation.Read);

        // Creating an ACL that exists is not an error, so a reused container is fine.
        await admin.CreateAclsAsync(bindings);
    }

    private static void Allow(List<AclBinding> bindings, string user, ResourceType type, string prefix, AclOperation operation) =>
        bindings.Add(new AclBinding
        {
            Pattern = new ResourcePattern { Type = type, Name = prefix, ResourcePatternType = ResourcePatternType.Prefixed },
            Entry = new AccessControlEntry { Principal = "User:" + user, Host = "*", Operation = operation, PermissionType = AclPermissionType.Allow },
        });
}

/// <summary>The broker for Kafka 3.9.</summary>
public sealed class Kafka39Fixture() : KafkaFixture(Image39);

/// <summary>The broker for Kafka 4.x.</summary>
public sealed class Kafka4Fixture() : KafkaFixture(Image4);

/// <summary>
/// A broker of its own, never reused and never warmed up: the first group request it sees is the test's, which is how a pristine cluster
/// behaves (<c>FreshBrokerLagReadLiveTests</c>).
/// </summary>
/// <param name="image">The image.</param>
public sealed class FreshKafkaFixture(string image) : KafkaFixture(image)
{
    /// <inheritdoc />
    protected override bool Reuse => false;

    /// <inheritdoc />
    protected override bool WarmUpCoordinator => false;
}

/// <summary>The xunit collections that give each image one broker for every live class on it.</summary>
public static class KafkaCollections
{
    /// <summary>The Kafka 3.9 collection.</summary>
    public const string V39 = "kafka-3.9";

    /// <summary>The Kafka 4.x collection.</summary>
    public const string V4 = "kafka-4";

    /// <summary>
    /// A Kafka 4.x collection that runs alone: xunit runs a collection that disables parallelization after every parallel one has finished, and
    /// one at a time, so a test that measures process-wide memory sees nothing else of the run (<c>ScanMemoryCeilingLiveTests4</c>). It has a
    /// broker of its own, which a reusing run attaches to the same container as <see cref="V4" />'s.
    /// </summary>
    public const string V4Alone = "kafka-4-alone";
}

/// <summary>Kafka 3.9: one broker for every class in the collection.</summary>
[CollectionDefinition(KafkaCollections.V39)]
public sealed class Kafka39Group : ICollectionFixture<Kafka39Fixture>;

/// <summary>Kafka 4.x: one broker for every class in the collection.</summary>
[CollectionDefinition(KafkaCollections.V4)]
public sealed class Kafka4Group : ICollectionFixture<Kafka4Fixture>;

/// <summary>Kafka 4.x, with nothing else of the run executing beside it: for the tests that measure the process's own memory.</summary>
[CollectionDefinition(KafkaCollections.V4Alone, DisableParallelization = true)]
public sealed class Kafka4AloneGroup : ICollectionFixture<Kafka4Fixture>;

/// <summary>A built container's reuse identity, as Testcontainers computes it.</summary>
internal static class ContainerReuse
{
    /// <summary>
    /// The configuration Testcontainers hashes to decide which running container a reusing start attaches to. It is held in a private field of
    /// <see cref="DockerContainer" />, so a rename there fails here, by name.
    /// </summary>
    /// <param name="container">The container.</param>
    public static IContainerConfiguration ConfigurationOf(IContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        FieldInfo field = typeof(DockerContainer).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Testcontainers' DockerContainer no longer has a private _configuration field.");

        return (IContainerConfiguration) field.GetValue(container)!;
    }

    /// <summary>The reuse hash: Testcontainers' own, from the container's configuration.</summary>
    /// <param name="container">The container.</param>
    public static string HashOf(IContainer container) => ConfigurationOf(container).GetReuseHash();
}

/// <summary>The few <c>docker</c> calls the fixture needs, by CLI: the Docker API client does not expose a container's anonymous volumes simply.</summary>
internal static class DockerCli
{
    /// <summary>The names of a container's volumes.</summary>
    /// <param name="containerId">The container.</param>
    public static async Task<string[]> VolumesOfAsync(string containerId)
    {
        string output = await RunAsync("inspect", "--format", "{{range .Mounts}}{{if .Name}}{{.Name}} {{end}}{{end}}", containerId);
        return output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Removes volumes; one that is already gone is not an error.</summary>
    /// <param name="volumes">The volumes' names.</param>
    public static async Task RemoveVolumesAsync(IReadOnlyList<string> volumes)
    {
        if (volumes.Count > 0)
        {
            await RunAsync(["volume", "rm", "--force", .. volumes]);
        }
    }

    /// <summary>The names, out of those given, of the volumes that still exist.</summary>
    /// <param name="volumes">The volumes' names.</param>
    public static async Task<string[]> ExistingAsync(IReadOnlyList<string> volumes)
    {
        if (volumes.Count == 0)
        {
            return [];
        }

        string output = await RunAsync("volume", "ls", "--quiet");
        HashSet<string> all = [.. output.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)];
        return [.. volumes.Where(all.Contains)];
    }

    private static async Task<string> RunAsync(params string[] arguments)
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
        await process.WaitForExitAsync();
        await error;
        return await output;
    }
}
