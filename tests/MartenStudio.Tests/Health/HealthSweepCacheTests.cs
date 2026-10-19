using MartenStudio.Services;
using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MartenStudio.Tests.Health;

/// <summary>
/// One sweep per database per interval, however many pages ask - and none at all while nobody does.
/// </summary>
public class HealthSweepCacheTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Many_callers_inside_one_interval_cost_one_sweep()
    {
        await using HealthHarness harness = HealthHarness.Create();

        for (int i = 0; i < 5; i++)
        {
            await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, harness.Scope.DatabaseId, Token);
        }

        harness.Cache.SweepCount.Should().Be(1);
        harness.Script.Runs["K90"].Should().Be(1);
    }

    [Fact]
    public async Task The_interval_is_HealthInterval_and_the_next_caller_after_it_sweeps_again()
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static options => options.HealthInterval = TimeSpan.FromSeconds(30));

        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, Token);

        harness.Clock.Advance(TimeSpan.FromSeconds(29));
        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, Token);
        harness.Cache.SweepCount.Should().Be(1);

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, Token);
        harness.Cache.SweepCount.Should().Be(2);
    }

    [Fact]
    public async Task A_deeper_sweep_is_cached_for_five_minutes_whatever_the_interval()
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static options => options.HealthInterval = TimeSpan.FromSeconds(15));

        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Deeper, null, Token);
        harness.Clock.Advance(TimeSpan.FromMinutes(4));
        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Deeper, null, Token);

        harness.Script.Runs["E90"].Should().Be(1);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Deeper, null, Token);

        harness.Script.Runs["E90"].Should().Be(2);
    }

    /// <summary>
    /// The badge's half: <see cref="HealthSweepCache.Latest" /> is a lookup - it never sweeps - and it finds
    /// the sweep under the spelling the visitor's scope used, an empty "store default" identity included.
    /// </summary>
    [Fact]
    public async Task Latest_is_a_lookup_that_finds_the_sweep_under_the_scopes_own_spelling()
    {
        await using HealthHarness harness = HealthHarness.Create();
        string identity = harness.Resolved.Database.Id.Identity;

        harness.Cache.Latest("default", identity).Should().BeNull();
        harness.Cache.Latest("default", string.Empty).Should().BeNull();
        harness.Cache.SweepCount.Should().Be(0, "asking for the latest sweep is not a reason to run one");

        HealthSweep sweep = await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, requestedDatabaseId: string.Empty, Token);

        harness.Cache.Latest("default", identity).Should().BeSameAs(sweep);
        harness.Cache.Latest("default", string.Empty).Should().BeSameAs(sweep, "the scope selector's empty identity means the default database");
        harness.Cache.Latest("default", identity, HealthSweepKind.Deeper).Should().BeNull();
    }

    /// <summary>
    /// Acceptance 3: no background work when no page holds a lease. The whole container is built, the sidebar
    /// polls its badges round after round as time passes, and not one sweep runs - there is no hosted
    /// service and no timer of the advisor's own, and the badge only ever reads the last sweep.
    /// </summary>
    [Fact]
    public async Task With_no_page_asking_the_sweep_count_stays_zero_however_long_the_sidebar_polls()
    {
        await using HealthHarness harness = HealthHarness.Create();
        StudioScope scope = harness.Scope;

        for (int poll = 0; poll < 20; poll++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            (await harness.Health.GlanceAsync(scope, Token)).Should().BeNull("there is no sweep to glance at");
        }

        harness.Cache.SweepCount.Should().Be(0);
        harness.Script.Runs.Should().BeEmpty("no check's gatherer ever ran");
    }

    [Fact]
    public void The_studio_registers_no_hosted_service_and_no_timer_for_the_advisor()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();

        services.Where(static x => !x.IsKeyedService && x.ServiceType == typeof(IHostedService))
            .Select(static x => x.ImplementationType?.Namespace ?? x.ImplementationFactory?.Method.DeclaringType?.Namespace ?? string.Empty)
            .Should().NotContain(static x => x.Contains("Health", StringComparison.Ordinal),
                "the advisor runs only when a page asks (plan §3.2)");

        typeof(HealthSweepCache).Assembly.GetTypes()
            .Where(static x => x.Namespace?.StartsWith("MartenStudio.Services.Health", StringComparison.Ordinal) == true)
            .SelectMany(static x => x.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
            .Select(static x => x.FieldType)
            .Should().NotContain([typeof(PeriodicTimer), typeof(Timer), typeof(System.Timers.Timer)],
                "nothing in the advisor keeps a timer; the pages' own polling loops are the only clock");
    }

    /// <summary>
    /// Review item 2: a sweep still running when its interval has passed is joined, never duplicated - a slow
    /// database gets one sweep at a time however long the sweep takes.
    /// </summary>
    [Fact]
    public async Task A_sweep_that_outlives_its_interval_is_joined_rather_than_started_again()
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static options => options.HealthInterval = TimeSpan.FromSeconds(30));

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Script.Gather["K90"] = async (_, _) =>
        {
            await release.Task;
            return new ScriptedFacts("K90", [], null);
        };

        Task<HealthSweep> first = harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, Token);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        Task<HealthSweep> late = harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, Token);

        harness.Cache.SweepCount.Should().Be(1, "the first sweep is still running, so the late caller waits for it");

        release.SetResult();
        (await late).Should().BeSameAs(await first);

        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, Token);
        harness.Cache.SweepCount.Should().Be(2, "once it finished, freshness is the interval again, counted from when it finished");
    }

    /// <summary>The cache a sweep is shared through is single flight (D10).</summary>
    [Fact]
    public async Task Concurrent_callers_share_one_sweep()
    {
        await using HealthHarness harness = HealthHarness.Create();

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Script.Gather["K90"] = async (_, _) =>
        {
            await release.Task;
            return new ScriptedFacts("K90", [], null);
        };

        Task<HealthSweep>[] waiting =
        [
            .. Enumerable.Range(0, 4).Select(_ => harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Regular, null, Token)),
        ];

        release.SetResult();
        HealthSweep[] sweeps = await Task.WhenAll(waiting);

        sweeps.Should().OnlyContain(x => ReferenceEquals(x, sweeps[0]));
        harness.Cache.SweepCount.Should().Be(1);
    }
}
