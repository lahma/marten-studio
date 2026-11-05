namespace PostgresStudio.Services.Find;

/// <summary>One thing Find found: a link to the page that shows it, which has read it under its own gates (1.2).</summary>
/// <param name="Kind">What it is, as its chip says: "Document", "Stream", "Event", "Table", "Statement".</param>
/// <param name="Title">The hit, in words: "Order ORD-1", "Stream 2f6c…".</param>
/// <param name="Url">The page that opens it, scope included (D9). The visitor clicks it: a link that names another store, database or tenant is followed by the shell as any clicked link is.</param>
/// <param name="Scope">The store, database and tenant the link names, which decides whether the box may open it by itself.</param>
/// <param name="Id">What tells this hit from another of the same kind in the same scope - its identity in the list's key.</param>
/// <param name="TenantChip">The tenant it is in, when the tenant matters.</param>
/// <param name="Mark">"deleted" or "archived".</param>
/// <param name="Detail">One line more, as the visitor may read it.</param>
internal sealed record FindHit(
    string Kind,
    string Title,
    string Url,
    StudioScope Scope,
    string Id,
    string? TenantChip = null,
    string? Mark = null,
    string? Detail = null)
{
    /// <summary>The key a list keeps this hit under: kind, store, database, tenant and identity (D-SCOPE F7).</summary>
    public string Key => string.Join('\u001f', Kind, Scope.StoreKey, Scope.DatabaseId, Scope.TenantId ?? string.Empty, Id);
}

/// <summary>What one contributor found.</summary>
/// <param name="Hits">The hits, in the contributor's own order.</param>
/// <param name="Searched">What it searched, in a phrase ("47 document types"), or <see langword="null" /> when it did not apply to the value.</param>
/// <param name="Notes">What it did not search and why - a sentence each, naming the setting or the selection that would change it.</param>
/// <param name="TenantsNarrowed">Whether it read across tenants in the visitor's tenants only, so the box does not say "all tenants".</param>
internal sealed record FindContribution(IReadOnlyList<FindHit> Hits, string? Searched, IReadOnlyList<string> Notes, bool TenantsNarrowed = false)
{
    /// <summary>A contributor the value did not fit: no hits, no phrase, no notes.</summary>
    public static FindContribution NotApplicable { get; } = new([], null, []);
}

/// <summary>What a Find found.</summary>
/// <param name="Value">What was searched for.</param>
/// <param name="Scope">The scope it was searched in.</param>
/// <param name="Hits">At most <see cref="FindService.MaxHits" />, in the contributors' order.</param>
/// <param name="HitsOmitted">How many more there were than the box shows.</param>
/// <param name="Searched">The phrases of what was searched.</param>
/// <param name="Notes">What was not searched, and why.</param>
/// <param name="Refusal">Why nothing was searched at all, or <see langword="null" />.</param>
/// <param name="TenantsNarrowed">Whether any kind read across tenants in the visitor's tenants only.</param>
internal sealed record FindResult(
    string Value,
    StudioScope Scope,
    IReadOnlyList<FindHit> Hits,
    int HitsOmitted,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> Notes,
    string? Refusal,
    bool TenantsNarrowed = false)
{
    /// <summary>Whether anything was found.</summary>
    public bool Found => Hits.Count > 0;

    /// <summary>A result of nothing searched, for a refusal or an empty box.</summary>
    public static FindResult Nothing(string value, StudioScope scope, string? refusal = null) =>
        new(value, scope, [], 0, [], [], refusal);
}
