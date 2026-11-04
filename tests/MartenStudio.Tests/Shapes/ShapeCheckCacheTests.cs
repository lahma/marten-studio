using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Shapes;
using MartenStudio.Tests.Sql;
using MartenStudio.Tests.Support;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Shapes;

/// <summary>The cache of the shape check (D-SHAPE S9): the key, five minutes, single flight, and "Check again" only after thirty seconds.</summary>
public class ShapeCheckCacheTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly DocumentStore store = DocumentStore.For(options =>
    {
        options.Connection(SqlTestStore.Unreachable);
        options.DatabaseSchemaName = "shape_cache";
    });

    private readonly FakeTimeProvider clock = new(Start);

    public void Dispose()
    {
        store.Dispose();
        GC.SuppressFinalize(this);
    }

    private ResolvedScope Resolved(string? tenant = null) => new(
        new StudioScope("default", string.Empty, tenant),
        new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
        store,
        store.Storage.Database);

    private Func<CancellationToken, Task<ShapeCheckResult>> Read(Counter counter, Task? gate = null) => async _ =>
    {
        Interlocked.Increment(ref counter.Value);

        if (gate is not null)
        {
            await gate;
        }

        return ShapeCheckResult.NotChecked("order", "run " + counter.Value, clock.GetUtcNow());
    };

    private sealed class Counter
    {
        public int Value;
    }

    [Fact]
    public void The_key_is_the_resolved_scope_the_kind_and_the_subject()
    {
        string key = ShapeCheckCache.KeyOf(Resolved("acme"), "documents", "order");

        key.Split('\u001f').Should().Equal("default", store.Storage.Database.Id.Identity, "acme", "documents", "order");
        ShapeCheckCache.KeyOf(Resolved(), "documents", "order").Should().NotBe(key);
        ShapeCheckCache.KeyOf(Resolved("acme"), "events", "order").Should().NotBe(key);
        ShapeCheckCache.KeyOf(Resolved("acme"), "documents", "customer").Should().NotBe(key);
    }

    [Fact]
    public async Task A_result_is_served_for_five_minutes_and_then_read_again()
    {
        ShapeCheckCache cache = new(clock);
        Counter reads = new();

        await cache.GetAsync(Resolved(), "documents", "order", false, Read(reads), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await cache.GetAsync(Resolved(), "documents", "order", false, Read(reads), CancellationToken.None);

        reads.Value.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(2));
        await cache.GetAsync(Resolved(), "documents", "order", false, Read(reads), CancellationToken.None);

        reads.Value.Should().Be(2);
    }

    [Fact]
    public async Task A_second_press_while_one_runs_waits_for_that_run_and_starts_no_other()
    {
        ShapeCheckCache cache = new(clock);
        Counter reads = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<ShapeCheckResult> first = cache.GetAsync(Resolved(), "documents", "order", false, Read(reads, gate.Task), CancellationToken.None);
        Task<ShapeCheckResult> second = cache.GetAsync(Resolved(), "documents", "order", true, Read(reads, gate.Task), CancellationToken.None);

        gate.SetResult();

        (await first).Should().BeSameAs(await second);
        reads.Value.Should().Be(1);
    }

    [Fact]
    public async Task A_caller_who_gives_up_ends_only_its_own_wait()
    {
        ShapeCheckCache cache = new(clock);
        Counter reads = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource giveUp = new();

        Task<ShapeCheckResult> impatient = cache.GetAsync(Resolved(), "documents", "order", false, Read(reads, gate.Task), giveUp.Token);
        Task<ShapeCheckResult> patient = cache.GetAsync(Resolved(), "documents", "order", false, Read(reads, gate.Task), CancellationToken.None);

        await giveUp.CancelAsync();
        await FluentActions.Awaiting(() => impatient).Should().ThrowAsync<OperationCanceledException>();

        gate.SetResult();

        (await patient).CoverageReason.Should().Be("run 1");
        reads.Value.Should().Be(1);
    }

    [Fact]
    public async Task Check_again_is_refused_for_thirty_seconds_and_then_runs_again()
    {
        ShapeCheckCache cache = new(clock);
        Counter reads = new();

        ShapeCheckResult first = await cache.GetAsync(Resolved(), "documents", "order", false, Read(reads), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        ShapeCheckResult tooSoon = await cache.GetAsync(Resolved(), "documents", "order", true, Read(reads), CancellationToken.None);

        tooSoon.Should().BeSameAs(first);
        reads.Value.Should().Be(1);
        first.RecheckAt.Should().Be(Start + TimeSpan.FromSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(2));
        ShapeCheckResult again = await cache.GetAsync(Resolved(), "documents", "order", true, Read(reads), CancellationToken.None);

        again.Should().NotBeSameAs(first);
        reads.Value.Should().Be(2);
    }

    [Fact]
    public async Task Check_again_while_a_forced_run_is_in_flight_starts_no_third()
    {
        ShapeCheckCache cache = new(clock);
        Counter reads = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await cache.GetAsync(Resolved(), "documents", "order", false, Read(reads), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(31));

        Task<ShapeCheckResult> forced = cache.GetAsync(Resolved(), "documents", "order", true, Read(reads, gate.Task), CancellationToken.None);
        Task<ShapeCheckResult> pressedAgain = cache.GetAsync(Resolved(), "documents", "order", true, Read(reads, gate.Task), CancellationToken.None);

        gate.SetResult();
        await Task.WhenAll(forced, pressedAgain);

        reads.Value.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_result_is_handed_to_those_waiting_and_not_kept_so_the_next_press_runs_at_once()
    {
        ShapeCheckCache cache = new(clock);
        int reads = 0;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<CancellationToken, Task<ShapeCheckResult>> failing = async _ =>
        {
            Interlocked.Increment(ref reads);
            await gate.Task;
            return ShapeCheckResult.Failed("order", "busy", clock.GetUtcNow(), TimeSpan.Zero, []);
        };

        Task<ShapeCheckResult> first = cache.GetAsync(Resolved(), "documents", "order", false, failing, CancellationToken.None);
        Task<ShapeCheckResult> waiting = cache.GetAsync(Resolved(), "documents", "order", false, failing, CancellationToken.None);

        gate.SetResult();

        (await first).Error.Should().Be("busy");
        (await waiting).Should().BeSameAs(await first, "everyone waiting on the run is given what it found");
        reads.Should().Be(1);

        // No time has passed: neither the five minutes nor the thirty seconds apply to a failure.
        Counter again = new();
        ShapeCheckResult next = await cache.GetAsync(Resolved(), "documents", "order", true, Read(again), CancellationToken.None);

        again.Value.Should().Be(1);
        next.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_run_that_ends_after_a_new_press_began_leaves_the_new_run_marked_in_flight()
    {
        ShapeCheckCache cache = new(clock);
        Counter reads = new();
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string key = ShapeCheckCache.KeyOf(Resolved(), "documents", "order");
        Task<ShapeCheckResult>? replacement = null;

        // The gap: the failed run has invalidated its key and has not yet ended, and a new press starts the key's next run in it.
        cache.AfterFailureInvalidated = () =>
        {
            cache.AfterFailureInvalidated = null;
            replacement = cache.GetAsync(Resolved(), "documents", "order", false, Read(reads, second.Task), CancellationToken.None);
        };

        ShapeCheckResult failed = await cache.GetAsync(
            Resolved(),
            "documents",
            "order",
            false,
            _ => Task.FromResult(ShapeCheckResult.Failed("order", "busy", clock.GetUtcNow(), TimeSpan.Zero, [])),
            CancellationToken.None);

        failed.Error.Should().Be("busy");
        replacement.Should().NotBeNull();
        reads.Value.Should().Be(1, "the new press began a run of its own");
        cache.IsRunning(key).Should().BeTrue("the old run's end must not erase the new run's marker");

        // So a forced press, thirty seconds later, joins that run and starts no third.
        clock.Advance(TimeSpan.FromSeconds(31));
        Task<ShapeCheckResult> forced = cache.GetAsync(Resolved(), "documents", "order", true, Read(reads, second.Task), CancellationToken.None);

        second.SetResult();
        (await forced).Should().BeSameAs(await replacement!);
        reads.Value.Should().Be(1);
        cache.IsRunning(key).Should().BeFalse();
    }

    [Fact]
    public async Task Two_scopes_do_not_share_a_result()
    {
        ShapeCheckCache cache = new(clock);
        Counter reads = new();

        await cache.GetAsync(Resolved("a"), "documents", "order", false, Read(reads), CancellationToken.None);
        await cache.GetAsync(Resolved("b"), "documents", "order", false, Read(reads), CancellationToken.None);

        reads.Value.Should().Be(2);
    }
}
