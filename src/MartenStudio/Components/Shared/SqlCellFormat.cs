using System.Globalization;
using System.Text;
using System.Text.Unicode;

using MartenStudio.Services.Json;

namespace MartenStudio.Components.Shared;

/// <summary>What the first bytes of a <c>bytea</c> value look like.</summary>
internal enum BinaryShape
{
    /// <summary>UTF-8 text starting with <c>{</c> or <c>[</c> - JSON kept in a byte column, which Quartz does.</summary>
    Json,

    /// <summary>A gzip stream: <c>1f 8b</c>.</summary>
    Gzip,

    /// <summary>A PNG image: its eight-byte signature.</summary>
    Png,

    /// <summary>UTF-8 text with no control characters but tab and line breaks.</summary>
    Text,

    /// <summary>Anything else: shown as hex.</summary>
    Unknown,
}

/// <summary>What <see cref="SqlCellFormat.Sniff" /> made of a byte prefix.</summary>
/// <param name="Shape">What it looks like.</param>
/// <param name="Label">The chip's words: "JSON · 1.2 KB", "gzip · 3.4 KB", "text · 57 B".</param>
/// <param name="Preview">For <see cref="BinaryShape.Text" />, the start of the text; otherwise <see langword="null" />.</param>
internal readonly record struct BinarySniff(BinaryShape Shape, string Label, string? Preview);

/// <summary>What a <c>bigint</c> that holds a time is counting.</summary>
internal enum DateHintUnit
{
    /// <summary>Not a time, or not one the grid can tell.</summary>
    None,

    /// <summary>.NET ticks: 100 ns since 0001-01-01 - what Quartz.NET stores.</summary>
    Ticks,

    /// <summary>Milliseconds since the Unix epoch.</summary>
    EpochMilliseconds,

    /// <summary>Seconds since the Unix epoch.</summary>
    EpochSeconds,
}

/// <summary>
/// How a SQL cell is read by a person: the kind of thing a <c>bytea</c> holds, whether a <c>bigint</c> is a
/// time in disguise, and a timestamp with or without its zone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is a hint drawn beside the value, never instead of it.</b> The raw value stays on
/// screen; a sniffed "JSON · 1.2 KB" or an "≈ 2026-09-29 14:03" says what it looks like, and says it as a
/// guess. That is the difference between a browser that helps and one that lies: a <c>bigint</c> called
/// <c>next_fire_time</c> is almost certainly ticks, and the grid still shows the number.
/// </para>
/// <para>
/// <b>The date hint is page-wide, not per cell.</b> A column is read as ticks, epoch milliseconds or epoch
/// seconds only when its name suggests a time and every non-sentinel value on the page falls in the same one
/// of three windows - so a page that mixes units, or a counter that happens to be the size of a timestamp in
/// one row, gets no hint at all. The windows are roughly the years 1969 (2001 for the epoch units) to 2099 in
/// each unit, and they do not overlap.
/// </para>
/// </remarks>
internal static class SqlCellFormat
{
    /// <summary>The smallest .NET tick count read as a time: late 1968.</summary>
    public const long MinTicks = 621_000_000_000_000_000;

    /// <summary>The largest .NET tick count read as a time: late 2098.</summary>
    public const long MaxTicks = 662_000_000_000_000_000;

    /// <summary>The smallest epoch-milliseconds value read as a time: 2001-01-19.</summary>
    public const long MinEpochMilliseconds = 980_000_000_000;

    /// <summary>The largest epoch-milliseconds value read as a time: 2099-12-03.</summary>
    public const long MaxEpochMilliseconds = 4_100_000_000_000;

    /// <summary>The smallest epoch-seconds value read as a time: 2001-01-20.</summary>
    public const long MinEpochSeconds = 980_000_000;

    /// <summary>The largest epoch-seconds value read as a time: 2099-12-03.</summary>
    public const long MaxEpochSeconds = 4_100_000_000;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    /// <summary>A byte count as the JSON viewer spells one: "57 B", "1.2 KB".</summary>
    public static string Bytes(long bytes) => JsonModelBuilder.FormatBytes(Math.Max(bytes, 0));

    /// <summary>
    /// The bytes a <c>\x…</c> cell shows - the first 64 in a grid, more in row detail - or <see langword="null" />
    /// when the text is not one. What follows the hex (<c>…</c>, <c> (N bytes)</c>) is ignored.
    /// </summary>
    public static byte[]? HexPrefix(string? text)
    {
        if (text is null || !text.StartsWith("\\x", StringComparison.Ordinal))
        {
            return null;
        }

        int end = 2;

        while (end + 1 < text.Length && char.IsAsciiHexDigit(text[end]) && char.IsAsciiHexDigit(text[end + 1]))
        {
            end += 2;
        }

        return Convert.FromHexString(text.AsSpan(2, end - 2));
    }

    /// <summary>What a value that starts with <paramref name="prefix" /> and is <paramref name="fullLength" /> bytes long looks like.</summary>
    public static BinarySniff Sniff(ReadOnlySpan<byte> prefix, long fullLength)
    {
        string size = Bytes(fullLength);

        if (prefix.Length >= 2 && prefix[0] == 0x1f && prefix[1] == 0x8b)
        {
            return new BinarySniff(BinaryShape.Gzip, "gzip · " + size, null);
        }

        if (prefix.Length >= PngSignature.Length && prefix[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return new BinarySniff(BinaryShape.Png, "PNG image · " + size, null);
        }

        if (prefix.Length == 0 || DecodeText(prefix, fullLength > prefix.Length) is not { } text)
        {
            return new BinarySniff(BinaryShape.Unknown, size, null);
        }

        string trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');

        if (trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            return new BinarySniff(BinaryShape.Json, "JSON · " + size, null);
        }

        return new BinarySniff(BinaryShape.Text, "text · " + size, Preview(text));
    }

    /// <summary>
    /// A byte prefix as UTF-8 text, or <see langword="null" /> when it is not text: invalid UTF-8, or a control
    /// character other than tab and a line break. A prefix cut mid-character loses the partial character.
    /// </summary>
    public static string? DecodeText(ReadOnlySpan<byte> bytes, bool cut)
    {
        ReadOnlySpan<byte> candidate = bytes;

        // A prefix cut on the server can end inside a multi-byte character; up to three trailing bytes may
        // belong to the next one.
        for (int drop = 0; drop <= (cut ? 3 : 0) && drop < bytes.Length; drop++)
        {
            candidate = bytes[..(bytes.Length - drop)];

            if (Utf8.IsValid(candidate))
            {
                string text = Encoding.UTF8.GetString(candidate);

                foreach (char c in text)
                {
                    if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
                    {
                        return null;
                    }
                }

                return text;
            }
        }

        return null;
    }

    /// <summary>Whether a column's name suggests it holds a time: <c>*time*</c>, <c>*_at</c>, <c>*date*</c>.</summary>
    public static bool NameSuggestsTime(string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return false;
        }

        return column.Contains("time", StringComparison.OrdinalIgnoreCase)
            || column.Contains("date", StringComparison.OrdinalIgnoreCase)
            || column.EndsWith("_at", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a <c>format_type</c> spelling is <c>bigint</c>.</summary>
    public static bool IsBigint(string? type) =>
        string.Equals(type?.Trim(), "bigint", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type?.Trim(), "int8", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A value that means "no time" rather than a time: zero, a negative (Quartz writes <c>-1</c>), and the
    /// largest values a column can hold.
    /// </summary>
    public static bool IsSentinel(long value) =>
        value <= 0 || value == long.MaxValue || value == DateTime.MaxValue.Ticks;

    /// <summary>Which window one value falls in.</summary>
    public static DateHintUnit UnitOf(long value) => value switch
    {
        >= MinTicks and <= MaxTicks => DateHintUnit.Ticks,
        >= MinEpochMilliseconds and <= MaxEpochMilliseconds => DateHintUnit.EpochMilliseconds,
        >= MinEpochSeconds and <= MaxEpochSeconds => DateHintUnit.EpochSeconds,
        _ => DateHintUnit.None,
    };

    /// <summary>
    /// The unit a whole column of a page is counting, or <see cref="DateHintUnit.None" />: a <c>bigint</c> whose
    /// name suggests a time, at least one value that is not a sentinel, and every such value in one window.
    /// </summary>
    /// <param name="column">The column's name.</param>
    /// <param name="type">Its type, as <c>format_type</c> spells it.</param>
    /// <param name="values">The page's values of it as text; <see langword="null" /> for NULL.</param>
    public static DateHintUnit DateHintFor(string column, string? type, IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!IsBigint(type) || !NameSuggestsTime(column))
        {
            return DateHintUnit.None;
        }

        DateHintUnit unit = DateHintUnit.None;

        foreach (string? text in values)
        {
            if (text is null)
            {
                continue;
            }

            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            {
                return DateHintUnit.None;
            }

            if (IsSentinel(value))
            {
                continue;
            }

            DateHintUnit found = UnitOf(value);

            if (found == DateHintUnit.None || (unit != DateHintUnit.None && found != unit))
            {
                return DateHintUnit.None;
            }

            unit = found;
        }

        return unit;
    }

    /// <summary>The instant a value in <paramref name="unit" /> stands for, or <see langword="null" />.</summary>
    public static DateTimeOffset? Instant(string? text, DateHintUnit unit)
    {
        if (unit == DateHintUnit.None
            || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value)
            || IsSentinel(value)
            || UnitOf(value) != unit)
        {
            return null;
        }

        return unit switch
        {
            DateHintUnit.Ticks => new DateTimeOffset(value, TimeSpan.Zero),
            DateHintUnit.EpochMilliseconds => DateTimeOffset.FromUnixTimeMilliseconds(value),
            _ => DateTimeOffset.FromUnixTimeSeconds(value),
        };
    }

    /// <summary>What a unit is called in the hint's tooltip.</summary>
    public static string UnitLabel(DateHintUnit unit) => unit switch
    {
        DateHintUnit.Ticks => ".NET ticks",
        DateHintUnit.EpochMilliseconds => "milliseconds since 1970",
        DateHintUnit.EpochSeconds => "seconds since 1970",
        _ => "not a time",
    };

    /// <summary>Whether a type is <c>timestamp with time zone</c>.</summary>
    public static bool IsTimestampWithZone(string? type) =>
        string.Equals(type?.Trim(), "timestamp with time zone", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type?.Trim(), "timestamptz", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a type is <c>timestamp without time zone</c>.</summary>
    public static bool IsTimestampWithoutZone(string? type) =>
        string.Equals(type?.Trim(), "timestamp without time zone", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type?.Trim(), "timestamp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A <c>timestamptz</c> cell's instant - the formatter writes it round-trippable, with its <c>Z</c> - or
    /// <see langword="null" /> when the text is not one.
    /// </summary>
    public static DateTimeOffset? ParseInstant(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value
            : null;

    /// <summary>
    /// A <c>timestamp</c> cell as a person reads it - <c>2026-09-29 14:03:00</c> - with no zone applied, because it
    /// has none; or <see langword="null" /> when the text is not one.
    /// </summary>
    public static string? FormatWithoutZone(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime value)
            ? value.ToString(TimeFormat, CultureInfo.InvariantCulture)
            : null;

    /// <summary>The grid's timestamp format: seconds, and a fraction only when there is one.</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    /// <summary>The date hint's format: to the minute, which is what a glance needs.</summary>
    public const string HintFormat = "yyyy-MM-dd HH:mm";

    private static string Preview(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();

        return flat.Length <= 40 ? flat : flat[..40] + "…";
    }
}
