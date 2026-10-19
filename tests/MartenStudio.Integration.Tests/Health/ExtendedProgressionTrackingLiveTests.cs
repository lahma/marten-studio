using MartenStudio.SampleDomain;
using MartenStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// K02 live: a store with async projections and, as Marten ships, extended progression tracking off is a K02 Info;
/// the sample store, which turns it on, and one with nothing for the daemon to run, are quiet.
/// </summary>
/// <remarks>
/// The firing case sets the switch off itself rather than relying on the sample: <see cref="SampleStore.Configure" />
/// turns it on, so that the sample starts healthy and a demo makes it unhealthy on purpose (the landing page's
/// switches). Off is Marten's default, so that store is any new host with an async projection.
/// </remarks>
public class ExtendedProgressionTrackingLiveTests(PostgresFixture postgres)
{
    [PostgresFact]
    [HealthFires("K02")]
    public async Task A_store_on_Martens_default_is_a_K02_suggestion()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres, "health_k02_fires", store: static options => options.Events.EnableExtendedProgressionTracking = false);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "K02").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(new HealthEvidence("Events.EnableExtendedProgressionTracking", "false"));
        finding.Evidence.Should().Contain(static x => x.Label == "Async projections and subscriptions" && x.Value.EndsWith("shards", StringComparison.Ordinal),
            "the sample domain registers two async projections, each with at least one shard");
    }

    [PostgresFact]
    [HealthQuiet("K02")]
    public async Task The_sample_store_as_it_ships_is_quiet()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_k02_quiet");

        host.Store.Options.Events.EnableExtendedProgressionTracking.Should().BeTrue("the sample turns it on");

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K02");
        HealthLiveHost.Status(report, "K02").Outcome.Should().Be(HealthCheckOutcome.Checked);
    }

    [PostgresFact]
    [HealthQuiet("K02")]
    public async Task A_store_with_no_async_work_is_quiet()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_k02_quiet_noasync",
            store: static options =>
            {
                options.Events.EnableExtendedProgressionTracking = false;
                options.Projections.All.Clear();
            });

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K02");
        HealthLiveHost.Status(report, "K02").Note.Should().Contain("no async projection");
    }
}
