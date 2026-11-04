using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Storage;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// 1.1-NP2 on real servers: C16 (a role's <c>idle_session_timeout</c> under a default pool), C17 (a data source with pooling off,
/// and session churn) and C18 (two data sources to one database) through the studio's own sweep. Each class of server is its
/// own container, since <c>ALTER ROLE</c> is the cluster's and a pool's identity is its host, port, database and login: a shared
/// matrix server would count the pools of other classes' hosts.
/// </summary>
public class ConnectionChecksLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>ALTER ROLE app SET idle_session_timeout = '60s'</c>: the studio's own connection starts with it, so the check reads it as
    /// the application's pool would meet it - and a default pool (300 s idle lifetime) outlasts it. The same role with
    /// <c>Keepalive=30</c>, which keeps an idle connection alive, is quiet.
    /// </summary>
    [PostgresFact]
    [HealthFires("C16")]
    [HealthQuiet("C16")]
    public async Task C16_fires_for_a_roles_idle_session_timeout_a_default_pool_outlasts_and_is_quiet_with_a_Keepalive()
    {
        MatrixServer server = await matrix.IsolatedServerAsync(17, "np2-c16");
        server.SkipIfUnavailable();

        await server.ExecuteAsync("alter role app set idle_session_timeout = '60s'", cancellationToken: Token);

        await using (MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App))
        {
            HealthCheckResult result = PostureHost.Result(await host.SweepAsync(HealthSweepKind.Regular), "C16");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
            finding.Variant.Should().Be("idle-session-timeout");
            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Evidence.Should().Contain(x => x.Label == "idle_session_timeout" && x.Value.StartsWith("60 s", StringComparison.Ordinal));
            finding.Remedy!.Code.Should().Contain("Keepalive=30").And.Contain("b.ConnectionStringBuilder.KeepAlive = 30;");
        }

        await using (MatrixHost kept = await MatrixHost.CreateAsync(server, MatrixRole.App, connection: static b => b.KeepAlive = 30))
        {
            HealthCheckResult result = PostureHost.Result(await kept.SweepAsync(HealthSweepKind.Regular), "C16");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Should().BeEmpty();
            result.Note.Should().Contain("before idle_session_timeout");
        }
    }

    /// <summary>
    /// A data source with <c>Pooling=false</c> and thirty sessions that come and go: C17 fires as a Warning because the database's
    /// sessions last milliseconds, and C16 is not applicable to a data source with no pool. The same host accepted with
    /// <c>AcceptedChecks</c> still runs C17 and marks its finding. With pooling on (the default) C17 is quiet.
    /// </summary>
    [PostgresFact]
    [HealthFires("C17")]
    [HealthQuiet("C17")]
    public async Task C17_fires_for_a_data_source_with_pooling_off_and_session_churn_and_is_quiet_with_a_pool()
    {
        MatrixServer server = await matrix.IsolatedServerAsync(17, "np2-pools");
        server.SkipIfUnavailable();

        await using (MatrixHost host = await MatrixHost.CreateAsync(
            server,
            MatrixRole.App,
            options: static o => o.AcceptedChecks["C17"] = "pooling is the sidecar's job here",
            connection: static b => b.Pooling = false))
        {
            NpgsqlDataSource source = host.Services.GetRequiredService<NpgsqlDataSource>();

            for (int i = 0; i < 30; i++)
            {
                await using NpgsqlConnection connection = await source.OpenConnectionAsync(Token);
                await using NpgsqlCommand command = new("select 1", connection);
                await command.ExecuteScalarAsync(Token);
            }

            await StorageLive.UntilAsync(
                async () => await StorageLive.ScalarAsync<long>(server.ConnectionString(MatrixRole.Superuser), "select sessions from pg_catalog.pg_stat_database where datname = current_database()") >= 30,
                TimeSpan.FromSeconds(30),
                "pg_stat_database to count the thirty sessions");

            HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);
            HealthCheckResult result = PostureHost.Result(sweep, "C17");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
            finding.Severity.Should().Be(HealthSeverity.Warning, "the database's sessions last milliseconds");
            finding.Evidence.Should().Contain(x => x.Label == "Average session");
            finding.Remedy!.Code.Should().Contain("Pooling=true");
            finding.Accepted.Should().BeTrue("AcceptedChecks still runs the check and marks its findings");
            finding.AcceptedReason.Should().Be("pooling is the sidecar's job here");

            HealthCheckResult c16 = PostureHost.Result(sweep, "C16");
            c16.Outcome.Should().Be(HealthCheckOutcome.NotApplicable);
            c16.Note.Should().Contain("Pooling is off");
        }

        await using MatrixHost pooled = await MatrixHost.CreateAsync(server, MatrixRole.App);
        HealthCheckResult quiet = PostureHost.Result(await pooled.SweepAsync(HealthSweepKind.Regular), "C17");

        quiet.Outcome.Should().Be(HealthCheckOutcome.Checked, quiet.Problem?.Reason);
        quiet.Findings.Should().BeEmpty();
        quiet.Note.Should().Contain("Pooling is on");
    }

    /// <summary>
    /// One pool of the process to the database is quiet; a second <c>NpgsqlDataSource</c> to the same host, port, database and login
    /// (its own <c>Application Name</c> makes it a pool of its own) makes two, and C18 says how many and what their ceilings add
    /// up to - without a name, a host or a connection string.
    /// </summary>
    [PostgresFact]
    [HealthFires("C18")]
    [HealthQuiet("C18")]
    public async Task C18_fires_for_two_data_sources_to_one_database_and_is_quiet_with_one()
    {
        MatrixServer server = await matrix.IsolatedServerAsync(17, "np2-pools");
        server.SkipIfUnavailable();

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App);

        HealthCheckResult alone = PostureHost.Result(await host.SweepAsync(HealthSweepKind.Regular), "C18");

        alone.Outcome.Should().Be(HealthCheckOutcome.Checked, alone.Problem?.Reason);
        alone.Findings.Should().BeEmpty();
        alone.Note.Should().Contain("Only one Npgsql pool");

        NpgsqlConnectionStringBuilder second = new(server.ConnectionString(MatrixRole.App)) { ApplicationName = "np2-second-pool" };
        await using NpgsqlDataSource other = NpgsqlDataSource.Create(second.ConnectionString);
        await using (NpgsqlConnection warm = await other.OpenConnectionAsync(Token))
        {
            await using NpgsqlCommand command = new("select 1", warm);
            await command.ExecuteScalarAsync(Token);
        }

        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(HealthSweepKind.Regular), "C18");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(x => x.Label == "Pools to this database as this login" && x.Value == "2");
        finding.Evidence.Should().Contain(x => x.Label == "Maximum Pool Size, added up" && x.Value == "200 connections at most");

        string everything = string.Join('\n', finding.Title, finding.WhyItMatters, finding.Caveat, finding.Remedy!.Code, string.Join('\n', finding.Evidence.Select(static x => x.Value)));
        everything.Should().NotContain("np2-second-pool").And.NotContain("ms-matrix-host-").And.NotContain("Host=").And.NotContain("Password");
    }

    /// <summary>
    /// Issue #56: a second pool built with <c>NpgsqlDataSourceBuilder.Name</c> reports to Npgsql's meter under that name, which is
    /// no connection string. With the data source registered in the container (a keyed registration, listed under a name of the
    /// studio's own) the studio identifies the pool by its connection string and C18 fires with two pools; with the data source
    /// the studio does not resolve the check stays quiet and says it did not judge the pool - not "nothing found".
    /// </summary>
    [PostgresFact]
    [HealthFires("C18")]
    [HealthQuiet("C18")]
    public async Task C18_counts_a_named_data_source_the_studio_resolves_and_says_it_did_not_judge_one_it_does_not()
    {
        MatrixServer server = await matrix.IsolatedServerAsync(17, "i56-named");
        server.SkipIfUnavailable();

        NpgsqlConnectionStringBuilder scheduler = new(server.ConnectionString(MatrixRole.App)) { ApplicationName = "i56-scheduler" };
        await using NpgsqlDataSource named = new NpgsqlDataSourceBuilder(scheduler.ConnectionString) { Name = "i56-scheduler-pool" }.Build();
        await using (NpgsqlConnection warm = await named.OpenConnectionAsync(Token))
        {
            await using NpgsqlCommand command = new("select 1", warm);
            await command.ExecuteScalarAsync(Token);
        }

        await using (MatrixHost unresolved = await MatrixHost.CreateAsync(server, MatrixRole.App))
        {
            HealthCheckResult result = PostureHost.Result(await unresolved.SweepAsync(HealthSweepKind.Regular), "C18");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Should().BeEmpty("the pool is the studio's to identify, and it cannot");
            result.Note.Should().Contain("Not judged for 1 named pool(s)").And.Contain("one pool of this process connects to this database as this login");
        }

        await using MatrixHost resolved = await MatrixHost.CreateAsync(
            server,
            MatrixRole.App,
            services: s => s.AddKeyedSingleton("scheduler", named),
            options: static o => o.AddDataSource("scheduler", static sp => sp.GetRequiredKeyedService<NpgsqlDataSource>("scheduler")));

        HealthCheckResult counted = PostureHost.Result(await resolved.SweepAsync(HealthSweepKind.Regular), "C18");

        counted.Outcome.Should().Be(HealthCheckOutcome.Checked, counted.Problem?.Reason);
        HealthFinding finding = counted.Findings.Should().ContainSingle().Subject;
        finding.Evidence.Should().Contain(x => x.Label == "Pools to this database as this login" && x.Value == "2");
        finding.Evidence.Should().Contain(x => x.Label == "Maximum Pool Size, added up" && x.Value == "200 connections at most");
        finding.Evidence.Should().NotContain(static x => x.Label.StartsWith("Named pools", StringComparison.Ordinal));

        string everything = string.Join('\n', finding.Title, finding.WhyItMatters, finding.Caveat, finding.Remedy!.Code, string.Join('\n', finding.Evidence.Select(static x => x.Label + x.Value)));
        everything.Should().NotContain("i56-scheduler").And.NotContain("Host=").And.NotContain("Password");
    }
}
