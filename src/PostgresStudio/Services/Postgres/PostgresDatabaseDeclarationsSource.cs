using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// Postgres Studio's <see cref="IDatabaseDeclarationsSource" />: a plain data source declares nothing, so every object is
/// "Other" but what the <c>mt_</c> rule calls Marten's, and its own schemas are the ones
/// <c>PostgresStudioOptions.BrowsableSchemas</c> names exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Own schemas from configuration alone</b> - the invariant <see cref="IDatabaseDeclarationsSource.Read(IStudioSource)" />
/// states: both reads answer the same <see cref="DatabaseDeclarations.StoreSchemas" /> whichever database a scope
/// resolved to, because neither looks at one. They are the exact names <c>BrowsableSchemas</c> lists, in its order,
/// less <c>"*"</c> and the names no schema the browser reads can have (a system schema, an entry
/// <see cref="BrowsableSchemaMatcher.IsValidEntry" /> refuses). Matching them against the live schema list is the
/// gate's (<see cref="DatabaseGate" />), which shows only those that exist. <c>"*"</c> admits every other schema the
/// reading role has <c>USAGE</c> on - which is a statement about the database, not the configuration, so it can make no
/// schema the source's own: those schemas' structure, like every row and every definition, needs
/// <c>BrowseDatabase</c> and the write policy, or for a definition <c>ViewDefinitions</c> (D27, design PS10).
/// </para>
/// <para>
/// <b>Marten's tables stay Marten's.</b> The classifier is empty, and its <c>mt_</c> rule still applies: any
/// <c>mt_</c>-prefixed object - a Marten store's documents, its event store, its bookkeeping - is "Marten internal", its
/// rows are never read here and a view over one is refused (design R2). No store is registered, so nothing is hidden
/// (<see cref="DatabaseObjectClassifier.MayHideDocumentTypes" /> is <see langword="false" />) and there are no other
/// sources to ask about: one source is one database.
/// </para>
/// <para>
/// <b>Never fails.</b> There is nothing to read that could fail, so nothing is withheld for want of a classification,
/// and nothing here connects or builds a schema (AGENTS.md hard rule 14).
/// </para>
/// </remarks>
internal sealed class PostgresDatabaseDeclarationsSource : IDatabaseDeclarationsSource
{
    /// <summary>The classifier every read answers: no declarations, the <c>mt_</c> rule, nothing hidden.</summary>
    private static readonly DatabaseObjectClassifier Undeclared = new([], hidesDocumentTypes: false);

    private static readonly IReadOnlyDictionary<string, DatabaseStoreAccess> NoOtherSources =
        new Dictionary<string, DatabaseStoreAccess>(StringComparer.OrdinalIgnoreCase);

    private readonly IStudioSettings settings;

    /// <param name="settings">The studio's settings, whose <c>BrowsableSchemas</c> are the own schemas.</param>
    public PostgresDatabaseDeclarationsSource(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
    }

    /// <inheritdoc />
    public DatabaseDeclarations Read(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return Declare();
    }

    /// <inheritdoc />
    public DatabaseDeclarations Read(IStudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Declare();
    }

    /// <inheritdoc />
    public DatabaseDeclarations ReadUncached(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return Declare();
    }

    /// <inheritdoc />
    /// <remarks>Always empty: another data source's objects are never classified as its, so nobody is asked about one.</remarks>
    public Task<IReadOnlyDictionary<string, DatabaseStoreAccess>> OtherSourcesAsync(
        ResolvedSource resolved,
        DatabaseObjectClassifier classifier,
        bool capabilityEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(classifier);

        return Task.FromResult(NoOtherSources);
    }

    /// <summary>
    /// The source's own schemas, from <paramref name="browsableSchemas" /> alone: the exact names it lists, in order and
    /// once each, less <c>"*"</c>, any system schema and any entry the browser would refuse.
    /// </summary>
    /// <param name="browsableSchemas"><c>PostgresStudioOptions.BrowsableSchemas</c>.</param>
    internal static IReadOnlyList<string> OwnSchemas(IEnumerable<string> browsableSchemas)
    {
        ArgumentNullException.ThrowIfNull(browsableSchemas);

        List<string> own = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string entry in browsableSchemas)
        {
            if (entry == BrowsableSchemaMatcher.Everything
                || !BrowsableSchemaMatcher.IsValidEntry(entry)
                || BrowsableSchemaMatcher.IsSystemSchema(entry)
                || !seen.Add(entry))
            {
                continue;
            }

            own.Add(entry);
        }

        return own;
    }

    private DatabaseDeclarations Declare() => new(Undeclared, OwnSchemas(settings.BrowsableSchemas), null);
}
