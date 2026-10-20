using System.Globalization;

using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's sidebar badges beyond the health glance: the dead-letter count and the projections dot.
/// </summary>
/// <remarks>
/// <para>
/// Reads through <see cref="IEventDataService" /> and <see cref="IProjectionDataService" /> - the services the badges'
/// pages read - so the store, database and tenant policies apply to a badge exactly as to its page, and a visitor who
/// may not see a database does not learn its dead-letter count from the sidebar.
/// </para>
/// <para>
/// <b>The projection half is gated on the event tables existing</b> (hard rule 14).
/// <see cref="IProjectionDataService.GetSummaryAsync" /> used to reach <c>IMartenDatabase.FetchHighestEventSequenceNumber</c>
/// and <c>AllProjectionProgress</c>, both of which open with <c>EnsureStorageExistsAsync(typeof(IEvent))</c> - a Weasel
/// migration of the event store under the database's own <c>AutoCreate</c>. The sidebar is on <em>every</em> route, so
/// an ungated badge meant that a host whose database had no event tables got them created by opening
/// <c>/marten/documents</c>. That service now reads the progression table and the event sequence with the studio's own
/// SQL and answers "no event store here" as a value, so this gate is no longer what stands between the sidebar and a
/// migration - it is kept because it is still the cheaper answer: <see cref="IEventDataService.DescribeAsync" /> is one
/// <c>information_schema</c> read through the shared, expiring column catalog, and it spares a database with no event
/// store the two queries that would each return nothing.
/// </para>
/// <para>
/// Both reads already answer "could not read" as a value rather than by throwing, so there is no per-call try here:
/// what this must not do is let one of them decide the other's fate. Anything that does throw - the scope no longer
/// resolving - is the indicator service's to catch, and it draws no badge for the whole read, as it always did.
/// </para>
/// </remarks>
internal sealed class MartenNavIndicatorContributor : INavIndicatorContributor
{
    private readonly IEventDataService events;
    private readonly IProjectionDataService projections;

    public MartenNavIndicatorContributor(IEventDataService events, IProjectionDataService projections)
    {
        this.events = events;
        this.projections = projections;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, StudioNavBadge>> ReadAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        MartenNavReading reading = await ReadNumbersAsync(scope, cancellationToken).ConfigureAwait(false);

        return MartenNavBadges.From(reading.DeadLetters, reading.ProjectionsNeedAttention, reading.ProjectionsExplanation);
    }

    /// <summary>
    /// The two numbers the badges are drawn from - where "zero" and "could not tell" are still different answers.
    /// </summary>
    /// <param name="scope">The circuit's active scope, which the scope policy has passed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal async Task<MartenNavReading> ReadNumbersAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // CountDeadLettersAsync is already safe on a database with no event store - it asks the column catalog
        // whether mt_doc_deadletterevent exists and answers a real zero when it does not.
        long? deadLetters = await events.CountDeadLettersAsync(scope, cancellationToken).ConfigureAwait(false);

        // Hard rule 14. DescribeAsync is the cheapest honest answer to "does this database have an event store": one
        // information_schema read through the shared, expiring ColumnCatalog, and no Marten call of any kind. A shape
        // that could not be read leaves EventTablesExist false, so an unreachable database is treated exactly as one
        // with no event tables - the conservative way round, because the only thing on the other side of this branch
        // is a migration.
        EventStoreShape shape = await events.DescribeAsync(scope, cancellationToken).ConfigureAwait(false);

        if (!shape.EventTablesExist)
        {
            return new MartenNavReading(deadLetters, false, null);
        }

        ProjectionSummary summary = await projections.GetSummaryAsync(scope, cancellationToken).ConfigureAwait(false);

        return new MartenNavReading(
            deadLetters,
            summary.Error is null && summary.NeedsAttention,
            MartenNavBadges.Explain(summary));
    }
}

/// <summary>What MartenStudio's two badges are drawn from.</summary>
/// <param name="DeadLetters">
/// How many dead letters this database holds, or <see langword="null" /> when nobody could tell. Zero and "could not
/// tell" are different answers and neither draws a badge.
/// </param>
/// <param name="ProjectionsNeedAttention">Whether a shard is lagging past the amber threshold, paused, or failed.</param>
/// <param name="ProjectionsExplanation">What to put in the dot's tooltip, when there is a dot.</param>
internal sealed record MartenNavReading(long? DeadLetters, bool ProjectionsNeedAttention, string? ProjectionsExplanation);

/// <summary>How MartenStudio's two badges read - in one place, whoever read the numbers.</summary>
internal static class MartenNavBadges
{
    /// <summary>No badge at all.</summary>
    public static IReadOnlyDictionary<string, StudioNavBadge> None { get; } = new Dictionary<string, StudioNavBadge>(StringComparer.Ordinal);

    /// <summary>The projections dot's tooltip: which shard is how far behind, and how many are paused or failed.</summary>
    /// <param name="summary">The projections summary the badge was read from.</param>
    internal static string? Explain(ProjectionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

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

    /// <summary>The badges for a dead-letter count and a projections verdict.</summary>
    /// <param name="deadLetters">How many dead letters, or <see langword="null" /> when nobody could tell.</param>
    /// <param name="projectionsNeedAttention">Whether a shard is lagging, paused or failed.</param>
    /// <param name="projectionsExplanation">The dot's tooltip.</param>
    public static IReadOnlyDictionary<string, StudioNavBadge> From(long? deadLetters, bool projectionsNeedAttention, string? projectionsExplanation)
    {
        Dictionary<string, StudioNavBadge> badges = new(StringComparer.Ordinal);

        // Zero and "could not tell" are different answers, and neither draws a badge.
        if (deadLetters is { } count && count > 0)
        {
            badges[MartenStudioDescriptor.DeadLettersBadge] = new StudioNavBadge(
                count.ToString("N0", CultureInfo.InvariantCulture),
                "ms-nav-badge-danger",
                count == 1
                    ? "One event the async daemon could not apply"
                    : $"{count:N0} events the async daemon could not apply");
        }

        if (projectionsNeedAttention)
        {
            badges[MartenStudioDescriptor.ProjectionsBadge] = new StudioNavBadge(
                string.Empty,
                "ms-nav-badge-dot ms-nav-badge-warning",
                projectionsExplanation ?? "A projection needs attention");
        }

        return badges;
    }
}
