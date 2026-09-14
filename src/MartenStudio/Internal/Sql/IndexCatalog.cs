using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// Reads <c>pg_indexes</c>, and remembers what it read.
/// </summary>
/// <remarks>
/// <para>
/// The index verdict (plan §3.4) cross-checks what <c>StoreOptions</c> <em>declares</em> against what the
/// database actually has, because the two disagree exactly when it matters: a computed index added in
/// code but never migrated, or an index a DBA added by hand that Marten knows nothing about. Declared
/// indexes come from <c>IDocumentType.Indexes</c>; this is the other half.
/// </para>
/// <para>
/// Cached like <see cref="ColumnCatalog"/>, and for the same reason: the verdict is asked for on every
/// search, indexes only change with a migration, and the cache key is the database identity rather than
/// the connection string, which holds a password. Bounded and expiring for the same reasons too — see
/// <see cref="CatalogCache{TValue}"/> — which matters more here than anywhere else in the studio, because
/// a stale index list is a verdict that tells somebody to add the index they have just added.
/// </para>
/// </remarks>
internal sealed class IndexCatalog
{
    /// <summary>The query, as a constant: no identifier or value is ever interpolated into it.</summary>
    internal const string Sql =
        """
        select indexname, indexdef
        from pg_catalog.pg_indexes
        where schemaname = @schema and tablename = @table
        order by indexname
        """;

    private readonly CatalogCache<IReadOnlyList<PostgresIndex>> cache = new();

    /// <summary>How long an index read may take before it is abandoned.</summary>
    public int CommandTimeoutSeconds { get; init; } = 15;

    /// <summary>The indexes on one table, from the cache when it has them.</summary>
    public async Task<IReadOnlyList<PostgresIndex>> GetAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var key = $"{connection.Host}:{connection.Port}/{connection.Database}/{schema}/{table}";

        if (cache.TryGet(key, out var cached))
        {
            return cached;
        }

        await using var command = new NpgsqlCommand(Sql, connection) { CommandTimeout = CommandTimeoutSeconds };

        command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = schema });
        command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = table });

        List<PostgresIndex> indexes = [];

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                indexes.Add(new PostgresIndex(reader.GetString(0), reader.GetString(1)));
            }
        }

        cache.Set(key, indexes);

        return indexes;
    }

    /// <summary>Forgets everything, for when the studio has just applied a schema change.</summary>
    public void Clear() => cache.Clear();
}
