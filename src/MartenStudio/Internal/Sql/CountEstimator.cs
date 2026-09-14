using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// How many documents a collection holds — or the honest admission that the studio could not find out.
/// </summary>
/// <remarks>
/// "Cannot report" is a value, not an error (plan §4.8): a table that has been dropped under the studio,
/// or a database that refused the read, must be drawn differently from a collection that genuinely holds
/// nothing. <see cref="IsEstimate"/> is what the UI prefixes with <c>~</c> (D8).
/// </remarks>
/// <param name="Value">The number of rows, or zero when <paramref name="IsUnavailable"/>.</param>
/// <param name="IsEstimate">Whether this came from <c>pg_class.reltuples</c> rather than <c>count(*)</c>.</param>
/// <param name="IsUnavailable">Whether the count could not be established at all.</param>
internal readonly record struct DocumentCount(long Value, bool IsEstimate, bool IsUnavailable)
{
    /// <summary>The count could not be established — the table is gone, or the read was refused.</summary>
    public static DocumentCount Unavailable { get; } = new(0, false, true);

    /// <summary>A <c>reltuples</c> estimate.</summary>
    public static DocumentCount Estimate(long value) => new(value, true, false);

    /// <summary>An exact <c>count(*)</c>.</summary>
    public static DocumentCount Exact(long value) => new(value, false, false);
}

/// <summary>
/// Counts documents the cheap way first (D8).
/// </summary>
/// <remarks>
/// <para>
/// An exact <c>count(*)</c> on a ten-million-row collection is a sequential scan, and a dashboard that
/// does one per collection per refresh is a denial of service against the database it is meant to help
/// you understand. So the first question is always <c>pg_class.reltuples</c>, which is free.
/// </para>
/// <para>
/// Two cases make the estimate useless and are the reason <see cref="ExactCountThreshold"/> exists.
/// Postgres reports <c>-1</c> for a table that has never been vacuumed or analysed — a freshly seeded
/// collection, which is exactly what a new user is looking at — and an estimate of "about 12" is worse
/// than the truth on a table where the truth costs nothing. Below the threshold the estimator therefore
/// pays for <c>count(*)</c>; above it, the estimate stands until somebody asks for the exact number.
/// </para>
/// </remarks>
internal sealed class CountEstimator
{
    /// <summary>
    /// The estimate query. <c>to_regclass</c> returns null rather than throwing for a table that is not
    /// there, which is what turns "the table is gone" into <see cref="DocumentCount.Unavailable"/> instead
    /// of an exception on a dashboard tile.
    /// </summary>
    internal const string EstimateSql =
        """
        select c.reltuples::bigint
        from pg_catalog.pg_class c
        where c.oid = to_regclass(@qualified)
        """;

    /// <summary>Rows at or below which the estimate is replaced by an exact count.</summary>
    public long ExactCountThreshold { get; init; } = 10_000;

    /// <summary>How long either query may take. An exact count runs under this and nothing longer.</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// The estimate, upgraded to an exact count when the table is small enough (or has never been
    /// analysed, which Postgres reports as a negative <c>reltuples</c>).
    /// </summary>
    public async Task<DocumentCount> CountAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        var estimate = await EstimateAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);

        if (estimate.IsUnavailable)
        {
            return estimate;
        }

        if (estimate.Value > ExactCountThreshold)
        {
            return estimate;
        }

        return await CountExactAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>reltuples</c> estimate alone.</summary>
    public async Task<DocumentCount> EstimateAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(EstimateSql, connection) { CommandTimeout = CommandTimeoutSeconds };

        command.Parameters.Add(new NpgsqlParameter("qualified", NpgsqlDbType.Text)
        {
            Value = SqlIdentifier.Qualify(schema, table),
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is null or DBNull)
        {
            return DocumentCount.Unavailable;
        }

        var rows = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);

        // -1 is Postgres for "never analysed", not for "minus one row".
        return rows < 0 ? DocumentCount.Estimate(0) : DocumentCount.Estimate(rows);
    }

    /// <summary>An exact <c>count(*)</c>, which the user asked for explicitly or the threshold allowed.</summary>
    public async Task<DocumentCount> CountExactAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(ExactSql(schema, table), connection)
        {
            CommandTimeout = CommandTimeoutSeconds,
        };

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull
            ? DocumentCount.Unavailable
            : DocumentCount.Exact(Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The exact-count query. The only thing interpolated is a quoted identifier.</summary>
    internal static string ExactSql(string schema, string table) =>
        "select count(*) from " + SqlIdentifier.Qualify(schema, table);
}
