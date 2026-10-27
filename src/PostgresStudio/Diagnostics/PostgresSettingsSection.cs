namespace PostgresStudio;

/// <summary>The curated settings and the extensions' status: numbers about the server as a whole.</summary>
public sealed record PostgresSettingsSection
{
    /// <summary>Whether it was read; when not, the other properties keep their defaults.</summary>
    public PostgresReadOutcome Outcome { get; init; }

    /// <summary>Why not, or which part could not be read.</summary>
    public string? Reason { get; init; }

    /// <summary>When it was read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The curated settings, by group in the order of <see cref="PostgresSetting.Group" />'s vocabulary.</summary>
    public IReadOnlyList<PostgresSetting> Settings { get; init; } = [];

    /// <summary>The extensions the studio uses, and <c>track_io_timing</c>.</summary>
    public IReadOnlyList<PostgresExtension> Extensions { get; init; } = [];

    /// <summary>
    /// Whether the server checks page checksums (<c>data_checksums</c>), or null when the studio could not read the setting.
    /// Chosen when a cluster is made; on by default from PostgreSQL 18.
    /// </summary>
    public bool? DataChecksums { get; init; }

    /// <summary>
    /// The pages this database has read whose checksum did not match (<c>pg_stat_database.checksum_failures</c>), since
    /// the database's statistics last began; null when checksums are off or the studio could not read the count. Any
    /// number above zero is corruption.
    /// </summary>
    public long? ChecksumFailures { get; init; }

    /// <summary>When the latest of those was counted (<c>checksum_last_failure</c>), or null when there has been none.</summary>
    public DateTimeOffset? LastChecksumFailure { get; init; }
}
