using System.Collections.Concurrent;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>One column of a table, as <c>information_schema.columns</c> describes it.</summary>
/// <param name="Name">The column name.</param>
/// <param name="DataType">The SQL standard type name, for display (<c>timestamp with time zone</c>).</param>
/// <param name="UdtName">The Postgres type name, which is what the studio branches on (<c>timestamptz</c>).</param>
/// <param name="IsNullable">Whether the column accepts nulls.</param>
internal sealed record PostgresColumn(string Name, string DataType, string UdtName, bool IsNullable)
{
    /// <summary>
    /// The Npgsql type for a <c>udt_name</c>, used when a discovered table's column has to be compared
    /// against a typed parameter. Anything unrecognised becomes <see cref="NpgsqlDbType.Unknown"/>, which
    /// Npgsql sends as an untyped literal for Postgres to resolve.
    /// </summary>
    public static NpgsqlDbType ToNpgsqlDbType(string? udtName) => udtName?.ToLowerInvariant() switch
    {
        "bool" => NpgsqlDbType.Boolean,
        "int2" => NpgsqlDbType.Smallint,
        "int4" => NpgsqlDbType.Integer,
        "int8" => NpgsqlDbType.Bigint,
        "float4" => NpgsqlDbType.Real,
        "float8" => NpgsqlDbType.Double,
        "numeric" => NpgsqlDbType.Numeric,
        "money" => NpgsqlDbType.Money,
        "uuid" => NpgsqlDbType.Uuid,
        "text" => NpgsqlDbType.Text,
        "varchar" => NpgsqlDbType.Varchar,
        "bpchar" => NpgsqlDbType.Char,
        "date" => NpgsqlDbType.Date,
        "time" => NpgsqlDbType.Time,
        "timestamp" => NpgsqlDbType.Timestamp,
        "timestamptz" => NpgsqlDbType.TimestampTz,
        "interval" => NpgsqlDbType.Interval,
        "json" => NpgsqlDbType.Json,
        "jsonb" => NpgsqlDbType.Jsonb,
        "bytea" => NpgsqlDbType.Bytea,
        _ => NpgsqlDbType.Unknown,
    };
}

/// <summary>
/// The little bounded, expiring cache the two schema catalogs share.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both things it does are corrections, not optimisations.</b> A cache with no expiry remembers a
/// migration that has since happened: somebody adds a duplicated field, applies it from the Schema screen
/// or from their own deployment, and every documents page in every circuit goes on selecting the old
/// column set until the process restarts. A cache with no bound grows with the number of distinct tables
/// a long-lived process is ever asked about — which on a multi-database, multi-tenant host is not a small
/// number, and is attacker-influenced, because the table name comes from a URL.
/// </para>
/// <para>
/// Overflow clears the whole thing rather than evicting least-recently-used. An LRU needs per-entry
/// bookkeeping and a lock on the read path to be correct; the cost of being wrong here is one extra read
/// of a system view, so the cheap answer is the right one.
/// </para>
/// </remarks>
/// <typeparam name="TValue">What is cached.</typeparam>
internal sealed class CatalogCache<TValue>
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>How long an entry stays usable. Sixty seconds by default.</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How many entries the cache holds before it forgets everything and starts again.</summary>
    public int MaxEntries { get; init; } = 512;

    /// <summary>The cached value, when there is one that has not expired.</summary>
    public bool TryGet(string key, out TValue value)
    {
        if (entries.TryGetValue(key, out var entry) && entry.ExpiresAt > Environment.TickCount64)
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Remembers a value until the TTL runs out.</summary>
    public void Set(string key, TValue value)
    {
        // Checked before the insert, so the dictionary never holds more than MaxEntries + the racing
        // writers that got past this line, which is bounded by the number of threads.
        if (entries.Count >= MaxEntries)
        {
            entries.Clear();
        }

        entries[key] = new Entry(value, Environment.TickCount64 + (long) Ttl.TotalMilliseconds);
    }

    /// <summary>Forgets one key.</summary>
    public void Remove(string key) => entries.TryRemove(key, out _);

    /// <summary>Forgets everything.</summary>
    public void Clear() => entries.Clear();

    /// <summary>How many entries are held, expired ones included. For tests.</summary>
    internal int Count => entries.Count;

    private readonly record struct Entry(TValue Value, long ExpiresAt);
}

/// <summary>The columns of one table, keyed by name, in ordinal order.</summary>
/// <param name="Schema">The schema asked for.</param>
/// <param name="Table">The table asked for.</param>
/// <param name="Columns">The columns in ordinal order; empty when the table does not exist.</param>
internal sealed record TableColumns(string Schema, string Table, IReadOnlyList<PostgresColumn> Columns)
{
    private readonly Dictionary<string, PostgresColumn> byName =
        Columns.ToDictionary(x => x.Name, StringComparer.Ordinal);

    /// <summary>Whether the table exists — a table with no columns cannot be created in Postgres.</summary>
    public bool Exists => Columns.Count > 0;

    /// <summary>Whether a column of this name exists.</summary>
    public bool Has(string column) => byName.ContainsKey(column);

    /// <summary>The column, or <see langword="null"/>.</summary>
    public PostgresColumn? Find(string column) => byName.GetValueOrDefault(column);
}

/// <summary>
/// Reads <c>information_schema.columns</c>, and remembers what it read.
/// </summary>
/// <remarks>
/// <para>
/// Two areas of the studio need the physical column set rather than Marten's configuration: the id
/// parameter's type on a strong-typed or <c>UseIdentityKey</c> document (plan §4.4, "Load one"), and the
/// <c>mt_events</c> column set, where <c>bdata</c>, <c>correlation_id</c>, <c>causation_id</c>,
/// <c>headers</c>, <c>user_name</c> and <c>tags</c> are all conditional (U8). Both are asked on every page
/// render, and neither changes without a schema migration, so the answer is cached.
/// </para>
/// <para>
/// The cache key is the database identity — host, port and database name — and never the connection
/// string, which holds a password and would put it into a dictionary key and a debugger view.
/// A failed read is not cached, so a database that was unreachable once is asked again.
/// </para>
/// <para>
/// Entries expire after <see cref="CatalogCache{TValue}.Ttl"/> and the cache is bounded — see
/// <see cref="CatalogCache{TValue}"/> for why both matter. <see cref="Clear"/> is still the right call
/// immediately after the studio itself applies a schema change; the TTL is what covers the migrations it
/// did not make.
/// </para>
/// </remarks>
internal sealed class ColumnCatalog
{
    /// <summary>The query, as a constant: no identifier or value is ever interpolated into it.</summary>
    internal const string Sql =
        """
        select column_name, data_type, udt_name, is_nullable
        from information_schema.columns
        where table_schema = @schema and table_name = @table
        order by ordinal_position
        """;

    private readonly CatalogCache<TableColumns> cache = new();

    /// <summary>How long a catalog read may take before it is abandoned.</summary>
    public int CommandTimeoutSeconds { get; init; } = 15;

    /// <summary>Reads the columns of one table, from the cache when it has them.</summary>
    public async Task<TableColumns> GetAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var key = CacheKey(connection, schema, table);

        if (cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var read = await ReadAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);

        // Losing a race here costs one extra read of a system view and nothing else, which is cheaper than
        // holding a lock across the round trip.
        cache.Set(key, read);

        return read;
    }

    /// <summary>Forgets one table, for when the studio has just applied a schema change.</summary>
    public void Invalidate(NpgsqlConnection connection, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(connection);

        cache.Remove(CacheKey(connection, schema, table));
    }

    /// <summary>Forgets everything.</summary>
    public void Clear() => cache.Clear();

    private async Task<TableColumns> ReadAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Sql, connection) { CommandTimeout = CommandTimeoutSeconds };

        command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = schema });
        command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = table });

        List<PostgresColumn> columns = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new PostgresColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                string.Equals(reader.GetString(3), "YES", StringComparison.Ordinal)));
        }

        return new TableColumns(schema, table, columns);
    }

    private static string CacheKey(NpgsqlConnection connection, string schema, string table)
    {
        // Host/port/database identifies the database without carrying the credentials that would make a
        // cache key a place a password could be read from.
        return $"{connection.Host}:{connection.Port}/{connection.Database}/{schema}/{table}";
    }
}
