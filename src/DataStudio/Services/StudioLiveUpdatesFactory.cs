using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DataStudio.Services;

/// <summary>
/// Hands a page its own polling loop without the container holding on to it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="StudioLiveUpdates" /> is <see cref="IAsyncDisposable" />, and a DI container disposes
/// everything disposable it creates. Registered transient in a Blazor circuit that means the circuit's
/// scope keeps a reference to <em>every</em> loop any page on that circuit ever asked for - each one
/// holding a <see cref="DotNetObjectReference{TValue}" /> and, through the tick callback, the component
/// that created it - until the circuit ends. A studio somebody navigates around for an afternoon then
/// accumulates one live-updates object per page visit, none of which the page's own
/// <c>DisposeAsync</c> can undo, because disposing an object does not untrack it.
/// </para>
/// <para>
/// This factory is what the container holds instead. It owns nothing, is not disposable, and hands back
/// an instance whose lifetime belongs entirely to the component that asked for it - which is the only
/// thing that knows when the loop should stop.
/// </para>
/// </remarks>
internal sealed class StudioLiveUpdatesFactory
{
    private readonly IJSRuntime jsRuntime;
    private readonly ILoggerFactory loggerFactory;
    private readonly StudioLiveLog log;

    /// <remarks>
    /// <paramref name="log" /> is the module's words for what the loop logs, which the module that registered the shell gives. A module that throttles its
    /// lines closes over its own throttle there; the shell names none.
    /// </remarks>
    public StudioLiveUpdatesFactory(
        IJSRuntime jsRuntime,
        ILoggerFactory loggerFactory,
        StudioLiveLog log)
    {
        ArgumentNullException.ThrowIfNull(log);

        this.jsRuntime = jsRuntime;
        this.loggerFactory = loggerFactory;
        this.log = log;
    }

    /// <summary>
    /// A polling loop for one page. The caller disposes it from its own <c>DisposeAsync</c>.
    /// </summary>
    public StudioLiveUpdates Create() =>
        new(jsRuntime, loggerFactory.CreateLogger<StudioLiveUpdates>(), log);
}
