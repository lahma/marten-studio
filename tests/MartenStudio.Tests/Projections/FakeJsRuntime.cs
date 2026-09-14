using System.Collections.Concurrent;

using Microsoft.JSInterop;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// A JavaScript runtime that records what was called and hands back what a test says the browser would.
/// </summary>
/// <remarks>
/// Hand-written rather than mocked, and deliberately not bUnit's: these tests are about
/// <c>StudioLiveUpdates</c> on its own, with no component and no renderer, and what they need to see is
/// that the <c>DotNetObjectReference</c> was passed to <c>visibility.watch</c> and given back on dispose.
/// </remarks>
internal sealed class FakeJsRuntime : IJSRuntime
{
    /// <summary>Every identifier that was invoked, in order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>What <c>visibility.watch</c> answers: whether the tab starts hidden.</summary>
    public bool StartsHidden { get; set; }

    /// <summary>What every call throws, when a test is about prerendering or a browser that refused.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The .NET reference the page handed the browser, so a test can call back through it.</summary>
    public object? DotNetReference { get; private set; }

    /// <summary>Whether the reference was given back.</summary>
    public bool Unwatched => Calls.Contains("martenStudio.visibility.unwatch");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        Invoke<TValue>(identifier, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        Invoke<TValue>(identifier, args);

    private ValueTask<TValue> Invoke<TValue>(string identifier, object?[]? args)
    {
        Calls.Enqueue(identifier);

        if (Failure is not null)
        {
            return ValueTask.FromException<TValue>(Failure);
        }

        if (identifier == "martenStudio.visibility.watch")
        {
            DotNetReference = args?.Length > 0 ? args[0] : null;
            return ValueTask.FromResult((TValue) (object) StartsHidden);
        }

        return ValueTask.FromResult(default(TValue)!);
    }
}
