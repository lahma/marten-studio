using System.Globalization;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// Runs the "make it unhealthy" switches: one background task per switch that is on, each turned off by hand, by its
/// timeout, or by the host stopping - and every one cleaned up whichever it was.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hosted singleton, like <see cref="DemoDataJob" />.</b> A switch outlives the request that turned it on, and the
/// host's shutdown is what has to end it when nobody turns it off: each run's cancellation is linked to
/// <see cref="IHostApplicationLifetime.ApplicationStopping" />, and <see cref="StopAsync" /> waits for every clean-up, so
/// Ctrl+C rolls the transactions back and puts autovacuum back rather than leaving the server to notice a dropped
/// connection.
/// </para>
/// <para>
/// <b>Bounded, always.</b> Every run carries a timeout - <see cref="DefaultTimeout" /> unless a caller says otherwise -
/// and turns itself off when it passes, so a demo never leaves a box broken. All four are off at start-up; a restart is
/// a clean slate.
/// </para>
/// <para>
/// <b>Never <c>async void</c>.</b> A run is a <see cref="Task" /> started with <see cref="Task.Run(Func{Task})" />, held
/// here and awaited on the way out; its failure is caught at the top of its own stack, logged, and shown on the panel.
/// </para>
/// </remarks>
internal sealed class UnhealthySwitchBoard : IHostedService, IDisposable
{
    /// <summary>How long a switch stays on unless it is turned off first.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long turning a switch off waits for its clean-up before answering.</summary>
    private static readonly TimeSpan TurnOffWait = TimeSpan.FromSeconds(45);

    private readonly Dictionary<UnhealthySwitchKind, Slot> slots;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<UnhealthySwitchBoard> logger;
    private readonly TimeProvider time;
    private readonly Lock gate = new();

    private volatile bool stopping;

    /// <summary>Wires the board to its switches and to the lifetime that stops them.</summary>
    /// <param name="switches">The switches, one of each kind.</param>
    /// <param name="database">Which database they act on, and so which of them may run (<see cref="UnhealthySwitchesDatabase" />).</param>
    /// <param name="lifetime">The host's lifetime: its stopping token ends every run.</param>
    /// <param name="logger">Where a run's start, end and failure are written.</param>
    /// <param name="time">The clock the panel's times come from; the system's when <see langword="null" />.</param>
    public UnhealthySwitchBoard(
        IEnumerable<UnhealthySwitch> switches,
        UnhealthySwitchesDatabase database,
        IHostApplicationLifetime lifetime,
        ILogger<UnhealthySwitchBoard> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        slots = switches.ToDictionary(static x => x.Kind, static x => new Slot(x));
        Database = database;
        this.lifetime = lifetime;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Which database the switches act on.</summary>
    public UnhealthySwitchesDatabase Database { get; }

    /// <summary>Why a switch may not be turned on here, or <see langword="null" />.</summary>
    public string? Refusal(UnhealthySwitchKind kind) => Database.RefusalFor(slots[kind].Switch);

    /// <summary>The switches, in the order the panel shows them.</summary>
    public IReadOnlyList<UnhealthySwitch> Switches =>
        [.. slots.Values.Select(static x => x.Switch).OrderBy(static x => x.Kind)];

    /// <summary>The switch with this URL name, or <see langword="null" />.</summary>
    public UnhealthySwitch? Find(string? slug) =>
        slots.Values.Select(static x => x.Switch).FirstOrDefault(x => string.Equals(x.Slug, slug, StringComparison.Ordinal));

    /// <summary>The switch's latest state. Safe from any thread.</summary>
    public UnhealthySwitchStatus Status(UnhealthySwitchKind kind) => slots[kind].Status;

    /// <summary>
    /// Turns a switch on, unless it already is.
    /// </summary>
    /// <param name="kind">Which switch.</param>
    /// <param name="error">Why it was refused.</param>
    /// <param name="timeout">When it turns itself off; <see cref="DefaultTimeout" /> when <see langword="null" />.</param>
    /// <returns>Whether a run was started.</returns>
    public bool TryTurnOn(UnhealthySwitchKind kind, out string? error, TimeSpan? timeout = null)
    {
        Slot slot = slots[kind];
        TimeSpan lasts = timeout ?? DefaultTimeout;

        if (lasts <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "A switch has to stay on for some time.");
        }

        // The refusal lives here, where the operation does, and not only on the panel's disabled button.
        if (Database.RefusalFor(slot.Switch) is { } refused)
        {
            error = refused;
            return false;
        }

        lock (gate)
        {
            if (slot.Running is { IsCompleted: false })
            {
                error = slot.Switch.Title + " is already on. Turn it off first.";
                return false;
            }

            if (stopping || lifetime.ApplicationStopping.IsCancellationRequested)
            {
                error = "The host is stopping.";
                return false;
            }

            slot.Cancellation?.Dispose();
            slot.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            slot.Cancellation.CancelAfter(lasts);
            slot.TurnedOffByHand = false;

            DateTimeOffset now = time.GetUtcNow();
            slot.Status = new UnhealthySwitchStatus(kind, UnhealthySwitchState.Starting, "Setting up.", now, now + lasts, slot.Status.LastOutcome);

            CancellationToken token = slot.Cancellation.Token;
            slot.Running = Task.Run(() => RunAsync(slot, lasts, token), CancellationToken.None);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("The {Switch} switch was turned on for {Timeout}", slot.Switch.Title, lasts);
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Turns a switch off and waits, for a bounded time, until its clean-up is done. Does nothing to a switch that is off.
    /// </summary>
    /// <param name="kind">Which switch.</param>
    /// <returns>Whether the clean-up finished within the wait.</returns>
    public async Task<bool> TurnOffAsync(UnhealthySwitchKind kind)
    {
        Slot slot = slots[kind];
        CancellationTokenSource? cancellation;
        Task? running;

        lock (gate)
        {
            running = slot.Running;

            if (running is null || running.IsCompleted)
            {
                return true;
            }

            slot.TurnedOffByHand = true;
            cancellation = slot.Cancellation;
            slot.Status = slot.Status with { State = UnhealthySwitchState.Stopping, Detail = "Cleaning up." };
        }

        // Outside the lock: cancelling runs the token's callbacks, and a statement in flight is cancelled through them.
        await CancelAsync(cancellation).ConfigureAwait(false);

        return await WaitAsync(running, TurnOffWait, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Nothing to start: every switch is off until somebody turns it on.</summary>
    /// <param name="cancellationToken">Ignored.</param>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Turns every switch off and waits for their clean-ups, for as long as the host is prepared to wait.
    /// </summary>
    /// <param name="cancellationToken">The host's shutdown timeout.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<(CancellationTokenSource? Cancellation, Task Running)> inFlight = [];

        lock (gate)
        {
            stopping = true;

            foreach (Slot slot in slots.Values)
            {
                if (slot.Running is { IsCompleted: false } running)
                {
                    inFlight.Add((slot.Cancellation, running));
                }
            }
        }

        foreach ((CancellationTokenSource? cancellation, Task _) in inFlight)
        {
            await CancelAsync(cancellation).ConfigureAwait(false);
        }

        foreach ((CancellationTokenSource? _, Task running) in inFlight)
        {
            await WaitAsync(running, Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            foreach (Slot slot in slots.Values)
            {
                slot.Cancellation?.Dispose();
                slot.Cancellation = null;
            }
        }
    }

    private static async Task CancelAsync(CancellationTokenSource? cancellation)
    {
        try
        {
            if (cancellation is not null)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // The board is being disposed with the host: the run has its own stopping token already.
        }
    }

    private static async Task<bool> WaitAsync(Task running, TimeSpan wait, CancellationToken cancellationToken)
    {
        try
        {
            await running.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // The host gave up waiting; the run's own clean-up timeout still bounds it.
            return false;
        }
    }

    private async Task RunAsync(Slot slot, TimeSpan lasts, CancellationToken token)
    {
        UnhealthySwitch @switch = slot.Switch;

        try
        {
            await @switch.RunAsync(
                detail => slot.Status = slot.Status with { State = UnhealthySwitchState.On, Detail = detail },
                token).ConfigureAwait(false);

            Ended(slot, UnhealthySwitchState.Off, Outcome(slot, lasts));
        }
        // Not `catch (OperationCanceledException)`: a statement cancelled through Npgsql surfaces as an NpgsqlException
        // wrapping the cancellation, so whether the token fired is the only reliable question (as in DemoDataJob). Except
        // for a clean-up that failed: that is never the turning off, and falls through to the failure below.
#pragma warning disable CA1031 // Deliberate: anything raised after the switch was turned off is that turning off.
        catch (Exception exception) when (token.IsCancellationRequested && exception is not UnhealthySwitchCleanUpException)
#pragma warning restore CA1031
        {
            Ended(slot, UnhealthySwitchState.Off, Outcome(slot, lasts));
        }
#pragma warning disable CA1031 // The run is the top of its own stack: an exception here has nowhere else to go.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // A clean-up failure lands here too: whatever the switch held may still be held, which is a Warning.
            logger.LogWarning(exception, "The {Switch} switch failed", @switch.Title);
            Ended(slot, UnhealthySwitchState.Failed, "Failed: " + exception.Message);
        }
    }

    private void Ended(Slot slot, UnhealthySwitchState state, string outcome)
    {
        slot.Status = slot.Status with { State = state, Detail = null, TurnsOffAt = null, LastOutcome = outcome };

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("The {Switch} switch is off: {Outcome}", slot.Switch.Title, outcome);
        }
    }

    private string Outcome(Slot slot, TimeSpan lasts)
    {
        string at = " at " + time.GetUtcNow().UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC.";

        if (slot.TurnedOffByHand)
        {
            return "Turned off" + at;
        }

        return stopping || lifetime.ApplicationStopping.IsCancellationRequested
            ? "Turned off when the host stopped" + at
            : "Turned itself off after " + Describe(lasts) + at;
    }

    /// <summary>"10 min", "45 s".</summary>
    internal static string Describe(TimeSpan span) =>
        span >= TimeSpan.FromMinutes(1) && span.Seconds == 0
            ? span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min"
            : span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";

    private sealed class Slot(UnhealthySwitch @switch)
    {
        private volatile UnhealthySwitchStatus status = UnhealthySwitchStatus.Initial(@switch.Kind);

        public UnhealthySwitch Switch { get; } = @switch;

        public UnhealthySwitchStatus Status
        {
            get => status;
            set => status = value;
        }

        public CancellationTokenSource? Cancellation { get; set; }

        public Task? Running { get; set; }

        public bool TurnedOffByHand { get; set; }
    }
}
