using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// What the Health page and the Overview draw, read through the real services against the live sample store:
/// one shared sweep however often it is asked for, and every shipped regular check in the status list.
/// </summary>
public class HealthReportLiveTests(PostgresFixture postgres)
{
    [PostgresFact]
    public async Task The_report_lists_every_regular_check_and_is_one_sweep_for_every_reader()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_report");

        HealthReport first = await host.ReportAsync();
        HealthReport second = await host.ReportAsync();

        first.Refusal.Should().Be(HealthRefusal.None, first.Reason);

        // Tiers 0 and 1 only: a deeper check is in a report only once "Run deeper checks" has run it.
        first.Checks.Select(static x => x.Code).Should().Contain(StudioHealthChecks.Registry.All
            .Where(static x => x.Info.Tier is HealthCostTier.InProcess or HealthCostTier.Catalog)
            .Select(static x => x.Info.Code));
        first.Checks.Should().OnlyContain(static x => x.Outcome != HealthCheckOutcome.Failed, "nothing fails on the healthy sample store");
        first.MeasuredAt.Should().NotBeNull();
        first.History.Should().NotBeNullOrWhiteSpace();
        first.Cost.Should().NotBeNull("no store policy is configured, so the database-wide gate passes");

        second.MeasuredAt.Should().Be(first.MeasuredAt, "a second reader inside the interval shares the first sweep");
        host.Cache.SweepCount.Should().Be(1);
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }
}
