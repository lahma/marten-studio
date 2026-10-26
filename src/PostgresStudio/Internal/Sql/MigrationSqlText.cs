using System.Globalization;
using System.Text.RegularExpressions;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The migration SQL a remedy for a relational table offers (issue #8, G12): text a person copies into the host's own
/// migrations and runs with more rights than the studio has. The studio never runs any of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Here because it is SQL (AGENTS.md hard rule 4).</b> Every schema, table and index name goes through
/// <see cref="SqlIdentifier.Quote" />, so a name with a space, a capital, a non-ASCII letter or a reserved word in it
/// comes out as Postgres needs it - <c>"order"</c>, <c>"Line Items"</c> - and nothing a name says can end the statement.
/// A name <see cref="SqlIdentifier" /> will not quote - one with a double quote or a NUL in it - gets no statement at
/// all: every builder answers <see langword="null" />, and the remedy then says nothing rather than hand somebody a
/// statement that would not mean what it says. Escaping it would "work", and is the choice that makes this file the
/// one place an unquoted name becomes somebody else's injection.
/// </para>
/// <para>
/// <b>Values are the studio's own.</b> A storage parameter's name and value come from a check, never from a visitor;
/// each is still held to the shape a storage parameter has (<see cref="IsParameterName" />, <see cref="IsParameterValue" />),
/// and anything else answers <see langword="null" />.
/// </para>
/// <para>
/// <b>Concurrently where Postgres allows it.</b> An index is created, dropped and rebuilt <c>CONCURRENTLY</c>, so the
/// statement a person runs from a remedy does not block writes to the table while it works - at the cost of not being
/// allowed inside a transaction block, which the remedy's explanation says.
/// </para>
/// </remarks>
internal static partial class MigrationSqlText
{
    /// <summary>The placeholder a column the studio cannot name is written as: a statement that fails until it is edited.</summary>
    public const string ColumnPlaceholder = "filtered_column";

    /// <summary>
    /// <c>CREATE INDEX CONCURRENTLY ON schema.table (filtered_column);</c> - an index whose column the person fills in,
    /// or <see langword="null" />.
    /// </summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    public static string? CreateIndexConcurrently(string schema, string table) =>
        Qualified(schema, table) is { } qualified
            ? "CREATE INDEX CONCURRENTLY ON " + qualified + " (" + ColumnPlaceholder + ");"
            : null;

    /// <summary><c>DROP INDEX CONCURRENTLY schema.index;</c>, or <see langword="null" />.</summary>
    /// <param name="schema">The index's schema, which is its table's.</param>
    /// <param name="index">The index.</param>
    public static string? DropIndexConcurrently(string schema, string index) =>
        Qualified(schema, index) is { } qualified ? "DROP INDEX CONCURRENTLY " + qualified + ";" : null;

    /// <summary><c>REINDEX INDEX CONCURRENTLY schema.index;</c> (PostgreSQL 12+), or <see langword="null" />.</summary>
    /// <param name="schema">The index's schema, which is its table's.</param>
    /// <param name="index">The index.</param>
    public static string? ReindexIndexConcurrently(string schema, string index) =>
        Qualified(schema, index) is { } qualified ? "REINDEX INDEX CONCURRENTLY " + qualified + ";" : null;

    /// <summary><c>ALTER TABLE schema.table SET (parameter = value);</c>, or <see langword="null" />.</summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="parameter">A storage parameter: <c>fillfactor</c>, <c>autovacuum_vacuum_scale_factor</c>.</param>
    /// <param name="value">Its value as SQL spells it: <c>0.05</c>, <c>80</c>, <c>true</c>.</param>
    public static string? SetStorageParameter(string schema, string table, string parameter, string value) =>
        Qualified(schema, table) is { } qualified && IsParameterName(parameter) && IsParameterValue(value)
            ? "ALTER TABLE " + qualified + " SET (" + parameter + " = " + value + ");"
            : null;

    /// <summary><c>ALTER TABLE schema.table SET (parameter = value);</c> for a whole-number value.</summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="parameter">A storage parameter.</param>
    /// <param name="value">Its value.</param>
    public static string? SetStorageParameter(string schema, string table, string parameter, int value) =>
        SetStorageParameter(schema, table, parameter, value.ToString(CultureInfo.InvariantCulture));

    /// <summary><c>ALTER TABLE schema.table RESET (parameter);</c> - back to the server's setting - or <see langword="null" />.</summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="parameter">A storage parameter.</param>
    public static string? ResetStorageParameter(string schema, string table, string parameter) =>
        Qualified(schema, table) is { } qualified && IsParameterName(parameter)
            ? "ALTER TABLE " + qualified + " RESET (" + parameter + ");"
            : null;

    /// <summary>Whether <paramref name="parameter" /> has a storage parameter's shape: lower-case letters and underscores.</summary>
    public static bool IsParameterName(string? parameter) => parameter is { Length: > 0 } && ParameterName().IsMatch(parameter);

    /// <summary>Whether <paramref name="value" /> has a storage parameter value's shape: a number, or a boolean.</summary>
    public static bool IsParameterValue(string? value) => value is { Length: > 0 } && ParameterValue().IsMatch(value);

    /// <summary><c>"schema"."name"</c>, or <see langword="null" /> for a name <see cref="SqlIdentifier" /> refuses.</summary>
    private static string? Qualified(string schema, string name)
    {
        if (!IsQuotable(schema) || !IsQuotable(name))
        {
            return null;
        }

        return SqlIdentifier.Qualify(schema, name);
    }

    /// <summary>
    /// The names <see cref="SqlIdentifier.Quote" /> accepts, asked without letting it throw - less any with a control or
    /// a format character in it. Those quote safely, but a person reads this text before running it, and a
    /// right-to-left override or a zero-width character in a name makes the line on the screen say something other than
    /// the statement it is (Trojan Source, CVE-2021-42574): such a name gets no statement either.
    /// </summary>
    private static bool IsQuotable(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)
            || identifier.Contains('"', StringComparison.Ordinal)
            || identifier.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in identifier)
        {
            if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return false;
            }
        }

        return true;
    }

    // ASCII only, and \z rather than $: a .NET \d is any Unicode digit, and $ matches before a trailing newline.
    [GeneratedRegex(@"\A[a-z][a-z_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();

    [GeneratedRegex(@"\A(?:[0-9]{1,10}(?:\.[0-9]{1,6})?|true|false|on|off)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterValue();
}
