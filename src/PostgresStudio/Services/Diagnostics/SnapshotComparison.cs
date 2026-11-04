namespace PostgresStudio.Services.Diagnostics;

/// <summary>How a compare of a snapshot file with the visitor's current view came out.</summary>
internal enum SnapshotCompareOutcome
{
    /// <summary>The file was read and compared.</summary>
    Compared,

    /// <summary>The visitor's own current view is short of the gate (or absent), so nothing was compared and the file was never read.</summary>
    Refused,

    /// <summary>The file is not a snapshot this studio can read.</summary>
    Unreadable,
}

/// <summary>What happened to a finding between the two snapshots.</summary>
internal enum SnapshotChangeKind
{
    /// <summary>Reported now, not in the earlier snapshot.</summary>
    New,

    /// <summary>In both, and more severe now.</summary>
    Worse,

    /// <summary>In both, and less severe now.</summary>
    Better,

    /// <summary>In both, at the same severity.</summary>
    Unchanged,

    /// <summary>In the earlier snapshot, and the visitor's current view would show it were it there - and does not.</summary>
    NoLongerReported,

    /// <summary>In both, accepted now and not before.</summary>
    NowAccepted,

    /// <summary>In both, accepted before and not now.</summary>
    NoLongerAccepted,
}

/// <summary>One finding's change. Its title is the current view's whenever the finding is in the current view.</summary>
/// <param name="Kind">What happened to it.</param>
/// <param name="Code">The check's code.</param>
/// <param name="Variant">The variant, when the check has them.</param>
/// <param name="Subject">What it is about, when it is about one thing.</param>
/// <param name="Title">The title, now's where there is one.</param>
/// <param name="SeverityBefore">The earlier severity, when the earlier snapshot had it.</param>
/// <param name="SeverityNow">The current severity, when the current view has it.</param>
internal sealed record SnapshotFindingChange(
    SnapshotChangeKind Kind,
    string Code,
    string? Variant,
    string? Subject,
    string Title,
    PostgresSeverity? SeverityBefore,
    PostgresSeverity? SeverityNow);

/// <summary>
/// Something the compare did not (or could not) say, and why: a count and the reason, never the items. A section the visitor is
/// short of now is here with the current view's own sentence, and nothing of the file's content for it is drawn.
/// </summary>
/// <param name="What">What was left out: a section's name, or "Earlier findings".</param>
/// <param name="Count">How many items; zero when it is a whole section.</param>
/// <param name="Why">The reason.</param>
internal sealed record SnapshotNotCompared(string What, int Count, string Why);

/// <summary>One value that changed: what it is, what it was and what it is.</summary>
/// <param name="Label">What it is - the studio's own words, or a name the current view also shows.</param>
/// <param name="Before">The earlier value, or <see langword="null" /> when there is none to draw.</param>
/// <param name="Now">The current value, or <see langword="null" /> when there is none to draw.</param>
/// <param name="Note">A word on the change.</param>
internal sealed record SnapshotValueChange(string Label, string? Before, string? Now, string? Note = null);

/// <summary>What happened to a table between the two snapshots.</summary>
internal enum SnapshotTableChangeKind
{
    /// <summary>In both, and its size moved.</summary>
    Changed,

    /// <summary>Among the largest now and not before.</summary>
    New,

    /// <summary>Among the largest before, and the current view would list it - and does not.</summary>
    Gone,
}

/// <summary>One table's change.</summary>
/// <param name="Kind">What happened to it.</param>
/// <param name="Table"><c>schema.name</c>.</param>
/// <param name="BytesBefore">Its earlier total size.</param>
/// <param name="BytesNow">Its current total size.</param>
internal sealed record SnapshotTableChange(SnapshotTableChangeKind Kind, string Table, long? BytesBefore, long? BytesNow);

/// <summary>What happened to a statement between the two snapshots.</summary>
internal enum SnapshotStatementChangeKind
{
    /// <summary>In both, and its mean time or its share moved.</summary>
    Changed,

    /// <summary>Among the top now and not before.</summary>
    New,

    /// <summary>Among the top before, and not now: by <c>queryid</c> and the earlier numbers only, never its text.</summary>
    Gone,
}

/// <summary>One statement's change. The text is the current view's, masked as the page masks it, and never the file's.</summary>
/// <param name="Kind">What happened to it.</param>
/// <param name="QueryId">The statement's <c>queryid</c>.</param>
/// <param name="Text">The current view's text; <see langword="null" /> for a statement that is gone, or one whose text the page withholds.</param>
/// <param name="MeanBefore">Its earlier mean time, in milliseconds.</param>
/// <param name="MeanNow">Its current mean time, in milliseconds.</param>
/// <param name="ShareBefore">Its earlier share of the time, 0 to 1.</param>
/// <param name="ShareNow">Its current share of the time, 0 to 1.</param>
/// <param name="CallsPerHourBefore">Its earlier calls an hour, when the earlier window is known.</param>
/// <param name="CallsPerHourNow">Its current calls an hour, when both windows are known.</param>
internal sealed record SnapshotStatementChange(
    SnapshotStatementChangeKind Kind,
    long QueryId,
    string? Text,
    double? MeanBefore,
    double? MeanNow,
    double? ShareBefore,
    double? ShareNow,
    double? CallsPerHourBefore,
    double? CallsPerHourNow);

/// <summary>
/// An earlier snapshot file compared with what the visitor's pages show now (1.2-H-B, D-HEALTH C6 and C7). It holds derived
/// values and counts only: not the file, not a stream, not the snapshot it was read into. A thing the earlier file names that
/// the visitor's current view does not show is counted, never described.
/// </summary>
internal sealed record SnapshotComparison
{
    /// <summary>How the compare came out.</summary>
    public SnapshotCompareOutcome Outcome { get; init; }

    /// <summary>The one sentence a refused or unreadable compare gives.</summary>
    public string? Reason { get; init; }

    /// <summary>The file's name, kept to 120 characters.</summary>
    public string? FileName { get; init; }

    /// <summary>When the earlier snapshot was written.</summary>
    public DateTimeOffset? EarlierAt { get; init; }

    /// <summary>When the current view was built.</summary>
    public DateTimeOffset? NowAt { get; init; }

    /// <summary>What wrote the earlier file ("MartenStudio 1.2.0"), kept to 512 characters.</summary>
    public string? EarlierProducer { get; init; }

    /// <summary>The verdict, in a line: how many findings are new, worse, better or gone, and what else moved.</summary>
    public string Verdict { get; init; } = string.Empty;

    /// <summary>Notices: another database, another product, an earlier snapshot that read nothing.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>The findings that changed, and the ones that did not (<see cref="SnapshotChangeKind.Unchanged" />).</summary>
    public IReadOnlyList<SnapshotFindingChange> Findings { get; init; } = [];

    /// <summary>What was left out, and why.</summary>
    public IReadOnlyList<SnapshotNotCompared> NotCompared { get; init; } = [];

    /// <summary>The server: its version, platform, start, recovery state and data checksums.</summary>
    public IReadOnlyList<SnapshotValueChange> Server { get; init; } = [];

    /// <summary>The curated settings that changed, appeared or went.</summary>
    public IReadOnlyList<SnapshotValueChange> Settings { get; init; } = [];

    /// <summary>The extensions that changed, appeared or went.</summary>
    public IReadOnlyList<SnapshotValueChange> Extensions { get; init; } = [];

    /// <summary>The database's size and growth.</summary>
    public IReadOnlyList<SnapshotValueChange> Storage { get; init; } = [];

    /// <summary>The tables whose size moved, and the ones that came or went.</summary>
    public IReadOnlyList<SnapshotTableChange> Tables { get; init; } = [];

    /// <summary>The statements that moved, and the ones that came or went.</summary>
    public IReadOnlyList<SnapshotStatementChange> Statements { get; init; } = [];

    /// <summary>The write-ahead log written between the two snapshots, or the sentence that says it is not known.</summary>
    public IReadOnlyList<SnapshotValueChange> Wal { get; init; } = [];

    /// <summary>The connection limits and the database's connections.</summary>
    public IReadOnlyList<SnapshotValueChange> Connections { get; init; } = [];

    /// <summary>The application's connection settings, compared by keyword as the Connection page exposes each.</summary>
    public IReadOnlyList<SnapshotValueChange> Connection { get; init; } = [];

    /// <summary>How many items named a value this studio does not know, and were left out of the file's reading.</summary>
    public int SkippedItems { get; init; }

    /// <summary>A compare that did not happen because the visitor's own view is short of the gate.</summary>
    /// <param name="reason">The view's own sentence.</param>
    /// <param name="fileName">The file's name.</param>
    public static SnapshotComparison Refused(string reason, string? fileName) =>
        new() { Outcome = SnapshotCompareOutcome.Refused, Reason = reason, FileName = SnapshotComparer.Cap(fileName, 120) };

    /// <summary>A file this studio could not read.</summary>
    /// <param name="problem">The reader's sentence.</param>
    /// <param name="fileName">The file's name.</param>
    public static SnapshotComparison Unreadable(string problem, string? fileName) =>
        new() { Outcome = SnapshotCompareOutcome.Unreadable, Reason = problem, FileName = SnapshotComparer.Cap(fileName, 120) };
}
