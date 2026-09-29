using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

namespace MartenStudio.Services.Projections;

/// <summary>
/// How far a daemon operation reaches, which is what it has to be authorized for - as opposed to what
/// the URL it was asked from names.
/// </summary>
/// <remarks>
/// <para>
/// A tenant in the scope selector narrows what the studio <em>reads</em>. It narrows almost nothing the
/// daemon <em>does</em>. Verified against Marten 9.31 and the JasperFx.Events 2.60 it brings:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>AdvanceHighWaterMarkToLatestAsync(tenantId)</c> and <c>TryCorrectProgressInDatabaseAsync(tenantId)</c>
/// find the tenant's database and build a <c>HighWaterDetector</c> over it with no tenant: the first
/// marks the store-global <c>HighWaterMark</c> row, the second rewrites every row of
/// <c>mt_event_progression</c> with no <c>where</c> on a tenant. Both reach every tenant stored in that
/// database.
/// </description></item>
/// <item><description>
/// <c>RebuildProjectionAsync(name, timeout, token)</c> tears the projection's storage down for the whole
/// database (<c>TeardownExistingProjectionStateAsync(Database, name)</c>) and replays every event in it.
/// </description></item>
/// <item><description>
/// <c>RestartHighWaterAgentAsync</c> restarts the database's high-water agent - or, under per-tenant
/// partitioning, every tenant's.
/// </description></item>
/// <item><description>
/// <c>StartAgentAsync(name)</c> and <c>StopAgentAsync(name)</c> act on the one agent the shard name
/// names. That is one tenant's only when the name carries a tenant (<c>{Name}:{ShardKey}:{tenant}</c>,
/// the per-tenant partitioning form); a store-global shard processes every tenant's events, and a name
/// with no colon at all starts every shard of the projection.
/// </description></item>
/// </list>
/// <para>
/// So an operation that reaches a database is authorized as <c>(store, database, null)</c> - "this
/// database as a whole" - and not as the tenant the visitor happened to have selected. The exception is
/// a database that is <em>exclusively</em> that tenant's (<see cref="IsExclusivelyTenantsAsync" />):
/// database-per-tenant, where reaching the database and reaching the tenant are the same thing, and the
/// tenant-scoped question is the equivalent one. A host whose policy allows a tenant and not its shared
/// database is exactly the host this refuses: before it, a visitor holding <c>CorrectProgression</c> for
/// one tenant could advance the high-water mark every other tenant in that database is read against,
/// and their async projections would skip what they had not processed, permanently.
/// </para>
/// </remarks>
internal static class DatabaseReach
{
    /// <summary>
    /// The tenant a shard name belongs to, or <see langword="null" /> when the agent it names processes
    /// more than one tenant's events.
    /// </summary>
    /// <remarks>
    /// <c>ShardName.TryParse</c> rather than a suffix: <c>Foo:acme</c>'s trailing segment is a shard key
    /// and <c>Orders:All:acme</c>'s is a tenant, and only parsing tells them apart - the same rule
    /// <c>ProjectionDataService.BelongsToTenant</c> applies to progression rows. A name that does not
    /// parse is not attributable to a tenant, so it reaches the database.
    /// </remarks>
    public static string? TenantOfShard(string shardName) =>
        ShardName.TryParse(shardName, out ShardName? shard) && shard?.TenantId is { Length: > 0 } tenantId
            ? tenantId
            : null;

    /// <summary>
    /// Whether <paramref name="database" /> holds <paramref name="tenantId" />'s data and nobody else's,
    /// so that an operation on the whole database reaches that tenant and no other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three conditions, each one a way a database can hold more than its one listed tenant:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// Its <c>TenantIds</c> are exactly <c>[tenantId]</c>, compared after
    /// <c>TenantIdStyle.MaybeCorrectTenantId</c> - which is how every tenancy stores them. A conjoined
    /// single database lists none; a shard or a static database with several tenants lists them all.
    /// </description></item>
    /// <item><description>
    /// It is not the tenancy's default database, where untenanted writes land (<c>AsDefault()</c> on a
    /// static multi-tenancy). A tenancy with no default - <c>MasterTableTenancy</c> and
    /// <c>ShardedTenancy</c> throw from <c>Default</c> - has none to be.
    /// </description></item>
    /// <item><description>
    /// No other database of the store is the same physical database. <c>MasterTableTenancy</c> builds one
    /// <c>MartenDatabase</c> per tenant row, each listing only its own tenant; two rows with the same
    /// connection string are two objects over one database, and a correction through either writes the
    /// progression table both of them read. Compared by <c>DatabaseId.Identity</c>, the studio's own key.
    /// </description></item>
    /// </list>
    /// <para>
    /// Anything that cannot be answered is answered <see langword="false" />, which asks the stricter
    /// question: not knowing how far an operation reaches is not a reason to authorize less of it.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsExclusivelyTenantsAsync(
        IDocumentStore store,
        IMartenDatabase database,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        try
        {
            string corrected = store.Options.TenantIdStyle.MaybeCorrectTenantId(tenantId);

            if (!HoldsOnly(database, corrected) || IsDefaultDatabase(store, database))
            {
                return false;
            }

            IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (IMartenDatabase other in databases)
            {
                if (!ReferenceEquals(other, database)
                    && string.Equals(other.Id.Identity, database.Id.Identity, StringComparison.OrdinalIgnoreCase)
                    && !HoldsOnly(other, corrected))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Whether a database lists exactly one tenant, and it is this one.</summary>
    /// <remarks>
    /// Copied before it is read: a sharded tenancy reconciles its databases' tenant lists whenever it
    /// rebuilds them, from whichever thread asked.
    /// </remarks>
    private static bool HoldsOnly(IMartenDatabase database, string tenantId)
    {
        string[] tenants = [.. database.TenantIds];
        return tenants is [string only] && string.Equals(only, tenantId, StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="database" /> is where the tenancy's default tenant lives.</summary>
    private static bool IsDefaultDatabase(IDocumentStore store, IMartenDatabase database)
    {
        IMartenDatabase? defaultDatabase;

        try
        {
            defaultDatabase = store.Options.Tenancy.Default?.Database;
        }
        catch (NotSupportedException)
        {
            // MasterTableTenancy and ShardedTenancy have no default tenant, by design.
            return false;
        }

        return defaultDatabase is not null
            && (ReferenceEquals(defaultDatabase, database)
                || string.Equals(defaultDatabase.Id.Identity, database.Id.Identity, StringComparison.OrdinalIgnoreCase));
    }
}
