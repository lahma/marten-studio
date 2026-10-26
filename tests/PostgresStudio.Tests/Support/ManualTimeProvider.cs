namespace PostgresStudio.Tests.Support;

/// <summary>
/// A clock a test moves by hand, timers included - so a cache window and a timeout built on
/// <c>new CancellationTokenSource(timeout, timeProvider)</c> can be tested without waiting for either.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from <c>Microsoft.Extensions.TimeProvider.Testing</c>: the package budget is complete
/// (AGENTS.md hard rule 1). A timer fires, on the thread that advances the clock, once the clock reaches its due time.
/// </remarks>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that came due.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due = [];
        lock (gate)
        {
            now = now.Add(by);
            foreach (ManualTimer timer in timers)
            {
                if (timer.Due is { } at && at <= now)
                {
                    timer.Due = timer.Period > TimeSpan.Zero ? at + timer.Period : null;
                    due.Add(timer);
                }
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
