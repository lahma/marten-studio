using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

namespace MartenStudio.Services.Projections;

/// <summary>What a long-running operation is doing now.</summary>
internal enum StudioOperationState
{
    /// <summary>Still going.</summary>
    Running,

    /// <summary>Finished, and did what it said.</summary>
    Succeeded,

    /// <summary>Threw. <see cref="StudioOperation.Message" /> says what.</summary>
    Failed,

    /// <summary>Somebody cancelled it, or the process is shutting down.</summary>
    Cancelled
}

/// <summary>
/// One long-running operation started from the studio.
/// </summary>
/// <param name="Id">The handle a page polls by. Opaque, and short enough to put in markup.</param>
/// <param name="Kind">What sort of operation it is - <c>Rebuild</c>.</param>
/// <param name="Target">What it is being done to, named the way the page names it.</param>
/// <param name="StoreKey">The store it runs against.</param>
/// <param name="DatabaseId">The database it runs against.</param>
/// <param name="User">Who started it. Captured at start, because the circuit may be long gone.</param>
/// <param name="StartedAt">When it started.</param>
/// <param name="FinishedAt">When it stopped, or <see langword="null" /> while it runs.</param>
/// <param name="State">Running, succeeded, failed or cancelled.</param>
/// <param name="Message">The outcome, or the failure.</param>
/// <param name="ElapsedMilliseconds">How long it took, or has taken so far.</param>
internal sealed record StudioOperation(
    string Id,
    string Kind,
    string Target,
    string StoreKey,
    string DatabaseId,
    string User,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    StudioOperationState State,
    string? Message,
    long ElapsedMilliseconds)
{
    /// <summary>Whether this is still going.</summary>
    public bool IsRunning => State == StudioOperationState.Running;
}

/// <summary>
/// A handle to something the studio started that outlives the click that started it.
/// </summary>
/// <param name="Id">What to poll for.</param>
/// <param name="Kind">What sort of operation it is.</param>
/// <param name="Target">What it is being done to.</param>
internal sealed record OperationHandle(string Id, string Kind, string Target);

/// <summary>
/// Runs the operations that take longer than a request, and survives the page that started them.
/// </summary>
/// <remarks>
/// <para>
/// A projection rebuild takes as long as it takes. Awaiting one on the circuit would mean that closing
/// the tab, a dropped WebSocket or a navigation cancels a rebuild halfway through - leaving the
/// projection's tables neither the old shape nor the new one. So a rebuild is started <em>detached</em>,
/// with a cancellation token of its own that no circuit owns, and the page watches it by id.
/// </para>
/// <para>
/// Singleton, bounded, and it audits both ends: event 9207 when a rebuild starts and 9208 when it
/// finishes, plus a ring entry for the Activity page each time, because the circuit that would otherwise
/// have written the second one may not exist by then.
/// </para>
/// <para>
/// It deliberately does not persist anything. An operation is a fact about this process; a restart
/// forgets it, and the projections screen then shows what the database says, which is the truth either
/// way.
/// </para>
/// </remarks>
internal sealed class StudioOperationTracker : IDisposable
{
    /// <summary>How many finished operations are remembered before the oldest is dropped.</summary>
    public const int MaxRemembered = 50;

    private readonly ConcurrentDictionary<string, Entry> operations = new(StringComparer.Ordinal);
    private readonly ILogger<StudioOperationTracker> logger;
    private readonly StudioActionLogService actionLog;
    private readonly TimeProvider timeProvider;
    private bool disposed;

    public StudioOperationTracker(ILogger<StudioOperationTracker> logger, StudioActionLogService actionLog)
        : this(logger, actionLog, TimeProvider.System)
    {
    }

    public StudioOperationTracker(
        ILogger<StudioOperationTracker> logger,
        StudioActionLogService actionLog,
        TimeProvider timeProvider)
    {
        this.logger = logger;
        this.actionLog = actionLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Starts <paramref name="work" /> detached and returns its handle immediately.
    /// </summary>
    /// <param name="kind">What sort of operation this is.</param>
    /// <param name="target">What it is being done to.</param>
    /// <param name="storeKey">The store it runs against.</param>
    /// <param name="databaseId">The database it runs against.</param>
    /// <param name="user">Who started it.</param>
    /// <param name="capability">The capability that let it start, for the audit entry.</param>
    /// <param name="work">The operation. Its token is the tracker's, never a circuit's.</param>
    public OperationHandle Start(
        string kind,
        string target,
        string storeKey,
        string databaseId,
        string user,
        StudioCapability capability,
        Func<CancellationToken, Task> work)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(disposed, this);

        string id = Guid.NewGuid().ToString("n")[..12];
        DateTimeOffset startedAt = timeProvider.GetUtcNow();

        Entry entry = new(
            new StudioOperation(id, kind, target, storeKey, databaseId, user, startedAt, null, StudioOperationState.Running, null, 0),
            new CancellationTokenSource(),
            capability);

        operations[id] = entry;
        Prune();

        logger.ProjectionRebuildStarted(user, target, storeKey, databaseId);
        actionLog.Record(new StudioActionLogEntry(
            startedAt, user, storeKey, databaseId, null, kind + "Started", target, true,
            "Started; it runs in the background and survives this page.", capability.ToString()));

        // Detached on purpose: nothing awaits this task, and its exceptions are observed in the
        // continuation below rather than escaping as unobserved.
        entry.Task = Task.Run(async () =>
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            StudioOperationState state;
            string? message;

            try
            {
                await work(entry.Cancellation.Token).ConfigureAwait(false);
                state = StudioOperationState.Succeeded;
                message = "Completed.";
            }
            catch (OperationCanceledException)
            {
                state = StudioOperationState.Cancelled;
                message = "Cancelled.";
            }
            catch (Exception exception)
            {
                state = StudioOperationState.Failed;
                message = exception.Message;
                logger.LogError(exception, "Marten Studio operation {Kind} on {Target} failed", kind, target);
            }

            stopwatch.Stop();
            Complete(id, state, message, stopwatch.ElapsedMilliseconds);
        }, CancellationToken.None);

        return new OperationHandle(id, kind, target);
    }

    /// <summary>The operation with this id, or <see langword="null" /> when it was never started or has been dropped.</summary>
    public StudioOperation? Find(string? id) =>
        id is { Length: > 0 } && operations.TryGetValue(id, out Entry? entry) ? entry.Operation : null;

    /// <summary>Every operation this process remembers, newest first.</summary>
    public IReadOnlyList<StudioOperation> All()
    {
        List<StudioOperation> all = [.. operations.Values.Select(static x => x.Operation)];
        all.Sort(static (left, right) => right.StartedAt.CompareTo(left.StartedAt));
        return all;
    }

    /// <summary>The running operations against one store and database, newest first.</summary>
    public IReadOnlyList<StudioOperation> RunningFor(string storeKey, string databaseId)
    {
        List<StudioOperation> running = [.. operations.Values
            .Select(static x => x.Operation)
            .Where(x => x.IsRunning
                && string.Equals(x.StoreKey, storeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.DatabaseId, databaseId, StringComparison.OrdinalIgnoreCase))];

        running.Sort(static (left, right) => right.StartedAt.CompareTo(left.StartedAt));
        return running;
    }

    /// <summary>Asks the operation to stop. Whether it can is up to what it is doing.</summary>
    public bool Cancel(string? id)
    {
        if (id is not { Length: > 0 } || !operations.TryGetValue(id, out Entry? entry) || !entry.Operation.IsRunning)
        {
            return false;
        }

        entry.Cancellation.Cancel();
        return true;
    }

    /// <summary>Cancels everything still running. The studio's own shutdown.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (Entry entry in operations.Values)
        {
            try
            {
                entry.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            entry.Cancellation.Dispose();
        }
    }

    private void Complete(string id, StudioOperationState state, string? message, long elapsedMilliseconds)
    {
        if (!operations.TryGetValue(id, out Entry? entry))
        {
            return;
        }

        DateTimeOffset finishedAt = timeProvider.GetUtcNow();
        StudioOperation completed = entry.Operation with
        {
            State = state,
            Message = message,
            FinishedAt = finishedAt,
            ElapsedMilliseconds = elapsedMilliseconds
        };

        string outcome = state switch
        {
            StudioOperationState.Succeeded => nameof(StudioOperationState.Succeeded),
            StudioOperationState.Failed => nameof(StudioOperationState.Failed),
            StudioOperationState.Cancelled => nameof(StudioOperationState.Cancelled),
            _ => nameof(StudioOperationState.Running)
        };

        logger.ProjectionRebuildFinished(
            completed.Target, completed.StoreKey, completed.DatabaseId, elapsedMilliseconds, outcome);

        actionLog.Record(new StudioActionLogEntry(
            finishedAt,
            completed.User,
            completed.StoreKey,
            completed.DatabaseId,
            null,
            completed.Kind + "Finished",
            completed.Target,
            state == StudioOperationState.Succeeded,
            message ?? outcome,
            entry.Capability.ToString()));

        // Published last, so that anything watching for "no longer running" - a page's next poll, a test -
        // sees the operation finish only once both records of it exist.
        entry.Operation = completed;
    }

    /// <summary>Drops the oldest finished operations once too many are remembered.</summary>
    private void Prune()
    {
        if (operations.Count <= MaxRemembered)
        {
            return;
        }

        foreach (Entry entry in operations.Values
            .Where(static x => !x.Operation.IsRunning)
            .OrderBy(static x => x.Operation.StartedAt)
            .Take(operations.Count - MaxRemembered))
        {
            if (operations.TryRemove(entry.Operation.Id, out Entry? removed))
            {
                removed.Cancellation.Dispose();
            }
        }
    }

    /// <summary>One operation, its cancellation source and the task nobody awaits.</summary>
    private sealed class Entry
    {
        public Entry(StudioOperation operation, CancellationTokenSource cancellation, StudioCapability capability)
        {
            Operation = operation;
            Cancellation = cancellation;
            Capability = capability;
        }

        /// <summary>Replaced wholesale when the operation finishes; the record is immutable, the slot is not.</summary>
        public StudioOperation Operation { get; set; }

        public CancellationTokenSource Cancellation { get; }

        /// <summary>What let this start, kept so the finishing audit entry can name it too.</summary>
        public StudioCapability Capability { get; }

        public Task? Task { get; set; }
    }
}
