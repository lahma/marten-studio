using MartenStudio.Services.Events;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Services.Find;

/// <summary>What the Marten contributors know about the scope they search: where it really is, what the event store looks like, and which tenants may be read across.</summary>
/// <param name="Scope">The scope as the resolver matched it - the registered key and the database's own identity - which a link carries and a policy is asked about a tenant.</param>
/// <param name="Shape">The event store, from <see cref="IEventDataService.DescribeAsync" />; its <see cref="EventStoreShape.EventTablesExist" /> says whether there is one.</param>
/// <param name="Tenants">Which tenants a conjoined collection or stream is read across when none is selected.</param>
internal sealed record FindContext(StudioScope Scope, EventStoreShape Shape, FindTenants Tenants)
{
    /// <summary>
    /// What a store that numbers and partitions its events per tenant answers when no tenant is selected: its streams are partitioned
    /// by tenant, and a sequence is only meaningful inside one. Natural-key tables are not partitioned themselves, but they point at
    /// those streams, so they are skipped with them (1.2, OPEN-4).
    /// </summary>
    public const string PerTenantNote =
        "This store numbers and partitions events per tenant: select a tenant to search streams, natural keys and events.";

    /// <summary>Whether streams, natural keys and sequences are skipped: events are per tenant and none is selected.</summary>
    public bool NeedsTenant => Shape.PerTenantSequences && Scope.TenantId is null;

    /// <summary>Whether streams and natural keys are not read across tenants, because the visitor's policy limits them and the store has more tenants than can be listed.</summary>
    public bool StreamsNeedTenant => Tenants.SelectionNeeded && Shape.EventsConjoined && Scope.TenantId is null;

    /// <summary>The tenants streams are read across: only when the event store is conjoined, because otherwise <c>*DEFAULT*</c> is every row's tenant.</summary>
    public IReadOnlyList<string>? StreamTenants => Shape.EventsConjoined && Scope.TenantId is null ? Tenants.Only : null;

    /// <summary>
    /// The tenant a stream or event row is in, for the policy, the link and the chip: its own under a conjoined event store, and none
    /// under any other - where Marten still writes <c>*DEFAULT*</c> into every row, which is no tenant the visitor can select and must
    /// not be asked of a tenant-allowlist policy.
    /// </summary>
    /// <param name="rowTenant">The row's <c>tenant_id</c>.</param>
    public string? TenantOf(string? rowTenant) => Shape.EventsConjoined && !string.IsNullOrEmpty(rowTenant) ? rowTenant : null;
}

/// <summary>The things the Marten contributors share, as a seam so a test can say them outright.</summary>
internal interface IFindScopes
{
    /// <summary>The context for this Find, shared by every contributor that asks within it; <see langword="null" /> reads as "could not be read".</summary>
    /// <param name="scope">The scope being searched.</param>
    /// <param name="query">The Find's parsed value; contributors of one Find pass the same instance.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<FindContext?> ContextAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken);

    /// <summary>The rows whose tenant the store policy admits for this database: the rest are dropped and never counted.</summary>
    /// <typeparam name="T">The rows.</typeparam>
    /// <param name="scope">The matched scope.</param>
    /// <param name="rows">The candidates, in order.</param>
    /// <param name="tenantOf">The tenant a row is in, or <see langword="null" /> for a row no tenant owns.</param>
    /// <param name="cancellationToken">Checked between rows.</param>
    ValueTask<List<T>> AdmitAsync<T>(StudioScope scope, IReadOnlyList<T> rows, Func<T, string?> tenantOf, CancellationToken cancellationToken);
}

/// <summary>
/// The things the Marten contributors share: the scope, the event store's shape and the tenants that may be read across - read once
/// per Find, not once per contributor - and the store policy's say over the tenants a hit names.
/// </summary>
/// <remarks>
/// <para>
/// The shape is <see cref="IEventDataService.DescribeAsync" />, which resolves the scope (so the store policy and the tenant check
/// apply), carries the matched scope back, and reads the catalog through the shared column cache: no Marten call, nothing
/// storage-ensuring. A scope the visitor may not have, a store that will not build and a database that cannot be read all read as no
/// context, which reads as no match.
/// </para>
/// <para>
/// With a store policy in force and no tenant selected, the tenants a conjoined probe may read across are the ones the header's selector
/// offers for the visitor (<see cref="FindTenants" />).
/// </para>
/// </remarks>
internal sealed class FindScopes : IFindScopes
{
    private readonly IEventDataService events;
    private readonly IStudioScopeCatalog catalog;
    private readonly StudioAuthorization authorization;
    private readonly Lock gate = new();
    private (FindQuery Query, StudioScope Scope, Task<FindContext?> Context)? last;

    public FindScopes(IEventDataService events, IStudioScopeCatalog catalog, StudioAuthorization authorization)
    {
        this.events = events;
        this.catalog = catalog;
        this.authorization = authorization;
    }

    /// <inheritdoc />
    public Task<FindContext?> ContextAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (last is { } held && ReferenceEquals(held.Query, query) && held.Scope == scope && !held.Context.IsCanceled && !held.Context.IsFaulted)
            {
                return held.Context;
            }

            Task<FindContext?> started = ReadAsync(scope, cancellationToken);
            last = (query, scope, started);
            return started;
        }
    }

    /// <inheritdoc />
    public async ValueTask<List<T>> AdmitAsync<T>(StudioScope scope, IReadOnlyList<T> rows, Func<T, string?> tenantOf, CancellationToken cancellationToken)
    {
        return await authorization
            .FilterAsync(rows, row => scope with { TenantId = tenantOf(row) is { Length: > 0 } tenant ? tenant : scope.TenantId }, take: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<FindContext?> ReadAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        try
        {
            EventStoreShape shape = await events.DescribeAsync(scope, cancellationToken).ConfigureAwait(false);
            if (!shape.Available || shape.Scope is not { } concrete)
            {
                return null;
            }

            return new FindContext(concrete, shape, await TenantsAsync(concrete, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A refused scope, a store that will not build, a database that is not there: no match, and nothing said about which.
            return null;
        }
    }

    private async Task<FindTenants> TenantsAsync(StudioScope concrete, CancellationToken cancellationToken)
    {
        if (!authorization.IsEnabled || concrete.TenantId is not null)
        {
            return FindTenants.Unrestricted;
        }

        StoreScopeFacts facts = await catalog.DescribeAsync(concrete.StoreKey, concrete.DatabaseId, cancellationToken).ConfigureAwait(false);

        // A store with no tenant selector has nothing tenanted to read across; one whose list may not be the whole truth cannot say what
        // "all of the visitor's tenants" is.
        if (!facts.ShowTenantSelector)
        {
            return FindTenants.Unrestricted;
        }

        return facts.Tenants.IsComplete ? FindTenants.OnlyThese(facts.Tenants.Ids) : FindTenants.Selection;
    }
}
