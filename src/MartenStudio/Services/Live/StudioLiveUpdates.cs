using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MartenStudio.Services.Live;

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
    private volatile bool hidden;
    private bool disposed;

    public StudioLiveUpdates(IJSRuntime jsRuntime, ILogger<StudioLiveUpdates> logger)
    {
        this.jsRuntime = jsRuntime;
        this.logger = logger;
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

    /// <summary>Whether the browser says this tab is hidden. Polling is suspended while it is.</summary>
    public bool IsHidden => hidden;

    /// <summary>Whether the loop is running.</summary>
    public bool IsRunning => loop is { IsCompleted: false };

    /// <summary>The last tick failure, or <see langword="null" />. A value, not an exception.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>How many ticks have actually run, for a test and for nothing else.</summary>
    internal int TickCount { get; private set; }

    /// <summary>
    /// Starts polling.
    /// </summary>
    /// <param name="interval">How often to tick - <c>MartenStudioOptions.RefreshInterval</c>.</param>
    /// <param name="tick">What to do on each tick. Its cancellation token is cancelled on dispose.</param>
    /// <param name="cancellationToken">Cancels the start itself, not the loop.</param>
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
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

        await WatchVisibilityAsync(cancellationToken).ConfigureAwait(false);

        loop = RunAsync(interval, cancellation.Token);
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
    public void OnVisibilityChanged(bool isHidden) => hidden = isHidden;

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
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The loop ending is what was asked for.
            }
        }

        await UnwatchVisibilityAsync().ConfigureAwait(false);

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

            logger.LogDebug(exception, "A Marten Studio live update failed");

            Func<Exception, Task>? failed = Failed;
            if (failed is not null)
            {
                try
                {
                    await failed(exception).ConfigureAwait(false);
                }
                catch (Exception handlerFailure) when (handlerFailure is not OperationCanceledException)
                {
                    logger.LogWarning(handlerFailure, "A Marten Studio page failed to handle a live update failure");
                }
            }
        }
    }

    private async Task WatchVisibilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            selfReference = DotNetObjectReference.Create(this);
            hidden = await jsRuntime
                .InvokeAsync<bool>("martenStudio.visibility.watch", cancellationToken, watchToken, selfReference)
                .ConfigureAwait(false);
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
            await jsRuntime.InvokeVoidAsync("martenStudio.visibility.unwatch", watchToken).ConfigureAwait(false);
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
    /// All three happen normally: <c>JSException</c> when the function is missing,
    /// <c>InvalidOperationException</c> during prerendering, and <c>JSDisconnectedException</c> when the
    /// circuit closed before <c>DisposeAsync</c> ran - which is the common case, not the rare one.
    /// </remarks>
    private static bool IsInteropUnavailable(Exception exception) =>
        exception is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException
        || exception is System.Text.Json.JsonException;
}
