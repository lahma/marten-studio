namespace MartenStudio.Services.Database;

/// <summary>
/// The database browser's row reads: pages of a non-Marten table, view or materialized view, one row, one
/// cell, an exact count, and what a row points at and what points at it. Nothing here writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated like the SQL console.</b> Every method starts with
/// <see cref="DatabaseAccess.RequireRowAccessAsync" />: <c>Capabilities.BrowseDatabase</c> (off by default,
/// off under <c>ReadOnly</c>), then the scope resolved <b>with no tenant</b> and the capability named - the
/// write policy asked of the database as a whole, because nothing here is filtered by tenant - then
/// <c>BrowsableSchemas</c>, then the relation looked up in the catalog and judged: a Marten document or
/// event table, Marten's bookkeeping, a foreign table and a view over a hidden type's table are refused
/// before any row SQL is built. Every refusal comes back as a value on the result's <c>Refusal</c> and is
/// in the audit ring; only cancellation is thrown.
/// </para>
/// <para>
/// <b>Read-only, bounded, and as the console's role.</b> Every statement runs inside
/// <c>ReadOnlySqlSession.InTransactionAsync</c>: <c>SET TRANSACTION READ ONLY</c>, <c>statement_timeout</c>
/// from <c>QueryTimeout</c>, a three-second <c>lock_timeout</c>, and <c>SET LOCAL ROLE SqlConsoleRole</c>
/// when one is set. Cells are cut on the server, pages are bounded by <c>MaxPageSize</c>, offsets by
/// 10,000, reference counts at "1,000+".
/// </para>
/// <para>
/// <b>Audited as a person would read the trail.</b> The ring gets one entry for opening a relation's rows
/// (the first page of a given filter and sort), one for a filter change, one for opening a row, and one
/// for every refusal - never one per page turn. Filter values and key values are in the application log
/// (events 9235 and 9236) and never in the ring, which any reader of the Activity page can see.
/// </para>
/// <para>
/// The names avoid every verb <c>CapabilityGatingMatrixTests</c> treats as mutating.
/// </para>
/// </remarks>
internal interface ITableRowService
{
    /// <summary>
    /// One page of rows: filtered, sorted, keyset-paged over the row key (or <c>ctid</c>, or offset for a
    /// view), with the SQL and the index verdict - or withheld until "Run anyway" when a large relation's
    /// filter or sort column is unindexed.
    /// </summary>
    Task<TableRowPage> ListRowsAsync(
        StudioScope scope,
        string schema,
        string name,
        TableRowRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>One row by its key: every column, cut only at 256 KB per cell.</summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="key">The row key: column to raw text value, one entry per key column.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<TableRowDetail> GetRowAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What one row points at - each parent row resolved, or <c>missing</c>, or a Documents link for a
    /// Marten document - and how many rows point at it, bounded.
    /// </summary>
    Task<TableRowReferences> GetReferencesAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The exact row count, within <c>ExactCountThreshold</c>, or a bounded count of the rows a filter
    /// matches. Never for a view.
    /// </summary>
    Task<TableRowCount> CountExactAsync(
        StudioScope scope,
        string schema,
        string name,
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One cell's whole value, up to <c>MaxInlineDocumentBytes</c>: the JSON, the bytes, the long text a grid
    /// cell was cut from. A relation with no key is read by the row's <c>ctid</c>, passed as the key
    /// <c>ctid</c>.
    /// </summary>
    Task<TableCellValue> GetCellAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        string column,
        CancellationToken cancellationToken = default);
}
