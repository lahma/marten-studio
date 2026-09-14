namespace MartenStudio.SampleDomain.Generation;

/// <summary>
/// The sample's own two-line version of the studio's SQL identifier builder.
/// </summary>
/// <remarks>
/// <para>
/// AGENTS.md hard rule 4 - no raw SQL outside <c>src/MartenStudio/Internal/Sql/</c> - is about the
/// shipped library, and this is the demo host's own domain project, which cannot reference the studio at
/// all (D17). The generator needs three statements Marten has no API for: <c>analyze</c> to make
/// <c>pg_class.reltuples</c> true after a bulk load, a <c>jsonb</c> concatenation to give a document a
/// property its CLR type does not have, and a read of <c>mt_event_progression</c>.
/// </para>
/// <para>
/// The rule behind the rule still applies: identifiers are quoted here and values are always
/// parameters. Nothing in this file ever sees user input - every name it quotes is a constant or comes
/// from Marten's own schema configuration - but it quotes anyway, because a helper that only works on
/// trusted input is a helper somebody will reuse on untrusted input.
/// </para>
/// </remarks>
public static class DemoDataSql
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
