using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Database;

/// <summary>
/// One visitor's database-browser gate for one resolved scope: which schemas they may see, and whose
/// rows and definitions they may read. Pure - it decides, and <see cref="DatabaseAccess" /> builds it.
/// </summary>
/// <remarks>
/// <para>
/// The levels (plan §1, "Gate levels"):
/// </para>
/// <list type="bullet">
/// <item><description>
/// The <b>structure</b> of the store's own schemas - names, kinds, owners, columns, keys, indexes, foreign
/// keys, trigger names, estimates - is visible without the capability. It always has been: the Schema
/// screen shows it.
/// </description></item>
/// <item><description>
/// <b>Other schemas</b> are visible only with the capability, the policy's yes, and a schema
/// <see cref="MartenStudioOptions.BrowsableSchemas" /> admits. Withheld ones are counted, never named.
/// </description></item>
/// <item><description>
/// <b>Definitions</b> of objects Marten does not own, and <b>rows</b> of any relation, need the same three
/// - in the store's own schemas too: the store's schemas give structure for free and nothing more.
/// Marten's own definitions in the store's schemas stay visible, as the Schema screen shows them.
/// </description></item>
/// <item><description>
/// Rows of a Marten document or event table, or of Marten's infrastructure, are <b>never</b> read here;
/// a foreign table's rows never; a view reading a hidden type's table, never.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class DatabaseGate
{
    private readonly HashSet<string> storeSchemas;
    private readonly HashSet<string> browsable;
    private readonly DatabaseRefusal closedBy;
    private readonly string? closedDenial;
    private readonly IReadOnlyList<string> configuredEntries;
    private readonly string? sqlConsoleRole;

    /// <summary>Builds the gate from what the service has established.</summary>
    /// <param name="storeSchemas">The store's own schemas, declaration order.</param>
    /// <param name="liveSchemas">The database's schemas, as the reading role sees them.</param>
    /// <param name="configuredEntries"><see cref="MartenStudioOptions.BrowsableSchemas" />.</param>
    /// <param name="capabilityEnabled">Whether <c>BrowseDatabase</c> is on and <c>ReadOnly</c> is off.</param>
    /// <param name="readOnly"><see cref="MartenStudioOptions.ReadOnly" />.</param>
    /// <param name="authorized">The policy's answer; <see langword="null" /> when not asked.</param>
    /// <param name="classifier">Whose each object is.</param>
    /// <param name="sqlConsoleRole">The role reads run as, for the privilege sentence.</param>
    public DatabaseGate(
        IReadOnlyList<string> storeSchemas,
        IReadOnlyList<CatalogSchema> liveSchemas,
        IReadOnlyList<string> configuredEntries,
        bool capabilityEnabled,
        bool readOnly,
        bool? authorized,
        DatabaseObjectClassifier classifier,
        string? sqlConsoleRole = null)
    {
        ArgumentNullException.ThrowIfNull(storeSchemas);
        ArgumentNullException.ThrowIfNull(liveSchemas);
        ArgumentNullException.ThrowIfNull(configuredEntries);
        ArgumentNullException.ThrowIfNull(classifier);

        Classifier = classifier;
        this.configuredEntries = configuredEntries;
        this.sqlConsoleRole = sqlConsoleRole;
        this.storeSchemas = new HashSet<string>(storeSchemas, StringComparer.Ordinal);

        (closedBy, closedDenial) = readOnly
            ? (DatabaseRefusal.ReadOnly, ReadOnlyDenial)
            : !capabilityEnabled
                ? (DatabaseRefusal.CapabilityOff, CapabilityDenial)
                : authorized != true
                    ? (DatabaseRefusal.WritePolicy, WritePolicyDenial)
                    : (DatabaseRefusal.None, null);

        browsable = closedBy == DatabaseRefusal.None
            ? new HashSet<string>(BrowsableSchemaMatcher.Match(configuredEntries, liveSchemas), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        HashSet<string> live = new(liveSchemas.Select(static x => x.Name), StringComparer.Ordinal);

        // The store's own schemas first, in declaration order, and only those that exist; then what
        // BrowsableSchemas admits, alphabetically. A declared schema Marten has not created yet has nothing
        // in it to show.
        List<string> visible = [.. storeSchemas.Where(live.Contains).Distinct(StringComparer.Ordinal)];
        visible.AddRange(browsable.Where(x => !this.storeSchemas.Contains(x)).Order(StringComparer.Ordinal));
        VisibleSchemas = visible;

        HashSet<string> shown = new(visible, StringComparer.Ordinal);
        WithheldSchemaCount = liveSchemas.Count(x => !BrowsableSchemaMatcher.IsSystemSchema(x.Name) && !shown.Contains(x.Name));

        DatabaseRefusal stateRefusal = closedBy != DatabaseRefusal.None
            ? closedBy
            : configuredEntries.Count == 0 ? DatabaseRefusal.SchemaNotBrowsable : DatabaseRefusal.None;

        string? stateDenial = closedBy != DatabaseRefusal.None
            ? closedDenial
            : configuredEntries.Count == 0 ? EmptyListDenial : null;

        State = new DatabaseAccessState(
            capabilityEnabled && !readOnly,
            readOnly,
            authorized,
            configuredEntries.Count > 0,
            [.. storeSchemas],
            [.. browsable.Order(StringComparer.Ordinal)],
            WithheldSchemaCount,
            stateRefusal,
            stateDenial);
    }

    /// <summary>What <see cref="MartenStudioOptions.ReadOnly" /> says.</summary>
    public const string ReadOnlyDenial =
        "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.BrowseDatabase off " +
        "with every other capability.";

    /// <summary>What a capability that is off says.</summary>
    public const string CapabilityDenial =
        "Other schemas, and the rows and definitions of objects Marten does not own, need " +
        "MartenStudioOptions.Capabilities.BrowseDatabase.";

    /// <summary>What the write policy's refusal says: the account, and the policy that answered for it.</summary>
    public const string WritePolicyDenial =
        "Your account may not browse the database here. The write policy (MartenStudioOptions." +
        "WriteAuthorizationPolicy, or StoreAuthorizationPolicy when no write policy is set) refused " +
        "BrowseDatabase for this store and database as a whole, with no tenant selected.";

    /// <summary>What an empty <see cref="MartenStudioOptions.BrowsableSchemas" /> says.</summary>
    public const string EmptyListDenial =
        "MartenStudioOptions.BrowsableSchemas is empty, so no schema's non-Marten rows or definitions are " +
        "browsable. List the schema names, or \"*\".";

    /// <summary>Whose each object is.</summary>
    public DatabaseObjectClassifier Classifier { get; }

    /// <summary>The per-visitor state, as a page draws it.</summary>
    public DatabaseAccessState State { get; }

    /// <summary>
    /// The schemas whose structure this visitor may see: the store's own that exist, then the ones
    /// <see cref="MartenStudioOptions.BrowsableSchemas" /> admits while the gate is open. Every catalog read
    /// is filtered to exactly these.
    /// </summary>
    public IReadOnlyList<string> VisibleSchemas { get; }

    /// <summary>How many schemas exist that this visitor is not shown.</summary>
    public int WithheldSchemaCount { get; }

    /// <summary>Whether the capability and the policy both let this visitor past the store's own schemas.</summary>
    public bool IsOpen => closedBy == DatabaseRefusal.None;

    /// <summary>Whether the store declares <paramref name="schema" />.</summary>
    public bool IsStoreSchema(string schema) => storeSchemas.Contains(schema);

    /// <summary>Whether this visitor may see <paramref name="schema" />'s structure.</summary>
    public bool CanSeeStructure(string schema) => IsStoreSchema(schema) || (IsOpen && browsable.Contains(schema));

    /// <summary>
    /// Whether this visitor may read non-Marten data - rows and definitions - in <paramref name="schema" />,
    /// and the sentence when not.
    /// </summary>
    public DatabaseRowAccess DataAccess(string schema)
    {
        if (!IsOpen)
        {
            return DatabaseRowAccess.Refused(closedBy, closedDenial!);
        }

        if (browsable.Contains(schema))
        {
            return DatabaseRowAccess.Granted;
        }

        return DatabaseRowAccess.Refused(DatabaseRefusal.SchemaNotBrowsable, SchemaDenial(schema, configuredEntries));
    }

    /// <summary>Whether this visitor may read the definition of an object in <paramref name="schema" />.</summary>
    /// <remarks>
    /// Marten's own objects in the store's own schemas are what the Schema screen already shows; anything
    /// else is the host's code, and reading it is a <c>BrowseDatabase</c> read.
    /// </remarks>
    public DatabaseRowAccess DefinitionAccess(string schema, DatabaseObjectOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        return IsStoreSchema(schema) && ownership.IsMarten ? DatabaseRowAccess.Granted : DataAccess(schema);
    }

    /// <summary>Whether this visitor may read <paramref name="relation" />'s rows, and the sentence when not.</summary>
    /// <param name="relation">The relation, as the catalog has it.</param>
    /// <param name="ownership">Whose it is.</param>
    /// <param name="dependsOnHidden">For a view, whether anything under it is a hidden type's table.</param>
    public DatabaseRowAccess RowsFor(CatalogRelation relation, DatabaseObjectOwnership ownership, bool dependsOnHidden)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(ownership);

        switch (ownership.Owner)
        {
            case DatabaseObjectOwner.MartenDocument:
                return DatabaseRowAccess.Refused(
                    DatabaseRefusal.MartenOwned,
                    "A Marten document table's rows are browsed in Documents, where tenancy, soft delete and " +
                    "the store's serializer apply - never raw.");

            case DatabaseObjectOwner.MartenEventStore:
                return DatabaseRowAccess.Refused(
                    DatabaseRefusal.MartenOwned,
                    "The event store's tables are browsed in Streams, the Feed and Projections, where tenancy " +
                    "and archiving apply - never raw.");

            case DatabaseObjectOwner.MartenInfrastructure:
                return DatabaseRowAccess.Refused(
                    DatabaseRefusal.MartenOwned,
                    "Marten's own bookkeeping (every mt_ object, mt_tenant_databases' connection strings " +
                    "included) is never browsed raw.");
        }

        if (relation.Kind == "f")
        {
            return DatabaseRowAccess.Refused(
                DatabaseRefusal.ForeignTable,
                "A foreign table is listed, never read: reading it would reach the remote server" +
                (relation.ForeignServer is { } server ? " '" + server + "'" : string.Empty) + ".");
        }

        if (dependsOnHidden)
        {
            return DatabaseRowAccess.Refused(
                DatabaseRefusal.HiddenDependency,
                "This view reads a table whose document type the host hides from the studio " +
                "(MartenStudioOptions.IsDocumentTypeVisible), so its rows are not shown.");
        }

        if (!DatabaseCatalogQueries.IsQuotable(relation.Schema) || !DatabaseCatalogQueries.IsQuotable(relation.Name))
        {
            return DatabaseRowAccess.Refused(
                DatabaseRefusal.Unquotable,
                "Its name cannot be put into SQL safely (it contains a double quote), so the studio lists it " +
                "and never reads it.");
        }

        DatabaseRowAccess data = DataAccess(relation.Schema);
        if (!data.Allowed)
        {
            return data;
        }

        if (!relation.Readable)
        {
            return DatabaseRowAccess.Refused(
                DatabaseRefusal.NoPrivilege,
                sqlConsoleRole is null
                    ? "The store's Postgres role has no SELECT privilege on it."
                    : "The role '" + sqlConsoleRole + "' (MartenStudioOptions.SqlConsoleRole) has no SELECT " +
                      "privilege on it.");
        }

        return DatabaseRowAccess.Granted;
    }

    /// <summary>What a schema outside <see cref="MartenStudioOptions.BrowsableSchemas" /> says.</summary>
    /// <remarks>
    /// Said the same way whether or not the schema exists, so the sentence tells nobody what is there.
    /// </remarks>
    internal static string SchemaDenial(string schema, IReadOnlyList<string> configuredEntries)
    {
        if (BrowsableSchemaMatcher.IsSystemSchema(schema))
        {
            return "'" + schema + "' is a system schema, which the database browser never reads.";
        }

        return configuredEntries.Count == 0
            ? EmptyListDenial
            : "Schema '" + schema + "' is not one MartenStudioOptions.BrowsableSchemas admits (it must be " +
              "listed by exact name, or \"*\" must be listed and the studio's role must have USAGE on it).";
    }
}
