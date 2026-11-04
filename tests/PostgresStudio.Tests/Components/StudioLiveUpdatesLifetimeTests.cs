using System.Collections.Concurrent;

using Bunit;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Services.Live;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-LIVE-FIX: a page left while its live loop is still starting. <c>StudioLiveUpdates.StartAsync</c> awaits a JavaScript call
/// (<c>visibility.watch</c>) before it starts the loop, and a page left during that await is disposed under it: the start must
/// find that out, start nothing, and throw nothing - an exception out of <c>OnAfterRenderAsync</c> ends the circuit.
/// </summary>
public class StudioLiveUpdatesLifetimeTests
{
    private const string Watch = "martenStudio.visibility.watch";
    private const string Unwatch = "martenStudio.visibility.unwatch";

    [Fact]
    public async Task A_dispose_during_the_watch_call_starts_no_loop_and_unwatches_once_after_the_watch()
    {
        var js = new PendingWatchRuntime();
        var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        Task start = updates.StartAsync(TimeSpan.FromMilliseconds(20), static _ => Task.CompletedTask, Xunit.TestContext.Current.CancellationToken);

        js.Calls.Should().Equal(Watch);
        start.IsCompleted.Should().BeFalse("the watch call is still in flight");

        await updates.DisposeAsync();

        js.Calls.Should().Equal([Watch, Unwatch], "the unwatch is sent after the watch, so the browser runs them in that order");
        ReferenceIsDisposed(js).Should().BeTrue("the .NET reference the watch was given is released");

        js.WatchResult.SetResult(false);
        await start;

        updates.IsRunning.Should().BeFalse("the page was left: nothing starts");
        updates.TickCount.Should().Be(0);
        js.Calls.Should().Equal([Watch, Unwatch], "exactly one unwatch");

        await updates.DisposeAsync();
        js.Calls.Should().Equal([Watch, Unwatch], "a second dispose does nothing");
    }

    [Fact]
    public async Task A_watch_that_fails_as_interop_unavailable_after_the_dispose_costs_nothing()
    {
        var js = new PendingWatchRuntime();
        var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);

        Task start = updates.StartAsync(TimeSpan.FromMilliseconds(20), static _ => Task.CompletedTask, Xunit.TestContext.Current.CancellationToken);

        await updates.DisposeAsync();

        js.WatchResult.SetException(new JSDisconnectedException("the circuit closed"));
        await start;

        updates.IsRunning.Should().BeFalse();
        updates.TickCount.Should().Be(0);
        js.Calls.Should().Equal([Watch, Unwatch]);
        ReferenceIsDisposed(js).Should().BeTrue();
    }

    [Fact]
    public async Task A_start_that_is_not_disposed_still_runs_its_loop_once_the_watch_answers()
    {
        var js = new PendingWatchRuntime();
        await using var updates = new StudioLiveUpdates(js, NullLogger<StudioLiveUpdates>.Instance);
        var ticked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task start = updates.StartAsync(
            TimeSpan.FromMilliseconds(20),
            _ =>
            {
                ticked.TrySetResult();
                return Task.CompletedTask;
            },
            Xunit.TestContext.Current.CancellationToken);

        js.WatchResult.SetResult(false);
        await start;

        await ticked.Task.WaitAsync(TimeSpan.FromSeconds(30), Xunit.TestContext.Current.CancellationToken);
        updates.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Sessions_left_before_its_watch_call_answers_ends_no_circuit()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var watch = context.JSInterop.Setup<bool>(Watch, static _ => true);

        var page = context.Render<Sessions>();
        page.WaitForAssertion(() => page.FindAll(".ms-sessions-stats").Should().ContainSingle());

        // The first read has finished and the page's OnAfterRenderAsync is waiting for the watch call: leave the page now.
        page.WaitForAssertion(() => context.JSInterop.Invocations[Watch].Should().ContainSingle());
        await context.DisposeComponentsAsync();

        watch.SetResult(false);

        // Whatever the page's start does when the call answers happens on the renderer's dispatcher; an exception out of it is the
        // renderer's unhandled exception.
        // The start's continuation, the page's faulted task and the renderer's handling of it are each a hop on the dispatcher.
        for (int hop = 0; hop < 10; hop++)
        {
            await context.Renderer.Dispatcher.InvokeAsync(static () => Task.CompletedTask);
        }

        Task<Exception> unhandled = context.Renderer.UnhandledException;
        string said = unhandled.IsCompleted ? (await unhandled).ToString() : string.Empty;

        unhandled.IsCompleted.Should().BeFalse(
            "an exception out of the page's OnAfterRenderAsync is the renderer's unhandled exception, which ends a circuit: " + said);
    }

    private static bool ReferenceIsDisposed(PendingWatchRuntime js)
    {
        try
        {
            _ = js.Reference!.Value;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>A JavaScript runtime whose <c>visibility.watch</c> stays pending until the test completes it.</summary>
    private sealed class PendingWatchRuntime : IJSRuntime
    {
        public ConcurrentQueue<string> CallsQueue { get; } = new();

        public IReadOnlyList<string> Calls => [.. CallsQueue];

        public TaskCompletionSource<bool> WatchResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DotNetObjectReference<StudioLiveUpdates>? Reference { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
#pragma warning disable BL0016 // A test fake forwarding to its own Invoke, not a studio call site.
            Invoke<TValue>(identifier, args);
#pragma warning restore BL0016

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
#pragma warning disable BL0016 // A test fake forwarding to its own Invoke, not a studio call site.
            Invoke<TValue>(identifier, args);
#pragma warning restore BL0016

        private ValueTask<TValue> Invoke<TValue>(string identifier, object?[]? args)
        {
            CallsQueue.Enqueue(identifier);

            if (identifier == Watch)
            {
                Reference = args?.Length > 1 ? args[1] as DotNetObjectReference<StudioLiveUpdates> : null;
                return new ValueTask<TValue>(WaitAsync<TValue>());
            }

            return ValueTask.FromResult(default(TValue)!);
        }

        private async Task<TValue> WaitAsync<TValue>() => (TValue) (object) await WatchResult.Task;
    }
}
