using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Support;

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

        OperationHandle handle = tracker.Start(
            "Rebuild", "DailySales", "default", "localhost.marten", "admin", StudioCapability.RebuildProjections,
            async _ =>
            {
                started.TrySetResult();
                await gate.Task;
            });

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

        OperationHandle handle = tracker.Start(
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

        OperationHandle handle = tracker.Start(
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

        OperationHandle handle = tracker.Start(
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
}
