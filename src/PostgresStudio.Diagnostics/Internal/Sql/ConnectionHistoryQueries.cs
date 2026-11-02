using Npgsql;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// Issue #43: what the connection history reads - which instance of the server answered, and
/// <see cref="HealthConnectionQueries.ServerHolders" /> - and the one place it runs them. Catalog functions and the statistics view
/// only; locks nothing, names no relation.
/// </summary>
internal static class ConnectionHistoryQueries
{
    /// <summary>
    /// The answering instance: its address and port (a socket says <c>local</c>) and its start time in UTC, so a failover - another
    /// address - and a restart - another start time - each read as a break in the series. Every role may call all three.
    /// </summary>
    public static HealthQuery Instance() => new(
        "select coalesce(host(inet_server_addr()), 'local') || ':' || coalesce(inet_server_port(), 0)::text\n" +
        "       || '@' || to_char(pg_postmaster_start_time() at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"')");

    /// <summary>
    /// One sample's reads, in the caller's read-only transaction: <see cref="Instance" />, then
    /// <see cref="HealthConnectionQueries.ServerHolders" />, each row mapped by <paramref name="map" />.
    /// </summary>
    /// <param name="connection">The sampler's kept connection.</param>
    /// <param name="transaction">The read-only transaction <c>ReadOnlySqlSession</c> opened on it.</param>
    /// <param name="timeout">The statement timeout the transaction set; the command waits five seconds past it.</param>
    /// <param name="map">Maps one holders row.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<(string? Instance, List<T> Holders)> ReadSampleAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TimeSpan timeout,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        string? instance;

        await using (NpgsqlCommand command = Command(Instance(), connection, transaction, timeout))
        {
            instance = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        List<T> rows = [];

        await using (NpgsqlCommand command = Command(HealthConnectionQueries.ServerHolders(), connection, transaction, timeout))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(map(reader));
            }
        }

        return (instance, rows);
    }

    /// <summary>
    /// Names the sampler's kept session for as long as it is kept (<c>set_config(..., false)</c>, the session's own value): between
    /// samples it is an idle session of the application's pool, and <c>pg_stat_activity</c> should say whose. Each sample's read-only
    /// transaction sets the same name locally, which reverts to this one at its end.
    /// </summary>
    /// <param name="connection">The kept connection, just opened.</param>
    /// <param name="name">The name, already one PostgreSQL keeps as written.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    public static async Task NameSessionAsync(NpgsqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("select pg_catalog.set_config('application_name', @name, false)", connection)
        {
            CommandTimeout = 5,
        };
        command.Parameters.Add(new NpgsqlParameter<string>("name", name));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives the session the name it was opened with (<c>reset application_name</c>), so the connection goes back to the
    /// application's pool as the application's - whatever <c>No Reset On Close</c> says.
    /// </summary>
    /// <param name="connection">The kept connection, about to be closed.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    public static async Task ForgetSessionNameAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("reset application_name", connection) { CommandTimeout = 5 };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlCommand Command(HealthQuery query, NpgsqlConnection connection, NpgsqlTransaction transaction, TimeSpan timeout)
    {
        NpgsqlCommand command = new(query.Sql, connection, transaction) { CommandTimeout = (int) Math.Ceiling(timeout.TotalSeconds) + 5 };

        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            command.Parameters.Add(parameter.Clone());
        }

        return command;
    }
}
