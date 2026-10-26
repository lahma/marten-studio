using System.Globalization;

using MartenStudio.Services.Health.Checks;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// What one sweep of a Marten store reads of the catalog besides the tables its store declares (issue #8, G1): which
/// schemas, which declared tables to leave out of the list, and how many tables at most.
/// </summary>
/// <param name="StoreSchemas">The store's own schemas (<c>SchemaDeclarations.Schemas</c>), declaration order.</param>
/// <param name="Names">
/// The schemas read by name: the store's own, and - only while <c>Capabilities.BrowseDatabase</c> is on and
/// <c>ReadOnly</c> is off - the ones <c>BrowsableSchemas</c> names exactly.
/// </param>
/// <param name="Everything">Whether <c>"*"</c> is configured and the database browser is open, so every schema it admits is read.</param>
/// <param name="Declared">
/// The tables registered stores declare and this read leaves out: the store's own visible declarations, which are judged
/// from the configuration, and every other store's visible ones, which are not this sweep's to judge. Never a hidden
/// document type's table: those are left out by <paramref name="SkipDocumentTables" />.
/// </param>
/// <param name="SkipDocumentTables">
/// Whether a document type may be hidden (<c>DatabaseObjectClassifier.MayHideDocumentTypes</c>), in which case no table
/// named like a document table is read at all - an undeclared one might be a hidden type Marten has not learned yet.
/// </param>
/// <param name="DeclaredCount">How many tables the store declares: they are always judged, and count toward the cap first.</param>
/// <param name="Cap">How many other tables the read may list: <see cref="MartenHealthTableSource.TableCap" /> less <paramref name="DeclaredCount" />.</param>
/// <param name="OtherStoresDeclareHere">
/// Whether another registered store declares a table in a schema this read admits - which the coverage sentence says,
/// never which store or which table.
/// </param>
internal sealed record MartenTableScope(
    IReadOnlyList<string> StoreSchemas,
    IReadOnlyList<string> Names,
    bool Everything,
    IReadOnlyList<HealthTableTarget> Declared,
    bool SkipDocumentTables,
    int DeclaredCount,
    int Cap,
    bool OtherStoresDeclareHere)
{
    /// <summary>Whether the catalog is read at all: the declared tables may fill the cap by themselves.</summary>
    public bool ReadsCatalog => Cap > 0;

    /// <summary>
    /// The scope of one sweep: the store's declarations, every registered store's (for what to leave out), and the studio's
    /// settings (for what the database browser admits).
    /// </summary>
    /// <param name="declared">The store's declared tables.</param>
    /// <param name="classifier">Every registered store's declarations, the swept store's among them.</param>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="storeKey">The swept store's registration key.</param>
    /// <param name="tableCap">The most tables a sweep judges.</param>
    public static MartenTableScope For(
        MartenTableSet declared,
        DatabaseObjectClassifier classifier,
        IStudioSettings settings,
        string storeKey,
        int tableCap)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);

        IReadOnlyList<string> storeSchemas = declared.Declarations.Schemas;

        // D27: a schema outside the store's own is the database browser's to admit, and only while it is open - with it
        // closed every finding there would be withheld from every visitor, so nothing there is read.
        bool browse = !settings.ReadOnly && settings.IsConfigured(StudioCapability.BrowseDatabase);
        IReadOnlyList<string> entries = [.. settings.BrowsableSchemas];
        bool everything = browse && BrowsableSchemaMatcher.IncludesEverything(entries);

        List<string> names = [.. storeSchemas.Distinct(StringComparer.Ordinal)];
        IReadOnlyList<string> named = browse ? PostgresDatabaseDeclarationsSource.OwnSchemas(entries) : [];

        foreach (string schema in named)
        {
            if (!names.Contains(schema, StringComparer.Ordinal))
            {
                names.Add(schema);
            }
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<HealthTableTarget> excluded = [];

        void Exclude(string schema, string table)
        {
            if (seen.Add(SchemaKey.For(schema, table)))
            {
                excluded.Add(new HealthTableTarget(schema, table));
            }
        }

        foreach (StudioTable table in declared.Tables)
        {
            Exclude(table.Schema, table.Table);
        }

        bool othersHere = false;

        foreach (StoreDeclarations store in classifier.Stores)
        {
            if (string.Equals(store.StoreKey, storeKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (MartenDeclaredObject other in store.Declarations.Objects.Values)
            {
                if (!IsTable(other))
                {
                    continue;
                }

                othersHere |= names.Contains(other.Schema, StringComparer.Ordinal)
                    || (everything && BrowsableSchemaMatcher.MightMatch(entries, other.Schema));

                // A hidden type's table is never bound: SkipDocumentTables leaves every document table out of the read.
                if (other is not { Kind: MartenObjectKind.DocumentTable, Visible: false })
                {
                    Exclude(other.Schema, other.Name);
                }
            }
        }

        return new MartenTableScope(
            storeSchemas,
            names,
            everything,
            excluded,
            classifier.MayHideDocumentTypes,
            declared.Tables.Count,
            tableCap - declared.Tables.Count,
            othersHere);
    }

    /// <summary>Whether a declared object is a table: a document, event, projection or extended table, or bookkeeping.</summary>
    internal static bool IsTable(MartenDeclaredObject declared) =>
        declared.Kind is MartenObjectKind.DocumentTable or MartenObjectKind.EventTable
            or MartenObjectKind.ProjectionOrExtendedTable or MartenObjectKind.Infrastructure;
}

/// <summary>
/// How MartenStudio classifies a table the catalog lists beside the store, and the set the two halves make (issue #8, G1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Complete on its own.</b> The statement already leaves the declared tables, other stores' and - while a type may be
/// hidden - every document table out, so the cap and the count are spent on tables that can be judged; the
/// classification here still refuses each of those by itself, so a row that reached it anyway is never judged as
/// somebody else's.
/// </para>
/// <para>
/// <b>The rules, in order</b> (<see cref="Classify" />): a hidden type's table, and one any registered store declares,
/// is left out; an undeclared <c>mt_doc_*</c> table is left out while a type may be hidden, and otherwise - like every
/// other <c>mt_</c> table nobody declares - is the Marten library's, never named as a document type; a table a recognised
/// library names (<see cref="StudioTableLibraries.LibraryOf" />) is that library's; anything else is the host's own in the
/// store's schemas and somebody else's in a schema only <c>BrowsableSchemas</c> admits. Every one of them outside the
/// store's own schemas carries its schema in <see cref="StudioTable.GatedSchemas" /> (D29).
/// </para>
/// </remarks>
internal static class MartenDatabaseTables
{
    /// <summary>What the coverage sentence says when another store's declarations could not be read.</summary>
    internal const string UnclassifiedCoverage =
        "only the tables the store declares - a registered Marten store's configuration could not be read, so no other "
        + "table here could be told from that store's";

    /// <summary>
    /// The table the catalog listed at <paramref name="schema" />.<paramref name="table" />, as the checks judge it - or
    /// <see langword="null" /> when it is not this sweep's to judge.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="storeSchemas">The store's own schemas, compared as the database browser's gate compares them.</param>
    /// <param name="classifier">Every registered store's declarations.</param>
    internal static StudioTable? Classify(
        string schema,
        string table,
        IReadOnlySet<string> storeSchemas,
        DatabaseObjectClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(storeSchemas);
        ArgumentNullException.ThrowIfNull(classifier);

        // A hidden type's table is absent, as from every list (D27).
        if (classifier.IsHiddenTable(schema, table))
        {
            return null;
        }

        // Declared by a registered store: this store's are judged from its configuration, and another's are that store's -
        // whose policy no visitor-less sweep can ask, so they are left out and the coverage sentence says so.
        if (classifier.ClassifyRelation(schema, table) is { StoreKey: not null })
        {
            return null;
        }

        bool own = storeSchemas.Contains(schema);
        IReadOnlyList<string> gated = own ? [] : [schema];

        if (DatabaseObjectClassifier.IsMartenName(table))
        {
            // Another store's, an orphan, or a hidden type Marten has not learned yet (D27's known limit): while a type may
            // be hidden it is left out, and otherwise it is the Marten library's - judged on vacuum and size, never named as
            // a document type, and offered no line, since no store's configuration declares it.
            if (classifier.IsUndeclaredDocumentTable(schema, table) && classifier.MayHideDocumentTypes)
            {
                return null;
            }

            return new StudioTable(schema, table, PostgresHealthTableSource.MartenKindOf(table), StudioTableOwnership.Library)
            {
                Library = "Marten",
                GatedSchemas = gated,
            };
        }

        if (StudioTableLibraries.LibraryOf(table) is { } library)
        {
            return new StudioTable(schema, table, StudioTableKind.Relational, StudioTableOwnership.Library)
            {
                Library = library,
                GatedSchemas = gated,
            };
        }

        return new StudioTable(schema, table, StudioTableKind.Relational, own ? StudioTableOwnership.Owned : StudioTableOwnership.Other)
        {
            GatedSchemas = gated,
        };
    }

    /// <summary>The set: the declared tables, and the listed ones the classification keeps.</summary>
    /// <param name="declared">The store's declared tables.</param>
    /// <param name="rows">What the statement listed, largest first.</param>
    /// <param name="scope">What the statement was asked.</param>
    /// <param name="classifier">Every registered store's declarations.</param>
    /// <param name="tableCap">The most tables a sweep judges.</param>
    internal static MartenDatabaseTableSet Build(
        MartenTableSet declared,
        IReadOnlyList<HealthRelationalTableRow> rows,
        MartenTableScope scope,
        DatabaseObjectClassifier classifier,
        int tableCap)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(classifier);

        HashSet<string> storeSchemas = new(scope.StoreSchemas, StringComparer.Ordinal);
        HashSet<string> taken = new(declared.Tables.Select(static x => x.Key), StringComparer.OrdinalIgnoreCase);
        List<StudioTable> undeclared = [];

        foreach (HealthRelationalTableRow row in rows)
        {
            if (undeclared.Count >= Math.Max(scope.Cap, 0))
            {
                break;
            }

            if (Classify(row.Schema, row.Table, storeSchemas, classifier) is { } table && taken.Add(table.Key))
            {
                undeclared.Add(table);
            }
        }

        int total = rows.Count == 0 ? 0 : rows[0].Total;
        return new MartenDatabaseTableSet(declared, undeclared, CoverageOf(scope, undeclared.Count, rows.Count, total, tableCap));
    }

    /// <summary>The declared tables alone - when the other stores' declarations could not be read - and why.</summary>
    internal static MartenDatabaseTableSet DeclaredOnly(MartenTableSet declared, string coverage) => new(declared, [], coverage);

    /// <summary>
    /// What the set leaves out, after "Judged: " - another store's tables, and how many past the cap - or
    /// <see langword="null" /> when it is every table the source judges. Counts, never a name.
    /// </summary>
    /// <param name="scope">What the statement was asked.</param>
    /// <param name="judged">How many listed tables were kept.</param>
    /// <param name="listed">How many the statement listed: fewer than <paramref name="total" /> only when its cap cut the list.</param>
    /// <param name="total">How many the admitted schemas hold that could have been, past the cap included.</param>
    /// <param name="tableCap">The most tables a sweep judges.</param>
    internal static string? CoverageOf(MartenTableScope scope, int judged, int listed, int total, int tableCap)
    {
        ArgumentNullException.ThrowIfNull(scope);

        List<string> parts = [];
        string cap = tableCap.ToString("N0", CultureInfo.InvariantCulture);

        if (!scope.ReadsCatalog)
        {
            parts.Add("only the " + scope.DeclaredCount.ToString("N0", CultureInfo.InvariantCulture)
                + " tables the store declares, which fill the " + cap + " a sweep judges by themselves");
        }
        else if (total > listed)
        {
            parts.Add("the store's " + scope.DeclaredCount.ToString("N0", CultureInfo.InvariantCulture) + " declared tables and the "
                + judged.ToString("N0", CultureInfo.InvariantCulture) + " largest of "
                + total.ToString("N0", CultureInfo.InvariantCulture) + " other tables by estimated size (a sweep judges at most "
                + cap + ")");
        }

        if (scope.OtherStoresDeclareHere)
        {
            parts.Add("another registered Marten store's tables are not judged here");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}

/// <summary>
/// MartenStudio's <see cref="IStudioTableSet" /> (issue #8, G1): the tables the store declares, with their kinds,
/// origin-aware index rules and <c>StoreOptions</c> remedies exactly as before, and every other table of the admitted
/// schemas beside them, classified as the host's, a library's or somebody else's.
/// </summary>
internal sealed class MartenDatabaseTableSet : IStudioTableSet
{
    private readonly Dictionary<string, StudioTable> byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The set over <paramref name="declared" /> and <paramref name="undeclared" />.</summary>
    /// <param name="declared">The store's declared tables.</param>
    /// <param name="undeclared">The other tables, as <see cref="MartenDatabaseTables.Classify" /> described them.</param>
    /// <param name="coverage">What the set leaves out.</param>
    public MartenDatabaseTableSet(MartenTableSet declared, IReadOnlyList<StudioTable> undeclared, string? coverage)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(undeclared);

        Declared = declared;
        Coverage = coverage;

        List<StudioTable> tables = [];

        foreach (StudioTable table in declared.Tables.Concat(undeclared))
        {
            if (byKey.TryAdd(table.Key, table))
            {
                tables.Add(table);
            }
        }

        tables.Sort(static (a, b) =>
        {
            int bySchema = string.CompareOrdinal(a.Schema, b.Schema);
            return bySchema != 0 ? bySchema : string.CompareOrdinal(a.Table, b.Table);
        });

        Tables = tables;
    }

    /// <summary>The declared half: what the store's configuration says of its tables' indexes.</summary>
    public MartenTableSet Declared { get; }

    /// <inheritdoc />
    public IReadOnlyList<StudioTable> Tables { get; }

    /// <inheritdoc />
    public string? Coverage { get; }

    /// <inheritdoc />
    public StudioTable? Find(string schema, string table) => byKey.GetValueOrDefault(SchemaKey.For(schema, table));

    /// <inheritdoc />
    /// <remarks>
    /// A declared table's indexes are told apart as they always were (<see cref="MartenTableSet.OriginOf" />). On a table
    /// no store declares, nothing reconciles an index: the host's or somebody else's relational table's are the owner's
    /// (<see cref="IndexOrigin.Host" />), and an undeclared <c>mt_</c> table's are Marten's to decide
    /// (<see cref="IndexOrigin.StoreManaged" />) - the index-design rules skip both kinds of library table anyway.
    /// </remarks>
    public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(table);

        if (table.IsDeclaredByStore)
        {
            return Declared.OriginOf(index, table);
        }

        return index.IsPrimaryKey
            ? IndexOrigin.PrimaryKey
            : table.Kind == StudioTableKind.Relational ? IndexOrigin.Host : IndexOrigin.StoreManaged;
    }
}
