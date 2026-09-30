using JasperFx.Descriptors;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using Weasel.Core.MultiTenancy;

namespace MartenStudio.Services;

/// <summary>
/// How far an operation reaches, which is what it has to be authorized for - as opposed to what the URL
/// it was asked from names.
/// </summary>
/// <remarks>
/// <para>
/// A tenant in the scope selector narrows what the studio <em>reads</em>. It narrows almost nothing the
/// daemon or the SQL console <em>does</em>. Verified against Marten 9.31 and the JasperFx.Events 2.60 it
/// brings:
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
/// the per-tenant partitioning form) and is not itself a shard the store registers
/// (<see cref="TenantOfShard(IDocumentStore, string)" />); a store-global shard processes every tenant's
/// events, and a name with no colon at all starts every shard of the projection.
/// </description></item>
/// <item><description>
/// <c>RewindSubscriptionAsync(name, token, floor)</c> - the dead-letter page's "Rewind subscription" -
/// stops every agent of the subscription in the daemon's database, then
/// <c>IEventStore.RewindSubscriptionProgressAsync</c> opens a session with <c>AllowAnyTenant = true</c>,
/// rewrites <em>every</em> shard's progression row of that projection and runs
/// <c>DeleteWhere&lt;DeadLetterEvent&gt;(ProjectionName == name &amp;&amp; EventSequence &gt;= floor)</c>
/// with no tenant, and the agents restart from the floor (<c>DocumentStore.EventStore.cs</c> and
/// <c>JasperFxAsyncDaemon.RewindSubscriptionAsync</c>). A visitor allowed only <c>acme</c> who rewound a
/// conjoined subscription deleted <c>globex</c>'s dead letters and re-applied <c>globex</c>'s poison
/// event - live-proven by the DB-0-fix-2 review.
/// </description></item>
/// <item><description>
/// The SQL console runs whatever it is given against the database, and nothing puts a tenant into a
/// statement somebody typed - see <c>QueryService.SqlScope</c>.
/// </description></item>
/// </list>
/// <para>
/// So an operation that reaches a database is authorized as <c>(store, database, null)</c> - "this
/// database as a whole" - and not as the tenant the visitor happened to have selected. The exception is
/// a database that is <em>exclusively</em> that tenant's (<see cref="IsExclusivelyTenantsAsync" />):
/// database-per-tenant, where reaching the database and reaching the tenant are the same thing, and the
/// tenant-scoped question is the equivalent one. The questions themselves are asked by
/// <see cref="DatabaseReachAuthorization" />.
/// </para>
/// </remarks>
internal static class DatabaseReach
{
    /// <summary>
    /// The tenant a shard name belongs to, or <see langword="null" /> when the agent it names processes
    /// more than one tenant's events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ShardName.TryParse</c> rather than a suffix: <c>Foo:acme</c>'s trailing segment is a shard key
    /// and <c>Orders:All:acme</c>'s is a tenant, and only parsing tells them apart - the same rule
    /// <c>ProjectionDataService.BelongsToTenant</c> applies to progression rows. A name that does not
    /// parse is not attributable to a tenant, so it reaches the database.
    /// </para>
    /// <para>
    /// <b>But the parse is a guess, and the daemon does not guess.</b> <c>JasperFxAsyncDaemon.StartAgentAsync</c>
    /// looks the name up among the store's registered shards <em>first</em>, by exact identity, and only
    /// parses what it did not find. A projection whose own name holds a colon - <c>Billing:Invoices</c>,
    /// which nothing in Marten or JasperFx refuses - has the store-global identity
    /// <c>Billing:Invoices:All</c>, which <c>TryParse</c> reads as tenant <c>All</c> of projection
    /// <c>Billing</c>. Asked about as "tenant <c>All</c>", a visitor holding exactly that tenant could stop
    /// an agent that processes every tenant's events. So a name that is the identity of a shard the store
    /// registers is store-global, whatever the parse says, and a store whose shards cannot be listed is
    /// answered <see langword="null" /> - the stricter question.
    /// </para>
    /// </remarks>
    /// <param name="store">The store whose registered shards decide what the name is.</param>
    /// <param name="shardName">The name a control was asked for.</param>
    public static string? TenantOfShard(IDocumentStore store, string shardName)
    {
        ArgumentNullException.ThrowIfNull(store);

        return TenantOfShard(shardName, RegisteredShardIdentities(store));
    }

    /// <summary>
    /// <see cref="TenantOfShard(IDocumentStore, string)" /> against a set of registered shard identities,
    /// or against none that could be read.
    /// </summary>
    /// <param name="shardName">The name a control was asked for.</param>
    /// <param name="registeredShardIdentities">
    /// Every shard identity the store registers, compared ordinally as the daemon compares them; or
    /// <see langword="null" /> when they could not be read, which makes every name store-global.
    /// </param>
    internal static string? TenantOfShard(string shardName, IReadOnlySet<string>? registeredShardIdentities)
    {
        if (!ShardName.TryParse(shardName, out ShardName? shard) || shard?.TenantId is not { Length: > 0 } tenantId)
        {
            return null;
        }

        return registeredShardIdentities is null || registeredShardIdentities.Contains(shardName)
            ? null
            : tenantId;
    }

    /// <summary>
    /// Every shard identity the store registers - projections and subscriptions both - or
    /// <see langword="null" /> when they cannot be listed.
    /// </summary>
    /// <remarks>
    /// <c>ProjectionOptions.AllShards()</c> is the list <c>StartAgentAsync</c> searches (through
    /// <c>IEventStore.AllShards()</c>, which <c>DocumentStore</c> implements as exactly that). It is only
    /// reachable on the concrete <see cref="StoreOptions" />, which is what every <see cref="DocumentStore" />
    /// hands out; any other options object falls back to <c>IReadOnlyEventStoreOptions.Projections()</c>,
    /// which lists the projections without the raw subscriptions. Configuration only - no connection.
    /// </remarks>
    private static HashSet<string>? RegisteredShardIdentities(IDocumentStore store)
    {
        try
        {
            if (store.Options is StoreOptions concrete)
            {
                return concrete.Projections.AllShards().Select(static x => x.Name.Identity).ToHashSet(StringComparer.Ordinal);
            }

            HashSet<string> identities = new(StringComparer.Ordinal);
            foreach (ISubscriptionSource source in store.Options.Events.Projections())
            {
                foreach (ShardName name in source.ShardNames())
                {
                    identities.Add(name.Identity);
                }
            }

            return identities;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="database" /> holds <paramref name="tenantId" />'s data and nobody else's,
    /// so that an operation on the whole database reaches that tenant and no other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four conditions, each one a way a database can hold more than its one listed tenant:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// The tenancy does not pool tenants by design. <c>ShardedTenancy</c> (an <see cref="ITenantDatabasePool" />)
    /// puts many tenants into each shard, conjoined and list-partitioned, and rebuilds each shard's
    /// <c>TenantIds</c> from its <em>active</em> assignments only (<c>where disabled = false</c>, read at
    /// <c>V9.31.0</c>, <c>ShardedTenancy.BuildDatabases</c>) - while a disabled tenant's partitions stay in
    /// the shard, and a progression correction rewrites <c>Name:All:globex</c> as readily as
    /// <c>Name:All:acme</c>. A shard listing only <c>acme</c> is therefore not provably <c>acme</c>'s alone,
    /// and is never treated as such.
    /// </description></item>
    /// <item><description>
    /// It is not the tenancy's default database, where untenanted writes land (<c>AsDefault()</c> on a
    /// static multi-tenancy). A tenancy with no default - <c>MasterTableTenancy</c> and
    /// <c>ShardedTenancy</c> throw from <c>Default</c> - has none to be.
    /// </description></item>
    /// <item><description>
    /// Its <c>TenantIds</c> are exactly <c>[tenantId]</c>, compared after
    /// <c>TenantIdStyle.MaybeCorrectTenantId</c> - which is how every tenancy stores them - and read
    /// <em>after</em> <c>AllDatabases()</c> has run, because that call is what reconciles a dynamic
    /// tenancy's lists: read before it, the list is whatever the previous caller left. A conjoined single
    /// database lists none; a static database with several tenants lists them all.
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
    /// question: not knowing how far an operation reaches is not a reason to authorize less of it. Nothing
    /// here asks the tenancy for a tenant (<c>GetTenantAsync</c>), which on a dynamic tenancy provisions one
    /// - see <see cref="FindTenantDatabaseAsync" />.
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
            if (IsPooledByDesign(store.Options.Tenancy) || IsDefaultDatabase(store, database))
            {
                return false;
            }

            string corrected = store.Options.TenantIdStyle.MaybeCorrectTenantId(tenantId);

            IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // After the refresh, never before it: the list is what AllDatabases() just reconciled.
            if (!HoldsOnly(database, corrected))
            {
                return false;
            }

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

    /// <summary>
    /// Whether <paramref name="tenancy" /> puts several tenants into one database by design, so that no
    /// database of it is ever one tenant's alone.
    /// </summary>
    /// <remarks>
    /// <c>ShardedTenancy</c> is the one Marten 9.31 ships, and <see cref="ITenantDatabasePool" /> is the
    /// Weasel interface it implements to be a pool - named as well, so a pool that is not Marten's own
    /// class is treated the same way.
    /// </remarks>
    internal static bool IsPooledByDesign(ITenancy tenancy) =>
        tenancy is ShardedTenancy or ITenantDatabasePool;

    /// <summary>
    /// The database Marten's tenant overloads would reach for <paramref name="tenantId" />, found without
    /// provisioning anything - or <see langword="null" /> when no database the store lists holds that
    /// tenant yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On a dynamic tenancy, finding a tenant creates one.</b> <c>ShardedTenancy.GetTenantAsync</c> for a
    /// tenant it has not assigned runs <c>findOrAssignTenantDatabaseAsync</c> - an assignment row, and the
    /// tenant's partitions and sequences as DDL; <c>SingleServerMultiTenancy.GetTenantAsync</c> for a tenant
    /// it was not configured with creates a database named after it (read at <c>V9.31.0</c>). The studio
    /// accepts a tenant from <c>KnownTenantIds</c> or a truncated discovery list for any database, so a
    /// visitor could name one that is not provisioned yet - and the progression correction's own
    /// <c>TenantDatabaseAsync</c> used to call <c>GetTenantAsync</c> with it <em>before</em> the
    /// whole-database authorization, which is DDL on behalf of a request that was then refused.
    /// </para>
    /// <para>
    /// So on a tenancy whose cardinality is <c>DynamicMultiple</c> - or that cannot say - the tenant has to
    /// be <em>present</em> first: listed in the <c>TenantIds</c> of a database <c>AllDatabases()</c> answers
    /// (sharded and master-table tenancies fill those), or in a database descriptor
    /// <c>DescribeDatabasesAsync</c> answers (single-server tenancy leaves its databases' lists empty and
    /// fills only the descriptors). Both calls run <c>BuildDatabases()</c>, which the resolver has already
    /// run for this request; neither asks for a tenant by name. Only a present tenant is then asked for,
    /// through the same two calls <c>AdvancedOperations</c> makes -
    /// <c>TenantIdStyle.MaybeCorrectTenantId</c>, then <c>Tenancy.GetTenantAsync</c> - which for a present
    /// tenant is a lookup, so the database authorized is the database the correction then writes to. A
    /// static or single-database tenancy never provisions from <c>GetTenantAsync</c>, and is asked
    /// directly.
    /// </para>
    /// <para>
    /// Known limit: a tenant disabled between this check and Marten's own <c>GetTenantAsync</c> a moment
    /// later is the tenancy's to handle; the window is one call wide.
    /// </para>
    /// </remarks>
    public static async Task<IMartenDatabase?> FindTenantDatabaseAsync(
        IDocumentStore store,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        ITenancy tenancy = store.Options.Tenancy;
        string corrected = store.Options.TenantIdStyle.MaybeCorrectTenantId(tenantId);

        if (!MayProvisionOnLookup(tenancy)
            || await IsPresentAsync(store, tenancy, corrected, cancellationToken).ConfigureAwait(false))
        {
            Tenant tenant = await tenancy.GetTenantAsync(corrected).ConfigureAwait(false);
            return tenant.Database;
        }

        return null;
    }

    /// <summary>
    /// Whether asking <paramref name="tenancy" /> for a tenant it does not have may create one.
    /// </summary>
    /// <remarks>
    /// <c>Single</c> (<c>DefaultTenancy</c>, the tenant-partitioned single database) answers every tenant
    /// with its one database, and <c>StaticMultiple</c> throws <c>UnknownTenantIdException</c> for one it
    /// was not configured with; neither creates anything. Every <c>DynamicMultiple</c> tenancy may, and a
    /// tenancy that cannot say is treated as one that may.
    /// </remarks>
    private static bool MayProvisionOnLookup(ITenancy tenancy)
    {
        try
        {
            return tenancy.Cardinality is not (DatabaseCardinality.Single or DatabaseCardinality.StaticMultiple);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Whether a database the store already knows holds <paramref name="corrected" />.</summary>
    private static async Task<bool> IsPresentAsync(
        IDocumentStore store,
        ITenancy tenancy,
        string corrected,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (IMartenDatabase database in databases)
        {
            if (Lists(database.TenantIds, corrected))
            {
                return true;
            }
        }

        DatabaseUsage usage = await tenancy.DescribeDatabasesAsync(cancellationToken).ConfigureAwait(false);

        if (usage.MainDatabase is { } main && Lists(main.TenantIds, corrected))
        {
            return true;
        }

        foreach (DatabaseDescriptor descriptor in usage.Databases)
        {
            if (Lists(descriptor.TenantIds, corrected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a tenant list - copied first, since a tenancy may reconcile it on another thread - holds the id.</summary>
    private static bool Lists(IEnumerable<string> tenantIds, string tenantId)
    {
        string[] tenants = [.. tenantIds];
        return tenants.Contains(tenantId, StringComparer.Ordinal);
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
