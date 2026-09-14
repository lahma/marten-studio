using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>One table in one of the store's schemas, as the catalog and the statistics collector see it.</summary>
/// <param name="Schema">The schema the table lives in.</param>
/// <param name="Table">The table name.</param>
/// <param name="TotalBytes"><c>pg_total_relation_size</c> — heap, indexes, toast.</param>
/// <param name="HeapBytes"><c>pg_relation_size</c> — the main fork alone.</param>
/// <param name="IndexBytes"><c>pg_indexes_size</c>.</param>
/// <param name="EstimatedRows">
/// <c>pg_class.reltuples</c> cast to <c>bigint</c>, which is an estimate and is <c>-1</c> before the
/// first analyze — the same shape <see cref="CountEstimator" /> reads, so the two agree.
/// </param>
/// <param name="LiveRows"><c>n_live_tup</c>, or <see langword="null" /> when the statistics collector has nothing.</param>
/// <param name="DeadRows"><c>n_dead_tup</c>, or <see langword="null" />.</param>
/// <param name="SequentialScans"><c>seq_scan</c>, or <see langword="null" />.</param>
/// <param name="IndexScans"><c>idx_scan</c>, or <see langword="null" />.</param>
/// <param name="LastVacuum">The later of <c>last_vacuum</c> and <c>last_autovacuum</c>.</param>
/// <param name="LastAnalyze">The later of <c>last_analyze</c> and <c>last_autoanalyze</c>.</param>
internal sealed record TableStatsRow(
    string Schema,
    string Table,
    long TotalBytes,
    long HeapBytes,
    long IndexBytes,
    long EstimatedRows,
    long? LiveRows,
    long? DeadRows,
    long? SequentialScans,
    long? IndexScans,
    DateTimeOffset? LastVacuum,
    DateTimeOffset? LastAnalyze);

/// <summary>One index in one of the store's schemas, with whatever use the database has recorded for it.</summary>
/// <param name="Schema">The schema the index lives in.</param>
/// <param name="Table">The table it is on.</param>
/// <param name="Name">The index name.</param>
/// <param name="Definition">The <c>create index</c> statement Postgres reports for it.</param>
/// <param name="Bytes"><c>pg_relation_size</c> of the index.</param>
/// <param name="Scans"><c>idx_scan</c>, or <see langword="null" /> when the collector has nothing.</param>
/// <param name="TuplesRead"><c>idx_tup_read</c>, or <see langword="null" />.</param>
/// <param name="IsPrimaryKey">Whether the index backs the table's primary key.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
internal sealed record IndexStatsRow(
    string Schema,
    string Table,
    string Name,
    string Definition,
    long Bytes,
    long? Scans,
    long? TuplesRead,
    bool IsPrimaryKey,
    bool IsUnique);

/// <summary>One function in one of the store's schemas, with the definition Postgres reconstructs for it.</summary>
/// <param name="Schema">The schema the function lives in.</param>
/// <param name="Name">The function name, without its argument list.</param>
/// <param name="Signature">The name with its argument types, which is what makes an overload identifiable.</param>
/// <param name="Definition">What <c>pg_get_functiondef</c> returns, or the reason it could not be read.</param>
internal sealed record FunctionStatsRow(string Schema, string Name, string Signature, string Definition);

/// <summary>
/// The catalog and statistics reads behind the Schema screen.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is scoped to the schemas the store actually owns — derived from <c>StoreOptions</c>
/// by <c>SchemaDeclarationReader</c> (never <c>AllSchemaNames()</c>, which applies migrations), passed as
/// a single <c>text[]</c> parameter and compared with <c>= any(@schemas)</c>. That is not a
/// convenience: a studio that listed every table on the server would report on the host application's
/// own tables, which is both a surprise and an information leak in a shared database. No identifier is
/// interpolated into any statement here; the schema names travel as a value like everything else
/// (AGENTS.md hard rule 4).
/// </para>
/// <para>
/// The numbers are estimates and are labelled as such on screen. <c>reltuples</c> is a planner estimate
/// that is <c>-1</c> until the table has been analyzed, the <c>pg_stat_*</c> counters are reset by
/// <c>pg_stat_reset()</c> and by a crash, and a table that has never been scanned since a reset is
/// indistinguishable from one that was never useful. The Indexes tab says so where it matters, because a
/// "never used" verdict that is really "statistics were reset yesterday" is how a UI talks somebody into
/// dropping an index they need.
/// </para>
/// </remarks>
internal static class SchemaStatsQueries
{
    /// <summary>
    /// Sizes and activity for every ordinary table in the store's schemas, largest first.
    /// </summary>
    /// <remarks>
    /// <c>relkind in ('r', 'p')</c>, not <c>= 'r'</c>: <c>'p'</c> is a partitioned table, and Marten
    /// partitions the event tables under <c>UseArchivedStreamPartitioning</c> or
    /// <c>UseTenantPartitionedEvents</c> and any document type with a <c>Partitioning</c> scheme. A
    /// <c>'r'</c>-only filter hid exactly the tables most worth looking at, and hid them silently.
    /// <c>pg_total_relation_size</c> of a partitioned parent is the parent's own forks only, so the
    /// partitions still appear as their own rows and nothing is double-counted.
    /// </remarks>
    internal const string TableStatsSql =
        """
        select n.nspname,
               c.relname,
               pg_total_relation_size(c.oid),
               pg_relation_size(c.oid),
               pg_indexes_size(c.oid),
               c.reltuples::bigint,
               s.n_live_tup,
               s.n_dead_tup,
               s.seq_scan,
               s.idx_scan,
               greatest(s.last_vacuum, s.last_autovacuum),
               greatest(s.last_analyze, s.last_autoanalyze)
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        left join pg_stat_user_tables s on s.relid = c.oid
        where c.relkind in ('r', 'p') and n.nspname = any(@schemas)
        order by pg_total_relation_size(c.oid) desc, n.nspname, c.relname
        """;

    /// <summary>Every index in the store's schemas, with its definition, its size and its recorded use.</summary>
    internal const string IndexStatsSql =
        """
        select n.nspname,
               t.relname,
               c.relname,
               pg_get_indexdef(c.oid),
               pg_relation_size(c.oid),
               s.idx_scan,
               s.idx_tup_read,
               i.indisprimary,
               i.indisunique
        from pg_index i
        join pg_class c on c.oid = i.indexrelid
        join pg_class t on t.oid = i.indrelid
        join pg_namespace n on n.oid = c.relnamespace
        left join pg_stat_user_indexes s on s.indexrelid = c.oid
        where n.nspname = any(@schemas)
        order by n.nspname, t.relname, c.relname
        """;

    /// <summary>
    /// Every ordinary function in the store's schemas.
    /// </summary>
    /// <remarks>
    /// <c>prokind = 'f'</c> is load-bearing: <c>pg_get_functiondef</c> raises <c>42809</c> for an
    /// aggregate, a window function or a procedure, and one such object in the schema would otherwise
    /// take the whole tab down with it.
    /// </remarks>
    internal const string FunctionsSql =
        """
        select n.nspname,
               p.proname,
               p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
               pg_get_functiondef(p.oid)
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        where n.nspname = any(@schemas) and p.prokind = 'f'
        order by n.nspname, p.proname
        """;

    /// <summary>The size of the database the connection is open against.</summary>
    internal const string DatabaseSizeSql = "select pg_database_size(current_database())";

    /// <summary>Reads table sizes and activity for the store's schemas.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="schemas">The schemas the store owns, derived from its options.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<TableStatsRow>> ReadTablesAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = CreateCommand(connection, TableStatsSql, schemas, commandTimeoutSeconds);

        List<TableStatsRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TableStatsRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                NullableInt64(reader, 6),
                NullableInt64(reader, 7),
                NullableInt64(reader, 8),
                NullableInt64(reader, 9),
                NullableTimestamp(reader, 10),
                NullableTimestamp(reader, 11)));
        }

        return rows;
    }

    /// <summary>Reads every index in the store's schemas, with its size and its recorded use.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="schemas">The schemas the store owns.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<IndexStatsRow>> ReadIndexesAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = CreateCommand(connection, IndexStatsSql, schemas, commandTimeoutSeconds);

        List<IndexStatsRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new IndexStatsRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetInt64(4),
                NullableInt64(reader, 5),
                NullableInt64(reader, 6),
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }

        return rows;
    }

    /// <summary>Reads the functions Marten (or anyone else) put into the store's schemas.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="schemas">The schemas the store owns.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<FunctionStatsRow>> ReadFunctionsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = CreateCommand(connection, FunctionsSql, schemas, commandTimeoutSeconds);

        List<FunctionStatsRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FunctionStatsRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>The size of the whole database, for the Tables tab's footer.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<long> ReadDatabaseSizeAsync(
        NpgsqlConnection connection,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(DatabaseSizeSql, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long size ? size : 0L;
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        string sql,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);

        var command = new NpgsqlCommand(sql, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = schemas as string[] ?? [.. schemas],
        });

        return command;
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
