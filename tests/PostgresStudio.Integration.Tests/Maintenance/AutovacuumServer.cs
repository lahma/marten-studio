using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Maintenance;

/// <summary>
/// A PostgreSQL server of one test's own, on one of the version matrix's images, with autovacuum woken every second
/// (<c>autovacuum_naptime = 1</c>) - so a table past its trigger is vacuumed within a second or two, and a test that keeps
/// autovacuum off one waits on a condition, never on the launcher's minute.
/// </summary>
/// <remarks>
/// <para>
/// <b>One per test, disposed at its end.</b> Each test starts the server it needs - its settings are the test's alone - and
/// disposes it before the next starts, so the class costs one small container at a time and no WAL beyond what its tables
/// write. Never reused: it ignores <c>MARTENSTUDIO_PG_REUSE</c>.
/// </para>
/// <para>
/// <b>D19's rule</b>, as <see cref="MatrixServer" /> applies it: an image that cannot be pulled or started skips the test
/// locally, with the <c>docker pull</c> line in the reason, and fails it on CI.
/// </para>
/// </remarks>
internal sealed class AutovacuumServer : IAsyncDisposable
{
    private static int started;

    private readonly PostgreSqlContainer container;

    private AutovacuumServer(MatrixVersion version, PostgreSqlContainer container, int versionNumber)
    {
        Version = version;
        this.container = container;
        VersionNumber = versionNumber;
    }

    /// <summary>The leg.</summary>
    public MatrixVersion Version { get; }

    /// <summary><c>server_version_num</c>.</summary>
    public int VersionNumber { get; }

    /// <summary>The superuser's connection string to <paramref name="database" />, unpooled so a closed session is gone.</summary>
    public string ConnectionString(string database = "postgres", string? user = null, string? password = null)
    {
        NpgsqlConnectionStringBuilder builder = new(container.GetConnectionString())
        {
            Database = database,
            Pooling = false,
            ApplicationName = "ms-autovacuum-test",
        };

        if (user is not null)
        {
            builder.Username = user;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Starts a server on <paramref name="major" />'s matrix image with <c>autovacuum_naptime = 1</c> and
    /// <paramref name="settings" /> (<c>name=value</c>) on its command line.
    /// </summary>
    public static async Task<AutovacuumServer> StartAsync(int major, params string[] settings)
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        MatrixVersion version = PostgresVersionMatrix.Versions.Single(x => x.Major == major);
        string name = "ms-autovacuum-" + major.ToString(CultureInfo.InvariantCulture) + "-" + PostgresVersionMatrix.RunId + "-"
            + Interlocked.Increment(ref started).ToString(CultureInfo.InvariantCulture);

        PostgreSqlBuilder builder = new PostgreSqlBuilder(version.Image)
            .WithReuse(false)
            .WithName(name)
            .WithLabel("ms-autovacuum-run", PostgresVersionMatrix.RunId)
            .WithCommand("-c", "autovacuum_naptime=1");

        foreach (string setting in settings)
        {
            builder = builder.WithCommand("-c", setting);
        }

        PostgreSqlContainer container = builder.Build();

        try
        {
            await container.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration && exception is not OperationCanceledException)
        {
            await container.DisposeAsync();
            Assert.Skip("PostgreSQL " + major.ToString(CultureInfo.InvariantCulture) + "'s autovacuum server could not start " + version.Image
                + " here (" + exception.GetType().Name + ": " + exception.Message + "); `docker pull " + version.Image + "` makes it run. "
                + "On CI this is a failure, not a skip.");
            throw;
        }

        AutovacuumServer server = new(version, container, 0);
        int number = await server.ScalarAsync<int>("select pg_catalog.current_setting('server_version_num')::int4");
        return new AutovacuumServer(version, container, number);
    }

    /// <summary>
    /// Runs <paramref name="statements" /> as the superuser on one connection of the test's own, then makes its statistics
    /// reach the server before it closes (<c>pg_stat_force_next_flush()</c>, PostgreSQL 15 and later).
    /// </summary>
    public async Task ExecuteAsync(params string[] statements)
    {
        await using NpgsqlConnection connection = await OpenAsync();

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await FlushAsync(connection);
    }

    /// <summary>Makes <paramref name="connection" />'s pending statistics reach the server at its next idle moment.</summary>
    public async Task FlushAsync(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (VersionNumber >= 150_000)
        {
            await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
            await flush.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>An open superuser connection to <paramref name="database" />.</summary>
    public async Task<NpgsqlConnection> OpenAsync(string database = "postgres")
    {
        NpgsqlConnection connection = new(ConnectionString(database));

        try
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>One scalar, as the superuser.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);

        foreach ((string parameter, object value) in parameters)
        {
            command.Parameters.AddWithValue(parameter, value);
        }

        return (T) (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// A Postgres Studio host over this server's <c>postgres</c> database, as <paramref name="user" /> (the superuser when
    /// <see langword="null" />), whose browsable schema is <paramref name="schema" />, on <paramref name="clock" />.
    /// </summary>
    public StudioHost Host(string schema, ManualClock clock, string? user = null, string? password = null, Action<PostgresStudioOptions>? studio = null)
    {
        string connectionString = ConnectionString("postgres", user, password);

        return StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
                services.AddSingleton<TimeProvider>(clock);
            },
            options =>
            {
                options.BrowsableSchemas.Add(schema);
                studio?.Invoke(options);
            });
    }

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

    /// <summary>The visitor's report of the regular checks, through a circuit of its own.</summary>
    public static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>()
            .GetReportAsync(new StudioScope("default", "default", null), HealthReportRequest.Regular, TestContext.Current.CancellationToken));

    /// <summary>One check's status row.</summary>
    public static HealthCheckStatus Status(HealthReport report, string code) =>
        report.Checks.Single(x => string.Equals(x.Code, code, StringComparison.Ordinal));

    /// <summary>The findings of one code about one table.</summary>
    public static IReadOnlyList<HealthReportFinding> About(HealthReport report, string code, string tableKey) =>
        [.. report.Findings.Where(x => x.Code == code
            && (x.Finding.Subject == tableKey || x.Finding.Subject?.StartsWith(tableKey + ".", StringComparison.Ordinal) == true))];

    /// <inheritdoc />
    public ValueTask DisposeAsync() => container.DisposeAsync();
}
