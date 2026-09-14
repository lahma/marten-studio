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

    /// <summary>They were read out of the event store's stream table, bounded.</summary>
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
/// nothing is discovered. Tier 2 is Marten's own <c>Tenancy.DescribeDatabasesAsync</c>, which answers for
/// a static multi-tenancy configuration without touching the database. Tier 3 is a bounded
/// <c>select distinct tenant_id</c> against the event store's stream table, run only when the event store
/// is conjoined and only when <see cref="MartenStudioOptions.DiscoverTenantIds" /> allows it.
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
    public async ValueTask<TenantList> DiscoverAsync(
        string storeKey,
        IDocumentStore store,
        IMartenDatabase database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);

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

        TenantList discovered = await DiscoverUncachedAsync(store, database, value, cancellationToken).ConfigureAwait(false);

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
        MartenStudioOptions value,
        CancellationToken cancellationToken)
    {
        // Tier 2: Marten's own descriptors. Free where the tenancy is static, and it answers for a
        // database whose tenants are configured rather than inferred from data.
        try
        {
            DatabaseUsage usage = await store.Options.Tenancy.DescribeDatabasesAsync(cancellationToken).ConfigureAwait(false);
            List<string> fromDescriptors = [];
            foreach (DatabaseDescriptor descriptor in usage.Databases)
            {
                if (!DescribesSameDatabase(descriptor, database))
                {
                    continue;
                }

                foreach (string tenantId in descriptor.TenantIds)
                {
                    if (!string.IsNullOrWhiteSpace(tenantId))
                    {
                        fromDescriptors.Add(tenantId);
                    }
                }
            }

            if (fromDescriptors.Count > 0)
            {
                fromDescriptors.Sort(StringComparer.Ordinal);
                return new TenantList(fromDescriptors, IsTruncated: false, TenantListSource.Descriptor);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A tenancy strategy that cannot describe itself is not a fault the selector should render as
            // one; the next tier may still answer.
            logger.LogDebug(exception, "Marten Studio could not describe the databases of a store while discovering tenants");
        }

        // Tier 3, and only for a conjoined event store: the tenant column of mt_streams is the only place
        // tenant ids exist as data rather than as configuration.
        if (store.Options.Events.TenancyStyle != TenancyStyle.Conjoined)
        {
            return TenantList.Unavailable;
        }

        try
        {
            return await QueryAsync(store, database, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Marten Studio could not query tenant ids from the event store");
            return TenantList.Unavailable;
        }
    }

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

    private static async ValueTask<TenantList> QueryAsync(
        IDocumentStore store,
        IMartenDatabase database,
        MartenStudioOptions value,
        CancellationToken cancellationToken)
    {
        // The only identifiers here are Marten's own schema name and its fixed table name, and both go
        // through the quoting builder rather than into the string as they are (AGENTS.md hard rule 4).
        string streams = SqlIdentifier.Qualify(store.Options.Events.DatabaseSchemaName, "mt_streams");

        await using NpgsqlConnection connection = database.CreateConnection(ConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            $"select distinct tenant_id from {streams} order by tenant_id limit {MaxListedTenants + 1}";
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
