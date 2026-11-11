using Confluent.Kafka;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Core.Windows;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Contract;

/// <summary>What the engine takes from its host studio, for a test.</summary>
internal sealed class TestEngineSettings(TimeSpan catalogTimeToLive) : IKafkaEngineSettings
{
    public TimeSpan CatalogTimeToLive { get; } = catalogTimeToLive;
}

/// <summary>Always hands out the one wire a test gave it, so the engine's real services run over a fake or a live wire.</summary>
internal sealed class SingleWireFactory(IKafkaWire wire) : IKafkaWireFactory
{
    public IKafkaWire Create(KafkaClusterDefinition cluster) => wire;
}

/// <summary>
/// The engine's real container (<c>KStudioEngineRegistration</c>) over one cluster named <c>test</c>, for the contract suites: the same
/// resolver and catalog run over the fake in the fast suite and over a real broker in the live suite. The catalog time-to-live is zero
/// unless a test says otherwise, so a read after a seed really reads.
/// </summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public const string ClusterKey = "test";

    private EngineHarness(ServiceProvider provider, CapturingLoggerProvider logs, TimeProvider clock)
    {
        Provider = provider;
        Logs = logs;
        Clock = clock;
    }

    public ServiceProvider Provider { get; }

    public CapturingLoggerProvider Logs { get; }

    public TimeProvider Clock { get; }

    public IKafkaWire Wire => Provider.GetRequiredService<KafkaWires>().For(ClusterKey);

    public IKafkaCatalog Catalog => Provider.GetRequiredService<IKafkaCatalog>();

    public WindowResolver Resolver => Provider.GetRequiredService<WindowResolver>();

    public KafkaEngineOptions Options => Provider.GetRequiredService<KafkaEngineOptions>();

    /// <summary>The engine over a wire a test supplies (the fake).</summary>
    public static EngineHarness ForWire(
        IKafkaWire wire,
        TimeProvider? clock = null,
        TimeSpan? catalogTimeToLive = null,
        Action<KafkaEngineOptions>? configure = null) =>
        Build(new SingleWireFactory(wire), clock, catalogTimeToLive, configure, new ClientConfig { BootstrapServers = "fake:9092" });

    /// <summary>The engine over the real Confluent wire, for a live cluster.</summary>
    public static EngineHarness ForClient(
        ClientConfig client,
        TimeSpan? catalogTimeToLive = null,
        Action<KafkaEngineOptions>? configure = null) =>
        Build(null, null, catalogTimeToLive, configure, client);

    private static EngineHarness Build(
        IKafkaWireFactory? factory,
        TimeProvider? clock,
        TimeSpan? catalogTimeToLive,
        Action<KafkaEngineOptions>? configure,
        ClientConfig client)
    {
        CapturingLoggerProvider logs = new();
        TimeProvider time = clock ?? TimeProvider.System;

        KafkaEngineOptions options = new();
        options.AddCluster(ClusterKey, client);
        configure?.Invoke(options);

        ServiceCollection services = new();
        services.AddSingleton(logs.CreateFactory());
        services.AddSingleton(time);

        if (factory is not null)
        {
            services.AddSingleton(factory);
        }

        KStudioEngineRegistration.Add(services, _ => options, _ => new TestEngineSettings(catalogTimeToLive ?? TimeSpan.Zero));

        return new EngineHarness(services.BuildServiceProvider(), logs, time);
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}
