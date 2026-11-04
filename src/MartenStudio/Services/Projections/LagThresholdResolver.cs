namespace MartenStudio.Services.Projections;

/// <summary>Where the lag thresholds that apply to one projection come from.</summary>
internal enum LagThresholdSource
{
    /// <summary>The studio's own 1,000 and 100,000 (<see cref="ProjectionLagThresholds" />).</summary>
    StudioDefault,

    /// <summary>
    /// Numbers a host wrote itself into a progression row's <c>warning_behind_threshold</c> and <c>critical_behind_threshold</c>
    /// (Marten creates the columns and writes neither).
    /// </summary>
    ProgressionRow,

    /// <summary><see cref="MartenStudioOptions.ProjectionThresholds" />.</summary>
    Option,
}

/// <summary>
/// The one place the precedence of lag thresholds is decided: <see cref="MartenStudioOptions.ProjectionThresholds" /> for the
/// shard's projection name, then a value a host wrote into the row's own columns, then the studio's defaults.
/// </summary>
/// <remarks>
/// <para>
/// The option is applied by <b>projection name</b> to every store, and as a pair: it states both numbers, so it replaces
/// whatever the row carries rather than mixing with it. A row's columns are still read as they always were, each
/// independently, so a row with one column set gets the studio's default for the other.
/// </para>
/// <para>
/// Every consumer reads a threshold off the model it already has - <see cref="ShardProgress" /> (the colours, the chart,
/// the Overview), <see cref="ShardTrend" /> (D04 and the arrow) and D03's recorded series - so this class puts the option
/// into those models once, where each is built, and nothing downstream knows there is an option except to say where a
/// number came from.
/// </para>
/// </remarks>
internal static class LagThresholdResolver
{
    private static readonly IReadOnlyDictionary<string, ProjectionLagThreshold> None =
        new Dictionary<string, ProjectionLagThreshold>(StringComparer.Ordinal);

    /// <summary>The option for <paramref name="projectionName" />, or <see langword="null" /> when the host set none.</summary>
    public static ProjectionLagThreshold? OptionFor(
        IReadOnlyDictionary<string, ProjectionLagThreshold>? options,
        string? projectionName) =>
        options is { Count: > 0 } && projectionName is { Length: > 0 } && options.TryGetValue(projectionName, out ProjectionLagThreshold? found)
            ? found
            : null;

    /// <summary>
    /// The thresholds that apply to a shard, as numbers when somebody set them: the option's pair when there is one, else the
    /// row's two columns, each as it is (a column nobody wrote is <see langword="null" />, and the consumer's default applies).
    /// </summary>
    /// <param name="option">The host's option for the shard's projection.</param>
    /// <param name="rowWarning">The row's <c>warning_behind_threshold</c>.</param>
    /// <param name="rowCritical">The row's <c>critical_behind_threshold</c>.</param>
    public static (long? Warning, long? Critical) Applying(ProjectionLagThreshold? option, long? rowWarning, long? rowCritical) =>
        option is null ? (rowWarning, rowCritical) : (option.WarningBehind, option.CriticalBehind);

    /// <summary>Each shard of <paramref name="shards" /> with its projection's option applied, where it has one.</summary>
    public static List<ShardProgress> Apply(
        IReadOnlyList<ShardProgress> shards,
        IReadOnlyDictionary<string, ProjectionLagThreshold>? options)
    {
        ArgumentNullException.ThrowIfNull(shards);

        if (options is not { Count: > 0 })
        {
            return [.. shards];
        }

        return
        [
            .. shards.Select(shard => OptionFor(options, shard.ProjectionName) is { } option
                ? shard with
                {
                    WarningThreshold = option.WarningBehind,
                    CriticalThreshold = option.CriticalBehind,
                    ThresholdsFromOption = true,
                }
                : shard),
        ];
    }

    /// <summary><paramref name="trend" /> with <paramref name="option" /> applied, when there is one.</summary>
    public static ShardTrend Apply(ShardTrend trend, ProjectionLagThreshold? option)
    {
        ArgumentNullException.ThrowIfNull(trend);

        return option is null
            ? trend
            : trend with
            {
                WarningThreshold = option.WarningBehind,
                CriticalThreshold = option.CriticalBehind,
                ThresholdsFromOption = true,
            };
    }

    /// <summary>The options a check reads off the studio's settings, or none when the settings are not Marten Studio's.</summary>
    public static IReadOnlyDictionary<string, ProjectionLagThreshold> From(object? settings) =>
        settings is MartenStudioSettings marten ? marten.ProjectionThresholds : None;

    /// <summary>
    /// Where the thresholds that apply to each projection come from, for the Projections page's disclosure: only the
    /// projections whose thresholds are not the studio's defaults, then every option naming a projection this store has none
    /// of.
    /// </summary>
    /// <param name="projections">The store's projections.</param>
    /// <param name="shards">Every registered shard, with <see cref="ShardProgress.ThresholdsFromOption" /> already applied.</param>
    /// <param name="options">The host's option.</param>
    public static ThresholdDisclosure Describe(
        IReadOnlyList<ProjectionInfo> projections,
        IReadOnlyList<ShardProgress> shards,
        IReadOnlyDictionary<string, ProjectionLagThreshold>? options)
    {
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(shards);

        List<ProjectionThresholdSource> sources = [];
        HashSet<string> known = new(StringComparer.Ordinal);

        foreach (ProjectionInfo projection in projections.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            known.Add(projection.Name);

            if (OptionFor(options, projection.Name) is { } option)
            {
                sources.Add(new ProjectionThresholdSource(
                    projection.Name, LagThresholdSource.Option, option.WarningBehind, option.CriticalBehind));
                continue;
            }

            // The row's own columns, one shard at a time: the first shard that carries any is what is said of the projection.
            ShardProgress? withRow = shards.FirstOrDefault(x =>
                string.Equals(x.ProjectionName, projection.Name, StringComparison.Ordinal)
                && (x.WarningThreshold is not null || x.CriticalThreshold is not null));

            if (withRow is not null)
            {
                sources.Add(new ProjectionThresholdSource(
                    projection.Name, LagThresholdSource.ProgressionRow, withRow.EffectiveWarningThreshold, withRow.EffectiveCriticalThreshold));
            }
        }

        string[] unmatched = options is { Count: > 0 }
            ? [.. options.Keys.Where(x => !known.Contains(x)).Order(StringComparer.Ordinal)]
            : [];

        return new ThresholdDisclosure(sources, unmatched);
    }
}

/// <summary>One projection's thresholds, and whose they are.</summary>
/// <param name="Projection">The projection.</param>
/// <param name="Source">Where they come from.</param>
/// <param name="Warning">Amber above this many events behind.</param>
/// <param name="Critical">Red above this many events behind.</param>
internal sealed record ProjectionThresholdSource(string Projection, LagThresholdSource Source, long Warning, long Critical);

/// <summary>What the Projections page's "Thresholds that apply" says about per-projection thresholds.</summary>
/// <param name="Projections">The projections whose thresholds are not the studio's defaults, by name.</param>
/// <param name="UnmatchedOptions">Names in <see cref="MartenStudioOptions.ProjectionThresholds" /> that this store has no projection of.</param>
internal sealed record ThresholdDisclosure(IReadOnlyList<ProjectionThresholdSource> Projections, IReadOnlyList<string> UnmatchedOptions)
{
    /// <summary>Whether there is nothing to say: every projection runs on the studio's defaults, and no option is unmatched.</summary>
    public bool IsEmpty => Projections.Count == 0 && UnmatchedOptions.Count == 0;
}
