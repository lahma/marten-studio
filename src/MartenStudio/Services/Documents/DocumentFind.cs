using PostgresStudio.Services;

namespace MartenStudio.Services.Documents;

/// <summary>One row a find probe found: the tenant it is in (conjoined collections), the subclass it is, and whether it is soft-deleted.</summary>
/// <param name="TenantId">The tenant, or <see langword="null" /> for a collection no tenant owns.</param>
/// <param name="TypeName">The subclass's name when the row is one, or <see langword="null" />.</param>
/// <param name="Deleted">Whether <c>mt_deleted</c> is set.</param>
internal sealed record DocumentFindRow(string? TenantId, string? TypeName, bool Deleted);

/// <summary>One collection that holds the id, with the tenants it is in that the visitor may see.</summary>
/// <param name="Alias">The collection.</param>
/// <param name="Hue">Its colour.</param>
/// <param name="Id">The id as typed.</param>
/// <param name="Rows">At most five admitted tenants' rows.</param>
/// <param name="MoreTenants">How many further admitted tenants hold it, which the page says as "and N more tenants you may see".</param>
/// <param name="MoreMaybe">Whether the probe's row limit was reached, so there may be still more ("or more").</param>
internal sealed record DocumentFindHit(string Alias, int Hue, string Id, IReadOnlyList<DocumentFindRow> Rows, int MoreTenants, bool MoreMaybe);

/// <summary>What a find over the visible collections found.</summary>
/// <param name="Hits">The collections that hold the id.</param>
/// <param name="VisibleTypes">How many visible document types the store has: the number the rail shows, never a hidden type.</param>
/// <param name="PartitionedSkipped">
/// How many tenant-partitioned collections were not probed because no tenant is selected: a probe would plan and lock every tenant's
/// partition and its index.
/// </param>
/// <param name="ProbeLimitReached">Whether the 25-collection bound stopped the search.</param>
/// <param name="Scope">
/// The scope as the resolver matched it - the registered store key and the database's own identity, with the tenant selected - which
/// is what a link carries and what a policy is asked about a tenant.
/// </param>
/// <param name="TenantedSkipped">
/// How many tenanted collections were not read across tenants because the visitor's policy limits the tenants they may address and the
/// store has more than can be listed: they must select a tenant (<c>FindTenants.SelectionNeeded</c>).
/// </param>
internal sealed record DocumentFindResult(
    IReadOnlyList<DocumentFindHit> Hits,
    int VisibleTypes,
    int PartitionedSkipped,
    bool ProbeLimitReached,
    StudioScope? Scope = null,
    int TenantedSkipped = 0)
{
    /// <summary>Nothing searched.</summary>
    public static DocumentFindResult Empty { get; } = new([], 0, 0, false);
}
