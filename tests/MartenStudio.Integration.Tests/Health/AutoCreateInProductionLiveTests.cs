using JasperFx;

using Microsoft.Extensions.Hosting;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// K01 against a real store on a real Postgres, through the studio's own services: it fires for the modes
/// that alter existing objects in Production, and is quiet for the healthy sample store in Development.
/// </summary>
public class AutoCreateInProductionLiveTests(PostgresFixture postgres)
{
    [PostgresTheory]
    [HealthFires("K01")]
    [InlineData(AutoCreate.All)]
    [InlineData(AutoCreate.CreateOrUpdate)]
    public async Task A_store_that_migrates_on_first_use_in_Production_is_a_K01_warning(AutoCreate mode)
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres, "health_k01_fires_" + mode.ToString().ToLowerInvariant(), Environments.Production, HealthLiveStores.WithAutoCreate(mode));

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "K01").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("AutoCreate", mode.ToString()));
        finding.DocsUrl.Should().EndWith("configuration.md#k01-autocreate-in-production");
        host.Logs.WarningsOrWorse.Should().BeEmpty("a finding is what the page says; the log is for failures");
    }

    [PostgresFact]
    [HealthQuiet("K01")]
    public async Task The_healthy_sample_store_in_Development_is_quiet()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_k01_quiet");

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K01");
        HealthCheckStatus status = HealthLiveHost.Status(report, "K01");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, "quiet because it looked, not because it did not run");
        status.Note.Should().Contain("Development");
    }

    [PostgresFact]
    [HealthQuiet("K01")]
    public async Task Production_with_AutoCreate_None_is_quiet()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres, "health_k01_quiet_none", Environments.Production, HealthLiveStores.WithAutoCreate(AutoCreate.None));

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K01");
        HealthLiveHost.Status(report, "K01").Note.Should().Contain("None");
    }

    /// <summary>A host that registers no <c>IHostEnvironment</c>: absent is a value, said in the status row.</summary>
    [PostgresFact]
    public async Task A_host_with_no_environment_is_quiet_and_says_it_cannot_tell()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_k01_noenv", environment: null);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "K01");
        HealthLiveHost.Status(report, "K01").Note.Should().Contain("IHostEnvironment");
        host.Logs.WarningsOrWorse.Should().BeEmpty("an absent environment is an expected state, never a warning");
    }
}
