namespace PostgresStudio;

/// <summary>What a snapshot should contain. Start from <see cref="FindingsOnly" /> or <see cref="AllSections" />.</summary>
/// <remarks>
/// A record with <c>init</c> properties, so a call names only what it changes:
/// <c>PostgresDiagnosticsRequest.FindingsOnly with { Storage = true }</c>. A later release adds a section as a new property
/// whose default leaves it out.
/// </remarks>
public sealed record PostgresDiagnosticsRequest
{
    /// <summary>The findings and every check's outcome. On by default.</summary>
    public bool Findings { get; init; } = true;

    /// <summary>Database size, growth and the largest tables.</summary>
    public bool Storage { get; init; }

    /// <summary>
    /// The top statements by execution time. Activity-grade: returned only with <see cref="IncludeActivity" />. Read from the
    /// statement checks' last run; with <see cref="RunDeeperChecks" /> a run is started when none is cached.
    /// </summary>
    public bool Statements { get; init; }

    /// <summary>The curated settings and the extensions' status.</summary>
    public bool Settings { get; init; }

    /// <summary>Connections by state, with headroom; by application only with <see cref="IncludeActivity" />.</summary>
    public bool Connections { get; init; }

    /// <summary>
    /// Include what the studio shows only past its activity gate: statement texts and the statements section, application
    /// names, other sessions' evidence, and findings about one tenant. Off by default; without it each is counted as
    /// withheld.
    /// </summary>
    public bool IncludeActivity { get; init; }

    /// <summary>
    /// Run the checks that read a bounded slice of the data, and with <see cref="IncludeActivity" /> the statement checks,
    /// when no recent run is cached. Otherwise a recent run is included if there is one.
    /// </summary>
    public bool RunDeeperChecks { get; init; }

    /// <summary>The findings alone, with no Activity-grade data.</summary>
    public static PostgresDiagnosticsRequest FindingsOnly { get; } = new();

    /// <summary>
    /// Every section this version of the studio has, with no Activity-grade data. It is a property, so a newer studio adds its
    /// new sections for an old caller too.
    /// </summary>
    public static PostgresDiagnosticsRequest AllSections { get; } = new()
    {
        Storage = true,
        Statements = true,
        Settings = true,
        Connections = true,
    };
}
