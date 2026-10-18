using System.Data;

using JasperFx.Descriptors;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services;

/// <summary>Where a tenant listing came from, which is what the selector says under it.</summary>
internal enum TenantListSource
{
    /// <summary>The host named them in <see cref="MartenStudioOptions.KnownTenantIds" />.</summary>
    Configured,

    /// <summary>Marten's own database descriptors carried them.</summary>
    Descriptor,

    /// <summary>They were read out of the tenant columns of the store's own tables, bounded.</summary>
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
    /// <summary>Nothing could answer - drawn differently from an empty list.</summary>
    public static TenantList Unavailable { get; } = new([], false, TenantListSource.Unavailable);
}

/// <summary>
/// Finds the tenant ids of one store and database, in three tiers, cheapest first.
/// </summary>
/// <remarks>
/// <para>
/// Tier 1 is <see cref="MartenStudioOptions.KnownTenantIds" />: a host that knows its tenants says so and
/// nothing is discovered. Tier 2 is what Marten itself lists for the database
/// (<see cref="ListedTenantsAsync" />). On a master-table or sharded tenancy that is the <c>TenantIds</c> of
/// every instance with the database's identity, which the caller's <c>AllDatabases()</c> has just
/// reconciled: describing either would be <c>BuildDatabases()</c> again, a read of the master or pool table
/// (AGENTS.md D27). On every other
/// tenancy it is <c>Tenancy.DescribeDatabasesAsync</c>, which answers from configuration for a static or
/// single-server multi-tenancy. Tier 3 is a bounded <c>select distinct tenant_id</c> over every table in the
/// store that has a tenant column, run only when <see cref="MartenStudioOptions.DiscoverTenantIds" />
/// allows it.
/// </para>
/// <para>
/// "Every table that has one" and not just <c>mt_streams</c>: conjoined tenancy is a per-document-type
/// setting, and a store whose documents are conjoined while its event store is not — which is the common
/// shape, and the sample's — has its tenant ids only in the document tables. Asking only the event store
/// meant such a store discovered nothing, the selector offered nothing, and
/// <see cref="StudioScopeResolver" /> then refused every tenant the visitor could have typed.
/// </para>
/// <para>
/// The tier-3 query is capped at 201 rows so that "more than 200" is answerable without counting, and the
/// whole answer is cached for a minute per store and database - a selector that re-ran a
/// <c>select distinct</c> on every render would be a small denial of service against the database the
/// studio exists to help you understand.
/// </para>
/// </remarks>
internal sealed class TenantDiscovery
{
    /// <summary>How many tenants the selector will list before it becomes a free-text box.</summary>
    public const int MaxListedTenants = 200;

    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(60);

    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<TenantDiscovery> logger;
    private readonly TimeProvider timeProvider;
    private readonly Lock gate = new();
    private readonly Dictionary<(string StoreKey, string DatabaseId), (DateTimeOffset At, TenantList List)> cache = [];

    public TenantDiscovery(IOptions<MartenStudioOptions> options, ILogger<TenantDiscovery> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    public TenantDiscovery(IOptions<MartenStudioOptions> options, ILogger<TenantDiscovery> logger, TimeProvider timeProvider)
    {
        this.options = options;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Whether anything in this store is tenanted per row, which is what makes a tenant selector mean
    /// something: any document type the visitor can see, or the event store itself.
    /// </summary>
    public bool IsTenantScopeRelevant(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Options.Events.TenancyStyle == TenancyStyle.Conjoined)
        {
            return true;
        }

        Func<Type, bool>? visible = options.Value.IsDocumentTypeVisible;
        foreach (IDocumentType documentType in store.Options.AllKnownDocumentTypes())
        {
            if (documentType.TenancyStyle == TenancyStyle.Conjoined
                && (visible is null || visible(documentType.DocumentType)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The tenants of <paramref name="database" /> in <paramref name="store" />, cached for a minute.
    /// </summary>
    /// <param name="storeKey">The store's registration key, which the cache is keyed by.</param>
    /// <param name="store">The store.</param>
    /// <param name="database">The database the scope resolved to: the first of <paramref name="enumerated" /> with its identity.</param>
    /// <param name="enumerated">
    /// What <c>Storage.AllDatabases()</c> has just answered, and <paramref name="database" /> came from - the
    /// resolver and the scope catalog both have it in hand, and nothing else calls this. On a master-table or
    /// sharded tenancy the answer is the <c>TenantIds</c> that call reconciled, of every instance in it with
    /// <paramref name="database" />'s identity (<see cref="ListedTenantsAsync" />). Required rather than
    /// defaulted, so that no caller can drop the siblings without saying so.
    /// </param>
    /// <param name="cancellationToken">Cancels the discovery.</param>
    public async ValueTask<TenantList> DiscoverAsync(
        string storeKey,
        IDocumentStore store,
        IMartenDatabase database,
        IReadOnlyList<IMartenDatabase> enumerated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(enumerated);

        MartenStudioOptions value = options.Value;

        // Tier 1. A host that listed its tenants has answered, and nothing is discovered - which is also
        // the only tier that works for a store whose tenants exist but have no streams yet.
        if (value.KnownTenantIds.Count > 0)
        {
            return new TenantList([.. value.KnownTenantIds], IsTruncated: false, TenantListSource.Configured);
        }

        if (!value.DiscoverTenantIds)
        {
            return TenantList.Unavailable;
        }

        (string StoreKey, string DatabaseId) cacheKey = (storeKey, database.Id.Identity);
        lock (gate)
        {
            if (cache.TryGetValue(cacheKey, out (DateTimeOffset At, TenantList List) cached)
                && timeProvider.GetUtcNow() - cached.At < CacheWindow)
            {
                return cached.List;
            }
        }

        TenantList discovered = await DiscoverUncachedAsync(store, database, enumerated, value, cancellationToken).ConfigureAwait(false);

        lock (gate)
        {
            cache[cacheKey] = (timeProvider.GetUtcNow(), discovered);
        }

        return discovered;
    }

    /// <summary>Forgets what was cached, for the selector's refresh button.</summary>
    public void Invalidate()
    {
        lock (gate)
        {
            cache.Clear();
        }
    }

    private async ValueTask<TenantList> DiscoverUncachedAsync(
        IDocumentStore store,
        IMartenDatabase database,
        IReadOnlyList<IMartenDatabase> enumerated,
        MartenStudioOptions value,
        CancellationToken cancellationToken)
    {
        // Tier 2: what Marten itself lists for this database. It answers for tenants that are configured or
        // assigned rather than inferred from data - a tenant with no rows yet included.
        try
        {
            if (await ListedTenantsAsync(store, database, enumerated, cancellationToken).ConfigureAwait(false) is { } listed)
            {
                return listed;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A tenancy strategy that cannot describe itself is not a fault the selector should render as
            // one; the next tier may still answer.
            logger.LogDebug(exception, "Marten Studio could not describe the databases of a store while discovering tenants");
        }

        // Tier 3: the tenant columns, which are the only place tenant ids exist as data rather than as
        // configuration.
        List<string> tables = TenantedTables(store, value);
        if (tables.Count == 0)
        {
            return TenantList.Unavailable;
        }

        try
        {
            return await QueryAsync(tables, database, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Marten Studio could not query tenant ids from the store's tenanted tables");
            return TenantList.Unavailable;
        }
    }

    /// <summary>
    /// How many tenanted tables tier 3 will read from before it gives up on being exhaustive.
    /// </summary>
    /// <remarks>
    /// A store with three hundred conjoined document types would otherwise produce a three-hundred-branch
    /// union, and the point of this tier is to be cheap. The tables are taken in the order Marten knows
    /// them, with the event store first, so the cap is deterministic rather than arbitrary.
    /// </remarks>
    private const int MaxTenantedTablesQueried = 16;

    /// <summary>
    /// Every table of <paramref name="store" /> that carries a <c>tenant_id</c>, quoted, event store
    /// first.
    /// </summary>
    private static List<string> TenantedTables(IDocumentStore store, MartenStudioOptions value)
    {
        List<string> tables = [];

        if (store.Options.Events.TenancyStyle == TenancyStyle.Conjoined)
        {
            tables.Add(SqlIdentifier.Qualify(store.Options.Events.DatabaseSchemaName, "mt_streams"));
        }

        // The visibility filter is applied here as well as in navigation (AGENTS.md: it is a data-layer
        // filter, not a UI one), so a hidden document type's tenants are not discovered on its behalf.
        Func<Type, bool>? visible = value.IsDocumentTypeVisible;
        foreach (IDocumentType documentType in store.Options.AllKnownDocumentTypes())
        {
            if (tables.Count >= MaxTenantedTablesQueried)
            {
                break;
            }

            if (documentType.TenancyStyle != TenancyStyle.Conjoined
                || (visible is not null && !visible(documentType.DocumentType)))
            {
                continue;
            }

            // Both halves are Marten's own mapping, and both go through the quoting builder rather than
            // into the string as they are (AGENTS.md hard rule 4).
            string table = SqlIdentifier.Qualify(documentType.TableName.Schema, documentType.TableName.Name);
            if (!tables.Contains(table, StringComparer.Ordinal))
            {
                tables.Add(table);
            }
        }

        return tables;
    }

    /// <summary>
    /// The tenants Marten itself lists for <paramref name="database" />, in ordinal order and without
    /// duplicates; <see langword="null" /> when it lists none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A master-table or sharded tenancy is read from the instance.</b> Both fill each database's
    /// <c>TenantIds</c> in <c>BuildDatabases()</c> - a master-table database lists the one tenant whose row it
    /// was built from, and a shard lists its active assignments, reconciled against the assignment table on
    /// every call (Marten #4868) - and their <c>DescribeDatabasesAsync</c> is <c>BuildDatabases()</c> again
    /// followed by a copy of those same lists (read at <c>V9.39.0</c> and <c>V9.50.2</c>, in
    /// <c>MasterTableTenancy</c> and <c>ShardedTenancy</c>). Describing would be a second read of the lookup
    /// table for an answer already in hand, because the instances handed in are what <c>AllDatabases()</c>
    /// has just returned (<see cref="DiscoverAsync" />).
    /// </para>
    /// <para>
    /// <b>Every instance with the database's identity, not only the one it resolved to.</b> Two master-table
    /// rows with one connection string are two <c>MartenDatabase</c> objects over one database, each listing
    /// its own tenant, and the studio - which scopes by <c>Id.Identity</c> - resolves that database to the
    /// first of them. Reading that one alone dropped the second row's tenant from the selector, and a link
    /// naming it was refused as "not a tenant of this database". The union of every same-identity instance's
    /// list is what describing would have matched by server and database name, and it costs no enumeration:
    /// the list is the caller's. The same twins are why <c>DatabaseReach.IsExclusivelyTenantsAsync</c> refuses
    /// a database another instance shares.
    /// </para>
    /// <para>
    /// <b>Every other tenancy is described.</b> A static multi-tenancy describes from its configuration, and a
    /// single-server one from its configured tenant-to-database map, which only the descriptors carry: its
    /// databases list no tenants. Neither reads a lookup table. A single-server describe call does run
    /// <c>BuildDatabases()</c>, but after the caller's own enumeration every database it names is built.
    /// Their lists are the host's configuration and are kept whole, so the resolver can refuse a tenant that is
    /// another database's.
    /// </para>
    /// <para>
    /// <b>A tenant-partitioned single database lists its tenants on the main descriptor</b>, and on no other
    /// (CL1 item 8). <c>TenantPartitionedSingleDatabaseTenancy.DescribeDatabasesAsync</c> is <c>DefaultTenancy</c>'s -
    /// one <c>MainDatabase</c>, no <c>Databases</c> - with that descriptor's <c>TenantIds</c> filled by
    /// <c>FetchManagedTenantIdsAsync</c>, one plain <c>select partition_value from mt_tenant_partitions</c> on its own
    /// connection, and empty when the table is not there (read at <c>V9.39.0</c> and <c>V9.50.2</c>). Reading only
    /// <c>Databases</c> threw that answer away and fell through to tier 3's scan of the tenanted tables, for a list
    /// the describe had already read - one that also misses a registered tenant with no rows yet. The main
    /// descriptor is read, then, when it describes this database and no per-database descriptor named a tenant:
    /// read-only, no call added, and no enumeration - this tenancy is <c>Single</c> and has nothing to build. That
    /// list is data and grows with the tenants, so it is capped like tier 3's: the first
    /// <see cref="MaxListedTenants" />, and truncated beyond them, which turns the selector into a free-text box.
    /// </para>
    /// </remarks>
    private static async ValueTask<TenantList?> ListedTenantsAsync(
        IDocumentStore store,
        IMartenDatabase database,
        IReadOnlyList<IMartenDatabase> enumerated,
        CancellationToken cancellationToken)
    {
        SortedSet<string> tenants = new(StringComparer.Ordinal);
        ITenancy tenancy = store.Options.Tenancy;

        if (ReadsTenantsFromTheInstance(tenancy))
        {
            // Copied before each is read: a sharded tenancy reconciles these lists from whichever thread asks.
            Add([.. database.TenantIds]);

            foreach (IMartenDatabase sibling in enumerated)
            {
                if (!ReferenceEquals(sibling, database)
                    && string.Equals(sibling.Id.Identity, database.Id.Identity, StringComparison.OrdinalIgnoreCase))
                {
                    Add([.. sibling.TenantIds]);
                }
            }

            return Whole();
        }

        DatabaseUsage usage = await tenancy.DescribeDatabasesAsync(cancellationToken).ConfigureAwait(false);
        foreach (DatabaseDescriptor descriptor in usage.Databases)
        {
            if (DescribesSameDatabase(descriptor, database))
            {
                Add(descriptor.TenantIds);
            }
        }

        if (tenants.Count > 0)
        {
            return Whole();
        }

        if (usage.MainDatabase is { } main && DescribesSameDatabase(main, database))
        {
            // Known limit: this is the list of tenants Marten has registered partitions for, and a tenant can hold rows
            // without one. A conjoined document type configured with DisablePartitioningIfAny keeps every tenant's rows
            // in one unpartitioned table, so a tenant that has written only there is in no mt_tenant_partitions row -
            // and, while this list is complete (200 or fewer), IsAcceptable refuses it as "not a tenant of this
            // database". Tier 3's scan of the tenanted tables would have found it; it is not run once this answers.
            Add(main.TenantIds);

            if (tenants.Count > 0)
            {
                return new TenantList(
                    [.. tenants.Take(MaxListedTenants)],
                    IsTruncated: tenants.Count > MaxListedTenants,
                    TenantListSource.Descriptor);
            }
        }

        return null;

        TenantList? Whole() => tenants.Count > 0 ? new TenantList([.. tenants], IsTruncated: false, TenantListSource.Descriptor) : null;

        void Add(IEnumerable<string> tenantIds)
        {
            foreach (string tenantId in tenantIds)
            {
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    tenants.Add(tenantId);
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="tenancy" />'s databases carry tenant lists of their own, reconciled by the
    /// enumeration that handed them out: Marten's <c>MasterTableTenancy</c> and <c>ShardedTenancy</c>.
    /// </summary>
    internal static bool ReadsTenantsFromTheInstance(ITenancy tenancy) =>
        tenancy is MasterTableTenancy or ShardedTenancy;

    /// <summary>
    /// Whether <paramref name="descriptor" /> is about <paramref name="database" />.
    /// </summary>
    /// <remarks>
    /// Not by name. <c>DatabaseDescriptor.Identifier</c> is the logical name the host gave the database
    /// (<c>tenants-a</c>), while <c>IMartenDatabase.Id.Identity</c> is Marten's composed server-and-database
    /// identity (<c>h1!invalid.none</c>) - the two are never equal for a statically multi-tenanted store,
    /// and comparing them was a silent no-match that made every descriptor tier fall through to a query.
    /// The server and database names are the pair both sides really carry.
    /// </remarks>
    private static bool DescribesSameDatabase(DatabaseDescriptor descriptor, IMartenDatabase database)
    {
        return (string.Equals(descriptor.ServerName, database.Id.Server, StringComparison.OrdinalIgnoreCase)
                && string.Equals(descriptor.DatabaseName, database.Id.Name, StringComparison.OrdinalIgnoreCase))
            || string.Equals(descriptor.Identifier, database.Id.Identity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One bounded read over every tenanted table, de-duplicated by the database.
    /// </summary>
    /// <remarks>
    /// Each branch is capped at <see cref="MaxListedTenants" /> + 1 rows of its own, so the union can
    /// never carry more than the number of tables times that however large the tables are, and the whole
    /// statement is capped again on the way out - "more than two hundred" is then answerable without
    /// counting. <c>CommandTimeout</c> is the studio's <see cref="MartenStudioOptions.QueryTimeout" />,
    /// because a <c>select distinct</c> over a table with no index on <c>tenant_id</c> is a scan and this
    /// runs behind a page that someone is looking at.
    /// </remarks>
    private static async ValueTask<TenantList> QueryAsync(
        IReadOnlyList<string> tables,
        IMartenDatabase database,
        MartenStudioOptions value,
        CancellationToken cancellationToken)
    {
        string branches = string.Join(
            " union ",
            tables.Select(table => $"(select distinct tenant_id from {table} order by tenant_id limit {MaxListedTenants + 1})"));

        await using NpgsqlConnection connection = database.CreateConnection(ConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"select tenant_id from ({branches}) as tenants where tenant_id is not null order by tenant_id limit {MaxListedTenants + 1}";
        command.CommandTimeout = (int) Math.Ceiling(value.QueryTimeout.TotalSeconds);

        List<string> ids = [];
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(reader.GetString(0));
                }
            }
        }

        bool truncated = ids.Count > MaxListedTenants;
        if (truncated)
        {
            ids.RemoveRange(MaxListedTenants, ids.Count - MaxListedTenants);
        }

        return new TenantList(ids, truncated, TenantListSource.Queried);
    }

    /// <summary>
    /// Whether <paramref name="tenantId" /> is one the studio will accept for
    /// <paramref name="tenants" />: one of the listed ones, or - when the listing is truncated - any
    /// plausible id, because the whole point of the free-text box is that the list is incomplete.
    /// </summary>
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

        return tenants.IsTruncated && tenantId.Trim().Length == tenantId.Length && tenantId.Length <= 200;
    }
}
