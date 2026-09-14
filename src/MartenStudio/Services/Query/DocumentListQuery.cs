using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

/// <summary>Which way a document list is ordered.</summary>
internal enum SortDirection
{
    /// <summary>Ascending, nulls last.</summary>
    Ascending,

    /// <summary>Descending, nulls last.</summary>
    Descending,
}

/// <summary>The tri-state the documents page offers for soft-deleted rows.</summary>
internal enum DeletedFilter
{
    /// <summary>Only live documents — what a collection shows by default.</summary>
    Exclude,

    /// <summary>Live and soft-deleted together.</summary>
    Include,

    /// <summary>Only the soft-deleted ones.</summary>
    Only,
}

/// <summary>
/// A column a document list may show or be ordered by. This type <em>is</em> the allow-list: the query
/// builder can only emit what one of these four cases describes, so no part of a sort or column choice
/// ever arrives as SQL text.
/// </summary>
/// <remarks>
/// <see cref="Metadata"/> and <see cref="Duplicated"/> are checked against the table before they are
/// emitted — a metadata column that is disabled, or a duplicated column that does not exist, is a request
/// the builder refuses rather than a column name it passes through. <see cref="JsonPath"/> never becomes
/// SQL text at all: the path travels as a <c>text[]</c> parameter to <c>#&gt;&gt;</c>.
/// </remarks>
internal abstract record DocumentColumn
{
    private DocumentColumn()
    {
    }

    /// <summary>The primary key. Always present, and always the tiebreaker of every sort.</summary>
    public static DocumentColumn ById { get; } = new Id();

    /// <summary>The <c>id</c> column.</summary>
    public sealed record Id : DocumentColumn;

    /// <summary>One of Marten's metadata columns, if the store has it enabled.</summary>
    /// <param name="Column">Which metadata column.</param>
    public sealed record Metadata(DocumentMetadataColumn Column) : DocumentColumn;

    /// <summary>A duplicated-field column, by its physical column name.</summary>
    /// <param name="ColumnName">The column name, checked against the table's duplicated fields.</param>
    public sealed record Duplicated(string ColumnName) : DocumentColumn;

    /// <summary>A JSON property, reached with <c>#&gt;&gt;</c> and a <c>text[]</c> parameter.</summary>
    /// <param name="Path">The path segments, for example <c>["Address", "City"]</c>.</param>
    public sealed record JsonPath(IReadOnlyList<string> Path) : DocumentColumn
    {
        /// <summary>A display label for the column header.</summary>
        public string Label => string.Join('.', Path);
    }
}

/// <summary>
/// Where the next page of a keyset-paged list starts: the last row's sort value and its id.
/// </summary>
/// <remarks>
/// Both halves are needed because the id is the mandatory tiebreaker — a sort on
/// <c>mt_last_modified</c> alone has no total order, and a keyset page that starts "after the last
/// timestamp" silently drops every row written in the same millisecond.
/// </remarks>
/// <param name="SortValue">
/// The last row's sort value as text, or <see langword="null"/> when that row's sort value was null. It is
/// converted to the sort column's real type before it is bound.
/// </param>
/// <param name="LastId">The last row's id as text, converted against the id column's type.</param>
internal sealed record DocumentKeysetCursor(string? SortValue, string LastId);

/// <summary>
/// Everything the documents page asks for, as data. The page builds one of these; the query builder turns
/// it into exactly one parameterised command.
/// </summary>
internal sealed record DocumentListQuery
{
    /// <summary>The default page size, and what the page opens with.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The largest page the builder will emit, whatever the caller asks for.</summary>
    public const int MaxPageSize = 500;

    /// <summary>The default inline-JSON budget: bigger documents come back as a size, not as text.</summary>
    public const int DefaultMaxInlineDocumentBytes = 64 * 1024;

    /// <summary>The filter terms, ANDed. Normally <see cref="SearchGrammar.Parse"/>'s output.</summary>
    public IReadOnlyList<DocumentPredicate> Predicates { get; init; } = [];

    /// <summary>The sort key, from the allow-list that <see cref="DocumentColumn"/> is.</summary>
    public DocumentColumn Sort { get; init; } = DocumentColumn.ById;

    /// <summary>The sort direction.</summary>
    public SortDirection Direction { get; init; } = SortDirection.Ascending;

    /// <summary>The keyset cursor. When set, <see cref="Offset"/> is ignored.</summary>
    public DocumentKeysetCursor? Cursor { get; init; }

    /// <summary>
    /// The offset, for the page's offset toggle. Capped at <see cref="DocumentQueryBuilder.MaxOffset"/>,
    /// beyond which the builder refuses: a ten-thousand-row offset is already a scan of ten thousand rows.
    /// </summary>
    public int Offset { get; init; }

    /// <summary>How many rows to return.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>What to do about soft-deleted documents.</summary>
    public DeletedFilter IncludeDeleted { get; init; } = DeletedFilter.Exclude;

    /// <summary>The selected tenant, or <see langword="null"/> for all of them.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The optional columns to select. Empty means "every enabled metadata column and every duplicated
    /// column", which is what the page shows before anyone touches the column chooser.
    /// </summary>
    public IReadOnlyList<DocumentColumn> Columns { get; init; } = [];

    /// <summary>
    /// The threshold above which <c>data</c> is not inlined. The list still reports every document's size.
    /// </summary>
    public int MaxInlineDocumentBytes { get; init; } = DefaultMaxInlineDocumentBytes;
}
