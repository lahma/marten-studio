using PostgresStudio.Services.Health;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// T17 on a live store: a table named like a document table that no store declares is counted - never named, since it
/// may be a type this process has not loaded yet - and a store whose every <c>mt_doc_</c> table is its own is quiet.
/// </summary>
public class OrphanedDocumentTablesLiveTests(PostgresFixture postgres)
{
    [PostgresFact]
    [HealthFires("T17")]
    public async Task A_document_named_table_no_store_declares_is_counted_by_T17_and_never_named()
    {
        await using HealthLiveHost host = await HostAsync(postgres, "health_t17_fires", new ManualClock(Start), static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        await ExecuteAsync(
            postgres,
            "create table health_t17_fires.mt_doc_strayedthing (id uuid primary key, data jsonb not null)",
            "insert into health_t17_fires.mt_doc_strayedthing select gen_random_uuid(), '{}'::jsonb from generate_series(1, 200)",
            "analyze health_t17_fires.mt_doc_strayedthing");

        HealthReport report = await ReportAsync(host);

        HealthReportFinding finding = Of(report, "T17").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Title.Should().StartWith("1 table in the store's schemas is named like document tables no store here declares");
        finding.Evidence.Should().Contain(new HealthEvidence("Tables", "1"));

        string text = finding.Finding.Title + " " + finding.Finding.WhyItMatters + " " + finding.Finding.Caveat + " " + finding.Finding.Subject + " "
            + string.Join(" ", finding.Evidence.Select(static x => x.Value));
        text.Should().NotContainEquivalentOf("strayed", "it may be a document type this process has not loaded yet");
        text.Should().NotContainEquivalentOf("healthscanthing", "the store's own table is declared");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthQuiet("T17")]
    public async Task A_store_whose_document_tables_are_all_declared_is_quiet_on_T17()
    {
        await using HealthLiveHost host = await HostAsync(
            postgres,
            "health_t17_quiet",
            new ManualClock(Start),
            static o =>
            {
                o.Schema.For<HealthScanThing>();
                o.Schema.For<HealthIndexedThing>();
            });
        await ApplyAsync(host.Store);

        HealthReport report = await ReportAsync(host);

        Of(report, "T17").Should().BeEmpty();
        report.Checks.Single(static x => x.Code == "T17").Outcome.Should().Be(HealthCheckOutcome.Checked, "quiet because it looked");
    }
}
