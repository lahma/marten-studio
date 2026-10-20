using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="IDatabaseDeclarationsSource" />: no store declares anything, so the classifier is empty
/// and a source's own schemas are the exact names in <c>BrowsableSchemas</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration only.</b> Both <see cref="Read(ResolvedSource)" /> and <see cref="Read(IStudioSource)" /> answer
/// from <see cref="IStudioSettings.BrowsableSchemas" /> alone, so they always agree (the invariant the interface
/// documents): no connection, no catalog, no resolved database.
/// </para>
/// <para>
/// <b>The <c>mt_</c> rule still applies.</b> The classifier is the shared one, which calls every <c>mt_</c>-prefixed
/// object Marten's whatever declares it, so a Marten table seen from here is never read raw (design R2).
/// </para>
/// <para>A minimal body by PS-3a; PS-3b owns the implementation.</para>
/// </remarks>
internal sealed class PostgresDatabaseDeclarationsSource : IDatabaseDeclarationsSource
{
    private readonly IStudioSettings settings;

    public PostgresDatabaseDeclarationsSource(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
    }

    /// <inheritdoc />
    public DatabaseDeclarations Read(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return Declarations();
    }

    /// <inheritdoc />
    public DatabaseDeclarations Read(IStudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Declarations();
    }

    /// <inheritdoc />
    public DatabaseDeclarations ReadUncached(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return Declarations();
    }

    /// <inheritdoc />
    /// <remarks>Always empty: one source is one database, and another source's objects are not classified as its.</remarks>
    public Task<IReadOnlyDictionary<string, DatabaseStoreAccess>> OtherSourcesAsync(
        ResolvedSource resolved,
        DatabaseObjectClassifier classifier,
        bool capabilityEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(classifier);

        return Task.FromResult<IReadOnlyDictionary<string, DatabaseStoreAccess>>(
            new Dictionary<string, DatabaseStoreAccess>(StringComparer.OrdinalIgnoreCase));
    }

    private DatabaseDeclarations Declarations()
    {
        List<string> own = [];
        foreach (string entry in settings.BrowsableSchemas)
        {
            if (entry != BrowsableSchemaMatcher.Everything && !own.Contains(entry, StringComparer.Ordinal))
            {
                own.Add(entry);
            }
        }

        return new DatabaseDeclarations(new DatabaseObjectClassifier([], hidesDocumentTypes: false), own, null);
    }
}
