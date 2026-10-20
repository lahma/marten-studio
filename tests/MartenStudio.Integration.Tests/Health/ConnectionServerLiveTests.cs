using PostgresStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// C02, C03 and C07 on a Postgres server of this class's own (<see cref="ConnectionsServerFixture" />): a
/// <c>max_connections</c> of 25 that a test can fill without starving every other test of the shared one, and
/// prepared transactions switched on, which the shared container - Postgres' defaults - does not have.
/// </summary>
public class ConnectionServerLiveTests(ConnectionsServerFixture server) : IClassFixture<ConnectionsServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    // ----------------------------------------------------------------------------------------------
    // C03
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("C03")]
    public async Task A_server_whose_connections_are_nearly_taken_is_a_C03_finding()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_c03_fires");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        // 22 of 25 are an ordinary role's (3 are the superuser's): 19 held here, and the sweep's own, is more than 80%.
        List<NpgsqlConnection> held = [];

        try
        {
            for (int i = 0; i < 19; i++)
            {
                NpgsqlConnection connection = new(Unpooled(server.ConnectionString));
                await connection.OpenAsync(Token);
                held.Add(connection);
            }

            HealthReport report = await host.ReportAsync();

            HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C03").Subject;
            finding.Severity.Should().BeOneOf(HealthSeverity.Warning, HealthSeverity.Critical);
            finding.Evidence.Single(static x => x.Label == "Available to ordinary roles").Value.Should().StartWith("22 (max_connections 25");
            finding.Finding.Caveat.Should().Contain("every database included");
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
    [HealthQuiet("C03")]
    public async Task A_server_with_room_is_quiet()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_c03_quiet");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "C03");
        ConnectionsLiveHost.Status(report, "C03").Note.Should().Contain("of 22 connections");
    }

    // ----------------------------------------------------------------------------------------------
    // C02
    // ----------------------------------------------------------------------------------------------

    private async Task<ConnectionsLiveHost> TwoDatabasesAsync(string name, int maxPoolSize)
    {
        string a = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, name + "_a");
        string b = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, name + "_b");

        string poolA = new NpgsqlConnectionStringBuilder(a) { MaxPoolSize = maxPoolSize }.ConnectionString;
        string poolB = new NpgsqlConnectionStringBuilder(b) { MaxPoolSize = maxPoolSize }.ConnectionString;

        return await ConnectionsLiveHost.CreateAsync(
            poolA,
            sample: false,
            store: options =>
            {
                options.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddSingleTenantDatabase(poolA, "alpha");
                    tenancy.AddSingleTenantDatabase(poolB, "beta");
                });

                options.DatabaseSchemaName = "budget";
            },
            studio: static options => options.DiscoverTenantIds = false);
    }

    [PostgresFact]
    [HealthFires("C02")]
    public async Task A_store_whose_pools_together_exceed_the_server_is_a_C02_warning()
    {
        await using ConnectionsLiveHost host = await TwoDatabasesAsync("health_c02_fires", 15);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C02").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Max Pool Size, summed", "30"));
        finding.Evidence.Should().Contain(new HealthEvidence("Databases on this server", "2"));
    }

    [PostgresFact]
    [HealthQuiet("C02")]
    public async Task A_store_whose_pools_fit_the_server_is_quiet()
    {
        await using ConnectionsLiveHost host = await TwoDatabasesAsync("health_c02_quiet", 5);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "C02");
        ConnectionsLiveHost.Status(report, "C02").Note.Should().Contain("at most 10 connections");
    }

    // ----------------------------------------------------------------------------------------------
    // C07
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("C07")]
    public async Task An_orphaned_prepared_transaction_is_critical_five_minutes_on_with_its_gid_behind_the_gate()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_c07_fires");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        const string Gid = "h3-orphan-c07";

        await using (NpgsqlConnection preparer = new(Unpooled(database)))
        {
            await preparer.OpenAsync(Token);

            // One statement per command: PREPARE TRANSACTION needs a real transaction block, which a batch's
            // implicit one is not.
            foreach (string statement in new[]
            {
                "create table if not exists h3_two_phase (id int)",
                "begin",
                "insert into h3_two_phase values (1)",
                "prepare transaction '" + Gid + "'",
            })
            {
                await using NpgsqlCommand command = new(statement, preparer);
                await command.ExecuteNonQueryAsync(Token);
            }
        }

        try
        {
            // The session that prepared it is gone, and the transaction is still there: that is what orphaned means.
            PreparedTransactionCheck check = new();
            PreparedTransactionFacts facts = await host.GatherAsync(check);

            facts.MaxPrepared.Should().Be(ConnectionsServerFixture.MaxPreparedTransactions);
            facts.Prepared.Should().ContainSingle(static x => x.Gid == Gid);

            check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty("it is seconds old");

            HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddMinutes(5))).Findings.Should().ContainSingle().Subject;
            finding.Severity.Should().Be(HealthSeverity.Critical);
            finding.Evidence.Single(static x => x.Label == "Prepared transaction 1 (oldest)").Should().Match<HealthEvidence>(
                x => x.Gate == HealthGate.Activity && x.Value.Contains(Gid, StringComparison.Ordinal));

            // The page shows it now, the gid only through the gate.
            await using AsyncServiceScope circuit = host.Circuit();
            ISessionActivityService service = circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

            (await service.GetSummaryAsync(host.Scope, Token)).PreparedCount.Should().Be(1);
            (await service.GetDetailsAsync(host.Scope, Token)).Prepared.Should().ContainSingle(static x => x.Gid == Gid);
        }
        finally
        {
            await using NpgsqlConnection cleanup = new(Unpooled(database));
            await cleanup.OpenAsync(CancellationToken.None);
            await using NpgsqlCommand rollback = new("rollback prepared '" + Gid + "'", cleanup);
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [PostgresFact]
    [HealthQuiet("C07")]
    public async Task A_server_that_allows_prepared_transactions_and_has_none_is_quiet()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_c07_quiet");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "C07");
        ConnectionsLiveHost.Status(report, "C07").Note.Should().Be("No prepared transaction is waiting in this database.");
    }

    // ----------------------------------------------------------------------------------------------
    // B3: a role that may not read into other roles' sessions still counts them
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Without <c>pg_read_all_stats</c>, Postgres hands a role every other role's row of <c>pg_stat_activity</c> with
    /// its <c>backend_type</c> null - so a filter on <c>client backend</c> alone counted nobody there. On this class's
    /// own server, where nothing else connects: a narrowed <c>SqlConsoleRole</c> reads while another role holds two
    /// sessions, and C03 must count what a superuser counts, and Database › Sessions must count two sessions it cannot
    /// see into and say so.
    /// </summary>
    [PostgresFact]
    public async Task A_narrowed_SqlConsoleRole_counts_the_sessions_it_cannot_see_into_and_says_so()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "health_sessions_narrowed");

        // The role the studio reads as, with no pg_read_all_stats, and somebody else's role.
        await using (NpgsqlConnection admin = new(Unpooled(server.ConnectionString)))
        {
            await admin.OpenAsync(Token);
            await using NpgsqlCommand roles = new(
                "do $$ begin"
                + " if not exists (select from pg_roles where rolname = 'h3_narrow') then create role h3_narrow nologin; end if;"
                + " if not exists (select from pg_roles where rolname = 'h3_other') then create role h3_other login password 'h3_other'; end if;"
                + " end $$",
                admin);
            await roles.ExecuteNonQueryAsync(Token);
        }

        string asOther = new NpgsqlConnectionStringBuilder(Unpooled(database)) { Username = "h3_other", Password = "h3_other" }.ConnectionString;
        await using NpgsqlConnection first = new(asOther);
        await using NpgsqlConnection second = new(asOther);
        await first.OpenAsync(Token);
        await second.OpenAsync(Token);

        // Unpooled, so that no idle connection of the studio's own sits in the database between two reads.
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            Unpooled(database),
            studio: static options => options.SqlConsoleRole = "h3_narrow");

        // A superuser's count of the server, and the narrowed role's, with its own reading connection in place of the
        // one that measured: equal once nothing is opening, and never while hidden rows are dropped (1 against 3).
        ServerConnectionLimits? narrowed = null;
        long whole = 0;

        try
        {
            await LiveWait.UntilAsync(
                async () =>
                {
                    whole = await LiveWait.ScalarAsync<long>(server.ConnectionString, "select count(*) from pg_stat_activity where backend_type = 'client backend'");
                    narrowed = await host.GatherAsync(new ServerConnectionHeadroomCheck());
                    return narrowed.ClientBackends == whole;
                },
                TimeSpan.FromSeconds(20),
                "the narrowed role's server count to equal the superuser's");
        }
        catch (TimeoutException)
        {
            // The assertion below says by how much, and against what.
        }

        string backends = await LiveWait.ScalarAsync<string>(
            server.ConnectionString,
            "select string_agg(coalesce(backend_type, '?') || ' role ' || coalesce(usesysid::text, '-') || ' db ' || coalesce(datid::text, '-'), '; ')"
            + " from pg_stat_activity") ?? string.Empty;

        narrowed!.ClientBackends.Should().Be(whole, "every backend, as a superuser sees it: {0}", backends);
        narrowed.ThisDatabase.Should().Be(3, "the other role's two sessions and the studio's own reading one");

        await using AsyncServiceScope circuit = host.Circuit();
        SessionActivitySummary summary = await circuit.ServiceProvider.GetRequiredService<ISessionActivityService>().GetSummaryAsync(host.Scope, Token);

        summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
        summary.SessionCount.Should().Be(2, "two sessions this role may not read into are still two sessions");
        summary.Sessions.Should().HaveCount(2).And.OnlyContain(static x => x.State == null, "counted, never judged");
        summary.Caveats.Should().Contain(static x => x.Contains("cannot see into 2 sessions", StringComparison.Ordinal),
            "the page says it cannot see, never that there is nothing");
    }
}
