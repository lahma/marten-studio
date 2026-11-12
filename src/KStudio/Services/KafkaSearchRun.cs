using KStudio.Core.Scanning;

namespace KStudio.Services;

/// <summary>
/// A running search as the page holds it (D-UI decision 11): the engine's handle, and the once-only audit of the scan. The scan's entry (9600 and the
/// ring) is written exactly once, by whichever comes first - the page receiving the final batch (<see cref="Complete" />) or the page being disposed
/// (<see cref="DisposeAsync" />, which waits for the result the engine reached) - so a visitor who closes the tab mid-scan still leaves their entry. There
/// is no fire-and-forget task.
/// </summary>
internal sealed class KafkaSearchRun : IAsyncDisposable
{
    private readonly KafkaScanHandle handle;
    private readonly Action<KafkaScanResult?> record;
    private int recorded;
    private int disposed;

    /// <summary>Creates a run over a started scan.</summary>
    /// <param name="handle">The engine's handle.</param>
    /// <param name="plan">The plan it runs.</param>
    /// <param name="record">Writes the scan's audit entry; called at most once, with <see langword="null" /> when the scan produced no result.</param>
    public KafkaSearchRun(KafkaScanHandle handle, KafkaScanPlan plan, Action<KafkaScanResult?> record)
    {
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.record = record ?? throw new ArgumentNullException(nameof(record));
    }

    /// <summary>The plan this run executes.</summary>
    public KafkaScanPlan Plan { get; }

    /// <summary>Whether the audit entry was written.</summary>
    public bool IsRecorded => Volatile.Read(ref recorded) != 0;

    /// <summary>The engine's batches, at most four a second, the last carrying the result. They can be read once.</summary>
    /// <param name="cancellationToken">Ends the enumeration, not the scan.</param>
    public IAsyncEnumerable<KafkaScanBatch> ReadBatchesAsync(CancellationToken cancellationToken) => handle.ReadBatchesAsync(cancellationToken);

    /// <summary>Stops the scan; the result keeps what was read and the cursor.</summary>
    public void Stop() => handle.Stop();

    /// <summary>Writes the scan's audit entry, if no one has.</summary>
    /// <param name="result">The final result.</param>
    public void Complete(KafkaScanResult? result)
    {
        if (Interlocked.Exchange(ref recorded, 1) != 0)
        {
            return;
        }

        record(result);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        KafkaScanResult? result = null;

        try
        {
            await handle.DisposeAsync().ConfigureAwait(false);

            if (handle.Result.IsCompletedSuccessfully)
            {
                result = handle.Result.Result;
            }
        }
        finally
        {
            Complete(result);
        }
    }
}
