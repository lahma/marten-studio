using DataStudio.Services;

using Microsoft.AspNetCore.Components;

namespace DataStudio.Components;

/// <summary>
/// The base of every component that spells a time in the visitor's time zone: it draws again, from what it already holds, when that zone
/// moves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cascading value.</b> A time-zone change used to reach a page as a scope change, so every page that formats a time read its data
/// again, and a page that did not subscribe (a module's with no scope gate) did not draw again at all. Blazor draws a
/// retained page again only when its parameters change, and a page has none, so nothing short of a value that every such component takes
/// can reach them all in one render pass. <c>StudioLayout</c> cascades <see cref="StudioPreferences.DisplayVersion" /> under
/// <see cref="StudioPreferences.DisplayVersionName" /> around everything it draws; it moves whenever the circuit's zone does; a component
/// that takes it is drawn again by that, and its children by the usual rule.
/// </para>
/// <para>
/// The component never reads anything because of it: <see cref="DisplayVersion" /> is a number to be drawn again for, and a component
/// that wants to react to it (a window typed in the visitor's zone, which keeps its instants) does so in <c>OnParametersSet</c>, from what
/// it holds. A component outside the layout (a test's) is given none and keeps zero.
/// </para>
/// <para>
/// Public because the Razor SDK emits every component as <c>public</c> and a public class cannot derive from an internal one; the public
/// API snapshot leaves out every component (D20), and this class has no member but the parameter.
/// </para>
/// </remarks>
public abstract class StudioDisplayComponent : ComponentBase
{
    /// <summary>
    /// How many times the visitor's time zone has moved on this circuit: the layout's, cascaded. A change is a reason to draw again, and
    /// for nothing else.
    /// </summary>
    [CascadingParameter(Name = StudioPreferences.DisplayVersionName)]
    public long DisplayVersion { get; set; }
}
