namespace PostgresStudio.SampleData;

/// <summary>
/// The demo data's own two-line version of the studio's SQL identifier builder: a copy of the two members of
/// <c>MartenStudio.SampleDomain.Generation.DemoDataSql</c> the relational seeding needs.
/// </summary>
/// <remarks>
/// <para>
/// A copy rather than a reference, because the generator it comes from is built on Marten and this project
/// references no Marten. Internal, so that it never meets its original in a file that imports both namespaces.
/// </para>
/// <para>
/// AGENTS.md hard rule 4 - no raw SQL outside the studios' <c>Internal/Sql/</c> - is about the shipped libraries,
/// and this project can reference neither of them (D17). The rule behind the rule still applies: identifiers are
/// quoted here and values are always parameters. Every name it quotes is a constant, but it quotes anyway, because a
/// helper that only works on trusted input is a helper somebody will reuse on untrusted input.
/// </para>
/// </remarks>
internal static class DemoDataSql
{
    /// <summary>A quoted, schema-qualified table name.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="table">The table.</param>
    public static string QuoteQualified(string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(table);

        return Quote(schema) + "." + Quote(table);
    }

    /// <summary>One quoted identifier, with embedded quotes doubled.</summary>
    /// <param name="identifier">The identifier.</param>
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
