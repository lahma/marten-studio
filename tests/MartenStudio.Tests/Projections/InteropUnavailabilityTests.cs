using MartenStudio.Services.Live;

using Microsoft.JSInterop;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The one predicate every interop call site filters on (AGENTS.md hard rule 15). Pinned member by
/// member, because a private copy of it once drifted - it named <see cref="ObjectDisposedException" />
/// and <see cref="OperationCanceledException" /> where the shared one named only
/// <see cref="TaskCanceledException" /> - and the drift was only found when the copy was deleted.
/// </summary>
public class InteropUnavailabilityTests
{
    public static TheoryData<Exception> Unavailable => new()
    {
        new JSException("no such function"),
        new JSDisconnectedException("the circuit is gone"),
        new InvalidOperationException("prerendering"),
        new TaskCanceledException(),
        new OperationCanceledException(),
        new ObjectDisposedException("the runtime"),
        new System.Text.Json.JsonException("the browser answered with something that is not JSON"),
    };

    [Theory]
    [MemberData(nameof(Unavailable))]
    public void Every_shape_of_the_browser_not_being_there_is_unavailability(Exception exception) =>
        StudioLiveUpdates.IsInteropUnavailable(exception).Should().BeTrue();

    /// <summary>
    /// The filter still rethrows anything that is a real bug: a null reference in a handler must reach
    /// the circuit's error boundary, not be swallowed as "the browser went away".
    /// </summary>
    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    public void A_real_bug_is_not_unavailability(Type exceptionType) =>
        StudioLiveUpdates.IsInteropUnavailable((Exception)Activator.CreateInstance(exceptionType)!)
            .Should().BeFalse();
}
