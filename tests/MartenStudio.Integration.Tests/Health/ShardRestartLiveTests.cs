using System.Collections.Concurrent;

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Projections;

using MartenStudio.Integration.Tests.Projections;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.DaemonSweeps;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>An event the <see cref="FlappingProjection" /> handles.</summary>
/// <param name="Key">The test's own key: the projection throws on a poison event for as long as the key is failing.</param>
/// <param name="Poison">Whether this is the event it throws on.</param>
/// <param name="Gate">A gate to wait at first, when it is closed, or <see langword="null" /> to pass straight through.</param>
public sealed record FlappingEvent(string Key, bool Poison = false, string? Gate = null);

/// <summary>The document the <see cref="FlappingProjection" /> writes.</summary>
public sealed class FlappingDocument
{
    public Guid Id { get; set; }

    public long Sequence { get; set; }
}

/// <summary>
/// An async projection that fails on one event every time it is tried, for as long as its test says - and that a test can
/// hold at a gate short of that event: the time a restarted agent spends on its page before it reaches the event again,
/// made as long as the test needs it.
/// </summary>
/// <remarks>
/// A synchronous wait, as <see cref="GatedProjection" />'s, so the page is held where the daemon cannot cancel it short of
/// the gate; every gate times out after two minutes, so a test that forgets to open one stalls a shard, never the run.
/// </remarks>
internal sealed partial class FlappingProjection : EventProjection
{
    public const string ProjectionName = "Flapping";

    public const string Shard = ProjectionName + ":All";

    private static readonly ConcurrentDictionary<string, bool> Failing = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, ManualResetEventSlim> Gates = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, int> Waiting = new(StringComparer.Ordinal);

    public FlappingProjection()
    {
        Name = ProjectionName;
    }

    /// <summary>Makes every poison event of <paramref name="key" /> throw.</summary>
    public static void Fail(string key) => Failing[key] = true;

    /// <summary>The cause is fixed: <paramref name="key" />'s poison events go through.</summary>
    public static void Fix(string key) => Failing[key] = false;

    /// <summary>Closes (creates) a gate.</summary>
    public static void Close(string gate) => Gates[gate] = new ManualResetEventSlim(false);

    /// <summary>Opens a gate, releasing every page held at it.</summary>
    public static void Open(string gate)
    {
        if (Gates.TryGetValue(gate, out ManualResetEventSlim? held))
        {
            held.Set();
        }
    }

    /// <summary>How many pages are held at the gate right now - so a test knows an attempt is really inside it.</summary>
    public static int WaitingAt(string gate) => Waiting.GetValueOrDefault(gate);

    public static void Project(IEvent<FlappingEvent> flapping, IDocumentOperations operations)
    {
        ArgumentNullException.ThrowIfNull(flapping);
        ArgumentNullException.ThrowIfNull(operations);

        if (flapping.Data.Gate is { } gate && Gates.TryGetValue(gate, out ManualResetEventSlim? held))
        {
            Waiting.AddOrUpdate(gate, 1, static (_, count) => count + 1);
            held.Wait(TimeSpan.FromMinutes(2));
            Waiting.AddOrUpdate(gate, 0, static (_, count) => count - 1);
        }

        if (flapping.Data.Poison && Failing.GetValueOrDefault(flapping.Data.Key))
        {
            throw new InvalidOperationException(
                "FlappingProjection fails on this event for as long as '" + flapping.Data.Key + "' is failing, on purpose.");
        }

        operations.Store(new FlappingDocument { Id = flapping.Id, Sequence = flapping.Sequence });
    }
}

/// <summary>
/// D05 at the default restart cadence: a shard that keeps failing is reported through every restart, and one that got past
/// its failure is quiet after its next page.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here sets <c>Projections.HealthCheckPollingTime</c>: the coordinator starts a paused agent again once its default
/// five seconds have passed (<c>ProjectionCoordinatorBase.startAgentsIfNecessaryAsync</c>, JasperFx 2.74.0 and 2.87.1), as a
/// new agent that reads <c>Running</c>, with no failure, until it reaches the failing event again - and, with extended
/// tracking on, whose start clears the progression row's failure columns. CL2's
/// <see cref="DaemonHealthLiveTests.A_projection_that_throws_without_skipping_is_failed_or_paused" /> holds a pause still for
/// ten minutes to prove the paused state; these prove the moments around it.
/// </para>
/// <para>
/// <b>The restart window, held open.</b> A restarted agent reaches its failing event again as soon as it has read its page,
/// so a sweep lands in that window by chance - more often the longer the page takes, which is a store under load. Here the
/// page begins with an event that waits at a gate, closed while the shard is paused: the coordinator's restart holds inside
/// its page, Running, short of the event it fails on, for as long as the test needs - and the sweep taken there is the one
/// that found nothing before D05 remembered what it saw.
/// </para>
/// </remarks>
public class ShardRestartLiveTests(PostgresFixture postgres)
{
    private const string Shard = FlappingProjection.Shard;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    [HealthFires("D05")]
    public async Task A_shard_that_keeps_failing_is_reported_through_every_restart_at_the_default_cadence(bool extendedTracking)
    {
        string suffix = extendedTracking ? "tracked" : "untracked";
        string key = "cl3-flapping-" + suffix;
        string gate = key + "-gate";

        await using DaemonHealthHost host = await StartAsync("cl3_d05_restarts_" + suffix, extendedTracking);

        host.Store.Options.Events.Daemon.HealthCheckPollingTime.Should().Be(TimeSpan.FromSeconds(5), "the default restart cadence is the one under test");

        FlappingProjection.Fail(key);

        try
        {
            await host.WaitForDaemonAsync();

            // One event the shard gets through, so that it has a row and a position to fail from.
            await AppendAsync(host, new FlappingEvent(key));
            long floor = await host.HighestSequenceAsync(postgres);
            await host.WaitForShardAsync(Shard, floor, postgres);

            // Then one page: an event that waits at the gate - not closed yet - and the event it fails on, behind it.
            await AppendAsync(host, new FlappingEvent(key, Gate: gate), new FlappingEvent(key, Poison: true));

            List<ISubscriptionAgent> attempts = [];
            await NextAttemptAsync(host, attempts, AgentStatus.Paused, "the poison event to pause the shard's first agent");

            // Closed at once, so that any restart from here on holds at the gate until the test lets it through.
            FlappingProjection.Close(gate);

            if (extendedTracking)
            {
                await WaitForRowAsync(host, "failure_category is not null", "the row to record the failure");
            }

            // The first sweep has no memory yet, so it must read the first agent while it is still paused. A coordinator restart
            // landing during the sweep would leave D05 nothing to say - the one race the closed gate cannot hold - so a sweep
            // with no finding is named for what it is, and one with a finding is judged as it stands, whenever the restart came.
            HealthSweep firstSweep = await host.SweepAsync();

            if (!Result(firstSweep, "D05").Findings.Any(static x => x.Subject == Shard))
            {
                host.Daemon.CurrentAgents().Should().Contain(
                    x => ReferenceEquals(x, attempts[0]),
                    "D05 found nothing because the coordinator restarted the shard during the first sweep, before it had anything to remember - the race this test cannot hold");
            }

            HealthFinding first = D05(firstSweep);
            first.Title.Should().Be("Shard " + Shard + " is paused", "nothing has been restarted yet");
            first.Evidence.Should().Contain(new HealthEvidence("Status", "Paused (the daemon in this process)"));

            for (int cycle = 1; cycle <= 2; cycle++)
            {
                // The coordinator's restart, five seconds after the pause: a new agent, held inside its page at the gate, short
                // of the event it fails on - Running, with no failure, and a row its start has cleared.
                ISubscriptionAgent restarted = await NextAttemptAsync(host, attempts, AgentStatus.Running, $"restart {cycle} to start a new agent");
                await ProjectionsFixture.WaitForAsync(
                    () => Task.FromResult(FlappingProjection.WaitingAt(gate) == 1),
                    $"restart {cycle} to hold at the gate",
                    TimeSpan.FromSeconds(60),
                    Token);

                if (extendedTracking)
                {
                    await WaitForRowAsync(host, "agent_status = 'Running' and failure_category is null", $"restart {cycle}'s start to clear the row's failure");
                }

                host.Advance(TimeSpan.FromMinutes(1));
                HealthSweep inTheWindow = await host.SweepAsync();

                restarted.Status.Should().Be(AgentStatus.Running, "the sweep read an agent the gate held short of its failure");
                attempts[^1].Should().BeSameAs(restarted);

                // What D05 said nothing about before it remembered: the daemon here says Running, and (tracked) so does the row.
                HealthFinding running = D05(inTheWindow);
                running.Severity.Should().Be(HealthSeverity.Warning);
                running.Title.Should().Be("Shard " + Shard + " keeps failing and being restarted");
                running.Evidence.Should().Contain(new HealthEvidence(
                    "Status", "Running (the daemon in this process), started again after a failure and not past it yet"));
                running.Evidence.Should().Contain(x => x.Label == "Seen failing before" && x.Value.Contains("at position " + DaemonCheckConfiguration.Number(floor), StringComparison.Ordinal));
                running.Remedy!.Code.Should().Be("opts.Projections.Errors.SkipApplyErrors = true;", "the failure the advisor remembers was an apply error");

                // Let it through to the event it fails on, and close the gate behind it for the next restart.
                FlappingProjection.Open(gate);
                await ProjectionsFixture.WaitForAsync(
                    () => Task.FromResult(restarted.Status == AgentStatus.Paused),
                    $"restart {cycle} to fail on the poison event again",
                    TimeSpan.FromSeconds(60),
                    Token);
                FlappingProjection.Close(gate);

                host.Advance(TimeSpan.FromMinutes(1));
                HealthSweep pausedAgain = await host.SweepAsync();
                bool sameAttempt = host.Daemon.CurrentAgents().Any(x => ReferenceEquals(x, restarted));

                HealthFinding again = D05(pausedAgain);
                again.Severity.Should().Be(HealthSeverity.Warning);
                again.Title.Should().Be("Shard " + Shard + " keeps failing and being restarted", "a later pause, no further on, is the same shard failing again");

                if (sameAttempt)
                {
                    again.Evidence.Should().Contain(new HealthEvidence("Status", "Paused (the daemon in this process)"));
                }
                else
                {
                    TestContext.Current.TestOutputHelper?.WriteLine($"Cycle {cycle}: the coordinator restarted the shard during the sweep.");
                }
            }

            attempts.Should().HaveCount(3, "the first agent and two restarts were each seen");
            (await host.ShardPositionAsync(Shard, postgres)).Should().Be(floor, "no attempt got past the event it fails on");
        }
        finally
        {
            FlappingProjection.Open(gate);
            FlappingProjection.Fix(key);
        }
    }

    /// <summary>
    /// The quiet half: a shard that failed and then got past the event - the cause fixed, the coordinator's next start
    /// committing it - says nothing from the first sweep after that progress, though the failure the advisor saw is only a
    /// minute old and still in its memory.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D05")]
    public async Task A_shard_that_failed_once_and_got_past_it_is_quiet_after_its_next_progress()
    {
        const string key = "cl3-recovers";

        await using DaemonHealthHost host = await StartAsync("cl3_d05_recovers", extendedTracking: false);

        FlappingProjection.Fail(key);

        try
        {
            await host.WaitForDaemonAsync();

            await AppendAsync(host, new FlappingEvent(key));
            long floor = await host.HighestSequenceAsync(postgres);
            await host.WaitForShardAsync(Shard, floor, postgres);

            await AppendAsync(host, new FlappingEvent(key, Poison: true));
            long poison = await host.HighestSequenceAsync(postgres);

            await NextAttemptAsync(host, [], AgentStatus.Paused, "the poison event to pause the shard");

            // The anti-vacuity half: the advisor saw it fail, and remembers where it stood.
            D05(await host.SweepAsync()).Title.Should().Be("Shard " + Shard + " is paused");
            HealthHistoryView history = host.Services.GetRequiredService<HealthHistory>()
                .Read(MartenStoreRegistry.DefaultStoreKey, host.Database.Id.Identity);
            history.Trend(DaemonSeries.ShardFailedAt(Shard)).Latest.Should().Be(floor, "the sweep recorded where the shard stood when it failed");

            // The cause is fixed: the coordinator's next start gets past the event, and commits.
            FlappingProjection.Fix(key);
            await ProjectionsFixture.WaitForAsync(
                () => Task.FromResult(host.Daemon.CurrentAgents().Any(x =>
                    x.Name.Identity == Shard && x.Status == AgentStatus.Running && x.Position >= poison)),
                "a restart of the shard to commit past the event it failed on",
                TimeSpan.FromSeconds(60),
                Token);

            host.Advance(TimeSpan.FromMinutes(1));
            HealthCheckResult result = Result(await host.SweepAsync(), "D05");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Should().NotContain(static x => x.Subject == Shard,
                "its first committed progress past the failure ends it, though the failure is a minute old");
            (await host.ShardPositionAsync(Shard, postgres)).Should().BeGreaterThanOrEqualTo(poison);
        }
        finally
        {
            FlappingProjection.Fix(key);
        }
    }

    private Task<DaemonHealthHost> StartAsync(string schema, bool extendedTracking) =>
        DaemonHealthHost.StartAsync(
            postgres,
            schema,
            seed: false,
            store: options =>
            {
                options.Projections.Errors.SkipApplyErrors = false;
                options.Events.EnableExtendedProgressionTracking = extendedTracking;
                options.Events.AddEventType<FlappingEvent>();
                options.Projections.Add(new FlappingProjection(), ProjectionLifecycle.Async);
            });

    private static async Task AppendAsync(DaemonHealthHost host, params FlappingEvent[] events)
    {
        await using IDocumentSession session = host.Store.LightweightSession();

        session.Events.StartStream(Guid.NewGuid(), events.Cast<object>().ToArray());
        await session.SaveChangesAsync(Token);
    }

    /// <summary>
    /// Waits for an agent of the shard the test has not seen yet - the first, or the coordinator's next restart - to read
    /// <paramref name="status" />, and adds it to <paramref name="attempts" />: the restart count is the list's.
    /// </summary>
    private static async Task<ISubscriptionAgent> NextAttemptAsync(DaemonHealthHost host, List<ISubscriptionAgent> attempts, AgentStatus status, string what)
    {
        ISubscriptionAgent? found = null;

        await ProjectionsFixture.WaitForAsync(
            () =>
            {
                found = host.Daemon.CurrentAgents().FirstOrDefault(x =>
                    x.Name.Identity == Shard && x.Status == status && !attempts.Exists(seen => ReferenceEquals(seen, x)));
                return Task.FromResult(found is not null);
            },
            what,
            TimeSpan.FromSeconds(60),
            Token);

        attempts.Add(found!);
        return found!;
    }

    private async Task WaitForRowAsync(DaemonHealthHost host, string condition, string what) =>
        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
                await using NpgsqlCommand command = new(
                    "select " + condition + " from " + SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression") + " where name = @name",
                    connection);
                command.Parameters.AddWithValue("name", Shard);

                return await command.ExecuteScalarAsync(Token) is true;
            },
            what,
            TimeSpan.FromSeconds(60),
            Token);

    private static HealthFinding D05(HealthSweep sweep)
    {
        HealthCheckResult result = Result(sweep, "D05");
        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);

        return result.Findings.Should().ContainSingle(static x => x.Subject == Shard, "D05 must see the shard: {0}", result.Note).Which;
    }
}
