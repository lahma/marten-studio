using JasperFx.Events.Daemon;

using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// A sweep of R01, S01, S02, S03, K04 and C10 takes no lock beyond a catalog read and creates nothing - measured with H1's
/// <see cref="HealthSweepProbe" />, which reads <c>pg_locks</c> for the sweep's backend from a second connection while each
/// statement's transaction is still open. The store is configured so that C10's probe runs (HotCold, a session-scoped
/// lock): its two <c>pg_backend_pid()</c> transactions are in the measurement too.
/// </summary>
public class ServerSweepNoDdlLiveTests(PostgresFixture postgres)
{
    private static readonly Type[] ServerChecks =
    [
        typeof(ReplicationSlotCheck),
        typeof(PostgresEndOfLifeCheck),
        typeof(DangerousSettingsCheck),
        typeof(ChecksumFailuresCheck),
        typeof(PooledDaemonLockCheck),
        typeof(PooledDaemonLockProofCheck),
    ];

    [PostgresFact]
    public async Task A_sweep_of_the_server_and_replication_checks_creates_nothing_and_holds_no_lock_beyond_a_catalog_read()
    {
        const string Schema = "health_server_noddl";
        HealthSweepProbe probe = new(postgres);

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            Schema,
            store: static options =>
            {
                options.Projections.AsyncMode = DaemonMode.HotCold;
                options.Events.UseAdvisoryLockTransaction = false;
            },
            services: services => services
                .AddSingleton<IHealthStatementObserver>(probe)
                .AddSingleton(HealthCheckRegistry.ForTypes(ServerChecks)));

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, Schema, Schema + "_events");

        HealthReport report = await host.ReportAsync();

        report.Checks.Select(static x => x.Code).Should().BeEquivalentTo(["R01", "S01", "S02", "S03", "K04", "C10"]);
        report.Checks.Should().OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked, "every check ran");
        HealthLiveHost.Status(report, "C10").Statements.Should().HaveCount(2, "the probe ran, and its two transactions were measured");

        probe.StatementsObserved.Should().BeGreaterThanOrEqualTo(6, "so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads.Should().BeEmpty(
            "reading pg_replication_slots, pg_settings, pg_stat_database, pg_roles and pg_proc locks nothing it lists, and pg_backend_pid() nothing at all");

        (await HealthSweepProbe.SnapshotAsync(postgres, Schema, Schema + "_events")).Should().BeEquivalentTo(before, "a sweep never runs DDL");
    }
}
