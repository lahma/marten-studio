namespace MartenStudio.Services.Find;

/// <summary>
/// Which tenants a Find may read across when no tenant is selected (1.2, Find by id).
/// </summary>
/// <remarks>
/// <para>
/// A probe of a conjoined collection with no tenant reads at most 21 rows, and under a store policy some of those rows could be tenants
/// the visitor may not address: they would fill the limit and the tenant the visitor may address would never be read, so "Nothing found"
/// would be false. With a policy in force the probe is therefore narrowed to the tenants the visitor may address
/// (<c>tenant_id = any(@tenants)</c>), taken from the same listing the header's selector offers.
/// </para>
/// <para>
/// That listing is only the whole truth when discovery found every tenant (<c>TenantList.IsComplete</c>). When it did not, nothing
/// honest can be said about "all tenants", so the probe is not made across tenants at all: <see cref="SelectionNeeded" /> says so and
/// the Find asks the visitor to select a tenant.
/// </para>
/// </remarks>
/// <param name="Only">The tenants to read, or <see langword="null" /> for all of them (no policy in force, or a tenant is selected).</param>
/// <param name="SelectionNeeded">Whether conjoined collections and streams are not read across tenants, and a tenant must be selected.</param>
internal sealed record FindTenants(IReadOnlyList<string>? Only, bool SelectionNeeded)
{
    /// <summary>What a Find says when it did not read across tenants.</summary>
    public const string SelectionNeededNote =
        "Your tenant policy limits which tenants you may search and this store has more tenants than can be listed: select a tenant to search documents and streams.";

    /// <summary>No restriction: every tenant is read.</summary>
    public static FindTenants Unrestricted { get; } = new(null, false);

    /// <summary>The whole list of tenants the visitor may address.</summary>
    /// <param name="tenants">Those tenants.</param>
    public static FindTenants OnlyThese(IReadOnlyList<string> tenants) => new(tenants, false);

    /// <summary>Nothing is read across tenants.</summary>
    public static FindTenants Selection { get; } = new(null, true);
}
