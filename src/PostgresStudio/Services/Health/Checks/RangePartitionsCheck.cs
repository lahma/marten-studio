using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>Where a range-partitioned table's newest partition ends, against now (T20).</summary>
/// <param name="Upper">The newest upper bound, Unix seconds.</param>
/// <param name="Width">That partition's width in seconds, or <see langword="null" /> when its lower bound is <c>MINVALUE</c>.</param>
/// <param name="Ahead">How far the bound is ahead of now, in seconds: negative once it has passed.</param>
internal sealed record RangeRunway(double Upper, double? Width, double Ahead)
{
    /// <summary>The bound has passed: rows for now have no partition of their own.</summary>
    public bool Passed => Ahead <= 0;

    /// <summary>Within one partition-width of now, or passed.</summary>
    public bool WithinOneWidth => Passed || (Width is { } width && Ahead <= width);

    /// <summary>Ahead by fewer than two partition widths.</summary>
    public bool WithinTwoWidths => WithinOneWidth || (Width is { } width && Ahead < 2 * width);
}

/// <summary>
/// T20: a table partitioned by range on a time key whose newest partition runs out soon or has run out - or whose
/// <c>DEFAULT</c> partition is taking rows (W5-B1, moved from W5-P1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule</b> (W5-P1 step 2), for a single-column range on a <c>date</c>, <c>timestamp</c> or <c>timestamptz</c> key:
/// <b>Critical</b> when there is no default partition and the newest upper bound has passed or is within one partition-width
/// of now, by the key's own spacing (the newest partition's width) - inserts for the next period then fail; <b>Warning</b>
/// when it is ahead by fewer than two widths, or - with a default partition, which catches them - when it has passed (variant
/// <c>running-out</c>). Rows in the default partition are a Warning of their own (variant <c>default-partition-rows</c>):
/// they are rows no partition was created for, and creating that partition later fails while they are there. A range on an
/// integer key without a time meaning is judged on its default partition alone. A <c>MAXVALUE</c> upper bound never runs out.
/// </para>
/// <para>
/// <b>Read without a lock</b> (<see cref="PartitionQueries" />): the bounds through <c>pg_get_expr(relpartbound, 0)</c>, which
/// opens no relation; a <c>timestamp</c> key is read as UTC.
/// </para>
/// </remarks>
[HealthCheck("T20", HealthArea.Tables, HealthCostTier.Catalog, "Range partitions running out", Variants = [RunningOutVariant, DefaultRowsVariant])]
internal sealed class RangePartitionsCheck : HealthCheck<PartitionFacts>
{
    /// <summary>The variant of a newest bound that has passed or is close.</summary>
    public const string RunningOutVariant = "running-out";

    /// <summary>The variant of rows in the default partition.</summary>
    public const string DefaultRowsVariant = "default-partition-rows";

    /// <inheritdoc />
    public override Task<PartitionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        HealthPartitions.SharedAsync(context);

    /// <summary>Whether a key type is a time the bound can be compared with now.</summary>
    internal static bool IsTimeKey(string? type) => type is "date" or "timestamp without time zone" or "timestamp with time zone";

    /// <summary>Whether a key type is an integer.</summary>
    internal static bool IsIntegerKey(string? type) => type is "smallint" or "integer" or "bigint";

    /// <summary>The runway of a time-keyed range table, or <see langword="null" /> when there is none to judge.</summary>
    internal static RangeRunway? Runway(HealthPartitionedTableRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Strategy != "r" || row.KeyColumns != 1 || !IsTimeKey(row.KeyType) || row.UnboundedAbove || row.NewestUpper is not { } upper || !double.IsFinite(upper))
        {
            return null;
        }

        double? width = row.NewestLower is { } lower && double.IsFinite(lower) && upper > lower ? upper - lower : null;
        return new RangeRunway(upper, width, upper - now.ToUnixTimeMilliseconds() / 1000d);
    }

    /// <summary>
    /// The partition the next period needs: from the newest upper bound, one width on - a calendar month (or several) when the
    /// newest partition spans whole months from midnight on the first, so a February is not given January's 31 days.
    /// </summary>
    internal static RangePartitionNext? Next(HealthPartitionedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.KeyType is not { } type || row.NewestUpper is not { } upper || row.NewestLower is not { } lower || !(upper > lower))
        {
            return null;
        }

        if (IsIntegerKey(type))
        {
            return new RangePartitionNext(type, upper, upper + (upper - lower), "p" + upper.ToString("0", CultureInfo.InvariantCulture), row.HasDefault);
        }

        DateTimeOffset from = DateTimeOffset.FromUnixTimeMilliseconds((long) Math.Round(upper * 1000d));
        DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds((long) Math.Round(lower * 1000d));
        DateTimeOffset to = MonthsBetween(start, from) is { } months and > 0
            ? from.AddMonths(months)
            : from.AddSeconds(upper - lower);

        return new RangePartitionNext(type, upper, to.ToUnixTimeMilliseconds() / 1000d, "p" + from.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), row.HasDefault);
    }

    /// <summary>Whole calendar months between two midnights on the first of a month, or <see langword="null" />.</summary>
    private static int? MonthsBetween(DateTimeOffset from, DateTimeOffset to)
    {
        static bool FirstOfMonth(DateTimeOffset at) => at.UtcDateTime is { Day: 1, TimeOfDay.Ticks: 0 };

        return FirstOfMonth(from) && FirstOfMonth(to)
            ? (to.UtcDateTime.Year * 12) + to.UtcDateTime.Month - ((from.UtcDateTime.Year * 12) + from.UtcDateTime.Month)
            : null;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PartitionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];
        int judged = 0;

        foreach (HealthPartitionedTableRow row in facts.Tables)
        {
            if (row.Strategy != "r" || row.KeyColumns != 1 || !(IsTimeKey(row.KeyType) || IsIntegerKey(row.KeyType)) || facts.TableOf(row) is not { } table)
            {
                continue;
            }

            judged++;

            if (Runway(row, evaluation.Now) is { } runway
                && (runway.WithinTwoWidths || (runway.Width is null && runway.Passed)))
            {
                bool critical = !row.HasDefault && runway.WithinOneWidth;
                findings.Add(RunningOut(table, row, runway, critical, evaluation));
            }

            if (row.HasDefault && row.DefaultRows > 0)
            {
                findings.Add(DefaultRows(table, row));
            }
        }

        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Quiet(judged == 0 ? "No table the studio judges is partitioned by range on one time or integer column." : null);
    }

    private static string When(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long) Math.Round(seconds * 1000d)).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Width(double seconds) => TableText.Span(TimeSpan.FromSeconds(seconds));

    private static HealthFinding RunningOut(StudioTable table, HealthPartitionedTableRow row, RangeRunway runway, bool critical, HealthEvaluation evaluation)
    {
        string state = runway.Passed
            ? "ran out at " + When(runway.Upper)
            : "runs out at " + When(runway.Upper) + ", in " + Width(runway.Ahead);

        return new HealthFinding
        {
            Code = "T20",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Variant = RunningOutVariant,
            Subject = table.Key,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = table.GatedSchemas,
            Title = "The newest range partition of " + table.Key + " " + state,
            WhyItMatters =
                (row.HasDefault
                    ? "Rows past the newest bound land in the DEFAULT partition, which grows without the partitioning's benefits, and "
                      + "the next period's partition cannot then be created until they are moved out of it."
                    : "There is no DEFAULT partition, so an insert past the newest bound fails: \"no partition of relation found for "
                      + "row\".")
                + " Partitions are created ahead of time by whoever manages them - a scheduled job, pg_partman, or the library that "
                + "declares the table - and this one is not being kept ahead.",
            Evidence =
            [
                new HealthEvidence("Table", table.Describe()),
                new HealthEvidence("Partition key", row.KeyType ?? "unknown"),
                new HealthEvidence("Newest upper bound", When(runway.Upper)),
                new HealthEvidence("Partition width", runway.Width is { } width ? Width(width) + " (the newest partition's)" : "unknown: its lower bound is MINVALUE"),
                new HealthEvidence("Partitions", TableText.Count(row.Leaves)),
                new HealthEvidence("DEFAULT partition", row.HasDefault ? "yes" : "none"),
            ],
            Caveat =
                "Judged by the studio's clock against the newest partition's own width; a timestamp key (without time zone) is read "
                + "as UTC. The bounds are read from the catalog without opening a partition.",
            Remedy = Next(row) is { } next ? evaluation.TableRemedies?.ExtendRangePartitions(table, next) : null,
        };
    }

    private static HealthFinding DefaultRows(StudioTable table, HealthPartitionedTableRow row) => new()
    {
        Code = "T20",
        Severity = HealthSeverity.Warning,
        Variant = DefaultRowsVariant,
        Subject = table.Key,
        DocumentTypes = table.DocumentTypes,
        GatedSchemas = table.GatedSchemas,
        Title = "The DEFAULT partition of " + table.Key + " holds about " + TableText.Count(row.DefaultRows) + " rows",
        WhyItMatters =
            "A row lands in the DEFAULT partition only when no range takes it - a partition that was never created, or a key outside "
            + "every range. Queries for those rows read the whole default partition, and creating the range they belong to fails "
            + "while they sit there (\"updated partition constraint for default partition would be violated\"): they must be moved "
            + "out first, under a lock on the default partition.",
        Evidence =
        [
            new HealthEvidence("Table", table.Describe()),
            new HealthEvidence("Partition key", row.KeyType ?? "unknown"),
            new HealthEvidence("Rows in the DEFAULT partition", "~" + TableText.Count(row.DefaultRows) + " (reltuples, or n_live_tup where higher)"),
            new HealthEvidence("Partitions", TableText.Count(row.Leaves)),
        ],
        Caveat =
            "Estimated from the catalog and the statistics collector, as of the last VACUUM, ANALYZE or statistics flush. Which "
            + "range the rows belong to is in the rows themselves, which the sweep never reads: no statement is offered to move "
            + "them.",
    };
}
