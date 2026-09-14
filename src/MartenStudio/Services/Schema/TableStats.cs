namespace MartenStudio.Services.Schema;

/// <summary>
/// One table in the store's schemas, with what the database knows about its size and its use.
/// </summary>
/// <param name="Schema">The schema it lives in.</param>
/// <param name="Table">The table name.</param>
/// <param name="TotalBytes">Heap plus indexes plus toast.</param>
/// <param name="HeapBytes">The main fork alone.</param>
/// <param name="IndexBytes">Every index on it.</param>
/// <param name="EstimatedRows">
/// <c>reltuples</c>, which is a planner estimate and is negative until the table has been analyzed.
/// Always rendered with a <c>~</c> (D8).
/// </param>
/// <param name="LiveRows"><c>n_live_tup</c>, or <see langword="null" /> when the collector has nothing.</param>
/// <param name="DeadRows"><c>n_dead_tup</c>, or <see langword="null" />.</param>
/// <param name="SequentialScans"><c>seq_scan</c>, or <see langword="null" />.</param>
/// <param name="IndexScans"><c>idx_scan</c>, or <see langword="null" />.</param>
/// <param name="LastVacuum">The later of the two vacuum timestamps.</param>
/// <param name="LastAnalyze">The later of the two analyze timestamps.</param>
/// <param name="CollectionAlias">
/// The document type whose table this is, when it is one. That is the whole reason this screen is better
/// than <c>\dt+</c>: the studio knows which <c>mt_doc_*</c> table belongs to which registered type.
/// </param>
/// <param name="DocumentTypeName">The .NET type behind the alias, when there is one.</param>
/// <param name="IsEventTable">Whether the table belongs to the event store's schema.</param>
internal sealed record TableStats(
    string Schema,
    string Table,
    long TotalBytes,
    long HeapBytes,
    long IndexBytes,
    long EstimatedRows,
    long? LiveRows,
    long? DeadRows,
    long? SequentialScans,
    long? IndexScans,
    DateTimeOffset? LastVacuum,
    DateTimeOffset? LastAnalyze,
    string? CollectionAlias,
    string? DocumentTypeName,
    bool IsEventTable)
{
    /// <summary>The qualified name, which is what a person copies into psql.</summary>
    public string QualifiedName => Schema + "." + Table;

    /// <summary>Whether <c>reltuples</c> has anything to say yet.</summary>
    public bool HasRowEstimate => EstimatedRows >= 0;
}

/// <summary>The Tables tab's answer for one database.</summary>
/// <param name="Tables">Every ordinary table in the store's schemas, largest first.</param>
/// <param name="Schemas">The schemas that were asked about.</param>
/// <param name="DatabaseBytes"><c>pg_database_size</c> for the whole database.</param>
/// <param name="Reason">Why nothing could be read, or <see langword="null" />.</param>
internal sealed record SchemaTables(
    IReadOnlyList<TableStats> Tables,
    IReadOnlyList<string> Schemas,
    long DatabaseBytes,
    string? Reason)
{
    /// <summary>Nothing read yet.</summary>
    public static SchemaTables Empty { get; } = new([], [], 0, null);

    /// <summary>The read failed, and this is why.</summary>
    public static SchemaTables Unavailable(string reason) => new([], [], 0, reason);
}
