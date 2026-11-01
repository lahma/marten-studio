using Npgsql;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The health runner's preamble: what each health statement's read-only transaction sets for itself before the statement
/// runs (<c>HealthCheckContext</c>), in one round trip and with no parameter at all.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>search_path</c> is pinned to <c>pg_catalog</c></b>, as every catalog read pins it
/// (<see cref="DatabaseCatalogQueries.PinSearchPathSql" />), so every name Postgres deparses comes back qualified.
/// </para>
/// <para>
/// <b><c>DateStyle</c> is pinned to <c>ISO</c></b> (INT-W1), so a date or a time Postgres writes as text is written in ISO
/// whatever the server, the database or the role defaults to. T20 needs it: it reads each range partition's bounds as the
/// text <c>pg_get_expr(relpartbound, 0)</c> deparses in the session's style, and <see cref="PartitionBoundText" /> reads a
/// time bound only in ISO, because under <c>SQL</c>, <c>Postgres</c> or <c>German</c> a <c>timestamptz</c> bound carries a zone
/// abbreviation rather than an offset. Only the output style is set: the field order (<c>MDY</c>, <c>DMY</c>) stays the
/// session's, and no health statement reads a date from text. Nothing else a sweep reads depends on it - every other date or
/// time comes back as a typed value, which <c>DateStyle</c> does not touch, and no settings read asks for <c>DateStyle</c>
/// (<c>ServerSettingsCatalog</c>) - so the pin changes T20's input and nothing else.
/// </para>
/// <para>
/// Both are local to the transaction, which the read-only session always rolls back, so nothing outlives the statement on the
/// pooled connection.
/// </para>
/// </remarks>
internal static class HealthSessionQueries
{
    /// <summary>
    /// Pins <c>search_path</c> to <c>pg_catalog</c> and <c>DateStyle</c> to ISO for the rest of the transaction. A constant.
    /// </summary>
    internal const string PinSessionSql =
        """
        select pg_catalog.set_config('search_path', 'pg_catalog', true),
               pg_catalog.set_config('datestyle', 'ISO', true)
        """;

    /// <summary>Runs <see cref="PinSessionSql" />. Run first in every health statement's transaction.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The read-only transaction.</param>
    /// <param name="commandTimeoutSeconds">The client-side timeout.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    public static async Task PinSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(PinSessionSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
