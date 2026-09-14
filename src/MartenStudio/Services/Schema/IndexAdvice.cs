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
/// <param name="DocumentTypeName">The .NET type name, which is what goes in <c>Schema.For&lt;T&gt;()</c>.</param>
/// <param name="Schema">The schema its table lives in.</param>
/// <param name="Table">The table name.</param>
/// <param name="HasDuplicatedFields">Whether the type already duplicates a member into a column.</param>
/// <param name="SampleMemberName">
/// A member of the type worth suggesting an index on, or <see langword="null" />. Used only to make the
/// suggested line read like the host's own code rather than like documentation.
/// </param>
internal sealed record DeclaredCollection(
    string Alias,
    string DocumentTypeName,
    string Schema,
    string Table,
    bool HasDuplicatedFields,
    string? SampleMemberName);

/// <summary>
/// Joins the indexes Postgres has against the indexes <c>StoreOptions</c> asks for, and says what is
/// worth doing about the difference.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and separate from <c>SchemaDataService</c> on purpose: this is the part with judgement in it,
/// and judgement is the part that has to be testable without a Postgres. Everything it needs arrives as
/// two lists.
/// </para>
/// <para>
/// The suggestions are always <c>StoreOptions</c> lines and never DDL. A studio that offered to create an
/// index directly would be creating schema the application's own configuration does not know about - so
/// the next <c>ApplyAllConfiguredChangesToDatabaseAsync</c> would report drift for it, and a migration
/// run with <c>AutoCreate.All</c> would quietly drop it again. The fix for a missing index on a Marten
/// document is a line in the host's registration; this screen's job is to write that line out.
/// </para>
/// </remarks>
internal static class IndexAdvice
{
    /// <summary>What a never-scanned index gets said about it.</summary>
    internal const string NeverUsedSuggestion =
        "Postgres has recorded no scans of this index. Index statistics are reset by pg_stat_reset(), " +
        "a crash and a restore, so confirm the counters are old enough to trust before dropping it.";

    /// <summary>What an index nobody configured gets said about it.</summary>
    internal const string UndeclaredSuggestion =
        "This index is not declared in StoreOptions. Marten leaves it alone under AutoCreate.CreateOrUpdate, " +
        "but a migration run with AutoCreate.All would drop it.";

    /// <summary>Joins the two lists and produces the tab's answer.</summary>
    /// <param name="actual">Every index Postgres reports in the store's schemas.</param>
    /// <param name="declared">Every index the store's configuration asks for.</param>
    /// <param name="collections">Every document collection, so a table with only a primary key can be named.</param>
    public static SchemaIndexes Join(
        IReadOnlyList<IndexStatsRow> actual,
        IReadOnlyList<DeclaredIndex> declared,
        IReadOnlyList<DeclaredCollection> collections)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(collections);

        Dictionary<string, DeclaredIndex> declaredByKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeclaredIndex index in declared)
        {
            declaredByKey[Key(index.Schema, index.Table, index.Name)] = index;
        }

        Dictionary<string, DeclaredCollection> collectionsByTable = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeclaredCollection collection in collections)
        {
            collectionsByTable[Key(collection.Schema, collection.Table)] = collection;
        }

        List<IndexInfo> indexes = new(actual.Count);
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> nonPrimaryKeyIndexes = new(StringComparer.OrdinalIgnoreCase);

        foreach (IndexStatsRow row in actual)
        {
            string indexKey = Key(row.Schema, row.Table, row.Name);
            present.Add(indexKey);

            bool isDeclared = declaredByKey.ContainsKey(indexKey);
            collectionsByTable.TryGetValue(Key(row.Schema, row.Table), out DeclaredCollection? collection);

            if (!row.IsPrimaryKey)
            {
                string tableKey = Key(row.Schema, row.Table);
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
                SuggestionFor(row, isDeclared)));
        }

        List<MissingIndex> missing = [];
        foreach (DeclaredIndex index in declared)
        {
            if (!present.Contains(Key(index.Schema, index.Table, index.Name)))
            {
                missing.Add(new MissingIndex(index.Schema, index.Table, index.Name, index.Definition, index.CollectionAlias));
            }
        }

        List<UnindexedCollection> unindexed = [];
        foreach (DeclaredCollection collection in collections)
        {
            string tableKey = Key(collection.Schema, collection.Table);

            // Only a table that is actually there counts: a collection whose table has not been created
            // yet belongs on the Drift tab, not here.
            bool tableExists = false;
            foreach (IndexStatsRow row in actual)
            {
                if (string.Equals(Key(row.Schema, row.Table), tableKey, StringComparison.OrdinalIgnoreCase))
                {
                    tableExists = true;
                    break;
                }
            }

            if (tableExists && nonPrimaryKeyIndexes.GetValueOrDefault(tableKey) == 0)
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

    private static string? SuggestionFor(IndexStatsRow row, bool isDeclared)
    {
        if (!isDeclared)
        {
            return UndeclaredSuggestion;
        }

        // A primary key or a unique index is enforcing something; "nobody scanned it" is not an argument
        // for dropping it, so it gets no suggestion at all.
        if (row.IsPrimaryKey || row.IsUnique)
        {
            return null;
        }

        return row.Scans is 0 ? NeverUsedSuggestion : null;
    }

    private static string Key(string schema, string table) => schema + "." + table;

    private static string Key(string schema, string table, string name) => schema + "." + table + "." + name;
}
