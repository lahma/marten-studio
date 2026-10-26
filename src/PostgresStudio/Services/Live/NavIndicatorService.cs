using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services.Live;

/// <summary>
/// What the left navigation decorates its entries with.
/// </summary>
/// <remarks>
/// <para>
/// Badges and nothing else, because a badge is a decoration and a decoration must not be able to break navigation.
/// The service answers <see cref="None" /> rather than throwing whenever anything it asked could not be read: a
/// database that is down takes the badges away, never the links.
/// </para>
/// <para>
/// There is deliberately no schema-drift indicator. Finding drift costs a <c>CreateMigrationAsync</c>, which is a
/// schema-building call that hard rule 14 keeps off every read and navigation path, so drift stays behind the button
/// on the schema screen that says what it may create.
/// </para>
/// </remarks>
/// <param name="Badges">
/// The badges beyond the health glance, keyed as the descriptor's sidebar entries name them
/// (<see cref="StudioNavItem.BadgeKey" />) - what each registered <see cref="INavIndicatorContributor" /> read: in
/// MartenStudio, the dead-letter count and the projections dot. A key with no badge is absent; "zero" and "could not
/// tell" are both absent, because neither draws anything.
/// </param>
internal sealed record NavIndicators(IReadOnlyDictionary<string, StudioNavBadge> Badges)
{
    /// <summary>Nothing known, and therefore nothing drawn.</summary>
    public static NavIndicators None { get; } = new(new Dictionary<string, StudioNavBadge>(StringComparer.Ordinal));

    /// <summary>
    /// What the last health sweep of this database said, for this visitor and unmuted, or
    /// <see langword="null" /> when no recent sweep exists. Read from the sweep cache, never by sweeping.
    /// </summary>
    public HealthGlance? Health { get; init; }

    /// <summary>Whether the Health entry carries a count.</summary>
    public bool ShowHealthBadge => Health?.Worst is not null;
}

/// <summary>
/// What <c>NavMenu</c> reads for its badges.
/// </summary>
/// <remarks>
/// An interface so the layout can be rendered in a component test without a contributor or a Postgres behind it -
/// none of which a test about navigation is about.
/// </remarks>
internal interface INavIndicatorService
{
    /// <summary>
    /// The indicators for the circuit's currently active scope, or <see cref="NavIndicators.None" /> when
    /// there is no scope yet or nothing could be read.
    /// </summary>
    Task<NavIndicators> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the navigation's badges: the health glance itself, and every other badge from the studio's
/// <see cref="INavIndicatorContributor" />s.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, because the scope it reads for is the circuit's. A contributor reads through the same services the
/// pages behind its badges use - never the database - so the source, database and tenant policies are applied to a
/// badge exactly as they are to the page it points at, and a visitor who may not see a database does not learn its
/// numbers from the sidebar.
/// </para>
/// <para>
/// <b>Authorization comes before the cache, not inside it.</b> <see cref="StudioSnapshotCache" /> is a process-wide
/// singleton keyed on the scope alone - its own contract says so, and says why: callers are expected to have passed
/// the scope policy for that scope before they reach it. A cache hit returns without running the factory, so asking
/// inside would mean the second circuit to want a badge for a database got one without the policy being consulted
/// (D5). The refusal is <see cref="NavIndicators.None" />, which is the same thing the sidebar draws for a database
/// nobody could read: a visitor who may not see a database learns nothing from the difference.
/// </para>
/// <para>
/// The contributors' answer goes through <see cref="StudioSnapshotCache" /> under one key (plan D10): the navigation
/// is on every page, so without it a studio open in five tabs would run each contributor's reads once per tab per
/// interval. The cache's time-to-live is about a second, well under the polling interval, so a badge is never more
/// than one tick stale. A contributor that throws takes every badge of that read with it - logged at Debug, and
/// <see cref="NavIndicators.None" /> drawn - and nothing is cached for it.
/// </para>
/// <para>
/// <b>The health badge starts no sweep.</b> It is <see cref="IHealthService.GlanceAsync" />: the last sweep a Health
/// page or the overview ran for this database, read from the sweep cache and shown through this visitor's gates, and
/// never a sweep of its own. For a scope with no tenant that is policy questions and no connection. For a scope with
/// a tenant it is also the resolver's own check that the tenant is one of the swept database's - the check every page
/// of that scope runs. With no recent sweep the entry carries no badge. It is read outside the snapshot cache,
/// because what a visitor is shown of a sweep is that visitor's.
/// </para>
/// </remarks>
internal sealed class NavIndicatorService : INavIndicatorService
{
    private readonly StudioState state;
    private readonly StudioAuthorization authorization;
    private readonly IReadOnlyList<INavIndicatorContributor> contributors;
    private readonly StudioSnapshotCache cache;
    private readonly ILogger<NavIndicatorService> logger;
    private readonly IHealthService? health;
    private readonly StudioProduct product;

    /// <remarks>
    /// <paramref name="health" /> and <paramref name="settings" /> are optional so that a test of the contributed
    /// badges can build this by hand; the container always supplies both.
    /// </remarks>
    public NavIndicatorService(
        StudioState state,
        StudioAuthorization authorization,
        IEnumerable<INavIndicatorContributor> contributors,
        StudioSnapshotCache cache,
        ILogger<NavIndicatorService> logger,
        IHealthService? health = null,
        IStudioSettings? settings = null)
    {
        product = StudioText.Of(settings).Product;
        ArgumentNullException.ThrowIfNull(contributors);

        this.state = state;
        this.authorization = authorization;
        this.contributors = [.. contributors];
        this.cache = cache;
        this.logger = logger;
        this.health = health;
    }

    /// <inheritdoc />
    public async Task<NavIndicators> ReadAsync(CancellationToken cancellationToken = default)
    {
        StudioScope? scope = state.ActiveScope;
        if (scope is null)
        {
            return NavIndicators.None;
        }

        try
        {
            // Before the cache, never inside it: a hit skips the factory, and the factory is where the contributors'
            // services would have resolved the scope and applied the policies.
            if (!await authorization.IsAuthorizedAsync(scope, capability: null, cancellationToken).ConfigureAwait(false))
            {
                return NavIndicators.None;
            }

            NavIndicators indicators = await cache
                .GetAsync(CacheKey(scope), token => ReadUncachedAsync(scope, token), cancellationToken)
                .ConfigureAwait(false);

            return health is null
                ? indicators
                : indicators with { Health = await GlanceAsync(health, scope, cancellationToken).ConfigureAwait(false) };
        }
        catch (OperationCanceledException)
        {
            return NavIndicators.None;
        }
        catch (Exception exception)
        {
            if (product == StudioProduct.PostgresStudio)
            {
                logger.LogDebug(exception, "Postgres Studio could not read the navigation indicators");
            }
            else
            {
                logger.LogDebug(exception, "Marten Studio could not read the navigation indicators");
            }

            return NavIndicators.None;
        }
    }

    /// <summary>The health glance, or nothing when it could not be read - a decoration, never a failure.</summary>
    private async Task<HealthGlance?> GlanceAsync(IHealthService service, StudioScope scope, CancellationToken cancellationToken)
    {
        try
        {
            return await service.GlanceAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (product == StudioProduct.PostgresStudio)
            {
                logger.LogDebug(exception, "Postgres Studio could not read the health badge");
            }
            else
            {
                logger.LogDebug(exception, "Marten Studio could not read the health badge");
            }

            return null;
        }
    }

    /// <summary>
    /// Every contributor's badges, in registration order; a key two contributors answer is the first one's.
    /// </summary>
    private async Task<NavIndicators> ReadUncachedAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        if (contributors.Count == 0)
        {
            return NavIndicators.None;
        }

        Dictionary<string, StudioNavBadge> badges = new(StringComparer.Ordinal);

        foreach (INavIndicatorContributor contributor in contributors)
        {
            IReadOnlyDictionary<string, StudioNavBadge> read = await contributor.ReadAsync(scope, cancellationToken).ConfigureAwait(false);

            foreach (KeyValuePair<string, StudioNavBadge> badge in read)
            {
                badges.TryAdd(badge.Key, badge.Value);
            }
        }

        return new NavIndicators(badges);
    }

    /// <summary>
    /// One key per scope, and the same shape the other services use so the cache's prefix invalidation
    /// reaches it when anything in that scope is changed.
    /// </summary>
    private static string CacheKey(StudioScope scope) =>
        scope.StoreKey + "|" + scope.DatabaseId + "|" + (scope.TenantId ?? string.Empty) + "|nav-indicators";
}
