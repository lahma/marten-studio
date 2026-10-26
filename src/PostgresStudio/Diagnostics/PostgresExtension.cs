namespace PostgresStudio;

/// <summary>One extension the studio uses (or <c>track_io_timing</c>), and whether it can use it.</summary>
public sealed record PostgresExtension
{
    /// <summary>The extension, or the setting for <c>track_io_timing</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The installed version, or null.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>The server's default version, or null.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>Whether the studio can use it: installed, and preloaded or granted where it must be.</summary>
    public bool Usable { get; init; }

    /// <summary>What stands in the way, in the studio's words, naming the grant or setting; null when nothing does.</summary>
    public string? Note { get; init; }
}
