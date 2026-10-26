using System.Collections.Concurrent;
using System.Globalization;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Versions;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// One Postgres server of a test's own for the replication checks - never reused, its settings its own: archive settings for
/// R02, a tiny <c>max_wal_size</c> for R04, or the primary of a <see cref="ReplicationPair" /> - seeded with the version
/// matrix's roles (<c>app</c>, <c>probe</c>, <c>stats</c>, <c>mon</c>; <see cref="PostgresVersionMatrix.SetupSql" />).
/// </summary>
internal sealed class ReplicationServer : IAsyncDisposable
{
    /// <summary>The image a server runs unless a test names one: the suite's default.</summary>
    public const string DefaultImage = "postgres:17-alpine";

    /// <summary>PostgreSQL 18, for the checkpointer's <c>num_done</c> and <c>pg_stat_io</c>'s byte columns (B0's pin).</summary>
    public const string Image18 = "postgres:18.6-alpine";

    private readonly ConcurrentBag<NpgsqlDataSource> dataSources = [];

    private ReplicationServer(PostgreSqlContainer container) => Container = container;

    /// <summary>The container.</summary>
    public PostgreSqlContainer Container { get; }

    /// <summary>The superuser's connection string.</summary>
    public string SuperuserConnectionString => Container.GetConnectionString();

    /// <summary>A connection string as <paramref name="role" />.</summary>
    public string ConnectionString(MatrixRole role) => As(SuperuserConnectionString, role);

    /// <summary>A connection string <paramref name="connectionString" /> as <paramref name="role" />, pooled, named for the role.</summary>
    public static string As(string connectionString, MatrixRole role)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString) { Pooling = true, ApplicationName = "ms-repl-" + role.ToString().ToLowerInvariant() };

        (builder.Username, builder.Password) = role switch
        {
            MatrixRole.Superuser => (builder.Username, builder.Password),
            MatrixRole.App => ("app", "app"),
            MatrixRole.Probe => ("probe", "probe"),
            MatrixRole.Stats => ("stats", "stats"),
            MatrixRole.Monitor => ("mon", "mon"),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Starts a server of <paramref name="image" /> with <paramref name="settings" /> (each <c>name=value</c>, as <c>-c</c>
    /// takes it), on <paramref name="network" /> under <paramref name="alias" /> when given, and seeds the matrix's roles.
    /// </summary>
    public static async Task<ReplicationServer> StartAsync(string image, INetwork? network, string? alias, params string[] settings)
    {
        PostgreSqlBuilder builder = new PostgreSqlBuilder(image).WithReuse(false).WithLabel("ms-repl", "1");

        if (network is not null)
        {
            builder = builder.WithNetwork(network).WithNetworkAliases(alias ?? "primary");
        }

        foreach (string setting in settings)
        {
            builder = builder.WithCommand("-c", setting);
        }

        PostgreSqlContainer container = builder.Build();

        try
        {
            await container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }

        ReplicationServer server = new(container);
        await server.ExecuteAsync(PostgresVersionMatrix.SetupSql);
        return server;
    }

    /// <summary>Runs <paramref name="sql" /> as the superuser.</summary>
    public Task ExecuteAsync(string sql) => Execute(SuperuserConnectionString, sql);

    /// <summary>Reads one value as the superuser.</summary>
    public Task<T> ScalarAsync<T>(string sql) => Scalar<T>(SuperuserConnectionString, sql);

    /// <summary>The size of this server's <c>pg_wal</c>, in KiB, for the disk measurement.</summary>
    public Task<long> WalKibAsync() => KibAsync(Container, "\"$PGDATA/pg_wal\"");

    /// <summary>The size of <paramref name="path" /> inside <paramref name="container" />, in KiB (BusyBox <c>du -sk</c>).</summary>
    internal static async Task<long> KibAsync(IContainer container, string path)
    {
        ExecResult result = await container.ExecAsync(["sh", "-c", "du -sk " + path + " | cut -f1"], TestContext.Current.CancellationToken);
        return long.TryParse(result.Stdout.Trim(), CultureInfo.InvariantCulture, out long kib) ? kib : -1;
    }

    /// <summary>Writes WAL a segment at a time: <paramref name="segments" /> times a row and <c>pg_switch_wal()</c>.</summary>
    public async Task SwitchSegmentsAsync(int segments)
    {
        await ExecuteAsync("create table if not exists public.repl_switch (id int)");

        for (int i = 0; i < segments; i++)
        {
            await ExecuteAsync("insert into public.repl_switch values (" + i.ToString(CultureInfo.InvariantCulture) + "); select pg_catalog.pg_switch_wal()");
        }
    }

    /// <summary>A Postgres Studio host over this server as <paramref name="role" />, on <paramref name="clock" />, every capability on.</summary>
    public StudioHost Host(MatrixRole role, TimeProvider clock) => HostOver(ConnectionString(role), clock, dataSources);

    /// <summary>A Postgres Studio host over <paramref name="connectionString" />, every capability on.</summary>
    internal static StudioHost HostOver(string connectionString, TimeProvider clock, ConcurrentBag<NpgsqlDataSource>? owned = null) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ =>
                {
                    NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
                    owned?.Add(dataSource);
                    return dataSource;
                });
                services.AddSingleton(clock);
            },
            options => options.Capabilities = PostgresStudioCapabilities.All());

    internal static async Task Execute(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    internal static async Task<T> Scalar<T>(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        return (T) (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Waits until <paramref name="condition" /> holds, re-asking every 200 ms for up to <paramref name="within" />.</summary>
    internal static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan within, string what)
    {
        DateTime deadline = DateTime.UtcNow + within;

        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Waited " + within + " for " + what + ".");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (NpgsqlDataSource dataSource in dataSources)
        {
            await dataSource.DisposeAsync();
        }

        await Container.DisposeAsync();
    }
}

/// <summary>
/// B0's two-container recipe (<c>repl-up.sh</c>), through Testcontainers on a private network: a primary allowing replication
/// connections, a physical slot <c>standby1</c>, and a standby made with <c>pg_basebackup -R -X stream -S standby1</c>,
/// started with <c>hot_standby_feedback=on</c>. Never reused; disposed with the class's fixture.
/// </summary>
internal sealed class ReplicationPair : IAsyncDisposable
{
    private readonly INetwork network;

    private ReplicationPair(INetwork network, ReplicationServer primary, IContainer standby, string image)
    {
        this.network = network;
        Primary = primary;
        Standby = standby;
        Image = image;
    }

    /// <summary>The primary.</summary>
    public ReplicationServer Primary { get; }

    /// <summary>The standby's container.</summary>
    public IContainer Standby { get; }

    /// <summary>The image both run.</summary>
    public string Image { get; }

    /// <summary>A connection string to the standby as <paramref name="role" /> - the roles and their passwords came with the base backup.</summary>
    public string StandbyConnectionString(MatrixRole role)
    {
        NpgsqlConnectionStringBuilder builder = new(Primary.SuperuserConnectionString)
        {
            Host = Standby.Hostname,
            Port = Standby.GetMappedPublicPort(5432),
        };

        return ReplicationServer.As(builder.ConnectionString, role);
    }

    /// <summary>Starts the pair on <paramref name="image" />.</summary>
    public static async Task<ReplicationPair> StartAsync(string image)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        INetwork network = new NetworkBuilder().WithName("ms-repl-" + Guid.NewGuid().ToString("N")[..8]).Build();
        await network.CreateAsync(token);

        ReplicationServer? primary = null;

        try
        {
            // A small max_wal_size on both, so a checkpoint after a test gives back what the test wrote (W5-R2-fix 8).
            primary = await ReplicationServer.StartAsync(image, network, "primary", "wal_level=replica", "max_wal_senders=10", "max_wal_size=64MB", "min_wal_size=32MB");

            await primary.Container.ExecAsync(["sh", "-c", "echo 'host replication all all trust' >> \"$PGDATA/pg_hba.conf\""], token);
            await primary.ExecuteAsync("select pg_catalog.pg_reload_conf()");
            await primary.ExecuteAsync("select pg_catalog.pg_create_physical_replication_slot('standby1')");

            IContainer standby = new ContainerBuilder(image)
                .WithNetwork(network)
                .WithLabel("ms-repl", "1")
                .WithCreateParameterModifier(static x => x.User = "postgres")
                .WithEntrypoint("sh")
                .WithCommand(
                    "-c",
                    "pg_basebackup -h primary -U postgres -D /tmp/standby -R -X stream -S standby1 && chmod 0700 /tmp/standby "
                    + "&& exec postgres -D /tmp/standby -c hot_standby_feedback=on -c archive_mode=off -c max_wal_size=64MB -c min_wal_size=32MB "
                    + "-c listen_addresses='*'")
                .WithPortBinding(5432, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("database system is ready to accept read-only connections"))
                .Build();

            await standby.StartAsync(token);

            ReplicationPair pair = new(network, primary, standby, image);

            await ReplicationServer.UntilAsync(
                async () => await ReplicationServer.Scalar<bool>(pair.StandbyConnectionString(MatrixRole.Superuser), "select pg_catalog.pg_is_in_recovery()"),
                TimeSpan.FromSeconds(60),
                "the standby to accept connections in recovery");

            return pair;
        }
        catch
        {
            if (primary is not null)
            {
                await primary.DisposeAsync();
            }

            await network.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Gives back the WAL a test wrote: a checkpoint on the primary and a restartpoint on the standby, once it has caught up,
    /// so a recycled segment is not still on disk when the next test writes its own (W5-R2-fix 8).
    /// </summary>
    public async Task CheckpointBothAsync()
    {
        await CaughtUpAsync();
        await Primary.ExecuteAsync("checkpoint");
        await ReplicationServer.Execute(StandbyConnectionString(MatrixRole.Superuser), "checkpoint");
    }

    /// <summary>The size of the standby's <c>pg_wal</c>, in KiB, for the disk measurement.</summary>
    public async Task<long> StandbyWalKibAsync() => await ReplicationServer.KibAsync(Standby, "/tmp/standby/pg_wal");

    /// <summary>Waits until the standby has replayed everything the primary has written.</summary>
    public Task CaughtUpAsync() =>
        ReplicationServer.UntilAsync(
            async () => await Primary.ScalarAsync<bool>(
                "select coalesce(bool_and(r.replay_lsn >= pg_catalog.pg_current_wal_lsn()), false) from pg_catalog.pg_stat_replication r"),
            TimeSpan.FromSeconds(60),
            "the standby to replay everything");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Standby.DisposeAsync();
        await Primary.DisposeAsync();
        await network.DisposeAsync();
    }
}

/// <summary>
/// The heavy live classes of this folder - the primary and standby pairs, the archiver's and the checkpoints' servers - in one
/// collection, so that they run one after another and their disk peaks never add up next to the version matrix's servers on
/// a 14 GB CI runner (W5-R2-fix 8). A class fixture is disposed when its class ends, so the 17 pair is gone before the 18
/// pair starts.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ReplicationHeavySuite
{
    /// <summary>The collection's name.</summary>
    public const string Name = "replication-heavy";
}

/// <summary>One primary and standby pair of <see cref="Image" />, started on first use by the class's tests and shared by them.</summary>
public abstract class ReplicationPairFixture : IAsyncLifetime
{
    private readonly Lazy<Task<ReplicationPair>> pair;

    protected ReplicationPairFixture(string image)
    {
        Image = image;
        pair = new Lazy<Task<ReplicationPair>>(() => ReplicationPair.StartAsync(image), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The image both servers run.</summary>
    public string Image { get; }

    internal Task<ReplicationPair> PairAsync() => pair.Value;

    /// <inheritdoc />
    public ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!pair.IsValueCreated)
        {
            return;
        }

        try
        {
            await (await pair.Value).DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A pair that never started has nothing to dispose; Testcontainers' reaper removes anything left.
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>The PostgreSQL 17 pair: the gibibyte of lag, and the release's shapes.</summary>
public sealed class ReplicationPair17Fixture() : ReplicationPairFixture(ReplicationServer.DefaultImage);

/// <summary>The PostgreSQL 18 pair: the checkpointer's num_done and pg_stat_io's byte columns - no gibibyte of lag.</summary>
public sealed class ReplicationPair18Fixture() : ReplicationPairFixture(ReplicationServer.Image18);
