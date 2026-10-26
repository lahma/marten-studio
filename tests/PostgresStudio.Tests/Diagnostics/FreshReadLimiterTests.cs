using PostgresStudio.Services.Diagnostics;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The diagnostics API's limit on fresh reads (API design §3.4): ten concurrent calls on cache-missing databases run at most two
/// reads at a time, and a read the caches hold waits for nothing.
/// </summary>
public class FreshReadLimiterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ten_concurrent_misses_run_two_at_a_time()
    {
        using FreshReadLimiter limiter = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;

        Task<int>[] calls =
        [
            .. Enumerable.Range(0, 10).Select(i => limiter.RunAsync(cached: false, async _ =>
            {
                Interlocked.Increment(ref started);
                await release.Task;
                return i;
            }, Token)),
        ];

        // Two started, and they stay two however long they take.
        await WaitForAsync(() => Volatile.Read(ref started) == 2);
        await Task.Yield();
        Volatile.Read(ref started).Should().Be(2, "the third waits for a permit");

        release.SetResult();
        (await Task.WhenAll(calls)).Should().BeEquivalentTo(Enumerable.Range(0, 10));
        limiter.MostRunning.Should().Be(2);
    }

    [Fact]
    public async Task A_read_the_caches_hold_takes_no_permit_and_waits_behind_nothing()
    {
        using FreshReadLimiter limiter = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = limiter.RunAsync(cached: false, async _ => await release.Task.ContinueWith(static _ => 0, TaskScheduler.Default), Token);
        Task second = limiter.RunAsync(cached: false, async _ => await release.Task.ContinueWith(static _ => 0, TaskScheduler.Default), Token);

        (await limiter.RunAsync(cached: true, static _ => Task.FromResult(42), Token).WaitAsync(TimeSpan.FromSeconds(10), Token))
            .Should().Be(42, "a cache hit is answered while both permits are held");

        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task A_caller_that_gives_up_waiting_releases_nothing_it_did_not_hold()
    {
        using FreshReadLimiter limiter = new(concurrency: 1);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder = limiter.RunAsync(cached: false, async _ => await release.Task.ContinueWith(static _ => 0, TaskScheduler.Default), Token);

        using CancellationTokenSource impatient = new();
        Task waiting = limiter.RunAsync(cached: false, static _ => Task.FromResult(1), impatient.Token);
        await impatient.CancelAsync();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();

        release.SetResult();
        await holder;
        (await limiter.RunAsync(cached: false, static _ => Task.FromResult(2), Token)).Should().Be(2, "the one permit came back once");
    }

    [Fact]
    public void The_host_limit_is_two() => new FreshReadLimiter().Concurrency.Should().Be(2);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            await Task.Yield();
            limit.Token.ThrowIfCancellationRequested();
        }
    }
}
