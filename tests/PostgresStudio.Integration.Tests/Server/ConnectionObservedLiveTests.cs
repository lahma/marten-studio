using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Integration.Tests.Storage;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// 1.1-NP1 on real servers: what the server saw of the studio's own connection (NP-DESIGN §4), through the studio's own
/// services - the settings a new session starts with (never the studio's read-only transaction's), the connection's TLS and
/// GSS state, the parameters the server reported, the pools that share the data source - and the sentinel test for the
/// whole read, logs at Trace included.
/// </summary>
public class ConnectionObservedLiveTests(PostgresVersionMatrix matrix)
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, MaxDepth = 64 };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The seeded <c>postgres</c> database as the <c>app</c> role, on every supported major: who the server thinks the
    /// connection is, the start-up values of the three settings the studio's transaction overrides (so <c>reset_val</c>, not the
    /// studio's 30 s), <c>search_path</c> as the connection started with and not the studio's pin, and no TLS on a plain
    /// container.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.Supported), MemberType = typeof(PostgresVersionMatrix))]
    public async Task The_server_sees_the_studios_own_connection_as_a_new_session_starts(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        // A database of this test's own. C18 counts every pool of the process that shares the data source's host, port, database and
        // login - whatever its Application Name - because Npgsql's meter is the process's: another class's host of the same role in
        // the seeded database, alive while this one runs, is a sibling by design. No other test connects here.
        string database = await server.CreateDatabaseAsync("ms_connobs_siblings", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        (HealthCheckContext context, HealthConnection connection) = host.Context();
        await using HealthConnection _ = connection;

        ConnectionFacts facts = await ConnectionFacts.ReadAsync(context, Token);

        facts.ObservedNotRead.Should().BeNull();
        ConnectionObserved observed = facts.Observed!;

        observed.SessionUser.Should().Be("app");
        observed.CurrentUser.Should().Be("app");
        observed.Database.Should().Be(database);
        observed.ServerAddress.Should().NotBeNullOrWhiteSpace();
        observed.ServerPort.Should().Be(5432);
        observed.ClientAddress.Should().NotBeNullOrWhiteSpace("the studio reaches the container over TCP, so the server sees a peer: the TLS tile's 'local' needs it to be loopback too");
        observed.InRecovery.Should().BeFalse();
        observed.Ssl.Should().BeFalse("a plain container has no TLS, and pg_stat_ssl has a row for this backend");
        observed.SslVersion.Should().BeNull();
        observed.GssAuthenticated.Should().BeFalse();
        observed.GssEncrypted.Should().BeFalse();
        observed.PasswordEncryption.Should().Be("scram-sha-256");

        // The studio's read-only transaction sets statement_timeout (and lock_timeout and idle_in_transaction_session_timeout) for
        // itself: what the application's connections start with is the server's, 0.
        observed.StatementTimeoutMs.Should().Be(0);
        observed.IdleInTransactionTimeoutMs.Should().Be(0);
        observed.IdleSessionTimeoutMs.Should().Be(0);
        observed.TransactionTimeoutMs.Should().Be(major >= 17 ? 0 : null, "transaction_timeout is PostgreSQL 17's");
        observed.SearchPath.Should().Be("\"$user\", public", "the studio pins pg_catalog inside its transaction, and the connection did not");
        observed.ClientEncoding.Should().Be("UTF8");
        observed.TimeZone.Should().NotBeNullOrWhiteSpace();
        observed.ApplicationName.Should().StartWith("ms-matrix-host-");
        observed.KnownOptionNames.Should().BeEmpty();

        observed.ReportedParameters.Should().ContainKey("server_version");
        observed.ReportedParameters["is_superuser"].Should().Be("off");

        facts.Platform.Kind.Should().Be(PostgresPlatformKind.SelfHosted);
        facts.Sessions.Should().BeNull("pooling is on, so the session statistics are not asked");
        facts.Settings.Readable.Should().BeTrue();
        facts.Settings.Rows.Single(static x => x.Keyword == "Application Name").Origin.Should().Be(ConnectionSettingOrigin.ConnectionString);
        facts.Settings.Rows.Single(static x => x.Keyword == "Password").Origin.Should().Be(ConnectionSettingOrigin.NotVisible);

        // The pool the host warmed is the only one to this host, port, database and login in the process.
        facts.Siblings.Should().NotBeNull();
        facts.Siblings!.Pools.Should().Be(1);
        facts.Siblings.SumMaxPoolSize.Should().Be(100);

        // A second pool to the same host, port, database and login is a sibling whatever its Application Name: the meter is the process's.
        await using NpgsqlDataSource second = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(server.ConnectionString(MatrixRole.App, database)) { ApplicationName = "ms-connobs-second" }.ConnectionString);
        await using NpgsqlConnection warm = await second.OpenConnectionAsync(Token);

        ConnectionFacts both = await ConnectionFacts.ReadAsync(context, Token);
        both.Siblings!.Pools.Should().Be(2);
        both.Siblings.SumMaxPoolSize.Should().Be(200);
    }

    /// <summary>
    /// A copy, not Npgsql's table: the connector empties its own (<c>NpgsqlConnector.Cleanup()</c>) when the physical connection
    /// is closed or broken. This host has <c>Pooling</c> off, so disposing the sweep's connection really closes it, and the facts
    /// still hold what the server said - which they could not if they were the connector's own table. With pooling off the
    /// session statistics are asked (C17's).
    /// </summary>
    [PostgresFact]
    public async Task The_reported_parameters_outlive_the_physical_connection_they_were_read_from()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, connection: static b => b.Pooling = false);
        (HealthCheckContext context, HealthConnection connection) = host.Context();
        await using HealthConnection _ = connection;

        ConnectionFacts facts = await ConnectionFacts.ReadAsync(context, Token);
        ConnectionObserved observed = facts.Observed!;

        observed.ReportedParameters.Should().ContainKey("server_version");

        await connection.DisposeAsync();
        await using (HealthConnection another = new(host.Resolved.Target))
        {
            await another.OpenAsync(Token);
        }

        observed.ReportedParameters.Should().ContainKey("server_version");
        observed.ReportedParameters["is_superuser"].Should().Be("off");
        observed.ReportedParameters["server_version"].Should().NotBeNullOrWhiteSpace();

        facts.Settings.Values.Pooling.Should().BeFalse();
        facts.Sessions.Should().NotBeNull("pooling is off, so the session statistics are asked");
    }

    /// <summary>Which of the option names exist as settings is the server's answer, by equality: a made-up name and a dotted one are not.</summary>
    [PostgresFact]
    public async Task The_server_says_which_option_names_are_settings()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App);
        (HealthCheckContext context, HealthConnection connection) = host.Context();
        await using HealthConnection _ = connection;

        IReadOnlyList<string[]> known = await context.Sql.QueryAsync(
            ConnectionObservedQueries.Observed(["statement_timeout", "nonsense", "work_mem", "app.secret"]),
            static reader => reader.GetFieldValue<string[]>(21),
            Token);

        known.Should().ContainSingle().Which.Should().BeEquivalentTo("statement_timeout", "work_mem");
    }

    /// <summary>
    /// A role-level <c>idle_session_timeout</c> and a role-in-database <c>statement_timeout</c> are visible through the studio's
    /// own connection, because the role and the database are the same; and the page's hint says which timeout cancels first.
    /// On an isolated server, since <c>ALTER ROLE</c> is the cluster's.
    /// </summary>
    [PostgresFact]
    public async Task A_role_level_setting_is_what_the_studios_connection_starts_with()
    {
        MatrixServer server = await matrix.IsolatedServerAsync(17, "np1-observed");
        server.SkipIfUnavailable();

        await server.ExecuteAsync(
            "alter role app set idle_session_timeout = '90s'; "
            + "alter role app set idle_in_transaction_session_timeout = '45s'; "
            + $"alter role app in database {MatrixServer.Database} set statement_timeout = '7s'",
            cancellationToken: Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App);
        (HealthCheckContext context, HealthConnection connection) = host.Context();
        await using HealthConnection _ = connection;

        ConnectionFacts facts = await ConnectionFacts.ReadAsync(context, Token);

        facts.ObservedNotRead.Should().BeNull();
        facts.Observed!.IdleSessionTimeoutMs.Should().Be(90_000);
        facts.Observed.IdleSessionTimeoutSeconds.Should().Be(90);
        facts.Observed.IdleInTransactionTimeoutMs.Should().Be(45_000);
        facts.Observed.StatementTimeoutMs.Should().Be(7_000, "not the 30 s the studio's own transaction sets");

        ConnectionHint hint = facts.Settings.Hints.Single(static x => x.Id == ConnectionHintIds.CommandTimeout);
        hint.Text.Should().Contain("statement_timeout 7 s").And.Contain("idle_in_transaction_session_timeout 45 s")
            .And.Contain("the server's statement_timeout cancels a long command first");
    }

    /// <summary>
    /// With pooling off, the session statistics are read (PostgreSQL 14 and later) and give an average session length.
    /// </summary>
    [PostgresFact]
    public async Task Session_statistics_are_read_only_when_pooling_is_off()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("np1_sessions", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, connection: static b => b.Pooling = false);
        (HealthCheckContext context, HealthConnection connection) = host.Context();
        await using HealthConnection _ = connection;

        ConnectionFacts facts = await ConnectionFacts.ReadAsync(context, Token);
        facts.Settings.Values.Pooling.Should().BeFalse();

        // A session is counted when its backend flushes its statistics, which a closed connection does on its way out.
        await StorageLive.UntilAsync(
            async () =>
            {
                facts = await ConnectionFacts.ReadAsync(context, Token);
                return facts.Sessions?.Sessions > 0;
            },
            TimeSpan.FromSeconds(30),
            "the warm-up connection's session to be counted");

        facts.Sessions!.SessionTimeMs.Should().NotBeNull();
        facts.Sessions.AverageSessionMs.Should().NotBeNull();
    }

    /// <summary>
    /// NP-DESIGN §2.6 on a live read: the real password (kept by <c>Persist Security Info</c>), an SSL password, a custom option,
    /// and <c>PGPASSWORD</c> are in the host's configuration, and no sentinel appears in the facts however they are dumped, in
    /// any statement the read ran, or in the logs at Trace - through a read and a full sweep.
    /// </summary>
    [PostgresFact]
    public async Task No_withheld_sentinel_reaches_the_facts_the_statements_or_the_logs()
    {
        const string Password = "S3NT1NEL-live-password";
        const string SslPassword = "S3NT1NEL-live-sslpassword";
        const string Option = "S3NT1NEL-live-option";
        const string EnvironmentPassword = "S3NT1NEL-live-pgpassword";

        MatrixServer server = await matrix.IsolatedServerAsync(17, "np1-sentinel");
        server.SkipIfUnavailable();

        await server.ExecuteAsync($"alter role app password '{Password}'", cancellationToken: Token);

        CapturingLoggerProvider logs = new();

        await using MatrixHost host = await MatrixHost.CreateAsync(
            server,
            MatrixRole.App,
            services: s =>
            {
                s.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
                s.AddSingleton<IProcessEnvironment>(new StatedEnvironment("PGPASSWORD", EnvironmentPassword));
            },
            connection: b =>
            {
                b.PersistSecurityInfo = true;
                b.Password = Password;
                b.SslPassword = SslPassword;
                b.Options = $"-c app.secret={Option} -c extra_float_digits=3";
            });

        (HealthCheckContext context, HealthConnection connection) = host.Context();
        await using HealthConnection _ = connection;

        ConnectionFacts facts = await ConnectionFacts.ReadAsync(context, Token);
        await host.SweepAsync(HealthSweepKind.Regular);

        facts.ObservedNotRead.Should().BeNull();
        facts.Settings.Rows.Single(static x => x.Keyword == "Password").Value.Should().Be("set");
        facts.Settings.Rows.Single(static x => x.Keyword == "SSL Password").Value.Should().Be("set");
        facts.Settings.Rows.Single(static x => x.Keyword == "Options").Value.Should().Be("-c app.secret=(value withheld) -c extra_float_digits=3");
        facts.Observed!.KnownOptionNames.Should().BeEquivalentTo("extra_float_digits");

        string dump = JsonSerializer.Serialize(facts, Json)
            + string.Join('\n', facts.Settings.Rows.Select(static x => x.Keyword + "=" + x.Value + " " + x.Note))
            + string.Join('\n', facts.Settings.Hints.Select(static x => x.Text + x.ConnectionStringFix + x.BuilderFix))
            + string.Join('\n', facts.Observed.ReportedParameters.Select(static x => x.Key + "=" + x.Value))
            + string.Join('\n', context.Sql.Statements.Select(static x => x.Sql + string.Join(',', x.ParameterNames)))
            + logs.Text();

        foreach (string sentinel in (string[]) [Password, SslPassword, Option, EnvironmentPassword])
        {
            dump.Should().NotContain(sentinel);
        }

        logs.Count.Should().BeGreaterThan(0, "the host logged at Trace, so the capture is not vacuous");
    }

    private sealed class StatedEnvironment(string name, string value) : IProcessEnvironment
    {
        public string? Get(string variable) => string.Equals(variable, name, StringComparison.Ordinal) ? value : null;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        public int Count => lines.Count;

        public string Text() => string.Join('\n', lines);

        public ILogger CreateLogger(string categoryName) => new Capture(lines, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(category + " " + logLevel + " " + formatter(state, exception) + " " + exception);
            }
        }
    }
}
