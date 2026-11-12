using Confluent.Kafka;

using KStudio.Core.Catalog;
using KStudio.Core.Logging;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;
using KStudio.Core.Windows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KStudio.Core.Clusters;

/// <summary>
/// The engine's registrations (decision 2.5): singletons only. No hosted service, no startup filter, nothing resolved here and nothing
/// connected: a client is created by the first call that needs it. KStudio calls <see cref="Add" />; tests call it directly, putting a
/// fake <see cref="IKafkaWireFactory" /> first.
/// </summary>
internal static class KStudioEngineRegistration
{
    /// <summary>Adds the engine to <paramref name="services" />.</summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The engine options, as the host studio builds them.</param>
    /// <param name="settings">What the engine takes from its host studio.</param>
    public static IServiceCollection Add(
        IServiceCollection services,
        Func<IServiceProvider, KafkaEngineOptions> options,
        Func<IServiceProvider, IKafkaEngineSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton(settings);
        services.TryAddSingleton<KStudioLoggers>();

        // Built on first use, and given the container so that a cluster source (D43) is run then, after the host has started, and not at the
        // options delegate. Everything it needs that is registered here is resolved lazily by the sources themselves.
        services.TryAddSingleton(static provider =>
            new KafkaClusterRegistry(provider.GetRequiredService<KafkaEngineOptions>(), provider));
        services.TryAddSingleton<KafkaSettingsReader>();

        // The options are validated once, by the registry, and read from this snapshot only (REVIEW-ENG-A 8).
        services.TryAddSingleton(static provider => provider.GetRequiredService<KafkaClusterRegistry>().Settings);

        services.TryAddSingleton(static provider => new KafkaLogOnce(provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ConfluentClientEvents>();

        services.TryAddSingleton(static provider =>
        {
            ConfluentClientEvents events = provider.GetRequiredService<ConfluentClientEvents>();
            KafkaEngineSnapshot snapshot = provider.GetRequiredService<KafkaEngineSnapshot>();
            return new KafkaAdminPool<IAdminClient>(
                cluster => ConfluentWireFactory.BuildAdminClient(cluster, events),
                "admin",
                provider.GetRequiredService<TimeProvider>(),
                snapshot.AdminIdleTimeout,
                provider.GetRequiredService<KStudioLoggers>().Clients,
                snapshot.RequestTimeout + TimeSpan.FromSeconds(1));
        });

        // The group-offsets read's own handle: a consumer that is never subscribed, with an admin client over it (D36, librdkafka #5040).
        services.TryAddSingleton(static provider =>
        {
            ConfluentClientEvents events = provider.GetRequiredService<ConfluentClientEvents>();
            KafkaEngineSnapshot snapshot = provider.GetRequiredService<KafkaEngineSnapshot>();
            return new KafkaAdminPool<ConfluentOffsetsReader>(
                cluster => new ConfluentOffsetsReader(cluster, snapshot.GroupIdPrefix, events),
                "offsets",
                provider.GetRequiredService<TimeProvider>(),
                snapshot.AdminIdleTimeout,
                provider.GetRequiredService<KStudioLoggers>().Clients,
                snapshot.RequestTimeout + TimeSpan.FromSeconds(1));
        });

        services.TryAddSingleton<IKafkaWireFactory, ConfluentWireFactory>();
        services.TryAddSingleton<KafkaWires>();
        services.TryAddSingleton(static provider => new KafkaSnapshotCache(provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(static provider => new KafkaThroughputSamples(provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<KafkaWireReads>();
        services.TryAddSingleton<IKafkaCatalog, KafkaCatalog>();
        services.TryAddSingleton<WindowResolver>();

        KStudio.Core.Scanning.ScanningRegistration.Add(services);
        return services;
    }
}
