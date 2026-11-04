using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>One relation the index advisor may imagine an index on, as the catalog describes it.</summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Table">Its name.</param>
/// <param name="Kind"><c>relkind</c>: <c>r</c> for an ordinary table.</param>
/// <param name="IsPartition">Whether it is a partition of a partitioned table (<c>relispartition</c>).</param>
/// <param name="Inserts"><c>n_tup_ins</c>: rows inserted since the counters began, when the statistics view has the relation.</param>
/// <param name="Updates"><c>n_tup_upd</c>: rows updated since the counters began.</param>
internal sealed record IndexAdvisorRelation(string Schema, string Table, char Kind, bool IsPartition, long? Inserts, long? Updates);

/// <summary>
/// 1.2-INDEX-A: the one catalog read of the Statements page's index advisor - which of the tables its candidates name may have an
/// imagined index, and how many writes would have maintained one. Everything else the advisor runs is the drill-in's plan
/// (<see cref="StatementPlanQueries" />) and hypopg's two calls (<see cref="HypotheticalIndexQueries" />).
/// </summary>
/// <remarks>
/// <para>
/// <b>By name through joins, never <c>to_regclass</c></b>, which raises <c>42501</c> for a schema the role has no <c>USAGE</c> on;
/// <c>pg_catalog</c> and one statistics view; no size or deparse function; nothing locked. Both arrays are parameters. It is run once
/// per advice, for the candidates' tables only - all of them read off plans that were admitted, so all of them tables the visitor may
/// see - in a read-only transaction of its own, as <c>SqlConsoleRole</c>.
/// </para>
/// <para>
/// <b>Only an ordinary table.</b> A plan of a partitioned table prints its partitions, so an index read off such a plan names one
/// partition, whose index is declared on the partitioned table: the advisor does not imagine it (hypopg's changelog claims
/// partitioned-table support from 1.1.4, which is unmeasured here, so 1.2 offers neither).
/// </para>
/// </remarks>
internal static class IndexAdvisorQueries
{
    /// <summary>The schemas array's parameter.</summary>
    internal const string SchemasParameter = "schemas";

    /// <summary>The tables array's parameter (paired with the schemas by position).</summary>
    internal const string TablesParameter = "tables";

    /// <summary>The relations named by the two parallel arrays, with their kind, partition flag and write counters.</summary>
    internal const string RelationsSql =
        """
        select n.nspname, c.relname, c.relkind::pg_catalog.text, c.relispartition, s.n_tup_ins, s.n_tup_upd
        from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
        join pg_catalog.pg_namespace n on n.nspname = m.schema_name
        join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
        left join pg_catalog.pg_stat_all_tables s on s.relid = c.oid
        """;

    /// <summary>Reads <see cref="RelationsSql" /> in <paramref name="transaction" />, as the role that transaction runs as.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The read-only transaction.</param>
    /// <param name="schemas">The candidates' tables' schemas, paired by position with <paramref name="tables" />.</param>
    /// <param name="tables">The candidates' tables.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<IndexAdvisorRelation>> ReadRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        IReadOnlyList<string> tables,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(tables);

        if (schemas.Count != tables.Count)
        {
            throw new ArgumentException("The schemas and the tables are read in pairs.", nameof(tables));
        }

        if (tables.Count == 0)
        {
            return [];
        }

        await using NpgsqlCommand command = new(RelationsSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter(SchemasParameter, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas.ToArray() });
        command.Parameters.Add(new NpgsqlParameter(TablesParameter, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tables.ToArray() });

        List<IndexAdvisorRelation> relations = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            relations.Add(new IndexAdvisorRelation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2) is { Length: > 0 } kind ? kind[0] : (char) 0,
                reader.GetBoolean(3),
                await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(4),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(5)));
        }

        return relations;
    }
}
