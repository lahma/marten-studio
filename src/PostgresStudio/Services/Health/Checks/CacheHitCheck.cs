using System.Globalization;

using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What C11 reads: the window's signals, whose <c>blks_hit</c> and <c>blks_read</c> are this database's since they began.</summary>
/// <param name="Reading">The sweep's one read of the statistics window (<see cref="StatisticsWindow.ReadingAsync" />).</param>
/// <param name="Platform">Who runs the server, for the one sentence that differs by platform (W5-AUR's A5 hook).</param>
internal sealed record CacheHitFacts(StatisticsReading Reading, PostgresPlatform Platform);

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

        return new CacheHitFacts(reading, server.Platform);
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
    /// What a miss costs on the server's platform, as one more sentence of the "why" - the hook issue #8 A5 (W5-AUR) words
    /// "on Aurora Standard this is billed I/O" through. Nothing anywhere else, and nothing until then.
    /// </summary>
    internal static string PlatformCost(CacheHitFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return string.Empty;
    }

    /// <summary>A ratio, to a tenth of a percent - 94.9 % is not 95 %.</summary>
    private static string Ratio(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A count of blocks, whole and grouped.</summary>
    private static string Blocks(double blocks) => SessionText.Number((long) Math.Round(blocks));

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
