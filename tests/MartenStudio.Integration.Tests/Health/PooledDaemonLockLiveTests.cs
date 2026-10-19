using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

using JasperFx.Events.Daemon;

using MartenStudio.Services.Health;

using Npgsql;

using Testcontainers.PostgreSql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// C10 through a real PgBouncer in transaction mode (<see cref="PooledServerFixture" />): two consecutive transactions on
/// one of the store's connections reach two Postgres server processes, which is the proof - and K04, which can only read
/// the host and port, does not see this pooler on the port the test maps it to, which is why C10 exists.
/// </summary>
public class PooledDaemonLockLiveTests(PooledServerFixture pooled) : IClassFixture<PooledServerFixture>
{
    [PostgresFact]
    [HealthFires("C10")]
    public async Task A_HotCold_session_lock_behind_a_transaction_mode_PgBouncer_is_proven_and_critical()
    {
        await ConnectionsLiveHost.CreateDatabaseAsync(pooled.DirectConnectionString, "health_c10_fires");
        string throughBouncer = pooled.PooledConnectionString("health_c10_fires");

        await pooled.WarmAsync("health_c10_fires");

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            throughBouncer,
            sample: false,
            studio: static options => options.Capabilities = MartenStudioCapabilities.All(),
            store: static options =>
            {
                options.Projections.AsyncMode = DaemonMode.HotCold;
                options.Events.UseAdvisoryLockTransaction = false;
            });

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C10").Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical);

        string[] processes = [.. finding.Evidence.Where(static x => x.Label.StartsWith("Server process of the", StringComparison.Ordinal)).Select(static x => x.Value)];
        processes.Should().HaveCount(2).And.OnlyContain(static x => x.StartsWith("pid ", StringComparison.Ordinal));
        processes.Distinct().Should().HaveCount(2, "two transactions, two server processes");

        report.Findings.Should().NotContain(static x => x.Code == "K04",
            "a pooler on a port the test chose looks like nothing K04 knows - only the probe can tell");
    }

    /// <summary>The same store with a transaction-scoped lock, through the same pooler: nothing to split, nothing probed.</summary>
    [PostgresFact]
    public async Task A_transaction_scoped_lock_through_the_same_pooler_is_quiet_and_probes_nothing()
    {
        await ConnectionsLiveHost.CreateDatabaseAsync(pooled.DirectConnectionString, "health_c10_xact");

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            pooled.PooledConnectionString("health_c10_xact"),
            sample: false,
            store: static options =>
            {
                options.Projections.AsyncMode = DaemonMode.HotCold;
                options.Events.UseAdvisoryLockTransaction = true;
            });

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "C10");
        HealthCheckStatus c10 = ConnectionsLiveHost.Status(report, "C10");
        c10.Note.Should().Contain("transaction-scoped");
        c10.Statements.Should().BeEmpty();
    }
}

/// <summary>
/// A Postgres and a PgBouncer in front of it, on a network of their own: <c>pool_mode = transaction</c>, and
/// <c>server_round_robin = 1</c> so that idle server connections are handed out oldest first - with two of them warm,
/// two consecutive transactions land on two different ones, deterministically. Never reused.
/// </summary>
/// <remarks>
/// <para>
/// <b>The image</b> is <see cref="Image" />, <c>edoburu/pgbouncer</c> - the most widely used PgBouncer image on Docker
/// Hub, an Alpine build of upstream PgBouncer - pinned to 1.25.2 (published 2026-06-10), by digest. Its entrypoint, which writes a
/// configuration from environment variables, is bypassed: the container runs <c>pgbouncer</c> itself over an ini and a
/// userlist this fixture maps in, so nothing about the pooler's behaviour depends on how a script parses a variable.
/// Pulled through Testcontainers' generic <c>ContainerBuilder</c>, which <c>Testcontainers.PostgreSql</c> already brings:
/// no new package (R1 decision 4).
/// </para>
/// <para>
/// PgBouncer authenticates the client and the server with SCRAM from the plain password in the userlist, which the
/// PgBouncer documentation allows for <c>auth_type = scram-sha-256</c>.
/// </para>
/// </remarks>
public sealed class PooledServerFixture : IAsyncLifetime
{
    /// <summary>
    /// The pinned PgBouncer image, by tag and digest: the multi-architecture index Docker Hub lists for
    /// <c>v1.25.2-p0</c> (last updated 2026-06-10, checked 2026-10-18), so a re-pushed tag cannot change what runs.
    /// </summary>
    public const string Image = "edoburu/pgbouncer:v1.25.2-p0@sha256:7d7a27d9e90985cab5cf42256f5c13a3120baa4b055b69df37beb272b89b2340";

    /// <summary>The port PgBouncer listens on inside the network.</summary>
    public const int BouncerPort = 6432;

    private const string Ini =
        "[databases]\n" +
        "* = host=pg port=5432\n" +
        "\n" +
        "[pgbouncer]\n" +
        "listen_addr = 0.0.0.0\n" +
        "listen_port = 6432\n" +
        "auth_type = scram-sha-256\n" +
        "auth_file = /etc/pgbouncer/userlist.txt\n" +
        "pool_mode = transaction\n" +
        "server_round_robin = 1\n" +
        "default_pool_size = 5\n" +
        "max_client_conn = 200\n" +
        "ignore_startup_parameters = extra_float_digits,options\n";

    private const string Userlist = "\"postgres\" \"postgres\"\n";

    private INetwork? network;
    private PostgreSqlContainer? postgres;
    private IContainer? bouncer;
    private bool started;

    /// <summary>A superuser connection straight to Postgres, past the pooler.</summary>
    public string DirectConnectionString => started
        ? postgres!.GetConnectionString()
        : throw new InvalidOperationException("The pooled server is not running; a test that needs it must be a [PostgresFact].");

    /// <summary>A connection string to <paramref name="database" /> through PgBouncer.</summary>
    public string PooledConnectionString(string database) => started
        ? new NpgsqlConnectionStringBuilder
        {
            Host = bouncer!.Hostname,
            Port = bouncer.GetMappedPublicPort(BouncerPort),
            Username = "postgres",
            Password = "postgres",
            Database = database,
        }.ConnectionString
        : throw new InvalidOperationException("The pooled server is not running; a test that needs it must be a [PostgresFact].");

    /// <summary>
    /// Makes PgBouncer open two server connections to <paramref name="database" />, by holding two transactions open at
    /// once through it, and then lets both go idle - so that the next two transactions, oldest-first, reach two.
    /// </summary>
    public async Task WarmAsync(string database)
    {
        string unpooled = new NpgsqlConnectionStringBuilder(PooledConnectionString(database)) { Pooling = false }.ConnectionString;

        await using NpgsqlConnection one = new(unpooled);
        await using NpgsqlConnection two = new(unpooled);
        await one.OpenAsync(TestContext.Current.CancellationToken);
        await two.OpenAsync(TestContext.Current.CancellationToken);

        await using NpgsqlTransaction first = await one.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using NpgsqlTransaction second = await two.BeginTransactionAsync(TestContext.Current.CancellationToken);

        int a = await PidAsync(one, first);
        int b = await PidAsync(two, second);
        a.Should().NotBe(b, "two transactions open at once through the pooler hold two server connections");

        await first.CommitAsync(TestContext.Current.CancellationToken);
        await second.CommitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> PidAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using NpgsqlCommand command = new("select pg_backend_pid()", connection, transaction);
        return (int) (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        network = new NetworkBuilder().Build();
        await network.CreateAsync();

        postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithNetwork(network)
            .WithNetworkAliases("pg")
            .WithReuse(false)
            .Build();
        await postgres.StartAsync();

        bouncer = new ContainerBuilder(Image)
            .WithNetwork(network)
            .WithEntrypoint("/usr/bin/pgbouncer")
            .WithCommand("/etc/pgbouncer/pgbouncer.ini")
            .WithResourceMapping(Encoding.UTF8.GetBytes(Ini), "/etc/pgbouncer/pgbouncer.ini")
            .WithResourceMapping(Encoding.UTF8.GetBytes(Userlist), "/etc/pgbouncer/userlist.txt")
            .WithPortBinding(BouncerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(BouncerPort))
            .Build();
        await bouncer.StartAsync();

        started = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (bouncer is not null)
        {
            await bouncer.DisposeAsync();
        }

        if (postgres is not null)
        {
            await postgres.DisposeAsync();
        }

        if (network is not null)
        {
            await network.DisposeAsync();
        }
    }
}
