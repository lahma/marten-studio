using System.Collections;
using System.Globalization;
using System.Text;

namespace MartenStudio.Internal.Sql;

/// <summary>What a formatted cell is, so the grid can style it without re-inspecting the value.</summary>
internal enum SqlCellKind
{
    /// <summary>SQL <c>NULL</c> — drawn differently from an empty string, which is the point.</summary>
    Null,

    /// <summary>Anything textual.</summary>
    Text,

    /// <summary>Any numeric type.</summary>
    Number,

    /// <summary><c>boolean</c>.</summary>
    Boolean,

    /// <summary>A date, a time or a timestamp.</summary>
    Timestamp,

    /// <summary><c>json</c> or <c>jsonb</c> — the cell the JSON viewer can open.</summary>
    Json,

    /// <summary><c>bytea</c>, shown as <c>\x…</c> and a length.</summary>
    Binary,

    /// <summary>A Postgres array.</summary>
    Array,

    /// <summary><c>uuid</c>.</summary>
    Uuid,
}

/// <summary>One formatted cell of a SQL console result.</summary>
/// <param name="Text">What to show.</param>
/// <param name="Kind">What it is.</param>
/// <param name="IsTruncated">Whether <paramref name="Text"/> is shorter than the value.</param>
/// <param name="FullLength">
/// The value's full length in characters (or bytes, for <see cref="SqlCellKind.Binary"/>), so the UI can
/// say how much it is not showing.
/// </param>
internal sealed record SqlCell(string Text, SqlCellKind Kind, bool IsTruncated, int FullLength)
{
    /// <summary>The rendering of SQL <c>NULL</c>.</summary>
    public static SqlCell Null { get; } = new("NULL", SqlCellKind.Null, false, 0);
}

/// <summary>The caps the formatter applies. Every one of them is a server-side clamp (plan §4.8).</summary>
internal sealed record SqlValueFormatterOptions
{
    /// <summary>The longest a non-JSON cell may be before it is cut.</summary>
    public int MaxCellLength { get; init; } = 1024;

    /// <summary>The longest a <c>json</c>/<c>jsonb</c> cell may be before it is cut. 4 KiB by default.</summary>
    public int MaxJsonLength { get; init; } = 4 * 1024;

    /// <summary>How many bytes of a <c>bytea</c> value are rendered as hex.</summary>
    public int MaxBinaryBytes { get; init; } = 32;

    /// <summary>How many elements of an array are rendered.</summary>
    public int MaxArrayElements { get; init; } = 50;
}

/// <summary>
/// Turns whatever Npgsql hands back into a string the results grid can show, with every cell capped.
/// </summary>
/// <remarks>
/// <para>
/// Two failure modes this exists to prevent. A <c>select *</c> over a table with a 40 MB <c>bytea</c>
/// column would otherwise push 40 MB down a SignalR circuit as base64 to render as mojibake; and a
/// timestamp rendered with the server's culture would read <c>3/4/2026</c> to half the world and
/// <c>4/3/2026</c> to the other half. So: binary becomes <c>\x…</c> plus a byte count, everything is
/// invariant and round-trippable, and every cell has a cap.
/// </para>
/// <para>
/// <c>NULL</c> is a kind of its own rather than an empty string, because on a results grid the difference
/// between "no value" and "the empty string" is frequently the answer to the question being asked.
/// </para>
/// </remarks>
internal sealed class SqlValueFormatter
{
    private static readonly SqlValueFormatterOptions Defaults = new();

    private readonly SqlValueFormatterOptions options;

    /// <summary>Creates a formatter, with the default caps when none are given.</summary>
    public SqlValueFormatter(SqlValueFormatterOptions? formatterOptions = null) =>
        options = formatterOptions ?? Defaults;

    /// <summary>The caps in force.</summary>
    public SqlValueFormatterOptions Options => options;

    /// <summary>
    /// Formats one value. <paramref name="postgresType"/> is the reader's data type name, which is the only
    /// way to know a string came out of a <c>jsonb</c> column rather than a <c>text</c> one.
    /// </summary>
    public SqlCell Format(object? value, string? postgresType = null)
    {
        if (value is null or DBNull)
        {
            return SqlCell.Null;
        }

        switch (value)
        {
            case byte[] bytes:
                return Binary(bytes);

            case string text:
                return IsJson(postgresType)
                    ? Cut(text, SqlCellKind.Json, options.MaxJsonLength)
                    : Cut(text, SqlCellKind.Text, options.MaxCellLength);

            case bool boolean:
                // Lower case, the way Postgres itself prints it, not .NET's "True".
                return Plain(boolean ? "true" : "false", SqlCellKind.Boolean);

            case Guid guid:
                return Plain(guid.ToString("D", CultureInfo.InvariantCulture), SqlCellKind.Uuid);

            case DateTime dateTime:
                // "O" carries the kind: Z for UTC, an offset for local, and nothing for unspecified - which
                // is exactly the distinction a `timestamp` and a `timestamptz` column are about.
                return Plain(dateTime.ToString("O", CultureInfo.InvariantCulture), SqlCellKind.Timestamp);

            case DateTimeOffset dateTimeOffset:
                return Plain(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture), SqlCellKind.Timestamp);

            case DateOnly date:
                return Plain(date.ToString("O", CultureInfo.InvariantCulture), SqlCellKind.Timestamp);

            case TimeOnly time:
                return Plain(time.ToString("O", CultureInfo.InvariantCulture), SqlCellKind.Timestamp);

            case TimeSpan interval:
                return Plain(interval.ToString("c", CultureInfo.InvariantCulture), SqlCellKind.Timestamp);

            case decimal or double or float or byte or sbyte or short or ushort or int or uint or long or ulong:
                return Plain(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, SqlCellKind.Number);

            // Strings and byte arrays are both IEnumerable and are both already handled above.
            case IEnumerable enumerable:
                return ArrayCell(enumerable);

            default:
                return Cut(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    SqlCellKind.Text,
                    options.MaxCellLength);
        }
    }

    private static bool IsJson(string? postgresType) =>
        string.Equals(postgresType, "jsonb", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(postgresType, "json", StringComparison.OrdinalIgnoreCase);

    private static SqlCell Plain(string text, SqlCellKind kind) => new(text, kind, false, text.Length);

    private static SqlCell Cut(string text, SqlCellKind kind, int max)
    {
        return text.Length <= max
            ? new SqlCell(text, kind, false, text.Length)
            : new SqlCell(text[..max] + "…", kind, true, text.Length);
    }

    private SqlCell Binary(byte[] bytes)
    {
        var shown = Math.Min(bytes.Length, options.MaxBinaryBytes);
        var text = new StringBuilder(2 + (shown * 2) + 24);

        text.Append("\\x");

        for (var i = 0; i < shown; i++)
        {
            text.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        var truncated = shown < bytes.Length;

        if (truncated)
        {
            text.Append('…');
        }

        text.Append(" (").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");

        return new SqlCell(text.ToString(), SqlCellKind.Binary, truncated, bytes.Length);
    }

    private SqlCell ArrayCell(IEnumerable values)
    {
        var text = new StringBuilder("{");
        var count = 0;
        var truncated = false;

        foreach (var element in values)
        {
            if (count == options.MaxArrayElements)
            {
                truncated = true;
                break;
            }

            if (count > 0)
            {
                text.Append(',');
            }

            var cell = Format(element);

            text.Append(cell.Kind == SqlCellKind.Null ? "NULL" : cell.Text);
            truncated |= cell.IsTruncated;
            count++;
        }

        if (truncated)
        {
            text.Append(",…");
        }

        text.Append('}');

        var rendered = text.ToString();

        return rendered.Length <= options.MaxCellLength
            ? new SqlCell(rendered, SqlCellKind.Array, truncated, rendered.Length)
            : new SqlCell(rendered[..options.MaxCellLength] + "…", SqlCellKind.Array, true, rendered.Length);
    }
}
