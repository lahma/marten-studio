using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Hosting;
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
/// What <see cref="StudioOperationTracker.Start" /> answered.
/// </summary>
/// <remarks>
/// One projection may only be rebuilt once at a time, on any circuit, so "start" is a request rather
/// than a command. A second request is answered with the handle of the rebuild that is already running:
/// the caller still has something to watch, and nothing has been started twice.
/// </remarks>
/// <param name="Handle">What to watch, whether or not this call is what started it.</param>
/// <param name="Started">
/// <see langword="true" /> when this call started the operation, <see langword="false" /> when an
/// identical one was already running and <paramref name="Handle" /> is that one.
/// </param>
internal sealed record OperationStart(OperationHandle Handle, bool Started)
{
    /// <summary>The handle's id, which is what a page polls by.</summary>
    public string Id => Handle.Id;
}

/// <summary>
/// What the tracker throws when it will not start anything more.
/// </summary>
/// <remarks>
/// Distinct from a capability or an authorization refusal: the visitor is allowed to do this and the
/// option is on, but the process is already running as many detached operations as it will hold. It is a
/// message the page shows rather than a log line, because the only useful response is to wait.
/// </remarks>
internal sealed class StudioOperationRefusedException : InvalidOperationException
{
    public StudioOperationRefusedException(string message) : base(message)
    {
    }
}

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
/// Detached is not unbounded. Two rebuilds of the same projection in the same database would race each
/// other over the same tables, so an identical operation that is already running is <em>answered</em>
/// rather than started again - the tracker is the singleton, so this holds across circuits and not only
/// across clicks. Beyond that there is a hard ceiling on how many may run at once
/// (<see cref="MaxRunning" />), and past it <see cref="Start" /> refuses with a message.
/// </para>
/// <para>
/// Singleton, bounded, and it audits both ends: event 9207 when a rebuild starts and 9208 when it
/// finishes, plus a ring entry for the Activity page each time, because the circuit that would otherwise
/// have written the second one may not exist by then.
/// </para>
/// <para>
/// Every operation's token is linked to <see cref="IHostApplicationLifetime.ApplicationStopping" />, so a
/// host shutting down cancels its replays instead of being held open by them - and the outcome recorded
/// is <see cref="StudioOperationState.Cancelled" />, which is what happened, rather than a failure.
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

    /// <summary>
    /// How many operations may be running at once before the tracker refuses to start another.
    /// </summary>
    /// <remarks>
    /// A rebuild is a full replay of the event store against one projection; several at once are several
    /// concurrent scans of the same table. The ceiling exists so that an unattended page - or somebody
    /// working through a list of projections - cannot turn an admin screen into the load that takes the
    /// database down.
    /// </remarks>
    public const int MaxRunning = 4;

    private readonly ConcurrentDictionary<string, Entry> operations = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly ILogger<StudioOperationTracker> logger;
    private readonly StudioActionLogService actionLog;
    private readonly TimeProvider timeProvider;
    private readonly CancellationToken applicationStopping;
    private bool disposed;

    public StudioOperationTracker(
        ILogger<StudioOperationTracker> logger,
        StudioActionLogService actionLog,
        IHostApplicationLifetime? applicationLifetime = null)
        : this(logger, actionLog, TimeProvider.System, applicationLifetime)
    {
    }

    public StudioOperationTracker(
        ILogger<StudioOperationTracker> logger,
        StudioActionLogService actionLog,
        TimeProvider timeProvider,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        this.logger = logger;
        this.actionLog = actionLog;
        this.timeProvider = timeProvider;
        applicationStopping = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>
    /// Starts <paramref name="work" /> detached, or answers with the identical operation already running.
    /// </summary>
    /// <param name="kind">What sort of operation this is.</param>
    /// <param name="target">What it is being done to.</param>
    /// <param name="storeKey">The store it runs against.</param>
    /// <param name="databaseId">The database it runs against.</param>
    /// <param name="user">Who started it.</param>
    /// <param name="capability">The capability that let it start, for the audit entry.</param>
    /// <param name="work">The operation. Its token is the tracker's, never a circuit's.</param>
    /// <returns>
    /// The handle, and whether this call is what started it. A <c>Started</c> of <see langword="false" />
    /// means an identical operation was already running and the handle is that one's.
    /// </returns>
    /// <exception cref="StudioOperationRefusedException">
    /// <see cref="MaxRunning" /> operations are already running.
    /// </exception>
    public OperationStart Start(
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

        string id;
        Entry entry;
        DateTimeOffset startedAt;

        // The duplicate check, the ceiling and the insert are one decision: two clicks a millisecond
        // apart on two circuits must not both get past a check that each made on its own.
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (FindRunning(kind, target, storeKey, databaseId) is { } already)
            {
                // Answered rather than logged: the caller records the refusal in the audit ring, which is
                // where a person looking for "why did my second click do nothing" will actually look.
                return new OperationStart(
                    new OperationHandle(already.Operation.Id, already.Operation.Kind, already.Operation.Target),
                    Started: false);
            }

            int running = CountRunning();
            if (running >= MaxRunning)
            {
                throw new StudioOperationRefusedException(
                    $"Marten Studio is already running {running} background operations, which is the most it will run at once. "
                    + "Wait for one of them to finish, or cancel it, and try again.");
            }

            id = Guid.NewGuid().ToString("n")[..12];
            startedAt = timeProvider.GetUtcNow();

            entry = new Entry(
                new StudioOperation(id, kind, target, storeKey, databaseId, user, startedAt, null, StudioOperationState.Running, null, 0),
                CancellationTokenSource.CreateLinkedTokenSource(applicationStopping),
                capability);

            operations[id] = entry;
            Prune();
        }

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

            try
            {
                Complete(id, state, message, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                // Disposed here rather than from Dispose(): the token is in use right up to the line
                // above, and a source disposed underneath its own work turns a cancellation into an
                // ObjectDisposedException recorded as a failure.
                entry.Cancellation.Dispose();
            }
        }, CancellationToken.None);

        return new OperationStart(new OperationHandle(id, kind, target), Started: true);
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

        try
        {
            entry.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the check above and this line, which is the same answer as "no".
            return false;
        }

        return true;
    }

    /// <summary>
    /// Cancels everything still running. The studio's own shutdown.
    /// </summary>
    /// <remarks>
    /// It asks and does not wait, and it disposes no cancellation source: each operation's task owns its
    /// own and disposes it once it has observed the cancellation. Disposing them here would race the
    /// tasks that are still reading their tokens.
    /// </remarks>
    public void Dispose()
    {
        List<Entry> open;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            open = [.. operations.Values.Where(static x => x.Operation.IsRunning)];
        }

        foreach (Entry entry in open)
        {
            try
            {
                entry.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private Entry? FindRunning(string kind, string target, string storeKey, string databaseId)
    {
        foreach (Entry entry in operations.Values)
        {
            StudioOperation operation = entry.Operation;
            if (operation.IsRunning
                && string.Equals(operation.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.Target, target, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.StoreKey, storeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(operation.DatabaseId, databaseId, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private int CountRunning()
    {
        int running = 0;
        foreach (Entry entry in operations.Values)
        {
            if (entry.Operation.IsRunning)
            {
                running++;
            }
        }

        return running;
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

    /// <summary>
    /// Drops the oldest finished operations once too many are remembered.
    /// </summary>
    /// <remarks>
    /// Only finished ones are ever dropped, and no cancellation source is disposed here: a running
    /// operation's handle has to stay addressable until it ends, and its source belongs to its task.
    /// Running operations are bounded at the other end instead, by <see cref="MaxRunning" />.
    /// </remarks>
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
            operations.TryRemove(entry.Operation.Id, out _);
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
