using System.Runtime.CompilerServices;

using KStudio.Core.Clusters;
using KStudio.Internal;
using KStudio.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Settings;

namespace KStudio;

/// <summary>Registers KStudio, the Kafka module of the studio the host registered.</summary>
public static class KafkaStudioServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Kafka module to the studio the host registered with <c>AddMartenStudio()</c> or <c>AddPostgresStudio()</c>: its pages, rail
    /// section, cluster selector, capabilities and audit entries join that studio's own (D34).
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no <c>MapKafkaStudio()</c>: the studio's own map call serves the Kafka pages under its path, at <c>/kafka</c>. This may be
    /// called before or after the studio's own <c>Add*Studio()</c>, and twice; the host is refused at start when it registered no studio,
    /// and when a capability is on that no policy answers.
    /// </para>
    /// <para>
    /// Nothing here connects to Kafka, creates a topic or commits an offset, and nothing is a hosted service but the startup guard.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Registers the clusters and topics, and sets the policies and capabilities.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The application was deployed without the PostgresStudio, PostgresStudio.Diagnostics or KStudio.Core build this one was released
    /// with, or with another version of it.
    /// </exception>
    public static IServiceCollection AddKafkaStudio(
        this IServiceCollection services,
        Action<KafkaStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Before anything that names a PostgresStudio or KStudio.Core type: this body names none, so the JIT can compile it and the
        // guard can say what is wrong even when NuGet left one of them out of the deployment. Everything else is in
        // AddKafkaStudioServices, which is NoInlining so that it is compiled only once the guard has passed.
        // KStudioVersionGuardTests scans this method's IL to keep it that way.
        KStudioVersionGuard.Ensure();

        return AddKafkaStudioServices(services, configure);
    }

    /// <summary>The body of <see cref="AddKafkaStudio" />, once the studio and the engine are known to be the right builds.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IServiceCollection AddKafkaStudioServices(
        IServiceCollection services,
        Action<KafkaStudioOptions>? configure)
    {
        // The module itself: TryAddEnumerable deduplicates by implementation type, so a second call adds it once.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, KafkaStudioModule>());

        OptionsBuilder<KafkaStudioOptions> optionsBuilder = services
            .AddOptions<KafkaStudioOptions>()
            .Validate(
                static options => options.Capabilities is not null,
                "KafkaStudioOptions.Capabilities cannot be null: use new KafkaStudioCapabilities() for none, or KafkaStudioCapabilities.All()")
            .Validate(
                static options => options.Capabilities is null || !options.Capabilities.ReadPayloads || options.Capabilities.SearchRecords,
                "KafkaStudioOptions.Capabilities.ReadPayloads needs KafkaStudioOptions.Capabilities.SearchRecords: a value is read only as part of a record a search or a permalink opened");

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // The engine: the options object answers both seams, as a studio's does (D30).
        KStudioEngineRegistration.Add(
            services,
            static provider => provider.GetRequiredService<IOptions<KafkaStudioOptions>>().Value,
            static provider => new KafkaEngineSettings(provider.GetRequiredService<IStudioSettings>()));

        services.TryAddSingleton<IKafkaClusterEntrySource, CatalogClusterEntrySource>();
        services.TryAddSingleton<KafkaCapabilityGuard>();

        // Scoped, because each is about one circuit: the visitor, the cluster it is pointed at and what it has been told.
        services.TryAddScoped<KafkaState>();
        services.TryAddScoped<KafkaAuthorization>();
        services.TryAddScoped<KafkaActionLog>();
        services.TryAddScoped<KafkaCatalogService>();

        // Search and the record page (UI-B): scoped, because a search and its audit are one circuit's, and so is the owner of its scans.
        services.TryAddScoped<KafkaOwnerKey>();
        services.TryAddScoped<KafkaReadGrants>();
        services.TryAddScoped<KafkaSearchService>();
        services.TryAddScoped<KafkaRecordService>();

        // Related records (UI-D): one audited scan over several topics, each searched by its own fields.
        services.TryAddScoped<KafkaCorrelationService>();

        // Which of the module's audit entries a visitor may read (D34): its own gate, never the host's store policy.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStudioAuditScopeGate, KafkaAuditGate>());

        // Refuses to start a host that registered no studio, and one with a capability no policy answers.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, KafkaStudioStartupGuard>());

        return services;
    }
}
