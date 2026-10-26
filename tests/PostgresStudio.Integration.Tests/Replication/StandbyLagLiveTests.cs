using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// R03 and the Replication page on a real primary and standby (B0's recipe), on PostgreSQL 17 and 18: lag measured from the
/// primary as a role with <c>pg_read_all_stats</c>, from the standby as a role with no grant at all, "not checked" from the
/// primary without the grant, and quiet once the standby has caught up.
/// </summary>
/// <remarks>
/// The lag is made the way B0 made it - replay paused on the standby - and pushed past a gibibyte with 70 segment switches
/// rather than a gibibyte of rows: each switch completes a 16 MiB segment, so the primary's WAL position is 1.1 GiB past the
/// standby's replay in seconds. Each test leaves replay resumed and caught up, whatever it asserted.
/// </remarks>
public class StandbyLagLiveTests(ReplicationPairFixture pairs) : IClassFixture<ReplicationPairFixture>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    public static TheoryData<string> Images => [ReplicationServer.DefaultImage, ReplicationServer.Image18];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));

    private static async Task<T> InCircuitAsync<T>(StudioHost host, Func<IServiceProvider, ResolvedSource, Task<T>> read) =>
        await host.CircuitAsync("admin", async services =>
        {
            ResolvedSource resolved = await services.GetRequiredService<StudioSourceResolver>().ResolveAsync(Scope, null, Token);
            return await read(services, resolved);
        });

    [PostgresTheory]
    [MemberData(nameof(Images))]
    [HealthFires("R03")]
    public async Task Paused_replay_a_gibibyte_behind_fires_from_the_primary_with_the_grant_and_from_the_standby_without_one(string image)
    {
        ReplicationPair pair = await pairs.PairAsync(image);
        string standby = pair.StandbyConnectionString(MatrixRole.Superuser);

        try
        {
            await ReplicationServer.Execute(standby, "select pg_catalog.pg_wal_replay_pause()");
            await ReplicationServer.UntilAsync(
                async () => await ReplicationServer.Scalar<string>(standby, "select pg_catalog.pg_get_wal_replay_pause_state()") == "paused",
                TimeSpan.FromSeconds(30),
                "replay to pause");

            await pair.Primary.SwitchSegmentsAsync(70);
            await ReplicationServer.UntilAsync(
                async () => await pair.Primary.ScalarAsync<bool>(
                    "select coalesce(bool_and(pg_catalog.pg_wal_lsn_diff(pg_catalog.pg_current_wal_lsn(), r.replay_lsn) >= 1073741824), false) from pg_catalog.pg_stat_replication r"),
                TimeSpan.FromSeconds(60),
                "the standby's replay to be a gibibyte behind");

            // From the primary, as a member of pg_read_all_stats.
            await using (StudioHost primary = pair.Primary.Host(MatrixRole.Stats, TimeProvider.System))
            {
                HealthReportFinding finding = (await ReportAsync(primary)).Findings.Should().ContainSingle(static x => x.Code == "R03").Subject;

                finding.Finding.Variant.Should().Be("primary-view");
                finding.Severity.Should().Be(HealthSeverity.Warning, "a gibibyte behind; replay_lag stays under an hour");
                finding.Evidence.Should().Contain(x => x.Label == "Furthest behind in WAL" && x.Value.EndsWith("GiB", StringComparison.Ordinal));
                finding.Evidence.Should().Contain(static x => x.Label.StartsWith("Replica", StringComparison.Ordinal) && x.Value.Contains("'walreceiver'", StringComparison.Ordinal),
                    "the standby is named to a visitor through the activity gate - every capability is on and no write policy is set");
            }

            // From the standby, as a role with no grant at all: received less replayed, and the pause state.
            await using (StudioHost onStandby = ReplicationServer.HostOver(pair.StandbyConnectionString(MatrixRole.Probe), TimeProvider.System))
            {
                HealthReportFinding finding = (await ReportAsync(onStandby)).Findings.Should().ContainSingle(static x => x.Code == "R03").Subject;

                finding.Finding.Variant.Should().Be("paused");
                finding.Severity.Should().Be(HealthSeverity.Warning);
                finding.Evidence.Should().Contain(new HealthEvidence("Replay", "paused"));
            }
        }
        finally
        {
            await ReplicationServer.Execute(standby, "select pg_catalog.pg_wal_replay_resume()");
            await pair.CaughtUpAsync();
        }
    }

    [PostgresTheory]
    [MemberData(nameof(Images))]
    public async Task From_the_primary_without_pg_read_all_stats_R03_is_not_checked_and_names_the_grant(string image)
    {
        ReplicationPair pair = await pairs.PairAsync(image);
        await using StudioHost host = pair.Primary.Host(MatrixRole.Probe, TimeProvider.System);

        HealthReport report = await ReportAsync(host);
        HealthCheckStatus status = report.Checks.Single(static x => x.Code == "R03");

        status.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        status.Problem.Should().StartWith("Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role probe.")
            .And.Contain("1 standby streams from this server")
            .And.Contain("multi-host data source");
        report.Findings.Should().NotContain(static x => x.Code == "R03");
    }

    [PostgresTheory]
    [MemberData(nameof(Images))]
    [HealthQuiet("R03")]
    public async Task A_standby_that_has_caught_up_is_quiet_from_either_side(string image)
    {
        ReplicationPair pair = await pairs.PairAsync(image);
        await ReplicationServer.Execute(pair.StandbyConnectionString(MatrixRole.Superuser), "select pg_catalog.pg_wal_replay_resume()");
        await pair.CaughtUpAsync();

        await using (StudioHost primary = pair.Primary.Host(MatrixRole.Stats, TimeProvider.System))
        {
            HealthReport report = await ReportAsync(primary);

            report.Findings.Should().NotContain(static x => x.Code == "R03");
            report.Checks.Single(static x => x.Code == "R03").Note.Should().StartWith("1 standby streams from this server, none of them behind");
        }

        await using (StudioHost onStandby = ReplicationServer.HostOver(pair.StandbyConnectionString(MatrixRole.Probe), TimeProvider.System))
        {
            HealthReport report = await ReportAsync(onStandby);

            report.Findings.Should().NotContain(static x => x.Code == "R03");
            report.Checks.Single(static x => x.Code == "R03").Note.Should().StartWith("This server is a standby, 0 B behind what it has received");
        }
    }

    [PostgresTheory]
    [MemberData(nameof(Images))]
    public async Task The_pages_facts_read_on_both_sides_without_a_failure_in_this_releases_shape(string image)
    {
        ReplicationPair pair = await pairs.PairAsync(image);
        bool eighteen = image == ReplicationServer.Image18;

        await using StudioHost primary = pair.Primary.Host(MatrixRole.Stats, TimeProvider.System);
        ReplicationFacts facts = await InCircuitAsync(primary, (services, resolved) => services.GetRequiredService<IReplicationFactsReader>().ReadAsync(resolved, Token));

        facts.Failures.Should().BeEmpty();
        facts.Role.Value!.InRecovery.Should().BeFalse();
        facts.Standbys.Value!.Total.Should().Be(1);
        facts.Standbys.Value.Rows.Should().ContainSingle().Which.Readable.Should().BeTrue("stats is a member of pg_read_all_stats");
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
        standby.Archiver.Value!.CurrentWal.Should().BeNull("no current file is named in recovery");

        // The view, names through the gate: the standby is named on the primary.
        ReplicationView view = await InCircuitAsync(primary, (services, _) => services.GetRequiredService<IReplicationService>().GetAsync(Scope, Token));
        view.NamesShown.Should().BeTrue();
        view.Standbys.Value!.Rows.Should().ContainSingle().Which.Name.Should().Be("walreceiver");
    }
}
