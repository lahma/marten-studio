using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// A PostgreSQL server of this class's own, with <c>max_connections</c> at 20 - so a test can use most of what ordinary roles
/// may open (17: three are the superuser's) without starving every other test of the shared server - and three login roles
/// that are not superusers. Never reused: its settings are this fixture's alone (AGENTS.md, <i>Testing</i> item 7).
/// </summary>
public sealed class HoldersServerFixture : IAsyncLifetime
{
    /// <summary><c>max_connections</c>.</summary>
    public const int MaxConnections = 20;

    /// <summary>What an ordinary role may open: <c>max_connections</c> less <c>superuser_reserved_connections</c>'s three.</summary>
    public const int Available = 17;

    /// <summary>The password every role has.</summary>
    public const string Password = "holders";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=" + MaxConnections.ToString(CultureInfo.InvariantCulture))
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>The server's superuser connection string, unpooled, with an application name so it is never an unnamed connection.</summary>
    public string ConnectionString => Connect("postgres", "postgres", application: "ms-holders-admin");

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;

        foreach (string role in new[] { "svc_orders", "svc_billing", "svc_reader" })
        {
            await ExecuteAsync("create role " + role + " login password '" + Password + "'");
        }
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    /// <summary>
    /// A connection string to <paramref name="database" /> as <paramref name="role" /> - the superuser when it is
    /// <c>postgres</c> - unpooled, so a closed connection is gone, with <paramref name="application" /> as its application name
    /// (none when <see langword="null" />).
    /// </summary>
    public string Connect(string database, string role, string? password = null, string? application = null)
    {
        if (!started)
        {
            throw new InvalidOperationException("The holders server is not running; a test that needs it must be a [PostgresFact].");
        }

        NpgsqlConnectionStringBuilder superuser = new(container.GetConnectionString());

        return new NpgsqlConnectionStringBuilder
        {
            Host = superuser.Host,
            Port = superuser.Port,
            Database = database,
            Username = role,
            Password = role == "postgres" ? superuser.Password : password ?? Password,
            Pooling = false,
            ApplicationName = application,
            Timeout = 30,
        }.ConnectionString;
    }

    /// <summary>Runs a statement as the superuser, on an unpooled connection of its own.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One scalar, as the superuser.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T) (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>
/// Sessions › Holders and C13 and C14 on a real PostgreSQL 17 (issue #20), against the server's connections as two databases hold
/// them: connections of distinct <c>Application Name</c>s and roles in both, a studio on the first database only, and a server
/// whose ordinary roles may open 17.
/// </summary>
/// <remarks>
/// <para>
/// <b>The studio's own connections are never held.</b> Its data source is unpooled, so every read opens a connection and closes it
/// and nothing of the studio's is idle on the server between reads; the read that makes a snapshot leaves its own session out. So
/// what the tests count is what they hold, exactly - and the held ones are waited for on the server's own word (their state in
/// <c>pg_stat_activity</c>), never on the clock.
/// </para>
/// <para>
/// Nothing is shared with another class: the server is this class's (<see cref="HoldersServerFixture" />), its databases are created
/// by each test and dropped at its end, and a test begins by waiting for the last test's connections to have gone.
/// </para>
/// </remarks>
public class ConnectionHoldersLiveTests(HoldersServerFixture server) : IClassFixture<HoldersServerFixture>
{
    private const string Orders = "orders-api";
    private const string Billing = "billing-api";
    private const string Reports = "reports";

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The databases of one test and the connections it holds, ended when it ends.</summary>
    private sealed class Held : IAsyncDisposable
    {
        private readonly HoldersServerFixture fixture;
        private readonly List<NpgsqlConnection> connections = [];

        private Held(HoldersServerFixture fixture, string a, string b)
        {
            this.fixture = fixture;
            A = a;
            B = b;
        }

        /// <summary>The first database, which the studio is on.</summary>
        public string A { get; }

        /// <summary>The second, which no source registers unless a test says so.</summary>
        public string B { get; }

        /// <summary>Waits for the last test's connections to be gone, then creates the two databases.</summary>
        public static async Task<Held> CreateAsync(HoldersServerFixture server, string name)
        {
            await UntilAsync(
                async () => await server.ScalarAsync<long>(
                    "select count(*) from pg_catalog.pg_stat_activity where backend_type = 'client backend' and pid <> pg_catalog.pg_backend_pid()") == 0,
                "the last test's connections to be gone");

            Held held = new(server, name + "_a", name + "_b");

            await server.ExecuteAsync("create database " + held.A);
            await server.ExecuteAsync("create database " + held.B);
            return held;
        }

        /// <summary>Opens <paramref name="count" /> connections to <paramref name="database" /> as <paramref name="role" />, idle.</summary>
        public async Task IdleAsync(string database, string role, string? application, int count)
        {
            for (int i = 0; i < count; i++)
            {
                NpgsqlConnection connection = new(fixture.Connect(database, role, application: application));
                await connection.OpenAsync(Token);
                connections.Add(connection);
            }
        }

        /// <summary>Opens <paramref name="count" /> connections, each inside a transaction it has begun and left.</summary>
        public async Task IdleInTransactionAsync(string database, string role, string? application, int count)
        {
            for (int i = 0; i < count; i++)
            {
                NpgsqlConnection connection = new(fixture.Connect(database, role, application: application));
                await connection.OpenAsync(Token);
                connections.Add(connection);

                await using NpgsqlCommand begin = new("begin; select 1", connection);
                await begin.ExecuteNonQueryAsync(Token);
            }
        }

        /// <summary>Waits until the server says <paramref name="count" /> sessions of <paramref name="state" /> are held.</summary>
        public Task UntilHeldAsync(string state, long count) => UntilAsync(
            async () => await fixture.ScalarAsync<long>(
                "select count(*) from pg_catalog.pg_stat_activity where backend_type = 'client backend' and state = @state and datname = any(@databases)",
                ("state", state),
                ("databases", new[] { A, B })) == count,
            count + " sessions " + state);

        /// <summary>Closes every connection held so far, and waits until the server says none is left.</summary>
        public async Task CloseAsync()
        {
            foreach (NpgsqlConnection connection in connections)
            {
                await connection.DisposeAsync();
            }

            connections.Clear();

            await UntilAsync(
                async () => await fixture.ScalarAsync<long>(
                    "select count(*) from pg_catalog.pg_stat_activity where backend_type = 'client backend' and datname = any(@databases)",
                    ("databases", new[] { A, B })) == 0,
                "the held connections to be gone");
        }

        public async ValueTask DisposeAsync()
        {
            foreach (NpgsqlConnection connection in connections)
            {
                await connection.DisposeAsync();
            }

            await fixture.ExecuteAsync("drop database if exists " + A + " with (force)");
            await fixture.ExecuteAsync("drop database if exists " + B + " with (force)");
        }
    }

    /// <summary>Asks <paramref name="condition" /> every 50 ms until it is true, or fails naming <paramref name="what" />.</summary>
    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        while (!await condition())
        {
            try
            {
                await timer.WaitForNextTickAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            {
                throw new TimeoutException("Gave up after 60 s waiting for " + what + ".");
            }
        }
    }

    /// <summary>
    /// A studio on <paramref name="held" />'s first database as <paramref name="role" /> - unpooled, named, so it holds nothing between
    /// reads and is never an unnamed connection - with the second registered as source <c>b</c> when <paramref name="registerB" />.
    /// </summary>
    private StudioHost Studio(Held held, string role = "postgres", bool registerB = false, bool capabilities = true)
    {
        string a = server.Connect(held.A, role, application: "holders-studio");
        string b = server.Connect(held.B, role, application: "holders-studio");

        return StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(a)),
            options =>
            {
                if (capabilities)
                {
                    options.Capabilities = PostgresStudioCapabilities.All();
                }

                if (registerB)
                {
                    options.AddDataSource("b", _ => NpgsqlDataSource.Create(b));
                }
            },
            sourcePolicy: static (resource, user) => resource.SourceName != "b" || user.Identity?.Name == "friend",
            writePolicy: static (_, user) => user.Identity?.Name == "admin");
    }

    private static Task<HealthReport> ReportAsync(StudioHost host, string visitor) =>
        host.CircuitAsync(visitor, services => services.GetRequiredService<IHealthService>()
            .GetReportAsync(Scope, HealthReportRequest.Regular, Token));

    // ----------------------------------------------------------------------------------------------
    // Database › Sessions › Holders
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_servers_connections_are_grouped_by_database_application_role_and_address_and_named_by_the_policy()
    {
        await using Held held = await Held.CreateAsync(server, "holders_view");
        await held.IdleAsync(held.A, "svc_orders", Orders, 3);
        await held.IdleAsync(held.A, "svc_orders", application: null, 2);
        await held.IdleAsync(held.B, "svc_orders", Orders, 3);
        await held.IdleAsync(held.B, "svc_billing", Billing, 5);
        await held.IdleAsync(held.B, "svc_billing", Reports, 2);
        await held.UntilHeldAsync("idle", 15);

        await using StudioHost host = Studio(held, registerB: true);

        // A visitor the source policy does not admit to the second database, and who is short of ViewDatabaseActivity: this
        // database by name, the other's sessions together and unnamed, and no application, role or address anywhere.
        await using (StudioCircuit viewer = host.Circuit("viewer"))
        {
            SessionActivitySummary summary = await viewer.Get<ISessionActivityService>().GetSummaryAsync(Scope, Token);

            summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
            summary.Details.IsOpen.Should().BeFalse();
            SessionHoldersSummary holders = summary.Holders!;

            holders.Databases.Select(x => x.Database).Should().Equal(held.A);
            holders.Databases.Single().ThisDatabase.Should().BeTrue();
            holders.Databases.Single().Idle.Should().Be(5);
            holders.Others.Should().NotBeNull();
            holders.Others!.Database.Should().BeNull();
            holders.Others.Total.Should().Be(10, "the second database's sessions, counted and not named");
            holders.Others.Idle.Should().Be(10);
            holders.Others.OldestIdle.Should().NotBeNull();
            holders.ServerConnections.Should().Be(16, "fifteen held and the read's own session");
            holders.Available.Should().Be(HoldersServerFixture.Available);
            holders.AllSessionsVisible.Should().BeTrue();

            System.Text.Json.JsonSerializer.Serialize(summary).Should().NotContain(held.B).And.NotContain(Billing).And.NotContain("svc_billing");
        }

        // A visitor the policy admits to it by name still sees no application, role or address.
        await using (StudioCircuit friend = host.Circuit("friend"))
        {
            SessionHoldersSummary holders = (await friend.Get<ISessionActivityService>().GetSummaryAsync(Scope, Token)).Holders!;

            holders.Databases.Select(x => x.Database).Should().Equal(held.B, held.A);
            holders.Databases[0].Total.Should().Be(10);
            holders.Others.Should().BeNull("nothing is counted unnamed");
        }

        host.Calls.All.Should().Contain(x => x.Policy == StudioHost.SourcePolicy && x.Resource.SourceName == "b" && x.Resource.Capability == null && !x.Allowed,
            "the source policy is asked about the registered database before its name is shown, and the viewer was refused");

        // Past the gate: every group, by database, application, role and address.
        await using (StudioCircuit admin = host.Circuit("admin"))
        {
            SessionActivityDetails details = await admin.Get<ISessionActivityService>().GetDetailsAsync(Scope, Token);

            details.Refusal.Should().Be(DatabaseRefusal.None, details.Reason);

            SessionHolderGroup Find(string database, string application, string role) =>
                details.Holders.Groups.Should().ContainSingle(x => x.Database == database && x.Application == application && x.Role == role).Subject;

            SessionHolderGroup ordersA = Find(held.A, Orders, "svc_orders");
            ordersA.Total.Should().Be(3);
            ordersA.Idle.Should().Be(3);
            ordersA.ThisDatabase.Should().BeTrue();
            ordersA.ClientAddress.Should().NotBeNullOrEmpty("a TCP client has an address, without its netmask").And.NotContain("/");
            ordersA.OldestIdle.Should().NotBeNull();
            Find(held.A, string.Empty, "svc_orders").Total.Should().Be(2);
            Find(held.B, Orders, "svc_orders").Total.Should().Be(3);
            Find(held.B, Billing, "svc_billing").Total.Should().Be(5);
            Find(held.B, Reports, "svc_billing").Total.Should().Be(2);
            details.Holders.Groups.Where(x => x.Database == held.B).Should().OnlyContain(static x => !x.ThisDatabase);

            // The studio's own reading session is counted in the budget and listed in no group.
            details.Holders.Groups.Sum(static x => x.Total).Should().Be(15);
            details.Holders.Databases.Select(static x => (x.Database, x.Total)).Should().Equal((held.B, 10L), (held.A, 5L));
            details.Holders.Truncated.Should().BeFalse();
        }
    }

    /// <summary>
    /// A role that is not a member of <c>pg_read_all_stats</c> reads other roles' sessions with no state and no address - and, as
    /// B0 measured and the page relies on, still with their database, role and application name. They are counted, as unknown.
    /// </summary>
    [PostgresFact]
    public async Task A_role_without_pg_read_all_stats_counts_other_roles_sessions_and_says_it_cannot_read_their_state()
    {
        await using Held held = await Held.CreateAsync(server, "holders_hidden");
        await held.IdleAsync(held.B, "svc_billing", Billing, 5);
        await held.IdleAsync(held.A, "svc_orders", Orders, 3);
        await held.UntilHeldAsync("idle", 8);

        await using StudioHost host = Studio(held, role: "svc_reader");
        await using StudioCircuit admin = host.Circuit("admin");
        ISessionActivityService sessions = admin.Get<ISessionActivityService>();

        SessionActivitySummary summary = await sessions.GetSummaryAsync(Scope, Token);
        summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
        summary.Holders!.AllSessionsVisible.Should().BeFalse("svc_reader reads other roles' sessions without a state");
        summary.Holders.ServerConnections.Should().Be(9, "eight held and the read's own: counted, whatever the role can read");

        SessionActivityDetails details = await sessions.GetDetailsAsync(Scope, Token);
        details.Refusal.Should().Be(DatabaseRefusal.None, details.Reason);

        SessionHolderGroup billing = details.Holders.Groups.Should().ContainSingle(x => x.Role == "svc_billing").Subject;
        billing.Database.Should().Be(held.B, "a hidden session's database is still read");
        billing.Application.Should().Be(Billing, "and so are its role and its application name");
        billing.Hidden.Should().BeTrue();
        billing.Total.Should().Be(5);
        billing.StateUnknown.Should().Be(5);
        billing.Idle.Should().Be(0, "an unreadable state is counted as neither idle nor active");
        billing.ClientAddress.Should().BeNull();
        billing.ClientText.Should().Be("not readable");
    }

    // ----------------------------------------------------------------------------------------------
    // C13
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("C13")]
    public async Task An_application_holding_a_tenth_of_the_limit_idle_across_two_databases_is_a_warning_once_the_server_is_nearly_full()
    {
        await using Held held = await Held.CreateAsync(server, "holders_c13_fires");
        await held.IdleAsync(held.A, "svc_orders", Orders, 4);
        await held.IdleAsync(held.B, "svc_orders", Orders, 4);
        await held.IdleAsync(held.B, "svc_billing", Billing, 6);
        await held.IdleAsync(held.B, "svc_billing", Reports, 1);
        await held.UntilHeldAsync("idle", 15);

        await using StudioHost host = Studio(held);
        HealthReport report = await ReportAsync(host, "admin");

        HealthCheckStatus status = report.Checks.Single(static x => x.Code == "C13");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C13").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().Contain("2 applications each hold at least 10% of the server's client limit idle");

        IReadOnlyList<HealthEvidence> named = [.. finding.Evidence.Where(static x => x.Label.StartsWith("Application ", StringComparison.Ordinal))];
        named.Should().HaveCount(2, "orders-api and billing-api; reports holds one idle, which is under the line");
        named[0].Label.Should().Be("Application 1 (most idle)");
        named[0].Value.Should().StartWith("'orders-api': 8 idle of 8 connections")
            .And.Contain(held.A + " (4)").And.Contain(held.B + " (4)", "one application, summed across the two databases")
            .And.Contain("svc_orders@");
        named[1].Value.Should().StartWith("'billing-api': 6 idle of 6 connections").And.Contain(held.B + " (6)").And.Contain("svc_billing@");
        finding.Evidence.Should().NotContain(static x => x.Value.Contains(Reports, StringComparison.Ordinal));
        finding.Evidence.Single(static x => x.Label == "Client limit").Value.Should().StartWith("17 (max_connections 20");
        finding.Finding.Link!.Query.Should().Contain(new KeyValuePair<string, string?>("view", "holders"));
    }

    [PostgresFact]
    [HealthQuiet("C13")]
    public async Task Idle_connections_are_a_warm_pool_below_the_line_and_connections_held_in_transactions_are_not_idle_above_it()
    {
        await using Held held = await Held.CreateAsync(server, "holders_c13_quiet");
        await held.IdleAsync(held.A, "svc_orders", Orders, 2);
        await held.IdleAsync(held.B, "svc_orders", Orders, 2);
        await held.UntilHeldAsync("idle", 4);

        await using (StudioHost below = Studio(held))
        {
            HealthReport report = await ReportAsync(below, "admin");

            report.Findings.Should().NotContain(static x => x.Code == "C13", "five of seventeen is a warm pool");
            HealthCheckStatus status = report.Checks.Single(static x => x.Code == "C13");
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
            status.Note.Should().Contain("warm pool").And.Contain("80%");
        }

        // Above the line, by connections that are not idle: fourteen sit inside a transaction, which is C04's to judge.
        await held.CloseAsync();
        await held.IdleInTransactionAsync(held.A, "svc_orders", Orders, 14);
        await held.UntilHeldAsync("idle in transaction", 14);

        await using StudioHost above = Studio(held);
        HealthReport crowded = await ReportAsync(above, "admin");

        crowded.Findings.Should().NotContain(static x => x.Code == "C13");
        crowded.Checks.Single(static x => x.Code == "C13").Note.Should().Contain("no application holds 2 or more of them idle");
    }

    // ----------------------------------------------------------------------------------------------
    // C14
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("C14")]
    public async Task Connections_with_no_application_name_are_named_by_role_address_and_database_as_an_info()
    {
        await using Held held = await Held.CreateAsync(server, "holders_c14_fires");
        await held.IdleAsync(held.A, "svc_orders", application: null, 2);
        await held.IdleAsync(held.B, "svc_billing", application: null, 1);
        await held.IdleAsync(held.B, "svc_billing", Billing, 3);
        await held.UntilHeldAsync("idle", 6);

        await using StudioHost host = Studio(held);
        HealthReport report = await ReportAsync(host, "admin");

        HealthCheckStatus status = report.Checks.Single(static x => x.Code == "C14");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C14").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Title.Should().Be("3 client connections set no application name, so they cannot be attributed");

        IReadOnlyList<string> holders = [.. finding.Evidence.Where(static x => x.Label.StartsWith("Holder ", StringComparison.Ordinal)).Select(static x => x.Value)];
        holders.Should().HaveCount(2);
        holders[0].Should().StartWith("svc_orders@").And.EndWith("/" + held.A + " (2)");
        holders[1].Should().StartWith("svc_billing@").And.EndWith("/" + held.B + " (1)");
        finding.Evidence.Should().NotContain(static x => x.Value.Contains(Billing, StringComparison.Ordinal), "a named application is not C14's");
    }

    [PostgresFact]
    [HealthQuiet("C14")]
    public async Task When_every_connection_sets_an_application_name_C14_is_quiet()
    {
        await using Held held = await Held.CreateAsync(server, "holders_c14_quiet");
        await held.IdleAsync(held.A, "svc_orders", Orders, 2);
        await held.IdleAsync(held.B, "svc_billing", Billing, 3);
        await held.UntilHeldAsync("idle", 5);

        await using StudioHost host = Studio(held);
        HealthReport report = await ReportAsync(host, "admin");

        report.Findings.Should().NotContain(static x => x.Code == "C14");
        HealthCheckStatus status = report.Checks.Single(static x => x.Code == "C14");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("sets an application name");
    }

    // ----------------------------------------------------------------------------------------------
    // The gate
    // ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_visitor_short_of_ViewDatabaseActivity_is_told_both_checks_are_not_shown_and_sees_neither_finding()
    {
        await using Held held = await Held.CreateAsync(server, "holders_gate");
        await held.IdleAsync(held.A, "svc_orders", Orders, 8);
        await held.IdleAsync(held.B, "svc_orders", application: null, 6);
        await held.UntilHeldAsync("idle", 14);

        await using StudioHost host = Studio(held);

        HealthReport admin = await ReportAsync(host, "admin");
        admin.Findings.Select(static x => x.Code).Should().Contain(["C13", "C14"], "past the gate both are shown");

        HealthReport viewer = await ReportAsync(host, "viewer");

        viewer.Findings.Select(static x => x.Code).Should().NotContain(["C13", "C14"], "neither finding is described to a visitor short of the gate");
        System.Text.Json.JsonSerializer.Serialize(viewer.Findings.Select(static x => x.Evidence)).Should().NotContain(Orders);

        foreach (string code in new[] { "C13", "C14" })
        {
            HealthCheckStatus status = viewer.Checks.Single(x => x.Code == code);
            status.FindingsWithheld.Should().BeTrue(code + "'s row must not read 'nothing found'");
            status.BelowGate.Should().NotBeNullOrEmpty();
        }

        viewer.WithheldCount.Should().BeGreaterThanOrEqualTo(2, "both findings are counted, never described");
    }
}
