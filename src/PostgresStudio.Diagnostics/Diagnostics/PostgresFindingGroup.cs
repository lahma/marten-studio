namespace PostgresStudio;

/// <summary>
/// One line per check, as the studio's Health page lists a database's findings: every finding one check raised, together.
/// A check that found thirty redundant indexes is one group of thirty, "Redundant indexes (30)", not thirty lines
/// (<see cref="PostgresDiagnosticsSnapshot.FindingsByCheck" />).
/// </summary>
/// <remarks>
/// A result, not a seam: every property has a safe default and none is required, so a property added in a later release
/// breaks nothing. Key your own wording on <see cref="Code" />, as for <see cref="PostgresFinding" />.
/// </remarks>
public sealed record PostgresFindingGroup
{
    /// <summary>The worst severity among the group's findings.</summary>
    public PostgresSeverity Severity { get; init; }

    /// <summary>The check's code: a letter and two digits (<c>T07</c>), the same as every finding in the group.</summary>
    public string Code { get; init; } = "";

    /// <summary>The check's area.</summary>
    public PostgresFindingCategory Category { get; init; }

    /// <summary>
    /// What to call the line: the check's own title when <see cref="Count" /> is above one (it names the kind of thing, not
    /// one of them), and the one finding's title when <see cref="Count" /> is one.
    /// </summary>
    public string Title { get; init; } = "";

    /// <summary>What the one finding is about, by name, when <see cref="Count" /> is one; otherwise null.</summary>
    public string? Subject { get; init; }

    /// <summary>Why the one finding matters, in English, when <see cref="Count" /> is one; otherwise null.</summary>
    public string? Detail { get; init; }

    /// <summary>How many findings the check raised.</summary>
    public int Count { get; init; }

    /// <summary>
    /// Where to read more, relative to the studio's mount path with no leading slash (<see cref="PostgresStudioLink" />): the
    /// one finding's card on the Health page when <see cref="Count" /> is one, and the database's Health page when it is more
    /// - the path <see cref="IPostgresDiagnostics.GetStudioPath" /> returns for <see cref="PostgresStudioPage.Health" />.
    /// Empty for findings built outside the studio.
    /// </summary>
    public string StudioPath { get; init; } = "";

    /// <summary>The group's findings, in the snapshot's order.</summary>
    public IReadOnlyList<PostgresFinding> Findings { get; init; } = [];
}
