using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

namespace MartenStudio.Services.Documents;

/// <summary>
/// One column of the document table: what it selects, what its header says, and the key the column
/// chooser remembers it under.
/// </summary>
/// <param name="Column">The allow-listed column this header selects.</param>
/// <param name="Key">
/// The stable key used in <c>?cols=</c> and in local storage: <c>id</c>, <c>meta:LastModified</c>,
/// <c>dup:email</c> or <c>json:Address.City</c>.
/// </param>
/// <param name="Label">The header text.</param>
/// <param name="Kind">What sort of column it is, which decides how a cell is drawn.</param>
internal sealed record DocumentColumnHeader(
    DocumentColumn Column,
    string Key,
    string Label,
    DocumentColumnKind Kind);

/// <summary>How a column's cells are rendered.</summary>
internal enum DocumentColumnKind
{
    /// <summary>The primary key, rendered as a link to the detail page.</summary>
    Id,

    /// <summary>A metadata column.</summary>
    Metadata,

    /// <summary>A duplicated-field column.</summary>
    Duplicated,

    /// <summary>A JSON property, read with <c>#&gt;&gt;</c>.</summary>
    Json,
}

/// <summary>One row of a document list.</summary>
internal sealed record DocumentRow
{
    /// <summary>The id as text — what travels in the query string of the detail link (D9).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The document's JSON, or <see langword="null"/> when it was larger than the inline budget. A null
    /// here is why the preview row says "too large to preview" rather than showing an empty document.
    /// </summary>
    public string? Json { get; init; }

    /// <summary>How many bytes <c>data::text</c> is (Appendix B addendum: <c>jsonb</c> has no overload).</summary>
    public long SizeBytes { get; init; }

    /// <summary>The cells, parallel to <see cref="DocumentPage.Columns"/> minus the id column.</summary>
    public IReadOnlyList<string?> Cells { get; init; } = [];

    /// <summary>When Marten last wrote the row, when the store keeps that column.</summary>
    public DateTimeOffset? LastModified { get; init; }

    /// <summary>The version, when the store keeps it.</summary>
    public string? Version { get; init; }

    /// <summary>Whether the row is soft-deleted.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>The tenant, on a conjoined collection.</summary>
    public string? TenantId { get; init; }

    /// <summary>The subclass alias from <c>mt_doc_type</c>, on a hierarchy.</summary>
    public string? DocumentTypeAlias { get; init; }

    /// <summary>The sort column's value as text, which together with the id is the keyset cursor.</summary>
    public string? SortValue { get; init; }
}

/// <summary>A JSON property the loaded page suggests as a column, with how often it occurred.</summary>
/// <param name="Name">The top-level key.</param>
/// <param name="Frequency">In how many of the page's documents it appeared.</param>
internal sealed record JsonPropertySuggestion(string Name, int Frequency);

/// <summary>Why a document list is not showing rows.</summary>
internal enum DocumentListState
{
    /// <summary>Rows were read.</summary>
    Loaded,

    /// <summary>The index verdict was red and nobody has pressed "Run anyway" yet.</summary>
    BlockedByVerdict,

    /// <summary>The read failed.</summary>
    Failed,
}

/// <summary>
/// One page of a document list, with everything the page needs to draw itself — including the SQL, so
/// that the "Show SQL" disclosure shows the statement that actually ran (D14).
/// </summary>
internal sealed record DocumentPage
{
    /// <summary>The columns, in order. The first is always the id.</summary>
    public IReadOnlyList<DocumentColumnHeader> Columns { get; init; } = [];

    /// <summary>
    /// Every column the chooser may offer for this collection: the id, the enabled metadata columns and
    /// the duplicated fields. The JSON properties sampled from the page are offered alongside these.
    /// </summary>
    public IReadOnlyList<DocumentColumnHeader> AvailableColumns { get; init; } = [];

    /// <summary>The rows.</summary>
    public IReadOnlyList<DocumentRow> Rows { get; init; } = [];

    /// <summary>Where the next keyset page starts, or <see langword="null"/> when this is the last one.</summary>
    public DocumentKeysetCursor? NextCursor { get; init; }

    /// <summary>Whether a next page exists.</summary>
    public bool HasMore { get; init; }

    /// <summary>The collection's size, per D8.</summary>
    public DocumentCount Estimate { get; init; } = DocumentCount.Unavailable;

    /// <summary>The statement that ran, verbatim.</summary>
    public string Sql { get; init; } = string.Empty;

    /// <summary>The parameter names in it, so the disclosure can show what each one was.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>The verdict on this search, always present even when the page was read.</summary>
    public SearchVerdict Verdict { get; init; } = SearchVerdict.Empty;

    /// <summary>Top-level JSON keys sampled from the rows of this page, most frequent first.</summary>
    public IReadOnlyList<JsonPropertySuggestion> JsonSuggestions { get; init; } = [];

    /// <summary>Whether the rows are real, withheld, or missing.</summary>
    public DocumentListState State { get; init; } = DocumentListState.Loaded;

    /// <summary>
    /// The CLR type behind this collection, so a row's preview can offer a Marten LINQ path from the
    /// JSON copy menu. <see langword="null"/> for a discovered table, where there is no type.
    /// </summary>
    public Type? DocumentClrType { get; init; }

    /// <summary>The serializer's naming policy, so a JSON key can be matched back to a CLR member.</summary>
    public System.Text.Json.JsonNamingPolicy? NamingPolicy { get; init; }

    /// <summary>The key of the column the rows are ordered by, as the URL spells it.</summary>
    public string SortKey { get; init; } = DocumentColumnKeys.Id;

    /// <summary>The direction they are ordered in.</summary>
    public SortDirection Direction { get; init; } = SortDirection.Ascending;

    /// <summary>What went wrong, when <see cref="State"/> is <see cref="DocumentListState.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>The Postgres <c>SqlState</c> of the failure, when there was one.</summary>
    public string? SqlState { get; init; }

    /// <summary>The <c>StoreOptions</c> line that would make a timed-out or unindexed read fast.</summary>
    public string? Suggestion { get; init; }

    /// <summary>A page that could not be read.</summary>
    public static DocumentPage Failed(string error, string? sqlState = null, string? suggestion = null) => new()
    {
        State = DocumentListState.Failed,
        Error = error,
        SqlState = sqlState,
        Suggestion = suggestion,
    };
}
