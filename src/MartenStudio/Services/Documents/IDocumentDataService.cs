using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

namespace MartenStudio.Services.Documents;

/// <summary>
/// What the documents browser asks for, in the terms the URL carries it in.
/// </summary>
/// <remarks>
/// Deliberately strings and simple values rather than a <see cref="DocumentListQuery"/>: everything here
/// arrives from a query string (plan D9, §3.2) and has to be validated against the collection before it
/// can become a sort key or a column. Turning this into a <see cref="DocumentListQuery"/> is the data
/// service's job, and is where a column the table does not have becomes "ignored" rather than "SQL".
/// </remarks>
internal sealed record DocumentListRequest
{
    /// <summary>The search box, in the grammar of plan §3.4.</summary>
    public string? Search { get; init; }

    /// <summary>The sort column's key, as <see cref="DocumentColumnHeader.Key"/> spells it.</summary>
    public string? SortKey { get; init; }

    /// <summary>The sort direction.</summary>
    public SortDirection Direction { get; init; } = SortDirection.Descending;

    /// <summary>The keyset cursor, when paging forwards.</summary>
    public DocumentKeysetCursor? Cursor { get; init; }

    /// <summary>The offset, when the page is in offset mode.</summary>
    public int Offset { get; init; }

    /// <summary>Whether to page by offset rather than by cursor.</summary>
    public bool UseOffsetPaging { get; init; }

    /// <summary>Rows per page; clamped to the options' <c>MaxPageSize</c>.</summary>
    public int PageSize { get; init; }

    /// <summary>What to do about soft-deleted rows. Only meaningful on a soft-deleted collection.</summary>
    public DeletedFilter Deleted { get; init; } = DeletedFilter.Exclude;

    /// <summary>The columns to show, by key. Empty means the collection's default set.</summary>
    public IReadOnlyList<string> ColumnKeys { get; init; } = [];

    /// <summary>
    /// Whether the visitor has accepted a red index verdict for this collection. Without it a red search
    /// is described but not run.
    /// </summary>
    public bool RunAnyway { get; init; }

    /// <summary>Whether an exact <c>count(*)</c> was asked for, rather than the <c>reltuples</c> estimate (D8).</summary>
    public bool ExactCount { get; init; }
}

/// <summary>
/// Everything the documents browser reads. Scoped, and every method takes a <see cref="StudioScope"/>
/// which it resolves before it looks anything up.
/// </summary>
/// <remarks>
/// No method accepts an <c>IDocumentStore</c> or an <c>IMartenDatabase</c> (plan §4.2): a resolved store
/// has already passed the per-store authorization policy, and a method that took one would make it
/// possible to reach a store the visitor may not see by handing it an object resolved for something else.
/// </remarks>
internal interface IDocumentDataService
{
    /// <summary>
    /// The collections rail: registered types with their subclasses nested, discovered tables, and a
    /// single grouped <c>reltuples</c> read for all of their counts.
    /// </summary>
    Task<CollectionRail> GetCollectionsAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>An exact <c>count(*)</c> for one collection, which the rail asks for on demand only (D8).</summary>
    Task<DocumentCount> CountExactAsync(StudioScope scope, string alias, CancellationToken cancellationToken = default);

    /// <summary>One page of a collection, with the SQL that produced it and the verdict on the search.</summary>
    Task<DocumentPage> ListAsync(
        StudioScope scope,
        string alias,
        DocumentListRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>One document, or a precise account of why not.</summary>
    Task<DocumentDetailResult> GetDocumentAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>What this document points at through its declared foreign keys (outbound only in v1).</summary>
    Task<RelatedDocuments> GetRelatedAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>Whether an event stream with this id exists, so the detail page can offer "view stream".</summary>
    Task<bool> StreamExistsAsync(StudioScope scope, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which other collections could hold this id, and which of them actually do — the answer to a link
    /// whose id does not fit the collection it names.
    /// </summary>
    Task<IReadOnlyList<DocumentIdProbe>> ProbeIdAsync(
        StudioScope scope,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>The <c>_recent</c> pseudo-collection: the most recently modified documents of any type.</summary>
    Task<IReadOnlyList<RecentDocument>> GetRecentAsync(
        StudioScope scope,
        int limit,
        CancellationToken cancellationToken = default);
}
