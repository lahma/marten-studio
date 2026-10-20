using System.Security.Claims;

using Marten;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using Testcontainers.PostgreSql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// A studio over the sample store in a <b>database of the test's own</b>, for the connection checks' live tests.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a database and not only a schema.</b> Every other live test shares one Postgres database and isolates
/// itself by schema. The connection checks read database-wide state - <c>pg_stat_activity</c> and
/// <c>pg_prepared_xacts</c> bounded to <c>current_database()</c>, <c>pg_stat_database</c>'s counters - so a
/// session another test class holds idle in a transaction would fire this class's quiet test. A database of its
/// own is the boundary those reads respect.
/// </para>
/// <para>
/// Everything goes through the studio's own services, resolved in a circuit-shaped scope, as
/// <see cref="HealthLiveHost" /> does; <see cref="GatherAsync{TFacts}(HealthCheck{TFacts})" /> runs one check's gatherer on its own,
/// for a test that judges a real snapshot at a later time (<c>SnapshotAge</c>) rather than waiting for it.
/// </para>
/// </remarks>
internal sealed class ConnectionsLiveHost : IAsyncDisposable
{
    private ConnectionsLiveHost(ServiceProvider provider, LogCapture logs, string connectionString, ManualClock? clock)
    {
        Provider = provider;
        Logs = logs;
        ConnectionString = connectionString;
        Clock = clock;
    }

    /// <summary>The container.</summary>
    public ServiceProvider Provider { get; }

    /// <summary>The studio's log.</summary>
    public LogCapture Logs { get; }

    /// <summary>The test's own database.</summary>
    public string ConnectionString { get; }

    /// <summary>The studio's clock, when the test moves it.</summary>
    public ManualClock? Clock { get; }

    /// <summary>The store.</summary>
    public IDocumentStore Store => Provider.GetRequiredService<IDocumentStore>();

    /// <summary>The store's database as a whole.</summary>
    public StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>
    /// A fresh, empty database on the server <paramref name="adminConnectionString" /> reaches, dropped first -
    /// with force, so a session a previous run leaked cannot make the drop hang.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(string adminConnectionString, string name)
    {
        string quoted = SqlIdentifier.Quote(name);

        await using (NpgsqlConnection connection = new(adminConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using (NpgsqlCommand drop = new($"drop database if exists {quoted} with (force)", connection))
            {
                await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using NpgsqlCommand create = new($"create database {quoted}", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;
    }

    /// <param name="connectionString">The test's own database (<see cref="CreateDatabaseAsync" />).</param>
    /// <param name="store">Changes on top of the sample domain's configuration, or the whole configuration when <paramref name="sample" /> is off.</param>
    /// <param name="studio">The studio's options.</param>
    /// <param name="services">Anything else the container needs.</param>
    /// <param name="clock">A clock the test moves; the system's when <see langword="null" />.</param>
    /// <param name="sample">Whether the store is the sample domain's.</param>
    public static async Task<ConnectionsLiveHost> CreateAsync(
        string connectionString,
        Action<StoreOptions>? store = null,
        Action<MartenStudioOptions>? studio = null,
        Action<IServiceCollection>? services = null,
        ManualClock? clock = null,
        bool sample = true)
    {
        LogCapture logs = new();
        ServiceCollection collection = new();

        collection.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        collection.AddAuthorization();
        collection.AddSingleton<AuthenticationStateProvider, Operator>();
        collection.AddSingleton<IHostEnvironment>(new Development());

        if (clock is not null)
        {
            collection.AddSingleton<TimeProvider>(clock);
        }

        collection.AddMarten(options =>
        {
            if (sample)
            {
                SampleStore.Configure(options, connectionString);
            }
            else
            {
                options.Connection(connectionString);
            }

            store?.Invoke(options);
        });

        collection.AddMartenStudio(options => studio?.Invoke(options));
        services?.Invoke(collection);

        ConnectionsLiveHost host = new(collection.BuildServiceProvider(), logs, connectionString, clock);

        if (sample)
        {
            await host.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        return host;
    }

    /// <summary>The visitor's health report, through a circuit-shaped scope of its own.</summary>
    public async Task<HealthReport> ReportAsync()
    {
        await using AsyncServiceScope circuit = Provider.CreateAsyncScope();

        return await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(Scope, HealthReportRequest.Regular, TestContext.Current.CancellationToken);
    }

    /// <summary>A circuit, for a test that reads the Sessions service more than once in the same one.</summary>
    public AsyncServiceScope Circuit() => Provider.CreateAsyncScope();

    /// <summary>One check's gatherer, run against the database the way a sweep runs it.</summary>
    public Task<TFacts> GatherAsync<TFacts>(HealthCheck<TFacts> check)
        where TFacts : notnull =>
        GatherAsync(check.GatherAsync);

    /// <summary>A gatherer's read, run against the database the way a sweep runs a check's.</summary>
    public async Task<TFacts> GatherAsync<TFacts>(Func<HealthCheckContext, CancellationToken, Task<TFacts>> gather)
        where TFacts : notnull
    {
        await using AsyncServiceScope circuit = Provider.CreateAsyncScope();

        ResolvedScope resolved = await circuit.ServiceProvider.GetRequiredService<StudioScopeResolver>()
            .ResolveAsync(Scope, null, TestContext.Current.CancellationToken);

        MartenStudioOptions value = Provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;

        await using HealthConnection connection = new(resolved.Target);
        HealthSqlRunner sql = new(connection, new ReadOnlySqlOptions { StatementTimeout = TimeSpan.FromSeconds(15), Role = value.SqlConsoleRole }, null);

        HealthCheckContext context = new(
            resolved,
            MartenStudioSettings.Of(value),
            MartenStudioRuntime.Current,
            circuit.ServiceProvider,
            sql,
            Clock?.GetUtcNow() ?? DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            TestContext.Current.CancellationToken);

        return await gather(context, TestContext.Current.CancellationToken);
    }

    /// <summary>One check's status row.</summary>
    public static HealthCheckStatus Status(HealthReport report, string code) =>
        report.Checks.Single(x => string.Equals(x.Code, code, StringComparison.Ordinal));

    /// <summary>What the test's own sessions call themselves.</summary>
    public const string TestSessionApplication = "MartenStudio.Integration.Tests.session";

    /// <summary>
    /// An open connection to the test's database, for the test to hold a condition with - from a pool of its own, as
    /// another application's session would be: Npgsql names an unnamed pool by its connection string, so sessions
    /// opened with the store's own would share the store pool's name and make C01's reading ambiguous.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = TestSessionApplication }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public async ValueTask DisposeAsync() => await Provider.DisposeAsync();

    private sealed class Operator : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test"))));
    }

    private sealed class Development : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "MartenStudio.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>Waiting on a condition, never on a fixed delay (AGENTS.md, Testing).</summary>
internal static class LiveWait
{
    /// <summary>Asks <paramref name="condition" /> every quarter of a second until it is true, or fails naming <paramref name="what" />.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        ArgumentNullException.ThrowIfNull(condition);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));

        while (!await condition())
        {
            try
            {
                await timer.WaitForNextTickAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Gave up after " + timeout + " waiting for " + what + ".");
            }
        }
    }

    /// <summary>One scalar from a connection of the test's own.</summary>
    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        object? result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? default : (T) result;
    }
}

/// <summary>
/// A Postgres server of this class's own, for the conditions the shared one cannot be put into without
/// disturbing every other test: a small <c>max_connections</c>, so headroom (C03) and a store's budget (C02) can
/// be exceeded, and prepared transactions switched on (<c>max_prepared_transactions</c> is 0 by default), for
/// C07. Never reused: its settings are this fixture's alone.
/// </summary>
public sealed class ConnectionsServerFixture : IAsyncLifetime
{
    /// <summary><c>max_connections</c>.</summary>
    public const int MaxConnections = 25;

    /// <summary><c>max_prepared_transactions</c>.</summary>
    public const int MaxPreparedTransactions = 4;

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=" + MaxConnections.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .WithCommand("-c", "max_prepared_transactions=" + MaxPreparedTransactions.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>The server's superuser connection string.</summary>
    public string ConnectionString => started
        ? container.GetConnectionString()
        : throw new InvalidOperationException("The connections server is not running; a test that needs it must be a [PostgresFact].");

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
