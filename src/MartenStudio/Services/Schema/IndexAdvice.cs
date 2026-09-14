using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Schema;

/// <summary>One index the store's configuration asks for, flattened out of <c>IDocumentType.Indexes</c>.</summary>
/// <param name="CollectionAlias">The document type that declares it.</param>
/// <param name="DocumentTypeName">The .NET type name.</param>
/// <param name="Schema">The schema its table lives in.</param>
/// <param name="Table">The table it is on.</param>
/// <param name="Name">The index name.</param>
/// <param name="Definition">The statement Weasel would write for it.</param>
internal sealed record DeclaredIndex(
    string CollectionAlias,
    string DocumentTypeName,
    string Schema,
    string Table,
    string Name,
    string Definition);

/// <summary>One document collection, as the advisor needs to know it.</summary>
/// <param name="Alias">The alias Marten gave the type.</param>
/// <param name="DocumentTypeName">
/// The .NET type name as C# spells it, which is what goes in <c>Schema.For&lt;T&gt;()</c>. A generic type
/// arrives from reflection as <c>Envelope`1</c>, and a suggestion nobody can paste is not a suggestion -
/// see <see cref="SchemaTypeName" />.
/// </param>
/// <param name="Schema">The schema its table lives in.</param>
/// <param name="Table">The table name.</param>
/// <param name="SampleMemberName">
/// A member of the type worth suggesting an index on, or <see langword="null" />. Used only to make the
/// suggested line read like the host's own code rather than like documentation.
/// </param>
internal sealed record DeclaredCollection(
    string Alias,
    string DocumentTypeName,
    string Schema,
    string Table,
    string? SampleMemberName);

/// <summary>
/// Joins the indexes Postgres has against the indexes <c>StoreOptions</c> asks for, and says what is
/// worth doing about the difference.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from <c>SchemaDataService</c> on purpose: this is the part with judgement in it,
/// and judgement is the part that has to be testable without a Postgres. Everything it needs arrives as
/// lists.
/// </para>
/// <para>
/// The suggestions are always <c>StoreOptions</c> lines and never DDL. A studio that offered to create an
/// index directly would be creating schema the application's own configuration does not know about - so
/// the next migration would report it as an extra, and <c>ApplyAllConfiguredChangesToDatabaseAsync</c>
/// would drop it again, under <c>CreateOrUpdate</c> and not only under <c>All</c>. The fix for a missing
/// index on a Marten document is a line in the host's registration; this screen's job is to write that
/// line out.
/// </para>
/// </remarks>
internal static class IndexAdvice
{
    /// <summary>What a never-scanned index gets said about it.</summary>
    internal const string NeverUsedSuggestion =
        "Postgres has recorded no scans of this index. Index statistics are reset by pg_stat_reset(), " +
        "a crash and a restore, so confirm the counters are old enough to trust before dropping it.";

    /// <summary>
    /// What an index on a Marten table that nobody configured gets said about it.
    /// </summary>
    /// <remarks>
    /// The wording is the whole point of this constant. Weasel's <c>TableDelta.WriteUpdate</c> writes
    /// <c>drop index</c> for every physical index that is not in the expected table's index list
    /// (<c>Indexes.Extras</c>), reports the table as <c>Update</c>, and <c>Update</c> is exactly what
    /// <c>AutoCreate.CreateOrUpdate</c> allows. A hand-made index really is dropped by the next apply -
    /// verified live, 2026-09-14 - and the only thing that stops it is telling Marten to ignore the name.
    /// </remarks>
    internal const string UndeclaredSuggestion =
        "This index is not declared in StoreOptions, and applying a migration DROPS it - " +
        "AutoCreate.CreateOrUpdate is not additive, and Weasel writes \"drop index\" for every index on a " +
        "Marten table that the configuration does not ask for. Declare it " +
        "(opts.Schema.For<T>().Index(...)), or tell the migration to leave it alone with " +
        "opts.Schema.For<T>().IgnoreIndex(\"<name>\") - or, on an event table, opts.Events.IgnoreIndex(\"<name>\").";

    /// <summary>What an index the host told Marten to ignore gets said about it.</summary>
    internal const string IgnoredSuggestion =
        "The configuration tells Marten's migration detection to ignore this index " +
        "(IgnoreIndex), so it is neither created nor dropped by an apply.";

    /// <summary>What an index on a table Marten does not manage gets said about it.</summary>
    internal const string ForeignTableSuggestion =
        "This table is not one Marten configures, so no migration from here touches it or its indexes. " +
        "It is listed because it lives in a schema this store owns.";

    /// <summary>Joins the lists and produces the tab's answer.</summary>
    /// <param name="actual">Every index Postgres reports in the store's schemas.</param>
    /// <param name="declared">Every index the store's configuration asks for.</param>
    /// <param name="collections">Every document collection, so a table with only a primary key can be named.</param>
    /// <param name="managedTables">
    /// The qualified names of the tables Marten configures. An index on anything else in these schemas
    /// belongs to the host application, and saying "the apply drops this" about one of those would be
    /// false.
    /// </param>
    /// <param name="ignoredIndexes">
    /// The qualified names of the indexes the host told Marten to ignore. Weasel removes them from both
    /// sides of the delta, so they are neither created nor dropped.
    /// </param>
    public static SchemaIndexes Join(
        IReadOnlyList<IndexStatsRow> actual,
        IReadOnlyList<DeclaredIndex> declared,
        IReadOnlyList<DeclaredCollection> collections,
        IReadOnlySet<string>? managedTables = null,
        IReadOnlySet<string>? ignoredIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(collections);

        Dictionary<string, DeclaredIndex> declaredByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeclaredIndex index in declared)
        {
            declaredByKey[SchemaKey.For(index.Schema, index.Table, index.Name)] = index;
        }

        Dictionary<string, DeclaredCollection> collectionsByTable = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeclaredCollection collection in collections)
        {
            collectionsByTable[SchemaKey.For(collection.Schema, collection.Table)] = collection;
        }

        // A table Marten configures, even when no index of its own is declared on it. When the caller
        // says nothing, every table that a declaration or a collection names counts - which is what the
        // pure unit tests want and what keeps this callable with three arguments.
        HashSet<string> managed = new(StringComparer.OrdinalIgnoreCase);
        if (managedTables is null)
        {
            foreach (DeclaredIndex index in declared)
            {
                managed.Add(SchemaKey.For(index.Schema, index.Table));
            }

            foreach (DeclaredCollection collection in collections)
            {
                managed.Add(SchemaKey.For(collection.Schema, collection.Table));
            }
        }
        else
        {
            foreach (string table in managedTables)
            {
                managed.Add(table);
            }
        }

        List<IndexInfo> indexes = new(actual.Count);
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> tablesPresent = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> nonPrimaryKeyIndexes = new(StringComparer.OrdinalIgnoreCase);

        foreach (IndexStatsRow row in actual)
        {
            string tableKey = SchemaKey.For(row.Schema, row.Table);
            string indexKey = SchemaKey.For(row.Schema, row.Table, row.Name);

            present.Add(indexKey);
            tablesPresent.Add(tableKey);

            bool onMartenTable = managed.Contains(tableKey);
            bool ignored = ignoredIndexes is not null
                && (ignoredIndexes.Contains(indexKey) || ignoredIndexes.Contains(SchemaKey.For(row.Schema, row.Name)));

            // A primary-key index is never an "extra" to Weasel: it reads indisprimary rows into
            // Table.PrimaryKeyName and never into Indexes, so no migration drops one as undeclared. On a
            // table Marten manages it is therefore declared, whatever its name happens to be.
            bool isDeclared = declaredByKey.ContainsKey(indexKey)
                || ignored
                || (row.IsPrimaryKey && onMartenTable);

            collectionsByTable.TryGetValue(tableKey, out DeclaredCollection? collection);

            if (!row.IsPrimaryKey)
            {
                nonPrimaryKeyIndexes[tableKey] = nonPrimaryKeyIndexes.GetValueOrDefault(tableKey) + 1;
            }

            indexes.Add(new IndexInfo(
                row.Schema,
                row.Table,
                row.Name,
                row.Definition,
                row.Bytes,
                row.Scans,
                row.TuplesRead,
                row.IsPrimaryKey,
                row.IsUnique,
                isDeclared,
                collection?.Alias,
                SuggestionFor(row, isDeclared, onMartenTable, ignored),
                onMartenTable,
                ignored));
        }

        List<MissingIndex> missing = [];
        foreach (DeclaredIndex index in declared)
        {
            // Only on a table that is actually there: a whole table that has not been created yet is a
            // Drift tab fact, and listing each of its indexes here as "missing" would bury it.
            if (!tablesPresent.Contains(SchemaKey.For(index.Schema, index.Table)))
            {
                continue;
            }

            if (!present.Contains(SchemaKey.For(index.Schema, index.Table, index.Name)))
            {
                missing.Add(new MissingIndex(index.Schema, index.Table, index.Name, index.Definition, index.CollectionAlias));
            }
        }

        List<UnindexedCollection> unindexed = [];
        foreach (DeclaredCollection collection in collections)
        {
            string tableKey = SchemaKey.For(collection.Schema, collection.Table);

            if (tablesPresent.Contains(tableKey) && nonPrimaryKeyIndexes.GetValueOrDefault(tableKey) == 0)
            {
                unindexed.Add(new UnindexedCollection(
                    collection.Alias,
                    collection.DocumentTypeName,
                    collection.Schema,
                    collection.Table,
                    SuggestionsFor(collection)));
            }
        }

        return new SchemaIndexes(indexes, missing, unindexed, null);
    }

    /// <summary>
    /// The <c>StoreOptions</c> lines that would put an index on <paramref name="collection" />.
    /// </summary>
    /// <remarks>
    /// Ordered by how often each is the right answer: a computed index on the property being filtered,
    /// then a duplicated column when that property is also sorted or joined on, then a GIN index for
    /// containment queries across the whole document, and last the <c>mt_last_modified</c> index that
    /// only helps a "recently changed" listing.
    /// </remarks>
    public static IReadOnlyList<string> SuggestionsFor(DeclaredCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        string type = collection.DocumentTypeName;
        string member = collection.SampleMemberName ?? "Property";

        List<string> lines =
        [
            $"opts.Schema.For<{type}>().Index(x => x.{member});",
            $"opts.Schema.For<{type}>().Duplicate(x => x.{member});",
            $"opts.Schema.For<{type}>().GinIndexJsonData();",
            $"opts.Schema.For<{type}>().IndexLastModified();",
        ];

        return lines;
    }

    private static string? SuggestionFor(IndexStatsRow row, bool isDeclared, bool onMartenTable, bool ignored)
    {
        if (ignored)
        {
            return IgnoredSuggestion;
        }

        if (!isDeclared)
        {
            return onMartenTable ? UndeclaredSuggestion : ForeignTableSuggestion;
        }

        // A primary key or a unique index is enforcing something; "nobody scanned it" is not an argument
        // for dropping it, so it gets no suggestion at all.
        if (row.IsPrimaryKey || row.IsUnique)
        {
            return null;
        }

        return row.Scans is 0 ? NeverUsedSuggestion : null;
    }
}
