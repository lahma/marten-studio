using System.Runtime.ExceptionServices;

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

        await EveryCheckThrows(context, facts.ObservedFailure!.SourceException);
    }

    /// <summary>
    /// Npgsql raises a login the server refused (28P01 after a password rotation, 28000 from pg_hba, 3D000, 53300, 57P03 during a
    /// failover) as a <see cref="PostgresException" /> while the connection opens, and <c>PostgresFailure.OpenAsync</c> marks it as a
    /// connection failure: the server did not answer the read, and the checks say so.
    /// </summary>
    [Theory]
    [InlineData("28P01")]
    [InlineData("28000")]
    [InlineData("3D000")]
    [InlineData("53300")]
    [InlineData("57P03")]
    public async Task A_login_the_server_refused_is_kept_as_the_failure_and_every_connection_check_throws_it(string sqlState)
    {
        PostgresException refused = new("refused", "FATAL", "FATAL", sqlState);
        PostgresFailure.MarkAsConnectionFailure(refused);

        ConnectionFacts.ServerDidNotAnswer(refused).Should().BeTrue();

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=none");
        (HealthCheckContext context, HealthConnection connection) = Context(source, services);
        await using HealthConnection _ = connection;

        ConnectionFacts facts = ConnectionCheckFacts.Facts("Host=db.example") with { ObservedFailure = ExceptionDispatchInfo.Capture(refused) };
        await context.Shared(ConnectionObservedQueries.FactsKey, _ => Task.FromResult(facts));

        await EveryCheckThrows(context, refused);
    }

    /// <summary>A statement the role may not run is Postgres answering: the database was reached, and the settings stand alone.</summary>
    [Fact]
    public async Task A_statement_Postgres_refused_is_not_a_failure_to_answer_and_the_checks_still_judge_the_settings()
    {
        ConnectionFacts.ServerDidNotAnswer(new PostgresException("denied", "ERROR", "ERROR", "42501")).Should().BeFalse();
        ConnectionFacts.ServerDidNotAnswer(new NpgsqlException("wrapped", new PostgresException("denied", "ERROR", "ERROR", "42501"))).Should().BeFalse();
        ConnectionFacts.ServerDidNotAnswer(new ObjectDisposedException("source")).Should().BeTrue();
        ConnectionFacts.ServerDidNotAnswer(new NpgsqlException("Failed to connect", new TimeoutException())).Should().BeTrue();

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=none");
        (HealthCheckContext context, HealthConnection connection) = Context(source, services);
        await using HealthConnection _ = connection;

        ConnectionFacts facts = ConnectionCheckFacts.Facts("Host=db.example", notRead: "the connection read failed (SQLSTATE 42501)");
        facts.ObservedFailure.Should().BeNull();
        await context.Shared(ConnectionObservedQueries.FactsKey, _ => Task.FromResult(facts));

        (await new PoolIdleCutCheck().GatherAsync(context, Token)).Should().BeSameAs(facts);
        Func<Task> driver = () => new DriverLoggingSettingsCheck().GatherAsync(context, Token);
        await driver.Should().NotThrowAsync();
    }

    /// <summary>
    /// What the connection string alone decides stands a check down whether or not the server answered: S11 behind a pooler (as S05
    /// is), C16 with pooling off, C17 behind a pooler. A check that needs what the server reported is not asked to.
    /// </summary>
    [Theory]
    [InlineData("S11", "Host=pgbouncer.example;Port=6432")]
    [InlineData("C16", "Host=db.example;Pooling=false")]
    [InlineData("C17", "Host=pgbouncer.example;Port=6432;Pooling=false")]
    public async Task What_the_connection_string_alone_decides_stands_a_check_down_on_an_unreachable_server(string code, string connectionString)
    {
        PostgresException refused = new("refused", "FATAL", "FATAL", "28P01");
        PostgresFailure.MarkAsConnectionFailure(refused);

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=none");
        (HealthCheckContext context, HealthConnection connection) = Context(source, services);
        await using HealthConnection _ = connection;

        ConnectionFacts facts = ConnectionCheckFacts.Facts(connectionString) with { ObservedFailure = ExceptionDispatchInfo.Capture(refused) };
        facts.Settings.Readable.Should().BeTrue();
        await context.Shared(ConnectionObservedQueries.FactsKey, _ => Task.FromResult(facts));

        IHealthCheck check = code switch
        {
            "S11" => new ServerCertificateCheck(),
            "C16" => new PoolIdleCutCheck(),
            _ => new PoolingOffCheck(),
        };

        Func<Task> gather = () => check.GatherAsync(context, Token);
        (await gather.Should().ThrowAsync<HealthCheckNotApplicableException>(code)).Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// 1.1-FINAL-POLISH-2: the findings judged from the setting (S05 at <c>SSL Mode=Disable</c>, S11 at <c>Require</c>, both behind a
    /// pooler sign) need no row from the server, but a server that never answered is still Failed and never a finding, or
    /// <c>Reached</c> would count a dead database as read.
    /// </summary>
    [Theory]
    [InlineData("S05", "Host=pgbouncer.example;Port=6432;SSL Mode=Disable")]
    [InlineData("S11", "Host=pgbouncer.example;Port=6432;SSL Mode=Require")]
    public async Task A_finding_judged_from_the_setting_still_throws_the_failure_of_a_server_that_never_answered(string code, string connectionString)
    {
        PostgresException refused = new("refused", "FATAL", "FATAL", "28P01");
        PostgresFailure.MarkAsConnectionFailure(refused);

        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        await using NpgsqlDataSource source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=none");
        (HealthCheckContext context, HealthConnection connection) = Context(source, services);
        await using HealthConnection _ = connection;

        ConnectionFacts facts = ConnectionCheckFacts.Facts(connectionString) with { ObservedFailure = ExceptionDispatchInfo.Capture(refused) };
        facts.Settings.PoolerSigns.Should().NotBeEmpty();
        (code == "S05" ? UnencryptedConnectionCheck.JudgedFromSslMode(facts) : ServerCertificateCheck.JudgedFromSslMode(facts))
            .Should().BeTrue("the from-setting conditions are met: a remote host, a pooler sign and the mode");
        await context.Shared(ConnectionObservedQueries.FactsKey, _ => Task.FromResult(facts));

        IHealthCheck check = code == "S05" ? new UnencryptedConnectionCheck() : new ServerCertificateCheck();
        Func<Task> gather = () => check.GatherAsync(context, Token);

        Exception thrown = (await gather.Should().ThrowAsync<Exception>(code)).Which;
        thrown.Should().BeSameAs(refused);
        HealthSweeper.Classify(thrown, budgetExpired: false).Outcome.Should().Be(HealthCheckOutcome.Failed);
    }

    private static async Task EveryCheckThrows(HealthCheckContext context, Exception failure)
    {
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
            thrown.Should().BeSameAs(failure);

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
