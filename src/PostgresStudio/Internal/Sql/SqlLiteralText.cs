using System.Globalization;
using System.Text;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// SQL text the studio writes for a person to read, paste or run themselves - the SQL console's editor, the
/// clipboard - and never runs on its own: a <c>SELECT</c> of a relation, and a <c>WHERE</c> clause naming one
/// row by its key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it lives here.</b> AGENTS.md hard rule 4 puts every piece of SQL text the studio composes in
/// <c>Internal/Sql</c>, so that the one place an identifier becomes SQL is small enough to review. That holds
/// for text that is only ever a prefill too: a statement that reaches an editor is a statement somebody may
/// press Run on.
/// </para>
/// <para>
/// <b>Identifiers</b> go through <see cref="SqlIdentifier" />, and only after
/// <see cref="MigrationSqlText.IsPrintableName" /> said they may be printed as they are: a name the quoting refuses
/// (a double quote, a NUL), and a name with a line break, a control or format character, or U+2028/U+2029 in it,
/// which would quote safely but make the text on the screen say something other than the statement it is (Trojan
/// Source, CVE-2021-42574). Such a name has no statement to write - never a lookalike - and every method here
/// answers <see langword="null" /> rather than throwing for one.
/// </para>
/// <para>
/// <b>Values are literals, not parameters</b>, because a clipboard has nowhere to put a parameter, and each one
/// means the same whatever the session's <c>standard_conforming_strings</c> says (<see cref="Literal" />). It is
/// left untyped, so Postgres reads it with the column's own input function exactly as it reads the grid's
/// parameters. None of this text is ever sent to the database by the studio: the console that may run it runs it
/// read-only, under its own gate (D13).
/// </para>
/// </remarks>
internal static class SqlLiteralText
{
    /// <summary>How many rows the <c>SELECT</c> that "Open in Query" writes into the console asks for.</summary>
    public const int ConsoleRowLimit = 100;

    /// <summary>
    /// <c>select * from "schema"."name" limit 100</c> - or <see langword="null" /> when a name cannot be printed.
    /// </summary>
    /// <param name="schema">The relation's schema, as the catalog spells it.</param>
    /// <param name="name">The relation's name, as the catalog spells it.</param>
    /// <param name="limit">The row limit written into it.</param>
    public static string? SelectStatement(string schema, string name, int limit = ConsoleRowLimit)
    {
        if (!MigrationSqlText.IsPrintableName(schema) || !MigrationSqlText.IsPrintableName(name))
        {
            return null;
        }

        return "select * from " + SqlIdentifier.Qualify(schema, name) + " limit "
            + limit.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A value as a SQL string literal that means the same under either <c>standard_conforming_strings</c> - or
    /// <c>null</c> for a NULL, and <see langword="null" /> for a value no literal can carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The plain form for a value without a backslash</b> - <c>'O''Brien'</c>, every embedded quote doubled, the SQL
    /// standard's escape - which reads the same under either setting, so ordinary output is unchanged.
    /// </para>
    /// <para>
    /// <b>An escape string for a value with one</b> - <c>E'a\\b'</c>, quotes and backslashes both doubled. In a plain
    /// <c>'…'</c> string a backslash is an ordinary character only while <c>standard_conforming_strings</c> is on (the
    /// default since 9.1); on a server or session that turns it off, <c>'a\b'</c> reads as <c>a</c> and a backspace,
    /// <c>'a\\b'</c> reads as another row's <c>a\b</c>, and a trailing backslash escapes the closing quote. An
    /// <c>E'…'</c> string reads backslashes as escapes under both settings, so the doubled form is exact under both.
    /// </para>
    /// <para>
    /// <b>A NUL is refused.</b> No Postgres text value contains one, so a value that does did not come from the
    /// database, and there is no literal to write for it.
    /// </para>
    /// </remarks>
    /// <param name="value">The value's text, as Postgres printed it; <see langword="null" /> for SQL NULL.</param>
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

    /// <summary>
    /// <c>where "a" = 'x' and "b" is null</c> for one row: each column compared with its value, a NULL as
    /// <c>is null</c> - or <see langword="null" /> when there is nothing to compare, a column cannot be printed or a
    /// value cannot be written.
    /// </summary>
    /// <param name="columns">The columns and their values, in order; a <see langword="null" /> value is SQL NULL.</param>
    public static string? WhereClause(IReadOnlyList<KeyValuePair<string, string?>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder("where ");

        for (int i = 0; i < columns.Count; i++)
        {
            (string column, string? value) = (columns[i].Key, columns[i].Value);

            if (!MigrationSqlText.IsPrintableName(column))
            {
                return null;
            }

            string comparison;

            if (value is null)
            {
                comparison = " is null";
            }
            else if (Literal(value) is { } literal)
            {
                comparison = " = " + literal;
            }
            else
            {
                return null;
            }

            if (i > 0)
            {
                builder.Append("\n  and ");
            }

            builder.Append(SqlIdentifier.Quote(column)).Append(comparison);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The whole statement for one row - <c>select * from "s"."t" where … </c> - or <see langword="null" />
    /// when a name cannot be printed, a value cannot be written or there is no key.
    /// </summary>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="key">The row key's columns and values, in key order.</param>
    public static string? RowStatement(string schema, string name, IReadOnlyList<KeyValuePair<string, string?>> key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!MigrationSqlText.IsPrintableName(schema) || !MigrationSqlText.IsPrintableName(name)
            || WhereClause(key) is not { } where)
        {
            return null;
        }

        return "select * from " + SqlIdentifier.Qualify(schema, name) + "\n" + where;
    }
}
