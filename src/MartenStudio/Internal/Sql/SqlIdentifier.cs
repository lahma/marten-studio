namespace MartenStudio.Internal.Sql;

/// <summary>
/// Quotes Postgres identifiers. Every schema, table and column name the studio puts into SQL goes
/// through here; values never do — they are parameters.
/// </summary>
internal static class SqlIdentifier
{
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
