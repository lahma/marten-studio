using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Server;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// Accepting a check, on a live server (issue #35): a host with <c>AcceptedChecks["K05"]</c> and the SQL console on with no
/// role to narrow it - the finding K05 exists for - whose Health report, badge and diagnostics API all show K05 accepted,
/// with the host's reason, and count it nowhere.
/// </summary>
public class AcceptedChecksLiveTests(PostgresVersionMatrix matrix)
{
    private const string Reason = "Database roles are managed by the platform team; the console is admin-only.";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task K05_accepted_is_shown_as_accepted_by_the_health_report_and_the_api_and_counted_by_neither()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App), options =>
        {
            options.Capabilities.RunSql = true;
            options.AcceptedChecks["k05"] = Reason;
        });

        // The check still runs, and finds what it always found.
        HealthSweep sweep = await host.SweepAsync();
        HealthFinding swept = PostureHost.Result(sweep, "K05").Findings.Should().ContainSingle().Subject;
        swept.Accepted.Should().BeTrue();
        swept.AcceptedReason.Should().Be(Reason);

        // The Health page's report: shown, accepted, with the reason, and out of the counts.
        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        IHealthService health = circuit.ServiceProvider.GetRequiredService<IHealthService>();
        StudioScope scope = host.Resolved.Scope;

        HealthReport report = await health.GetReportAsync(scope, HealthReportRequest.Regular, Token);

        report.Refusal.Should().Be(HealthRefusal.None, report.Reason);
        HealthReportFinding shown = report.Findings.Should().ContainSingle(static x => x.Code == "K05").Subject;
        shown.Accepted.Should().BeTrue();
        shown.AcceptedReason.Should().Be(Reason);

        HealthCheckStatus row = report.Checks.Single(static x => x.Code == "K05");
        row.AcceptedCount.Should().Be(1);
        row.AcceptedReason.Should().Be(Reason);
        row.FindingCount.Should().Be(0);

        // The badge reads the same sweep: with K05 left out, it holds what the other findings are and nothing of K05.
        HealthGlance? glance = await health.GlanceAsync(scope, Token);
        glance.Should().NotBeNull();
        glance.Warning.Should().Be(report.Findings.Count(static x => !x.Accepted && x.Severity == HealthSeverity.Warning));
        glance.Critical.Should().Be(report.Findings.Count(static x => !x.Accepted && x.Severity == HealthSeverity.Critical));
        report.Worst.Should().Be(report.Findings.Where(static x => !x.Accepted).Select(static x => (HealthSeverity?) x.Severity).Min(),
            "the overall state is judged without K05");
        (await health.CachedFindingsAsync(scope, ["K05"], Token)).Should().BeEmpty("no banner is drawn for an accepted finding");

        // The diagnostics API: the finding is there, marked, with the reason, in the model and in the JSON.
        IPostgresDiagnostics diagnostics = host.Services.GetRequiredService<IPostgresDiagnostics>();
        PostgresDiagnosticsSnapshot snapshot = await diagnostics.GetSnapshotAsync(new PostgresDatabaseRef("default", ""), PostgresDiagnosticsRequest.FindingsOnly, Token);

        snapshot.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Reason);
        PostgresFinding finding = snapshot.Findings.Should().ContainSingle(static x => x.Code == "K05").Subject;
        finding.Accepted.Should().BeTrue();
        finding.AcceptedReason.Should().Be(Reason);
        finding.Severity.Should().Be(PostgresSeverity.Warning);
        snapshot.Findings.Where(static x => x.Code != "K05").Should().OnlyContain(static x => !x.Accepted);

        using JsonDocument json = JsonDocument.Parse(snapshot.ToJson());
        JsonElement k05 = json.RootElement.GetProperty("findings").GetProperty("items").EnumerateArray().Single(static x => x.GetProperty("code").GetString() == "K05");
        k05.GetProperty("accepted").GetBoolean().Should().BeTrue();
        k05.GetProperty("acceptedReason").GetString().Should().Be(Reason);
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
    }

    [PostgresFact]
    public async Task Without_acceptance_the_same_host_counts_K05()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App), options => options.Capabilities.RunSql = true);

        await host.SweepAsync();

        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        IHealthService health = circuit.ServiceProvider.GetRequiredService<IHealthService>();
        StudioScope scope = host.Resolved.Scope;

        HealthReport report = await health.GetReportAsync(scope, HealthReportRequest.Regular, Token);

        report.Findings.Single(static x => x.Code == "K05").Accepted.Should().BeFalse();
        (await health.CachedFindingsAsync(scope, ["K05"], Token)).Should().ContainSingle("a finding nobody accepted is in the banner's list");
        report.Checks.Single(static x => x.Code == "K05").FindingCount.Should().Be(1);
    }
}
