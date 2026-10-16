using Marten.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// E08 live: a large tag-version table whose updates have stopped being heap-only, found from the statistics
/// collector's own counters - and the remedy's availability, which is the version-dependent half, asserted against
/// whatever Marten this run resolved (the floor by default, 9.50.2 with <c>-p:MartenStudioMartenVersion=9.50.2</c>).
/// </summary>
public class DcbTagVersionLiveTests(PostgresFixture postgres)
{
    private const string Table = "mt_dcb_tag_version";

    [PostgresFact]
    [HealthFires("E08")]
    public async Task A_large_tag_set_whose_updates_are_not_heap_only_is_E08()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_e08_fires",
            store: static options => ((EventGraph) options.Events).RegisterTagType<HealthTag>());

        string table = SqlIdentifier.Qualify(host.EventSchema, Table);

        // 120,000 tag values on fully packed pages, then 31,112 updates that change the primary key - which
        // PostgreSQL can never do heap-only - and 11,111 bumps of version like the ones a boundary save makes, on
        // pages with no room left for them.
        await EventHealthData.ExecuteAsync(
            postgres,
            $"insert into {table} (tag_table, tag_value, tenant_id, version) "
            + "select 'mt_event_tag_health_tag', 'v' || g, '*DEFAULT*', 0 from generate_series(1, 120000) g; "
            + $"update {table} set tag_value = tag_value || 'x' where tag_value like 'v1%'; "
            + $"update {table} set version = version + 1 where tag_value like 'v2%'; "
            + $"analyze {table}; "
            + "select pg_catalog.pg_stat_force_next_flush();");

        string updates = $"select pg_catalog.pg_stat_get_tuples_updated('{table}'::regclass)";
        await EventHealthData.WaitForAsync(
            async () => await EventHealthData.ScalarAsync(postgres, updates) >= 10_000,
            "the statistics collector to count the updates");

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E08").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Remedy!.Code.Should().Be("opts.Events.DcbTagVersionFillFactor = 70;");

        bool available = RuntimeFeature.DcbTagVersionFillFactor.IsAvailable(MartenRuntime.Current);
        finding.RemedyAvailable.Should().Be(available, "the option exists from Marten 9.47, and this run resolved {0}", MartenRuntime.Current.Describe(RuntimePackage.Marten));

        if (!available)
        {
            finding.HostRuns.Should().StartWith("Marten 9.", "an older host is told what it runs beside 'Marten 9.47+'");
        }
    }

    [PostgresFact]
    [HealthQuiet("E08")]
    public async Task The_healthy_sample_store_registers_no_tag_types()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e08_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E08");
        HealthCheckStatus status = EventHealthHost.Status(report, "E08");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("no DCB tag types");
    }

    [PostgresFact]
    [HealthQuiet("E08")]
    public async Task A_small_tag_set_is_quiet_whatever_its_updates()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_e08_small",
            store: static options => ((EventGraph) options.Events).RegisterTagType<HealthTag>());

        string table = SqlIdentifier.Qualify(host.EventSchema, Table);
        await EventHealthData.ExecuteAsync(
            postgres,
            $"insert into {table} (tag_table, tag_value, tenant_id, version) "
            + "select 'mt_event_tag_health_tag', 'v' || g, '*DEFAULT*', 0 from generate_series(1, 50) g;");

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E08");
        EventHealthHost.Status(report, "E08").Note.Should().Contain("makes no measurable difference");
    }
}
