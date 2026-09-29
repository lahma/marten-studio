using MartenStudio.Services.Schema;

namespace MartenStudio.Services.Database;

/// <summary>One registered store's declarations, and the key it is registered under.</summary>
/// <param name="StoreKey">The registration key.</param>
/// <param name="Declarations">What its configuration declares.</param>
internal sealed record StoreDeclarations(string StoreKey, SchemaDeclarations Declarations);

/// <summary>
/// Says whose each database object is - Marten's, in which of four ways, or somebody else's - and which
/// objects the browser must not show at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every registered store at once.</b> The classifier is built from the declarations of every store the
/// registry can build (<see cref="MartenStoreRegistry.Registrations(bool)" /> with ancillary stores
/// included, whatever <see cref="MartenStudioOptions.IncludeAncillaryStores" /> says), because another
/// store's table in a shared database is Marten's table, and labelling it "Other" would hand its rows to a
/// raw reader that knows nothing of its tenancy or soft delete. The declarations are read from options
/// only; no store's databases are enumerated. When two stores declare the same object the first
/// registration wins - the default store first, as the registry orders them.
/// </para>
/// <para>
/// <b>A hidden type is not "Other", it is absent.</b> A table whose document type
/// <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> hides answers <see langword="null" />, and so
/// does everything that is only about it - its triggers, and a sequence one of its columns owns. Its
/// indexes and partitions never reach a list anyway (indexes are read per relation, partitions are rolled
/// up), and the foreign keys into and out of it are dropped where they are assembled. A view that reads it
/// is still listed, because the view is somebody's object, but its rows are refused
/// (<see cref="DatabaseRefusal.HiddenDependency" />).
/// </para>
/// <para>
/// <b>The order of the rules is the design.</b> A declared document table (visible or hidden) and a
/// declared event table are recognised first; then any <c>mt_</c>-prefixed name in any schema is Marten's
/// infrastructure - <c>mt_hilo</c>, <c>mt_tenant_databases</c> (which holds tenant connection strings),
/// <c>mt_tenant_partitions</c>, an <c>mt_doc_*</c> table no store registers, the <c>mt_</c> functions and
/// sequences; then a declared projection or extended object. What is left is "Other", with an optional
/// hint recognised from the name. Matching is case-insensitive, which errs towards "Marten's" - the safe
/// direction for a rule whose job is to keep Marten's rows from being read raw.
/// </para>
/// <para>
/// <b>What it cannot see.</b> A feature added with <c>StoreOptions.Storage.Add(IFeatureSchema)</c> is
/// reachable only through Marten's internal <c>AllActiveFeatures</c>, which applies migrations (hard rule
/// 14). Its objects are Marten's here only if their names start with <c>mt_</c>.
/// </para>
/// </remarks>
internal sealed class DatabaseObjectClassifier
{
    /// <summary>The prefix every Marten-created object name carries.</summary>
    internal const string MartenPrefix = "mt_";

    /// <summary>The event store's global sequence; its per-tenant siblings carry a tenant id after it.</summary>
    internal const string EventSequence = "mt_events_sequence";

    /// <summary>The prefix of every Marten document table's name.</summary>
    internal const string DocumentTablePrefix = "mt_doc_";

    private readonly Dictionary<string, (string StoreKey, MartenDeclaredObject Declared)> declared =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> hiddenTables = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool hidesDocumentTypes;

    /// <summary>Builds the classifier from every store's declarations.</summary>
    /// <param name="stores">Every registered store's declarations, the default store first.</param>
    /// <param name="hidesDocumentTypes">
    /// Whether the host set <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> at all - which means a
    /// document type Marten has not learned yet may be one it hides (see <see cref="MayHideDocumentTypes" />).
    /// </param>
    public DatabaseObjectClassifier(IReadOnlyList<StoreDeclarations> stores, bool hidesDocumentTypes = false)
    {
        ArgumentNullException.ThrowIfNull(stores);

        Stores = stores;
        this.hidesDocumentTypes = hidesDocumentTypes;

        foreach (StoreDeclarations store in stores)
        {
            foreach ((string key, MartenDeclaredObject value) in store.Declarations.Objects)
            {
                if (value is { Kind: MartenObjectKind.DocumentTable, Visible: false })
                {
                    // Hidden in any store is hidden: one store's visible type and another's hidden type
                    // sharing a table is a configuration nobody means, and the conservative answer wins.
                    hiddenTables.Add(key);
                }

                declared.TryAdd(key, (store.StoreKey, value));
            }
        }
    }

    /// <summary>The stores it was built from.</summary>
    public IReadOnlyList<StoreDeclarations> Stores { get; }

    /// <summary>Every hidden document type's table, as <c>schema.table</c>.</summary>
    public IReadOnlyCollection<string> HiddenTables => hiddenTables;

    /// <summary>
    /// Whether any document type may be hidden from the studio: one is known to be, or the host set
    /// <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> at all.
    /// </summary>
    /// <remarks>
    /// The second half matters because Marten learns document types lazily: a type that was never
    /// registered with <c>Schema.For&lt;T&gt;()</c> and that nothing in this process has touched yet is not
    /// among <c>AllKnownDocumentTypes()</c>, so its table is not known to be hidden even when it is. Every
    /// rule that exists only to protect hidden types - a view that calls a function, a view over a document
    /// table nobody declared - applies whenever hiding is configured, and not only when a hidden type is
    /// already known.
    /// </remarks>
    public bool MayHideDocumentTypes => hidesDocumentTypes || hiddenTables.Count > 0;

    /// <summary>Whether <paramref name="schema" />.<paramref name="name" /> is a hidden document type's table.</summary>
    public bool IsHiddenTable(string schema, string name) => hiddenTables.Contains(SchemaKey.For(schema, name));

    /// <summary>
    /// Whether <paramref name="schema" />.<paramref name="name" /> is named like a Marten document table
    /// that no registered store declares - a stale table, or a type Marten has not learned yet.
    /// </summary>
    public bool IsUndeclaredDocumentTable(string schema, string name) =>
        name.StartsWith(DocumentTablePrefix, StringComparison.OrdinalIgnoreCase)
        && !declared.ContainsKey(SchemaKey.For(schema, name));

    /// <summary>
    /// Whose one relation is, or <see langword="null" /> when the browser must not show it at all.
    /// </summary>
    public DatabaseObjectOwnership? ClassifyRelation(string schema, string name)
    {
        string key = SchemaKey.For(schema, name);

        if (hiddenTables.Contains(key))
        {
            return null;
        }

        if (declared.TryGetValue(key, out var found))
        {
            switch (found.Declared.Kind)
            {
                case MartenObjectKind.DocumentTable:
                    return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, found.StoreKey, found.Declared.Alias);

                case MartenObjectKind.EventTable:
                    return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenEventStore, found.StoreKey);
            }
        }

        if (IsMartenName(name))
        {
            return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure, found.StoreKey);
        }

        if (found.Declared is { Kind: MartenObjectKind.Infrastructure })
        {
            return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure, found.StoreKey);
        }

        if (found.Declared is { Kind: MartenObjectKind.ProjectionOrExtendedTable })
        {
            return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenProjectionOrExtended, found.StoreKey);
        }

        return Other(schema, name);
    }

    /// <summary>Whose one function, procedure, aggregate or window function is.</summary>
    public DatabaseObjectOwnership ClassifyRoutine(string schema, string name)
    {
        declared.TryGetValue(SchemaKey.For(schema, name), out var found);

        if (IsMartenName(name))
        {
            return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure, found.StoreKey);
        }

        return found.Declared is { Kind: MartenObjectKind.Function }
            ? new DatabaseObjectOwnership(DatabaseObjectOwner.MartenProjectionOrExtended, found.StoreKey)
            : Other(schema, name);
    }

    /// <summary>
    /// Whose one sequence is, or <see langword="null" /> when the browser must not list it: a per-tenant
    /// event sequence (its name is a tenant id), or a sequence owned by a hidden type's column.
    /// </summary>
    /// <param name="schema">The sequence's schema.</param>
    /// <param name="name">The sequence's name.</param>
    /// <param name="ownerSchema">The owning table's schema, when a column owns it.</param>
    /// <param name="ownerTable">The owning table, when a column owns it.</param>
    public DatabaseObjectOwnership? ClassifySequence(string schema, string name, string? ownerSchema, string? ownerTable)
    {
        if (IsPerTenantEventSequence(name))
        {
            return null;
        }

        if (ownerSchema is not null && ownerTable is not null && IsHiddenTable(ownerSchema, ownerTable))
        {
            return null;
        }

        declared.TryGetValue(SchemaKey.For(schema, name), out var found);

        if (IsMartenName(name))
        {
            return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure, found.StoreKey);
        }

        if (found.Declared is { Kind: MartenObjectKind.Sequence })
        {
            return new DatabaseObjectOwnership(DatabaseObjectOwner.MartenProjectionOrExtended, found.StoreKey);
        }

        // An identity or serial column's sequence belongs with its table: one on a projection's table is
        // Marten-managed, one on the host's table is the host's.
        if (ownerSchema is not null && ownerTable is not null
            && ClassifyRelation(ownerSchema, ownerTable) is { IsMarten: true } owner)
        {
            return owner with { Alias = null };
        }

        return Other(schema, name);
    }

    /// <summary>
    /// Whose one trigger is - by its own name, not its table's, since a trigger a host put on a Marten
    /// table is the host's - or <see langword="null" /> when its table is hidden.
    /// </summary>
    public DatabaseObjectOwnership? ClassifyTrigger(string schema, string table, string name)
    {
        if (IsHiddenTable(schema, table))
        {
            return null;
        }

        return IsMartenName(name)
            ? new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure)
            : Other(schema, name);
    }

    /// <summary>Whose one type is. Marten declares no types of its own, so only the <c>mt_</c> rule applies.</summary>
    public static DatabaseObjectOwnership ClassifyType(string schema, string name) =>
        IsMartenName(name)
            ? new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure)
            : Other(schema, name);

    /// <summary>Whether a sequence name is one of <c>mt_events_sequence_&lt;tenant&gt;</c>.</summary>
    public static bool IsPerTenantEventSequence(string name) =>
        name.Length > EventSequence.Length + 1
        && name.StartsWith(EventSequence + "_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a name carries Marten's prefix.</summary>
    public static bool IsMartenName(string name) => name.StartsWith(MartenPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A neutral "recognised by name" hint for an object nobody here owns, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// A guess from a naming convention, shown as one: nothing about the object is changed by it, and a
    /// table that happens to be called <c>qrtz_triggers</c> is labelled as if Quartz.NET made it.
    /// </remarks>
    public static string? RecognisedAs(string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);

        if (string.Equals(schema, "hangfire", StringComparison.OrdinalIgnoreCase))
        {
            return "Hangfire";
        }

        if (name.StartsWith("qrtz_", StringComparison.OrdinalIgnoreCase))
        {
            return "Quartz.NET";
        }

        if (name.StartsWith("wolverine_", StringComparison.OrdinalIgnoreCase))
        {
            return "Wolverine";
        }

        if (string.Equals(name, "__EFMigrationsHistory", StringComparison.OrdinalIgnoreCase))
        {
            return "EF Core";
        }

        if (string.Equals(name, "flyway_schema_history", StringComparison.OrdinalIgnoreCase))
        {
            return "Flyway";
        }

        return null;
    }

    private static DatabaseObjectOwnership Other(string schema, string name) =>
        new(DatabaseObjectOwner.Other, RecognisedAs: RecognisedAs(schema, name));
}
