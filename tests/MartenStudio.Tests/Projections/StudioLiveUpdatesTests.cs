using MartenStudio.Services.Live;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The per-page polling loop: it pauses while nobody is looking, it never lets a tick escape as an
/// unobserved exception, and it gives the browser's .NET reference back.
/// </summary>
public class StudioLiveUpdatesTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(25);

    /// <summary>How long a test waits for something that should happen.</summary>
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>How long a test watches for something that should <em>not</em> happen.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(400);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Starting_watches_the_tab_and_ticks_on_the_interval()
    {
        var js = new FakeJsRuntime();
        await using var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        var ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await updates.StartAsync(Interval, _ =>
        {
            ticked.TrySetResult();
            return Task.CompletedTask;
        }, Token);

        await ticked.Task.WaitAsync(Generous, Token);

        js.Calls.Should().Contain("martenStudio.visibility.watch");
        js.DotNetReference.Should().BeOfType<DotNetObjectReference<StudioLiveUpdates>>();
    }

    /// <summary>
    /// A studio left open in a background tab stops querying entirely: that is the whole point of the
    /// visibility watch (plan D10).
    /// </summary>
    [Fact]
    public async Task A_hidden_tab_pauses_the_polling_and_showing_it_again_resumes_it()
    {
        var js = new FakeJsRuntime();
        await using var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        TaskCompletionSource ticked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await updates.StartAsync(Interval, _ =>
        {
            ticked.TrySetResult();
            return Task.CompletedTask;
        }, Token);

        await ticked.Task.WaitAsync(Generous, Token);

        // Hidden: nothing more happens, however many intervals go by.
        updates.OnVisibilityChanged(true);
        updates.IsHidden.Should().BeTrue();

        ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task finished = await Task.WhenAny(ticked.Task, Task.Delay(Window, Token));
        finished.Should().NotBeSameAs(ticked.Task, "the loop must not tick while the tab is hidden");

        // Shown again: the next tick happens.
        updates.OnVisibilityChanged(false);
        await ticked.Task.WaitAsync(Generous, Token);
    }

    /// <summary>
    /// A tick that throws is a value, not an unhandled exception: it is kept, it is reported, and the
    /// loop carries on so that a database which comes back is rendered by the next tick.
    /// </summary>
    [Fact]
    public async Task A_failing_tick_is_reported_as_a_value_and_does_not_stop_the_loop()
    {
        var js = new FakeJsRuntime();
        await using var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        List<string> reported = [];
        updates.Failed += exception =>
        {
            reported.Add(exception.Message);
            return Task.CompletedTask;
        };

        bool throwNext = true;
        await updates.StartAsync(Interval, _ => throwNext
            ? Task.FromException(new InvalidOperationException("the database went away"))
            : Task.CompletedTask, Token);

        await updates.TickNowAsync(Token);

        reported.Should().ContainSingle().Which.Should().Be("the database went away");
        updates.LastError.Should().BeOfType<InvalidOperationException>();

        throwNext = false;
        await updates.TickNowAsync(Token);

        updates.LastError.Should().BeNull("a tick that worked clears the last failure");
    }

    /// <summary>
    /// The browser holds a reference to this object; not giving it back pins the whole component graph
    /// behind it for the life of the process.
    /// </summary>
    [Fact]
    public async Task Disposing_unwatches_the_tab_and_stops_the_loop()
    {
        var js = new FakeJsRuntime();
        var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        var ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await updates.StartAsync(Interval, _ =>
        {
            ticked.TrySetResult();
            return Task.CompletedTask;
        }, Token);

        await ticked.Task.WaitAsync(Generous, Token);

        await updates.DisposeAsync();

        js.Unwatched.Should().BeTrue();
        updates.IsRunning.Should().BeFalse();

        int after = updates.TickCount;
        await Task.Delay(Window, Token);
        updates.TickCount.Should().Be(after, "the loop is cancelled on dispose");
    }

    /// <summary>
    /// Prerendering has no JavaScript at all. Polling then simply never pauses, which is the safe way
    /// round: a page that shows stale numbers is worse than one that queries too often.
    /// </summary>
    [Fact]
    public async Task JavaScript_being_unavailable_does_not_stop_the_page_from_polling()
    {
        var js = new FakeJsRuntime
        {
            Failure = new InvalidOperationException(
                "JavaScript interop calls cannot be issued at this time. This is because the component is being statically rendered.")
        };

        await using var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        var ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await updates.StartAsync(Interval, _ =>
        {
            ticked.TrySetResult();
            return Task.CompletedTask;
        }, Token);

        await ticked.Task.WaitAsync(Generous, Token);
        updates.IsHidden.Should().BeFalse();
    }

    [Fact]
    public async Task A_tab_that_starts_hidden_is_known_to_be_hidden()
    {
        var js = new FakeJsRuntime { StartsHidden = true };
        await using var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        await updates.StartAsync(Interval, static _ => Task.CompletedTask, Token);

        updates.IsHidden.Should().BeTrue();
    }
}
