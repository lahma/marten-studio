using System.Buffers.Text;
using System.Globalization;
using System.Text;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

namespace MartenStudio.Services.Database;

/// <summary>What a row filter term compares with.</summary>
internal enum RowFilterOperator
{
    /// <summary><c>=</c>.</summary>
    Equal,

    /// <summary><c>!=</c> or <c>&lt;&gt;</c>.</summary>
    NotEqual,

    /// <summary><c>&lt;</c>.</summary>
    LessThan,

    /// <summary><c>&lt;=</c>.</summary>
    LessThanOrEqual,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterThanOrEqual,

    /// <summary><c>~</c>: a case-insensitive substring of the value's text.</summary>
    Contains,

    /// <summary><c>is:null</c>.</summary>
    IsNull,

    /// <summary><c>is:notnull</c>.</summary>
    IsNotNull,
}

/// <summary>One term of a row filter, resolved against the relation's columns.</summary>
/// <param name="Column">The column, spelled as the catalog spells it - the only spelling that is ever quoted.</param>
/// <param name="Operator">The comparison.</param>
/// <param name="Value">
/// The value exactly as typed (quotes removed, nothing else): <c>007</c> stays <c>007</c>, because Postgres
/// applies the column's own input function to it. <see langword="null" /> for <c>is:null</c> and
/// <c>is:notnull</c>.
/// </param>
/// <param name="Position">Zero-based offset of the term in the filter text.</param>
/// <param name="Length">How much of the text the term covers.</param>
internal sealed record RowFilterTerm(string Column, RowFilterOperator Operator, string? Value, int Position, int Length)
{
    /// <summary>Whether the term compares with an ordering or equality operator - what needs a btree opclass.</summary>
    public bool IsComparison => Operator is not (RowFilterOperator.Contains or RowFilterOperator.IsNull or RowFilterOperator.IsNotNull);
}

/// <summary>A filter, parsed: the terms that resolved, and everything that did not, with positions.</summary>
/// <param name="Terms">The terms, in the order typed. ANDed.</param>
/// <param name="Errors">What could not be parsed or resolved.</param>
internal sealed record RowFilterParse(IReadOnlyList<RowFilterTerm> Terms, IReadOnlyList<SearchGrammarError> Errors)
{
    /// <summary>Nothing typed.</summary>
    public static RowFilterParse Empty { get; } = new([], []);

    /// <summary>Whether anything failed.</summary>
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// The verdict strip for a row read: each filter term and the sort, with whether an index serves it -
/// the same chips and levels the documents list draws.
/// </summary>
internal sealed record RowFilterVerdict
{
    /// <summary>Nothing typed and no sort: green, and empty.</summary>
    public static RowFilterVerdict Empty { get; } = new();

    /// <summary>The terms that resolved.</summary>
    public IReadOnlyList<RowFilterTerm> Terms { get; init; } = [];

    /// <summary>What could not be parsed or resolved, with positions.</summary>
    public IReadOnlyList<SearchGrammarError> Errors { get; init; } = [];

    /// <summary>One chip per term, one per error, and the sort's chip last.</summary>
    public IReadOnlyList<SearchChip> Chips { get; init; } = [];

    /// <summary>The worst of the filter chips.</summary>
    public IndexVerdictLevel FilterLevel { get; init; } = IndexVerdictLevel.Green;

    /// <summary>The sort's verdict.</summary>
    public IndexVerdictLevel SortLevel { get; init; } = IndexVerdictLevel.Green;

    /// <summary>Whether anything failed to parse or resolve.</summary>
    public bool HasErrors => Errors.Count > 0;

    /// <summary>The worst of everything.</summary>
    public IndexVerdictLevel Level => HasErrors
        ? IndexVerdictLevel.Red
        : FilterLevel > SortLevel ? FilterLevel : SortLevel;
}

/// <summary>How a page of rows is walked.</summary>
internal enum TableRowPagingMode
{
    /// <summary>Keyset over the row key, optionally after a sort column.</summary>
    Key,

    /// <summary>Keyset over <c>ctid</c>, for a heap table or materialized view with no key (PG 14+).</summary>
    Ctid,

    /// <summary><c>limit … offset …</c>, capped at <see cref="TableRowQueryBuilder.MaxOffset" />.</summary>
    Offset,
}

/// <summary>What the page asked for.</summary>
internal enum TableRowPagingPreference
{
    /// <summary>Keyset where the relation allows it, offset where it does not.</summary>
    Keyset,

    /// <summary>Offset, whatever the relation allows - the "page N" mode.</summary>
    Offset,
}

/// <summary>
/// A position in a keyset walk: the last row's sort value and key, and what sort they belong to.
/// </summary>
/// <remarks>
/// <para>
/// The key half is always the raw <c>::text</c> of the key columns (or of <c>ctid</c>), read beside the
/// capped cells rather than out of them, so a cell cut at a kilobyte never breaks a cursor. It goes back
/// to Postgres as untyped text, and the column's own input function parses it.
/// </para>
/// <para>
/// The sort it belongs to travels with it, because a cursor pasted into a page sorted differently is a
/// position in a different walk: the service refuses it rather than skip rows.
/// </para>
/// </remarks>
/// <param name="SortColumn">The sort column, or <see langword="null" /> for the key order.</param>
/// <param name="Descending">Whether the walk is descending.</param>
/// <param name="SortValue">The last row's sort value as text, or <see langword="null" /> when it was NULL.</param>
/// <param name="Key">The last row's key values as text, in key order; one value, the ctid, for <see cref="IsCtid" />.</param>
/// <param name="IsCtid">Whether the key is the row's physical position rather than its key.</param>
internal sealed record TableRowCursor(
    string? SortColumn,
    bool Descending,
    string? SortValue,
    IReadOnlyList<string> Key,
    bool IsCtid = false)
{
    /// <summary>
    /// The cursor as one opaque <c>?cursor=</c> value: Base64url of length-prefixed fields, so any text -
    /// a comma, a colon, the difference between NULL and an empty string - survives.
    /// </summary>
    public static string Encode(TableRowCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var builder = new StringBuilder();

        builder.Append('1');
        builder.Append(cursor.Descending ? 'd' : 'a');
        builder.Append(cursor.IsCtid ? 'c' : 'k');
        Field(builder, cursor.SortColumn);
        Field(builder, cursor.SortValue);
        builder.Append(cursor.Key.Count.ToString(CultureInfo.InvariantCulture)).Append(';');

        foreach (string value in cursor.Key)
        {
            Field(builder, value);
        }

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>Reads a <c>?cursor=</c> value, or <see langword="null" /> when it is not one.</summary>
    public static TableRowCursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            string text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));

            if (text.Length < 4 || text[0] != '1' || text[1] is not ('a' or 'd') || text[2] is not ('c' or 'k'))
            {
                return null;
            }

            int position = 3;

            if (!TryField(text, ref position, out string? sortColumn) || !TryField(text, ref position, out string? sortValue))
            {
                return null;
            }

            int semicolon = text.IndexOf(';', position);

            if (semicolon < 0
                || !int.TryParse(text.AsSpan(position, semicolon - position), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                || count is < 1 or > 32)
            {
                return null;
            }

            position = semicolon + 1;
            List<string> key = new(count);

            for (int i = 0; i < count; i++)
            {
                if (!TryField(text, ref position, out string? part) || part is null)
                {
                    return null;
                }

                key.Add(part);
            }

            return position == text.Length
                ? new TableRowCursor(sortColumn, text[1] == 'd', sortValue, key, text[2] == 'c')
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void Field(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("n;");
            return;
        }

        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static bool TryField(string text, ref int position, out string? value)
    {
        value = null;

        if (position + 1 < text.Length && text[position] == 'n' && text[position + 1] == ';')
        {
            position += 2;
            return true;
        }

        int colon = text.IndexOf(':', position);

        if (colon < 0
            || !int.TryParse(text.AsSpan(position, colon - position), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
            || colon + 1 + length > text.Length)
        {
            return false;
        }

        value = text.Substring(colon + 1, length);
        position = colon + 1 + length;
        return true;
    }
}

/// <summary>What a page of rows is asked for with.</summary>
internal sealed record TableRowRequest
{
    /// <summary>The filter, in <see cref="RowFilterGrammar" />'s syntax, or <see langword="null" />.</summary>
    public string? Filter { get; init; }

    /// <summary>The column to sort by, or <see langword="null" /> for the row key's order.</summary>
    public string? SortColumn { get; init; }

    /// <summary>The direction.</summary>
    public SortDirection Direction { get; init; } = SortDirection.Ascending;

    /// <summary>Where a keyset walk continues from - <see cref="TableRowPage.NextCursor" /> of the page before.</summary>
    public TableRowCursor? Cursor { get; init; }

    /// <summary>The offset, for offset paging.</summary>
    public int Offset { get; init; }

    /// <summary>Keyset where possible, or offset.</summary>
    public TableRowPagingPreference Paging { get; init; } = TableRowPagingPreference.Keyset;

    /// <summary>Rows per page; zero or less means <see cref="MartenStudioOptions.DefaultPageSize" />.</summary>
    public int PageSize { get; init; }

    /// <summary>The columns to show, by name; <see langword="null" /> for all of them.</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>
    /// Read even though a filter or sort column is unindexed on a relation above the threshold - the
    /// documents list's "Run anyway".
    /// </summary>
    public bool RunAnyway { get; init; }

    /// <summary>Whether this is a first page rather than a page turn.</summary>
    public bool IsFirstPage => Cursor is null && Offset <= 0;
}

/// <summary>A Postgres error, as a value: its SQLSTATE and a sentence a person can act on.</summary>
/// <param name="SqlState">The five-character SQLSTATE, or empty for a failure that never reached Postgres.</param>
/// <param name="Sentence">What happened, in plain words, naming the option that governs it where one does.</param>
/// <param name="PostgresMessage">Postgres' (or Npgsql's) own message.</param>
internal sealed record TableRowError(string SqlState, string Sentence, string? PostgresMessage);

/// <summary>One foreign key a column is part of, as the column header shows it.</summary>
/// <param name="ForeignKey">The constraint name.</param>
/// <param name="Schema">The parent's schema, or <see langword="null" /> when this visitor may not see it.</param>
/// <param name="Table">The parent table, likewise.</param>
internal sealed record TableRowColumnReference(string ForeignKey, string? Schema, string? Table);

/// <summary>One column of a row read, as the grid's header draws it.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Position">Its <c>attnum</c>.</param>
/// <param name="Type">Its type, as <c>format_type</c> spells it.</param>
/// <param name="Nullable">Whether it accepts NULL.</param>
/// <param name="Sortable">Whether it can be sorted and compared (a default btree opclass).</param>
/// <param name="Shown">
/// Whether its cells are read. <see langword="false" /> only for a name the studio cannot quote, which is
/// listed and never read.
/// </param>
/// <param name="Kind">What its cells are, for styling.</param>
/// <param name="KeyPosition">Its one-based position in the row key, or <see langword="null" />.</param>
/// <param name="References">The foreign keys it is part of - the header's "→ parent" marker.</param>
/// <param name="Index">Whether a valid index leads with it.</param>
/// <param name="IndexReason">The sentence behind <paramref name="Index" />.</param>
/// <param name="Comment">The column comment.</param>
internal sealed record TableRowColumn(
    string Name,
    int Position,
    string Type,
    bool Nullable,
    bool Sortable,
    bool Shown,
    SqlCellKind Kind,
    int? KeyPosition,
    IReadOnlyList<TableRowColumnReference> References,
    IndexVerdictLevel Index,
    string IndexReason,
    string? Comment);

/// <summary>One row of a page.</summary>
/// <param name="Key">
/// The row key's values as raw text - never cut - keyed by column, for row detail and for "filter by this".
/// <see langword="null" /> for a relation with no key.
/// </param>
/// <param name="Locator">
/// For a heap table or materialized view with no key, the row's <c>ctid</c>: enough to expand one of its
/// cells now, and nothing more - an update moves it.
/// </param>
/// <param name="Cells">The cells, one per <see cref="TableRowPage.Columns" /> entry, cut on the server.</param>
internal sealed record TableRow(
    IReadOnlyDictionary<string, string>? Key,
    string? Locator,
    IReadOnlyList<SqlCell> Cells);

/// <summary>How the page was walked, for the paging note.</summary>
/// <param name="Mode">Keyset on the key, keyset on ctid, or offset.</param>
/// <param name="OrderColumns">What the walk is ordered by after the sort column: the key, <c>ctid</c>, or the tiebreak columns.</param>
/// <param name="SortColumn">The sort column, or <see langword="null" /> for the key order.</param>
/// <param name="Direction">The direction.</param>
/// <param name="Offset">The offset, for offset paging.</param>
/// <param name="Note">Why this mode, when it is not the obvious one.</param>
internal sealed record TableRowPaging(
    TableRowPagingMode Mode,
    IReadOnlyList<string> OrderColumns,
    string? SortColumn,
    SortDirection Direction,
    int Offset,
    string? Note);

/// <summary>What happened to a page read.</summary>
internal enum TableRowPageState
{
    /// <summary>The rows are here.</summary>
    Loaded,

    /// <summary>
    /// Not read: the relation is above the threshold and a filter or sort column is unindexed. The SQL is
    /// here; "Run anyway" reads it.
    /// </summary>
    Withheld,

    /// <summary>The filter, sort, cursor or offset cannot be used; <see cref="TableRowPage.Reason" /> says why.</summary>
    Invalid,

    /// <summary>A gate refused; <see cref="TableRowPage.Refusal" /> says which.</summary>
    Refused,

    /// <summary>Postgres refused the read; <see cref="TableRowPage.Error" /> says how.</summary>
    Failed,
}

/// <summary>One page of a non-Marten relation's rows.</summary>
internal sealed record TableRowPage
{
    /// <summary>What happened.</summary>
    public TableRowPageState State { get; init; }

    /// <summary>The gate that refused, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal or an invalid request.</summary>
    public string? Reason { get; init; }

    /// <summary>The Postgres error, for <see cref="TableRowPageState.Failed" />.</summary>
    public TableRowError? Error { get; init; }

    /// <summary>The relation's schema, as the catalog has it.</summary>
    public string? Schema { get; init; }

    /// <summary>The relation's name, as the catalog has it.</summary>
    public string? Name { get; init; }

    /// <summary>What kind of relation.</summary>
    public DatabaseObjectKind Kind { get; init; }

    /// <summary>Whose it is - "Marten-managed" for a flat-table projection's table.</summary>
    public DatabaseObjectOwnership? Ownership { get; init; }

    /// <summary>The row key, or <see langword="null" /> when it has none.</summary>
    public DatabaseRowKey? RowKey { get; init; }

    /// <summary><c>reltuples</c>, or <see langword="null" /> when Postgres has no estimate.</summary>
    public long? EstimatedRows { get; init; }

    /// <summary>Whether row-level security is on: rows the role's policies hide are not here.</summary>
    public bool RowSecurity { get; init; }

    /// <summary>The columns shown, in order - every cell list lines up with this.</summary>
    public IReadOnlyList<TableRowColumn> Columns { get; init; } = [];

    /// <summary>Every column the relation has, for the column chooser.</summary>
    public IReadOnlyList<TableRowColumn> AvailableColumns { get; init; } = [];

    /// <summary>The rows.</summary>
    public IReadOnlyList<TableRow> Rows { get; init; } = [];

    /// <summary>Whether there is a next page.</summary>
    public bool HasMore { get; init; }

    /// <summary>Where the next keyset page starts.</summary>
    public TableRowCursor? NextCursor { get; init; }

    /// <summary>The next page's offset, for offset paging.</summary>
    public int? NextOffset { get; init; }

    /// <summary>How the page was walked.</summary>
    public TableRowPaging? Paging { get; init; }

    /// <summary>The filter and sort, with their index verdicts.</summary>
    public RowFilterVerdict Verdict { get; init; } = RowFilterVerdict.Empty;

    /// <summary>The exact SQL text, for "Show SQL".</summary>
    public string Sql { get; init; } = string.Empty;

    /// <summary>Its parameter names - never their values.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>A refused page.</summary>
    public static TableRowPage Refused(DatabaseRefusal refusal, string reason) =>
        new() { State = TableRowPageState.Refused, Refusal = refusal, Reason = reason };
}

/// <summary>One row, every column read, cut only at the detail cap.</summary>
internal sealed record TableRowDetail
{
    /// <summary>The gate that refused, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal or an unusable key.</summary>
    public string? Reason { get; init; }

    /// <summary>The Postgres error, when the read failed.</summary>
    public TableRowError? Error { get; init; }

    /// <summary>Whether a row with that key exists.</summary>
    public bool Found { get; init; }

    /// <summary>The relation's schema.</summary>
    public string? Schema { get; init; }

    /// <summary>The relation's name.</summary>
    public string? Name { get; init; }

    /// <summary>Whose it is.</summary>
    public DatabaseObjectOwnership? Ownership { get; init; }

    /// <summary>The row key.</summary>
    public DatabaseRowKey? RowKey { get; init; }

    /// <summary>The key values, as read back - raw text.</summary>
    public IReadOnlyDictionary<string, string>? Key { get; init; }

    /// <summary>Every column.</summary>
    public IReadOnlyList<TableRowColumn> Columns { get; init; } = [];

    /// <summary>One cell per column, cut at <see cref="TableRowCaps.Detail" />.</summary>
    public IReadOnlyList<SqlCell> Cells { get; init; } = [];

    /// <summary>Whether row-level security is on.</summary>
    public bool RowSecurity { get; init; }

    /// <summary>The exact SQL text.</summary>
    public string Sql { get; init; } = string.Empty;

    /// <summary>Its parameter names.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>A refused read.</summary>
    public static TableRowDetail Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>What became of one outbound reference.</summary>
internal enum RowReferenceState
{
    /// <summary>The parent row exists.</summary>
    Present,

    /// <summary>
    /// The parent row does not exist - a <c>NOT VALID</c> foreign key, or one whose check was deferred or
    /// disabled, can point at nothing.
    /// </summary>
    Missing,

    /// <summary>A key column is NULL, so the row references nothing.</summary>
    NoReference,

    /// <summary>The parent is in a schema this visitor may not see: never read, never named.</summary>
    NotVisible,

    /// <summary>
    /// The parent is a Marten document table: never read raw, because tenancy and soft delete apply there.
    /// <see cref="RowOutboundReference.DocumentAlias" /> and <see cref="RowOutboundReference.DocumentId" />
    /// open it in Documents.
    /// </summary>
    MartenDocument,

    /// <summary>The parent's rows may not be read here; <see cref="RowOutboundReference.Reason" /> says why.</summary>
    NotChecked,

    /// <summary>The check itself failed; <see cref="RowOutboundReference.Reason" /> says how.</summary>
    Failed,
}

/// <summary>One foreign key out of the row, resolved.</summary>
/// <param name="ForeignKey">The constraint name.</param>
/// <param name="Columns">This row's columns, in constraint order.</param>
/// <param name="Values">This row's values for them, raw text; a NULL is <see langword="null" />.</param>
/// <param name="Schema">The parent's schema, or <see langword="null" /> when not visible.</param>
/// <param name="Table">The parent table, or <see langword="null" /> when not visible.</param>
/// <param name="LinkedColumns">The parent's columns, in constraint order - empty when not visible.</param>
/// <param name="Validated"><see langword="false" /> for a <c>NOT VALID</c> key, which can dangle.</param>
/// <param name="State">What became of it.</param>
/// <param name="TargetsRowKey">
/// Whether the key points at the parent's row key, so the page can link straight to the parent row
/// (<paramref name="ParentKey" />); otherwise it links to the parent's rows filtered by
/// <paramref name="ParentFilter" />.
/// </param>
/// <param name="ParentKey">The parent row's key, column to raw value, when <paramref name="TargetsRowKey" />.</param>
/// <param name="ParentFilter">A filter, in the row grammar, selecting the parent's rows this one points at.</param>
/// <param name="DocumentAlias">For a Marten document parent, its collection alias.</param>
/// <param name="DocumentId">For a Marten document parent, the id this row points at.</param>
/// <param name="StoreKey">For a Marten document parent, the store that declares it.</param>
/// <param name="Reason">Why it was not checked, or how the check failed.</param>
internal sealed record RowOutboundReference(
    string ForeignKey,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string?> Values,
    string? Schema,
    string? Table,
    IReadOnlyList<string> LinkedColumns,
    bool Validated,
    RowReferenceState State,
    bool TargetsRowKey,
    IReadOnlyDictionary<string, string>? ParentKey,
    string? ParentFilter,
    string? DocumentAlias,
    string? DocumentId,
    string? StoreKey,
    string? Reason);

/// <summary>What became of one inbound reference.</summary>
internal enum RowInboundState
{
    /// <summary>Counted, up to the cap.</summary>
    Counted,

    /// <summary>A referenced column of this row is NULL, so nothing can point at it through this key.</summary>
    NoReference,

    /// <summary>The pointing table is a Marten document table: browsed in Documents, never counted raw.</summary>
    MartenDocument,

    /// <summary>The pointing table's rows may not be read here.</summary>
    NotChecked,

    /// <summary>The count failed.</summary>
    Failed,
}

/// <summary>One foreign key into the row's relation, counted for this row.</summary>
/// <param name="ForeignKey">The constraint name.</param>
/// <param name="Schema">The pointing table's schema.</param>
/// <param name="Table">The pointing table.</param>
/// <param name="Columns">The pointing columns.</param>
/// <param name="LinkedColumns">This relation's columns they point at.</param>
/// <param name="Values">This row's values for <paramref name="LinkedColumns" />, raw text.</param>
/// <param name="State">What became of it.</param>
/// <param name="Count">How many rows point here, up to <see cref="TableRowQueryBuilder.InboundCap" /> less one.</param>
/// <param name="More">Whether there are more than <paramref name="Count" /> - "1,000+".</param>
/// <param name="ChildFilter">A filter, in the row grammar, selecting the pointing rows.</param>
/// <param name="DocumentAlias">For a Marten document table pointing here, its collection alias.</param>
/// <param name="OnDelete">What deleting this row would do to them.</param>
/// <param name="Reason">Why it was not counted, or how the count failed.</param>
internal sealed record RowInboundReference(
    string ForeignKey,
    string Schema,
    string Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> LinkedColumns,
    IReadOnlyList<string?> Values,
    RowInboundState State,
    long? Count,
    bool More,
    string? ChildFilter,
    string? DocumentAlias,
    string OnDelete,
    string? Reason);

/// <summary>What a row points at and what points at it.</summary>
internal sealed record TableRowReferences
{
    /// <summary>The gate that refused, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal or an unusable key.</summary>
    public string? Reason { get; init; }

    /// <summary>The Postgres error, when the row itself could not be read.</summary>
    public TableRowError? Error { get; init; }

    /// <summary>Whether the row exists.</summary>
    public bool Found { get; init; }

    /// <summary>Foreign keys out of the relation, with this row's values.</summary>
    public IReadOnlyList<RowOutboundReference> Outbound { get; init; } = [];

    /// <summary>Foreign keys into the relation from tables this visitor may see, counted for this row.</summary>
    public IReadOnlyList<RowInboundReference> Inbound { get; init; } = [];

    /// <summary>Every statement run, in order - which is also the proof of what was not read.</summary>
    public IReadOnlyList<string> Statements { get; init; } = [];

    /// <summary>A refused read.</summary>
    public static TableRowReferences Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>An exact count, or why there is none.</summary>
internal sealed record TableRowCount
{
    /// <summary>The gate that refused, <see cref="DatabaseRefusal.NotApplicable" /> for a view, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal or an unusable filter.</summary>
    public string? Reason { get; init; }

    /// <summary>The Postgres error - a timeout, most often.</summary>
    public TableRowError? Error { get; init; }

    /// <summary>
    /// The count: exact, or the estimate marked as refused above <c>ExactCountThreshold</c>, exactly as
    /// the documents list's "=" answers.
    /// </summary>
    public DocumentCount Count { get; init; } = DocumentCount.Unavailable;

    /// <summary>For a filtered count, whether it stopped at <see cref="Cap" /> - "more than".</summary>
    public bool Bounded { get; init; }

    /// <summary>How many matching rows a filtered count looks at before it stops.</summary>
    public long? Cap { get; init; }

    /// <summary>The filter, as parsed.</summary>
    public RowFilterVerdict Verdict { get; init; } = RowFilterVerdict.Empty;

    /// <summary>The count's SQL text, when one ran.</summary>
    public string Sql { get; init; } = string.Empty;

    /// <summary>Its parameter names.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>A refused count.</summary>
    public static TableRowCount Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>One cell, whole - up to <see cref="MartenStudioOptions.MaxInlineDocumentBytes" />.</summary>
internal sealed record TableCellValue
{
    /// <summary>The gate that refused, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal, an unknown column or an unusable key.</summary>
    public string? Reason { get; init; }

    /// <summary>The Postgres error, when the read failed.</summary>
    public TableRowError? Error { get; init; }

    /// <summary>Whether the row exists.</summary>
    public bool Found { get; init; }

    /// <summary>The column, as the catalog has it.</summary>
    public TableRowColumn? Column { get; init; }

    /// <summary>Whether the value is NULL.</summary>
    public bool IsNull { get; init; }

    /// <summary>The value's text - every kind but <c>bytea</c>.</summary>
    public string? Text { get; init; }

    /// <summary>The value's bytes, for <c>bytea</c>.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Whether it was cut at <see cref="Cap" />.</summary>
    public bool Truncated { get; init; }

    /// <summary>The value's full length in bytes (of its text form, for everything but <c>bytea</c>).</summary>
    public long FullLength { get; init; }

    /// <summary>The cap it was read under: characters, or bytes for <c>bytea</c>.</summary>
    public int Cap { get; init; }

    /// <summary>The exact SQL text.</summary>
    public string Sql { get; init; } = string.Empty;

    /// <summary>Its parameter names.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>A refused read.</summary>
    public static TableCellValue Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}
