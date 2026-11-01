namespace PostgresStudio;

/// <summary>One curated server setting, as <c>pg_settings</c> shows it to the role the studio reads as.</summary>
public sealed record PostgresSetting
{
    /// <summary>The setting.</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// The curated group: <c>memory</c>, <c>connections</c>, <c>wal</c>, <c>vacuum</c>, <c>planner</c>, <c>io</c> or
    /// <c>statistics</c>. A frozen vocabulary, like the codes.
    /// </summary>
    public string Group { get; init; } = "";

    /// <summary>As <c>pg_settings.setting</c> shows it, or null where the role may not read it or the server lacks it.</summary>
    public string? Value { get; init; }

    /// <summary><c>pg_settings.unit</c>, or null.</summary>
    public string? Unit { get; init; }

    /// <summary><c>pg_settings.source</c>: <c>default</c>, <c>configuration file</c>, <c>database</c>, ...</summary>
    public string? Source { get; init; }

    /// <summary>Whether a change to it waits for the server to restart.</summary>
    public bool PendingRestart { get; init; }
}
