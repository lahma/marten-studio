using System.Globalization;

namespace DataStudio.Services.Json;

/// <summary>
/// The two text rules the JSON viewer's copy menu takes from SQL's literal syntax: whether a member name may be printed as itself, and a value as a
/// string literal. A shell-side copy of <c>SqlLiteralText.IsPrintableText</c> and <c>SqlLiteralText.Literal</c> of the diagnostics engine, which this
/// assembly cannot reference (it references no database code); a parity test in <c>DataStudio.Postgres.Tests</c> holds the two to the same answers.
/// </summary>
/// <remarks>
/// <para>
/// The text is only ever printed for a person to read, copy or paste, and never sent to a database by the studio. It means the same under either
/// <c>standard_conforming_strings</c> (<see cref="Literal" />).
/// </para>
/// </remarks>
internal static class JsonLiteralText
{
    /// <summary>
    /// Whether <paramref name="text" /> reads on screen as what it is when printed into text a person copies: no control character (a NUL, a tab or a
    /// line break among them), no format character (a right-to-left override, a zero-width space) and no Unicode line or paragraph separator, any of
    /// which makes the text say something other than the statement it is (Trojan Source, CVE-2021-42574).
    /// </summary>
    /// <param name="text">A name or value about to be printed; <see langword="null" /> is not printable.</param>
    public static bool IsPrintableText(string? text)
    {
        if (text is null)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A value as a SQL string literal that means the same under either <c>standard_conforming_strings</c> - or <c>null</c> for a NULL, and
    /// <see langword="null" /> for a value no literal can carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The plain form for a value without a backslash</b> - <c>'O''Brien'</c>, every embedded quote doubled, the SQL standard's escape.
    /// </para>
    /// <para>
    /// <b>An escape string for a value with one</b> - <c>E'a\\b'</c>, quotes and backslashes both doubled, which reads the same under both settings.
    /// </para>
    /// <para>
    /// <b>A NUL is refused.</b> No Postgres text value contains one, so there is no literal to write for it.
    /// </para>
    /// </remarks>
    /// <param name="value">The value's text; <see langword="null" /> for SQL NULL.</param>
    public static string? Literal(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        string quoted = value.Replace("'", "''", StringComparison.Ordinal);

        return value.Contains('\\', StringComparison.Ordinal)
            ? "E'" + quoted.Replace("\\", "\\\\", StringComparison.Ordinal) + "'"
            : "'" + quoted + "'";
    }
}
