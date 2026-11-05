namespace MartenStudio.Services.Find;

/// <summary>
/// Which tenants a Find reads across when no tenant is selected (1.2, Find by id).
/// </summary>
/// <remarks>
/// <para>
/// A probe of a conjoined collection with no tenant reads at most 21 rows, and under a store policy some of those rows could be tenants
/// the visitor may not address: they would fill the limit and the tenant the visitor may address would never be read, so "Nothing found"
/// would be false. With a policy in force the probe is therefore always narrowed to the tenants the header lists for the visitor
/// (<c>tenant_id = any(@tenants)</c>) - the same listing the selector offers.
/// </para>
/// <para>
/// <b>It is the same in every case, on purpose.</b> That listing is not claimed complete (a filtered list never is - see
/// <c>StudioScopeCatalog.FilterTenantsAsync</c>), and a Find must not tell a visitor a policy filters whether the store has more than
/// 200 tenants, any more than the Tenants page may: the narrowing, and the note that says so, do not depend on how many tenants
/// discovery found, or whether it found any.
/// </para>
/// </remarks>
/// <param name="Only">The tenants to read, or <see langword="null" /> for all of them (no policy in force, a tenant selected, or no tenanted store).</param>
internal sealed record FindTenants(IReadOnlyList<string>? Only)
{
    /// <summary>What a Find says when it read across tenants under a policy: the same words in every case.</summary>
    public const string RestrictedNote =
        "Documents and streams: searched in the tenants the header lists for you. To search another tenant, select it in the header.";

    /// <summary>No restriction: every tenant is read.</summary>
    public static FindTenants Unrestricted { get; } = new((IReadOnlyList<string>?) null);

    /// <summary>The tenants the header lists for the visitor.</summary>
    /// <param name="tenants">Those tenants, possibly none.</param>
    public static FindTenants OnlyThese(IReadOnlyList<string> tenants) => new(tenants);
}
