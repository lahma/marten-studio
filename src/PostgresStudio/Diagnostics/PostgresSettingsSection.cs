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
}
