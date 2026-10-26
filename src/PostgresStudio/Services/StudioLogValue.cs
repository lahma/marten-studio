using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace PostgresStudio.Services;

/// <summary>
/// A value a visitor could have typed, made safe to write into the application's log (wave 6): every control, format and
/// line or paragraph separator character escaped, and the whole bounded - as the Activity ring's target is
/// (<c>SqlConsoleService.Target</c>), but escaping rather than dropping what it cannot print.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> A refused scope is what the visitor asked for, not what exists: <c>?store=</c> and <c>?db=</c> are the
/// address's, and a refusal (9203, 9205, the 9201 that records it) logs them before anything has resolved them. Uncapped,
/// one request writes a log line of any length; with a line break in it, it writes a second line that reads as the
/// application's own (log forging); with a bidirectional override, a line that does not read as it is. So every audit and
/// refusal event the studio writes - 9200 to 9205, 9235, 9236 and 9245 - takes each value a visitor could have shaped through
/// here, in the per-product dispatchers every call goes through (<c>StudioLog.PostgresStudio.cs</c>).
/// </para>
/// <para>
/// <b>Escaped, never dropped</b>, so an operator reading the line sees that the value had a line break in it, as
/// <c>\n</c>, rather than a value that silently differs from what was sent. A value cut at the bound ends with
/// <c>…</c> and says how long it was.
/// </para>
/// </remarks>
internal static class StudioLogValue
{
    /// <summary>
    /// The most characters a logged name keeps - a user, a store key, a database id, a tenant, a target: past the Activity
    /// ring's target (two hundred characters and an ellipsis), so a target it already cut goes through as it is.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>
    /// The most characters a logged sentence keeps - a statement, a reason, an outcome, a filter: longer, because the
    /// statement is what 9204 and 9205 are about and a refusal's reason is a studio sentence that can run past a name's bound.
    /// </summary>
    public const int MaxTextLength = 4_000;

    /// <summary><paramref name="value" />, escaped and bounded at <see cref="MaxLength" />; <see langword="null" /> stays so.</summary>
    /// <param name="value">A name a visitor could have typed.</param>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Cap(string? value) => Bounded(value, MaxLength);

    /// <summary><paramref name="text" />, escaped and bounded at <see cref="MaxTextLength" />; <see langword="null" /> stays so.</summary>
    /// <param name="text">A statement a visitor typed, or a sentence carrying what they typed.</param>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? CapText(string? text) => Bounded(text, MaxTextLength);

    /// <summary>Whether <paramref name="c" /> is escaped: a control, format or line or paragraph separator character.</summary>
    internal static bool IsEscaped(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator;

    private static string? Bounded(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length <= max && !value.Any(IsEscaped))
        {
            return value;
        }

        StringBuilder safe = new(Math.Min(value.Length, max) + 32);

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            string piece = !IsEscaped(c)
                ? char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? value.Substring(i, 2) : c.ToString()
                : c switch
                {
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    _ => "\\u" + ((int) c).ToString("X4", CultureInfo.InvariantCulture),
                };

            if (safe.Length + piece.Length > max)
            {
                return safe.Append("… (").Append(value.Length.ToString("N0", CultureInfo.InvariantCulture)).Append(" characters)").ToString();
            }

            safe.Append(piece);
            i += piece.Length == 2 && !IsEscaped(c) ? 1 : 0;
        }

        return safe.ToString();
    }
}
