using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The tracker that runs a rebuild detached, so that closing the tab does not leave a projection's tables
/// half rewritten.
/// </summary>
public class StudioOperationTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_operation_is_addressed_by_id_and_reports_when_it_finishes()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        OperationStart handle = tracker.Start(
            "Rebuild", "DailySales", "default", "localhost.marten", "admin", StudioCapability.RebuildProjections,
            async _ =>
            {
                started.TrySetResult();
                await gate.Task;
            });

        handle.Started.Should().BeTrue();

        await started.Task.WaitAsync(Generous, Token);

        tracker.Find(handle.Id)!.IsRunning.Should().BeTrue();
        tracker.RunningFor("default", "localhost.marten").Should().ContainSingle();

        gate.SetResult();

        await WaitForAsync(() => tracker.Find(handle.Id)?.IsRunning == false);

        tracker.Find(handle.Id)!.State.Should().Be(StudioOperationState.Succeeded);
    }

    /// <summary>
    /// The circuit that started it may be long gone by the time it ends, so both ends are audited by the
    /// tracker itself - 9207 on the way in and 9208 on the way out.
    /// </summary>
    [Fact]
    public async Task Both_ends_are_audited_even_though_the_circuit_may_be_gone()
    {
        var audit = new StudioActionLogService();
        using var provider = new CapturingLoggerProvider();
        using ILoggerFactory factory = provider.CreateFactory();

        using var tracker = new StudioOperationTracker(
            factory.CreateLogger<StudioOperationTracker>(), audit, new FakeTimeProvider(Now));

        OperationStart handle = tracker.Start(
            "Rebuild", "DailySales", "default", "localhost.marten", "admin", StudioCapability.RebuildProjections,
            static _ => Task.CompletedTask);

        await WaitForAsync(() => tracker.Find(handle.Id)?.IsRunning == false);

        provider.Entries.Select(static x => x.EventId.Id).Should().Contain([9207, 9208]);

        audit.GetLatest().Select(static x => x.Action)
            .Should().Contain(["RebuildStarted", "RebuildFinished"]);

        audit.GetLatest().Should().AllSatisfy(static entry =>
            entry.Capability.Should().Be(nameof(StudioCapability.RebuildProjections)));
    }

    [Fact]
    public async Task A_failing_operation_keeps_its_failure_rather_than_throwing_into_nothing()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        OperationStart handle = tracker.Start(
            "Rebuild", "DailySales", "default", "localhost.marten", "admin", StudioCapability.RebuildProjections,
            static _ => Task.FromException(new InvalidOperationException("the shard would not stop")));

        await WaitForAsync(() => tracker.Find(handle.Id)?.IsRunning == false);

        StudioOperation operation = tracker.Find(handle.Id)!;
        operation.State.Should().Be(StudioOperationState.Failed);
        operation.Message.Should().Be("the shard would not stop");

        audit.GetLatest().Should().Contain(static x => x.Action == "RebuildFinished" && !x.Succeeded);
    }

    /// <summary>
    /// The cancellation token belongs to the tracker, not to any circuit: a page that goes away must not
    /// cancel the rebuild it started.
    /// </summary>
    [Fact]
    public async Task An_operation_can_be_cancelled_by_id_and_says_so()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        OperationStart handle = tracker.Start(
            "Rebuild", "DailySales", "default", "localhost.marten", "admin", StudioCapability.RebuildProjections,
            async token =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });

        await started.Task.WaitAsync(Generous, Token);

        tracker.Cancel(handle.Id).Should().BeTrue();

        await WaitForAsync(() => tracker.Find(handle.Id)?.IsRunning == false);

        tracker.Find(handle.Id)!.State.Should().Be(StudioOperationState.Cancelled);
        tracker.Cancel(handle.Id).Should().BeFalse("it is no longer running");
    }

    [Fact]
    public void An_unknown_handle_is_answered_with_nothing_rather_than_an_exception()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        tracker.Find("nope").Should().BeNull();
        tracker.Find(null).Should().BeNull();
        tracker.Cancel("nope").Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------------
    // One at a time
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// B3: two rebuilds of one projection would replay the same events into the same tables at once. The
    /// second request is answered with the first rather than started, so two circuits clicking at the
    /// same moment produce one operation.
    /// </summary>
    [Fact]
    public async Task A_second_identical_operation_is_answered_with_the_one_already_running()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;

        OperationStart first = Start(tracker, "DailySales", async _ =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
        });

        OperationStart second = Start(tracker, "DailySales", async _ =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
        });

        first.Started.Should().BeTrue();
        second.Started.Should().BeFalse();
        second.Id.Should().Be(first.Id);

        tracker.RunningFor("default", "localhost.marten").Should().ContainSingle();

        gate.SetResult();
        await WaitForAsync(() => tracker.Find(first.Id)?.IsRunning == false);

        runs.Should().Be(1, "the second request started nothing");
    }

    [Fact]
    public async Task A_different_projection_is_not_held_back_by_the_one_that_is_running()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        OperationStart daily = Start(tracker, "DailySales", _ => gate.Task);
        OperationStart shipments = Start(tracker, "ShipmentTracker", _ => gate.Task);

        daily.Started.Should().BeTrue();
        shipments.Started.Should().BeTrue();
        shipments.Id.Should().NotBe(daily.Id);

        gate.SetResult();
        await WaitForAsync(() => tracker.Find(shipments.Id)?.IsRunning == false);
    }

    /// <summary>
    /// Once an identical operation has finished, the next request starts a new one: the refusal is about
    /// what is running, not about what has ever run.
    /// </summary>
    [Fact]
    public async Task A_finished_operation_does_not_hold_the_next_one_back()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        OperationStart first = Start(tracker, "DailySales", static _ => Task.CompletedTask);
        await WaitForAsync(() => tracker.Find(first.Id)?.IsRunning == false);

        OperationStart second = Start(tracker, "DailySales", static _ => Task.CompletedTask);

        second.Started.Should().BeTrue();
        second.Id.Should().NotBe(first.Id);

        await WaitForAsync(() => tracker.Find(second.Id)?.IsRunning == false);
    }

    /// <summary>
    /// F10: running operations are bounded too. A rebuild is a full replay; several at once are several
    /// concurrent scans of the same event table, and an admin screen must not be able to become the load
    /// that takes the database down.
    /// </summary>
    [Fact]
    public async Task Past_the_ceiling_a_new_operation_is_refused_with_a_message()
    {
        var audit = new StudioActionLogService();
        using var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<OperationStart> started = [];

        for (int index = 0; index < StudioOperationTracker.MaxRunning; index++)
        {
            started.Add(Start(tracker, "Projection" + index, _ => gate.Task));
        }

        Action oneTooMany = () => Start(tracker, "OneMore", _ => gate.Task);

        oneTooMany.Should().Throw<StudioOperationRefusedException>()
            .WithMessage("*background operations*");

        gate.SetResult();

        foreach (OperationStart operation in started)
        {
            await WaitForAsync(() => tracker.Find(operation.Id)?.IsRunning == false);
        }

        // And once they are done, the ceiling is no longer in the way.
        Start(tracker, "OneMore", static _ => Task.CompletedTask).Started.Should().BeTrue();
    }

    // ------------------------------------------------------------------------------------------------
    // Shutdown
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// F10: a host that is stopping cancels the replays rather than being held open by them, and what is
    /// recorded is a cancellation - which is what happened - rather than a failure.
    /// </summary>
    [Fact]
    public async Task A_host_that_is_stopping_cancels_the_operations_and_records_them_as_cancelled()
    {
        var audit = new StudioActionLogService();
        using var lifetime = new FakeApplicationLifetime();
        using var tracker = new StudioOperationTracker(
            NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now), lifetime);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        OperationStart handle = tracker.Start(
            "Rebuild", "DailySales", "default", "localhost.marten", "admin", StudioCapability.RebuildProjections,
            async token =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });

        await started.Task.WaitAsync(Generous, Token);

        lifetime.StopApplication();

        await WaitForAsync(() => tracker.Find(handle.Id)?.IsRunning == false);

        tracker.Find(handle.Id)!.State.Should().Be(StudioOperationState.Cancelled);
        audit.GetLatest().Should().Contain(static x => x.Action == "RebuildFinished" && !x.Succeeded);
    }

    /// <summary>
    /// Disposing the tracker asks and does not wait, and the operation's own cancellation source outlives
    /// the tracker long enough for the work to observe it: disposing it from <c>Dispose</c> turned a
    /// cancellation into an <c>ObjectDisposedException</c> recorded as a failure.
    /// </summary>
    [Fact]
    public async Task Disposing_the_tracker_cancels_running_work_without_faulting_it()
    {
        var audit = new StudioActionLogService();
        var tracker = new StudioOperationTracker(NullLogger<StudioOperationTracker>.Instance, audit, new FakeTimeProvider(Now));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        OperationStart handle = tracker.Start(
            "Rebuild", "DailySales", "default", "localhost.marten", "admin", StudioCapability.RebuildProjections,
            async token =>
            {
                started.TrySetResult();

                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    // Reading the token after cancellation is exactly what a real rebuild's cleanup does.
                    observed.TrySetResult(token.IsCancellationRequested);
                    throw;
                }
            });

        await started.Task.WaitAsync(Generous, Token);

        tracker.Dispose();

        (await observed.Task.WaitAsync(Generous, Token)).Should().BeTrue();

        await WaitForAsync(() => tracker.Find(handle.Id)?.IsRunning == false);

        tracker.Find(handle.Id)!.State.Should().Be(StudioOperationState.Cancelled);
    }

    private static OperationStart Start(StudioOperationTracker tracker, string target, Func<CancellationToken, Task> work) =>
        tracker.Start(
            "Rebuild", target, "default", "localhost.marten", "admin", StudioCapability.RebuildProjections, work);

    /// <summary>Wait on the condition, never on a fixed delay.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Generous);

        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    /// <summary>
    /// The half of <see cref="IHostApplicationLifetime" /> the tracker uses: a token that is cancelled
    /// when the host starts stopping.
    /// </summary>
    private sealed class FakeApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication() => stopping.Cancel();

        public void Dispose()
        {
            stopping.Dispose();
            stopped.Dispose();
        }
    }
}
