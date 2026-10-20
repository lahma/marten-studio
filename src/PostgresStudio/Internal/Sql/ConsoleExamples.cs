namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The statements PostgresStudio's SQL console offers before anything has run. They are text for the editor - nothing
/// runs them until somebody presses Run, through the console's own gate (D13) - and they are written here, where every
/// statement the studio composes is, because text that reaches an editor is text somebody may press Run on (AGENTS.md
/// hard rule 4).
/// </summary>
/// <remarks>Catalog reads only, schema-qualified, each one statement the console's read-only guard accepts.</remarks>
internal static class ConsoleExamples
{
    /// <summary>What each one shows, and its text. None is about a document type, so none carries an alias.</summary>
    public static IReadOnlyList<(string Title, string Snippet, string? Alias)> All { get; } =
    [
        (
            "The server and the database",
            "select pg_catalog.version() as server, pg_catalog.current_database() as database, pg_catalog.current_user as role",
            null),
        (
            "The largest tables, by Postgres' estimate",
            "select n.nspname as schema, c.relname as table, c.reltuples::int8 as estimated_rows\n" +
            "  from pg_catalog.pg_class c\n" +
            "  join pg_catalog.pg_namespace n on n.oid = c.relnamespace\n" +
            " where c.relkind in ('r', 'p')\n" +
            "   and n.nspname not in ('pg_catalog', 'information_schema')\n" +
            " order by c.reltuples desc\n" +
            " limit 20",
            null),
        (
            "Schemas and what is in them",
            "select n.nspname as schema, pg_catalog.count(c.oid) as relations\n" +
            "  from pg_catalog.pg_namespace n\n" +
            "  left join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relkind in ('r', 'p', 'v', 'm')\n" +
            " where n.nspname not like 'pg\\_%' and n.nspname <> 'information_schema'\n" +
            " group by n.nspname\n" +
            " order by n.nspname",
            null),
    ];
}
