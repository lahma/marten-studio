using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// A sweep of R02, R03 and R04 holds no lock beyond a catalog read and creates nothing - measured with
/// <see cref="HealthSweepProbe" />, which reads <c>pg_locks</c> for the sweep's backend from a second connection while each
/// statement's transaction is still open. (The page's facts read is the version matrix's: <c>MatrixReads</c>' "replication:
/// facts", on 14 to 19, behind an <c>ACCESS EXCLUSIVE</c> lock and against a whole-database fingerprint.)
/// </summary>
public class ReplicationSweepNoLockLiveTests(PostgresFixture postgres)
{
    private static readonly StudioScope Scope = new("default", "default", null);

    [PostgresFact]
    public async Task A_sweep_of_R02_R03_and_R04_creates_nothing_and_holds_no_lock_beyond_a_catalog_read()
    {
        const string Schema = "ps_repl_noddl";
        CancellationToken token = TestContext.Current.CancellationToken;

        await postgres.CreateSchemaAsync(Schema, token);
        HealthSweepProbe probe = new(postgres);

        await using StudioHost host = StudioHost.Create(
            services => services
                .AddSingleton(_ => NpgsqlDataSource.Create(postgres.ConnectionString))
                .AddSingleton<IHealthStatementObserver>(probe)
                .AddSingleton(HealthCheckRegistry.ForTypes([typeof(ArchiverFailingCheck), typeof(StandbyLagCheck), typeof(RequestedCheckpointsCheck)])),
            options => options.BrowsableSchemas.Add(Schema));

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, Schema);

        HealthReport report = await host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, token));

        report.Checks.Select(static x => x.Code).Should().BeEquivalentTo(["R02", "R03", "R04"]);
        report.Checks.Should().OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked, "every check ran");
        probe.StatementsObserved.Should().BeGreaterThanOrEqualTo(6, "so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads.Should().BeEmpty(
            "pg_stat_archiver, pg_stat_replication, pg_stat_checkpointer, pg_stat_wal and pg_settings lock nothing they list");

        (await HealthSweepProbe.SnapshotAsync(postgres, Schema)).Should().BeEquivalentTo(before, "a sweep never runs DDL");
    }
}
