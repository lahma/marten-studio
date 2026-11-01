using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PostgresStudio.Internal.Sql;

/// <summary>What one end of a range bound is.</summary>
internal enum RangeEndKind
{
    /// <summary>A value the studio read: Unix seconds for a time key, the number for an integer one.</summary>
    Value,

    /// <summary><c>MINVALUE</c>, or <c>'-infinity'</c> for a time key: no lower end.</summary>
    MinValue,

    /// <summary><c>MAXVALUE</c>, or <c>'infinity'</c> for a time key: no upper end, so the range never runs out.</summary>
    MaxValue,

    /// <summary>Text the studio does not read: another key type, a date before Christ, a <c>DateStyle</c> other than ISO.</summary>
    Unread,
}

/// <summary>One end of a range bound.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Value">The value, when <paramref name="Kind" /> is <see cref="RangeEndKind.Value" />; 0 otherwise.</param>
internal readonly record struct RangeEnd(RangeEndKind Kind, double Value = 0)
{
    /// <summary>An end the studio does not read.</summary>
    public static RangeEnd Unread { get; } = new(RangeEndKind.Unread);

    /// <summary><c>MINVALUE</c>.</summary>
    public static RangeEnd Min { get; } = new(RangeEndKind.MinValue);

    /// <summary><c>MAXVALUE</c>.</summary>
    public static RangeEnd Max { get; } = new(RangeEndKind.MaxValue);

    /// <summary>A value.</summary>
    public static RangeEnd Of(double value) => new(RangeEndKind.Value, value);
}

/// <summary>A single-column range partition's bound: <c>FROM (lower) TO (upper)</c>.</summary>
/// <param name="Lower">The inclusive lower end.</param>
/// <param name="Upper">The exclusive upper end.</param>
internal sealed record RangeBound(RangeEnd Lower, RangeEnd Upper)
{
    /// <summary>Whether both ends were read.</summary>
    public bool IsRead => Lower.Kind != RangeEndKind.Unread && Upper.Kind != RangeEndKind.Unread;
}

/// <summary>What a range-partitioned table's partitions say, rolled into the figures T20 judges - never a partition's name.</summary>
/// <param name="Bounded">How many partitions have an upper end that was read as a value.</param>
/// <param name="NewestUpper">The highest such upper end, or <see langword="null" /> for none.</param>
/// <param name="NewestLower">That partition's lower end, or <see langword="null" /> for <c>MINVALUE</c> or none.</param>
/// <param name="UnboundedAbove">Whether a partition's upper end is <c>MAXVALUE</c> (or <c>'infinity'</c>).</param>
/// <param name="PopulatedUpper">
/// The upper end of the highest partition that holds rows by the catalog's estimate, or <see langword="null" /> when none does
/// (or none was read).
/// </param>
/// <param name="EmptyAbove">How many partitions lie wholly above <paramref name="PopulatedUpper" />, holding no rows yet.</param>
/// <param name="Unread">How many bounds were not read: another key type, a date before Christ, a <c>DateStyle</c> other than ISO.</param>
internal sealed record RangeSummary(
    int Bounded,
    double? NewestUpper,
    double? NewestLower,
    bool UnboundedAbove,
    double? PopulatedUpper,
    int EmptyAbove,
    int Unread)
{
    /// <summary>Nothing to summarise.</summary>
    public static RangeSummary Empty { get; } = new(0, null, null, false, null, 0, 0);
}

/// <summary>
/// Reads the text <c>pg_get_expr(relpartbound, 0)</c> gives for a partition's bound - <c>FOR VALUES FROM ('2026-10-01
/// 00:00:00+00') TO ('2026-11-01 00:00:00+00')</c>, <c>FOR VALUES IN ('acme')</c>, <c>DEFAULT</c> - into values (X4).
/// Pure: no clock, no culture, no database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the text, and why here.</b> The list reads its bounds with <c>pg_get_expr(relpartbound, 0)</c>, the one deparse that
/// opens no relation (D27, measured on 14.24 and 18.6); <c>pg_get_expr(relpartbound, relid)</c> and <c>pg_get_partkeydef</c>
/// queue behind a migration's <c>ACCESS EXCLUSIVE</c>. What it returns is Postgres' own output of each bound's constant, so it
/// is parsed here, as a pure function the tests hold to real strings captured from 14.24 and 18.6.
/// </para>
/// <para>
/// <b>The shapes Postgres writes</b> (measured 2026-10-27 on 14.24 and 18.6, identical): a non-negative <c>integer</c> bound
/// bare (<c>(1000)</c>), a negative one and every <c>smallint</c> and <c>bigint</c> one quoted (<c>('-1000')</c>,
/// <c>('32767')</c>); <c>MINVALUE</c> and <c>MAXVALUE</c> bare; a time bound quoted in the session's <c>DateStyle</c> and
/// <c>TimeZone</c> - under ISO, <c>'2026-10-01'</c>, <c>'2026-10-01 00:00:00'</c>, <c>'2026-10-01 03:00:00+03'</c>,
/// <c>'2026-10-01 05:30:00+05:30'</c>, fractions to the microsecond, <c>'infinity'</c>, and <c>' BC'</c> after a date before
/// the common era. A list's values are quoted with every quote doubled (<c>('o''brien', 'two words')</c>).
/// </para>
/// <para>
/// <b>ISO only.</b> Under <c>SQL</c>, <c>Postgres</c> or <c>German</c> output a <c>timestamptz</c> bound carries a zone
/// abbreviation rather than an offset (<c>'01/10/2026 00:00:00 UTC'</c>), which is not an instant on its own, so a time bound
/// is read only under an ISO <c>DateStyle</c> - Postgres' default, what <c>initdb</c> sets, and what the health runner pins
/// for its own transactions whatever the server, database or role sets (<see cref="HealthSessionQueries" />, INT-W1) - and is
/// otherwise not judged. Integer bounds do not depend on it.
/// </para>
/// </remarks>
internal static partial class PartitionBoundText
{
    private const string RangePrefix = "FOR VALUES FROM (";
    private const string RangeSeparator = ") TO (";
    private const string ListPrefix = "FOR VALUES IN (";

    /// <summary>The text of a <c>DEFAULT</c> partition's bound.</summary>
    public const string Default = "DEFAULT";

    /// <summary>The integer key types, as <c>format_type</c> spells them.</summary>
    public static bool IsIntegerType(string? keyType) => keyType is "smallint" or "integer" or "bigint";

    /// <summary>The time key types, as <c>format_type</c> spells them.</summary>
    public static bool IsTimeType(string? keyType) => keyType is "date" or "timestamp without time zone" or "timestamp with time zone";

    /// <summary>
    /// Whether a <c>DateStyle</c> writes ISO output - <c>ISO, MDY</c>, <c>ISO, DMY</c> - which is all a time bound is read in. A
    /// <see langword="null" /> reads as ISO, Postgres' default.
    /// </summary>
    public static bool IsIsoDateStyle(string? dateStyle) =>
        dateStyle is null || dateStyle.TrimStart().StartsWith("ISO", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A single-column range bound, <c>FOR VALUES FROM (lower) TO (upper)</c>, with each end read for <paramref name="keyType" />
    /// - or <see langword="null" /> for any other shape: <c>DEFAULT</c>, a list, a hash, a bound of several columns.
    /// </summary>
    /// <param name="bound">What <c>pg_get_expr(relpartbound, 0)</c> returned.</param>
    /// <param name="keyType">The key column's type, as <c>format_type</c> spells it.</param>
    /// <param name="dateStyle">The session's <c>DateStyle</c>, which a time bound was written in.</param>
    public static RangeBound? Range(string? bound, string? keyType, string? dateStyle)
    {
        if (bound is null || !bound.StartsWith(RangePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int at = RangePrefix.Length;

        if (Literal(bound, ref at) is not { } lower || !bound.AsSpan(at).StartsWith(RangeSeparator, StringComparison.Ordinal))
        {
            return null;
        }

        at += RangeSeparator.Length;

        if (Literal(bound, ref at) is not { } upper || at != bound.Length - 1 || bound[at] != ')')
        {
            return null;
        }

        return new RangeBound(End(lower, keyType, dateStyle), End(upper, keyType, dateStyle));
    }

    /// <summary>
    /// The one value of a list bound, <c>FOR VALUES IN ('acme')</c>, unquoted - or <see langword="null" /> for a bound of several
    /// values, of <c>NULL</c>, or of another shape.
    /// </summary>
    /// <param name="bound">What <c>pg_get_expr(relpartbound, 0)</c> returned.</param>
    public static string? SingleListValue(string? bound)
    {
        if (bound is null || !bound.StartsWith(ListPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int at = ListPrefix.Length;

        if (Literal(bound, ref at) is not { } value || at != bound.Length - 1 || bound[at] != ')')
        {
            return null;
        }

        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? Unquote(value)
            : string.Equals(value, "NULL", StringComparison.Ordinal) ? null : value;
    }

    /// <summary>
    /// One end of a range bound, as Postgres wrote it (<c>MAXVALUE</c>, <c>1000</c>, <c>'-1000'</c>, <c>'2026-10-01
    /// 00:00:00+00'</c>), read for <paramref name="keyType" />.
    /// </summary>
    /// <param name="literal">The literal, quotes and all.</param>
    /// <param name="keyType">The key column's type, as <c>format_type</c> spells it.</param>
    /// <param name="dateStyle">The session's <c>DateStyle</c>.</param>
    public static RangeEnd End(string literal, string? keyType, string? dateStyle)
    {
        ArgumentNullException.ThrowIfNull(literal);

        switch (literal)
        {
            case "MINVALUE":
                return RangeEnd.Min;
            case "MAXVALUE":
                return RangeEnd.Max;
        }

        string text = literal.Length >= 2 && literal[0] == '\'' && literal[^1] == '\'' ? Unquote(literal) : literal;

        if (IsIntegerType(keyType))
        {
            return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
                ? RangeEnd.Of(number)
                : RangeEnd.Unread;
        }

        if (!IsTimeType(keyType))
        {
            return RangeEnd.Unread;
        }

        switch (text)
        {
            case "infinity":
                return RangeEnd.Max;
            case "-infinity":
                return RangeEnd.Min;
        }

        if (!IsIsoDateStyle(dateStyle))
        {
            return RangeEnd.Unread;
        }

        return (keyType switch
        {
            "date" => Date(text),
            "timestamp without time zone" => Timestamp(text),
            _ => TimestampWithZone(text),
        }) is { } seconds
            ? RangeEnd.Of(seconds)
            : RangeEnd.Unread;
    }

    /// <summary>
    /// Rolls a range-partitioned table's partitions into what T20 judges: the newest upper end and its partition's lower end,
    /// whether one partition is unbounded above, and where the rows stop - never a partition's name.
    /// </summary>
    /// <param name="bounds">Each partition's bound text, the <c>DEFAULT</c> partition's left out.</param>
    /// <param name="rows">Each partition's estimated rows, in the same order (its sub-partitions' summed).</param>
    /// <param name="keyType">The key column's type.</param>
    /// <param name="dateStyle">The session's <c>DateStyle</c>.</param>
    public static RangeSummary Summarise(IReadOnlyList<string> bounds, IReadOnlyList<double> rows, string? keyType, string? dateStyle)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(rows);

        int bounded = 0;
        int unread = 0;
        bool unboundedAbove = false;
        double? newestUpper = null;
        double? newestLower = null;
        double? populated = null;
        List<double> uppers = [];

        for (int i = 0; i < bounds.Count; i++)
        {
            if (Range(bounds[i], keyType, dateStyle) is not { IsRead: true } bound)
            {
                unread++;
                continue;
            }

            if (bound.Upper.Kind == RangeEndKind.MaxValue)
            {
                unboundedAbove = true;
                continue;
            }

            if (bound.Upper.Kind != RangeEndKind.Value)
            {
                continue;
            }

            double upper = bound.Upper.Value;
            bounded++;
            uppers.Add(upper);

            if (newestUpper is not { } newest || upper > newest)
            {
                newestUpper = upper;
                newestLower = bound.Lower.Kind == RangeEndKind.Value ? bound.Lower.Value : null;
            }

            if (i < rows.Count && rows[i] > 0 && (populated is not { } highest || upper > highest))
            {
                populated = upper;
            }
        }

        int emptyAbove = populated is { } frontier ? uppers.Count(x => x > frontier) : 0;
        return new RangeSummary(bounded, newestUpper, newestLower, unboundedAbove, populated, emptyAbove, unread);
    }

    /// <summary>
    /// Reads one literal at <paramref name="at" />: a quoted one through its closing quote (a doubled quote is part of it), or
    /// a bare word up to the next <c>,</c> or <c>)</c>. <see langword="null" /> for nothing, an unclosed quote, or a literal
    /// followed by a comma - a bound of several columns or values.
    /// </summary>
    private static string? Literal(string text, ref int at)
    {
        int start = at;

        if (at < text.Length && text[at] == '\'')
        {
            at++;

            while (at < text.Length)
            {
                if (text[at] == '\'')
                {
                    if (at + 1 < text.Length && text[at + 1] == '\'')
                    {
                        at += 2;
                        continue;
                    }

                    at++;
                    break;
                }

                at++;
            }

            if (at > text.Length || text[at - 1] != '\'' || at - start < 2)
            {
                return null;
            }
        }
        else
        {
            while (at < text.Length && text[at] is not (',' or ')'))
            {
                at++;
            }
        }

        if (at == start || at >= text.Length || text[at] == ',')
        {
            return null;
        }

        return text[start..at];
    }

    /// <summary>A quoted literal's text, its outer quotes dropped and each doubled quote made one.</summary>
    private static string Unquote(string literal)
    {
        StringBuilder text = new(literal.Length);

        for (int i = 1; i < literal.Length - 1; i++)
        {
            text.Append(literal[i]);

            if (literal[i] == '\'' && i + 1 < literal.Length - 1 && literal[i + 1] == '\'')
            {
                i++;
            }
        }

        return text.ToString();
    }

    /// <summary>An ISO date, <c>2026-10-01</c>, as the Unix seconds of its midnight, UTC.</summary>
    private static double? Date(string text) =>
        IsoDate().Match(text) is { Success: true } match && Moment(match, withTime: false) is { } at
            ? Seconds(at)
            : null;

    /// <summary>An ISO timestamp, <c>2026-11-01 06:30:15.123456</c>, read as UTC.</summary>
    private static double? Timestamp(string text) =>
        IsoTimestamp().Match(text) is { Success: true } match && Moment(match, withTime: true) is { } at
            ? Seconds(at)
            : null;

    /// <summary>An ISO timestamp with its offset, <c>2026-10-01 05:30:00+05:30</c>, as the instant it names.</summary>
    private static double? TimestampWithZone(string text)
    {
        if (IsoTimestampWithZone().Match(text) is not { Success: true } match || Moment(match, withTime: true) is not { } at)
        {
            return null;
        }

        int hours = int.Parse(match.Groups["oh"].ValueSpan, CultureInfo.InvariantCulture);
        int minutes = match.Groups["om"].Success ? int.Parse(match.Groups["om"].ValueSpan, CultureInfo.InvariantCulture) : 0;
        int seconds = match.Groups["os"].Success ? int.Parse(match.Groups["os"].ValueSpan, CultureInfo.InvariantCulture) : 0;
        double offset = (hours * 3600) + (minutes * 60) + seconds;

        return Seconds(at) - (match.Groups["sign"].Value == "-" ? -offset : offset);
    }

    /// <summary>The wall-clock moment a match names, or <see langword="null" /> for one <see cref="DateTime" /> cannot hold.</summary>
    private static DateTime? Moment(Match match, bool withTime)
    {
        int year = int.Parse(match.Groups["y"].ValueSpan, CultureInfo.InvariantCulture);
        int month = int.Parse(match.Groups["mo"].ValueSpan, CultureInfo.InvariantCulture);
        int day = int.Parse(match.Groups["d"].ValueSpan, CultureInfo.InvariantCulture);

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        if (!withTime)
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }

        int hour = int.Parse(match.Groups["h"].ValueSpan, CultureInfo.InvariantCulture);
        int minute = int.Parse(match.Groups["mi"].ValueSpan, CultureInfo.InvariantCulture);
        int second = int.Parse(match.Groups["s"].ValueSpan, CultureInfo.InvariantCulture);

        // 24:00:00 is a time Postgres accepts and writes back as the next midnight, so it never reaches here; 60 seconds is not.
        if (hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        long ticks = 0;

        if (match.Groups["f"].Success)
        {
            string fraction = match.Groups["f"].Value.PadRight(7, '0');
            ticks = long.Parse(fraction, CultureInfo.InvariantCulture);
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(ticks);
    }

    private static double Seconds(DateTime utc) => (utc - DateTime.UnixEpoch).Ticks / (double) TimeSpan.TicksPerSecond;

    [GeneratedRegex(@"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2}) (?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})(?:\.(?<f>\d{1,6}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestamp();

    [GeneratedRegex(
        @"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2}) (?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})(?:\.(?<f>\d{1,6}))?(?<sign>[+-])(?<oh>\d{2})(?::(?<om>\d{2})(?::(?<os>\d{2}))?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoTimestampWithZone();
}
