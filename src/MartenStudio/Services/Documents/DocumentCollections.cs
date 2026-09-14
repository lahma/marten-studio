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

/// <summary>
/// The <c>_recent</c> region: its rows, or the reason it has none.
/// </summary>
/// <remarks>
/// <para>
/// The region needs three answers and a bare list can only give two. "Nothing has been written lately" and
/// "this store keeps no <c>mt_last_modified</c> anywhere" are both empty, and so was the third — the union
/// ran past its <c>statement_timeout</c> because a collection in it has no index on the sort column and
/// several million rows. Reporting that as "nothing recent" is the studio being quietly wrong about the
/// one thing the region exists to say, so it is a value instead (plan §4.8).
/// </para>
/// <para>
/// <see cref="Notice"/> is what the region draws in place of rows; <see cref="TooLargeToScan"/> separates
/// the timeout from every other reason, because it is the one a host can act on — the branch that cost the
/// time is a collection whose <c>mt_last_modified</c> has no index behind it.
/// </para>
/// </remarks>
internal sealed record RecentDocuments
{
    /// <summary>Nothing recent, and nothing to say about it.</summary>
    public static RecentDocuments None { get; } = new();

    /// <summary>The rows, newest first.</summary>
    public IReadOnlyList<RecentDocument> Rows { get; init; } = [];

    /// <summary>Why there are none, when that is worth saying.</summary>
    public string? Notice { get; init; }

    /// <summary>Whether the union was abandoned by <c>statement_timeout</c>.</summary>
    public bool TooLargeToScan { get; init; }

    /// <summary>What the region says when the scan ran out of time.</summary>
    internal const string TooLargeNotice =
        "Too large to scan: reading the most recently changed documents ran past the studio's query " +
        "timeout. Marten declares no index on mt_last_modified, so a collection of any size is sorted in " +
        "full to answer this - opts.Schema.For<T>().IndexLastModified() on the large ones fixes it.";

    /// <summary>The rows this read produced.</summary>
    /// <param name="rows">The rows, newest first.</param>
    public static RecentDocuments From(IReadOnlyList<RecentDocument> rows) => new() { Rows = rows };

    /// <summary>The <c>57014</c> answer: a value, not an empty list and not an exception.</summary>
    public static RecentDocuments TooLarge() => new() { Notice = TooLargeNotice, TooLargeToScan = true };

    /// <summary>Any other failure, reported rather than blanked.</summary>
    /// <param name="notice">What went wrong, phrased for the page.</param>
    public static RecentDocuments Failed(string notice) => new() { Notice = notice };
}
