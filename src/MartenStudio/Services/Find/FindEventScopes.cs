using MartenStudio.Services.Events;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Services.Find;

/// <summary>What the event-side contributors know about the scope they search: where it really is, and what the event store looks like.</summary>
/// <param name="Scope">The scope as the resolver matched it - the registered key and the database's own identity - which a link carries and a policy is asked about a tenant.</param>
/// <param name="Shape">The event store, from <see cref="IEventDataService.DescribeAsync" />.</param>
internal sealed record FindEventContext(StudioScope Scope, EventStoreShape Shape)
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
}

/// <summary>The two things the event-side contributors share, as a seam so a test can say them outright.</summary>
internal interface IFindEventScopes
{
    /// <summary>The context for this Find, shared by every contributor that asks within it; <see langword="null" /> reads as no match.</summary>
    /// <param name="scope">The scope being searched.</param>
    /// <param name="query">The Find's parsed value; contributors of one Find pass the same instance.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<FindEventContext?> ContextAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken);

    /// <summary>The rows whose tenant the store policy admits for this database: the rest are dropped and never counted.</summary>
    /// <typeparam name="T">The rows.</typeparam>
    /// <param name="scope">The matched scope.</param>
    /// <param name="rows">The candidates, in order.</param>
    /// <param name="tenantOf">The tenant a row is in, or <see langword="null" /> for a row no tenant owns.</param>
    /// <param name="cancellationToken">Checked between rows.</param>
    ValueTask<List<T>> AdmitAsync<T>(StudioScope scope, IReadOnlyList<T> rows, Func<T, string?> tenantOf, CancellationToken cancellationToken);
}

/// <summary>
/// The two things the event-side contributors share: the scope and the event store's shape - read once per Find, not once per
/// contributor - and the store policy's say over the tenants a hit names.
/// </summary>
/// <remarks>
/// The shape is <see cref="IEventDataService.DescribeAsync" />, which resolves the scope (so the store policy and the tenant check
/// apply) and reads the catalog through the shared column cache: no Marten call, nothing storage-ensuring. A scope the visitor may
/// not have, a store that will not build and a database with no event tables all read as no context, which reads as no match.
/// </remarks>
internal sealed class FindEventScopes : IFindEventScopes
{
    private readonly StudioScopeResolver resolver;
    private readonly IEventDataService events;
    private readonly StudioAuthorization authorization;
    private readonly Lock gate = new();
    private (FindQuery Query, StudioScope Scope, Task<FindEventContext?> Context)? last;

    public FindEventScopes(StudioScopeResolver resolver, IEventDataService events, StudioAuthorization authorization)
    {
        this.resolver = resolver;
        this.events = events;
        this.authorization = authorization;
    }

    /// <inheritdoc />
    public Task<FindEventContext?> ContextAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (last is { } held && ReferenceEquals(held.Query, query) && held.Scope == scope && !held.Context.IsCanceled && !held.Context.IsFaulted)
            {
                return held.Context;
            }

            Task<FindEventContext?> started = ReadAsync(scope, cancellationToken);
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

    private async Task<FindEventContext?> ReadAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
            StudioScope concrete = new(resolved.Registration.Key, resolved.Database.Id.Identity, resolved.TenantId);

            EventStoreShape shape = await events.DescribeAsync(scope, cancellationToken).ConfigureAwait(false);

            return shape.Available && shape.EventTablesExist ? new FindEventContext(concrete, shape) : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A refused scope, a store that will not build, a database that is not there: no match, and nothing said about which.
            return null;
        }
    }
}
