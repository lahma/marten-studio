using System.Globalization;

using KStudio.Core.Decoding;
using KStudio.Core.Scanning;

namespace KStudio.Services;

/// <summary>What the visitor asked to tail: the form's content, before any gate has looked at it.</summary>
/// <param name="Cluster">The cluster entry in scope.</param>
/// <param name="Topics">The topics, by name.</param>
/// <param name="PredicateText">The visitor's typed text; it is parsed by the engine and never recorded.</param>
/// <param name="IncludeUncommitted">Whether uncommitted records are included.</param>
/// <param name="ResumeFrom">The cursor of the paused segment this one goes on from, or <see langword="null" /> for a tail that starts at the end of each partition.</param>
internal sealed record KafkaTailInput(
    KafkaClusterEntry Cluster,
    IReadOnlyList<string> Topics,
    string? PredicateText,
    bool IncludeUncommitted,
    KafkaScanCursor? ResumeFrom = null);

/// <summary>Why a tail segment ended without the tail ending, for the line the page shows (D-M5 decision 3.7).</summary>
internal enum KafkaTailPause
{
    /// <summary>The visitor pressed Pause.</summary>
    You,

    /// <summary>The tab was hidden; the tail resumes when it is shown again.</summary>
    TabHidden,

    /// <summary>The segment read for <c>TailPauseAfter</c>.</summary>
    Time,

    /// <summary>The segment read <c>MaxBytesPerScan</c>.</summary>
    Bytes,

    /// <summary>The segment read <c>MaxRecordsPerScan</c>.</summary>
    Records,

    /// <summary>The visitor's hourly allowance ran out.</summary>
    Allowance,

    /// <summary>The cluster stopped answering; the cursor is exact, so the tail can go on once it does.</summary>
    Unavailable,
}

/// <summary>
/// A tail session (D-M5 decision 3.9): the visitor's one tail from Start to Stop, over however many segments it reads in. A segment is one run of the
/// engine, ended by a pause; the session keeps what the segments leave - the newest matches (the ring), the exact cursor, the sums - so that a pause
/// frees the engine's slot and consumer and loses nothing. The session's one audit entry is written once, when it ends: <see cref="End" /> is
/// idempotent and is called from Stop, a terminal stop, a new Start and <see cref="DisposeAsync" />.
/// </summary>
/// <remarks>
/// A session is touched by the circuit's renderer only - the page's reading loop and its event handlers all run on it - and is not thread-safe.
/// </remarks>
internal sealed class KafkaTailSession : IAsyncDisposable
{
    private readonly Action<KafkaTailSession, KafkaScanStopReason> recordEnd;
    private readonly List<KafkaMatch> ring = [];
    private readonly List<KafkaScanGap> gaps = [];
    private readonly List<string> notes = [];
    private readonly Dictionary<KafkaUnknownReason, long> undecidableByReason = [];
    private readonly HashSet<string> topicRefusals = new(StringComparer.Ordinal);
    private readonly int capacity;
    private KafkaTailSegment? current;
    private KafkaScanProgress? liveProgress;
    private int ended;

    /// <summary>Creates a session over its first segment's plan.</summary>
    /// <param name="input">What the visitor asked, without a cursor.</param>
    /// <param name="plan">The first segment's plan.</param>
    /// <param name="grant">What the first segment may read; a later segment may not read less.</param>
    /// <param name="user">The visitor's name, captured at the start so that recording needs nothing from a scope that may be disposing.</param>
    /// <param name="recordEnd">Writes the session's audit entry; called at most once.</param>
    public KafkaTailSession(KafkaTailInput input, KafkaScanPlan plan, KafkaGrantDecision grant, string user, Action<KafkaTailSession, KafkaScanStopReason> recordEnd)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Grant = grant ?? throw new ArgumentNullException(nameof(grant));
        User = user ?? throw new ArgumentNullException(nameof(user));
        this.recordEnd = recordEnd ?? throw new ArgumentNullException(nameof(recordEnd));
        capacity = plan.Limits.TailCapacity;
        AuditText = plan.AuditText;
        ReadsValues = plan.ReadsValues;
    }

    /// <summary>What the visitor asked, without a cursor.</summary>
    public KafkaTailInput Input { get; }

    /// <summary>The newest plan: its limits and notes are the ones the page shows.</summary>
    public KafkaScanPlan Plan { get; private set; }

    /// <summary>What the first segment was allowed to read.</summary>
    public KafkaGrantDecision Grant { get; }

    /// <summary>Who started the tail.</summary>
    public string User { get; }

    /// <summary>The predicate as the audit writes it: a literal on a redacted field is <c>&lt;redacted&gt;</c>.</summary>
    public string AuditText { get; }

    /// <summary>Whether judging a record could read its value on any topic: the entry says so.</summary>
    public bool ReadsValues { get; }

    /// <summary>How many segments started.</summary>
    public int Segments { get; private set; }

    /// <summary>Records the finished segments read.</summary>
    public long CompletedRecords { get; private set; }

    /// <summary>Bytes the finished segments read.</summary>
    public long CompletedBytes { get; private set; }

    /// <summary>Matches the finished segments found (found, not kept: the ring evicts).</summary>
    public long CompletedMatches { get; private set; }

    /// <summary>Time the finished segments read for.</summary>
    public TimeSpan CompletedElapsed { get; private set; }

    /// <summary>Records read, finished segments and the one that runs.</summary>
    public long Records => CompletedRecords + (liveProgress?.Records ?? 0);

    /// <summary>Bytes read, finished segments and the one that runs.</summary>
    public long Bytes => CompletedBytes + (liveProgress?.Bytes ?? 0);

    /// <summary>Matches found, finished segments and the one that runs.</summary>
    public long Matches => CompletedMatches + (liveProgress?.Matches ?? 0);

    /// <summary>The newest segment's progress while it runs, per partition; <see langword="null" /> between segments.</summary>
    public KafkaScanProgress? LiveProgress => liveProgress;

    /// <summary>Matches that arrived and were evicted before the page showed them, over the whole tail.</summary>
    public long Skipped { get; private set; }

    /// <summary>Offsets the visitor chose to skip by jumping to now.</summary>
    public long JumpedOffsets { get; private set; }

    /// <summary>How many times the visitor jumped to now.</summary>
    public int Jumps { get; private set; }

    /// <summary>Resumes that were refused: counted here and not as entries of their own (D-M5 decision 3.9).</summary>
    public int RefusedResumes { get; private set; }

    /// <summary>Why the last refused resume was refused.</summary>
    public string? LastRefusedResume { get; private set; }

    /// <summary>The last stop reason a segment ended by, or <see langword="null" /> before the first ends.</summary>
    public KafkaScanStopReason? LastStop { get; private set; }

    /// <summary>The exact cursor of the last finished segment: the next offset of every partition the tail follows. <see langword="null" /> before one ends.</summary>
    public KafkaScanCursor? Cursor { get; private set; }

    /// <summary>The newest matches, newest first, at most <c>MaxTailMatches</c> of them.</summary>
    public IReadOnlyList<KafkaMatch> Ring => ring;

    /// <summary>Records lost to retention, over the whole tail.</summary>
    public IReadOnlyList<KafkaScanGap> Gaps => gaps;

    /// <summary>What a visitor must be told about the partitions and topics, over the whole tail.</summary>
    public IReadOnlyList<string> Notes => notes;

    /// <summary>The topics a segment could not read at all, with why.</summary>
    public IReadOnlyCollection<string> TopicRefusals => topicRefusals;

    /// <summary>Records the tail could not decide, by reason, over the whole tail.</summary>
    public IReadOnlyDictionary<KafkaUnknownReason, long> UndecidableByReason => undecidableByReason;

    /// <summary>Records read that had no timestamp (a tail evaluates them all).</summary>
    public long NoTimestamp { get; private set; }

    /// <summary>The segment that runs, or <see langword="null" /> when the tail is paused or over.</summary>
    public KafkaTailSegment? Current => current;

    /// <summary>Whether the session's audit entry was written.</summary>
    public bool IsEnded => Volatile.Read(ref ended) != 0;

    /// <summary>Makes a started engine run the session's next segment.</summary>
    /// <param name="handle">The engine's handle.</param>
    /// <param name="plan">The plan it runs.</param>
    internal KafkaTailSegment BeginSegment(KafkaScanHandle handle, KafkaScanPlan plan)
    {
        Segments++;
        Plan = plan;
        liveProgress = null;
        AddNotes(plan.Notes.Select(static x => x.Text));

        foreach (KafkaScanGap gap in plan.Gaps)
        {
            AddGap(gap);
        }

        return current = new KafkaTailSegment(this, handle);
    }

    /// <summary>Counts a resume the gate or the engine refused.</summary>
    /// <param name="reason">The sentence the visitor was shown.</param>
    internal void CountRefusedResume(string reason)
    {
        RefusedResumes++;
        LastRefusedResume = reason;
    }

    /// <summary>Counts a jump to now and the offsets it skipped.</summary>
    /// <param name="offsets">The offsets produced while paused that the new segment does not read.</param>
    internal void CountJump(long offsets)
    {
        Jumps++;
        JumpedOffsets += Math.Max(0, offsets);
    }

    /// <summary>Puts a batch's matches into the ring (newest first) and keeps its progress.</summary>
    /// <param name="batch">The engine's batch.</param>
    public void Apply(KafkaScanBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.NewMatches.Count > 0)
        {
            // The batch is oldest first; the ring is newest first. Built in one pass: a batch can be as large as the ring.
            List<KafkaMatch> merged = new(Math.Min(capacity, batch.NewMatches.Count + ring.Count));

            for (int i = batch.NewMatches.Count - 1; i >= 0 && merged.Count < capacity; i--)
            {
                merged.Add(batch.NewMatches[i]);
            }

            for (int i = 0; i < ring.Count && merged.Count < capacity; i++)
            {
                merged.Add(ring[i]);
            }

            ring.Clear();
            ring.AddRange(merged);
        }

        Skipped += batch.Skipped;
        liveProgress = batch.Final is null ? batch.Progress : null;
    }

    /// <summary>
    /// A finished segment's result, once: the sums, the cursor, the gaps and the caveats. Called by the segment's <see cref="KafkaTailSegment.Complete" />.
    /// </summary>
    /// <param name="result">The result, or <see langword="null" /> when the engine produced none.</param>
    internal void AddResult(KafkaScanResult? result)
    {
        current = null;
        liveProgress = null;

        if (result is null)
        {
            return;
        }

        CompletedRecords += result.Records;
        CompletedBytes += result.Bytes;
        CompletedMatches += result.Matches;
        CompletedElapsed += result.Elapsed;
        NoTimestamp += result.NoTimestamp;
        LastStop = result.StopReason;
        Cursor = result.Cursor;
        AddNotes(result.Notes.Select(static x => x.Text));

        foreach (KafkaScanGap gap in result.Gaps)
        {
            AddGap(gap);
        }

        foreach (KeyValuePair<KafkaUnknownReason, long> pair in result.UndecidableByReason)
        {
            undecidableByReason[pair.Key] = undecidableByReason.GetValueOrDefault(pair.Key) + pair.Value;
        }

        foreach (KafkaTopicRefusal refusal in result.TopicRefusals)
        {
            topicRefusals.Add(refusal.Text);
        }
    }

    /// <summary>
    /// Ends the session and writes its one audit entry. Idempotent: the first call records, any other does nothing, whichever of Stop, a terminal
    /// stop, a new Start and the page being left gets here first.
    /// </summary>
    /// <param name="reason">What ended it.</param>
    public void End(KafkaScanStopReason reason)
    {
        if (Interlocked.Exchange(ref ended, 1) != 0)
        {
            return;
        }

        recordEnd(this, reason);
    }

    /// <summary>Stops the segment that runs, waits for it, and ends the session as the page being left.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (current is { } running)
            {
                await running.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            End(KafkaScanStopReason.CallerGone);
        }
    }

    private void AddNotes(IEnumerable<string> texts)
    {
        foreach (string text in texts)
        {
            if (!notes.Contains(text, StringComparer.Ordinal))
            {
                notes.Add(text);
            }
        }
    }

    private void AddGap(KafkaScanGap gap)
    {
        if (!gaps.Contains(gap))
        {
            gaps.Add(gap);
        }
    }

    /// <summary>The audit entry's message for the session's end (D-M5 decision 3.9).</summary>
    /// <param name="reason">What ended the session.</param>
    internal string AuditMessage(KafkaScanStopReason reason)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string refused = RefusedResumes == 0
            ? string.Empty
            : string.Create(c, $"; {KafkaDisplay.Count(RefusedResumes, "resume")} refused: {LastRefusedResume?.TrimEnd('.')}");
        string jumped = Jumps == 0
            ? string.Empty
            : string.Create(c, $"; jumped to now {KafkaDisplay.Count(Jumps, "time")}, skipping {KafkaDisplay.Count(JumpedOffsets, "offset")} produced while paused");

        return string.Create(
            c,
            $"{KafkaDisplay.Count(Segments, "segment")} over {KafkaTailText.Elapsed(CompletedElapsed)}: {KafkaDisplay.Count(CompletedRecords, "record")} and {KafkaDisplay.Bytes(CompletedBytes)} read, {KafkaDisplay.Count(CompletedMatches, "match", "matches")}; ended by {reason}; predicate: {AuditText}{(ReadsValues && Grant.HasPayload ? "; values read" : string.Empty)}{jumped}{refused}");
    }
}

/// <summary>
/// One run of the engine inside a tail session (D-M5 decision 3.5): started from the end of each partition or from the session's cursor, ended by a
/// pause or a stop. It holds the engine's slot and consumer while it reads and no longer. Its result is added to the session exactly once, by whichever
/// comes first - the page receiving the final batch (<see cref="Complete" />) or the page being left (<see cref="DisposeAsync" />, which waits for the
/// result the engine reached).
/// </summary>
internal sealed class KafkaTailSegment : IAsyncDisposable
{
    private readonly KafkaTailSession session;
    private readonly KafkaScanHandle handle;
    private readonly Lazy<Task> dispose;
    private int completed;

    internal KafkaTailSegment(KafkaTailSession session, KafkaScanHandle handle)
    {
        this.session = session;
        this.handle = handle;
        dispose = new Lazy<Task>(DisposeCoreAsync);
    }

    /// <summary>The session this segment belongs to.</summary>
    public KafkaTailSession Session => session;

    /// <summary>The engine's batches, at most four a second, the last carrying the result. They can be read once.</summary>
    /// <param name="cancellationToken">Ends the enumeration, not the segment.</param>
    public IAsyncEnumerable<KafkaScanBatch> ReadBatchesAsync(CancellationToken cancellationToken) => handle.ReadBatchesAsync(cancellationToken);

    /// <summary>Ends the segment on purpose: its slot and consumer are released and the cursor is exact. The result's stop reason is <c>Paused</c>.</summary>
    public void Pause() => handle.Stop(KafkaScanStopReason.Paused);

    /// <summary>Ends the segment because the visitor pressed Stop; the result's stop reason is <c>Cancelled</c>.</summary>
    public void Stop() => handle.Stop();

    /// <summary>Adds the segment's result to its session, if no one has.</summary>
    /// <param name="result">The final result, or <see langword="null" /> when the engine produced none.</param>
    public void Complete(KafkaScanResult? result)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return;
        }

        session.AddResult(result);
    }

    /// <summary>
    /// Stops the segment if it still runs (the page is gone) and adds its result to the session. Every caller gets the same task, so that the page's reading
    /// loop and the session's own dispose cannot each think the other finished: whoever comes second waits for the first, and the session's entry is written after it.
    /// </summary>
    public ValueTask DisposeAsync() => new(dispose.Value);

    private async Task DisposeCoreAsync()
    {
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
