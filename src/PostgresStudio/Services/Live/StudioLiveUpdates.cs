using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Live;

/// <summary>
/// One page's polling loop: a timer that stops while the tab is hidden and is cancelled when the page
/// goes away.
/// </summary>
/// <remarks>
/// <para>
/// This is the reusable half of plan D10, and it is written to be reused: any page that wants live
/// numbers injects one, calls <see cref="StartAsync" /> with an interval and a callback, and disposes it
/// from <c>DisposeAsync</c>. The projections page is the first caller; the feed's follow mode and the
/// Overview's tiles are meant to be the next ones.
/// </para>
/// <para>
/// <b>How to use it from a component.</b>
/// </para>
/// <code>
/// @implements IAsyncDisposable
/// @inject StudioLiveUpdates Live
///
/// protected override async Task OnAfterRenderAsync(bool firstRender)
/// {
///     if (!firstRender) return;
///     Live.Failed += OnPollFailedAsync;             // Func&lt;Exception, Task&gt;, never an EventHandler
///     await Live.StartAsync(Options.Value.RefreshInterval, RefreshAsync);
/// }
///
/// public async ValueTask DisposeAsync() =&gt; await Live.DisposeAsync();
/// </code>
/// <para>
/// Three things it does that a bare <see cref="PeriodicTimer" /> in a component does not. It pauses on a
/// hidden tab, so a studio left open in a background tab stops querying entirely. It never lets a tick
/// escape as an unobserved exception - the callback's failure is handed to <see cref="Failed" /> and kept
/// on <see cref="LastError" />, which is what lets a page render "could not refresh" instead of dying
/// (AGENTS.md hard rule 6 - there is no <c>async void</c> anywhere in this file). And it disposes its
/// <see cref="DotNetObjectReference{TValue}" />, which otherwise pins this object - and the whole
/// component graph behind it - for the life of the process.
/// </para>
/// <para>
/// It never blocks the circuit: <see cref="StartAsync" /> returns as soon as the loop is running, and the
/// loop itself lives on the thread pool.
/// </para>
/// </remarks>
internal sealed class StudioLiveUpdates : IAsyncDisposable
{
    private readonly IJSRuntime jsRuntime;
    private readonly ILogger<StudioLiveUpdates> logger;
    private readonly StudioLogThrottle? throttle;
    private readonly StudioProduct product;

    /// <summary>
    /// What the browser-side watcher list is keyed on for this instance.
    /// </summary>
    /// <remarks>
    /// Not the <see cref="DotNetObjectReference{TValue}" />: Blazor marshals one as an id and the browser
    /// materialises a fresh JS wrapper for every call, so the object that reaches <c>unwatch</c> is never
    /// the object that reached <c>watch</c>. Keyed on a reference, <c>unwatch</c> removed nothing, the
    /// document listener stayed attached for the life of the page, and every closed circuit left another
    /// dead reference behind it. A string generated here survives the round trip unchanged.
    /// </remarks>
    private readonly string watchToken = Guid.NewGuid().ToString("N");

    private DotNetObjectReference<StudioLiveUpdates>? selfReference;
    private CancellationTokenSource? cancellation;
    private Task? loop;
    private Func<CancellationToken, Task>? onTick;
    private readonly object raiseGate = new();
    private Task raised = Task.CompletedTask;
    private volatile bool hidden;
    private volatile bool disposed;

    /// <remarks>
    /// <paramref name="throttle" /> is optional so that a test can build a loop by hand; the factory
    /// always hands the container's over, and without one a failing handler is simply a Warning. So is
    /// <paramref name="product" />, the studio whose words the loop logs in.
    /// </remarks>
    public StudioLiveUpdates(
        IJSRuntime jsRuntime,
        ILogger<StudioLiveUpdates> logger,
        StudioLogThrottle? throttle = null,
        StudioProduct product = StudioProduct.MartenStudio)
    {
        this.jsRuntime = jsRuntime;
        this.logger = logger;
        this.throttle = throttle;
        this.product = product;
    }

    /// <summary>
    /// Raised when a tick threw, so the page can put the failure on screen.
    /// </summary>
    /// <remarks>
    /// <see cref="Func{T, TResult}" /> returning a <see cref="Task" /> rather than an
    /// <see cref="EventHandler" />: a handler that has to re-render is asynchronous, and the
    /// <c>async void</c> an <c>EventHandler</c> forces is the one shape whose exception kills a circuit
    /// silently.
    /// </remarks>
    public event Func<Exception, Task>? Failed;

    /// <summary>
    /// Raised when the tab hides or shows, after <see cref="IsHidden" /> changed; never for the state the watch started in (that one is the
    /// watch call's answer, not a change). The argument is whether the tab is now hidden. A page that must stop reading while nobody looks - a module's
    /// page that follows a stream as it grows - subscribes; everything else keeps polling on the flag and never hears of it.
    /// </summary>
    /// <remarks>
    /// Raised on the thread pool, one change at a time and in the order the browser sent them, and only when the value changed: the same value sent
    /// twice is not a change. <c>Func&lt;bool, Task&gt;</c> and not an <see cref="EventHandler" /> (hard rule 6), and a handler that throws is handed to
    /// <see cref="Failed" /> like a tick's failure, so it never reaches the circuit.
    /// </remarks>
    public event Func<bool, Task>? VisibilityChanged;

    /// <summary>Whether the browser says this tab is hidden. Polling is suspended while it is.</summary>
    public bool IsHidden => hidden;

    /// <summary>Whether the loop is running.</summary>
    public bool IsRunning => loop is { IsCompleted: false };

    /// <summary>The last tick failure, or <see langword="null" />. A value, not an exception.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>How many ticks have actually run, for a test and for nothing else.</summary>
    internal int TickCount { get; private set; }

    /// <summary>The raise of the last visibility change, finished or not: what a test waits on to know that every handler has heard of it. For a test and for nothing else.</summary>
    internal Task VisibilityChangesRaised
    {
        get
        {
            lock (raiseGate)
            {
                return raised;
            }
        }
    }

    /// <summary>
    /// Starts polling.
    /// </summary>
    /// <param name="interval">How often to tick - <c>MartenStudioOptions.RefreshInterval</c>.</param>
    /// <param name="tick">What to do on each tick. Its cancellation token is cancelled on dispose.</param>
    /// <param name="cancellationToken">Cancels the start itself, not the loop.</param>
    /// <remarks>
    /// <para>
    /// <b>Two halves, and neither may be dropped (1.2-LIVE-FIX).</b> What fixes the order of events across the watch call is the
    /// last paragraph below: the source and its token are taken before the await and <c>disposed</c> is checked after it. The
    /// <see cref="NullReferenceException" /> that used to end the circuit came from that order (a page left during the await had
    /// nulled the source the start then read) and would happen on a single-threaded context too.
    /// </para>
    /// <para>
    /// <b>Never <c>ConfigureAwait(false)</c> on this method's own awaits.</b> A component calls it from <c>OnAfterRenderAsync</c>,
    /// on the renderer's dispatcher, and <see cref="DisposeAsync" /> is called from the same dispatcher. Keeping the context is what
    /// lets the <c>disposed</c> check run without racing the dispose (the two run one at a time), and it keeps every interop call
    /// (watch, unwatch) on the renderer, in order - resuming on the thread pool also let two instances' calls reach a JS runtime from
    /// two threads at once. The loop itself, <c>RunAsync</c>, does run on the pool.
    /// </para>
    /// <para>
    /// The source and its token are taken before the await, and <c>disposed</c> is checked after it: a page left during the watch
    /// call has already cancelled and disposed that source and sent its unwatch, so the start starts nothing.
    /// </para>
    /// </remarks>
    public async Task StartAsync(TimeSpan interval, Func<CancellationToken, Task> tick, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (IsRunning)
        {
            return;
        }

        onTick = tick;

        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cancellation = source;
        CancellationToken token = source.Token;

        await WatchVisibilityAsync(cancellationToken);

        if (disposed)
        {
            // Left during the watch call: DisposeAsync cancelled and disposed the source, and unwatched (after the watch, in the order
            // the browser received them). Nothing is left to start.
            return;
        }

        loop = RunAsync(interval, token);
    }

    /// <summary>
    /// Called from the browser when the tab is hidden or shown.
    /// </summary>
    /// <remarks>
    /// Public because JavaScript calls it; <c>[JSInvokable]</c> on a non-public method is not reachable.
    /// It only sets a flag - the loop decides what to do about it on its next tick, so a burst of
    /// visibility changes costs nothing.
    /// </remarks>
    /// <param name="isHidden">Whether the tab is now hidden.</param>
    [JSInvokable]
    public void OnVisibilityChanged(bool isHidden)
    {
        bool changed;

        lock (raiseGate)
        {
            changed = hidden != isHidden;
            hidden = isHidden;

            if (changed && !disposed && VisibilityChanged is not null)
            {
                // Chained, so that two quick changes reach the handlers in the order they happened; the chain never faults (the raise catches).
                Task previous = raised;
                raised = Task.Run(() => RaiseVisibilityChangedAsync(previous, isHidden));
            }
        }
    }

    /// <summary>
    /// Runs one tick now, outside the timer. What a Refresh button calls.
    /// </summary>
    public async Task TickNowAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task>? tick = onTick;
        if (tick is null)
        {
            return;
        }

        await RunOneAsync(tick, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the loop, unwatches the tab and releases the .NET reference the browser holds.</summary>
    /// <remarks>
    /// Idempotent, and it never throws for interop loss. Like <see cref="StartAsync" /> it keeps the caller's synchronization context
    /// (no <c>ConfigureAwait(false)</c>): it is called on the renderer's dispatcher, so it never runs beside a start, and its unwatch
    /// is issued from the renderer like every other interop call, one instance at a time (1.2-LIVE-FIX).
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (cancellation is not null)
        {
            await cancellation.CancelAsync();
        }

        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // The loop ending is what was asked for.
            }
        }

        await UnwatchVisibilityAsync();

        selfReference?.Dispose();
        selfReference = null;

        cancellation?.Dispose();
        cancellation = null;
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // Yield first, so StartAsync returns to the renderer before the first tick runs: a page must
        // never wait on its own polling loop.
        await Task.Yield();

        using PeriodicTimer timer = new(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (hidden)
                {
                    // Nobody is looking. The next tick after the tab comes back is a full refresh, which
                    // is what a person switching back to the tab expects anyway.
                    continue;
                }

                Func<CancellationToken, Task>? tick = onTick;
                if (tick is null)
                {
                    continue;
                }

                await RunOneAsync(tick, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private async Task RunOneAsync(Func<CancellationToken, Task> tick, CancellationToken cancellationToken)
    {
        try
        {
            await tick(cancellationToken).ConfigureAwait(false);
            TickCount++;
            LastError = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A failure is a value here. It is kept, reported to whoever asked, and the loop carries on -
            // a database that comes back is one the next tick renders.
            LastError = exception;
            TickCount++;

            if (product == StudioProduct.PostgresStudio)
            {
                logger.LogDebug(exception, "A Postgres Studio live update failed");
            }
            else
            {
                logger.LogDebug(exception, "A Marten Studio live update failed");
            }

            await ReportFailureAsync(exception).ConfigureAwait(false);
        }
    }

    /// <summary>Hands a failure to <see cref="Failed" />; a handler that throws is a bug in a page and is logged as 9220, never rethrown.</summary>
    private async Task ReportFailureAsync(Exception exception)
    {
        Func<Exception, Task>? failed = Failed;
        if (failed is not null)
        {
            try
            {
                await failed(exception).ConfigureAwait(false);
            }
            catch (Exception handlerFailure) when (handlerFailure is not OperationCanceledException)
            {
                // A bug in a page, on a loop that runs every RefreshInterval while the read keeps
                // failing: event 9220, a Warning once per kind of failure per window.
                LogLevel level = StudioLogThrottle.LevelOrWarning(
                    throttle, "LiveUpdates.FailedHandler", null, null, StudioLogThrottle.KindOf(handlerFailure));

                if (product == StudioProduct.PostgresStudio)
                {
                    logger.PostgresStudioLiveUpdateHandlerFailed(level, handlerFailure);
                }
                else
                {
                    logger.LiveUpdateHandlerFailed(level, handlerFailure);
                }
            }
        }
    }

    private async Task RaiseVisibilityChangedAsync(Task previous, bool isHidden)
    {
        // The previous raise never faults, but a chain must not depend on that.
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already reported by the raise that failed.
        }

        Func<bool, Task>? changed = VisibilityChanged;
        if (changed is null || disposed)
        {
            return;
        }

        try
        {
            // Each subscriber on its own: one that throws must not keep the others from hearing of the change.
            foreach (Func<bool, Task> handler in changed.GetInvocationList().Cast<Func<bool, Task>>())
            {
                try
                {
                    await handler(isHidden).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The page ended while it handled the change.
                }
                catch (Exception exception)
                {
                    LastError = exception;
                    await ReportFailureAsync(exception).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The last resort: nothing here may become an unobserved exception.
            logger.LogDebug(exception, "A live update visibility handler failed to be reported");
        }
    }

    private async Task WatchVisibilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            selfReference = DotNetObjectReference.Create(this);
            hidden = await jsRuntime.InvokeAsync<bool>("martenStudio.visibility.watch", cancellationToken, watchToken, selfReference);
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
            // Prerendering, or a browser that refused. Polling then simply never pauses, which is the
            // safe way round: a page that shows stale numbers is worse than one that queries too often.
            selfReference?.Dispose();
            selfReference = null;
            hidden = false;
        }
    }

    private async ValueTask UnwatchVisibilityAsync()
    {
        if (selfReference is null)
        {
            return;
        }

        try
        {
            await jsRuntime.InvokeVoidAsync("martenStudio.visibility.unwatch", watchToken);
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
            // The circuit is already gone; the browser-side watcher drops the reference itself.
        }
    }

    /// <summary>
    /// Whether this is JavaScript being unreachable rather than a bug.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of them happen normally: <c>JSException</c> when the function is missing,
    /// <c>InvalidOperationException</c> during prerendering, and <c>JSDisconnectedException</c> when the
    /// circuit closed before <c>DisposeAsync</c> ran - which is the common case, not the rare one.
    /// </para>
    /// <para>
    /// <b>Every interop call site in the studio must go through this, and a list of <c>catch</c> clauses
    /// is not a substitute for it.</b> <c>JSDisconnectedException</c> derives from <see cref="Exception" />
    /// and <em>not</em> from <c>JSException</c> (verified against
    /// <c>Microsoft.JSInterop.JSDisconnectedException</c>), so the obvious
    /// <c>catch (JSException) { } catch (InvalidOperationException) { }</c> ladder misses exactly the case
    /// that happens on every closed browser tab - and an exception escaping an <c>await using</c> or a
    /// component's <c>DisposeAsync</c> takes the rest of that disposal with it. Hence
    /// <c>catch (Exception e) when (StudioLiveUpdates.IsInteropUnavailable(e))</c>, which is a filter and
    /// therefore still rethrows anything that is a real bug.
    /// </para>
    /// </remarks>
    /// <param name="exception">The exception an interop call threw.</param>
    /// <returns>Whether it means "the browser is not reachable" rather than "this code is wrong".</returns>
    internal static bool IsInteropUnavailable(Exception exception) =>
        exception is JSException or JSDisconnectedException or InvalidOperationException
            or OperationCanceledException or ObjectDisposedException
        || exception is System.Text.Json.JsonException;
}
