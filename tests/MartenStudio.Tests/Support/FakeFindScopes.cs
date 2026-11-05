using MartenStudio.Services.Events;
using MartenStudio.Services.Find;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Tests.Support;

/// <summary>What the Marten Find contributors share, said outright: the context, and which tenants the policy admits.</summary>
internal sealed class FakeFindScopes(FindContext? context, Func<string?, bool>? admits = null, FindTenants? tenants = null, bool tenantsUnreadable = false) : IFindScopes
{
    /// <summary>How many times a contributor asked for the tenant restriction.</summary>
    public int TenantReads { get; private set; }

    /// <summary>How many times a contributor asked for the context.</summary>
    public int Contexts { get; private set; }

    /// <summary>A context over a store with its event tables, as the resolver would have matched <paramref name="scope" />.</summary>
    public static FindContext Context(StudioScope scope, bool perTenant = false, bool eventsConjoined = true, FindTenants? tenants = null, params string[] naturalKeys) =>
        new(
            scope,
            new EventStoreShape
            {
                StreamIdentity = JasperFx.Events.StreamIdentity.AsGuid,
                Schema = "studio_events",
                EventTablesExist = true,
                PerTenantSequences = perTenant,
                EventsConjoined = eventsConjoined,
                NaturalKeys = naturalKeys,
                Scope = scope,
            },
            tenants ?? FindTenants.Unrestricted);

    public Task<FindTenants?> TenantsAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        TenantReads++;
        return Task.FromResult(tenantsUnreadable ? null : tenants ?? context?.Tenants ?? FindTenants.Unrestricted);
    }

    public Task<FindContext?> ContextAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        Contexts++;
        return Task.FromResult(context);
    }

    public ValueTask<List<T>> AdmitAsync<T>(StudioScope scope, IReadOnlyList<T> rows, Func<T, string?> tenantOf, CancellationToken cancellationToken) =>
        ValueTask.FromResult<List<T>>([.. rows.Where(x => admits is null || admits(tenantOf(x)))]);
}
