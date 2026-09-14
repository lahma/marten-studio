namespace MartenStudio.Services.Schema;

/// <summary>One index that exists in the database, beside what the store's configuration says about it.</summary>
/// <param name="Schema">The schema it lives in.</param>
/// <param name="Table">The table it is on.</param>
/// <param name="Name">The index name.</param>
/// <param name="Definition">The <c>create index</c> statement Postgres reconstructs for it.</param>
/// <param name="Bytes">How much disk it takes.</param>
/// <param name="Scans"><c>idx_scan</c>, or <see langword="null" /> when the collector has nothing to say.</param>
/// <param name="TuplesRead"><c>idx_tup_read</c>, or <see langword="null" />.</param>
/// <param name="IsPrimaryKey">Whether it backs the primary key.</param>
/// <param name="IsUnique">Whether it enforces uniqueness.</param>
/// <param name="DeclaredByMarten">
/// Whether the store's configuration asks for an index of this name, or told Marten to ignore it. An
/// index on a Marten table that is neither is <em>dropped</em> by the next
/// <c>ApplyAllConfiguredChangesToDatabaseAsync</c>, under <c>AutoCreate.CreateOrUpdate</c> and not only
/// under <c>All</c> - which is exactly the sort of thing that has to be visible before it happens.
/// </param>
/// <param name="CollectionAlias">The document type whose table this index is on, when it is one.</param>
/// <param name="Suggestion">What the studio has to say about it, or <see langword="null" />.</param>
/// <param name="OnMartenTable">
/// Whether the table this index is on is one Marten configures. A host's own table can live in the same
/// schema, and no migration from here touches it - so "undeclared" means something quite different there
/// and must not carry the same warning.
/// </param>
/// <param name="IgnoredByConfiguration">
/// Whether the host called <c>IgnoreIndex</c> for this name. Weasel drops such an index from both sides
/// of the delta, so it is neither created nor dropped.
/// </param>
internal sealed record IndexInfo(
    string Schema,
    string Table,
    string Name,
    string Definition,
    long Bytes,
    long? Scans,
    long? TuplesRead,
    bool IsPrimaryKey,
    bool IsUnique,
    bool DeclaredByMarten,
    string? CollectionAlias,
    string? Suggestion,
    bool OnMartenTable = true,
    bool IgnoredByConfiguration = false)
{
    /// <summary>Whether the next apply would drop this index.</summary>
    public bool WouldBeDropped => OnMartenTable && !DeclaredByMarten && !IsPrimaryKey;

    /// <summary>
    /// Whether Postgres has recorded no scan of this index at all.
    /// </summary>
    /// <remarks>
    /// A flag, not a verdict. <c>pg_stat_reset()</c>, a crash and a recent restore all zero these
    /// counters, so "never used" and "the statistics are two days old" look identical from here. The tab
    /// says so beside the flag, because a UI that talks somebody into dropping an index they need has
    /// done more harm than one that said nothing.
    /// </remarks>
    public bool NeverUsed => Scans is 0;

    /// <summary>Whether dropping it would change what the schema enforces rather than only what it costs.</summary>
    public bool IsConstraint => IsPrimaryKey || IsUnique;
}

/// <summary>An index the configuration asks for that is not in the database.</summary>
/// <param name="Schema">The schema it should be in.</param>
/// <param name="Table">The table it should be on.</param>
/// <param name="Name">The index name Marten would give it.</param>
/// <param name="Definition">The statement Marten would run, as Weasel renders it.</param>
/// <param name="CollectionAlias">The document type that declares it.</param>
internal sealed record MissingIndex(
    string Schema,
    string Table,
    string Name,
    string Definition,
    string CollectionAlias);

/// <summary>A document collection whose table carries nothing but its primary key.</summary>
/// <param name="Alias">The document type's alias.</param>
/// <param name="DocumentTypeName">The .NET type name, which is what goes in <c>Schema.For&lt;T&gt;()</c>.</param>
/// <param name="Schema">The schema the table lives in.</param>
/// <param name="Table">The table name.</param>
/// <param name="Suggestions">The <c>StoreOptions</c> lines that would add an index, in the order to try them.</param>
internal sealed record UnindexedCollection(
    string Alias,
    string DocumentTypeName,
    string Schema,
    string Table,
    IReadOnlyList<string> Suggestions)
{
    /// <summary>The qualified table name.</summary>
    public string QualifiedName => Schema + "." + Table;
}

/// <summary>The Indexes tab's answer for one database.</summary>
/// <param name="Indexes">Every index that exists in the store's schemas.</param>
/// <param name="Missing">Every index the configuration declares that is not there.</param>
/// <param name="Unindexed">Every document collection whose table has only its primary key.</param>
/// <param name="Reason">Why nothing could be read, or <see langword="null" />.</param>
internal sealed record SchemaIndexes(
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<MissingIndex> Missing,
    IReadOnlyList<UnindexedCollection> Unindexed,
    string? Reason)
{
    /// <summary>Nothing read yet.</summary>
    public static SchemaIndexes Empty { get; } = new([], [], [], null);

    /// <summary>The read failed, and this is why.</summary>
    public static SchemaIndexes Unavailable(string reason) => new([], [], [], reason);

    /// <summary>How many indexes Postgres has recorded no scan of.</summary>
    public int NeverUsedCount
    {
        get
        {
            int count = 0;
            foreach (IndexInfo index in Indexes)
            {
                if (index.NeverUsed && !index.IsConstraint)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// How many indexes an apply would drop.
    /// </summary>
    /// <remarks>
    /// The number a person most needs before they press Apply, and the reason it is a summary rather than
    /// only a per-row flag: one hand-made index buried in a list of forty is exactly what gets lost.
    /// </remarks>
    public int WouldBeDroppedCount
    {
        get
        {
            int count = 0;
            foreach (IndexInfo index in Indexes)
            {
                if (index.WouldBeDropped)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
