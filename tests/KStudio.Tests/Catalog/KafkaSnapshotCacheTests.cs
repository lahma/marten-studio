using KStudio.Core.Catalog;
using KStudio.Tests.Support;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Catalog;

/// <summary>The cache that makes N browser tabs cost one read per interval: a port of <c>StudioSnapshotCacheTests</c> (decision 5.3).</summary>
public class KafkaSnapshotCacheTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Single flight: callers that arrive while the read is still running wait on the same task rather than each starting one.</summary>
    [Fact]
    public async Task Concurrent_callers_for_one_key_share_one_read()
    {
        KafkaSnapshotCache cache = new(new ManualTimeProvider());
        TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<int>[] callers =
        [
            .. Enumerable.Range(0, 8).Select(_ => cache.GetAsync("cluster|metadata", OneSecond, async _ =>
            {
                Interlocked.Increment(ref calls);
                return await gate.Task.Bounded();
            }, Token)),
        ];

        gate.SetResult(42);
        int[] results = await Task.WhenAll(callers);

        results.Should().AllBeEquivalentTo(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_second_call_within_the_time_to_live_is_served_from_the_cache()
    {
        KafkaSnapshotCache cache = new(new ManualTimeProvider());
        int calls = 0;

        await cache.GetAsync("k", OneSecond, _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("k", OneSecond, _ => Task.FromResult(++calls), Token);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Past_the_time_to_live_the_read_runs_again()
    {
        ManualTimeProvider clock = new();
        KafkaSnapshotCache cache = new(clock);
        int calls = 0;

        await cache.GetAsync("k", OneSecond, _ => Task.FromResult(++calls), Token);
        clock.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync("k", OneSecond, _ => Task.FromResult(++calls), Token);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Each_call_brings_its_own_time_to_live_so_a_slow_key_outlives_a_fast_one()
    {
        ManualTimeProvider clock = new();
        KafkaSnapshotCache cache = new(clock);
        int fast = 0;
        int slow = 0;

        await cache.GetAsync("fast", OneSecond, _ => Task.FromResult(++fast), Token);
        await cache.GetAsync("slow", TimeSpan.FromMinutes(1), _ => Task.FromResult(++slow), Token);
        clock.Advance(TimeSpan.FromSeconds(30));
        await cache.GetAsync("fast", OneSecond, _ => Task.FromResult(++fast), Token);
        await cache.GetAsync("slow", TimeSpan.FromMinutes(1), _ => Task.FromResult(++slow), Token);

        (fast, slow).Should().Be((2, 1));
    }

    [Fact]
    public async Task Different_keys_are_different_reads()
    {
        KafkaSnapshotCache cache = new(new ManualTimeProvider());
        int calls = 0;

        await cache.GetAsync("a|metadata", OneSecond, _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("b|metadata", OneSecond, _ => Task.FromResult(++calls), Token);

        calls.Should().Be(2);
    }

    /// <summary>A failure must not be served for the rest of the window: the next poll is how a page recovers from a cluster that came back.</summary>
    [Fact]
    public async Task A_failed_read_is_not_cached()
    {
        KafkaSnapshotCache cache = new(new ManualTimeProvider());
        int calls = 0;

        Func<Task> failing = async () => await cache.GetAsync<int>("k", TimeSpan.FromSeconds(30), _ =>
        {
            calls++;
            return Task.FromException<int>(new InvalidOperationException("the cluster went away"));
        }, Token);

        await failing.Should().ThrowAsync<InvalidOperationException>();

        int value = await cache.GetAsync("k", TimeSpan.FromSeconds(30), _ => Task.FromResult(++calls), Token);

        (calls, value).Should().Be((2, 2));
    }

    /// <summary>A shared task that came back cancelled is as useless as one that faulted.</summary>
    [Fact]
    public async Task A_cancelled_read_is_evicted_like_a_failed_one()
    {
        KafkaSnapshotCache cache = new(new ManualTimeProvider());
        int calls = 0;

        Func<Task> cancelling = async () => await cache.GetAsync<int>("k", TimeSpan.FromSeconds(30), _ =>
        {
            calls++;
            return Task.FromCanceled<int>(new CancellationToken(canceled: true));
        }, Token);

        await cancelling.Should().ThrowAsync<OperationCanceledException>();

        int value = await cache.GetAsync("k", TimeSpan.FromSeconds(30), _ => Task.FromResult(++calls), Token);

        (calls, value).Should().Be((2, 2));
    }

    /// <summary>One caller walking away must not cancel the read every other tab is waiting on.</summary>
    [Fact]
    public async Task One_callers_cancellation_does_not_cancel_the_shared_work()
    {
        KafkaSnapshotCache cache = new(new ManualTimeProvider());
        TaskCompletionSource<int> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sawCancellation = false;

        using CancellationTokenSource leaving = new();

        Task<int> patient = cache.GetAsync("k", TimeSpan.FromSeconds(30), async token =>
        {
            sawCancellation = token.CanBeCanceled;
            return await gate.Task.Bounded();
        }, CancellationToken.None);

        Task<int> impatient = cache.GetAsync("k", TimeSpan.FromSeconds(30), _ => Task.FromResult(-1), leaving.Token);

        await leaving.CancelAsync();

        Func<Task> awaiting = async () => await impatient;
        await awaiting.Should().ThrowAsync<OperationCanceledException>();

        gate.SetResult(7);
        (await patient).Should().Be(7);
        sawCancellation.Should().BeFalse("the shared read runs under CancellationToken.None");
    }

    [Fact]
    public async Task Invalidating_a_prefix_drops_every_key_under_it()
    {
        KafkaSnapshotCache cache = new(new ManualTimeProvider());
        int calls = 0;
        TimeSpan ttl = TimeSpan.FromSeconds(30);

        await cache.GetAsync("main|", ttl, _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("main|metadata", ttl, _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("other|metadata", ttl, _ => Task.FromResult(++calls), Token);

        cache.InvalidatePrefix("main|");

        await cache.GetAsync("main|metadata", ttl, _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("other|metadata", ttl, _ => Task.FromResult(++calls), Token);

        calls.Should().Be(4, "only the two keys under the prefix were dropped");
    }
}
