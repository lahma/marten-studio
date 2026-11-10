using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace KStudio.Core.Logging;

/// <summary>
/// A value a visitor could have typed, made safe to write into the application's log: every control, format and line or paragraph
/// separator character escaped, a backslash doubled, and the whole bounded (decision 11, F9).
/// </summary>
/// <remarks>
/// A verbatim port of <c>Cap</c> and <c>CapText</c> of the diagnostics engine's <c>StudioLogValue</c> (not its <c>CapStatement</c>):
/// the engine must not reference the diagnostics assembly, so the code is copied and <c>KafkaLogValueParityTests</c> holds the two
/// equal on one table of inputs. Escaped, never dropped, so an operator sees that a value had a line break in it, as <c>\n</c>.
/// </remarks>
internal static class KafkaLogValue
{
    /// <summary>The most characters a logged name keeps - a cluster key, a topic, a group.</summary>
    public const int MaxLength = 256;

    /// <summary>The most characters a logged sentence keeps - librdkafka's own text.</summary>
    public const int MaxTextLength = 4_000;

    /// <summary><paramref name="value" />, escaped and bounded at <see cref="MaxLength" />; <see langword="null" /> stays so.</summary>
    /// <param name="value">A name.</param>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Cap(string? value) => Bounded(value, MaxLength);

    /// <summary><paramref name="text" />, escaped and bounded at <see cref="MaxTextLength" />; <see langword="null" /> stays so.</summary>
    /// <param name="text">A sentence.</param>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? CapText(string? text) => Bounded(text, MaxTextLength);

    private static bool IsEscaped(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator;

    private static bool NeedsEscape(char c) => c == '\\' || IsEscaped(c);

    private static (string Piece, int Width) Piece(string value, int i)
    {
        char c = value[i];

        if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
        {
            return (value.Substring(i, 2), 2);
        }

        if (!NeedsEscape(c))
        {
            return (c.ToString(), 1);
        }

        string escaped = c switch
        {
            '\\' => "\\\\",
            '\r' => "\\r",
            '\n' => "\\n",
            '\t' => "\\t",
            _ => "\\u" + ((int) c).ToString("X4", CultureInfo.InvariantCulture),
        };

        return (escaped, 1);
    }

    private static (string Escaped, int End) Forward(string value, int start, int budget)
    {
        StringBuilder safe = new(Math.Min(value.Length - start, budget) + 16);
        int i = start;

        while (i < value.Length)
        {
            (string piece, int width) = Piece(value, i);

            if (safe.Length + piece.Length > budget)
            {
                break;
            }

            safe.Append(piece);
            i += width;
        }

        return (safe.ToString(), i);
    }

    private static string? Bounded(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length <= max && !value.Any(NeedsEscape))
        {
            return value;
        }

        (string kept, int end) = Forward(value, 0, max);

        return end == value.Length
            ? kept
            : kept + "… (" + value.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters)";
    }
}
