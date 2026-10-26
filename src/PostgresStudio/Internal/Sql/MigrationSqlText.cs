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

    /// <summary>
    /// <c>-- "covering" already does this index's whole job:</c> and <c>DROP INDEX CONCURRENTLY schema.index;</c> - or
    /// <see langword="null" /> when any of the three names cannot be printed honestly. The covering index is named in a
    /// comment, quoted like every other name: a name that could end the comment line (a line feed, a carriage return, a
    /// Unicode line or paragraph separator) would otherwise plant a statement of its own under it.
    /// </summary>
    /// <param name="schema">The indexes' schema, which is their table's.</param>
    /// <param name="index">The redundant index.</param>
    /// <param name="coveredBy">The index that already does its job.</param>
    public static string? DropRedundantIndexConcurrently(string schema, string index, string coveredBy) =>
        IsQuotable(coveredBy) && DropIndexConcurrently(schema, index) is { } drop
            ? "-- " + SqlIdentifier.Quote(coveredBy) + " already does this index's whole job:\n" + drop
            : null;

    /// <summary>
    /// Whether <paramref name="name" /> may be printed inside text a person copies - a remedy's comment or line, the
    /// database browser's Copy SELECT, Copy WHERE and row statements (<see cref="SqlLiteralText" />) - as it is: the names
    /// this builder quotes (<see cref="IsQuotable" />), the same rule a studio's configuration-line remedies hold a catalog
    /// name to before naming it.
    /// </summary>
    /// <param name="name">A schema, table or index name, as the catalog has it.</param>
    public static bool IsPrintableName(string? name) => IsQuotable(name);

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

    /// <summary>
    /// <c>ALTER TABLE schema.table RESET (toast.parameter);</c> - the table's TOAST table's storage parameter back to the
    /// server's setting (W5-V1, T24) - or <see langword="null" />.
    /// </summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="parameter">A storage parameter, without its <c>toast.</c> prefix.</param>
    public static string? ResetToastStorageParameter(string schema, string table, string parameter) =>
        Qualified(schema, table) is { } qualified && IsParameterName(parameter)
            ? "ALTER TABLE " + qualified + " RESET (toast." + parameter + ");"
            : null;

    // ---- W5-B1 --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>CREATE INDEX CONCURRENTLY ON schema.table (a, b);</c> - an index whose leading columns are a foreign key's (T15) - or
    /// <see langword="null" /> when a name cannot be printed honestly. Postgres names it.
    /// </summary>
    /// <param name="schema">The referencing table's schema.</param>
    /// <param name="table">The referencing table.</param>
    /// <param name="columns">The foreign key's columns, in key order.</param>
    public static string? CreateIndexConcurrently(string schema, string table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        return columns.Count > 0 && columns.All(IsQuotable) && Qualified(schema, table) is { } qualified
            ? "CREATE INDEX CONCURRENTLY ON " + qualified + " (" + string.Join(", ", columns.Select(SqlIdentifier.Quote)) + ");"
            : null;
    }

    /// <summary>
    /// <c>CREATE INDEX ON schema.table (a, b);</c> - for a partitioned table, where <c>CONCURRENTLY</c> is not allowed (T15) -
    /// or <see langword="null" />.
    /// </summary>
    /// <param name="schema">The referencing table's schema.</param>
    /// <param name="table">The referencing table.</param>
    /// <param name="columns">The foreign key's columns, in key order.</param>
    public static string? CreateIndexOnPartitionedTable(string schema, string table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        return columns.Count > 0 && columns.All(IsQuotable) && Qualified(schema, table) is { } qualified
            ? "CREATE INDEX ON " + qualified + " (" + string.Join(", ", columns.Select(SqlIdentifier.Quote)) + ");"
            : null;
    }

    /// <summary><c>ALTER TABLE schema.table ALTER COLUMN column TYPE bigint;</c> (T26), or <see langword="null" />.</summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">The integer column.</param>
    public static string? WidenColumnToBigint(string schema, string table, string column) =>
        IsQuotable(column) && Qualified(schema, table) is { } qualified
            ? "ALTER TABLE " + qualified + " ALTER COLUMN " + SqlIdentifier.Quote(column) + " TYPE bigint;"
            : null;

    /// <summary><c>ALTER SEQUENCE schema.sequence AS bigint;</c> (T26), or <see langword="null" />.</summary>
    /// <param name="schema">The sequence's schema.</param>
    /// <param name="sequence">The sequence.</param>
    public static string? WidenSequenceToBigint(string schema, string sequence) =>
        Qualified(schema, sequence) is { } qualified ? "ALTER SEQUENCE " + qualified + " AS bigint;" : null;

    /// <summary>
    /// <c>ALTER SEQUENCE schema.sequence [AS bigint] NO MAXVALUE;</c> (T26), or <see langword="null" />: an explicit
    /// <c>MAXVALUE</c> survives <c>AS bigint</c>, which raises only a maximum that was the old type's own (W5-B1 review F8).
    /// </summary>
    /// <param name="schema">The sequence's schema.</param>
    /// <param name="sequence">The sequence.</param>
    /// <param name="widen">Whether to widen it to <c>bigint</c> in the same statement.</param>
    public static string? SequenceNoMaxValue(string schema, string sequence, bool widen) =>
        Qualified(schema, sequence) is { } qualified ? "ALTER SEQUENCE " + qualified + (widen ? " AS bigint" : string.Empty) + " NO MAXVALUE;" : null;

    /// <summary>
    /// <c>ALTER TABLE schema.table ALTER COLUMN column SET NO MAXVALUE;</c> (T26): an identity column's sequence, through its
    /// column, back to its type's maximum - or <see langword="null" />.
    /// </summary>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="column">The identity column.</param>
    public static string? IdentityNoMaxValue(string schema, string table, string column) =>
        IsQuotable(column) && Qualified(schema, table) is { } qualified
            ? "ALTER TABLE " + qualified + " ALTER COLUMN " + SqlIdentifier.Quote(column) + " SET NO MAXVALUE;"
            : null;

    /// <summary>
    /// <c>CREATE TABLE schema.partition PARTITION OF schema.table FOR VALUES FROM (from) TO (to);</c> (T20), the bounds
    /// literals this builder formats (<see cref="RangeBoundLiteral" />) - or <see langword="null" /> when a name cannot be
    /// printed honestly, is longer than Postgres keeps (63 bytes, which it would cut without a word), or a bound cannot be
    /// written for the key's type.
    /// </summary>
    /// <param name="schema">The partitioned table's schema, which the new partition goes in.</param>
    /// <param name="table">The partitioned table.</param>
    /// <param name="partition">The new partition's name.</param>
    /// <param name="keyType">The partition key's type, as <c>format_type</c> spells it.</param>
    /// <param name="from">The lower bound: Unix seconds for a time key, the number for an integer one.</param>
    /// <param name="to">The upper bound, likewise.</param>
    public static string? CreateRangePartition(string schema, string table, string partition, string keyType, double from, double to)
    {
        if (!IsQuotable(partition)
            || System.Text.Encoding.UTF8.GetByteCount(partition) > 63
            || Qualified(schema, table) is not { } parent
            || Qualified(schema, partition) is not { } child
            || RangeBoundLiteral(keyType, from) is not { } lower
            || RangeBoundLiteral(keyType, to) is not { } upper)
        {
            return null;
        }

        return "CREATE TABLE " + child + " PARTITION OF " + parent + " FOR VALUES FROM (" + lower + ") TO (" + upper + ");";
    }

    /// <summary>
    /// A range bound as SQL spells it for <paramref name="keyType" />: <c>'2026-12-01'</c> for a <c>date</c>,
    /// <c>'2026-12-01 00:00:00'</c> for a <c>timestamp</c> (the value read as UTC), <c>'2026-12-01 00:00:00+00'</c> for a
    /// <c>timestamptz</c>, the whole number for an integer type - or <see langword="null" /> for any other type or a value
    /// that is not finite or not whole.
    /// </summary>
    /// <param name="keyType">The key's type, as <c>format_type</c> spells it.</param>
    /// <param name="value">Unix seconds for a time key, the number for an integer one.</param>
    public static string? RangeBoundLiteral(string keyType, double value)
    {
        if (!double.IsFinite(value))
        {
            return null;
        }

        switch (keyType)
        {
            case "smallint" or "integer" or "bigint":
                return Math.Floor(value) == value && value is >= long.MinValue and <= long.MaxValue
                    ? ((long) value).ToString(CultureInfo.InvariantCulture)
                    : null;

            case "date" or "timestamp without time zone" or "timestamp with time zone":
                if (value < -62_135_596_800d || value > 253_402_300_799d)
                {
                    return null;
                }

                DateTime at = DateTimeOffset.FromUnixTimeMilliseconds((long) Math.Round(value * 1000d)).UtcDateTime;
                return keyType switch
                {
                    "date" => "'" + at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
                    "timestamp without time zone" => "'" + at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
                    _ => "'" + at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00'",
                };

            default:
                return null;
        }
    }

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
    /// The names <see cref="SqlIdentifier.Quote" /> accepts, asked without letting it throw - less any with a control, a
    /// format, a line-separator or a paragraph-separator character in it. Those quote safely, but a person reads this
    /// text before running it, and a right-to-left override, a zero-width character or a line break (U+2028 and U+2029
    /// included) in a name makes the text on the screen say something other than the statement it is (Trojan Source,
    /// CVE-2021-42574) - or, in a comment, ends the comment: such a name gets no statement either.
    /// </summary>
    private static bool IsQuotable(string? identifier) =>
        !string.IsNullOrWhiteSpace(identifier)
        && !identifier.Contains('"', StringComparison.Ordinal)
        && SqlLiteralText.IsPrintableText(identifier);

    // ASCII only, and \z rather than $: a .NET \d is any Unicode digit, and $ matches before a trailing newline.
    [GeneratedRegex(@"\A[a-z][a-z_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();

    [GeneratedRegex(@"\A(?:[0-9]{1,10}(?:\.[0-9]{1,6})?|true|false|on|off)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterValue();
}
