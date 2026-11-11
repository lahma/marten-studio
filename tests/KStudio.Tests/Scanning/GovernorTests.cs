using KStudio.Core.Clusters;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The byte rate (AGENTS.md D41, decision 9.3): the token bucket's arithmetic is exact on a manual clock, two scans share one bucket, a
/// scan in debt does not call <c>Consume</c>, and the owner's rolling hour and the process and owner slots refuse rather than queue.
/// </summary>
public class GovernorTests
{
    private const long MiB = 1024 * 1024;

    private static KafkaEngineSnapshot Settings(Action<KafkaScanLimits>? configure = null)
    {
        KafkaEngineOptions options = new();
        configure?.Invoke(options.Limits);
        return KafkaEngineSnapshot.Take(options);
    }

    [Fact]
    public void The_bucket_starts_full_with_one_second_of_rate_and_owes_exactly_what_it_overdrew()
    {
        ManualTimeProvider clock = new();
        KafkaByteRateGovernor governor = new(Settings(static l => l.BytesPerSecond = MiB), clock);

        governor.BytesPerSecond.Should().Be(MiB);
        governor.DebtWait().Should().Be(TimeSpan.Zero);

        governor.Take(MiB);
        governor.DebtWait().Should().Be(TimeSpan.Zero, "exactly one second of rate was spent, and nothing is owed");

        governor.Take(MiB / 2);
        governor.DebtWait().Should().Be(TimeSpan.FromSeconds(0.5), "half a second of rate is owed");

        governor.Take(MiB / 2);
        governor.DebtWait().Should().Be(TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(1));
        governor.DebtWait().Should().Be(TimeSpan.Zero, "the clock paid the debt");
    }

    [Fact]
    public void The_bucket_never_holds_more_than_one_second_of_rate_however_long_it_was_idle()
    {
        ManualTimeProvider clock = new();
        KafkaByteRateGovernor governor = new(Settings(static l => l.BytesPerSecond = MiB), clock);

        clock.Advance(TimeSpan.FromHours(1));
        governor.Take(2 * MiB);

        governor.DebtWait().Should().Be(TimeSpan.FromSeconds(1), "an hour idle is worth one second of rate, not an hour");
    }

    [Fact]
    public void Over_ten_seconds_a_consumer_that_reads_as_fast_as_it_may_reads_the_ceiling_exactly()
    {
        ManualTimeProvider clock = new();
        KafkaByteRateGovernor governor = new(Settings(static l => l.BytesPerSecond = MiB), clock);
        const int chunk = 64 * 1024;

        long readAfterFirstSecond = 0;
        DateTimeOffset start = clock.GetUtcNow();
        DateTimeOffset end = start.AddSeconds(11);

        while (clock.GetUtcNow() < end)
        {
            TimeSpan wait = governor.DebtWait();

            if (wait > TimeSpan.Zero)
            {
                clock.Advance(wait);
                continue;
            }

            governor.Take(chunk);

            if (clock.GetUtcNow() - start >= TimeSpan.FromSeconds(1))
            {
                readAfterFirstSecond += chunk;
            }
        }

        // 10 s of rate, within 10 % (the live suite's tolerance); on a manual clock it is exact to a chunk.
        readAfterFirstSecond.Should().BeInRange((long) (10 * MiB * 0.9), (long) (10 * MiB * 1.1));
        Math.Abs(readAfterFirstSecond - (10 * MiB)).Should().BeLessThanOrEqualTo(chunk);
    }

    [Fact]
    public void Two_scans_share_one_bucket_so_together_they_read_the_ceiling_and_not_twice_it()
    {
        ManualTimeProvider clock = new();
        KafkaByteRateGovernor governor = new(Settings(static l => l.BytesPerSecond = MiB), clock);
        const int chunk = 64 * 1024;
        long first = 0;
        long second = 0;
        bool turn = false;
        DateTimeOffset end = clock.GetUtcNow().AddSeconds(11);

        while (clock.GetUtcNow() < end)
        {
            TimeSpan wait = governor.DebtWait();

            if (wait > TimeSpan.Zero)
            {
                clock.Advance(wait);
                continue;
            }

            governor.Take(chunk);
            turn = !turn;

            if (turn)
            {
                first += chunk;
            }
            else
            {
                second += chunk;
            }
        }

        (first + second).Should().BeInRange((11 * MiB) - (2 * chunk), (12 * MiB) + (2 * chunk), "11 s of rate plus the first second's burst");
        Math.Abs(first - second).Should().BeLessThanOrEqualTo(chunk, "each scan reads half");
    }

    [Fact]
    public async Task A_wait_ends_when_the_clock_has_moved_by_the_duration_or_when_the_token_is_cancelled()
    {
        ManualTimeProvider clock = new();
        KafkaByteRateGovernor governor = new(Settings(), clock);
        CancellationToken test = TestContext.Current.CancellationToken;

        using (CancellationTokenSource never = new())
        {
            Task<bool> elapsed = Task.Run(() => governor.Wait(TimeSpan.FromSeconds(2), never.Token), test);
            ScanFixture.Until(() => governor.Waiters == 1, "the wait begins");
            clock.Advance(TimeSpan.FromSeconds(1));
            elapsed.IsCompleted.Should().BeFalse("a second of two has passed");
            clock.Advance(TimeSpan.FromSeconds(1));
            (await elapsed.WaitAsync(TestBounds.Gate, test)).Should().BeTrue();
        }

        using CancellationTokenSource cancel = new();
        Task<bool> cancelled = Task.Run(() => governor.Wait(TimeSpan.FromHours(1), cancel.Token), test);
        ScanFixture.Until(() => governor.Waiters == 1, "the second wait begins");
        await cancel.CancelAsync();
        (await cancelled.WaitAsync(TestBounds.Gate, test)).Should().BeFalse();
        governor.Waiters.Should().Be(0);
    }

    [Fact]
    public async Task A_scan_in_debt_does_not_call_consume_until_the_debt_is_paid()
    {
        // 64 KiB a second, and a record of 100 KiB: the first record overdraws the bucket by 36 KiB, which is half a second of rate.
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync(static o => o.Limits.BytesPerSecond = 64 * 1024);
        await using ScanEngineTests.ScriptedScan guard = scan;
        string big = new('x', 100 * 1024);
        long stamp = ScanFixture.T0.ToUnixTimeMilliseconds();

        scan.Reader.Enqueue(new WireReadOutcome.Record(new WireRecord(scan.Topic, 0, 0, [1], System.Text.Encoding.UTF8.GetBytes(big), [], stamp + 1_000, WireTimestampType.CreateTime, 0)));

        KafkaByteRateGovernor governor = scan.Fixture.Provider.GetRequiredService<KafkaByteRateGovernor>();
        ScanFixture.Until(() => governor.Waiters == 1, "the scan thread waits out its debt");

        int readsInDebt = scan.Reader.Reads;
        scan.Reader.Enqueue(new WireReadOutcome.Record(new WireRecord(scan.Topic, 0, 1, [1], System.Text.Encoding.UTF8.GetBytes("v"), [], stamp + 2_000, WireTimestampType.CreateTime, 0)));

        scan.Reader.Reads.Should().Be(readsInDebt, "a thread in debt is in the wait, not in Consume");
        scan.Handle.Progress.Records.Should().Be(1, "the second record is not read while the first is owed");

        scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        ScanFixture.Until(() => scan.Handle.Progress.Records == 2, "the debt is paid and the scan reads on");
        governor.Waiters.Should().Be(0);
    }

    [Fact]
    public void An_owners_hour_is_rolling_on_the_clock_and_a_second_owner_is_untouched()
    {
        ManualTimeProvider clock = new();
        OwnerByteBudget budget = new(Settings(static l => l.OwnerBytesPerHour = 10 * MiB), clock);

        budget.Remaining("a").Should().Be(10 * MiB);

        budget.Add("a", 4 * MiB);
        clock.Advance(TimeSpan.FromMinutes(30));
        budget.Add("a", 5 * MiB);

        budget.Remaining("a").Should().Be(MiB);
        budget.Remaining("b").Should().Be(10 * MiB);

        clock.Advance(TimeSpan.FromMinutes(31));
        budget.Remaining("a").Should().Be(5 * MiB, "the first 4 MiB are more than an hour old");

        clock.Advance(TimeSpan.FromMinutes(30));
        budget.Remaining("a").Should().Be(10 * MiB);

        budget.Add("a", 20 * MiB);
        budget.Remaining("a").Should().Be(0, "never negative");
    }

    [Fact]
    public async Task A_start_takes_a_process_slot_and_an_owner_slot_or_is_refused_never_queued_and_a_finished_scan_gives_them_back()
    {
        int opened = 0;
        ScanFixture fixture = ScanFixture.Create(
            static o =>
            {
                o.Limits.MaxConcurrentScans = 2;
                o.Limits.MaxConcurrentScansPerOwner = 1;
            },
            wrap: wire => new ScriptedWire(wire, options => Interlocked.Increment(ref opened) == 1 ? wire.OpenReader(options) : new ScriptedReader()));
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("slots");

        for (int i = 0; i < 10; i++)
        {
            fixture.Produce(topic, 0, i, "k", "v");
        }

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        KafkaScanOwner a = new("a");
        KafkaScanOwner b = new("b");
        KafkaScanOwner c = new("c");

        KafkaScanStart first = fixture.Start(plan, confirmed: true, owner: a);
        first.Started.Should().BeTrue();
        fixture.Start(plan, confirmed: true, owner: a).Refusal.Should().Be(KafkaScanRefusal.OwnerBusy);

        KafkaScanStart second = fixture.Start(plan, confirmed: true, owner: b);
        second.Started.Should().BeTrue();
        fixture.Start(plan, confirmed: true, owner: c).Refusal.Should().Be(KafkaScanRefusal.ProcessBusy);

        KafkaScanLimiter limiter = fixture.Provider.GetRequiredService<KafkaScanLimiter>();
        limiter.Running.Should().Be(2);

        first.Handle!.Stop();
        await first.Handle.Result.Bounded();
        ScanFixture.Until(() => limiter.Running == 1, "the stopped scan gives its slots back");

        KafkaScanStart third = fixture.Start(plan, confirmed: true, owner: c);
        third.Started.Should().BeTrue("a slot was free again");

        await first.Handle.DisposeAsync();
        await second.Handle!.DisposeAsync();
        await third.Handle!.DisposeAsync();
    }

    [Fact]
    public async Task An_owner_whose_hour_is_spent_is_refused_at_start_and_may_read_again_when_the_hour_has_passed()
    {
        ScanFixture fixture = ScanFixture.Create(static o => o.Limits.OwnerBytesPerHour = MiB);
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("budget");

        for (int i = 0; i < 4; i++)
        {
            fixture.Produce(topic, 0, i, "k", "v");
        }

        KafkaScanOwner owner = new("spender");
        fixture.Provider.GetRequiredService<OwnerByteBudget>().Add(owner.Key, MiB);

        KafkaScanPlan plan = await fixture.Scanner.PlanAsync(ScanFixture.Request([topic]), new KafkaScanOwner("planner"), CancellationToken.None);

        fixture.Start(plan, confirmed: true, owner: owner).Refusal.Should().Be(KafkaScanRefusal.OwnerBudgetSpent);

        fixture.Clock.Advance(TimeSpan.FromMinutes(61));
        KafkaScanStart again = fixture.Start(plan, confirmed: true, owner: owner);

        again.Started.Should().BeTrue();
        await again.Handle!.DisposeAsync();
    }
}
