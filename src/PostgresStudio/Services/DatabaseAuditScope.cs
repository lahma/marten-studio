namespace PostgresStudio.Services;

/// <summary>
/// The database module's own scope as the Activity ring records it (D34): the store, the database and the tenant an action was aimed at.
/// Read only by <see cref="DatabaseAuditGate" /> and by the checks that look for what this studio did to one database.
/// </summary>
/// <param name="StoreKey">The store the action was aimed at.</param>
/// <param name="DatabaseId">The database's identity, never a connection string.</param>
/// <param name="TenantId">The tenant in scope, or <see langword="null" /> for a view spanning all of them.</param>
internal sealed record DatabaseAuditScope(string StoreKey, string DatabaseId, string? TenantId)
{
    /// <summary>What the Scope cell says of a view that is not filtered to one tenant.</summary>
    private const string AllTenants = "all";

    /// <summary>The scope as the ring records it, under <paramref name="kind" />: the database module's key.</summary>
    /// <param name="kind">The database module's key, which names the gate that judges the entry.</param>
    public StudioAuditScope ToAuditScope(string kind) => new(
        kind,
        [StoreKey, DatabaseId, TenantId ?? AllTenants],
        "Store " + StoreKey + ", database " + DatabaseId + ", tenant " + (TenantId ?? AllTenants),
        this);
}

/// <summary>What a reader of the ring asks of an entry the database module recorded.</summary>
internal static class StudioActionLogEntryExtensions
{
    /// <summary>The database scope of <paramref name="entry" />, or <see langword="null" /> for an entry another module recorded.</summary>
    /// <param name="entry">The entry.</param>
    public static DatabaseAuditScope? DatabaseScope(this StudioActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Scope.Value as DatabaseAuditScope;
    }
}
