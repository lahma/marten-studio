using System.Collections.Concurrent;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

using PostgresStudio.Services.Live;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// D-M5 decision 3.6 (F6): <see cref="StudioLiveUpdates.VisibilityChanged" />, the event a page that must stop reading while its tab is hidden acts on.
/// Raised once per change after <see cref="StudioLiveUpdates.IsHidden" /> changed, never for the state the watch started in, in the order the browser sent
/// the changes, never into a circuit: a handler that throws is handed to <see cref="StudioLiveUpdates.Failed" />.
/// </summary>
public class StudioLiveUpdatesVisibilityTests
{
    [Fact]
    public async Task A_change_raises_the_event_once_after_the_flag_changed_and_the_state_the_watch_started_in_raises_nothing()
    {
        await using StudioLiveUpdates updates = await StartedAsync(startsHidden: true);
        ConcurrentQueue<(bool Hidden, bool Flag)> heard = new();
        TaskCompletionSource<bool> second = new(TaskCreationOptions.RunContinuationsAsynchronously);

        updates.IsHidden.Should().BeTrue("the watch's answer is the starting state");
        updates.VisibilityChanged += hidden =>
        {
            heard.Enqueue((hidden, updates.IsHidden));
            second.TrySetResult(hidden);
            return Task.CompletedTask;
        };

        // Hidden again is not a change: the watch already said so.
        updates.OnVisibilityChanged(true);
        updates.OnVisibilityChanged(false);

        (await second.Task.Bounded()).Should().BeFalse();
        heard.Should().Equal([(false, false)], "the flag had changed by the time the handler ran");
    }

    [Fact]
    public async Task The_same_value_twice_is_one_change_and_changes_arrive_in_the_order_they_happened()
    {
        await using StudioLiveUpdates updates = await StartedAsync(startsHidden: false);
        ConcurrentQueue<bool> heard = new();
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        updates.VisibilityChanged += async hidden =>
        {
            // The first handler is held: the later changes must wait for it, or they would overtake it.
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task.Bounded();
            }

            heard.Enqueue(hidden);

            if (heard.Count == 3)
            {
                done.TrySetResult();
            }
        };

        updates.OnVisibilityChanged(true);
        updates.OnVisibilityChanged(true);
        updates.OnVisibilityChanged(false);
        updates.OnVisibilityChanged(true);
        release.SetResult();

        await done.Task.Bounded();
        heard.Should().Equal(true, false, true);
    }

    [Fact]
    public async Task A_handler_that_throws_is_handed_to_Failed_and_the_other_handlers_still_hear_the_change()
    {
        await using StudioLiveUpdates updates = await StartedAsync(startsHidden: false);
        TaskCompletionSource<Exception> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> other = new(TaskCreationOptions.RunContinuationsAsynchronously);

        updates.Failed += exception =>
        {
            failed.TrySetResult(exception);
            return Task.CompletedTask;
        };
        updates.VisibilityChanged += static _ => throw new InvalidOperationException("a page bug");
        updates.VisibilityChanged += hidden =>
        {
            other.TrySetResult(hidden);
            return Task.CompletedTask;
        };

        updates.OnVisibilityChanged(true);

        (await failed.Task.Bounded()).Message.Should().Be("a page bug");
        (await other.Task.Bounded()).Should().BeTrue();
        updates.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task A_Failed_handler_that_throws_too_ends_nothing()
    {
        await using StudioLiveUpdates updates = await StartedAsync(startsHidden: false);
        TaskCompletionSource<bool> later = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        updates.Failed += static _ => throw new InvalidOperationException("the page's own failure handler is broken");
        updates.VisibilityChanged += hidden =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("first");
            }

            later.TrySetResult(hidden);
            return Task.CompletedTask;
        };

        updates.OnVisibilityChanged(true);
        updates.OnVisibilityChanged(false);

        (await later.Task.Bounded()).Should().BeFalse("the raise after the failing one still happens");
    }

    [Fact]
    public async Task A_change_after_the_dispose_raises_nothing()
    {
        StudioLiveUpdates updates = await StartedAsync(startsHidden: false);
        bool raised = false;
        updates.VisibilityChanged += _ =>
        {
            raised = true;
            return Task.CompletedTask;
        };

        await updates.DisposeAsync();
        updates.OnVisibilityChanged(true);

        // The raise of a change is a task on the pool; this is the one a change would have queued, and when it has finished every handler it would have run has run.
        await updates.VisibilityChangesRaised.Bounded();
        updates.VisibilityChangesRaised.IsCompleted.Should().BeTrue();
        raised.Should().BeFalse();
    }

    [Fact]
    public async Task A_handler_that_throws_is_logged_at_Debug_as_well_as_handed_to_Failed()
    {
        ListLogger<StudioLiveUpdates> log = new();
        await using StudioLiveUpdates updates = new(new WatchAnswers(false), log);
        await updates.StartAsync(TimeSpan.FromHours(1), static _ => Task.CompletedTask, Xunit.TestContext.Current.CancellationToken);
        TaskCompletionSource failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.Failed += _ =>
        {
            failed.TrySetResult();
            return Task.CompletedTask;
        };
        updates.VisibilityChanged += static _ => throw new InvalidOperationException("a page bug");

        updates.OnVisibilityChanged(true);
        await failed.Task.Bounded();

        log.Entries.Should().Contain(static x => x.Level == Microsoft.Extensions.Logging.LogLevel.Debug && x.Message.Contains("visibility handler failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Asking_the_browser_takes_its_answer_as_the_state_and_raises_a_change_only_if_it_differs()
    {
        AskedAnswers js = new(startsHidden: false, asked: true);
        await using StudioLiveUpdates updates = new(js, NullLogger<StudioLiveUpdates>.Instance);
        await updates.StartAsync(TimeSpan.FromHours(1), static _ => Task.CompletedTask, Xunit.TestContext.Current.CancellationToken);
        ConcurrentQueue<bool> heard = new();
        updates.VisibilityChanged += hidden =>
        {
            heard.Enqueue(hidden);
            return Task.CompletedTask;
        };

        (await updates.ReadIsHiddenAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeTrue();
        updates.IsHidden.Should().BeTrue("the page missed the change and the browser's answer corrects it");
        (await updates.ReadIsHiddenAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeTrue();
        await updates.VisibilityChangesRaised.Bounded();

        heard.Should().Equal([true], "the second answer was no change");
        js.Asked.Should().Be(2);
    }

    [Fact]
    public async Task A_browser_that_cannot_be_asked_counts_as_hidden()
    {
        AskedAnswers js = new(startsHidden: false, asked: false) { Fails = true };
        await using StudioLiveUpdates updates = new(js, NullLogger<StudioLiveUpdates>.Instance);
        await updates.StartAsync(TimeSpan.FromHours(1), static _ => Task.CompletedTask, Xunit.TestContext.Current.CancellationToken);

        (await updates.ReadIsHiddenAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeTrue();
        updates.IsHidden.Should().BeTrue();
    }

    private sealed class AskedAnswers(bool startsHidden, bool asked) : IJSRuntime
    {
        public bool Fails { get; init; }

        public int Asked { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
#pragma warning disable BL0016 // A test fake forwarding to its own Invoke, not a studio call site.
            Invoke<TValue>(identifier);
#pragma warning restore BL0016

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
#pragma warning disable BL0016 // A test fake forwarding to its own Invoke, not a studio call site.
            Invoke<TValue>(identifier);
#pragma warning restore BL0016

        private ValueTask<TValue> Invoke<TValue>(string identifier)
        {
            if (identifier == "martenStudio.visibility.isHidden")
            {
                Asked++;
                return Fails ? throw new JSException("gone") : ValueTask.FromResult((TValue) (object) asked);
            }

            return identifier == "martenStudio.visibility.watch" ? ValueTask.FromResult((TValue) (object) startsHidden) : ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> queue = new();

        public IReadOnlyCollection<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries => queue;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            queue.Enqueue((logLevel, formatter(state, exception)));
    }

    private static async Task<StudioLiveUpdates> StartedAsync(bool startsHidden)
    {
        StudioLiveUpdates updates = new(new WatchAnswers(startsHidden), NullLogger<StudioLiveUpdates>.Instance);
        await updates.StartAsync(TimeSpan.FromHours(1), static _ => Task.CompletedTask, Xunit.TestContext.Current.CancellationToken);
        return updates;
    }

    /// <summary>A JavaScript runtime whose <c>visibility.watch</c> answers at once with the state the tab is in.</summary>
    private sealed class WatchAnswers(bool hidden) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
#pragma warning disable BL0016 // A test fake forwarding to its own Invoke, not a studio call site.
            Invoke<TValue>(identifier);
#pragma warning restore BL0016

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
#pragma warning disable BL0016 // A test fake forwarding to its own Invoke, not a studio call site.
            Invoke<TValue>(identifier);
#pragma warning restore BL0016

        private ValueTask<TValue> Invoke<TValue>(string identifier) =>
            identifier == "martenStudio.visibility.watch" ? ValueTask.FromResult((TValue) (object) hidden) : ValueTask.FromResult(default(TValue)!);
    }
}
