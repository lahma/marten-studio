namespace PostgresStudio;

/// <summary>The server a database lives on, as one read found it.</summary>
public sealed record PostgresServerInfo
{
    /// <summary><c>server_version_num</c>: 170004 for 17.4.</summary>
    public int VersionNumber { get; init; }

    /// <summary><c>server_version</c>, as the server spells it.</summary>
    public string Version { get; init; } = "";

    /// <summary>Who runs it, as far as the studio can tell.</summary>
    public PostgresPlatformKind Platform { get; init; }

    /// <summary>Which instance answered: <c>aurora_db_instance_identifier()</c> on Aurora, else <see langword="null" />.</summary>
    public string? Instance { get; init; }

    /// <summary><c>pg_is_in_recovery()</c>: a standby or a reader answered.</summary>
    public bool InRecovery { get; init; }

    /// <summary>
    /// <c>pg_postmaster_start_time()</c>. A change between readings means a restart or a failover, so the server's
    /// counters started again.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }
}
