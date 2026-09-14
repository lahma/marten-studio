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

    // There is deliberately no ExactCount flag here.
    //
    // There was one, no page ever set it, and if a page had set it the header would have shown a count of
    // the whole collection over a grid showing the rows a search had narrowed - the studio answering a
    // different question from the one on screen, which is the bug P2-fix fixed for tenancy and would have
    // reintroduced for filters. Counting *with* the predicates is not the fix either: the predicates a
    // person types are exactly the ones the index verdict withholds a read for, so an honest filtered
    // count is the sequential scan the verdict just refused to run.
    //
    // The header therefore always reports the size of the collection in scope - tenant and soft-delete
    // tri-state, never the search - and the exact number is asked for on the rail, per collection, through
    // CountExactAsync. One number, one meaning.
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

    /// <summary>
    /// The same, for a document the caller has already loaded.
    /// </summary>
    /// <remarks>
    /// The detail page has the row in its hand: it calls <see cref="GetDocumentAsync"/> to render the
    /// document and then asked for the related documents by id, which read the very same row a second time
    /// — a second scope resolution, a second connection, a second single-row read and a second size query,
    /// for a handful of column values already on screen. This overload is the one a page should use; the
    /// id form remains for a caller that has not loaded it.
    /// </remarks>
    Task<RelatedDocuments> GetRelatedAsync(
        StudioScope scope,
        string alias,
        DocumentDetail detail,
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

    /// <summary>
    /// The <c>_recent</c> pseudo-collection: the most recently modified documents of any type, with the
    /// reason there are no rows when there are none.
    /// </summary>
    /// <remarks>
    /// Every branch of the union is <c>order by mt_last_modified desc limit n</c> and Marten declares no
    /// index on that column, so the region's cost grows with the store. It runs under a
    /// <c>statement_timeout</c> and reports <c>57014</c> as <see cref="RecentDocuments.TooLargeToScan"/> —
    /// a value the page renders, because an empty region that means "this timed out" is the studio being
    /// quietly wrong about the one thing the region exists to say.
    /// </remarks>
    Task<RecentDocuments> ListRecentAsync(
        StudioScope scope,
        int limit,
        CancellationToken cancellationToken = default);
}
