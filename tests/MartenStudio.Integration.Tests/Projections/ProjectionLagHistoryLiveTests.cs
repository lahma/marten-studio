using JasperFx.Events.Projections;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// M2 against a real daemon, through the projections service as a page polls it: a shard that falls behind and then
/// catches up gets the right arrow, a rebuild gets an estimate once three of its samples show progress and not before,
/// and a threshold a host writes into the progression row appears on the shard's history.
/// </summary>
/// <remarks>
/// <para>
/// <b>The page's own samples, and nothing else.</b> Every number here comes from <see cref="IProjectionDataService" />'s
/// <c>GetProjectionsAsync</c>, whose snapshot read is what records them - the test is the page, polling. The history is
/// registered with a half-second floor and the studio refreshes every second, so a sample is about a second apart rather
/// than five; the arithmetic is the same at any spacing.
/// </para>
/// <para>
/// <b>A slow shard, on purpose.</b> The <see cref="GatedProjection" /> is registered here with a batch of one event, so its
/// progress is committed event by event, and its events carry a delay, so a drain and a replay take seconds rather than
/// milliseconds - long enough to be sampled. A closed gate holds its page: a shard alive and not moving.
/// </para>
/// <para>
/// Waits are on the condition (AGENTS.md's testing rule); events are appended only when a new sample has been recorded,
/// so the lag grows by the same amount at every sample however fast the machine is.
/// </para>
/// </remarks>
public class ProjectionLagHistoryLiveTests(PostgresFixture postgres)
{
    private const string Shard = GatedProjection.ProjectionName + ":All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<DaemonHealthHost> StartAsync(PostgresFixture postgres, string schema) =>
        DaemonHealthHost.StartAsync(
            postgres,
            schema,
            seed: false,
            store: options =>
            {
                options.Events.AddEventType<GatedEvent>();

                GatedProjection slow = new();
                slow.Options.BatchSize = 1;
                options.Projections.Add(slow, ProjectionLifecycle.Async);
            },
            studio: options => options.RefreshInterval = TimeSpan.FromSeconds(1),
            services: services => services.AddSingleton(provider =>
                new ProjectionLagHistory(provider.GetService<HealthDatabaseBound>(), TimeSpan.FromMilliseconds(500))));

    /// <summary>What the projections screen's view says of the gated shard right now, as one poll would read it.</summary>
    private static async Task<(ShardProgress? Shard, ShardLagHistory? History)> PollAsync(DaemonHealthHost host)
    {
        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(host.Scope, Token));

        return (view.Progress.FirstOrDefault(static x => x.ShardName == Shard), view.LagHistories.GetValueOrDefault(Shard));
    }

    /// <summary>Appends ungated events and waits until the shard has a progression row past them.</summary>
    private async Task StartTheShardAsync(DaemonHealthHost host, int events, int delayMilliseconds = 0)
    {
        await host.WaitForDaemonAsync();
        await host.AppendGatedAsync(events, delayMilliseconds: delayMilliseconds);
        await host.WaitForShardAsync(Shard, await host.HighestSequenceAsync(postgres), postgres);
    }

    [PostgresFact]
    public async Task A_shard_falling_behind_then_catching_up_gets_the_right_arrow()
    {
        const string gate = "m2-lag-arrow";

        await using DaemonHealthHost host = await StartAsync(postgres, "m2_lag_arrow");
        await StartTheShardAsync(host, 3);

        GatedProjection.Close(gate);

        try
        {
            // Its next page holds at the gate: the shard is alive and does not move.
            await host.AppendGatedAsync(1, gate);
            await ProjectionsFixture.WaitForAsync(
                () => Task.FromResult(GatedProjection.WaitingAt(gate) > 0), "the shard's page to reach the gate", TimeSpan.FromSeconds(60), Token);

            // Events keep arriving, ten at every new sample for at least four: the lag grows at every one, the position
            // stands still - and the backlog is long enough that its drain spans more samples than the arrow reads.
            ShardLagHistory? behind = null;
            DateTimeOffset latest = DateTimeOffset.MinValue;
            int rounds = 0;

            await ProjectionsFixture.WaitForAsync(
                async () =>
                {
                    (_, ShardLagHistory? history) = await PollAsync(host);

                    if (history is not { Points.Count: > 0 } || history.Points[^1].At == latest)
                    {
                        return false;
                    }

                    latest = history.Points[^1].At;

                    if (rounds >= 4 && history.Trend.Direction == LagDirection.Growing && history.Stall is not null)
                    {
                        behind = history;
                        return true;
                    }

                    await host.AppendGatedAsync(10, delayMilliseconds: 400);
                    rounds++;
                    return false;
                },
                "the shard to read as falling behind and stalled",
                TimeSpan.FromSeconds(90),
                Token);

            behind!.Arrow.Should().Be("↑");
            behind.WithheldBecause.Should().BeNull("a stall is coloured and said, never withheld");
            behind.StallSentence().Should().StartWith("Stalled: not moved for ");
            behind.Summary().Should().Contain("falling behind by");
            LagChart.Draw(behind).Stalled.Should().NotBeEmpty();
        }
        finally
        {
            GatedProjection.Open(gate);
        }

        // Released, it drains at about two and a half events a second: the lag shrinks at every sample.
        ShardLagHistory? catchingUp = null;

        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                (_, ShardLagHistory? history) = await PollAsync(host);

                if (history?.Trend.Direction is LagDirection.Draining)
                {
                    catchingUp = history;
                    return true;
                }

                return false;
            },
            "the shard to read as catching up",
            TimeSpan.FromSeconds(90),
            Token);

        catchingUp!.Arrow.Should().Be("↓");
        catchingUp.Trend.CaughtUpIn.Should().NotBeNull();
        catchingUp.Stall.Should().BeNull("it moves at every sample now");
        catchingUp.Summary().Should().Contain("catching up over the last");

        await ProjectionsFixture.WaitForAsync(
            async () => (await PollAsync(host)).History?.Trend.Direction == LagDirection.CaughtUp,
            "the shard to be caught up",
            TimeSpan.FromSeconds(90),
            Token);
    }

    [PostgresFact]
    public async Task A_rebuild_shows_an_estimate_once_three_of_its_samples_show_progress()
    {
        await using DaemonHealthHost host = await StartAsync(postgres, "m2_lag_rebuild");

        // Forty events at a quarter of a second each: ten seconds a pass, continuous and replay alike.
        await StartTheShardAsync(host, 40, delayMilliseconds: 250);

        OperationStart start = await host.ProjectionsAsync(x => x.RebuildAsync(host.Scope, GatedProjection.ProjectionName, Token));
        start.Started.Should().BeTrue();

        List<RebuildEta> seen = [];
        ShardLagHistory? estimated = null;

        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                (_, ShardLagHistory? history) = await PollAsync(host);

                if (history?.Rebuild is not { } eta)
                {
                    return false;
                }

                seen.Add(eta);

                if (eta.Remaining is not null)
                {
                    estimated = history;
                    return true;
                }

                return false;
            },
            "the rebuild's estimate",
            TimeSpan.FromSeconds(90),
            Token);

        RebuildEta estimate = estimated!.Rebuild!;
        estimate.HasEnoughSamples.Should().BeTrue();
        estimate.ProgressSamples.Should().BeGreaterThanOrEqualTo(ShardLagAnalysis.EtaProgressSamples);
        estimate.PerSecond.Should().BePositive();
        estimate.Left.Should().BePositive();

        seen.Should().Contain(static x => !x.HasEnoughSamples && x.Remaining == null,
            "before three samples showed progress the screen said there were not enough, and gave no number");
        seen.Where(static x => x.Remaining is not null).Should().OnlyContain(static x => x.ProgressSamples >= ShardLagAnalysis.EtaProgressSamples,
            "no estimate from fewer");

        estimated.RebuildSentence().Should().StartWith("Rebuild: about ").And.Contain(" events at ").And.Contain(", from the last ");

        await ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(host.Services.GetRequiredService<StudioOperationTracker>().Find(start.Id)?.IsRunning == false),
            "the rebuild to finish",
            TimeSpan.FromSeconds(120),
            Token);
    }

    [PostgresFact]
    public async Task A_threshold_written_into_the_progression_row_appears_on_the_shard_and_its_chart()
    {
        const string gate = "m2-lag-threshold";

        await using DaemonHealthHost host = await StartAsync(postgres, "m2_lag_threshold");
        await StartTheShardAsync(host, 3);

        // What a host does: Marten creates the two columns and writes neither (#5173).
        await using (NpgsqlConnection connection = await postgres.OpenAsync(Token))
        await using (NpgsqlCommand command = new(
            "update " + SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression")
            + " set warning_behind_threshold = 2, critical_behind_threshold = 50 where name = @name",
            connection))
        {
            command.Parameters.AddWithValue("name", Shard);
            (await command.ExecuteNonQueryAsync(Token)).Should().Be(1);
        }

        GatedProjection.Close(gate);

        try
        {
            await host.AppendGatedAsync(1, gate);
            await host.AppendGatedAsync(5);

            (ShardProgress? Shard, ShardLagHistory? History) polled = default;

            await ProjectionsFixture.WaitForAsync(
                async () =>
                {
                    polled = await PollAsync(host);
                    return polled is { Shard: { WarningThreshold: 2, Lag: >= 2 }, History: { WarningThreshold: 2 } history } && history.Points[^1].Lag >= 2;
                },
                "the row's thresholds and a lag past the warning on the shard",
                TimeSpan.FromSeconds(60),
                Token);

            polled.Shard!.CriticalThreshold.Should().Be(50);
            polled.Shard.Severity.Should().Be(LagSeverity.Warning, "the row's own warning threshold colours it");

            ShardLagHistory history = polled.History!;
            history.CriticalThreshold.Should().Be(50);
            history.ThresholdSentence().Should().Be(
                "Thresholds: warning above 2 (its progression row's), critical above 50 (its progression row's).");
            LagChart.Draw(history).Warning.Should().NotBeEmpty("a lag of two or more is within half of the row's warning of 2");
        }
        finally
        {
            GatedProjection.Open(gate);
        }
    }
}
