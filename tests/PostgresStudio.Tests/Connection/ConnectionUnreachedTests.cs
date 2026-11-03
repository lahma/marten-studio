using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// 1.1-CI-FIX: a database the server never answers is not a database in good order to the connection-settings checks. The
/// facts keep the settings of an unreachable database (the Connection page shows them), but C16, C17, C18, S11, S12 and S13
/// judge settings alone and would all report on a database the sweep never reached - and
/// <c>PostgresDiagnosticsService.Reached</c> counts a catalog check that did not fail as proof that it was, so a disposed
/// data source read as <c>Read</c> (<c>DiagnosticsLiveTests</c>). The checks throw the connection read's failure again.
/// </summary>
public sealed class ConnectionUnreachedTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_data_source_that_was_disposed_leaves_the_facts_with_the_failure_and_every_connection_check_throws_it()
    {
        NpgsqlDataSource source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unreached_" + Guid.NewGuid().ToString("N") + ";Username=none;Password=secret;Timeout=1;Persist Security Info=true");
        await source.DisposeAsync();

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        (HealthCheckContext context, HealthConnection connection) = Context(source, services);
        await using HealthConnection _ = connection;

        ConnectionFacts facts = await ConnectionFacts.SharedAsync(context);

        facts.Settings.Readable.Should().BeTrue("the settings need no connection");
        facts.Observed.Should().BeNull();
        facts.ObservedNotRead.Should().NotBeNullOrWhiteSpace();
        facts.ObservedFailure.Should().NotBeNull("the server never answered");

        IHealthCheck[] checks =
        [
            new PoolIdleCutCheck(),
            new PoolingOffCheck(),
            new SiblingPoolsCheck(),
            new ServerCertificateCheck(),
            new ReadableCredentialCheck(),
            new DriverLoggingSettingsCheck(),
        ];

        foreach (IHealthCheck check in checks)
        {
            Func<Task> gather = () => check.GatherAsync(context, Token);

            Exception thrown = (await gather.Should().ThrowAsync<Exception>(check.GetType().Name + " reads a database that did not answer")).Which;
            thrown.Should().BeSameAs(facts.ObservedFailure);

            HealthSweeper.Classify(thrown, budgetExpired: false).Outcome.Should().Be(
                HealthCheckOutcome.Failed,
                "the sweep classifies it as it does every other check that met the same failure");
        }
    }

    private static (HealthCheckContext Context, HealthConnection Connection) Context(NpgsqlDataSource source, IServiceProvider services)
    {
        DataSourceOnly target = new(source);
        ResolvedSource resolved = new(new StudioScope("default", "default", null), new StudioSourceRegistration("default", "Default", StudioSourceKind.DataSource), target);
        HealthConnection connection = new(target);

        HealthCheckContext context = new(
            resolved,
            new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
            NoStudioRuntime.Instance,
            services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
            new HealthSweepMemo(),
            Token);

        return (context, connection);
    }

    private sealed class DataSourceOnly(NpgsqlDataSource dataSource) : IStudioDatabase
    {
        public string Identity => "default";

        public string Name => "unreached";

        public string Server => "127.0.0.1:1";

        public NpgsqlDataSource? DataSource => dataSource;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => dataSource.CreateConnection();
    }
}
