using System.Globalization;

using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What C11 reads: the window's signals, whose <c>blks_hit</c> and <c>blks_read</c> are this database's since they began.</summary>
/// <param name="Reading">The sweep's one read of the statistics window (<see cref="StatisticsWindow.ReadingAsync" />).</param>
/// <param name="Platform">Who runs the server, for the one sentence that differs by platform (W5-AUR's A5 hook).</param>
internal sealed record CacheHitFacts(StatisticsReading Reading, PostgresPlatform Platform)
{
    /// <summary>
    /// What the host's capacity source said this interval, on Aurora - whose <see cref="PostgresComputeCapacity.IoOptimized" />
    /// says which storage configuration bills the misses (A5) - or <see langword="null" />.
    /// </summary>
    public Aurora.CapacityReading? Capacity { get; init; }
}

/// <summary>
/// C11: this database finds too few of the blocks it reads in shared buffers, with enough of them read a day for it to matter
/// (issue #8 G5) - judged over the statistics window, once it has counted for a day.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hosts' own rule, as written.</b> Below 95 % of <c>blks_hit / (blks_hit + blks_read)</c> with at least a million blocks
/// read a day. Both counters are cumulative since <see cref="StatisticsWindow.Since" />, so the ratio is the window's and the
/// reads per day are <c>blks_read</c> over the window's days - which is why nothing is judged before the window is a day long,
/// or while the counters look reset: a ratio over an hour after a restart is a cold cache, not a small one.
/// </para>
/// <para>
/// <b>Which cache.</b> <c>blks_read</c> counts every block Postgres did not find in its own shared buffers; the operating
/// system's page cache may still have served it from memory. So a low ratio says the database's working set does not fit the
/// buffers it was given, not that every miss went to disk - the finding says so.
/// </para>
/// <para>
/// <b>A remedy in words.</b> More memory, or less to hold in it. Issue #8's R1 rejected tuning advice that derives a setting
/// from the machine's memory, so C11 names no setting and no value.
/// </para>
/// </remarks>
[HealthCheck("C11", HealthArea.Connections, HealthCostTier.Catalog, "Low cache hit ratio", Gate = HealthGate.Database)]
internal sealed class CacheHitCheck : HealthCheck<CacheHitFacts>
{
    /// <summary>Below this share of blocks found in shared buffers, with <see cref="BlocksReadPerDay" />, is a Warning (G5).</summary>
    internal const double HitRatioWarning = 0.95;

    /// <summary>…once at least this many blocks are read a day (G5).</summary>
    internal const double BlocksReadPerDay = 1_000_000;

    /// <inheritdoc />
    public override async Task<CacheHitFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        StatisticsReading reading = await StatisticsWindow.ReadingAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return new CacheHitFacts(reading, server.Platform)
        {
            Capacity = server.Platform.Kind == PostgresPlatformKind.AmazonAurora ? await Aurora.CapacityReading.SharedAsync(context).ConfigureAwait(false) : null,
        };
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(CacheHitFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        StatisticsWindow window = evaluation.Statistics ?? StatisticsWindow.Compute(facts.Reading, evaluation.History);

        if (!window.CoversADay)
        {
            return HealthVerdict.Quiet(window.Watching("the cache hit ratio", StatisticsWindow.Day));
        }

        long hit = facts.Reading.BlocksHit;
        long read = facts.Reading.BlocksRead;

        if (hit + read == 0)
        {
            return HealthVerdict.Quiet("Nothing was read in this database " + window.SinceText + ".");
        }

        double ratio = (double) hit / (hit + read);
        double readsPerDay = read / window.Days;

        string summary = Ratio(ratio) + " of this database's block reads were found in shared buffers " + window.SinceText + ", with "
            + Blocks(readsPerDay) + " blocks read from outside them a day";

        if (ratio >= HitRatioWarning || readsPerDay < BlocksReadPerDay)
        {
            return HealthVerdict.Quiet(Capitalised(summary) + " - judged only below " + Ratio(HitRatioWarning) + " with at least "
                + Blocks(BlocksReadPerDay) + " blocks read a day.");
        }

        return new HealthVerdict(
        [
            new HealthFinding
            {
                Code = "C11",
                Severity = HealthSeverity.Warning,
                Title = Capitalised(summary),
                WhyItMatters =
                    "A block Postgres does not find in its shared buffers is read from the operating system - from its page cache "
                    + "if it is lucky, from storage if not - so a database that misses this often, this many times a day, spends "
                    + "its time waiting on I/O rather than on its queries. What helps is more memory for the database, or less to "
                    + "hold in it: an index that reads fewer pages, dead rows vacuumed away, data nobody reads any more moved out."
                    + PlatformCost(facts),
                Evidence =
                [
                    new("Cache hit ratio", Ratio(ratio)),
                    new("Blocks read a day", Blocks(readsPerDay)),
                    new("Blocks read", SessionText.Number(read)),
                    new("Blocks found in shared buffers", SessionText.Number(hit)),
                    new("Window", SessionText.Duration(window.Length) + ", " + window.SinceText),
                    new("Counting since", window.BasisText),
                ],
                Caveat =
                    "Measured in pg_stat_database over the statistics window, which began " + window.ReasonText + ". blks_read "
                    + "counts every block not found in shared buffers, whether the operating system's cache then had it or not, so "
                    + "this is the database's own cache, not the disk's.",
            },
        ]);
    }

    /// <summary>
    /// What a miss costs on the server's platform, as one more sentence of the "why" (issue #8 A5, W5-AUR): on Aurora, whether
    /// the cluster's storage configuration bills it, from the host's capacity source's
    /// <see cref="PostgresComputeCapacity.IoOptimized" /> - and both cases when it does not say, because SQL cannot see which.
    /// Nothing on any other platform.
    /// </summary>
    /// <remarks>
    /// Aurora Standard: "In addition to the usage and storage of your DB clusters, you also pay a standard rate per 1 million
    /// requests for I/O operations"; Aurora I/O-Optimized: "You pay only for the usage and storage of your DB clusters, with no
    /// additional charges for read and write I/O operations" (<i>Amazon Aurora storage</i>, Aurora User Guide, read 2026-10-26).
    /// </remarks>
    internal static string PlatformCost(CacheHitFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Platform.Kind != PostgresPlatformKind.AmazonAurora)
        {
            return string.Empty;
        }

        return facts.Capacity?.Capacity?.IoOptimized switch
        {
            true => " This cluster uses Aurora I/O-Optimized storage (its capacity source says so), which charges nothing for read "
                + "and write I/O: a miss costs time here, not money.",
            false => " This cluster uses Aurora Standard storage (its capacity source says so), where a read that misses the cache "
                + "and goes to the cluster volume is billed I/O - Aurora Standard charges \"a standard rate per 1 million requests "
                + "for I/O operations\" - so a low hit ratio is a cost as well as a wait.",
            _ => " On Aurora a read that misses the cache goes to the cluster volume, and whether that is billed depends on the "
                + "cluster's storage configuration, which SQL cannot see: on Aurora Standard reads that miss the cache are billed I/O "
                + "requests, on Aurora I/O-Optimized they are not (Amazon Aurora storage, Aurora User Guide). A capacity source that "
                + "sets IoOptimized says which.",
        };
    }

    /// <summary>A ratio, to a tenth of a percent - 94.9 % is not 95 %.</summary>
    private static string Ratio(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A count of blocks, whole and grouped.</summary>
    private static string Blocks(double blocks) => SessionText.Number((long) Math.Round(blocks));

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
