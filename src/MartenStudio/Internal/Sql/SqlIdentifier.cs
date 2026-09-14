namespace MartenStudio.Internal.Sql;

/// <summary>
/// The one place a Postgres identifier becomes SQL text.
/// </summary>
/// <remarks>
/// <para>
/// Every schema, table and column name the studio puts into SQL goes through here, and values never do:
/// values are always parameters (AGENTS.md hard rule 4). Marten only ever hands the studio a runtime
/// <see cref="System.Type" />, so the reads <em>are</em> generated SQL - which means there is exactly one
/// place where an interpolated identifier could become an injection, and this is it.
/// </para>
/// <para>
/// Quoting is the double-quote form, which makes the identifier exact and case-sensitive, the way
/// Marten's own generated SQL writes it. An identifier that already contains a double quote is refused
/// rather than escaped: no name Marten produces contains one, so a name that does is a sign that
/// something other than <c>StoreOptions</c> reached this call, and doubling it would hide that.
/// </para>
/// </remarks>
internal static class SqlIdentifier
{
    /// <summary>
    /// <paramref name="identifier" /> as a quoted Postgres identifier.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The identifier is null, empty or whitespace, or contains a double quote or a NUL - none of which
    /// any name Marten generates can contain.
    /// </exception>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        if (identifier.Contains('"', StringComparison.Ordinal) || identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{identifier}' is not a Postgres identifier Marten could have produced: it contains a quote or a NUL. " +
                "Identifiers reaching the SQL builders come from StoreOptions, never from a request.",
                nameof(identifier));
        }

        return "\"" + identifier + "\"";
    }

    /// <summary>
    /// <c>"schema"."name"</c>, both halves quoted.
    /// </summary>
    public static string Qualify(string schema, string name) => Quote(schema) + "." + Quote(name);
}
