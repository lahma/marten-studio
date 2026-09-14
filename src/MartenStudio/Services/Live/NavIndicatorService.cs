using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.Logging;

namespace MartenStudio.Services.Live;

/// <summary>
/// What the left navigation decorates its entries with.
/// </summary>
/// <remarks>
/// <para>
/// Three values and nothing else, because a badge is a decoration and a decoration must not be able to
/// break navigation. Every field is nullable or false-by-default, and the service answers
/// <see cref="None" /> rather than throwing whenever anything it asked could not be read: a database that
/// is down takes the counts away, never the links.
/// </para>
/// <para>
/// There is deliberately no schema-drift indicator. Finding drift costs a <c>CreateMigrationAsync</c>,
/// which is a schema-building call that hard rule 14 keeps off every read and navigation path, so drift
/// stays behind the button on the schema screen that says what it may create.
/// </para>
/// </remarks>
/// <param name="DeadLetters">
/// How many dead letters this database holds, or <see langword="null" /> when nobody could tell. Zero and
/// "could not tell" are different answers and neither draws a badge.
/// </param>
/// <param name="ProjectionsNeedAttention">
/// Whether a shard is lagging past the amber threshold, paused, or failed.
/// </param>
/// <param name="ProjectionsExplanation">What to put in the dot's tooltip, when there is a dot.</param>
internal sealed record NavIndicators(
    long? DeadLetters,
    bool ProjectionsNeedAttention,
    string? ProjectionsExplanation)
{
    /// <summary>Nothing known, and therefore nothing drawn.</summary>
    public static NavIndicators None { get; } = new(null, false, null);

    /// <summary>Whether the dead-letter entry carries a count.</summary>
    public bool ShowDeadLetterCount => DeadLetters is > 0;
}

/// <summary>
/// What <c>NavMenu</c> reads for its badges.
/// </summary>
/// <remarks>
/// An interface so the layout can be rendered in a component test without an events service, a
/// projections service or a Postgres behind either of them - none of which a test about navigation is
/// about.
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
/// Reads the navigation's badges through the same services the pages behind them use.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, because the scope it reads for is the circuit's. It asks
/// <see cref="IEventDataService.CountDeadLettersAsync" /> and
/// <see cref="IProjectionDataService.GetSummaryAsync" /> - the services, never the database - so the
/// store, database and tenant policies are applied to a badge exactly as they are to the page it points
/// at, and a visitor who may not see a database does not learn its dead-letter count from the sidebar.
/// </para>
/// <para>
/// <b>The projection half is gated on the event tables existing</b> (hard rule 14).
/// <see cref="IProjectionDataService.GetSummaryAsync" /> reaches
/// <c>IMartenDatabase.FetchHighestEventSequenceNumber</c> and <c>AllProjectionProgress</c>, and both of
/// those open with <c>EnsureStorageExistsAsync(typeof(IEvent))</c> - a Weasel migration of the event
/// store under the database's own <c>AutoCreate</c>. The sidebar is on <em>every</em> route, so an
/// ungated badge would mean that a host whose database has no event tables got them created by opening
/// <c>/marten/documents</c>. <see cref="IEventDataService.DescribeAsync" /> answers the same question
/// from <c>information_schema</c> through the studio's own cached column catalog, and the projection
/// read only happens when it says there is already something to read.
/// </para>
/// <para>
/// <b>Authorization comes before the cache, not inside it.</b>
/// <see cref="StudioSnapshotCache" /> is a process-wide singleton keyed on the scope alone - its own
/// contract says so, and says why: callers are expected to have passed
/// <see cref="MartenStudioOptions.StoreAuthorizationPolicy" /> for that scope before they reach it. A
/// cache hit returns without running the factory, so asking inside would mean the second circuit to want
/// a badge for a database got one without the policy being consulted (D5). The refusal is
/// <see cref="NavIndicators.None" />, which is the same thing the sidebar draws for a database nobody
/// could read: a visitor who may not see a database learns nothing from the difference.
/// </para>
/// <para>
/// The whole answer goes through <see cref="StudioSnapshotCache" /> under one key (plan D10): the
/// navigation is on every page, so without it a studio open in five tabs would run two extra queries per
/// tab per interval for two small numbers. The cache's time-to-live is about a second, which is well
/// under the polling interval, so a badge is never more than one tick stale.
/// </para>
/// <para>
/// Nothing here throws. A badge that could break the sidebar would be a decoration that takes navigation
/// down with it, and the studio's answer to an unreadable region is always a value (plan §4.8).
/// </para>
/// </remarks>
internal sealed class NavIndicatorService : INavIndicatorService
{
    private readonly StudioState state;
    private readonly StudioAuthorization authorization;
    private readonly IEventDataService events;
    private readonly IProjectionDataService projections;
    private readonly StudioSnapshotCache cache;
    private readonly ILogger<NavIndicatorService> logger;

    public NavIndicatorService(
        StudioState state,
        StudioAuthorization authorization,
        IEventDataService events,
        IProjectionDataService projections,
        StudioSnapshotCache cache,
        ILogger<NavIndicatorService> logger)
    {
        this.state = state;
        this.authorization = authorization;
        this.events = events;
        this.projections = projections;
        this.cache = cache;
        this.logger = logger;
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
            // Before the cache, never inside it: a hit skips the factory, and the factory is where the
            // data services would have resolved the scope and applied the store policy.
            if (!await authorization.IsAuthorizedAsync(scope, capability: null, cancellationToken).ConfigureAwait(false))
            {
                return NavIndicators.None;
            }

            return await cache
                .GetAsync(CacheKey(scope), token => ReadUncachedAsync(scope, token), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return NavIndicators.None;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Marten Studio could not read the navigation indicators");
            return NavIndicators.None;
        }
    }

    private async Task<NavIndicators> ReadUncachedAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        // Both already answer "could not read" as a value rather than by throwing, so there is no
        // per-call try here: what this method must not do is let one of them decide the other's fate.
        // CountDeadLettersAsync is already safe on a database with no event store - it asks the column
        // catalog whether mt_doc_deadletterevent exists and answers a real zero when it does not.
        long? deadLetters = await events.CountDeadLettersAsync(scope, cancellationToken).ConfigureAwait(false);

        // Hard rule 14. DescribeAsync is the cheapest honest answer to "does this database have an event
        // store": one information_schema read through the shared, expiring ColumnCatalog, and no Marten
        // call of any kind. A shape that could not be read leaves EventTablesExist false, so an
        // unreachable database is treated exactly as one with no event tables - the conservative way
        // round, because the only thing on the other side of this branch is a migration.
        EventStoreShape shape = await events.DescribeAsync(scope, cancellationToken).ConfigureAwait(false);

        if (!shape.EventTablesExist)
        {
            return new NavIndicators(deadLetters, false, null);
        }

        ProjectionSummary summary = await projections.GetSummaryAsync(scope, cancellationToken).ConfigureAwait(false);

        return new NavIndicators(
            deadLetters,
            summary.Error is null && summary.NeedsAttention,
            Explain(summary));
    }

    private static string? Explain(ProjectionSummary summary)
    {
        if (summary.Error is not null || !summary.NeedsAttention)
        {
            return null;
        }

        List<string> reasons = [];

        if (summary.MaxLag > ProjectionLagThresholds.Amber)
        {
            reasons.Add(
                summary.WorstShardName is { Length: > 0 } worst
                    ? $"{worst} is {summary.MaxLag:N0} events behind"
                    : $"a shard is {summary.MaxLag:N0} events behind");
        }

        if (summary.PausedCount > 0)
        {
            reasons.Add(summary.PausedCount == 1 ? "one shard is paused" : $"{summary.PausedCount} shards are paused");
        }

        if (summary.FailedCount > 0)
        {
            reasons.Add(summary.FailedCount == 1 ? "one shard has failed" : $"{summary.FailedCount} shards have failed");
        }

        return string.Join("; ", reasons);
    }

    /// <summary>
    /// One key per scope, and the same shape the other services use so the cache's prefix invalidation
    /// reaches it when anything in that scope is changed.
    /// </summary>
    private static string CacheKey(StudioScope scope) =>
        scope.StoreKey + "|" + scope.DatabaseId + "|" + (scope.TenantId ?? string.Empty) + "|nav-indicators";
}
