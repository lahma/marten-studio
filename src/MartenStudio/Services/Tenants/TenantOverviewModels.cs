namespace MartenStudio.Services.Tenants;

/// <summary>What the Tenants page asks for (1.2): a tenant-id prefix and a zero-based page.</summary>
internal sealed record TenantOverviewRequest
{
    /// <summary>Rows a page holds.</summary>
    public const int PageSize = 25;

    /// <summary>The longest prefix taken; a longer one is cut.</summary>
    public const int MaxFilterLength = 256;

    /// <summary>Only tenants whose id starts with this (ordinal, ignoring case), or <see langword="null" /> for all.</summary>
    public string? Filter { get; init; }

    /// <summary>
    /// The zero-based page; a page past the end is the last. <see langword="null" /> opens on the page that holds the scope's tenant, or
    /// the first.
    /// </summary>
    public int? Page { get; init; }
}

/// <summary>Why the overview was not given.</summary>
internal enum TenantOverviewRefusal
{
    /// <summary>It was given.</summary>
    None,

    /// <summary>The store policy refuses the store or database.</summary>
    StorePolicy,

    /// <summary>The store is registered and will not build, or the scope names nothing.</summary>
    Unavailable,
}

/// <summary>Where a tenant's rows are, as far as the catalog can say.</summary>
internal enum TenantPlacement
{
    /// <summary>Known by id only (<c>KnownTenantIds</c> on a store with several databases): no database to place it in.</summary>
    NotPlaced,

    /// <summary>The database holds this tenant's data and nobody else's.</summary>
    OwnDatabase,

    /// <summary>A tenant-partitioned table holds a partition for it.</summary>
    OwnPartition,

    /// <summary>A tenant-partitioned table holds no partition for it.</summary>
    NoPartitionYet,

    /// <summary>In tenant-partitioned tables, whether a partition exists not read (it is activity-gated).</summary>
    PartitionedTables,

    /// <summary>Conjoined tables shared with other tenants.</summary>
    SharedTables,

    /// <summary>In a shard of a pooled tenancy, shared with other tenants (partitioned or not).</summary>
    Shard,
}

/// <summary>One cell of the table: what it says, and the longer reading on hover.</summary>
/// <param name="Text">The cell.</param>
/// <param name="Title">The tooltip, or <see langword="null" />.</param>
internal sealed record TenantCell(string Text, string? Title = null)
{
    /// <summary>A moment the cell is about (the newest event's time), drawn as "n minutes ago" beside the text.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>The cell of a number the overview did not read: a dash and why.</summary>
    public static TenantCell Dash(string why) => new("—", why);
}

/// <summary>One tenant in one database.</summary>
/// <param name="DatabaseId">The database's identity, or empty for a tenant known by id only.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="Placement">Where its rows are.</param>
/// <param name="PlacementText">The same in words.</param>
/// <param name="Documents">Estimated documents.</param>
/// <param name="Events">Estimated events.</param>
/// <param name="Streams">Estimated streams.</param>
/// <param name="LastEvent">Its newest event.</param>
/// <param name="Projections">Its projections' progress, or <see langword="null" /> where Marten keeps none per tenant.</param>
/// <param name="ReadProblem">Why this row's database was not read (out of time, unreachable), or <see langword="null" />.</param>
internal sealed record TenantRow(
    string DatabaseId,
    string TenantId,
    TenantPlacement Placement,
    string PlacementText,
    TenantCell Documents,
    TenantCell Events,
    TenantCell Streams,
    TenantCell LastEvent,
    TenantCell? Projections,
    string? ReadProblem);

/// <summary>The whole-table numbers no tenant owns: the visible single-tenanted collections of the scope's database.</summary>
/// <param name="Collections">How many collections.</param>
/// <param name="Documents">Their estimated documents, summed, as a cell.</param>
internal sealed record TenantSharedRow(int Collections, TenantCell Documents);

/// <summary>What the Tenants page draws.</summary>
internal sealed record TenantOverview
{
    /// <summary>Why there is none, or <see cref="TenantOverviewRefusal.None" />.</summary>
    public TenantOverviewRefusal Refusal { get; init; }

    /// <summary>The refusal in words.</summary>
    public string? Reason { get; init; }

    /// <summary>The store keeps no data per tenant: no visible document type is conjoined and neither is the event store.</summary>
    public bool KeepsNoTenants { get; init; }

    /// <summary>The page's rows, in tenant order, at most <see cref="TenantOverviewRequest.PageSize" />.</summary>
    public IReadOnlyList<TenantRow> Rows { get; init; } = [];

    /// <summary>The zero-based page these rows are (clamped).</summary>
    public int Page { get; init; }

    /// <summary>Whether a page follows.</summary>
    public bool HasNext { get; init; }

    /// <summary>The union of the tenants listed was cut at <c>TenantList.MaxListedTenants</c>; never set under a store policy.</summary>
    public bool ListTruncated { get; init; }

    /// <summary>The store policy filters the listing, so it is never claimed to be complete.</summary>
    public bool UnderPolicy { get; init; }

    /// <summary>Tenant discovery is off and the host listed none: there is nothing to list.</summary>
    public bool ListUnavailable { get; init; }

    /// <summary>Some rows are the host's <c>KnownTenantIds</c> on a store with several databases, placed in none.</summary>
    public bool IdsWithoutDatabases { get; init; }

    /// <summary>Whether the estimate and last-event columns are shown (the activity gate, for at least one database on the page).</summary>
    public bool VolumesShown { get; init; }

    /// <summary>Why they are not, naming the option (D4).</summary>
    public string? VolumesWithheldReason { get; init; }

    /// <summary>The projections column exists for at least one row.</summary>
    public bool HasProjectionsColumn { get; init; }

    /// <summary>Marten keeps no progress per tenant here: "Projections run every tenant's events together in this store."</summary>
    public bool ProjectionsAreShared { get; init; }

    /// <summary>Captions under the table: "includes soft-deleted documents", "includes archived events".</summary>
    public IReadOnlyList<string> Captions { get; init; } = [];

    /// <summary>The footer row, or <see langword="null" />.</summary>
    public TenantSharedRow? Shared { get; init; }

    /// <summary>The overview of a scope that could not be given.</summary>
    public static TenantOverview Refused(TenantOverviewRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}
