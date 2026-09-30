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
/// a foreign table's rows never - nor those of a partitioned table with a foreign partition, a table with
/// a foreign inheritance child, or a view that reads any of them; a view reading Marten's own tables, or
/// another store's its policies refuse, never; a view reading a hidden type's table, never; a view that
/// reaches into a schema the visitor may not see - a table, a function, a type, a collation, a text search
/// object, the schema's own name as a value - never, nor a table (or a view over one) whose partitions or
/// children sit in such a schema; a view calling a function the studio cannot follow (a body that is
/// source text), while a schema the reading role may read is withheld - never; and, while any type may be
/// hidden, a view that calls such a function at all. A schema an extension owns is neither browsable nor
/// withheld for these purposes.
/// </description></item>
/// <item><description>
/// Another registered store's objects keep their classification, but its identity (store key and
/// collection alias) is shown only to a visitor its store policy passes, and the rows of its
/// Marten-managed tables only to one its store policy and write policy both pass.
/// </description></item>
/// <item><description>
/// Every free-text field - a type, a default, a definition, a comment - is shown with each withheld
/// schema's name masked (<see cref="WithheldNames" />).
/// </description></item>
/// </list>
/// </remarks>
internal sealed class DatabaseGate
{
    private readonly HashSet<string> storeSchemas;
    private readonly HashSet<string> browsable;
    private readonly HashSet<string> withheld;
    private readonly HashSet<string> extensionSchemas;
    private readonly bool readableWithheld;
    private readonly bool anyWithheld;
    private readonly DatabaseRefusal closedBy;
    private readonly string? closedDenial;
    private readonly IReadOnlyList<string> configuredEntries;
    private readonly string? sqlConsoleRole;
    private readonly string? storeKey;
    private readonly IReadOnlyDictionary<string, DatabaseStoreAccess> otherStores;

    /// <summary>Builds the gate from what the service has established.</summary>
    /// <param name="storeSchemas">The store's own schemas, declaration order.</param>
    /// <param name="liveSchemas">The database's schemas, as the reading role sees them.</param>
    /// <param name="configuredEntries"><see cref="MartenStudioOptions.BrowsableSchemas" />.</param>
    /// <param name="capabilityEnabled">Whether <c>BrowseDatabase</c> is on and <c>ReadOnly</c> is off.</param>
    /// <param name="readOnly"><see cref="MartenStudioOptions.ReadOnly" />.</param>
    /// <param name="authorized">The policies' answer; <see langword="null" /> when not asked.</param>
    /// <param name="classifier">Whose each object is.</param>
    /// <param name="sqlConsoleRole">The role reads run as, for the privilege sentence.</param>
    /// <param name="policyRefusal">
    /// When <paramref name="authorized" /> is <see langword="false" />, which policy said no:
    /// <see cref="DatabaseRefusal.StorePolicy" /> or <see cref="DatabaseRefusal.WritePolicy" /> (the default).
    /// </param>
    /// <param name="storeKey">The resolved store's registration key.</param>
    /// <param name="otherStores">
    /// What this visitor may know and read of every other registered store, by key. A store missing from it
    /// is shown as the classifier has it.
    /// </param>
    public DatabaseGate(
        IReadOnlyList<string> storeSchemas,
        IReadOnlyList<CatalogSchema> liveSchemas,
        IReadOnlyList<string> configuredEntries,
        bool capabilityEnabled,
        bool readOnly,
        bool? authorized,
        DatabaseObjectClassifier classifier,
        string? sqlConsoleRole = null,
        DatabaseRefusal policyRefusal = DatabaseRefusal.WritePolicy,
        string? storeKey = null,
        IReadOnlyDictionary<string, DatabaseStoreAccess>? otherStores = null)
    {
        ArgumentNullException.ThrowIfNull(storeSchemas);
        ArgumentNullException.ThrowIfNull(liveSchemas);
        ArgumentNullException.ThrowIfNull(configuredEntries);
        ArgumentNullException.ThrowIfNull(classifier);

        Classifier = classifier;
        this.configuredEntries = configuredEntries;
        this.sqlConsoleRole = sqlConsoleRole;
        this.storeKey = storeKey;
        this.otherStores = otherStores ?? new Dictionary<string, DatabaseStoreAccess>(StringComparer.OrdinalIgnoreCase);
        this.storeSchemas = new HashSet<string>(storeSchemas, StringComparer.Ordinal);

        (closedBy, closedDenial) = readOnly
            ? (DatabaseRefusal.ReadOnly, ReadOnlyDenial)
            : !capabilityEnabled
                ? (DatabaseRefusal.CapabilityOff, CapabilityDenial)
                : authorized != true
                    ? policyRefusal == DatabaseRefusal.StorePolicy
                        ? (DatabaseRefusal.StorePolicy, StorePolicyDenial)
                        : (DatabaseRefusal.WritePolicy, WritePolicyDenial)
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
        withheld = new HashSet<string>(
            liveSchemas.Where(x => !BrowsableSchemaMatcher.IsSystemSchema(x.Name) && !shown.Contains(x.Name)).Select(static x => x.Name),
            StringComparer.Ordinal);
        WithheldSchemaCount = withheld.Count;

        extensionSchemas = new HashSet<string>(liveSchemas.Where(static x => x.OwnedByExtension).Select(static x => x.Name), StringComparer.Ordinal);

        // A withheld schema the reading role has USAGE on: somewhere a function the studio cannot see into
        // could read, as that role, and hand back through a view.
        readableWithheld = liveSchemas.Any(x => x.HasUsage && withheld.Contains(x.Name) && !extensionSchemas.Contains(x.Name));
        anyWithheld = withheld.Any(x => !extensionSchemas.Contains(x));

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

    /// <summary>
    /// What the store policy's refusal says: asked first, for the database as a whole, before the write
    /// policy is asked at all.
    /// </summary>
    public const string StorePolicyDenial =
        "Your account may not browse the database here. The store policy (MartenStudioOptions." +
        "StoreAuthorizationPolicy) refused this store and database as a whole, with no tenant selected.";

    /// <summary>What a view that reaches into a withheld schema says.</summary>
    public const string WithheldDependencyDenial = "It reads from a schema you cannot see.";

    /// <summary>What a view that calls a function says, while any document type may be hidden.</summary>
    public const string OpaqueDependencyDenial =
        "It calls a function; the studio cannot tell what that function reads.";

    /// <summary>What a view over a hidden type's table says.</summary>
    public const string HiddenDependencyDenial =
        "This view reads a table whose document type the host hides from the studio " +
        "(MartenStudioOptions.IsDocumentTypeVisible), so its rows and its query are not shown.";

    /// <summary>What a partitioned table with a foreign partition, or a table with a foreign child, says.</summary>
    public const string ForeignDescendantDenial =
        "One of its partitions (or inheritance children) is a foreign table: reading its rows would reach the " +
        "remote server, so they are never read.";

    /// <summary>What a view whose rows would be read from a foreign table says.</summary>
    public const string ForeignViewDenial =
        "This view reads a foreign table - directly, or through a partition or an inheritance child of a table " +
        "it reads - so reading its rows would reach the remote server, and they are never read.";

    /// <summary>What a relation whose partitions or children sit in a withheld schema says about its rows.</summary>
    public const string WithheldDescendantDenial =
        "Some of its rows are stored in a partition or child table in a schema you cannot see, so they are " +
        "not shown.";

    /// <summary>What a view over such a relation says about its rows.</summary>
    public const string WithheldDescendantViewDenial =
        "This view reads a table some of whose rows are stored in a partition or child table in a schema you " +
        "cannot see, so its rows are not shown.";

    /// <summary>What a view that reads one of Marten's own tables says about its rows.</summary>
    public const string MartenDependencyDenial =
        "This view reads one of Marten's own tables - a document or event table, or Marten's bookkeeping - " +
        "whose rows are never read raw: open them in Documents or Events, where tenancy, soft delete and the " +
        "store's serializer apply.";

    /// <summary>
    /// What a view that calls a function the studio cannot see into says about its rows, while some schema
    /// the reading role may read is withheld from this visitor.
    /// </summary>
    public const string OpaqueWithheldDenial =
        "This view calls a function whose body the studio cannot follow (PL/pgSQL, or SQL in a string), and " +
        "there are schemas the studio's Postgres role can read that you cannot see here: the function could " +
        "read them, so its rows are not shown. A function with a SQL-standard body (BEGIN ATOMIC or RETURN) " +
        "is followed instead.";

    /// <summary>What a view over a document table no store declares says, while hiding is configured.</summary>
    public const string UndeclaredDocumentDenial =
        "This view reads a Marten document table that no registered store declares yet, and the host hides " +
        "some document types from the studio (MartenStudioOptions.IsDocumentTypeVisible), so the studio " +
        "cannot tell whether this one is hidden.";

    /// <summary>What another store's refused store policy says about that store's rows.</summary>
    public const string OtherStorePolicyDenial =
        "It belongs to another Marten store, and the store policy (MartenStudioOptions.StoreAuthorizationPolicy) " +
        "refused your account that store, with no tenant selected.";

    /// <summary>What another store's refused write policy says about that store's rows.</summary>
    public const string OtherStoreWritePolicyDenial =
        "It belongs to another Marten store, and the write policy (MartenStudioOptions.WriteAuthorizationPolicy, " +
        "or StoreAuthorizationPolicy when no write policy is set) refused BrowseDatabase for that store, with " +
        "no tenant selected.";

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
    /// Whether <paramref name="schema" /> is one this visitor is not told about: not a system schema, and
    /// not one whose structure they may see. A schema created after the schema list was read counts - it
    /// is not visible, so it is withheld.
    /// </summary>
    public bool IsWithheld(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return !BrowsableSchemaMatcher.IsSystemSchema(schema) && !CanSeeStructure(schema);
    }

    /// <summary>
    /// Whether a view, or a table's partition, that reaches into <paramref name="schema" /> reaches into a
    /// schema withheld from this visitor: <see cref="IsWithheld" />, except for a schema an extension owns
    /// (<c>cron</c>, TimescaleDB's internals). Those are never browsable - <c>"*"</c> leaves them out - and
    /// never withheld either, for this purpose: a view over <c>cron.job</c> or a hypertable whose chunks live
    /// in <c>_timescaledb_internal</c> is somebody's object that the reading role may read, and refusing it
    /// would refuse it under <c>"*"</c> too. The name is still masked in text.
    /// </summary>
    public bool IsWithheldDependency(string schema) => IsWithheld(schema) && !extensionSchemas.Contains(schema);

    /// <summary>The names of the live schemas this visitor is not told about - what <see cref="Redact" /> masks.</summary>
    public IReadOnlySet<string> WithheldSchemas => withheld;

    /// <summary>SQL, or a fragment of it, with every withheld schema's name masked.</summary>
    public string? Redact(string? text) => WithheldNames.Redact(text, withheld);

    /// <summary>Free text - a comment - with every withheld schema's name masked.</summary>
    public string? RedactText(string? text) => WithheldNames.RedactText(text, withheld);

    /// <summary>A routine's <c>SET</c> clauses with every withheld schema's name masked.</summary>
    public IReadOnlyList<string> RedactConfig(IReadOnlyList<string> config) => WithheldNames.RedactConfig(config, withheld);

    /// <summary>
    /// <paramref name="ownership" /> as this visitor may see it: another store's key and alias blanked
    /// when that store's policy refuses them - still Marten's, so still never read raw.
    /// </summary>
    public DatabaseObjectOwnership Present(DatabaseObjectOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        return ownership.StoreKey is { } key
            && IsOtherStore(key)
            && otherStores.TryGetValue(key, out DatabaseStoreAccess? access)
            && !access.IdentityVisible
                ? ownership with { StoreKey = null, Alias = null }
                : ownership;
    }

    /// <summary>
    /// Why a view's query and rows may not be shown, from everything it reads and refers to - or
    /// <see langword="null" /> when nothing it touches is a reason. Name and owner play no part: an
    /// <c>mt_</c>-named view over a hidden type's table is refused like any other.
    /// </summary>
    /// <param name="dependencies">The relations it reads, through other views and followed functions too.</param>
    /// <param name="references">
    /// The functions, operators, sequences, types, collations, text search objects and schemas it refers to.
    /// </param>
    public DatabaseRowAccess? ViewRefusal(
        IEnumerable<CatalogViewDependency> dependencies,
        IEnumerable<CatalogViewReference> references)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(references);

        bool hidden = false;
        bool undeclared = false;
        bool reachesWithheld = false;
        bool callsCode = false;

        foreach (CatalogViewDependency dependency in dependencies)
        {
            hidden |= Classifier.IsHiddenTable(dependency.Schema, dependency.Name);
            undeclared |= Classifier.MayHideDocumentTypes && Classifier.IsUndeclaredDocumentTable(dependency.Schema, dependency.Name);
            reachesWithheld |= IsWithheldDependency(dependency.Schema);
        }

        foreach (CatalogViewReference reference in references)
        {
            reachesWithheld |= IsWithheldDependency(reference.Schema);
            callsCode |= reference.Kind == "f" && reference.UserCode;
        }

        if (hidden)
        {
            return DatabaseRowAccess.Refused(DatabaseRefusal.HiddenDependency, HiddenDependencyDenial);
        }

        if (undeclared)
        {
            return DatabaseRowAccess.Refused(DatabaseRefusal.HiddenDependency, UndeclaredDocumentDenial);
        }

        if (reachesWithheld)
        {
            return DatabaseRowAccess.Refused(DatabaseRefusal.WithheldDependency, WithheldDependencyDenial);
        }

        if (callsCode && Classifier.MayHideDocumentTypes)
        {
            return DatabaseRowAccess.Refused(DatabaseRefusal.HiddenDependency, OpaqueDependencyDenial);
        }

        return null;
    }

    private bool IsOtherStore(string key) =>
        storeKey is not null && !string.Equals(key, storeKey, StringComparison.OrdinalIgnoreCase);

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
    public DatabaseRowAccess RowsFor(CatalogRelation relation, DatabaseObjectOwnership ownership, bool dependsOnHidden) =>
        RowsFor(
            relation,
            ownership,
            dependsOnHidden ? DatabaseRowAccess.Refused(DatabaseRefusal.HiddenDependency, HiddenDependencyDenial) : null);

    /// <summary>Whether this visitor may read <paramref name="relation" />'s rows, and the sentence when not.</summary>
    /// <param name="relation">The relation, as the catalog has it.</param>
    /// <param name="ownership">Whose it is, as the classifier has it - never a <see cref="Present" />ed copy.</param>
    /// <param name="viewRefusal">
    /// For a view, what <see cref="ViewRefusal" /> said about what it reads, or <see langword="null" />. It is
    /// checked first, whatever the view is called or whoever owns it.
    /// </param>
    /// <param name="viewReads">
    /// For a view or materialized view, the relations it reads (<see cref="CatalogViewDependency" />): what
    /// decides whether reading its rows would read a foreign table's, Marten's own, another store's, or rows
    /// stored in a withheld schema.
    /// </param>
    /// <param name="viewReferences">
    /// For a view or materialized view, the functions and other objects it refers to: what decides whether it
    /// calls code the studio cannot see into while something the reading role may read is withheld.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Foreign rows are never reached indirectly.</b> A foreign table is refused; so is a partitioned
    /// table with a foreign partition, a table with a foreign inheritance child, and a view that reads any of
    /// the three - every one of them reads the remote server when it is read. A materialized view is not: its
    /// rows are stored here, and a view reaching a foreign table only through one reads nothing remote.
    /// </para>
    /// <para>
    /// <b>Nor are Marten's own rows, or another store's.</b> A view (or materialized view) that reads a
    /// Marten document or event table or Marten's bookkeeping is refused its rows as the table itself is,
    /// and one that reads another store's table is refused whatever that store's own policies refuse. A
    /// view over one of this store's projection or extended tables - relational data Marten manages - is not.
    /// </para>
    /// <para>
    /// Only the rows: a view's query names what it reads, and what it reads here is listed anyway. The
    /// facts that are about its query too - a hidden type's table, a withheld schema - are
    /// <see cref="ViewRefusal" />'s.
    /// </para>
    /// </remarks>
    public DatabaseRowAccess RowsFor(
        CatalogRelation relation,
        DatabaseObjectOwnership ownership,
        DatabaseRowAccess? viewRefusal,
        IReadOnlyList<CatalogViewDependency>? viewReads = null,
        IReadOnlyList<CatalogViewReference>? viewReferences = null)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(ownership);

        if (viewRefusal is { Allowed: false })
        {
            return viewRefusal;
        }

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

        if (ForeignReach(relation, viewReads) is { } foreign)
        {
            return foreign;
        }

        if (MartenReach(relation, viewReads) is { } marten)
        {
            return marten;
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

        // Another store's Marten-managed table: this store's tenant-less grant says nothing about that
        // store, so its own policies are asked (by DatabaseAccess, into otherStores) - and a store nobody
        // asked about is refused rather than assumed. A view reading one is the same read.
        if (OtherStoreRows(ownership) is { } otherStore)
        {
            return otherStore;
        }

        if (relation.Kind is "v" or "m" && viewReads is not null)
        {
            foreach (CatalogViewDependency read in viewReads)
            {
                if (Classifier.ClassifyRelation(read.Schema, read.Name) is { } readOwnership && OtherStoreRows(readOwnership) is { } refused)
                {
                    return refused;
                }
            }
        }

        if (WithheldReach(relation, viewReads) is { } withheldRows)
        {
            return withheldRows;
        }

        if (OpaqueReach(relation, viewReferences) is { } opaque)
        {
            return opaque;
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

    /// <summary>
    /// Why reading <paramref name="relation" />'s rows would read a foreign table's - a partitioned table
    /// with a foreign partition, a table with a foreign inheritance child, or a view reading one of those or
    /// a foreign table directly - or <see langword="null" />. A path through a materialized view does not
    /// count: its rows are stored here.
    /// </summary>
    /// <param name="relation">The relation, as the catalog has it.</param>
    /// <param name="viewReads">For a view, what it reads; ignored for anything else.</param>
    internal static DatabaseRowAccess? ForeignReach(CatalogRelation relation, IEnumerable<CatalogViewDependency>? viewReads)
    {
        ArgumentNullException.ThrowIfNull(relation);

        if (relation.Kind is "p" or "r" && relation.ForeignDescendant)
        {
            return DatabaseRowAccess.Refused(DatabaseRefusal.ForeignTable, ForeignDescendantDenial);
        }

        if (relation.Kind != "v" || viewReads is null)
        {
            return null;
        }

        foreach (CatalogViewDependency read in viewReads)
        {
            if (!read.ThroughMaterializedView && (read.Kind == "f" || (read.Kind is "p" or "r" && read.ForeignDescendant)))
            {
                return DatabaseRowAccess.Refused(DatabaseRefusal.ForeignTable, ForeignViewDenial);
            }
        }

        return null;
    }

    /// <summary>
    /// Why a view's or materialized view's rows would be Marten's own rows, read raw - it reads a document
    /// or event table, or Marten's bookkeeping, directly or through anything - or <see langword="null" />.
    /// A projection's or an extended table is Marten-managed relational data, and not a reason.
    /// </summary>
    private DatabaseRowAccess? MartenReach(CatalogRelation relation, IReadOnlyList<CatalogViewDependency>? viewReads)
    {
        if (relation.Kind is not ("v" or "m") || viewReads is null)
        {
            return null;
        }

        foreach (CatalogViewDependency read in viewReads)
        {
            if (Classifier.ClassifyRelation(read.Schema, read.Name) is
                { Owner: DatabaseObjectOwner.MartenDocument or DatabaseObjectOwner.MartenEventStore or DatabaseObjectOwner.MartenInfrastructure })
            {
                return DatabaseRowAccess.Refused(DatabaseRefusal.MartenOwned, MartenDependencyDenial);
            }
        }

        return null;
    }

    /// <summary>
    /// What another store's own policies say about the rows of an object <paramref name="ownership" /> says is
    /// that store's - or <see langword="null" /> when it is this store's, nobody's, or that store allows them.
    /// </summary>
    private DatabaseRowAccess? OtherStoreRows(DatabaseObjectOwnership ownership)
    {
        if (ownership.StoreKey is not { } key || !IsOtherStore(key))
        {
            return null;
        }

        DatabaseRowAccess other = otherStores.TryGetValue(key, out DatabaseStoreAccess? access)
            ? access.Rows
            : DatabaseRowAccess.Refused(DatabaseRefusal.StorePolicy, OtherStorePolicyDenial);

        return other.Allowed ? null : other;
    }

    /// <summary>
    /// Why <paramref name="relation" />'s rows would show rows stored in a schema this visitor may not see -
    /// a partition or inheritance child there, of the relation or of a table a view reads - or
    /// <see langword="null" />. A materialized view counts too: its rows were copied out of those tables.
    /// </summary>
    private DatabaseRowAccess? WithheldReach(CatalogRelation relation, IReadOnlyList<CatalogViewDependency>? viewReads)
    {
        if (relation.Kind is "p" or "r" && relation.DescendantSchemas is { } own && own.Any(IsWithheldDependency))
        {
            return DatabaseRowAccess.Refused(DatabaseRefusal.WithheldDependency, WithheldDescendantDenial);
        }

        if (relation.Kind is not ("v" or "m") || viewReads is null)
        {
            return null;
        }

        foreach (CatalogViewDependency read in viewReads)
        {
            if (read.Kind is "p" or "r" && read.DescendantSchemas is { } schemas && schemas.Any(IsWithheldDependency))
            {
                return DatabaseRowAccess.Refused(DatabaseRefusal.WithheldDependency, WithheldDescendantViewDenial);
            }
        }

        return null;
    }

    /// <summary>
    /// Why a view's rows could hold whatever a function the studio cannot see into read from a withheld
    /// schema, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// A function whose body is source text - PL/pgSQL, or SQL in a string - records no dependency on what it
    /// reads, so a view calling it can hand back a withheld schema's rows while looking like a view over
    /// nothing. It is refused whenever the reading role could read a withheld schema at all (it has
    /// <c>USAGE</c> on one), and - when the function is <c>SECURITY DEFINER</c>, and so reads as its owner -
    /// whenever any schema is withheld. The residual is D27's: under <c>"*"</c> with a <c>SqlConsoleRole</c>
    /// narrowed to the browsable schemas, nothing is withheld that the role may read, and such a view is
    /// read; what its function reads is then limited by the role, as every other read is.
    /// </remarks>
    private DatabaseRowAccess? OpaqueReach(CatalogRelation relation, IReadOnlyList<CatalogViewReference>? viewReferences)
    {
        if (relation.Kind is not ("v" or "m") || viewReferences is null || (!readableWithheld && !anyWithheld))
        {
            return null;
        }

        foreach (CatalogViewReference reference in viewReferences)
        {
            if (reference.Kind == "f" && reference.UserCode && (readableWithheld || (reference.SecurityDefiner && anyWithheld)))
            {
                return DatabaseRowAccess.Refused(DatabaseRefusal.WithheldDependency, OpaqueWithheldDenial);
            }
        }

        return null;
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
