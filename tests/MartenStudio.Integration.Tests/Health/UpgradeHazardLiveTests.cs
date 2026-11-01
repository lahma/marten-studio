using MartenStudio.Services.Configuration;

using PostgresStudio.Services.Health;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// K09 and K10 on a live store (0.10 V1): document storage named with a dotless <c>ı</c>, which Marten 9.55 no longer writes,
/// and a schema name Postgres writes quoted, which Weasel before 9.47.1 cannot apply twice.
/// </summary>
/// <summary>A document type with a literal dotless <c>ı</c> in its name, which Marten keeps at every version.</summary>
public sealed class HealthKayıt
{
    public Guid Id { get; set; }
}

public class UpgradeHazardLiveTests(PostgresFixture postgres)
{
    private const string Dotless = "ı";

    [PostgresFact]
    [HealthFires("K09")]
    public async Task Tables_and_functions_named_with_a_dotless_i_are_found_whichever_Marten_wrote_them()
    {
        await using HealthLiveHost host = await HostAsync(postgres, "health_k09_fires", new ManualClock(Start));
        await ApplyAsync(host.Store);

        // What a tr-TR host on Marten before 9.55 left behind: an idempotency marker table with no invariant twin (the sample store declares an Invoice, so that name is not used), Item with one, and the
        // upsert function of the first.
        await ExecuteAsync(
            postgres,
            "create table health_k09_fires.\"mt_doc_" + Dotless + "dempotent\" (id uuid primary key, data jsonb not null)",
            "create table health_k09_fires.\"mt_doc_" + Dotless + "tem\" (id uuid primary key, data jsonb not null)",
            "create table health_k09_fires.\"mt_doc_item\" (id uuid primary key, data jsonb not null)",
            "create function health_k09_fires.\"mt_upsert_" + Dotless + "dempotent\"() returns void language sql as 'select 1'");

        HealthReport report = await ReportAsync(host);

        HealthReportFinding dotlessOnly = Of(report, "K09").Should().ContainSingle(x => x.Finding.Variant == "dotless-only").Subject;
        dotlessOnly.Severity.Should().Be(HealthSeverity.Warning);
        dotlessOnly.Finding.Subject.Should().Be("health_k09_fires.mt_doc_" + Dotless + "dempotent");
        dotlessOnly.Evidence.Should().Contain(new HealthEvidence("Invariant name", "mt_doc_idempotent (does not exist)"));
        dotlessOnly.Evidence.Should().Contain(x => x.Label == "Functions" && x.Value.Contains("mt_upsert_" + Dotless + "dempotent", StringComparison.Ordinal));

        HealthReportFinding both = Of(report, "K09").Should().ContainSingle(x => x.Finding.Variant == "both").Subject;
        both.Finding.Subject.Should().Be("health_k09_fires.mt_doc_" + Dotless + "tem");
        both.Evidence.Should().Contain(new HealthEvidence("Invariant name", "mt_doc_item (exists too)"));

        Of(report, "K09").Should().HaveCount(2, "the function is evidence of its table's finding, not a finding of its own");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthQuiet("K09")]
    public async Task A_store_with_no_such_name_is_quiet_on_K09()
    {
        await using HealthLiveHost host = await HostAsync(postgres, "health_k09_quiet", new ManualClock(Start), static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        HealthReport report = await ReportAsync(host);

        Of(report, "K09").Should().BeEmpty();
        report.Checks.Single(static x => x.Code == "K09").Outcome.Should().Be(HealthCheckOutcome.Checked, "quiet because it looked");
    }

    [PostgresFact]
    [HealthQuiet("K09")]
    public async Task A_document_type_with_a_literal_dotless_i_in_its_name_is_healthy_and_never_a_K09_finding()
    {
        // Marten lowers with ToLowerInvariant from 9.55, which leaves the letter alone, and the test process lowers I to i, so
        // before 9.55 too: the table is mt_doc_healthkayıt at every Marten, and renaming it would lose its documents.
        await using HealthLiveHost host = await HostAsync(postgres, "health_k09_kayit", new ManualClock(Start), static o => o.Schema.For<HealthKayıt>());
        await ApplyAsync(host.Store);

        // The table exists under the name the type declares; its upsert function is the one Marten wrote with it.
        await ExecuteAsync(postgres, "select 1 from health_k09_kayit.\"mt_doc_healthkay" + Dotless + "t\" limit 1");

        HealthReport report = await ReportAsync(host);

        Of(report, "K09").Should().BeEmpty("a table the store's own type names this way is not an upgrade hazard");
        report.Checks.Single(static x => x.Code == "K09").Outcome.Should().Be(HealthCheckOutcome.Checked);
    }

    [PostgresFact]
    public async Task While_the_host_hides_document_types_a_name_no_known_type_owns_is_counted_and_never_named()
    {
        await using HealthLiveHost host = await HostAsync(
            postgres,
            "health_k09_hidden",
            new ManualClock(Start),
            static o => o.Schema.For<HealthScanThing>(),
            static studio => studio.IsDocumentTypeVisible = static type => type != typeof(HealthIndexedThing));
        await ApplyAsync(host.Store);

        await ExecuteAsync(
            postgres,
            "create table health_k09_hidden.\"mt_doc_" + Dotless + "dempotent\" (id uuid primary key, data jsonb not null)");

        HealthReport report = await ReportAsync(host);

        HealthReportFinding finding = Of(report, "K09").Should().ContainSingle().Subject;
        finding.Finding.Subject.Should().Be("(not listed)");
        finding.Finding.Title.Should().StartWith("1 table or function is named with a dotless");

        string text = finding.Finding.Title + " " + finding.Finding.WhyItMatters + " " + finding.Finding.Caveat + " "
            + string.Join(" ", finding.Evidence.Select(static x => x.Value));
        text.Should().NotContain("dempotent").And.NotContain("mt_doc_");
    }

    /// <summary>
    /// K10's expectation is the host's own Weasel: at the floor (Weasel 9.33) a schema named with a reserved word is a finding;
    /// from Marten 9.55 (Weasel 9.47.1) it never is. One test, both ends, asserted against the version it runs on.
    /// </summary>
    [PostgresFact]
    [HealthFires("K10")]
    public async Task A_reserved_word_schema_is_a_K10_finding_below_Weasel_9_47_1_and_never_from_it()
    {
        Version weasel = MartenRuntime.Current.VersionOf(RuntimePackage.WeaselPostgresql)!;
        await ExecuteAsync(postgres, "create schema if not exists \"order\"");

        try
        {
            await using HealthLiveHost host = await HostAsync(
                postgres,
                "health_k10_fires",
                new ManualClock(Start),
                static o =>
                {
                    o.DatabaseSchemaName = "order";
                    o.Events.DatabaseSchemaName = "order";
                });

            HealthReport report = await ReportAsync(host);

            if (weasel < new Version(9, 47, 1))
            {
                HealthReportFinding finding = Of(report, "K10").Should().ContainSingle().Subject;
                finding.Severity.Should().Be(HealthSeverity.Warning);
                finding.Finding.Title.Should().Be("The schema \"order\" breaks every schema apply after the first");
                finding.Evidence.Should().Contain(new HealthEvidence("Schema", "\"order\""));
            }
            else
            {
                Of(report, "K10").Should().BeEmpty("Weasel " + weasel + " reads such a schema's indexes back");
                report.Checks.Single(static x => x.Code == "K10").Outcome.Should().Be(HealthCheckOutcome.Checked);
            }
        }
        finally
        {
            await ExecuteAsync(postgres, "drop schema if exists \"order\" cascade");
        }
    }

    [PostgresFact]
    [HealthQuiet("K10")]
    public async Task Schemas_that_need_no_quoting_are_quiet_on_K10_at_every_Weasel()
    {
        await using HealthLiveHost host = await HostAsync(postgres, "health_k10_quiet", new ManualClock(Start));

        HealthReport report = await ReportAsync(host);

        Of(report, "K10").Should().BeEmpty();
        report.Checks.Single(static x => x.Code == "K10").Outcome.Should().Be(HealthCheckOutcome.Checked, "quiet because it looked");
    }
}
