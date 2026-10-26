using Npgsql;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using Testcontainers.PostgreSql;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// W5-V1's two autovacuum checks on a Marten store: T24 on a document table whose autovacuum a DBA switched off, with the
/// <c>StoreOptions</c> line that declares it back on, quiet once it is reset; and T16 on a server of its own whose one
/// autovacuum worker two slow tables hold for three sweeps. <c>PostgresStudio.Integration.Tests</c>' <c>Maintenance</c>
/// folder proves both on a plain database, with the TOAST case and the unprivileged count.
/// </summary>
public class AutovacuumLiveTests(PostgresFixture postgres, AutovacuumWorkersServerFixture server) : IClassFixture<AutovacuumWorkersServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("T24")]
    public async Task A_document_table_whose_autovacuum_is_switched_off_is_T24_with_its_StoreOptions_line_until_it_is_reset()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_t24_fires", clock, store: static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthScanThing>(host.Store);
        string key = KeyOf<HealthScanThing>(host.Store);
        await ExecuteAsync(postgres, "alter table " + table + " set (autovacuum_enabled = false)");

        HealthReportFinding finding = Of(await ReportAsync(host), "T24").Should().ContainSingle(x => x.Finding.Subject == key).Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().Be("Autovacuum is switched off for " + key);
        finding.Finding.DocumentTypes.Should().NotBeEmpty("a document table is named by its type");
        finding.Finding.Remedy!.Code.Should().Contain("HealthScanThing>().StorageParameter(\"autovacuum_enabled\", true);");

        await ExecuteAsync(postgres, "alter table " + table + " reset (autovacuum_enabled)");
        clock.Advance(TimeSpan.FromMinutes(2));

        HealthReport quiet = await ReportAsync(host);
        Of(quiet, "T24").Should().NotContain(x => x.Finding.Subject == key);
        HealthLiveHost.Status(quiet, "T24").Outcome.Should().Be(HealthCheckOutcome.Checked);
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>
    /// <c>autovacuum_max_workers = 1</c>, two tables that vacuum at a crawl (<c>autovacuum_vacuum_cost_limit = 1</c>,
    /// <c>autovacuum_vacuum_cost_delay = 100</c>, their own storage parameters), and three sweeps while the one worker is
    /// on them: every worker busy for three samples running is T16, on PostgreSQL 17 a restart to change.
    /// </summary>
    [PostgresFact]
    [HealthFires("T16")]
    public async Task One_worker_held_by_slow_tables_for_three_sweeps_is_T16()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "av_t16");

        await RunAsync(
            database,
            "create table slow_a (id int, pad text) with (autovacuum_vacuum_cost_limit = 1, autovacuum_vacuum_cost_delay = 100)",
            "create table slow_b (id int, pad text) with (autovacuum_vacuum_cost_limit = 1, autovacuum_vacuum_cost_delay = 100)",
            "insert into slow_a select g, pg_catalog.repeat('x', 200) from generate_series(1, 10000) g",
            "insert into slow_b select g, pg_catalog.repeat('x', 200) from generate_series(1, 10000) g",
            "analyze slow_a",
            "analyze slow_b");
        await RunAsync(database, "delete from slow_a where id % 2 = 0", "delete from slow_b where id % 2 = 0");

        await LiveWait.UntilAsync(
            async () => await LiveWait.ScalarAsync<long>(
                database,
                "select pg_catalog.count(*) from pg_catalog.pg_stat_progress_vacuum v join pg_catalog.pg_class c on c.oid = v.relid "
                + "where c.relname in ('slow_a', 'slow_b')") > 0,
            TimeSpan.FromSeconds(60),
            "the one autovacuum worker to start on a slow table");

        ManualClock clock = new(Start);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock, sample: false);

        HealthReport? report = null;

        for (int sample = 1; sample <= AutovacuumWorkersCheck.SamplesNeeded; sample++)
        {
            report = await host.ReportAsync();
            clock.Advance(TimeSpan.FromMinutes(2));
        }

        HealthReportFinding finding = report!.Findings.Should().ContainSingle(static x => x.Code == "T16").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().Be("Every one of the 1 autovacuum workers has been busy for 3 samples running");
        finding.Evidence.Should().Contain(new HealthEvidence("autovacuum_max_workers", "1 (changes only at a restart)"), "PostgreSQL 17 restarts for it");
        finding.Finding.Remedy!.Code.Should().Contain("autovacuum_max_workers");
    }

    private static async Task RunAsync(string connectionString, params string[] statements)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }

        await FlushStatisticsAsync(connection);
    }
}

/// <summary>
/// A server of T16's own: one autovacuum worker, woken every second. Never reused; its settings are this fixture's alone.
/// </summary>
public sealed class AutovacuumWorkersServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "autovacuum_max_workers=1")
        .WithCommand("-c", "autovacuum_naptime=1")
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>The server's superuser connection string.</summary>
    public string ConnectionString => started
        ? container.GetConnectionString()
        : throw new InvalidOperationException("The autovacuum server is not running; a test that needs it must be a [PostgresFact].");

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
