using Microsoft.JSInterop;

namespace MartenStudio.Components.Pages.Documents;

/// <summary>
/// Whether a failed JavaScript call means "there is no browser to talk to" rather than "the code is
/// wrong".
/// </summary>
/// <remarks>
/// <para>
/// A local copy of the predicate <c>StudioLiveUpdates</c> applies to its own interop, because that one is
/// <c>private</c> on this branch. When it becomes <c>internal</c> this file should go and its call sites
/// should point at the shared one — there is no reason for the studio to hold two opinions about which
/// exceptions mean the circuit has gone.
/// </para>
/// <para>
/// The list is not obvious and getting it wrong is how a page ends up logging a red error every time
/// somebody closes a tab. <c>JSDisconnectedException</c> derives from <see cref="Exception" />, not from
/// <see cref="JSException" />, so catching the latter does not catch the former (the P4 review found
/// exactly that). <see cref="InvalidOperationException" /> is what prerendering throws, before any
/// browser exists at all. <see cref="TaskCanceledException" /> is the interop timeout. And a
/// <c>JsonException</c> is what a loose test double produces when it answers a call with the default for
/// a type the call did not expect.
/// </para>
/// </remarks>
internal static class DocumentsInterop
{
    /// <summary>Whether the exception means the browser is unreachable rather than mistaken.</summary>
    /// <param name="exception">The exception a JavaScript call threw.</param>
    public static bool IsUnavailable(Exception exception) =>
        exception is JSException
            or JSDisconnectedException
            or InvalidOperationException
            or TaskCanceledException
            or System.Text.Json.JsonException;
}
