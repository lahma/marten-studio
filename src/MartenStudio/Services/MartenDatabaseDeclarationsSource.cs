using MartenStudio.Services.Database;
using MartenStudio.Services.Sources;

using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IDatabaseDeclarationsSource" />: every registered store's declarations and every other
/// store's policies, as <see cref="DatabaseAccess" /> reads them today. PS-1a's pass-through; the database packet
/// (PS-1b) moves that code here and makes <see cref="DatabaseAccess" /> ask this seam instead.
/// </summary>
internal sealed class MartenDatabaseDeclarationsSource : IDatabaseDeclarationsSource
{
    private readonly DatabaseAccess access;
    private readonly MartenStoreRegistry registry;
    private readonly IServiceProvider provider;
    private readonly IOptions<MartenStudioOptions> options;

    public MartenDatabaseDeclarationsSource(
        DatabaseAccess access,
        MartenStoreRegistry registry,
        IServiceProvider provider,
        IOptions<MartenStudioOptions> options)
    {
        this.access = access;
        this.registry = registry;
        this.provider = provider;
        this.options = options;
    }

    /// <inheritdoc />
    public DatabaseDeclarations Read(ResolvedSource resolved) => access.ReadDeclarations(Marten(resolved));

    /// <inheritdoc />
    public DatabaseDeclarations ReadUncached(ResolvedSource resolved)
    {
        ResolvedScope scope = Marten(resolved);
        return DatabaseAccess.DeclareEveryStore(registry, provider, scope.Registration, scope.Store, options.Value.IsDocumentTypeVisible);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, DatabaseStoreAccess>> OtherSourcesAsync(
        ResolvedSource resolved,
        DatabaseObjectClassifier classifier,
        bool capabilityEnabled,
        CancellationToken cancellationToken) =>
        access.OtherStoresAsync(Marten(resolved), classifier, capabilityEnabled, cancellationToken);

    private static ResolvedScope Marten(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved as ResolvedScope
            ?? throw new ArgumentException("MartenStudio declares the objects of a Marten store only.", nameof(resolved));
    }
}
