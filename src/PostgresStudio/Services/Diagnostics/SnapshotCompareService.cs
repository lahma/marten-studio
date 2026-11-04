using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>Compares a snapshot file the visitor chose with what their pages show now (1.2-H-B).</summary>
internal interface ISnapshotCompareService
{
    /// <summary>
    /// Compares the file <paramref name="openFile" /> opens with the visitor's current view of <paramref name="scope" />. The visitor's
    /// current view is built first, as Download JSON builds it; when it is refused the file is never opened, so it never starts
    /// crossing the circuit.
    /// </summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="openFile">
    /// Opens the snapshot file's bytes, called once and only past the visitor's gate. Read once, to at most
    /// <see cref="SnapshotFileReader.MaxBytes" />, disposed and not kept.
    /// </param>
    /// <param name="fileName">The file's name, for the heading; kept to 120 characters.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    Task<SnapshotComparison> CompareAsync(StudioScope scope, Func<CancellationToken, Stream> openFile, string? fileName, CancellationToken cancellationToken = default);
}

/// <summary>
/// The snapshot compare (1.2-H-B, D-HEALTH C3): the visitor's gate first, then the file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not audited, not logged, not cached.</b> It reads nothing a download does not (<see cref="VisitorSnapshotBuilder" />), and
/// the file is the visitor's own: no logger, no <c>StudioActionLog</c>, no cache, and no field that could hold a stream, bytes or
/// a snapshot between calls - only the two services it asks. <c>SnapshotCompareServiceTests</c> holds all of that structurally.
/// Paste-to-compare on the Connection page is not audited either.
/// </para>
/// <para>
/// <b>The current view is the authority.</b> The comparer says nothing about "now" that the visitor's own gated view would not
/// show (<see cref="SnapshotComparer" />), and asks no policy with a value from the file.
/// </para>
/// </remarks>
internal sealed class SnapshotCompareService : ISnapshotCompareService
{
    private readonly VisitorSnapshotBuilder builder;
    private readonly IStudioSettings settings;

    /// <param name="builder">The visitor's gated view, as Download JSON builds it.</param>
    /// <param name="settings">The studio's settings, for whether the host hides document types.</param>
    public SnapshotCompareService(VisitorSnapshotBuilder builder, IStudioSettings settings)
    {
        this.builder = builder;
        this.settings = settings;
    }

    /// <inheritdoc />
    public async Task<SnapshotComparison> CompareAsync(StudioScope scope, Func<CancellationToken, Stream> openFile, string? fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(openFile);

        VisitorView view = await builder.BuildViewAsync(scope, cancellationToken).ConfigureAwait(false);

        if (view.Report.Refusal != HealthRefusal.None || view.Snapshot.Outcome != PostgresReadOutcome.Read)
        {
            // The visitor is short of the gate for this database: nothing is compared, and the file is never read.
            return SnapshotComparison.Refused(
                view.Report.Reason ?? view.Snapshot.Reason ?? "Your account is not shown this database's health, so there is nothing to compare it with.",
                fileName);
        }

        // Only now does the file start crossing the circuit.
        await using Stream file = openFile(cancellationToken);
        SnapshotFileRead read = await SnapshotFileReader.ReadAsync(file, cancellationToken).ConfigureAwait(false);

        if (read.Snapshot is not { } earlier)
        {
            return SnapshotComparison.Unreadable(read.Problem ?? "This file is not a snapshot this studio can read.", fileName);
        }

        return SnapshotComparer.Compare(earlier, view, fileName, read.SkippedItems, settings.IsDocumentTypeVisible is not null);
    }
}
