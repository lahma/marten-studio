namespace PostgresStudio;

/// <summary>
/// One sample of a server's client connections, as the connection history keeps it (issue #43): when, on which server and
/// instance, how many of the client budget were used, and by which applications.
/// </summary>
/// <remarks>
/// A result, not a seam: every property has a safe default and none is required, so a property added in a later release
/// breaks nothing - a host's own <see cref="IPostgresConnectionHistoryStore" /> keeps what it understands. Counted from
/// <c>pg_stat_activity</c> as Database › Sessions › Holders counts it: client sessions only, the sampler's own left out.
/// </remarks>
public sealed record PostgresConnectionSample
{
    /// <summary>When the sample was taken, by the sampling process's clock.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>
    /// The server the sample is of: its host and port as the application's connection string names them (<c>db.example:5432</c>),
    /// never a credential. The series key: one sample per server per interval, whatever databases the studio lists on it.
    /// </summary>
    public string Server { get; init; } = "";

    /// <summary>
    /// Which instance answered: its address, port and start time. A change between two samples is a failover or a restart -
    /// a break in the series, not a drop in connections.
    /// </summary>
    public string? Instance { get; init; }

    /// <summary>Client connections to the server at the sample, every database's, the sampler's own left out.</summary>
    public long Connections { get; init; }

    /// <summary>The client budget: <c>max_connections</c> less <c>superuser_reserved_connections</c> and <c>reserved_connections</c>.</summary>
    public int ClientLimit { get; init; }

    /// <summary>
    /// The connections by <c>application_name</c>, most first, at most <see cref="PostgresConnectionHistoryOptions.MaxApplications" />
    /// of them; <see cref="PostgresConnectionCounts.ApplicationName" /> is <see langword="null" /> for sessions that set none.
    /// Names an application, so a page shows it only to a visitor past the activity gate.
    /// </summary>
    public IReadOnlyList<PostgresConnectionCounts> ByApplication { get; init; } = [];
}
