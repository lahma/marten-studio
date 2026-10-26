using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// R03 and the Replication page on a real primary and standby (B0's recipe), on PostgreSQL 17: lag measured from the primary
/// as a role with <c>pg_read_all_stats</c>, from the standby as a role with no grant at all, "not checked" from the primary
/// without the grant, and quiet once the standby has caught up.
/// </summary>
/// <remarks>
/// The lag is made the way B0 made it - replay paused on the standby - and pushed past a gibibyte with 70 segment switches
/// rather than a gibibyte of rows. That gibibyte is 17's alone; 18's pair proves its own shapes without it, and the two pairs
/// run one after the other (<see cref="ReplicationHeavySuite" />), each test giving back what it wrote with a checkpoint
/// on both servers (W5-R2-fix 8).
/// </remarks>
[Collection(ReplicationHeavySuite.Name)]
public class StandbyLag17LiveTests(ReplicationPair17Fixture pairs) : IClassFixture<ReplicationPair17Fixture>
{
    [PostgresFact]
    [HealthFires("R03")]
    public async Task Paused_replay_a_gibibyte_behind_fires_from_the_primary_with_the_grant_and_from_the_standby_without_one()
    {
        ReplicationPair pair = await pairs.PairAsync();
        string standby = pair.StandbyConnectionString(MatrixRole.Superuser);

        try
        {
            await ReplicationServer.Execute(standby, "select pg_catalog.pg_wal_replay_pause()");
            await ReplicationServer.UntilAsync(
                async () => await ReplicationServer.Scalar<string>(standby, "select pg_catalog.pg_get_wal_replay_pause_state()") == "paused",
                TimeSpan.FromSeconds(30),
                "replay to pause");

            // Ten segments at a time, each batch given back on the primary by a checkpoint once the standby has flushed it: the
            // standby holds what it has received and not replayed, so its slot holds nothing back there, and the gibibyte is
            // on one server's disk at a time, never two (W5-R2-fix 8).
            for (int batch = 0; batch < 7; batch++)
            {
                await pair.Primary.SwitchSegmentsAsync(10);
                await ReplicationServer.UntilAsync(
                    async () => await pair.Primary.ScalarAsync<bool>(
                        "select coalesce(bool_and(r.flush_lsn >= pg_catalog.pg_current_wal_lsn()), false) from pg_catalog.pg_stat_replication r"),
                    TimeSpan.FromSeconds(60),
                    "the standby to have received the batch");
                await pair.Primary.ExecuteAsync("checkpoint");
            }

            await ReplicationServer.UntilAsync(
                async () => await pair.Primary.ScalarAsync<bool>(
                    "select coalesce(bool_and(pg_catalog.pg_wal_lsn_diff(pg_catalog.pg_current_wal_lsn(), r.replay_lsn) >= 1073741824), false) from pg_catalog.pg_stat_replication r"),
                TimeSpan.FromSeconds(60),
                "the standby's replay to be a gibibyte behind");

            // From the primary, as a member of pg_read_all_stats.
            await using (StudioHost primary = pair.Primary.Host(MatrixRole.Stats, TimeProvider.System))
            {
                HealthReportFinding finding = (await StandbyLagLive.ReportAsync(primary)).Findings.Should().ContainSingle(static x => x.Code == "R03").Subject;

                finding.Finding.Variant.Should().Be("primary-view");
                finding.Severity.Should().Be(HealthSeverity.Warning, "a gibibyte behind; replay_lag stays under an hour");
                finding.Evidence.Should().Contain(x => x.Label == "Furthest behind in WAL" && x.Value.EndsWith("GiB", StringComparison.Ordinal));
                finding.Evidence.Should().Contain(static x => x.Label.StartsWith("Replica", StringComparison.Ordinal) && x.Value.Contains("'walreceiver'", StringComparison.Ordinal),
                    "the standby is named to a visitor through the activity gate - every capability is on and no write policy is set");
            }

            // From the standby, as a role with no grant at all: received less replayed, and the pause state.
            await using (StudioHost onStandby = ReplicationServer.HostOver(pair.StandbyConnectionString(MatrixRole.Probe), TimeProvider.System))
            {
                HealthReportFinding finding = (await StandbyLagLive.ReportAsync(onStandby)).Findings.Should().ContainSingle(static x => x.Code == "R03").Subject;

                finding.Finding.Variant.Should().Be("paused");
                finding.Severity.Should().Be(HealthSeverity.Warning);
                finding.Evidence.Should().Contain(new HealthEvidence("Replay", "paused"));
            }

            TestContext.Current.TestOutputHelper?.WriteLine(
                "disk at the peak (pg_wal, KiB): primary {0}, standby {1}", await pair.Primary.WalKibAsync(), await pair.StandbyWalKibAsync());
        }
        finally
        {
            await ReplicationServer.Execute(standby, "select pg_catalog.pg_wal_replay_resume()");
            await pair.CheckpointBothAsync();

            TestContext.Current.TestOutputHelper?.WriteLine(
                "disk after the checkpoints (pg_wal, KiB): primary {0}, standby {1}", await pair.Primary.WalKibAsync(), await pair.StandbyWalKibAsync());
        }
    }

    [PostgresFact]
    public Task From_the_primary_without_pg_read_all_stats_R03_is_not_checked_and_names_the_grant() =>
        StandbyLagLive.NotCheckedAsync(pairs);

    [PostgresFact]
    [HealthQuiet("R03")]
    public Task A_standby_that_has_caught_up_is_quiet_from_either_side() => StandbyLagLive.QuietAsync(pairs);

    [PostgresFact]
    public Task The_pages_facts_read_on_both_sides_without_a_failure_in_this_releases_shape() => StandbyLagLive.FactsAsync(pairs, eighteen: false);
}

/// <summary>The same pair on PostgreSQL 18, for the checkpointer's <c>num_done</c> and <c>pg_stat_io</c>'s byte columns.</summary>
[Collection(ReplicationHeavySuite.Name)]
public class StandbyLag18LiveTests(ReplicationPair18Fixture pairs) : IClassFixture<ReplicationPair18Fixture>
{
    [PostgresFact]
    public Task From_the_primary_without_pg_read_all_stats_R03_is_not_checked_and_names_the_grant() =>
        StandbyLagLive.NotCheckedAsync(pairs);

    [PostgresFact]
    [HealthQuiet("R03")]
    public Task A_standby_that_has_caught_up_is_quiet_from_either_side() => StandbyLagLive.QuietAsync(pairs);

    [PostgresFact]
    public Task The_pages_facts_read_on_both_sides_without_a_failure_in_this_releases_shape() => StandbyLagLive.FactsAsync(pairs, eighteen: true);
}

/// <summary>What both releases' pair classes run.</summary>
internal static class StandbyLagLive
{
    public static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));

    public static async Task<T> InCircuitAsync<T>(StudioHost host, Func<IServiceProvider, ResolvedSource, Task<T>> read) =>
        await host.CircuitAsync("admin", async services =>
        {
            ResolvedSource resolved = await services.GetRequiredService<StudioSourceResolver>().ResolveAsync(Scope, null, Token);
            return await read(services, resolved);
        });

    public static async Task NotCheckedAsync(ReplicationPairFixture pairs)
    {
        ReplicationPair pair = await pairs.PairAsync();
        await using StudioHost host = pair.Primary.Host(MatrixRole.Probe, TimeProvider.System);

        HealthReport report = await ReportAsync(host);
        HealthCheckStatus status = report.Checks.Single(static x => x.Code == "R03");

        status.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        status.Problem.Should().StartWith("Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role probe.")
            .And.Contain("1 standby streams from this server")
            .And.Contain("multi-host data source");
        report.Findings.Should().NotContain(static x => x.Code == "R03");
    }

    public static async Task QuietAsync(ReplicationPairFixture pairs)
    {
        ReplicationPair pair = await pairs.PairAsync();
        await ReplicationServer.Execute(pair.StandbyConnectionString(MatrixRole.Superuser), "select pg_catalog.pg_wal_replay_resume()");
        await pair.CaughtUpAsync();

        await using (StudioHost primary = pair.Primary.Host(MatrixRole.Stats, TimeProvider.System))
        {
            HealthReport report = await ReportAsync(primary);

            report.Findings.Should().NotContain(static x => x.Code == "R03");
            report.Checks.Single(static x => x.Code == "R03").Note.Should().StartWith("1 standby streams from this server, none of them behind");
        }

        // On the standby itself, replay caught up with what it has received - the primary's view above says replay reached the
        // primary's position, and the standby may since have received a record (a checkpoint, a running-xacts record) it has
        // not replayed yet (W5-R2-fix3 3).
        await ReplicationServer.UntilAsync(
            async () => await ReplicationServer.Scalar<bool>(
                pair.StandbyConnectionString(MatrixRole.Superuser),
                "select coalesce(pg_catalog.pg_last_wal_receive_lsn() = pg_catalog.pg_last_wal_replay_lsn(), false)"),
            TimeSpan.FromSeconds(60),
            "the standby to have replayed everything it received");

        await using (StudioHost onStandby = ReplicationServer.HostOver(pair.StandbyConnectionString(MatrixRole.Probe), TimeProvider.System))
        {
            HealthReport report = await ReportAsync(onStandby);

            report.Findings.Should().NotContain(static x => x.Code == "R03");
            report.Checks.Single(static x => x.Code == "R03").Note.Should().StartWith("This server is a standby, 0 B behind what it has received");
        }
    }

    public static async Task FactsAsync(ReplicationPairFixture pairs, bool eighteen)
    {
        ReplicationPair pair = await pairs.PairAsync();

        await using StudioHost primary = pair.Primary.Host(MatrixRole.Stats, TimeProvider.System);
        ReplicationFacts facts = await InCircuitAsync(primary, (services, resolved) => services.GetRequiredService<IReplicationFactsReader>().ReadAsync(resolved, Token));

        facts.Failures.Should().BeEmpty();
        facts.Role.Value!.InRecovery.Should().BeFalse();
        facts.Standbys.Value!.Total.Should().Be(1);
        facts.Standbys.Value.Rows.Should().ContainSingle().Which.Should().Match<StandbyRow>(static x => x.Readable && x.Physical,
            "stats is a member of pg_read_all_stats, and a standby's sender is a physical one");
        facts.Standbys.Value.OtherDatabases.Should().Be(0);
        facts.Slots.Value!.Slots.Should().Contain(static x => x.Name == "standby1" && x.SlotType == "physical" && x.Active);
        facts.Checkpoints.Value!.Counting.Should().Be(eighteen ? CheckpointCounting.CheckpointerDone : CheckpointCounting.CheckpointerWithSkipped);
        facts.Checkpoints.Value.Done.HasValue.Should().Be(eighteen, "num_done is 18's");
        facts.Io.Value!.BytesFromOpBytes.Should().Be(!eighteen);
        facts.Io.Value.Groups.Any(static x => x.IsWal).Should().Be(eighteen, "18 counts WAL I/O in pg_stat_io");
        facts.Io.Value.Groups.Should().Contain(static x => x.BackendType == "client backend" && x.ReadBytes != null);
        facts.Wal.Value!.Writes.HasValue.Should().Be(!eighteen, "18 moved WAL writes to pg_stat_io");
        facts.Tablespaces.Value!.Rows.Select(static x => x.Name).Should().Contain(["pg_default", "pg_global"]);

        await using StudioHost onStandby = ReplicationServer.HostOver(pair.StandbyConnectionString(MatrixRole.Probe), TimeProvider.System);
        ReplicationFacts standby = await InCircuitAsync(onStandby, (services, resolved) => services.GetRequiredService<IReplicationFactsReader>().ReadAsync(resolved, Token));

        standby.Failures.Should().BeEmpty();
        standby.Role.Value!.InRecovery.Should().BeTrue();
        standby.Role.Value.ReceiveLsn.Should().NotBeNull("it streams");
        standby.Role.Value.PauseState.Should().NotBeNull();
        standby.Settings.Value!.ApplyDelay.Should().Be(TimeSpan.Zero, "recovery_min_apply_delay is read on the standby");
        standby.Archiver.Value!.CurrentWal.Should().BeNull("no current file is named in recovery");

        // The view, names through the gate: the standby is named on the primary.
        ReplicationView view = await InCircuitAsync(primary, (services, _) => services.GetRequiredService<IReplicationService>().GetAsync(Scope, Token));
        view.NamesShown.Should().BeTrue();
        view.Standbys.Value!.Rows.Should().ContainSingle().Which.Name.Should().Be("walreceiver");
    }
}
