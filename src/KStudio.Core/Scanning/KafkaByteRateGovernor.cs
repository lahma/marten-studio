using KStudio.Core.Clusters;

namespace KStudio.Core.Scanning;

/// <summary>
/// The byte rate every scan in the process shares (AGENTS.md D41, decision 9.3): one token bucket on the <see cref="TimeProvider" />, with a
/// rate of <c>BytesPerSecond</c> and a capacity of one second of it. The scan thread pays for each record it has read; when the bucket is in
/// debt the thread stops calling <c>Consume</c> and waits out the debt, and librdkafka's bounded queue stops fetching, so the broker sends no
/// more than what was consumed plus the queue and the fetch size (SPIKE S4f: at most 10.1 MiB over the bytes consumed, against a bound of
/// <c>Q + F</c> = 16 MiB, with the designed limits). The governor paces consumption; it does not pause partitions.
/// </summary>
/// <remarks>
/// A scan may therefore read up to one second of rate at once (the bucket starts full), and then the rate holds: over any ten seconds after
/// the first second a scan's reads stay within ±10 % of the ceiling. The clock is read through <see cref="TimeProvider.GetUtcNow" />, so a
/// test drives it by hand.
/// </remarks>
internal sealed class KafkaByteRateGovernor
{
    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly double rate;
    private readonly double capacity;
    private double tokens;
    private long lastTicks;
    private int waiters;

    /// <summary>Creates the governor from the engine's validated limits.</summary>
    /// <param name="settings">The options as validated.</param>
    /// <param name="time">The clock.</param>
    public KafkaByteRateGovernor(KafkaEngineSnapshot settings, TimeProvider time)
    {
        this.time = time;
        rate = settings.Limits.BytesPerSecond;
        capacity = rate;
        tokens = capacity;
        lastTicks = time.GetUtcNow().UtcTicks;
    }

    /// <summary>The ceiling, in bytes per second.</summary>
    public long BytesPerSecond => (long) rate;

    /// <summary>How many threads are waiting out a debt right now: how a test knows the scan thread is in the wait and not in <c>Consume</c>.</summary>
    public int Waiters => Volatile.Read(ref waiters);

    /// <summary>Pays for bytes already read. The bucket may go negative: that is the debt.</summary>
    /// <param name="bytes">What was read.</param>
    public void Take(long bytes)
    {
        lock (gate)
        {
            Refill();
            tokens -= bytes;
        }
    }

    /// <summary>How long to wait before reading again: zero unless the bucket is in debt.</summary>
    public TimeSpan DebtWait()
    {
        lock (gate)
        {
            Refill();
            return tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-tokens / rate);
        }
    }

    /// <summary>
    /// Waits for <paramref name="duration" /> on the governor's clock, or until <paramref name="cancellation" /> is cancelled, without calling
    /// <c>Consume</c>. A manual clock ends it when it is advanced by the duration.
    /// </summary>
    /// <param name="duration">How long.</param>
    /// <param name="cancellation">Ends the wait early.</param>
    /// <returns><see langword="true" /> when the time elapsed; <see langword="false" /> when the token ended the wait.</returns>
    public bool Wait(TimeSpan duration, CancellationToken cancellation)
    {
        using ManualResetEventSlim elapsed = new(false);
        using ITimer timer = time.CreateTimer(static state => ((ManualResetEventSlim) state!).Set(), elapsed, duration, Timeout.InfiniteTimeSpan);

        Interlocked.Increment(ref waiters);

        try
        {
            return WaitHandle.WaitAny([elapsed.WaitHandle, cancellation.WaitHandle]) == 0;
        }
        finally
        {
            Interlocked.Decrement(ref waiters);
        }
    }

    private void Refill()
    {
        long now = time.GetUtcNow().UtcTicks;
        double seconds = (now - lastTicks) / (double) TimeSpan.TicksPerSecond;

        if (seconds > 0)
        {
            tokens = Math.Min(capacity, tokens + (seconds * rate));
            lastTicks = now;
        }
    }
}

/// <summary>
/// How many scans may run at once (AGENTS.md D41, decision 9.11): <c>MaxConcurrentScans</c> in the process and
/// <c>MaxConcurrentScansPerOwner</c> for one owner. A start takes a slot of each atomically or is refused; nothing queues.
/// </summary>
internal sealed class KafkaScanLimiter(KafkaEngineSnapshot settings)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, int> perOwner = new(StringComparer.Ordinal);
    private readonly int processLimit = settings.Limits.MaxConcurrentScans;
    private readonly int ownerLimit = settings.Limits.MaxConcurrentScansPerOwner;
    private int running;

    /// <summary>How many scans run now.</summary>
    public int Running
    {
        get
        {
            lock (gate)
            {
                return running;
            }
        }
    }

    /// <summary>Takes a process slot and an owner slot, or says which was full.</summary>
    /// <param name="owner">The owner's key.</param>
    /// <param name="lease">Releases both slots when disposed.</param>
    /// <returns><see langword="null" /> when the slots were taken; otherwise the reason.</returns>
    public KafkaScanRefusal? TryAcquire(string owner, out IDisposable? lease)
    {
        lock (gate)
        {
            perOwner.TryGetValue(owner, out int own);

            if (own >= ownerLimit)
            {
                lease = null;
                return KafkaScanRefusal.OwnerBusy;
            }

            if (running >= processLimit)
            {
                lease = null;
                return KafkaScanRefusal.ProcessBusy;
            }

            running++;
            perOwner[owner] = own + 1;
            lease = new Lease(this, owner);
            return null;
        }
    }

    private void Release(string owner)
    {
        lock (gate)
        {
            running--;

            if (perOwner.TryGetValue(owner, out int own))
            {
                if (own <= 1)
                {
                    perOwner.Remove(owner);
                }
                else
                {
                    perOwner[owner] = own - 1;
                }
            }
        }
    }

    private sealed class Lease(KafkaScanLimiter limiter, string owner) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                limiter.Release(owner);
            }
        }
    }
}

/// <summary>
/// The bytes one owner may read in any rolling hour (AGENTS.md D41, decision 9.11), on the <see cref="TimeProvider" />. Checked when a scan
/// starts and enforced while it reads; a sampling read for an estimate counts too.
/// </summary>
internal sealed class OwnerByteBudget(KafkaEngineSnapshot settings, TimeProvider time)
{
    private const long HourSeconds = 3_600;

    private readonly Lock gate = new();
    private readonly Dictionary<string, List<(long Second, long Bytes)>> reads = new(StringComparer.Ordinal);
    private readonly long budget = settings.Limits.OwnerBytesPerHour;

    /// <summary>The owner's hourly budget, in bytes.</summary>
    public long BytesPerHour => budget;

    /// <summary>How many bytes the owner may still read in the hour that ends now.</summary>
    /// <param name="owner">The owner's key.</param>
    public long Remaining(string owner)
    {
        lock (gate)
        {
            return Math.Max(0, budget - Used(owner));
        }
    }

    /// <summary>Records bytes the owner has read.</summary>
    /// <param name="owner">The owner's key.</param>
    /// <param name="bytes">The bytes.</param>
    public void Add(string owner, long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        lock (gate)
        {
            if (!reads.TryGetValue(owner, out List<(long Second, long Bytes)>? entries))
            {
                reads[owner] = entries = [];
            }

            long second = time.GetUtcNow().ToUnixTimeSeconds();
            Prune(entries);

            // One entry per second, so a long-running owner holds at most 3,600 of them.
            if (entries.Count > 0 && entries[^1].Second == second)
            {
                entries[^1] = (second, entries[^1].Bytes + bytes);
            }
            else
            {
                entries.Add((second, bytes));
            }
        }
    }

    private long Used(string owner)
    {
        if (!reads.TryGetValue(owner, out List<(long Second, long Bytes)>? entries))
        {
            return 0;
        }

        Prune(entries);

        if (entries.Count == 0)
        {
            reads.Remove(owner);
            return 0;
        }

        long total = 0;

        foreach ((_, long bytes) in entries)
        {
            total += bytes;
        }

        return total;
    }

    private void Prune(List<(long Second, long Bytes)> entries)
    {
        long oldest = time.GetUtcNow().ToUnixTimeSeconds() - HourSeconds;
        int expired = 0;

        while (expired < entries.Count && entries[expired].Second <= oldest)
        {
            expired++;
        }

        if (expired > 0)
        {
            entries.RemoveRange(0, expired);
        }
    }
}
