using MartenStudio.Sample.Generation;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The sample starts healthy: a demo turns it unhealthy on purpose (the landing page's switches), so the store as the
/// sample host configures it has nothing to say for itself in the configuration area.
/// </summary>
/// <remarks>
/// <para>
/// The store is the sample host's own shape - <c>SampleStore.Configure</c> plus what the switches declare on it
/// (<see cref="UnhealthyDemoStore.Configure" />) - in Development, on the suite's Testcontainers Postgres, which is the
/// kind of server the sample's own throwaway one is.
/// </para>
/// <para>
/// Stricter than "no Warning or Critical": K02 and K03 are Infos, and a store that starts healthy has none of them
/// either. Every finding the advisor does show at start-up is written to the test's output - the server's
/// (S02's <c>synchronous_commit</c> Info on a Testcontainers server) included - so a run says what a demo opens on.
/// </para>
/// <para>
/// One server finding is asserted, on purpose (W5-S2): S04's Info, because the sample's store logs in to its throwaway
/// server as that server's superuser - true of the demo, and Info because it is Development.
/// </para>
/// </remarks>
public class SampleBaselineLiveTests(PostgresFixture postgres)
{
    private static readonly string[] Configuration = ["K01", "K02", "K03", "K04"];

    [PostgresFact]
    [HealthQuiet("K02")]
    [HealthQuiet("K03")]
    public async Task The_sample_store_starts_with_no_configuration_finding_at_all()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_sample_baseline", store: UnhealthyDemoStore.Configure);

        HealthReport report = await host.ReportAsync();

        foreach (HealthReportFinding finding in report.Findings)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(finding.Severity + " " + finding.Code + ": " + finding.Finding.Title);
        }

        report.Findings.Should().NotContain(x => Configuration.Contains(x.Code), "the sample starts healthy, and is made unhealthy on purpose");

        foreach (string code in Configuration)
        {
            HealthLiveHost.Status(report, code).Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} has to run for its silence to mean anything", code);
        }

        // W5-S2, deliberately: the sample's throwaway server, like the suite's, is a Testcontainers Postgres the store logs in
        // to as its superuser, so S04 says so - as Info, because this is Development, where that is a reasonable choice.
        HealthReportFinding superuser = report.Findings.Should().ContainSingle(x => x.Code == "S04").Subject;
        superuser.Severity.Should().Be(HealthSeverity.Info);
        superuser.Finding.Variant.Should().Be("superuser");
    }
}
