namespace PostgresStudio;

/// <summary>
/// A check: in <see cref="IPostgresDiagnostics.Checks" /> its description, in a snapshot also how its last run ended.
/// </summary>
public sealed record PostgresCheck
{
    /// <summary>The code: a letter and two digits, the letter its area's. Stable: never reused, never renamed.</summary>
    public string Code { get; init; } = "";

    /// <summary>Its area: the code's letter.</summary>
    public PostgresFindingCategory Category { get; init; }

    /// <summary>What it looks for, in English; also its docs heading.</summary>
    public string Title { get; init; } = "";

    /// <summary>Its variants, frozen; empty for a check with one kind of finding, whose findings carry none.</summary>
    public IReadOnlyList<string> Variants { get; init; } = [];

    /// <summary>
    /// Whether its findings are Activity-grade - other people's sessions and statements - and so need
    /// <see cref="PostgresDiagnosticsRequest.IncludeActivity" />.
    /// </summary>
    public bool RequiresActivity { get; init; }

    /// <summary>The check's documentation, pinned to this release.</summary>
    public string DocsUrl { get; init; } = "";

    /// <summary>
    /// How its last run ended: <see cref="PostgresReadOutcome.Read" /> when it judged what it read, with or without findings;
    /// <see cref="PostgresReadOutcome.Withheld" /> for an Activity-grade check the request did not include. Null in the
    /// catalogue.
    /// </summary>
    public PostgresReadOutcome? Outcome { get; init; }

    /// <summary>Why it did not check, in the studio's words. A missing grant is named, with who can give it.</summary>
    public string? Reason { get; init; }

    /// <summary>The SQLSTATE, when PostgreSQL refused. PostgreSQL's own words are not carried.</summary>
    public string? SqlState { get; init; }

    /// <summary>How many of its findings the snapshot includes.</summary>
    public int Findings { get; init; }
}
