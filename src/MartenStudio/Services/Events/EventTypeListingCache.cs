using Microsoft.Extensions.Options;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Live;

namespace MartenStudio.Services.Events;

/// <summary>
/// The Event types page's counted listing, shared (1.2-SHAPE): one <c>group by type</c> per resolved scope per half minute, however many
/// circuits press "Load counts" and however many Check shapes clicks need the names and counts it holds.
/// </summary>
/// <remarks>
/// <para>
/// A singleton that <b>resolves nothing</b> - the pattern <see cref="DeadLetterGroupCache" /> follows. The caller resolves the scope under
/// its own visitor and every policy before the cache is reached, and the shared read is given what was resolved: no resolver, no DI scope,
/// no scoped service. A read that created a scope of its own and resolved again there would run with no user (a Blazor Server host's
/// <c>ServerAuthenticationStateProvider</c> throws outside a circuit's scope), and every store policy would refuse every visitor. The key is
/// the resolved scope - the store's registered key, the database's own identity and the tenant - and nothing about the visitor, so a cached
/// listing is only ever handed to someone who has just passed the same checks for the same scope. A read that fails is thrown to every
/// waiting caller, which turns it into its own sentence, and is not kept.
/// </para>
/// </remarks>
internal sealed class EventTypeListingCache
{
    /// <summary>How long a counted listing is shared: long enough for the click that follows "Load counts", short enough to be a fresh count.</summary>
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(30);

    private readonly ColumnCatalog catalog;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioSnapshotCache cache;
    private readonly TimeProvider clock;
    private readonly Func<ResolvedScope, CancellationToken, Task<EventTypeList>>? read;

    public EventTypeListingCache(ColumnCatalog catalog, IOptions<MartenStudioOptions> options)
        : this(catalog, options, TimeProvider.System)
    {
    }

    /// <param name="catalog">The column catalog.</param>
    /// <param name="options">The studio's options.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="read">The shared read, for a test that has no database; the group-by itself otherwise.</param>
    internal EventTypeListingCache(
        ColumnCatalog catalog,
        IOptions<MartenStudioOptions> options,
        TimeProvider timeProvider,
        Func<ResolvedScope, CancellationToken, Task<EventTypeList>>? read = null)
    {
        this.catalog = catalog;
        this.options = options;
        this.read = read;
        clock = timeProvider;
        cache = new StudioSnapshotCache(timeProvider, TimeToLive);
    }

    /// <summary>The counted listing of <paramref name="resolved" />'s scope, read at most once per <see cref="TimeToLive" />.</summary>
    /// <param name="resolved">The scope as it resolved: every policy applied already, by the caller, under its visitor.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    public async Task<EventTypeList> GetAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string key = resolved.Registration.Key + "\u001f" + resolved.Database.Id.Identity + "\u001f" + (resolved.TenantId ?? string.Empty);
        int timeout = (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds);

        return await cache
            .GetAsync(key, token => ReadAsync(resolved, timeout, token), cancellationToken)
            .ConfigureAwait(false);
    }

    // Stamped here, once, by the read that fills the cache: every caller handed the shared listing is handed the time it was read.
    private async Task<EventTypeList> ReadAsync(ResolvedScope resolved, int timeout, CancellationToken token)
    {
        EventTypeList list = read is null
            ? await EventDataService.ReadCountedAsync(resolved, catalog, timeout, token).ConfigureAwait(false)
            : await read(resolved, token).ConfigureAwait(false);

        return list.Error is null ? list with { ListedAt = clock.GetUtcNow() } : list;
    }
}
