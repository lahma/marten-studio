using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// K03 live: quiet on the healthy sample store, which has Marten 9's default of advanced async tracking on;
/// fires on one that turned it off - and on one that restored the Marten 8 defaults, which is how most stores
/// end up with it off.
/// </summary>
public class AdvancedAsyncTrackingLiveTests(PostgresFixture postgres)
{
    [PostgresFact]
    [HealthFires("K03")]
    public async Task A_store_that_turned_it_off_is_a_K03_suggestion()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres, "health_k03_fires", store: static options => options.Events.EnableAdvancedAsyncTracking = false);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "K03").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Remedy!.Code.Should().Be("opts.Events.EnableAdvancedAsyncTracking = true;");
    }

    [PostgresFact]
    [HealthFires("K03")]
    public async Task A_store_on_the_Marten_8_defaults_is_a_K03_suggestion()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres, "health_k03_fires_v8", store: static options => options.RestoreV8Defaults());

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().ContainSingle(static x => x.Code == "K03");
    }

    [PostgresFact]
    [HealthQuiet("K03")]
    public async Task The_healthy_sample_store_is_quiet()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_k03_quiet");

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K03");
        HealthLiveHost.Status(report, "K03").Outcome.Should().Be(HealthCheckOutcome.Checked);
    }
}
