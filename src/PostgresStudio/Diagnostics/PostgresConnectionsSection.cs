namespace PostgresStudio;

/// <summary>
/// Connections and headroom. Totals are database-wide; by application is Activity-grade (another application's name).
/// </summary>
/// <remarks>
/// The studio's own reading session is excluded. Its other sessions - another page's read, a sweep - share the host's data
/// source, and so its <c>application_name</c>, and are counted under it.
/// </remarks>
public sealed record PostgresConnectionsSection
{
    /// <summary>Whether it was read; when not, the other properties keep their defaults.</summary>
    public PostgresReadOutcome Outcome { get; init; }

    /// <summary>Why not, or what limits it.</summary>
    public string? Reason { get; init; }

    /// <summary>When it was read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary><c>max_connections</c>.</summary>
    public int MaxConnections { get; init; }

    /// <summary><c>superuser_reserved_connections</c>, plus <c>reserved_connections</c> on PostgreSQL 16 and later.</summary>
    public int ReservedConnections { get; init; }

    /// <summary>Client sessions on the whole server, every database's: what <see cref="MaxConnections" /> is spent on.</summary>
    public int ServerConnections { get; init; }

    /// <summary>This database's client sessions, the studio's own reading session excluded.</summary>
    public PostgresConnectionCounts Database { get; init; } = new();

    /// <summary>
    /// The same, grouped by <c>application_name</c>, most first: at most a hundred applications. Only with
    /// <see cref="PostgresDiagnosticsRequest.IncludeActivity" />.
    /// </summary>
    public IReadOnlyList<PostgresConnectionCounts> ByApplication { get; init; } = [];

    /// <summary>
    /// Whether the role saw every session's state (<c>pg_read_all_stats</c>, or no other role's session was connected). When
    /// not, the states are a floor and <see cref="PostgresConnectionCounts.StateUnknown" /> counts the rest; the totals are
    /// exact either way.
    /// </summary>
    public bool AllSessionsVisible { get; init; }
}
