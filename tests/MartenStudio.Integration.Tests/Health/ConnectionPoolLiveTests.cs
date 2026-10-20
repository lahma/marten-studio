using Marten;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// C01 against a real Npgsql pool (acceptance 1 and 2): it fires when the store's own pool is used up, it is
/// quiet on the healthy sample store, the listener records nothing per command, and it is gone once the sweep
/// that leased it is over.
/// </summary>
public class ConnectionPoolLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("C01")]
    public async Task A_pool_lent_out_to_its_ceiling_is_a_warning_and_on_two_consecutive_checks_critical()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c01_fires");

        // A small pool, and a short wait for it: the sweep's own catalog checks are refused a connection while the
        // test holds every one, and should say so quickly rather than wait out Npgsql's default fifteen seconds.
        string small = new NpgsqlConnectionStringBuilder(database) { MaxPoolSize = 4, Timeout = 2 }.ConnectionString;
        ManualClock clock = new(DateTimeOffset.UtcNow);

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(small, clock: clock);

        List<NpgsqlConnection> held = [];

        try
        {
            for (int i = 0; i < 4; i++)
            {
                NpgsqlConnection connection = host.Store.Storage.Database.CreateConnection();
                await connection.OpenAsync(Token);
                held.Add(connection);
            }

            HealthReport first = await host.ReportAsync();

            HealthReportFinding warning = first.Findings.Should().ContainSingle(static x => x.Code == "C01").Subject;
            warning.Severity.Should().Be(HealthSeverity.Warning, "one check that finds the pool full is a moment");
            warning.Evidence.Should().Contain(new HealthEvidence("In use", "4 of 4 (100%)"));

            clock.Advance(host.Provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value.EffectiveHealthInterval + TimeSpan.FromSeconds(1));

            HealthReport second = await host.ReportAsync();

            HealthReportFinding critical = second.Findings.Should().ContainSingle(static x => x.Code == "C01").Subject;
            critical.Severity.Should().Be(HealthSeverity.Critical, "two consecutive checks found every connection lent out");
            critical.Evidence.Should().Contain(new HealthEvidence("Fully used", "2 consecutive samples"));
        }
        finally
        {
            foreach (NpgsqlConnection connection in held)
            {
                await connection.DisposeAsync();
            }
        }

        host.Provider.GetRequiredService<SessionPoolObserver>().IsListening
            .Should().BeFalse("the listener lives only while a sweep or a page holds a lease");
    }

    /// <summary>
    /// Follow-up 4: an <c>NpgsqlConnection</c> opened with Marten's own connection string is pooled by Npgsql in a
    /// pool of its own - under the same <c>pool.name</c>. Three held there and none in Marten's are two pools telling
    /// different stories under one name: C01 says it cannot tell which is Marten's, and Database › Sessions shows no
    /// pool, rather than either reporting the host's three as Marten's.
    /// </summary>
    [PostgresFact]
    public async Task Two_pools_under_Martens_connection_string_that_disagree_are_not_judged()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c01_shared_name");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        // Marten's own pool, made to exist; and the host's, through a plain NpgsqlConnection on the same string.
        await using (NpgsqlConnection touched = host.Store.Storage.Database.CreateConnection())
        {
            await touched.OpenAsync(Token);
        }

        List<NpgsqlConnection> held = [];

        try
        {
            for (int i = 0; i < 3; i++)
            {
                NpgsqlConnection connection = new(host.ConnectionString);
                await connection.OpenAsync(Token);
                held.Add(connection);
            }

            HealthReport report = await host.ReportAsync();

            report.Findings.Should().NotContain(static x => x.Code == "C01");
            HealthCheckStatus status = ConnectionsLiveHost.Status(report, "C01");
            status.Outcome.Should().Be(HealthCheckOutcome.Unavailable, "which pool is Marten's cannot be told");
            status.Problem.Should().Contain("Their numbers differ");

            await using AsyncServiceScope circuit = host.Circuit();
            SessionPoolObserver observer = host.Provider.GetRequiredService<SessionPoolObserver>();
            using SessionPoolLease lease = observer.Lease();

            SessionActivitySummary summary = await circuit.ServiceProvider.GetRequiredService<ISessionActivityService>().GetSummaryAsync(host.Scope, Token);
            summary.Pool.Should().NotBeNull();
            summary.Pool!.Ambiguous.Should().BeTrue();
            summary.Pool.Used.Should().BeNull("the host's three are not Marten's three");
        }
        finally
        {
            foreach (NpgsqlConnection connection in held)
            {
                await connection.DisposeAsync();
            }
        }
    }

    [PostgresFact]
    [HealthQuiet("C01")]
    public async Task The_healthy_sample_stores_pool_is_quiet()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c01_quiet");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "C01");
        HealthCheckStatus status = ConnectionsLiveHost.Status(report, "C01");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("of 100 connections", "the pool was found by its connection string and read");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>Acceptance 2, live: commands through the host's pool deliver nothing to the listener.</summary>
    [PostgresFact]
    public async Task Commands_through_a_watched_pool_record_nothing_until_the_studio_reads()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c01_per_command");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        SessionPoolObserver observer = host.Provider.GetRequiredService<SessionPoolObserver>();
        using SessionPoolLease lease = observer.Lease();

        long before = observer.MeasurementsReceived;

        for (int i = 0; i < 50; i++)
        {
            await using IQuerySession session = host.Store.QuerySession();
            await session.Query<Customer>().Take(1).ToListAsync(Token);
        }

        observer.MeasurementsReceived.Should().Be(before, "no synchronous instrument is enabled, so a command records nothing here");

        IReadOnlyList<NpgsqlPoolReading> pools = lease.Read();

        observer.MeasurementsReceived.Should().BeGreaterThan(before, "a reading is the only thing that produces measurements");
        pools.Should().Contain(x => DatabasePoolSettings.IsPoolOf(MartenStudio.Services.MartenStudioDatabase.Of(host.Store.Storage.Database), x.PoolName));
    }
}
