using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Live;
using MartenStudio.Services.Relationships;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Relationships;

/// <summary>
/// The real Relationships service, built by hand from a studio's own container with a cache, settings or logger of
/// the test's choosing: the shared graph service over the shared reads, and MartenStudio's node source over the same
/// reads - exactly the parts the container wires, so nothing about the gating is bypassed.
/// </summary>
internal static class RelationshipServiceBuilder
{
    /// <summary>Builds the service from <paramref name="services" />, replacing what the test names.</summary>
    /// <param name="services">A DI scope's provider to take the resolver, the catalogs and the gate from.</param>
    /// <param name="cache">The snapshot cache the shared key read goes through.</param>
    /// <param name="settings">The studio's settings, or <see langword="null" /> for the container's.</param>
    /// <param name="logger">The logger both halves write under, or <see langword="null" /> for the container's.</param>
    public static RelationshipDataService Build(
        IServiceProvider services,
        StudioSnapshotCache cache,
        IStudioSettings? settings = null,
        ILogger<RelationshipDataService>? logger = null)
    {
        settings ??= services.GetRequiredService<IStudioSettings>();
        logger ??= services.GetRequiredService<ILogger<RelationshipDataService>>();

        var reads = new RelationshipReads(
            settings,
            cache,
            logger,
            services.GetRequiredService<DatabaseAccess>(),
            services.GetRequiredService<DatabaseCatalog>(),
            services.GetRequiredService<StudioCapabilityGuard>());

        var nodes = new MartenRelationshipNodeSource(
            settings,
            services.GetRequiredService<StudioScopeResolver>(),
            services.GetRequiredService<ColumnCatalog>(),
            reads,
            services.GetRequiredService<DatabaseAccess>(),
            logger);

        return new RelationshipDataService(
            services.GetRequiredService<StudioSourceResolver>(),
            reads,
            logger,
            nodes,
            TimeProvider.System);
    }
}
