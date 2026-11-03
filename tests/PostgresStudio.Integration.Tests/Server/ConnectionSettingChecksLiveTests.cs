using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// 1.1-NP2 on a real server: S12 (a credential readable from configuration) and S13 (driver settings that copy values into logs
/// or exceptions, in Production) through the studio's own sweep, with the configuration they judge held by a host whose
/// environment the test states. They are configuration facts, so the proof is that the sweep reads them and finds what the host
/// set - and, for NP-DESIGN §2.6, that the real password, an SSL password and <c>PGPASSWORD</c> reach nothing a finding, the
/// diagnostics API, the health check or a Trace log can show. On a server of its own, since the role's password is changed.
/// </summary>
public class ConnectionSettingChecksLiveTests(PostgresVersionMatrix matrix)
{
    private const string Password = "S3NT1NEL-live-password";
    private const string SslPassword = "S3NT1NEL-live-sslpassword";
    private const string EnvironmentPassword = "S3NT1NEL-live-pgpassword";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<MatrixServer> ServerAsync()
    {
        MatrixServer server = await matrix.IsolatedServerAsync(17, "np2-settings");
        server.SkipIfUnavailable();

        await server.ExecuteAsync($"alter role app password '{Password}'", cancellationToken: Token);
        return server;
    }

    private static Action<IServiceCollection> Host(string environment, string? pgPassword = null, ILoggerProvider? logs = null) => services =>
    {
        services.AddSingleton<IHostEnvironment>(new Environment(environment));

        if (pgPassword is not null)
        {
            services.AddSingleton<IProcessEnvironment>(new StatedEnvironment("PGPASSWORD", pgPassword));
        }

        if (logs is not null)
        {
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        }
    };

    /// <summary>
    /// Persist Security Info with a password, an SSL Password and PGPASSWORD: the first two fire (PGPASSWORD is then ignored, since
    /// the visible connection string has a password of its own). With Persist Security Info off the password is not visible, and
    /// PGPASSWORD fires instead. A clean data source is quiet.
    /// </summary>
    [PostgresFact]
    [HealthFires("S12")]
    [HealthQuiet("S12")]
    public async Task S12_finds_the_credentials_the_host_keeps_readable_and_is_quiet_without_them()
    {
        MatrixServer server = await ServerAsync();

        await using (MatrixHost host = await MatrixHost.CreateAsync(
            server,
            MatrixRole.App,
            services: Host("Production", EnvironmentPassword),
            connection: static b =>
            {
                b.Password = Password;
                b.PersistSecurityInfo = true;
                b.SslPassword = SslPassword;
            }))
        {
            HealthCheckResult result = PostureHost.Result(await host.SweepAsync(HealthSweepKind.Regular), "S12");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Select(static x => x.Key).Should().Equal("S12|persist-security-info", "S12|ssl-password");
            result.Findings.Should().OnlyContain(static x => x.Severity == HealthSeverity.Warning);
        }

        await using (MatrixHost hidden = await MatrixHost.CreateAsync(
            server,
            MatrixRole.App,
            services: Host("Production", EnvironmentPassword),
            connection: static b => b.Password = Password))
        {
            HealthCheckResult result = PostureHost.Result(await hidden.SweepAsync(HealthSweepKind.Regular), "S12");

            result.Findings.Should().ContainSingle().Which.Key.Should().Be("S12|pgpassword-env");
            result.Findings[0].Severity.Should().Be(HealthSeverity.Info);
        }

        await using MatrixHost clean = await MatrixHost.CreateAsync(server, MatrixRole.App, services: Host("Production"), connection: static b => b.Password = Password);
        HealthCheckResult quiet = PostureHost.Result(await clean.SweepAsync(HealthSweepKind.Regular), "S12");

        quiet.Outcome.Should().Be(HealthCheckOutcome.Checked, quiet.Problem?.Reason);
        quiet.Findings.Should().BeEmpty();
        quiet.Note.Should().Contain("No credential is kept");
    }

    /// <summary>
    /// Log Parameters and Include Error Detail in Production fire at Info; the same data source in Development is quiet, and so is
    /// Production without them.
    /// </summary>
    [PostgresFact]
    [HealthFires("S13")]
    [HealthQuiet("S13")]
    public async Task S13_fires_in_Production_for_settings_that_copy_values_and_is_quiet_elsewhere()
    {
        MatrixServer server = await ServerAsync();

        static void Settings(Npgsql.NpgsqlConnectionStringBuilder b)
        {
            b.Password = Password;
            b.LogParameters = true;
            b.IncludeErrorDetail = true;
        }

        await using (MatrixHost production = await MatrixHost.CreateAsync(server, MatrixRole.App, services: Host("Production"), connection: Settings))
        {
            HealthCheckResult result = PostureHost.Result(await production.SweepAsync(HealthSweepKind.Regular), "S13");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Select(static x => x.Key).Should().Equal("S13|log-parameters", "S13|include-error-detail");
            result.Findings.Should().OnlyContain(static x => x.Severity == HealthSeverity.Info);
        }

        await using (MatrixHost development = await MatrixHost.CreateAsync(server, MatrixRole.App, services: Host("Development"), connection: Settings))
        {
            HealthCheckResult result = PostureHost.Result(await development.SweepAsync(HealthSweepKind.Regular), "S13");

            result.Findings.Should().BeEmpty();
            result.Note.Should().Contain("Production only");
        }

        await using MatrixHost off = await MatrixHost.CreateAsync(server, MatrixRole.App, services: Host("Production"), connection: static b => b.Password = Password);
        HealthCheckResult quiet = PostureHost.Result(await off.SweepAsync(HealthSweepKind.Regular), "S13");

        quiet.Findings.Should().BeEmpty();
        quiet.Note.Should().Contain("are off");
    }

    /// <summary>
    /// NP-DESIGN §2.6 through a full sweep: the role's real password (kept by Persist Security Info), an SSL password and
    /// PGPASSWORD are configured, and none of them is in any finding, in a check's note or problem, in a statement a check ran, in
    /// the diagnostics API's snapshot serialized, in what the health check exports, or in the logs at Trace.
    /// </summary>
    [PostgresFact]
    public async Task No_withheld_sentinel_reaches_a_finding_the_diagnostics_api_the_health_export_or_the_logs()
    {
        MatrixServer server = await ServerAsync();
        CapturingLoggerProvider logs = new();

        await using MatrixHost host = await MatrixHost.CreateAsync(
            server,
            MatrixRole.App,
            services: Host("Production", EnvironmentPassword, logs),
            connection: static b =>
            {
                b.Password = Password;
                b.PersistSecurityInfo = true;
                b.SslPassword = SslPassword;
                b.LogParameters = true;
                b.IncludeErrorDetail = true;
            });

        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);
        string[] mine = ["C16", "C17", "C18", "S11", "S12", "S13"];

        sweep.Results.Where(x => mine.Contains(x.Check.Code, StringComparer.Ordinal)).Should().HaveCount(6);
        PostureHost.Result(sweep, "S12").Findings.Should().NotBeEmpty("the dump below is not vacuous");

        IPostgresDiagnostics diagnostics = host.Services.GetRequiredService<IPostgresDiagnostics>();
        PostgresDiagnosticsSnapshot snapshot = await diagnostics.GetSnapshotAsync(new PostgresDatabaseRef("default", string.Empty), PostgresDiagnosticsRequest.FindingsOnly, Token);

        snapshot.Findings.Should().Contain(static x => x.Code == "S12", "the API returns the same findings, and the dump below covers them");

        string dump = string.Join(
            '\n',
            sweep.Results.SelectMany(static result => new[] { result.Note, result.Problem?.Reason, result.Problem?.Detail }
                .Concat(result.Statements.Select(static x => x.Sql + string.Join(',', x.ParameterNames)))
                .Concat(result.Findings.SelectMany(static x => new[]
                {
                    x.Title, x.WhyItMatters, x.Caveat, x.Subject, x.Variant, x.Remedy?.Code, x.Remedy?.Explanation, string.Join('\n', x.Evidence.Select(static e => e.Label + e.Value)),
                })))
                .Where(static x => x is not null))
            + "\n" + JsonSerializer.Serialize(snapshot)
            + "\n" + HealthExportSummary(sweep)
            + "\n" + logs.Text();

        foreach (string sentinel in (string[]) [Password, SslPassword, EnvironmentPassword])
        {
            dump.Should().NotContain(sentinel);
        }

        logs.Count.Should().BeGreaterThan(0, "the host logged at Trace, so the capture is not vacuous");
    }

    private static string HealthExportSummary(HealthSweep sweep)
    {
        PostgresStudio.Services.Health.Export.HealthExportSummary summary = PostgresStudio.Services.Health.Export.HealthExportSummary.From(DateTimeOffset.UtcNow, [sweep]);

        return summary.Describe(TimeSpan.FromSeconds(60)) + "\n" + JsonSerializer.Serialize(summary.ToData());
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "PostgresStudio.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
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
