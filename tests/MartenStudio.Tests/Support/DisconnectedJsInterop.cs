using Bunit;

using Microsoft.JSInterop;

namespace MartenStudio.Tests.Support;

/// <summary>
/// Configures bUnit's JS interop mock to fail one identifier the way a closed browser tab does.
/// </summary>
/// <remarks>
/// <see cref="Events.DisconnectingJsRuntime" /> proves the same thing for the Events page's own hand-rolled
/// <see cref="IJSRuntime" />, but every component this helper is used from is rendered against bUnit's own
/// <c>JSInterop</c> mock instead (<c>JSRuntimeMode.Loose</c>, configured per identifier with
/// <c>Setup</c>/<c>SetupVoid</c>), which is the pattern every other test in the JSON toolkit, the layout
/// and the schema and configuration pages already use. Rather than a second, competing fake
/// <see cref="IJSRuntime" />, this gives that same mock the one behaviour none of those tests could reach
/// otherwise: <see cref="JSDisconnectedException" /> is not a <see cref="JSException" />, and a test that
/// only ever throws the latter never proves the <c>catch (Exception e) when
/// (StudioLiveUpdates.IsInteropUnavailable(e))</c> filter actually covers the case that happens on every
/// closed browser tab.
/// </remarks>
internal static class DisconnectedJsInterop
{
    /// <summary>The exception a lost circuit throws out of every interop call in flight.</summary>
    public static JSDisconnectedException Exception() => new("The circuit has been disconnected.");

    /// <summary>
    /// Makes every call to <paramref name="identifier" /> that returns <typeparamref name="TResult" />
    /// throw <see cref="JSDisconnectedException" />, the way a closed browser tab does.
    /// </summary>
    public static void Disconnect<TResult>(this Bunit.BunitJSInterop jsInterop, string identifier) =>
        jsInterop.Setup<TResult>(identifier, _ => true).SetException(Exception());

    /// <summary>
    /// Makes every void call to <paramref name="identifier" /> throw <see cref="JSDisconnectedException" />,
    /// the way a closed browser tab does.
    /// </summary>
    /// <remarks>
    /// Also shadows bUnit's own built-in handler for <c>Blazor._internal.domWrapper.focus</c> - the one
    /// <see cref="Microsoft.AspNetCore.Components.ElementReferenceExtensions.FocusAsync(Microsoft.AspNetCore.Components.ElementReference)" />
    /// calls - which otherwise answers every focus call with success. A handler added after it is checked
    /// first, so this one wins.
    /// </remarks>
    public static void DisconnectVoid(this Bunit.BunitJSInterop jsInterop, string identifier) =>
        jsInterop.SetupVoid(identifier, _ => true).SetException(Exception());
}
