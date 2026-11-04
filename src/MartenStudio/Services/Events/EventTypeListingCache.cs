using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Live;

namespace MartenStudio.Services.Events;

/// <summary>
/// The Event types page's counted listing, shared (1.2-SHAPE): one <c>group by type</c> per resolved scope per half minute, however many
/// circuits press "Load counts" and however many Check shapes clicks need the names and counts it holds.
/// </summary>
/// <remarks>
/// <para>
/// A singleton whose read runs <b>in a scope of its own</b>: the shared work must not close over the first caller's scoped
/// <see cref="IEventDataService" />, which that circuit may dispose while others are still waiting on the read. The caller has already
/// resolved the scope under the store policy - the key is the resolved scope's, as every cache here - so a cached listing is only
/// ever handed to someone who has just passed it. A listing that carries an error is not kept.
/// </para>
/// </remarks>
internal sealed class EventTypeListingCache
{
    /// <summary>How long a counted listing is shared: long enough for the click that follows "Load counts", short enough to be a fresh count.</summary>
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory scopes;
    private readonly StudioSnapshotCache cache;

    public EventTypeListingCache(IServiceScopeFactory scopes)
        : this(scopes, TimeProvider.System)
    {
    }

    internal EventTypeListingCache(IServiceScopeFactory scopes, TimeProvider timeProvider)
    {
        this.scopes = scopes;
        cache = new StudioSnapshotCache(timeProvider, TimeToLive);
    }

    /// <summary>The counted listing of <paramref name="resolved" />'s scope, read at most once per <see cref="TimeToLive" />.</summary>
    /// <param name="resolved">The scope as it resolved: every policy applied already.</param>
    /// <param name="scope">The scope the read is made with.</param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    public async Task<EventTypeList> GetAsync(ResolvedScope resolved, StudioScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(scope);

        string key = resolved.Registration.Key + "\u001f" + resolved.Database.Id.Identity + "\u001f" + (resolved.TenantId ?? string.Empty);

        return await cache
            .GetAsync(
                key,
                async token =>
                {
                    await using AsyncServiceScope own = scopes.CreateAsyncScope();
                    var service = (EventDataService) own.ServiceProvider.GetRequiredService<IEventDataService>();

                    EventTypeList list = await service.ReadEventTypesAsync(scope, withCounts: true, token).ConfigureAwait(false);

                    if (list.Error is not null)
                    {
                        cache.Invalidate(key);
                    }

                    return list;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
