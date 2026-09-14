using Microsoft.JSInterop;

namespace MartenStudio.Tests.Events;

/// <summary>
/// A JavaScript runtime that answers the visibility watch and then loses the circuit: every call after
/// the first <c>visibility.watch</c> throws <see cref="JSDisconnectedException" />.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape a real Blazor Server circuit has when a browser tab is closed. It is worth having a
/// runtime for rather than a planned bUnit invocation, because the bug it reproduces is about which
/// <em>type</em> comes out: <see cref="JSDisconnectedException" /> derives from <see cref="Exception" />
/// and not from <see cref="JSException" />, so a <c>catch (JSException)</c> ladder lets it through and it
/// escapes <c>DisposeAsync</c> - taking the rest of the disposal with it.
/// </para>
/// <para>
/// It keeps the <see cref="DotNetObjectReference{TValue}" /> the page handed over, so a test can prove the
/// page disposed it anyway: a disposed reference throws <see cref="ObjectDisposedException" /> from its
/// <c>Value</c>, and an undisposed one pins the component - and the whole render tree behind it - for the
/// life of the process.
/// </para>
/// </remarks>
internal sealed class DisconnectingJsRuntime : IJSRuntime
{
    /// <summary>Every identifier that was invoked, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The .NET reference the page handed the browser.</summary>
    public object? DotNetReference { get; private set; }

    /// <summary>The token <c>visibility.watch</c> was keyed on.</summary>
    public string? WatchToken { get; private set; }

    /// <summary>The token <c>visibility.unwatch</c> was given, if the call got that far.</summary>
    public string? UnwatchToken { get; private set; }

    /// <summary>Whether the page tried to give the watch token back before disposing the reference.</summary>
    public bool TriedToUnwatch => Calls.Contains("martenStudio.visibility.unwatch");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        Invoke<TValue>(identifier, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        Invoke<TValue>(identifier, args);

    private ValueTask<TValue> Invoke<TValue>(string identifier, object?[]? args)
    {
        Calls.Add(identifier);

        if (identifier == "martenStudio.visibility.watch")
        {
            WatchToken = args?.Length > 0 ? args[0] as string : null;
            DotNetReference = args?.Length > 1 ? args[1] : null;

            // The tab starts visible, so follow mode is not paused and the page behaves normally until
            // the circuit goes.
            return ValueTask.FromResult((TValue) (object) false);
        }

        if (identifier == "martenStudio.visibility.unwatch")
        {
            UnwatchToken = args?.Length > 0 ? args[0] as string : null;
        }

        return ValueTask.FromException<TValue>(
            new JSDisconnectedException("The circuit has been disconnected."));
    }
}
