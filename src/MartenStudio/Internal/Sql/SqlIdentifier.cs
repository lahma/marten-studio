namespace MartenStudio.Internal.Sql;

/// <summary>
/// Quotes Postgres identifiers. Every schema, table and column name the studio puts into SQL goes
/// through here; values never do — they are parameters.
/// </summary>
internal static class SqlIdentifier
{
    /// <summary>
    /// <paramref name="identifier" /> as a double-quoted Postgres identifier.
    /// </summary>
    /// <remarks>
    /// A quote or a NUL is <em>refused</em> rather than escaped, and that is the whole design. Postgres
    /// does define an escape - a doubled <c>""</c> inside the quotes - so escaping would "work", and it
    /// is exactly the choice that turns this file into the one place a bug becomes an injection: an
    /// escaper has to be right every time, and a reviewer has to convince themselves it is. Nothing the
    /// studio legitimately quotes can contain either character. Every identifier here came out of Marten's
    /// own mapping or out of <c>information_schema</c>, so one that does is not a name with an awkward
    /// character in it - it is a sign that something reached this method from a place it should never have
    /// reached it from, and the right answer to that is to stop.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="identifier" /> is empty, whitespace, or contains <c>"</c> or a NUL.
    /// </exception>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Contains('"') || identifier.Contains('\0'))
        {
            throw new ArgumentException($"'{identifier}' is not a valid identifier.", nameof(identifier));
        }

        return "\"" + identifier + "\"";
    }

    public static string Qualify(string schema, string name) => Quote(schema) + "." + Quote(name);
}
