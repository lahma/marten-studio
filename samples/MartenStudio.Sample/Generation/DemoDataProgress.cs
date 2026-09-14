using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.Sample.Generation;

/// <summary>Where a demo-data job is.</summary>
internal enum DemoDataJobState
{
    /// <summary>Nothing has been started in this process.</summary>
    Idle,

    /// <summary>A job is running.</summary>
    Running,

    /// <summary>The last job finished.</summary>
    Completed,

    /// <summary>The last job was cancelled, and left behind whatever it had already committed.</summary>
    Cancelled,

    /// <summary>The last job threw.</summary>
    Failed,
}

/// <summary>Which of the two jobs the panel can start.</summary>
internal enum DemoDataJobKind
{
    /// <summary>Nothing has run yet.</summary>
    None,

    /// <summary>Writing documents and appending streams.</summary>
    Generate,

    /// <summary>Deleting generated documents and archiving generated streams.</summary>
    Truncate,
}

/// <summary>
/// Everything <c>GET /sample/generate/status</c> answers with: an immutable snapshot the panel polls.
/// </summary>
/// <remarks>
/// A record rather than mutable counters read field by field, because the status endpoint and the job
/// run on different threads: a reader that saw <c>Documents</c> from one instant and <c>Elapsed</c> from
/// another would report a rate that never happened. The job publishes a whole new snapshot per batch and
/// the endpoint reads the reference once.
/// </remarks>
internal sealed record DemoDataProgress
{
    /// <summary>The starting state: nothing has run in this process.</summary>
    public static DemoDataProgress Idle { get; } = new();

    /// <summary>Where the job is.</summary>
    public DemoDataJobState State { get; init; } = DemoDataJobState.Idle;

    /// <summary>Which job.</summary>
    public DemoDataJobKind Kind { get; init; } = DemoDataJobKind.None;

    /// <summary>The run marker every document of this run carries.</summary>
    public string? RunId { get; init; }

    /// <summary>The preset name.</summary>
    public string Size { get; init; } = string.Empty;

    /// <summary>What the job is doing right now.</summary>
    public string Phase { get; init; } = string.Empty;

    /// <summary>Documents written so far.</summary>
    public long Documents { get; init; }

    /// <summary>Documents the plan asks for.</summary>
    public long DocumentTarget { get; init; }

    /// <summary>Events appended so far.</summary>
    public long Events { get; init; }

    /// <summary>Events the plan asks for.</summary>
    public long EventTarget { get; init; }

    /// <summary>Streams started so far.</summary>
    public long Streams { get; init; }

    /// <summary>Streams the plan asks for.</summary>
    public long StreamTarget { get; init; }

    /// <summary>Streams a truncation has archived so far.</summary>
    public long StreamsArchived { get; init; }

    /// <summary>Rows (documents plus events) per second over the whole run so far.</summary>
    public double RowsPerSecond { get; init; }

    /// <summary>How long it has been going.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>What is left, at the current rate, or <see langword="null" /> when it cannot be guessed.</summary>
    public TimeSpan? Eta { get; init; }

    /// <summary>How far along, 0 to 1.</summary>
    public double Fraction => Kind == DemoDataJobKind.Generate && DocumentTarget + EventTarget > 0
        ? Math.Clamp((double) (Documents + Events) / (DocumentTarget + EventTarget), 0, 1)
        : 0;

    /// <summary>Why it failed.</summary>
    public string? Error { get; init; }

    /// <summary>Whether another job can be started right now.</summary>
    public bool CanStart => State != DemoDataJobState.Running;

    /// <summary>A snapshot built from the generator's own counters.</summary>
    public DemoDataProgress With(DemoDataCounters counters, TimeSpan elapsed)
    {
        long rows = counters.Rows;
        double seconds = elapsed.TotalSeconds;
        double rate = seconds > 0.001 ? rows / seconds : 0;
        long remaining = Math.Max(0, DocumentTarget + EventTarget - rows);

        return this with
        {
            Phase = counters.Phase.ToString(),
            Documents = counters.Documents,
            Events = counters.Events,
            Streams = counters.Streams,
            RowsPerSecond = rate,
            Elapsed = elapsed,
            Eta = rate > 1 ? TimeSpan.FromSeconds(remaining / rate) : null,
        };
    }
}

/// <summary>
/// How far behind the async daemon is, read straight out of <c>mt_event_progression</c>.
/// </summary>
/// <param name="HighWaterMark">The highest event sequence the daemon has seen.</param>
/// <param name="Shards">One entry per projection shard, with how far behind it is.</param>
/// <param name="Available">Whether the progression table could be read at all.</param>
internal sealed record DemoDataDaemonLag(long HighWaterMark, IReadOnlyList<DemoDataShardLag> Shards, bool Available)
{
    /// <summary>Nothing to report: the event store has no progression table yet.</summary>
    public static DemoDataDaemonLag Unavailable { get; } = new(0, [], false);

    /// <summary>The worst shard's lag, which is the number worth putting on a status line.</summary>
    public long MaxLag => Shards.Count == 0 ? 0 : Shards.Max(static x => x.Lag);
}

/// <summary>One projection shard's progress.</summary>
/// <param name="Name">The shard identity, as <c>mt_event_progression.name</c> spells it.</param>
/// <param name="Sequence">The last sequence it processed.</param>
/// <param name="Lag">How many events behind the high-water mark it is.</param>
internal sealed record DemoDataShardLag(string Name, long Sequence, long Lag);
