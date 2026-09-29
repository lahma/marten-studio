using System.Globalization;
using System.Text;

namespace MartenStudio.Internal.Sql;

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
/// <see cref="DatabaseCatalogQueries.IsQuotable" /> said they can: a name the builder refuses has no statement
/// to write, and every method here answers <see langword="null" /> rather than throwing for one.
/// </para>
/// <para>
/// <b>Values are literals, not parameters</b>, because a clipboard has nowhere to put a parameter. A literal
/// is single-quoted with every embedded quote doubled - the SQL standard's escape, and the only one
/// Postgres honours in a plain <c>'…'</c> string under <c>standard_conforming_strings</c>, which has been the
/// default since 9.1; a backslash is an ordinary character there. It is left untyped, so Postgres reads it
/// with the column's own input function exactly as it reads the grid's parameters. None of this text is ever
/// sent to the database by the studio: the console that may run it runs it read-only, under its own gate
/// (D13).
/// </para>
/// </remarks>
internal static class SqlLiteralText
{
    /// <summary>How many rows the <c>SELECT</c> that "Open in Query" writes into the console asks for.</summary>
    public const int ConsoleRowLimit = 100;

    /// <summary>
    /// <c>select * from "schema"."name" limit 100</c> - or <see langword="null" /> when a name cannot be quoted.
    /// </summary>
    /// <param name="schema">The relation's schema, as the catalog spells it.</param>
    /// <param name="name">The relation's name, as the catalog spells it.</param>
    /// <param name="limit">The row limit written into it.</param>
    public static string? SelectStatement(string schema, string name, int limit = ConsoleRowLimit)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name)
            || !DatabaseCatalogQueries.IsQuotable(schema) || !DatabaseCatalogQueries.IsQuotable(name))
        {
            return null;
        }

        return "select * from " + SqlIdentifier.Qualify(schema, name) + " limit "
            + limit.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A value as a SQL string literal - <c>'O''Brien'</c> - or <c>null</c> for a NULL.
    /// </summary>
    /// <param name="value">The value's text, as Postgres printed it; <see langword="null" /> for SQL NULL.</param>
    public static string Literal(string? value) =>
        value is null ? "null" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// <c>where "a" = 'x' and "b" is null</c> for one row: each column compared with its value, a NULL as
    /// <c>is null</c> - or <see langword="null" /> when there is nothing to compare or a column cannot be quoted.
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

            if (!DatabaseCatalogQueries.IsQuotable(column))
            {
                return null;
            }

            if (i > 0)
            {
                builder.Append("\n  and ");
            }

            builder.Append(SqlIdentifier.Quote(column))
                .Append(value is null ? " is null" : " = " + Literal(value));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The whole statement for one row - <c>select * from "s"."t" where … </c> - or <see langword="null" />
    /// when a name cannot be quoted or there is no key.
    /// </summary>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="key">The row key's columns and values, in key order.</param>
    public static string? RowStatement(string schema, string name, IReadOnlyList<KeyValuePair<string, string?>> key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name)
            || !DatabaseCatalogQueries.IsQuotable(schema) || !DatabaseCatalogQueries.IsQuotable(name)
            || WhereClause(key) is not { } where)
        {
            return null;
        }

        return "select * from " + SqlIdentifier.Qualify(schema, name) + "\n" + where;
    }
}
