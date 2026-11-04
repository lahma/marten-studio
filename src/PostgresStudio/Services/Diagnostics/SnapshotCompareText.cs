using System.Globalization;

using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The compare drawer's words (1.2-H-B): a comparison's kinds, severities and numbers as text, so the component that draws them
/// names none of the read model's types (<c>DiagnosticsApiIsNotServedTests</c>).
/// </summary>
internal static class SnapshotCompareText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The kind of a finding's change in words.</summary>
    /// <param name="kind">The kind.</param>
    public static string Kind(SnapshotChangeKind kind) => kind switch
    {
        SnapshotChangeKind.New => "New",
        SnapshotChangeKind.Worse => "Worse",
        SnapshotChangeKind.Better => "Better",
        SnapshotChangeKind.Unchanged => "Unchanged",
        SnapshotChangeKind.NoLongerReported => "No longer reported",
        SnapshotChangeKind.NowAccepted => "Now accepted",
        SnapshotChangeKind.NoLongerAccepted => "No longer accepted",
        _ => "Changed",
    };

    /// <summary>The style class of a finding's kind.</summary>
    /// <param name="kind">The kind.</param>
    public static string KindClass(SnapshotChangeKind kind) => kind switch
    {
        SnapshotChangeKind.New => "ms-snapshot-compare-kind-new",
        SnapshotChangeKind.Worse => "ms-snapshot-compare-kind-worse",
        SnapshotChangeKind.Better => "ms-snapshot-compare-kind-better",
        SnapshotChangeKind.NoLongerReported => "ms-snapshot-compare-kind-gone",
        _ => "ms-snapshot-compare-kind-same",
    };

    /// <summary>The kind of a table's change in words.</summary>
    /// <param name="kind">The kind.</param>
    public static string Kind(SnapshotTableChangeKind kind) => kind switch
    {
        SnapshotTableChangeKind.New => "New among the largest",
        SnapshotTableChangeKind.Gone => "No longer among the largest",
        _ => "Changed",
    };

    /// <summary>The style class of a table's kind.</summary>
    /// <param name="kind">The kind.</param>
    public static string KindClass(SnapshotTableChangeKind kind) => kind switch
    {
        SnapshotTableChangeKind.New => "ms-snapshot-compare-kind-new",
        SnapshotTableChangeKind.Gone => "ms-snapshot-compare-kind-gone",
        _ => "ms-snapshot-compare-kind-same",
    };

    /// <summary>The kind of a statement's change in words.</summary>
    /// <param name="kind">The kind.</param>
    public static string Kind(SnapshotStatementChangeKind kind) => kind switch
    {
        SnapshotStatementChangeKind.New => "New among the top",
        SnapshotStatementChangeKind.Gone => "No longer among the top",
        _ => "Changed",
    };

    /// <summary>The style class of a statement's kind.</summary>
    /// <param name="kind">The kind.</param>
    public static string KindClass(SnapshotStatementChangeKind kind) => kind switch
    {
        SnapshotStatementChangeKind.New => "ms-snapshot-compare-kind-new",
        SnapshotStatementChangeKind.Gone => "ms-snapshot-compare-kind-gone",
        _ => "ms-snapshot-compare-kind-same",
    };

    /// <summary>A finding's severity before and now: <c>Warning to Critical</c>, or the one it has.</summary>
    /// <param name="change">The finding's change.</param>
    public static string Severity(SnapshotFindingChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return (change.SeverityBefore, change.SeverityNow) switch
        {
            ({ } before, { } now) when before != now => before + " to " + now,
            (_, { } now) => now.ToString(),
            ({ } before, _) => before.ToString(),
            _ => string.Empty,
        };
    }

    /// <summary>A size.</summary>
    /// <param name="bytes">The bytes, or <see langword="null" />.</param>
    public static string Bytes(long? bytes) => bytes is { } value ? TableText.Bytes(value) : "-";

    /// <summary>A time in milliseconds.</summary>
    /// <param name="ms">The time, or <see langword="null" />.</param>
    public static string Milliseconds(double? ms) => ms is { } value ? value.ToString("N1", Invariant) + " ms" : "-";

    /// <summary>A share of time.</summary>
    /// <param name="share">0 to 1, or <see langword="null" />.</param>
    public static string Share(double? share) => share is { } value ? TableText.Percent(value) : "-";

    /// <summary>A rate.</summary>
    /// <param name="perHour">Calls an hour, or <see langword="null" />.</param>
    public static string Rate(double? perHour) => perHour is { } value ? value.ToString("N0", Invariant) + " an hour" : "-";
}
