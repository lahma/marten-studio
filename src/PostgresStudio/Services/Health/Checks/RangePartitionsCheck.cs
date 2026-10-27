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

/// <summary>Where the rows of a range-partitioned table on an integer key stop, against its newest partition (T20, X4).</summary>
/// <param name="Upper">The newest upper bound.</param>
/// <param name="Width">The newest partition's width, or <see langword="null" /> when its lower bound is <c>MINVALUE</c>.</param>
/// <param name="PopulatedUpper">The upper bound of the highest partition that holds rows.</param>
/// <param name="EmptyAbove">How many partitions lie wholly above the rows: the room left, in partitions.</param>
internal sealed record DataRunway(double Upper, double? Width, double PopulatedUpper, int EmptyAbove)
{
    /// <summary>The rows reach the newest partition: within one partition-width of its bound.</summary>
    public bool InNewest => EmptyAbove == 0;

    /// <summary>At most one empty partition is left above the rows: within two partition-widths of the newest bound.</summary>
    public bool WithinTwoPartitions => EmptyAbove <= 1;
}

/// <summary>
/// T20: a table partitioned by range on a time key whose newest partition runs out soon or has run out, one on an integer
/// key whose rows near its newest partition's end (X4) - or one whose <c>DEFAULT</c> partition is taking rows (W5-B1, moved
/// from W5-P1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule</b> (W5-P1 step 2), for a single-column range on a <c>date</c>, <c>timestamp</c> or <c>timestamptz</c> key:
/// <b>Critical</b> when there is no default partition and the newest upper bound has passed or is within one partition-width
/// of now, by the key's own spacing (the newest partition's width) - inserts for the next period then fail; <b>Warning</b>
/// when it is ahead by fewer than two widths, or - with a default partition, which catches them - when it has passed (variant
/// <c>running-out</c>). Rows in the default partition are a Warning of their own (variant <c>default-partition-rows</c>):
/// they are rows no partition was created for, and creating that partition later fails while they are there. A <c>MAXVALUE</c>
/// upper bound never runs out.
/// </para>
/// <para>
/// <b>An integer key against its data</b> (X4). A range on a <c>smallint</c>, <c>integer</c> or <c>bigint</c> key has no clock,
/// so it is judged by where its rows stop: a <b>Warning</b> (variant <c>running-out</c>) when the rows reach the newest
/// partition, or leave at most one empty partition above them - within two partition-widths of the newest bound, the time
/// rule's line. Never Critical: how soon the data gets there is the application's pace, which the catalog does not show (a
/// sequence-fed id gets there for certain, a code or a bucket number perhaps never). Where the rows stop is each partition's
/// estimate (<c>reltuples</c>, or <c>n_live_tup</c> where higher, its sub-partitions' summed), so the judgement is by
/// partition, never by value, and a partition whose rows the statistics have not counted yet reads as empty.
/// </para>
/// <para>
/// <b>A range its source keeps ahead</b> (<see cref="StudioTable.RangePartitionsManaged" />: a Marten document table on
/// <c>ByRollingRange(...)</c>, which provisions <c>periodsAhead</c> periods and always has a <c>DEFAULT</c> partition) is warned
/// of only within one width: Marten's recommended <c>periodsAhead: 1</c> leaves the newest bound more than one width ahead
/// whenever its roll-forward has run, so two widths would warn of it forever (orchestrator decision, W5-B1-rest). Every other
/// range keeps the two-widths rule.
/// </para>
/// <para>
/// <b>Read without a lock</b> (<see cref="PartitionQueries" />): the bounds through <c>pg_get_expr(relpartbound, 0)</c>, which
/// opens no relation, parsed by <see cref="PartitionBoundText" />; a <c>timestamp</c> key is read as UTC, and a time bound only
/// under an ISO <c>DateStyle</c> - a range whose bounds were written in another is not judged, and the row says so.
/// </para>
/// </remarks>
[HealthCheck("T20", HealthArea.Tables, HealthCostTier.Catalog, "Range partitions running out", Variants = [RunningOutVariant, DefaultRowsVariant], Gate = HealthGate.Database)]
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
    internal static bool IsTimeKey(string? type) => PartitionBoundText.IsTimeType(type);

    /// <summary>Whether a key type is an integer.</summary>
    internal static bool IsIntegerKey(string? type) => PartitionBoundText.IsIntegerType(type);

    /// <summary>
    /// Where an integer-keyed range's rows stop against its newest bound, or <see langword="null" /> when there is none to judge:
    /// another strategy or key, a <c>MAXVALUE</c> partition, no bound read, or no partition the catalog says holds rows.
    /// </summary>
    internal static DataRunway? Data(HealthPartitionedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Strategy != "r" || row.KeyColumns != 1 || !IsIntegerKey(row.KeyType) || row.UnboundedAbove
            || row.NewestUpper is not { } upper || row.PopulatedUpper is not { } populated)
        {
            return null;
        }

        double? width = row.NewestLower is { } lower && upper > lower ? upper - lower : null;
        return new DataRunway(upper, width, populated, row.EmptyAbove);
    }

    /// <summary>The largest value an integer key type holds, as a double: past it no partition can be written.</summary>
    private static double IntegerMax(string type) => type switch
    {
        "smallint" => short.MaxValue,
        "integer" => int.MaxValue,
        _ => long.MaxValue,
    };

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
    internal static RangePartitionNext? Next(HealthPartitionedTableRow row, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.KeyType is not { } type || row.NewestUpper is not { } upper || row.NewestLower is not { } lower || !(upper > lower))
        {
            return null;
        }

        double width = upper - lower;

        if (IsIntegerKey(type))
        {
            // No clock, so no periods to catch up on (X4); and a next partition past what the key type holds is none at all.
            return upper + width <= IntegerMax(type)
                ? new RangePartitionNext(type, upper, upper + width, "p" + upper.ToString("0", CultureInfo.InvariantCulture), row.HasDefault)
                : null;
        }

        double behind = now is { } at ? (at.ToUnixTimeMilliseconds() / 1000d) - upper : 0;
        int after = behind >= width ? (int) Math.Min(Math.Floor(behind / width), int.MaxValue) : 0;

        // Month boundaries are looked for where the bounds were cut: a timestamptz range in the session's TimeZone, which a
        // bound written without an offset was read in (W5-B1 review F10); a timestamp or date range on its own wall clock.
        TimeZoneInfo zone = ZoneOf(row) ?? TimeZoneInfo.Utc;
        DateTime from = Local(upper, zone);
        DateTime start = Local(lower, zone);
        double to = MonthsBetween(start, from) is { } months and > 0
            ? Instant(from.AddMonths(months), zone)
            : upper + width;

        return new RangePartitionNext(type, upper, to, "p" + from.ToString("yyyyMMdd", CultureInfo.InvariantCulture), row.HasDefault)
        {
            PeriodsAfter = after,
        };
    }

    /// <summary>
    /// The zone a <c>timestamptz</c> key's month boundaries are looked for in - the session's <c>TimeZone</c> - or
    /// <see langword="null" /> for UTC: another key type, no zone read, UTC itself, or a zone this host does not know
    /// (<see cref="UnknownZone" />).
    /// </summary>
    internal static TimeZoneInfo? ZoneOf(HealthPartitionedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row is { KeyType: "timestamp with time zone", TimeZone: { Length: > 0 } id }
               && !string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(id, "Etc/UTC", StringComparison.OrdinalIgnoreCase)
               && TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone)
            ? zone
            : null;
    }

    /// <summary>Whether a <c>timestamptz</c> key's session zone could not be resolved here, so months were looked for in UTC.</summary>
    internal static bool UnknownZone(HealthPartitionedTableRow row) =>
        row is { KeyType: "timestamp with time zone", TimeZone: { Length: > 0 } id }
        && !string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(id, "Etc/UTC", StringComparison.OrdinalIgnoreCase)
        && ZoneOf(row) is null;

    /// <summary>A bound's wall-clock time in <paramref name="zone" />.</summary>
    private static DateTime Local(double seconds, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds((long) Math.Round(seconds * 1000d)).UtcDateTime, zone);

    /// <summary>A wall-clock time in <paramref name="zone" /> as Unix seconds - an hour on where a clock change skips it.</summary>
    private static double Instant(DateTime local, TimeZoneInfo zone)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000d;
    }

    /// <summary>Whole calendar months between two wall-clock midnights on the first of a month, or <see langword="null" />.</summary>
    private static int? MonthsBetween(DateTime from, DateTime to)
    {
        static bool FirstOfMonth(DateTime at) => at is { Day: 1, TimeOfDay.Ticks: 0 };

        return FirstOfMonth(from) && FirstOfMonth(to)
            ? (to.Year * 12) + to.Month - ((from.Year * 12) + from.Month)
            : null;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PartitionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];
        int judged = 0;
        int unreadTime = 0;
        string? dateStyle = null;

        foreach (HealthPartitionedTableRow row in facts.Tables)
        {
            if (row.Strategy != "r" || row.KeyColumns != 1 || !(IsTimeKey(row.KeyType) || IsIntegerKey(row.KeyType)) || facts.TableOf(row) is not { } table)
            {
                continue;
            }

            judged++;

            if (IsTimeKey(row.KeyType) && row.UnreadBounds > 0 && !PartitionBoundText.IsIsoDateStyle(row.DateStyle))
            {
                unreadTime++;
                dateStyle = row.DateStyle;
            }

            // A range the source's own configuration keeps ahead (Marten's ByRollingRange, which always has a DEFAULT) is
            // short only once its schedule has fallen behind: periodsAhead 1 always leaves more than one width.
            if (Runway(row, evaluation.Now) is { } runway
                && ((table.RangePartitionsManaged ? runway.WithinOneWidth : runway.WithinTwoWidths) || (runway.Width is null && runway.Passed)))
            {
                bool critical = !row.HasDefault && runway.WithinOneWidth;
                findings.Add(RunningOut(table, row, runway, critical, evaluation));
            }

            if (Data(row) is { WithinTwoPartitions: true } data)
            {
                findings.Add(DataRunningOut(table, row, data, evaluation));
            }

            if (row.HasDefault && row.DefaultRows > 0)
            {
                findings.Add(DefaultRows(table, row));
            }
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        if (judged == 0)
        {
            return HealthVerdict.Quiet("No table the studio judges is partitioned by range on one time or integer column.");
        }

        return HealthVerdict.Quiet(unreadTime == 0
            ? null
            : TableText.Count(unreadTime)
              + (unreadTime == 1
                  ? " table partitioned by range on a time column was not judged: its bounds are"
                  : " tables partitioned by range on a time column were not judged: their bounds are")
              + " written in the session's DateStyle, " + dateStyle + ", and the studio reads time bounds only in ISO, Postgres' default.");
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
                + "as UTC, and a timestamptz range's months are looked for in the session's TimeZone"
                + (UnknownZone(row)
                    ? " - but " + row.TimeZone + " is not a zone this host knows, so they were looked for in UTC, and a range cut at "
                      + "local midnights is given a next partition of the newest one's width instead"
                    : string.Empty)
                + ". The bounds are read from the catalog without opening a partition.",
            Remedy = Next(row, evaluation.Now) is { } next ? evaluation.TableRemedies?.ExtendRangePartitions(table, next) : null,
        };
    }

    private static HealthFinding DataRunningOut(StudioTable table, HealthPartitionedTableRow row, DataRunway data, HealthEvaluation evaluation)
    {
        string upper = TableText.Count(data.Upper);

        return new HealthFinding
        {
            Code = "T20",
            Severity = HealthSeverity.Warning,
            Variant = RunningOutVariant,
            Subject = table.Key,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = table.GatedSchemas,
            Title = data.InNewest
                ? "The rows of " + table.Key + " reach its newest range partition, which ends at " + upper
                : "One empty range partition is left above the rows of " + table.Key + ", up to " + upper,
            WhyItMatters =
                (row.HasDefault
                    ? "Rows past " + upper + " land in the DEFAULT partition, which grows without the partitioning's benefits, and the "
                      + "next partition cannot then be created until they are moved out of it."
                    : "There is no DEFAULT partition, so an insert past " + upper + " fails: \"no partition of relation found for row\".")
                + " A range on an integer key has no clock: how soon the rows get there is the application's pace, which the "
                + "catalog does not show - an id a sequence hands out gets there for certain, a code or a bucket number perhaps "
                + "never. Partitions are created ahead of the data by whoever manages them, and this one is close to its end.",
            Evidence =
            [
                new HealthEvidence("Table", table.Describe()),
                new HealthEvidence("Partition key", row.KeyType ?? "unknown"),
                new HealthEvidence("Newest upper bound", upper),
                new HealthEvidence("The rows reach", "the partition ending at " + TableText.Count(data.PopulatedUpper)),
                new HealthEvidence("Empty partitions above the rows", TableText.Count(data.EmptyAbove)),
                new HealthEvidence("Partition width", data.Width is { } width ? TableText.Count(width) + " (the newest partition's)" : "unknown: its lower bound is MINVALUE"),
                new HealthEvidence("Partitions", TableText.Count(row.Leaves)),
                new HealthEvidence("DEFAULT partition", row.HasDefault ? "yes" : "none"),
            ],
            Caveat =
                "Where the rows stop is each partition's estimated rows - reltuples, or n_live_tup where higher, as of the last "
                + "VACUUM, ANALYZE or statistics flush - so it is the partition the highest rows sit in, never the highest value, "
                + "and a partition whose rows the statistics have not counted yet reads as empty. No row was read. The bounds are "
                + "read from the catalog without opening a partition.",
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
