using MartenStudio.Services.Database;

namespace MartenStudio.Services.Schema;

/// <summary>
/// One table in the store's schemas, with what the database knows about its size and its use, and whose it
/// is.
/// </summary>
/// <param name="Schema">The schema it lives in.</param>
/// <param name="Table">The table name.</param>
/// <param name="TotalBytes">Heap plus indexes plus toast, its partitions' included.</param>
/// <param name="HeapBytes">The main fork alone, its partitions' included.</param>
/// <param name="IndexBytes">Every index on it, its partitions' included.</param>
/// <param name="EstimatedRows">
/// <c>reltuples</c>, which is a planner estimate and is negative until the table has been analyzed.
/// Always rendered with a <c>~</c> (D8). For a partitioned table, the sum over its partitions.
/// </param>
/// <param name="LiveRows"><c>n_live_tup</c>, or <see langword="null" /> when the collector has nothing.</param>
/// <param name="DeadRows"><c>n_dead_tup</c>, or <see langword="null" />.</param>
/// <param name="SequentialScans"><c>seq_scan</c>, or <see langword="null" />.</param>
/// <param name="IndexScans"><c>idx_scan</c>, or <see langword="null" />.</param>
/// <param name="LastVacuum">The later of the two vacuum timestamps, over its partitions too.</param>
/// <param name="LastAnalyze">The later of the two analyze timestamps, over its partitions too.</param>
/// <param name="CollectionAlias">
/// The document collection whose table this is, when it is one of <em>this</em> store's visible document
/// types - the link to Documents. That is the whole reason this screen is better than <c>\dt+</c>: the
/// studio knows which <c>mt_doc_*</c> table belongs to which registered type.
/// </param>
/// <param name="DocumentTypeName">The .NET type behind the alias, when there is one.</param>
/// <param name="Ownership">
/// Whose it is, by the database browser's own rules (<see cref="DatabaseObjectClassifier" />): a document
/// collection, the event store, a Marten-managed projection or extended table, Marten's infrastructure, or
/// not Marten at all - with the "recognised by name" hint for the last. A hidden document type's table is
/// never a <see cref="TableStats" /> at all.
/// </param>
/// <param name="OwnedByThisStore">
/// Whether it is the scope's store that declares it (or nobody), which is what makes a link to that store's
/// Documents or Streams the right link. Another registered store's table in a shared schema is labelled,
/// never linked into this store's screens.
/// </param>
/// <param name="IsPartitioned">Whether it is a partitioned table, whose partitions are rolled into this row.</param>
/// <param name="PartitionCount">
/// How many partitions hang directly off it - or <see langword="null" /> when that is withheld. A
/// per-tenant partition count is the number of tenants, so it is shown only past the database browser's
/// gate (<c>Capabilities.BrowseDatabase</c> and the write policy's yes); <see cref="SchemaTables.PartitionCountsWithheld" />
/// says why when it is not. The partitions' names are never here, whoever asks.
/// </param>
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
    DatabaseObjectOwnership Ownership,
    bool OwnedByThisStore = true,
    bool IsPartitioned = false,
    int? PartitionCount = null)
{
    /// <summary>The qualified name, which is what a person copies into psql.</summary>
    public string QualifiedName => Schema + "." + Table;

    /// <summary>Whether <c>reltuples</c> has anything to say yet.</summary>
    public bool HasRowEstimate => EstimatedRows >= 0;

    /// <summary>Whether it is one of the event store's own tables.</summary>
    public bool IsEventTable => Ownership.Owner == DatabaseObjectOwner.MartenEventStore;
}

/// <summary>The Tables tab's answer for one database.</summary>
/// <param name="Tables">
/// Every ordinary and partitioned table in the store's schemas, largest first - no partition, and no hidden
/// document type's table.
/// </param>
/// <param name="Schemas">The schemas that were asked about.</param>
/// <param name="DatabaseBytes"><c>pg_database_size</c> for the whole database.</param>
/// <param name="Reason">Why nothing could be read, or <see langword="null" />.</param>
/// <param name="PartitionCountsWithheld">
/// Why the partition counts are not shown, naming the gate that is shut - or <see langword="null" /> when
/// they are.
/// </param>
internal sealed record SchemaTables(
    IReadOnlyList<TableStats> Tables,
    IReadOnlyList<string> Schemas,
    long DatabaseBytes,
    string? Reason,
    string? PartitionCountsWithheld = null)
{
    /// <summary>Nothing read yet.</summary>
    public static SchemaTables Empty { get; } = new([], [], 0, null);

    /// <summary>The read failed, and this is why.</summary>
    public static SchemaTables Unavailable(string reason) => new([], [], 0, reason);
}
