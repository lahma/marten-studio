using System.Text.Json;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Aurora;

/// <summary>One instance of an Aurora cluster, as <c>aurora_replica_status()</c> reports it (A3).</summary>
/// <param name="Instance">
/// <c>server_id</c>: the instance's identifier - the cluster's own name for it, given when it was created. Database-wide
/// information (D29), not a session's, a role's or a client's.
/// </param>
/// <param name="Writer">Whether it is the writer: its <c>session_id</c> is <c>MASTER_SESSION_ID</c>; a reader's is its UUID.</param>
/// <param name="Lag"><c>replica_lag_in_msec</c>: how far a reader is behind the writer; <see langword="null" /> on the writer.</param>
/// <param name="ReplayLatency"><c>cur_replay_latency_in_usec</c>: how long a reader is expected to take to replay the log.</param>
/// <param name="FeedbackXmin"><c>feedback_xmin</c>: the oldest transaction id the instance's queries still need, or <see langword="null" />.</param>
/// <param name="LastUpdate">
/// <c>last_update_timestamp</c>: when its status was last updated - <see langword="null" /> for the instance this session is
/// connected to from Aurora 13.9 (the function's page).
/// </param>
internal sealed record AuroraReplica(
    string Instance,
    bool Writer,
    TimeSpan? Lag,
    TimeSpan? ReplayLatency,
    string? FeedbackXmin,
    DateTimeOffset? LastUpdate)
{
    /// <summary>
    /// One row of <see cref="AuroraQueries.ReplicaStatus" />, read by column name - or <see langword="null" /> for a row with no
    /// <c>server_id</c>, which names no instance. A column the release lacks is <see langword="null" />.
    /// </summary>
    public static AuroraReplica? FromJson(JsonElement row)
    {
        if (AuroraJson.Text(row, "server_id") is not { Length: > 0 } instance)
        {
            return null;
        }

        return new AuroraReplica(
            instance,
            string.Equals(AuroraJson.Text(row, "session_id"), AuroraReplicaStatus.WriterSession, StringComparison.Ordinal),
            AuroraJson.Number(row, "replica_lag_in_msec") is { } milliseconds and >= 0 ? TimeSpan.FromMilliseconds(milliseconds) : null,
            AuroraJson.Number(row, "cur_replay_latency_in_usec") is { } microseconds and >= 0 ? TimeSpan.FromMicroseconds(microseconds) : null,
            AuroraJson.Text(row, "feedback_xmin"),
            AuroraJson.Timestamp(row, "last_update_timestamp"));
    }
}

/// <summary>
/// Every instance of the Aurora cluster the database is on, read once per sweep (R03's <c>aurora-reader</c> view, the
/// Replication page's "Aurora replicas" and the Server page's Aurora section share it).
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Instances">The writer first, then the readers by identifier.</param>
/// <param name="Total">How many rows the function returned, which <see cref="AuroraQueries.MaxInstances" /> may cut.</param>
internal sealed record AuroraReplicaStatus(DateTimeOffset ReadAt, IReadOnlyList<AuroraReplica> Instances, long Total)
{
    /// <summary>The memo key of the read.</summary>
    public const string Key = "aurora.replicas";

    /// <summary>The writer's <c>session_id</c>, "always" in the function's own words.</summary>
    public const string WriterSession = "MASTER_SESSION_ID";

    /// <summary>The readers, in order.</summary>
    public IReadOnlyList<AuroraReplica> Readers => [.. Instances.Where(static x => !x.Writer)];

    /// <summary>The writer, or <see langword="null" /> when the function listed none.</summary>
    public AuroraReplica? WriterInstance => Instances.FirstOrDefault(static x => x.Writer);

    /// <summary>The rows, read by name, the writer first and then the readers by identifier.</summary>
    public static AuroraReplicaStatus FromJson(DateTimeOffset readAt, IEnumerable<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        List<AuroraReplica> instances = [];
        long total = 0;

        foreach (string row in rows)
        {
            total++;

            if (AuroraJson.Object(row) is { } json && AuroraReplica.FromJson(json) is { } replica)
            {
                instances.Add(replica);
            }
        }

        return new AuroraReplicaStatus(
            readAt,
            [.. instances.OrderByDescending(static x => x.Writer).ThenBy(static x => x.Instance, StringComparer.Ordinal)],
            total);
    }

    /// <summary>The sweep's one read of it, whichever check or page asks first.</summary>
    public static Task<AuroraReplicaStatus> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(Key, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<AuroraReplicaStatus> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = AuroraQueries.ReplicaStatus();
        IReadOnlyList<string> rows = await context.Sql
            .QueryAsync(query, static reader => reader.IsDBNull(0) ? "{}" : reader.GetString(0), cancellationToken)
            .ConfigureAwait(false);

        return FromJson(context.Now, rows);
    }
}
