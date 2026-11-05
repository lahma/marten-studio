using MartenStudio.Tests.Support;
using PostgresStudio.Services.Live;

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
                return await gate.Task.Bounded();
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
    /// A shared task that came back cancelled is as useless as one that faulted: it will never produce a
    /// value, and serving it for the rest of the window would hand every later caller a cancellation that
    /// has already happened.
    /// </summary>
    [Fact]
    public async Task A_cancelled_query_is_evicted_like_a_failed_one()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(30));
        int calls = 0;

        Func<Task> cancelling = async () => await cache.GetAsync<int>("k", _ =>
        {
            calls++;
            return Task.FromCanceled<int>(new CancellationToken(canceled: true));
        }, Token);

        await cancelling.Should().ThrowAsync<OperationCanceledException>();

        // The caller's own token was never cancelled, so this is the shared work having been cancelled -
        // and the entry must be gone rather than replayed to the next poll.
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
            return await gate.Task.Bounded();
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

    // 1.2-UX-FIX-FU3 (F4): the peek inserts nothing and replaces nothing.

    [Fact]
    public async Task A_peek_at_a_key_that_is_not_held_is_null_and_starts_and_inserts_nothing()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(30));

        (await cache.PeekAsync<string>("k", Token)).Should().BeNull();

        cache.Count.Should().Be(0, "no placeholder was inserted for a real read to adopt");
    }

    [Fact]
    public async Task A_peek_at_a_value_in_hand_returns_it_and_one_in_flight_is_awaited()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        Task<string> real = cache.GetAsync("k", async _ =>
        {
            Interlocked.Increment(ref calls);
            return await gate.Task.Bounded();
        }, Token);

        Task<string?> peek = cache.PeekAsync<string>("k", Token);
        peek.IsCompleted.Should().BeFalse("it waits on the read that is under way");

        gate.SetResult("counted");

        (await peek).Should().Be("counted");
        (await real).Should().Be("counted");
        (await cache.PeekAsync<string>("k", Token)).Should().Be("counted");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_peek_at_a_stale_entry_is_null_and_leaves_it_alone_so_the_next_real_read_runs_its_own_work()
    {
        var clock = new FakeTimeProvider(Now);
        var cache = new StudioSnapshotCache(clock, TimeSpan.FromSeconds(30));
        int calls = 0;

        await cache.GetAsync("k", _ => Task.FromResult("old" + ++calls), Token);
        clock.Advance(TimeSpan.FromSeconds(31));

        (await cache.PeekAsync<string>("k", Token)).Should().BeNull();
        cache.Count.Should().Be(1, "the stale entry is neither replaced nor added to");

        (await cache.GetAsync("k", _ => Task.FromResult("new" + ++calls), Token)).Should().Be("new2", "a real read runs its own factory: nothing of the peek's was adopted");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task A_peek_at_failed_work_is_null()
    {
        var cache = new StudioSnapshotCache(new FakeTimeProvider(Now), TimeSpan.FromSeconds(30));

        Func<Task> failing = async () => await cache.GetAsync<string>("k", _ => throw new InvalidOperationException("no"), Token);
        await failing.Should().ThrowAsync<InvalidOperationException>();

        (await cache.PeekAsync<string>("k", Token)).Should().BeNull();
    }
}
