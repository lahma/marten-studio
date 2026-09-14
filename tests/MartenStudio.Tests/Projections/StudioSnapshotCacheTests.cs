using MartenStudio.Services.Live;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The cache that makes N browser tabs cost one query per interval instead of N.
/// </summary>
public class StudioSnapshotCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Single flight: callers that arrive while the query is still running wait on the same task rather
    /// than each starting one. This is the half a time-to-live alone does not give you - without it, N
    /// circuits stampede the moment an entry expires.
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_for_one_key_share_one_query()
    {
        var clock = new FakeTimeProvider(Now);
        var cache = new StudioSnapshotCache(clock, TimeSpan.FromSeconds(1));

        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<int>[] callers =
        [
            .. Enumerable.Range(0, 8).Select(_ => cache.GetAsync("scope|projections", async _ =>
            {
                Interlocked.Increment(ref calls);
                return await gate.Task;
            }, Token))
        ];

        gate.SetResult(42);
        int[] results = await Task.WhenAll(callers);

        results.Should().AllBeEquivalentTo(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_second_call_within_the_time_to_live_is_served_from_the_cache()
    {
        var clock = new FakeTimeProvider(Now);
        var cache = new StudioSnapshotCache(clock, TimeSpan.FromSeconds(1));
        int calls = 0;

        await cache.GetAsync("k", _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("k", _ => Task.FromResult(++calls), Token);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Past_the_time_to_live_the_query_runs_again()
    {
        var clock = new FakeTimeProvider(Now);
        var cache = new StudioSnapshotCache(clock, TimeSpan.FromSeconds(1));
        int calls = 0;

        await cache.GetAsync("k", _ => Task.FromResult(++calls), Token);
        clock.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync("k", _ => Task.FromResult(++calls), Token);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Different_keys_are_different_queries()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(1));
        int calls = 0;

        await cache.GetAsync("a|projections", _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("b|projections", _ => Task.FromResult(++calls), Token);

        calls.Should().Be(2);
    }

    /// <summary>
    /// A failure must not be served for the rest of the window: the next poll is how a page recovers from
    /// a database that came back.
    /// </summary>
    [Fact]
    public async Task A_failed_query_is_not_cached()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(30));
        int calls = 0;

        Func<Task> failing = async () => await cache.GetAsync<int>("k", _ =>
        {
            calls++;
            return Task.FromException<int>(new InvalidOperationException("the database went away"));
        }, Token);

        await failing.Should().ThrowAsync<InvalidOperationException>();

        int value = await cache.GetAsync("k", _ => Task.FromResult(++calls), Token);

        calls.Should().Be(2);
        value.Should().Be(2);
    }

    /// <summary>
    /// One caller walking away must not cancel the query every other tab is waiting on, so the shared
    /// work runs under a token of its own.
    /// </summary>
    [Fact]
    public async Task One_callers_cancellation_does_not_cancel_the_shared_work()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool sawCancellation = false;

        using var leaving = new CancellationTokenSource();

        Task<int> patient = cache.GetAsync("k", async token =>
        {
            sawCancellation = token.CanBeCanceled;
            return await gate.Task;
        }, CancellationToken.None);

        Task<int> impatient = cache.GetAsync("k", _ => Task.FromResult(-1), leaving.Token);

        await leaving.CancelAsync();

        Func<Task> awaiting = async () => await impatient;
        await awaiting.Should().ThrowAsync<OperationCanceledException>();

        gate.SetResult(7);
        (await patient).Should().Be(7);
        sawCancellation.Should().BeFalse("the shared query runs under CancellationToken.None");
    }

    [Fact]
    public async Task Invalidating_a_prefix_drops_every_key_under_it()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(30));
        int calls = 0;

        await cache.GetAsync("default|db|", _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("default|db|projections", _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("other|db|projections", _ => Task.FromResult(++calls), Token);

        cache.InvalidatePrefix("default|db|");

        await cache.GetAsync("default|db|projections", _ => Task.FromResult(++calls), Token);
        await cache.GetAsync("other|db|projections", _ => Task.FromResult(++calls), Token);

        calls.Should().Be(4, "only the two keys under the prefix were dropped");
    }
}
