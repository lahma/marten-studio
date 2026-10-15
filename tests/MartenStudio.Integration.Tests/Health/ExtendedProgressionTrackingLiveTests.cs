using MartenStudio.SampleDomain;
using MartenStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// K02 live: the sample store has async projections and, as Marten ships, extended progression tracking
/// off - so K02 fires there as an Info; a store that follows K02's own advice, and one with nothing for the
/// daemon to run, are quiet.
/// </summary>
/// <remarks>
/// The quiet case is the sample store <em>with K02's recommendation applied</em>, not the sample store as it
/// ships: <see cref="SampleStore.Configure" /> keeps Marten's defaults on purpose, so that what the advisor
/// says about the sample is what it says about a new host - K02 firing there is intended, and
/// <c>docs/health/configuration.md</c> says so.
/// </remarks>
public class ExtendedProgressionTrackingLiveTests(PostgresFixture postgres)
{
    [PostgresFact]
    [HealthFires("K02")]
    public async Task The_sample_store_as_Marten_ships_it_is_a_K02_suggestion()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_k02_fires");

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "K02").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(new HealthEvidence("Events.EnableExtendedProgressionTracking", "false"));
        finding.Evidence.Should().Contain(static x => x.Label == "Async projections and subscriptions" && x.Value.EndsWith("shards", StringComparison.Ordinal),
            "the sample domain registers two async projections, each with at least one shard");
    }

    [PostgresFact]
    [HealthQuiet("K02")]
    public async Task The_sample_store_with_extended_tracking_on_is_quiet()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres, "health_k02_quiet", store: static options => options.Events.EnableExtendedProgressionTracking = true);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K02");
        HealthLiveHost.Status(report, "K02").Outcome.Should().Be(HealthCheckOutcome.Checked);
    }

    [PostgresFact]
    [HealthQuiet("K02")]
    public async Task A_store_with_no_async_work_is_quiet()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres, "health_k02_quiet_noasync", store: static options => options.Projections.All.Clear());

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K02");
        HealthLiveHost.Status(report, "K02").Note.Should().Contain("no async projection");
    }
}
