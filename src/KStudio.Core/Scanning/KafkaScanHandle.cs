using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace KStudio.Core.Scanning;

/// <summary>
/// What a caller holds of a running scan (decision 9.7). Batches arrive through a channel of capacity one, written by a batcher task that
/// wakes every 250 ms on the <see cref="TimeProvider" /> (so at most four batches a second) and takes the matches added since the last
/// batch and a copy of the progress. The scan's thread never writes to the channel, so a slow reader of batches waits only the batcher and
/// rendering never paces the reader.
/// </summary>
/// <remarks>
/// The contract for a component: start reading after the first interactive render, <c>InvokeAsync(StateHasChanged)</c> once per batch,
/// and dispose the handle in <c>DisposeAsync</c>. Disposing a running scan stops it with <see cref="KafkaScanStopReason.CallerGone" />
/// and waits for it for at most ten seconds.
/// </remarks>
internal sealed class KafkaScanHandle : IAsyncDisposable
{
    /// <summary>How often the batcher looks for something to hand over.</summary>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long disposing waits for a running scan to stop.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly ScanRun run;
    private readonly Task<KafkaScanResult> scanTask;
    private readonly TimeProvider time;
    private readonly Channel<KafkaScanBatch> channel = Channel.CreateBounded<KafkaScanBatch>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });

    private readonly CancellationTokenSource batcherStop = new();
    private readonly Task batcher;
    private int reading;
    private int disposed;

    internal KafkaScanHandle(ScanRun run, Task<KafkaScanResult> scanTask, TimeProvider time)
    {
        this.run = run;
        this.scanTask = scanTask;
        this.time = time;

        // The timer exists before the constructor returns, so before the caller can do anything the clock could answer. Created inside the task
        // (as it was until KS-CI-FIX-2) it was armed whenever the thread pool got to it, and a clock that moved in between had already gone past
        // its first tick: a manual clock lost the tick for good, and a loaded host delayed the first batch by however long the pool took.
        PeriodicTimer timer = new(BatchInterval, time);
        batcher = Task.Run(() => BatchLoopAsync(timer));
    }

    /// <summary>The scan's id.</summary>
    public Guid ScanId => run.ScanId;

    /// <summary>The scan's result, once it has stopped. It never faults: a failure is a <see cref="KafkaScanStopReason.Failed" /> result.</summary>
    public Task<KafkaScanResult> Result => scanTask;

    /// <summary>Whether the scan is still reading.</summary>
    public bool IsRunning => !scanTask.IsCompleted;

    /// <summary>A copy of the progress.</summary>
    public KafkaScanProgress Progress => run.Snapshot();

    /// <summary>The matches kept so far.</summary>
    public IReadOnlyList<KafkaMatch> Matches => run.Store.From(0);

    /// <summary>Stops the scan with <see cref="KafkaScanStopReason.Cancelled" />; the result keeps what was read and the cursor.</summary>
    public void Stop() => run.RequestStop(KafkaScanStopReason.Cancelled);

    internal void Stop(KafkaScanStopReason reason) => run.RequestStop(reason);

    /// <summary>The batches, at most four a second, the last carrying the result. It can be read once.</summary>
    /// <param name="cancellationToken">Ends the enumeration, not the scan.</param>
    public async IAsyncEnumerable<KafkaScanBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0)
        {
            throw new InvalidOperationException("The batches of a scan can be read once.");
        }

        await foreach (KafkaScanBatch batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <summary>Stops the scan if it still runs, with <see cref="KafkaScanStopReason.CallerGone" />, and waits for it for at most ten seconds.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (!scanTask.IsCompleted)
        {
            run.RequestStop(KafkaScanStopReason.CallerGone);

            try
            {
                await scanTask.WaitAsync(StopTimeout, time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The scan thread will end on its own; the coordinator reports a scan that outlives its host.
            }
        }

        await batcherStop.CancelAsync().ConfigureAwait(false);

        try
        {
            await batcher.WaitAsync(StopTimeout, time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The batcher only waits for a tick or for a reader; it ends with the token it was just given.
        }

        batcherStop.Dispose();
    }

    private async Task BatchLoopAsync(PeriodicTimer timer)
    {
        long sentVersion = -1;
        int sentMatches = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(batcherStop.Token).ConfigureAwait(false))
            {
                bool finished = scanTask.IsCompleted;
                KafkaScanProgress progress = run.Snapshot();
                IReadOnlyList<KafkaMatch> fresh = run.Store.From(sentMatches);

                if (finished)
                {
                    KafkaScanResult result = await scanTask.ConfigureAwait(false);
                    progress = run.Snapshot();
                    fresh = run.Store.From(sentMatches);
                    await channel.Writer.WriteAsync(new KafkaScanBatch(fresh, progress, result), batcherStop.Token).ConfigureAwait(false);
                    return;
                }

                if (progress.Version != sentVersion || fresh.Count > 0)
                {
                    sentVersion = progress.Version;
                    sentMatches += fresh.Count;
                    await channel.Writer.WriteAsync(new KafkaScanBatch(fresh, progress, null), batcherStop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (batcherStop.IsCancellationRequested)
        {
            // The handle was disposed: nobody is left to read.
        }
        finally
        {
            timer.Dispose();
            channel.Writer.TryComplete();
        }
    }
}
