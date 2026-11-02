using Npgsql;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Services;

/// <summary>
/// What kind of database failure an exception is, for the services that treat some kinds as an
/// expected answer rather than a fault.
/// </summary>
/// <remarks>
/// One place, because the question is asked by the documents browser and by the event-store reads, and
/// a rule written out at each of them is a rule that drifts: the event reads once treated only
/// <c>57014</c> as a timeout while the documents browser had long known the second spelling, so the same
/// expired budget was Debug on one screen and a Warning on the next.
/// </remarks>
internal static class PostgresFailure
{
    /// <summary><c>query_canceled</c>: what <c>statement_timeout</c> and a cancel request end a statement with.</summary>
    public const string QueryCanceled = "57014";

    /// <summary>The <see cref="Exception.Data" /> key <see cref="OpenAsync" /> marks an opening failure with.</summary>
    private const string ConnectionOpenKey = "MartenStudio.PostgresFailure.ConnectionOpen";

    /// <summary>
    /// Opens <paramref name="connection" />, marking whatever it throws as a failure to connect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Npgsql spells three different things the same way: a statement that ran past
    /// <c>CommandTimeout</c> (<c>NpgsqlException("Exception while reading from stream",
    /// TimeoutException)</c>), a server that did not answer the connect
    /// (<c>NpgsqlException("Failed to connect to …", TimeoutException)</c>), and a pool that ran dry
    /// (<c>NpgsqlException("The connection pool has been exhausted …", new TimeoutException())</c>) -
    /// verified by decompiling Npgsql 9.0.4's <c>NpgsqlConnector</c> and <c>PoolingDataSource</c>. Only
    /// the first is the studio's own budget doing its job. The other two are exactly the anomalies an
    /// operator needs to see, and <see cref="IsTimeout" /> would otherwise read them as "timed out" - a
    /// Debug line, and on the recent-documents region a notice that the store is too large to scan.
    /// </para>
    /// <para>
    /// So a caller that means "a statement ran out of time" opens through here, and
    /// <see cref="IsTimeout" /> answers <see langword="false" /> for anything this marked. The mark is an
    /// entry in <see cref="Exception.Data" /> rather than a wrapping exception, so what the page renders -
    /// the message, the retry offer - is exactly what it was.
    /// </para>
    /// </remarks>
    /// <param name="connection">A connection that has not been opened yet.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    public static Task OpenAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Issue #44: opened through the studio's share of its pool - and not async here, so the slot the open marks is marked in
        // the caller's own execution context, where a nested read of the caller's sees it (StudioConnections.OpenStudioAsync).
        Task opening;

        try
        {
            opening = connection.OpenStudioAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkAsConnectionFailure(exception);
            throw;
        }

        return MarkFailuresAsync(opening);
    }

    private static async Task MarkFailuresAsync(Task opening)
    {
        try
        {
            await opening.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkAsConnectionFailure(exception);
            throw;
        }
    }

    /// <summary>Whether <see cref="OpenAsync" /> marked this as a failure to connect.</summary>
    /// <param name="exception">The failure.</param>
    public static bool IsConnectionFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Data.Contains(ConnectionOpenKey);
    }

    /// <summary>Marks <paramref name="exception" /> as raised while a connection was being opened.</summary>
    /// <remarks>
    /// An exception type is free to hand out a read-only <see cref="Exception.Data" />, and a mark that
    /// could not be written is not worth replacing the original failure over.
    /// </remarks>
    internal static void MarkAsConnectionFailure(Exception exception)
    {
        try
        {
            exception.Data[ConnectionOpenKey] = true;
        }
        catch (Exception readOnly) when (readOnly is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // Left unmarked: at worst an open that timed out is read as a statement that did.
        }
    }

    /// <summary>
    /// Whether a failed read is a timeout rather than a fault - Postgres' own, or Npgsql's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both spellings, because which one arrives is a race.</b> A
    /// <see cref="NpgsqlCommand.CommandTimeout"/> that expires makes Npgsql send a cancellation request:
    /// when the backend answers it in time the client sees <c>57014</c>, and when it does not the client
    /// sees an <see cref="NpgsqlException"/> wrapping a <see cref="TimeoutException"/>. A host that sets
    /// <c>statement_timeout</c> on the role, the database or the connection string produces the first
    /// directly. Catching only one of them works until the day the database is busy, which is the day this
    /// matters.
    /// </para>
    /// <para>
    /// <b>Why the distinction is worth a method.</b> The documents browser maps a timeout to
    /// <c>DocumentCount.Unknown</c> and a fault to <c>DocumentCount.Unavailable</c>, and those two draw
    /// differently on purpose: unknown is "there, and nobody has measured it" and keeps the "=" button on
    /// offer, while unavailable is "could not read this table" and takes the badge away entirely. The
    /// event-store reads and the recent-documents region log a timeout at Debug - the budget the studio
    /// set doing its job, on a read the page already shows as timed out - and anything else as an
    /// anomaly. Mistaking one spelling for a fault is a Warning on exactly the day the database is slow.
    /// </para>
    /// <para>
    /// <b>Never a failure to connect.</b> The second spelling is also how Npgsql reports a connect that
    /// timed out and a pool that ran dry, and neither is a statement's budget; anything
    /// <see cref="OpenAsync" /> marked is therefore not a timeout here, whatever it wraps.
    /// </para>
    /// </remarks>
    /// <param name="exception">The failure.</param>
    public static bool IsTimeout(Exception exception) =>
        !IsConnectionFailure(exception)
        && ((exception is PostgresException postgres && string.Equals(postgres.SqlState, QueryCanceled, StringComparison.Ordinal))
            || exception is TimeoutException
            || (exception is NpgsqlException npgsql && npgsql.InnerException is TimeoutException));

    /// <summary>
    /// Whether <paramref name="exception" /> - or one it wraps - is the studio's own share of the pool being all in use
    /// (<c>MaxStudioConnections</c>, issue #44): the host's cap at work, not the database's state.
    /// </summary>
    /// <param name="exception">What was thrown.</param>
    public static bool IsStudioBusy(Exception exception) => StudioBusy(exception) is not null;

    /// <summary>The studio's busy exception <paramref name="exception" /> is or wraps, or <see langword="null" />.</summary>
    private static StudioConnectionsBusyException? StudioBusy(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is StudioConnectionsBusyException busy)
            {
                return busy;
            }
        }

        return null;
    }

    /// <summary>
    /// What a page says of a read that could not get a connection: that the studio could not connect, or - when the studio's own
    /// share of the pool was all in use (<see cref="IsStudioBusy" />) - that, with the option that sets it. A clause, lower case and
    /// without a full stop, for a sentence of the caller's; <see cref="CouldNotConnect" /> is the whole sentence.
    /// </summary>
    /// <param name="exception">The failure, one <see cref="IsConnectionFailure" /> answered for.</param>
    public static string CouldNotConnectClause(Exception exception) =>
        StudioBusy(exception) is { } busy
            ? "the studio's connections to this database were all in use (MaxStudioConnections"
                + (busy.PoolSize is { } poolSize
                    ? ", held to the data source's Maximum Pool Size of " + poolSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty)
                + "); try again in a moment"
            : "the studio could not connect to this database";

    /// <summary>The whole sentence of <see cref="CouldNotConnectClause" />.</summary>
    /// <param name="exception">The failure, one <see cref="IsConnectionFailure" /> answered for.</param>
    public static string CouldNotConnect(Exception exception)
    {
        string clause = CouldNotConnectClause(exception);
        return char.ToUpperInvariant(clause[0]) + clause[1..] + ".";
    }
}
