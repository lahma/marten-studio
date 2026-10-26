using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests;

/// <summary>
/// NPG10, live: Database › Sessions' pool panel and C01 read a real pool of a real data source, through
/// <c>AddPostgresStudio()</c> and the application's own unkeyed <see cref="NpgsqlDataSource" />, at the Npgsql this
/// project restored - 9.0.4 in the whole-solution pass, the latest-tested 10.x in the Npgsql pass over
/// <c>postgres-studio.slnf</c>.
/// </summary>
/// <remarks>
/// At Npgsql 10, reading the older instrument names alone, both came back empty and nothing said why: the panel's pool
/// "not matched to this database", and C01 unavailable because the pool "is not among the pools Npgsql reports in this
/// process". The container is this class's own (<see cref="DockerPostgres" />), so its <c>postgres</c> database has no
/// other application's sessions in it.
/// </remarks>
public sealed class NpgsqlPoolLiveTests(DockerPostgres postgres) : IClassFixture<DockerPostgres>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [DockerFact]
    public async Task The_Sessions_pool_panel_and_C01_read_a_real_pool()
    {
        string npgsql = typeof(NpgsqlDataSource).Assembly.GetName().Version!.ToString(3);
        TestContext.Current.TestOutputHelper?.WriteLine("Npgsql " + npgsql);

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
            {
                MaxPoolSize = 5,
                ApplicationName = "PostgresStudio.Tests.npg10",
            }.ConnectionString);

        TestAuthenticationStateProvider visitor = new();
        visitor.SignIn("operator");

        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider>(visitor);
        services.AddSingleton(source);
        services.AddPostgresStudio();

        await using ServiceProvider provider = services.BuildServiceProvider();
        SessionPoolObserver observer = provider.GetRequiredService<SessionPoolObserver>();

        // Two of the pool's five lent out to the application, and held while the studio reads.
        await using NpgsqlConnection first = await source.OpenConnectionAsync(Token);
        await using NpgsqlConnection second = await source.OpenConnectionAsync(Token);

        // The panel: "This process's pool" on Database › Sessions, read while the page's lease is held.
        await using (AsyncServiceScope circuit = provider.CreateAsyncScope())
        {
            using SessionPoolLease lease = observer.Lease();

            SessionActivitySummary summary = await circuit.ServiceProvider.GetRequiredService<ISessionActivityService>()
                .GetSummaryAsync(Scope, Token);

            summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
            summary.SessionCount.Should().BeGreaterThanOrEqualTo(2, "the two held connections are sessions of this database");
            summary.Pool.Should().Be(
                new SessionPoolSummary(true, 2, summary.Pool?.Idle, 5, true),
                "Npgsql " + npgsql + " reported the pool and it was found by its connection string; the summary's own read gave "
                + "its connection back, so the two held are the two in use");
        }

        // C01, as the Health page's report runs it.
        await using (AsyncServiceScope circuit = provider.CreateAsyncScope())
        {
            HealthReport report = await circuit.ServiceProvider.GetRequiredService<IHealthService>()
                .GetReportAsync(Scope, HealthReportRequest.Regular, Token);

            report.Refusal.Should().Be(HealthRefusal.None, report.Reason);

            HealthCheckStatus c01 = report.Checks.Should().ContainSingle(static x => x.Code == "C01").Subject;
            c01.Outcome.Should().Be(HealthCheckOutcome.Checked, c01.Problem);
            c01.Note.Should().MatchRegex(
                "^[23] of 5 connections of this process's pool are in use\\.$",
                "the two held, and the sweep's own connection when one of its checks has opened it");
            report.Findings.Should().NotContain(static x => x.Code == "C01", "two or three of five is room in the pool");
        }

        observer.IsListening.Should().BeFalse("the listener lives only while a sweep or a page holds a lease");
    }
}
