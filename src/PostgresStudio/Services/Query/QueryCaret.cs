namespace PostgresStudio.Services.Query;

/// <summary>
/// 1.0-HARD: the line an error points into, and a caret under the character it points at - cut to a window around the caret when
/// the line is long, so the caret is never off-screen at a narrow width (the POL review measured Mode A's caret at x = 457 in a
/// 234 px excerpt, and the console's at 792). The editor above still holds the whole statement.
/// </summary>
internal static class QueryCaret
{
    /// <summary>The most characters of a line shown around the caret; a shorter line is shown whole.</summary>
    internal const int Width = 48;

    /// <summary>What marks a cut end of the window.</summary>
    internal const string Cut = "…";

    /// <summary>
    /// <paramref name="line" /> - or a window of it, with <see cref="Cut" /> where it was cut - and the caret line under the
    /// character at <paramref name="column" /> (zero-based, clamped to the line: a column at the line's end - an error "at end of
    /// input" - points just after its last character).
    /// </summary>
    public static (string Source, string Line) Window(string line, int column)
    {
        ArgumentNullException.ThrowIfNull(line);

        int at = Math.Clamp(column, 0, line.Length);

        if (line.Length <= Width)
        {
            return (line, new string(' ', at) + "^");
        }

        int start = Math.Clamp(at - (Width / 2), 0, line.Length - Width);
        int end = start + Width;
        string prefix = start > 0 ? Cut : string.Empty;
        string suffix = end < line.Length ? Cut : string.Empty;

        return (prefix + line[start..end] + suffix, new string(' ', prefix.Length + (at - start)) + "^");
    }
}
