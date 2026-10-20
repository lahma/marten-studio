namespace PostgresStudio.Services;

/// <summary>
/// Whether a registered source could be built, and if not, why.
/// </summary>
/// <remarks>
/// A value rather than an exception, because "cannot report" is a state the UI has to draw differently
/// from "nothing there" (plan section 4.8): a source whose connection string is wrong is still a source the
/// application registered, and leaving it out of the picker makes it look as though it never was.
/// </remarks>
/// <param name="IsAvailable">Whether it resolved.</param>
/// <param name="Message">What the failure said, or <see langword="null" />.</param>
/// <param name="At">When it failed, for the provider's failure cache.</param>
internal sealed record StoreAvailability(bool IsAvailable, string? Message, DateTimeOffset? At)
{
    /// <summary>The source resolved.</summary>
    public static StoreAvailability Available { get; } = new(true, null, null);

    /// <summary>The source did not resolve, and this is what it said.</summary>
    public static StoreAvailability Unavailable(string message, DateTimeOffset at) => new(false, message, at);
}

/// <summary>Where a tenant listing came from, which is what the selector says under it.</summary>
internal enum TenantListSource
{
    /// <summary>The host named them in its options (MartenStudio's <c>KnownTenantIds</c>).</summary>
    Configured,

    /// <summary>The source's own configuration carried them (Marten's database descriptors).</summary>
    Descriptor,

    /// <summary>They were read out of the tenant columns of the source's own tables, bounded.</summary>
    Queried,

    /// <summary>Nothing could answer. Not the same as "there are none".</summary>
    Unavailable
}

/// <summary>
/// The tenants the tenant selector offers, and how much of the truth they are.
/// </summary>
/// <param name="Ids">The tenant ids, in order.</param>
/// <param name="IsTruncated">
/// Whether there are more than this. A truncated list turns the selector into a free-text box, because a
/// picker that silently omits tenants is worse than no picker.
/// </param>
/// <param name="Source">Where the ids came from.</param>
internal sealed record TenantList(IReadOnlyList<string> Ids, bool IsTruncated, TenantListSource Source)
{
    /// <summary>How many tenants the selector will list before it becomes a free-text box.</summary>
    public const int MaxListedTenants = 200;

    /// <summary>Nothing could answer - drawn differently from an empty list.</summary>
    public static TenantList Unavailable { get; } = new([], false, TenantListSource.Unavailable);

    /// <summary>
    /// Whether <paramref name="tenantId" /> is one the studio will accept for <paramref name="tenants" />: one of the
    /// listed ones, or - when the listing is truncated - any plausible id, because the whole point of the free-text box
    /// is that the list is incomplete.
    /// </summary>
    /// <param name="tenants">The listing.</param>
    /// <param name="tenantId">The id to accept, or a blank for "all tenants", which is always acceptable.</param>
    public static bool IsAcceptable(TenantList tenants, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return true;
        }

        foreach (string id in tenants.Ids)
        {
            if (string.Equals(id, tenantId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return tenants.IsTruncated && tenantId.Trim().Length == tenantId.Length && tenantId.Length <= MaxTenantIdLength;
    }

    /// <summary>The longest tenant id the free-text box accepts.</summary>
    private const int MaxTenantIdLength = 200;
}
