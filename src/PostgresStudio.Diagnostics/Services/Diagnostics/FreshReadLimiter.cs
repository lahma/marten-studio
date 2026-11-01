namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// How many fresh reads the diagnostics API may have running at once in the process: two (API design §3.4, API10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Misses only.</b> A call whose sweep or section the studio's caches already hold - one finished and fresh, or one running
/// that the call joins - takes no permit and never waits here; only a read the call would start does. So a host's
/// careless <c>Task.WhenAll</c> over five hundred tenants on one server costs two connections at a time, not five hundred
/// against <c>max_connections</c>, and its cluster page, reading what the caches hold, is never queued behind that batch.
/// </para>
/// <para>
/// <b>Never the pages'.</b> A page's sweep and a page's facts read do not come through here, so a visitor is never queued
/// behind a host's batch.
/// </para>
/// <para>
/// <b>A permit is the read's, not the caller's.</b> The read runs under no caller's token, as every cache here runs its work,
/// and its permit comes back when it finishes - never when the caller that started it gives up waiting. A caller that cancels
/// stops waiting at once; the read it started goes on holding its connection, and so its permit, so callers that cancel and
/// call again still have at most two reads running.
/// </para>
/// </remarks>
internal sealed class FreshReadLimiter : IDisposable
{
    /// <summary>How many fresh reads may run at once.</summary>
    public const int DefaultConcurrency = 2;

    private readonly SemaphoreSlim permits;
    private int running;
    private int mostRunning;
    private bool disposed;

    /// <param name="concurrency">How many fresh reads may run at once; <see cref="DefaultConcurrency" /> in a host.</param>
    public FreshReadLimiter(int concurrency = DefaultConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        Concurrency = concurrency;
        permits = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <summary>How many fresh reads may run at once.</summary>
    public int Concurrency { get; }

    /// <summary>The most that have run at once since the process began, for a test.</summary>
    internal int MostRunning => Volatile.Read(ref mostRunning);

    /// <summary>
    /// Runs <paramref name="read" /> - a read the caches do not hold - once a permit is free; or at once, without a permit,
    /// when <paramref name="cached" /> says the caches hold it.
    /// </summary>
    /// <typeparam name="T">What the read answers.</typeparam>
    /// <param name="cached">Whether the read would be answered from a cache, asked before a permit is taken.</param>
    /// <param name="read">The read.</param>
    /// <param name="cancellationToken">
    /// Cancels this caller's wait for a permit, and its wait for the read - never the read itself once it has started, which is
    /// handed <see cref="CancellationToken.None" /> and keeps its permit until it finishes.
    /// </param>
    public async Task<T> RunAsync<T>(bool cached, Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (cached)
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }

        await permits.WaitAsync(cancellationToken).ConfigureAwait(false);

        int now = Interlocked.Increment(ref running);
        for (int seen = Volatile.Read(ref mostRunning); now > seen; seen = Volatile.Read(ref mostRunning))
        {
            if (Interlocked.CompareExchange(ref mostRunning, now, seen) == seen)
            {
                break;
            }
        }

        // The read runs under no caller's token and holds its permit until it finishes, however soon the caller stops waiting:
        // a read a cancelled caller started still holds a connection, so releasing its permit early would let the next caller
        // start a third (W5-API-fix F2).
        Task<T> work;

        try
        {
            work = read(CancellationToken.None);
        }
        catch
        {
            Done();
            throw;
        }

        _ = work.ContinueWith(
            static (_, state) => ((FreshReadLimiter) state!).Done(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Volatile.Write(ref disposed, true);
        permits.Dispose();
    }

    /// <summary>A read finished, however it ended: its permit goes back - unless the process is shutting the limiter down.</summary>
    private void Done()
    {
        Interlocked.Decrement(ref running);

        if (Volatile.Read(ref disposed))
        {
            return;
        }

        try
        {
            permits.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the release, at shutdown: nobody is waiting for the permit any more.
        }
    }
}
