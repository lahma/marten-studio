using System.Diagnostics;

using Marten;

using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// The one demo-data job this process will run, and the progress the panel polls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a hosted singleton.</b> Generating a million documents takes minutes, and an HTTP request that
/// waits for it is a request that times out somewhere in the middle and leaves the caller with no way to
/// find out what happened. So <c>POST /sample/generate</c> starts a task and returns immediately, the
/// panel polls <c>GET /sample/generate/status</c>, and the work is owned by a singleton that outlives
/// every request. Being an <see cref="IHostedService" /> is what gives it a shutdown hook: the job's
/// cancellation source is linked to <see cref="IHostApplicationLifetime.ApplicationStopping" />, so
/// Ctrl+C stops the generator between batches rather than killing a COPY mid-transaction.
/// </para>
/// <para>
/// <b>One at a time.</b> Two generators against one store would fight over the same tables and produce
/// a progress line that is the sum of two runs; two truncations would archive the same streams twice.
/// <see cref="TryStartGenerate" /> and <see cref="TryStartTruncate" /> refuse while anything is running,
/// and say so, rather than queueing - a demo host does not need a queue, it needs an honest answer.
/// </para>
/// </remarks>
internal sealed class DemoDataJob : IHostedService, IDisposable
{
    private readonly IDocumentStore store;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<DemoDataJob> logger;
    private readonly Lock gate = new();

    private CancellationTokenSource? cancellation;
    private Task? running;
    private volatile DemoDataProgress progress = DemoDataProgress.Idle;

    /// <summary>Wires the job to the store it writes and the lifetime that stops it.</summary>
    /// <param name="store">The demo store.</param>
    /// <param name="lifetime">The host lifetime, whose stopping token every job is linked to.</param>
    /// <param name="logger">Where a failure is written, since nobody may be watching the panel.</param>
    public DemoDataJob(IDocumentStore store, IHostApplicationLifetime lifetime, ILogger<DemoDataJob> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        this.store = store;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    /// <summary>The latest snapshot. Safe to read from any thread.</summary>
    public DemoDataProgress Progress => progress;

    /// <summary>
    /// Starts a generation run, unless one is already going.
    /// </summary>
    /// <param name="plan">What to write.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether a run was started.</returns>
    public bool TryStartGenerate(DemoDataPlan plan, out string? error)
    {
        ArgumentNullException.ThrowIfNull(plan);

        error = plan.Validate();
        if (error is not null)
        {
            return false;
        }

        var generator = new DemoDataGenerator(store, plan);

        DemoDataProgress initial = new()
        {
            State = DemoDataJobState.Running,
            Kind = DemoDataJobKind.Generate,
            RunId = generator.RunId,
            Size = plan.Size.ToString(),
            Phase = nameof(DemoDataPhase.Starting),
            DocumentTarget = plan.DocumentTarget,
            EventTarget = plan.EventTarget,
            StreamTarget = plan.Streams,
        };

        return TryStart(initial, (token) => RunGenerateAsync(generator, token), out error);
    }

    /// <summary>
    /// Starts a truncation, unless something is already going.
    /// </summary>
    /// <param name="deleteAllEventData">Whether to delete every event rather than archive generated streams.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether a truncation was started.</returns>
    public bool TryStartTruncate(bool deleteAllEventData, out string? error)
    {
        DemoDataProgress initial = new()
        {
            State = DemoDataJobState.Running,
            Kind = DemoDataJobKind.Truncate,
            Phase = deleteAllEventData ? "DeletingAllEventData" : "DeletingDocuments",
        };

        return TryStart(initial, (token) => RunTruncateAsync(deleteAllEventData, token), out error);
    }

    /// <summary>Asks the running job to stop between batches. Does nothing when none is running.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            cancellation?.Cancel();
        }
    }

    /// <summary>Nothing to start: the job exists to be asked.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Cancels whatever is running and waits a moment for it to notice.
    /// </summary>
    /// <param name="cancellationToken">How long the host is prepared to wait.</param>
    /// <remarks>
    /// The wait is bounded by the host's own shutdown token rather than open-ended: a batch of five
    /// thousand documents is committing or it is not, and a demo host that will not exit is worse than
    /// one that leaves a committed batch behind.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? inFlight;

        lock (gate)
        {
            cancellation?.Cancel();
            inFlight = running;
        }

        if (inFlight is null)
        {
            return;
        }

        try
        {
            await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting. The generator commits per batch, so nothing is torn.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    private bool TryStart(DemoDataProgress initial, Func<CancellationToken, Task> work, out string? error)
    {
        lock (gate)
        {
            if (running is { IsCompleted: false })
            {
                error = "A demo-data job is already running. Cancel it first.";
                return false;
            }

            cancellation?.Dispose();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

            progress = initial;

            CancellationToken token = cancellation.Token;

            // Task.Run, so the POST handler returns before the first COPY starts. The continuation is a
            // Func<Task>, never an async void: an unobserved exception here would take the host down
            // with no component in the stack to name.
            running = Task.Run(() => RunAsync(work, token), CancellationToken.None);
        }

        error = null;
        return true;
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        try
        {
            await work(token).ConfigureAwait(false);

            progress = progress with { State = DemoDataJobState.Completed };
        }
        // Not `catch (OperationCanceledException)`. Cancelling a COPY or a SaveChangesAsync surfaces as
        // an NpgsqlException or a MartenCommandException wrapping the cancellation, not as an
        // OperationCanceledException - so the only reliable question is whether the token was signalled,
        // and asking it first is what keeps a cancelled run from being reported as a failure.
#pragma warning disable CA1031 // Deliberate: any exception raised after a cancel is that cancel.
        catch (Exception) when (token.IsCancellationRequested)
#pragma warning restore CA1031
        {
            progress = progress with
            {
                State = DemoDataJobState.Cancelled,
                Error = "Cancelled. Whatever had already been committed is still there; truncate to remove it.",
            };
        }
#pragma warning disable CA1031 // The job is the top of its own stack: an exception here has nowhere else to go.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogError(exception, "The demo data job failed");

            progress = progress with { State = DemoDataJobState.Failed, Error = exception.Message };
        }
    }

    private async Task RunGenerateAsync(DemoDataGenerator generator, CancellationToken token)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        long ticks = Stopwatch.GetTimestamp();

        try
        {
            await generator.GenerateAsync(
                counters => progress = progress.With(counters, Stopwatch.GetElapsedTime(ticks)),
                token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever went wrong, the rows it already committed have to be findable.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The run record is written even for a run that did not finish, because the streams it did
            // append are only findable through it - and "cancel, then truncate" has to leave the
            // database as clean as "finish, then truncate" does.
            try
            {
                await generator.RecordPartialRunAsync(startedAt, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A bookkeeping write that fails must not replace the real failure.
            catch (Exception bookkeeping)
#pragma warning restore CA1031
            {
                logger.LogWarning(bookkeeping, "The demo data run could not be recorded after it failed");
            }

            throw;
        }
    }

    private async Task RunTruncateAsync(bool deleteAllEventData, CancellationToken token)
    {
        long ticks = Stopwatch.GetTimestamp();
        var truncator = new DemoDataTruncator(store);

        DemoDataTruncation result = await truncator.TruncateAsync(
            deleteAllEventData,
            archived => progress = progress with
            {
                Phase = "ArchivingStreams",
                StreamsArchived = archived,
                Elapsed = Stopwatch.GetElapsedTime(ticks),
            },
            token).ConfigureAwait(false);

        progress = progress with
        {
            Phase = deleteAllEventData ? "DeletedAllEventData" : "Done",
            StreamsArchived = result.StreamsArchived,
            Elapsed = Stopwatch.GetElapsedTime(ticks),
        };
    }
}
