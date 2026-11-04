using AngleSharp.Dom;

using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MartenStudio.Tests.Support;

/// <summary>
/// Clicks an element of the component's current render, however many renders happen between finding it and clicking it (1.2-FLK3).
/// </summary>
/// <remarks>
/// <para>
/// <c>Find(...).ClickAsync(...)</c> is two steps with a gap: the element found carries the event handler id of the render it was found
/// in, and a render in the gap replaces it. Blazor then refuses the click with <c>UnknownEventHandlerIdException</c> - the
/// handler it names is gone - and the click was never dispatched. A page that renders on its own (the Query panel's run timer renders
/// at 200 ms, from the thread pool, whenever it is due) makes that a matter of how long the machine stalls between the two lines: on a
/// starved core it happens, about once in two thousand clicks, and the test fails with an exception that says nothing about what it
/// tested.
/// </para>
/// <para>
/// A refused click did nothing, so finding the element again and clicking it is not a second click. The retry is bounded by
/// <see cref="TestBounds.Gate" />; any other exception, and the last one at the bound, is the test's.
/// </para>
/// </remarks>
internal static class RenderRace
{
    /// <summary>Finds the element with <paramref name="find" /> and clicks it, finding it again if a render took its handler.</summary>
    /// <param name="find">Looks the element up in the component's current render.</param>
    public static async Task ClickCurrentAsync(Func<IElement> find)
    {
        ArgumentNullException.ThrowIfNull(find);

        using CancellationTokenSource bound = new(TestBounds.Gate);

        while (true)
        {
            try
            {
                await find().ClickAsync(new MouseEventArgs());
                return;
            }
            catch (Exception exception) when (!bound.IsCancellationRequested && IsHandlerGone(exception))
            {
                // A render replaced the handler between the find and the click; the click went nowhere.
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="exception" /> is Blazor's refusal of a handler id that is no longer in the render tree. Named rather than
    /// caught by type: the type is not public in the framework or in bUnit.
    /// </summary>
    private static bool IsHandlerGone(Exception exception) =>
        exception.GetType().Name == "UnknownEventHandlerIdException";

    /// <summary>Clicks the first element matching <paramref name="selector" /> in <paramref name="component" />'s current render.</summary>
    /// <typeparam name="TComponent">The component.</typeparam>
    /// <param name="component">The rendered component.</param>
    /// <param name="selector">A CSS selector.</param>
    public static Task ClickCurrentAsync<TComponent>(this IRenderedComponent<TComponent> component, string selector)
        where TComponent : IComponent
    {
        ArgumentNullException.ThrowIfNull(component);

        return ClickCurrentAsync(() => component.Find(selector));
    }
}
