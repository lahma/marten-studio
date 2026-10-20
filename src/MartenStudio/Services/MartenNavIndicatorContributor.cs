using System.Globalization;

using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
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
/// pages read - so the store, database and tenant policies apply to a badge exactly as to its page. The projection half
/// is asked only where the event tables exist (hard rule 14): <see cref="IEventDataService.DescribeAsync" /> is one
/// catalog read through the shared, expiring column catalog, and it spares a database with no event store the queries
/// that would each return nothing.
/// </para>
/// <para>
/// PS-1a's implementation of the seam. <see cref="NavIndicatorService" /> still reads the same two numbers itself and
/// hands them to the sidebar through <c>NavIndicators.Badges</c>, worded by <see cref="MartenNavBadges" />; the shared
/// indicator service asks this contributor instead once the badges move out of it (PS-1d).
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
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            long? deadLetters = await events.CountDeadLettersAsync(scope, cancellationToken).ConfigureAwait(false);
            EventStoreShape shape = await events.DescribeAsync(scope, cancellationToken).ConfigureAwait(false);

            ProjectionSummary? summary = shape.EventTablesExist
                ? await projections.GetSummaryAsync(scope, cancellationToken).ConfigureAwait(false)
                : null;

            return MartenNavBadges.From(
                deadLetters,
                summary is { Error: null, NeedsAttention: true },
                summary is null ? null : NavIndicatorService.Explain(summary));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A decoration never takes navigation down: an unreadable badge is a key this does not answer.
            return MartenNavBadges.None;
        }
    }
}

/// <summary>How MartenStudio's two badges read - in one place, whoever read the numbers.</summary>
internal static class MartenNavBadges
{
    /// <summary>No badge at all.</summary>
    public static IReadOnlyDictionary<string, StudioNavBadge> None { get; } = new Dictionary<string, StudioNavBadge>(StringComparer.Ordinal);

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
