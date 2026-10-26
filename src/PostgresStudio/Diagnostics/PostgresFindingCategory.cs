namespace PostgresStudio;

/// <summary>
/// A check's area: the code's letter. May gain members, each with a letter of its own; treat an unknown value as a category
/// you do not group.
/// </summary>
public enum PostgresFindingCategory
{
    /// <summary>Marten's async daemon, its shards and the high-water mark (<c>D</c> codes, Marten Studio).</summary>
    Daemon = 0,

    /// <summary>The event store's traffic and shape (<c>E</c> codes, Marten Studio).</summary>
    Events = 1,

    /// <summary>Tables and their indexes (<c>T</c> codes).</summary>
    Tables = 2,

    /// <summary>Connections, sessions and locks (<c>C</c> codes).</summary>
    Connections = 3,

    /// <summary>Replication slots, standbys, archiving and checkpoints (<c>R</c> codes).</summary>
    Replication = 4,

    /// <summary>The server itself: its version, its settings, its security posture (<c>S</c> codes).</summary>
    Server = 5,

    /// <summary>The application's own configuration, and the studio's (<c>K</c> codes).</summary>
    Configuration = 6,
}
