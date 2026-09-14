using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Documents;

/// <summary>
/// Which band of the collections rail a group belongs to. The order of the members is the order the
/// groups are drawn in.
/// </summary>
internal enum CollectionGroupKind
{
    /// <summary>Collections the visitor pinned, remembered in this browser's local storage.</summary>
    Pinned,

    /// <summary>The <c>_recent</c> pseudo-collection: the most recently modified documents, whatever their type.</summary>
    Recent,

    /// <summary>Everything <c>StoreOptions</c> knows about, root types with their subclasses nested.</summary>
    Documents,

    /// <summary>
    /// <c>mt_doc_*</c> tables the database has and no mapping claims. Always read-only: nothing is known
    /// about them beyond their columns.
    /// </summary>
    Discovered,
}

/// <summary>
/// The four facts about a collection that change how it behaves, rendered as flags on the rail.
/// </summary>
/// <param name="SoftDeleted">
/// Whether the delete style is soft, read through <c>Marten.Schema.DocumentMapping.DeleteStyle</c> rather
/// than <c>Metadata.IsSoftDeleted.Enabled</c> — Appendix B addendum: the latter is true even for a type
/// whose table has no <c>mt_deleted</c> column.
/// </param>
/// <param name="Conjoined">Whether rows carry a <c>tenant_id</c>.</param>
/// <param name="OptimisticConcurrency">Whether Marten checks <c>mt_version</c> on write.</param>
/// <param name="Hierarchy">Whether the table holds more than one .NET type, discriminated by <c>mt_doc_type</c>.</param>
internal readonly record struct CollectionFlags(
    bool SoftDeleted,
    bool Conjoined,
    bool OptimisticConcurrency,
    bool Hierarchy);

/// <summary>
/// One entry of the collections rail.
/// </summary>
/// <remarks>
/// A subclass of a hierarchy is one of these too, nested under its root in
/// <see cref="SubCollections"/>: it shares the root's table and is reached by adding
/// <c>mt_doc_type = &lt;alias&gt;</c> to every read, which is why it carries its own alias but the root's
/// schema and table.
/// </remarks>
internal sealed record CollectionInfo
{
    /// <summary>Marten's document alias — the segment that appears in the URL.</summary>
    public required string Alias { get; init; }

    /// <summary>The .NET type's short name, or <see langword="null"/> for a discovered table.</summary>
    public string? ClrTypeName { get; init; }

    /// <summary>The .NET type's namespace-qualified name, shown as the entry's tooltip.</summary>
    public string? FullTypeName { get; init; }

    /// <summary>The schema the table lives in.</summary>
    public required string Schema { get; init; }

    /// <summary>The table name.</summary>
    public required string Table { get; init; }

    /// <summary>The stable colour of this collection, from <see cref="CollectionColorizer"/>.</summary>
    public int Hue { get; init; }

    /// <summary>How many documents there are, or the admission that the studio could not find out.</summary>
    public DocumentCount Count { get; init; } = DocumentCount.Unavailable;

    /// <summary>Whether <c>StoreOptions</c> knows this collection at all.</summary>
    public bool IsRegistered { get; init; } = true;

    /// <summary>Whether this entry is a subclass nested under a root type.</summary>
    public bool IsSubclass { get; init; }

    /// <summary>The root type's alias, for a subclass.</summary>
    public string? RootAlias { get; init; }

    /// <summary>The flags drawn beside the name.</summary>
    public CollectionFlags Flags { get; init; }

    /// <summary>The id column's Postgres type, which decides what an id in a URL is parsed as.</summary>
    public DocumentIdColumnType IdColumnType { get; init; }

    /// <summary>How many duplicated-field columns the table has.</summary>
    public int DuplicatedFieldCount { get; init; }

    /// <summary>The subclasses of a hierarchy, nested under their root.</summary>
    public IReadOnlyList<CollectionInfo> SubCollections { get; init; } = [];
}

/// <summary>One band of the rail.</summary>
/// <param name="Kind">Which band.</param>
/// <param name="Title">The heading.</param>
/// <param name="Collections">What is in it, already ordered.</param>
internal sealed record CollectionGroup(
    CollectionGroupKind Kind,
    string Title,
    IReadOnlyList<CollectionInfo> Collections);

/// <summary>
/// Everything the collections rail draws, plus the honest admission when a band could not be read.
/// </summary>
/// <param name="Groups">The bands, in rail order.</param>
/// <param name="Error">
/// What went wrong, or <see langword="null"/>. A rail that cannot be read must say so rather than render
/// as an empty store (plan §4.8).
/// </param>
internal sealed record CollectionRail(IReadOnlyList<CollectionGroup> Groups, string? Error = null)
{
    /// <summary>An empty rail with a message on it.</summary>
    public static CollectionRail Failed(string error) => new([], error);

    /// <summary>Finds a collection by alias, subclasses included.</summary>
    public CollectionInfo? Find(string? alias)
    {
        if (string.IsNullOrEmpty(alias))
        {
            return null;
        }

        foreach (var group in Groups)
        {
            if (group.Kind == CollectionGroupKind.Pinned)
            {
                // Pinned entries are the same objects again; skipping them keeps the first match the
                // canonical one and stops a pin from shadowing a discovered table of the same name.
                continue;
            }

            foreach (var collection in group.Collections)
            {
                if (string.Equals(collection.Alias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    return collection;
                }

                foreach (var subclass in collection.SubCollections)
                {
                    if (string.Equals(subclass.Alias, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        return subclass;
                    }
                }
            }
        }

        return null;
    }
}

/// <summary>
/// One row of the <c>_recent</c> pseudo-collection: a document from some collection, with when it changed.
/// </summary>
/// <param name="Alias">The collection it came from.</param>
/// <param name="Id">Its id, as text.</param>
/// <param name="LastModified">When Marten last wrote it.</param>
/// <param name="Hue">The collection's colour, so the mixed list stays readable.</param>
internal sealed record RecentDocument(string Alias, string Id, DateTimeOffset LastModified, int Hue);
