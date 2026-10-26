using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// Issue #8 A2 on VERS's Aurora shim: on a server the platform detector calls Aurora, R02, R03 and R04 are listed as not
/// applicable - never run - and the Replication page's checkpoint, WAL, archiver, tablespace and standby sections say so,
/// while the slots and <c>pg_stat_io</c> are read.
/// </summary>
public class AuroraReplicationLiveTests(PostgresVersionMatrix matrix)
{
    private const string Database = "aurora_r2";

    [PostgresFact]
    public async Task On_Aurora_R02_R03_and_R04_are_not_applicable_and_never_run()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        await AuroraShim.ApplyAsync(server, Database, TestContext.Current.CancellationToken);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, Database);

        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);

        foreach (string code in new[] { "R02", "R03", "R04" })
        {
            HealthCheckResult result = sweep.Results.Single(x => x.Check.Code == code);

            result.Outcome.Should().Be(HealthCheckOutcome.NotApplicable, code + " judges what Aurora's storage layer does");
            result.Note.Should().Be(ReplicationPlatform.Aurora);
            result.Findings.Should().BeEmpty();
            result.Statements.Should().OnlyContain(static x => !x.Sql.Contains("pg_stat_archiver", StringComparison.Ordinal)
                && !x.Sql.Contains("pg_stat_replication", StringComparison.Ordinal)
                && !x.Sql.Contains("pg_stat_checkpointer", StringComparison.Ordinal),
                "a not-applicable check runs no statement of its own - only the sweep's shared platform read");
        }

        sweep.Results.Single(static x => x.Check.Code == "R01").Outcome.Should().Be(HealthCheckOutcome.Checked, "Aurora has logical slots");
    }

    [PostgresFact]
    public async Task On_Aurora_the_pages_sections_its_storage_layer_does_say_so()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        await AuroraShim.ApplyAsync(server, Database + "_page", TestContext.Current.CancellationToken);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, Database + "_page");

        ReplicationFacts facts = await host.Services.GetRequiredService<IReplicationFactsReader>().ReadAsync(host.Resolved, TestContext.Current.CancellationToken);

        facts.Server.Platform.Kind.Should().Be(PostgresPlatformKind.AmazonAurora);
        facts.Failures.Should().BeEmpty();

        foreach (ReplicationReadOutcome outcome in new[] { facts.Standbys.Outcome, facts.Archiver.Outcome, facts.Wal.Outcome, facts.Checkpoints.Outcome, facts.Tablespaces.Outcome })
        {
            outcome.Should().Be(ReplicationReadOutcome.NotApplicable);
        }

        facts.Checkpoints.Note.Should().Be("Not applicable on Aurora: Aurora's storage layer does this, and its readers do not stream.");
        facts.Slots.Outcome.Should().Be(ReplicationReadOutcome.Read);
        facts.Io.Outcome.Should().Be(ReplicationReadOutcome.Read);
        facts.Statements.Should().NotContain(static x => x.Sql.Contains("pg_stat_archiver", StringComparison.Ordinal), "a not-applicable section is never read");

        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        ReplicationView view = await circuit.ServiceProvider.GetRequiredService<IReplicationService>()
            .GetAsync(new StudioScope("default", "default", null), TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(ReplicationRefusal.None);
        view.Archiver.Note.Should().Be(ReplicationPlatform.Aurora);
        view.Standbys.Note.Should().Be(ReplicationPlatform.Aurora);
    }
}
