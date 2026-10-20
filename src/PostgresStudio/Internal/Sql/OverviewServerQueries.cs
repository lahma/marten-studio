using Npgsql;

namespace PostgresStudio.Internal.Sql;

/// <summary>What <see cref="HealthServerQueries.Platform" /> answered, as plain values.</summary>
/// <param name="VersionNumber"><c>server_version_num</c>.</param>
/// <param name="Version"><c>server_version</c>.</param>
/// <param name="RdsSettings">An <c>rds.extensions</c> or <c>rds.allowed_extensions</c> setting exists.</param>
/// <param name="AuroraVersionFunction">An <c>aurora_version()</c> function exists.</param>
/// <param name="AzureExtensions">An <c>azure.extensions</c> setting exists.</param>
/// <param name="CloudSqlSettings">A <c>cloudsql.*</c> setting exists.</param>
/// <param name="NeonSuperuserRole">A <c>neon_superuser</c> role exists.</param>
internal sealed record ServerPlatformRow(
    int VersionNumber,
    string Version,
    bool RdsSettings,
    bool AuroraVersionFunction,
    bool AzureExtensions,
    bool CloudSqlSettings,
    bool NeonSuperuserRole);

/// <summary>
/// PostgresStudio's Overview reads the server's version and platform outside a health sweep, with S01's own statement:
/// no new SQL, run here so that a command is still built only under <c>Internal/Sql</c> (AGENTS.md hard rule 4).
/// </summary>
internal static class OverviewServerQueries
{
    /// <summary>Runs <see cref="HealthServerQueries.Platform" /> in <paramref name="transaction" /> and reads its one row.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The read-only transaction it runs in.</param>
    /// <param name="commandTimeout">The client-side timeout, a little past the server's <c>statement_timeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="InvalidOperationException">The server answered no row.</exception>
    public static async Task<ServerPlatformRow> ReadPlatformAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        HealthQuery query = HealthServerQueries.Platform();

        await using NpgsqlCommand command = new(query.Sql, connection, transaction) { CommandTimeout = commandTimeout };

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The server answered no row for its version.");
        }

        return new ServerPlatformRow(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6));
    }
}
