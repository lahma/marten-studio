using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// SEC2 M1: a status row never says "checked, nothing found" when findings were only withheld. The review's scenario:
/// an operator the store policy allows tenant <c>acme</c> only, while C04, C06 and T14 have findings behind the
/// database-wide gate - the notice said "3 findings not shown" and every row said "checked, nothing found".
/// </summary>
/// <remarks>
/// The three checks are scripted under the real codes the scenario names (the registry holds them beside nothing);
/// what is under test is how the service and the page word a gate, not what the checks read. T90 is a database-wide
/// check that finds nothing, and K90 an ungated one that finds nothing: the first proves a row short of its gate does
/// not say whether there was anything to find, the second that an ungated row still says "nothing found" truthfully.
/// </remarks>
public class HealthWithheldStatusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Type[] Checks =
    [
        typeof(ScriptedIdleInTransactionCheck),
        typeof(ScriptedLockWaitCheck),
        typeof(ScriptedHorizonCheck),
        typeof(ScriptedCatalogCheck),
        typeof(ScriptedConfigurationCheck),
    ];

    private static readonly string[] DatabaseWide = ["C04", "C06", "T14", "T90"];

    private static HealthHarness TenantScenario(Action<MartenStudioOptions>? studio = null)
    {
        HealthHarness harness = HealthHarness.Create(
            checks: Checks,
            studio: options =>
            {
                options.KnownTenantIds.Add("acme");
                studio?.Invoke(options);
            },
            policies: true);

        harness.Script.Finds("C04", HealthSeverity.Warning, "A session is idle in transaction");
        harness.Script.Finds("C06", HealthSeverity.Critical, "A blocking chain");
        harness.Script.Finds("T14", HealthSeverity.Warning, "Vacuum is held back");

        // The operator: allowed tenant acme, and not the database as a whole.
        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");

        return harness;
    }

    [Fact]
    public async Task Short_of_the_database_gate_every_database_wide_row_says_its_findings_are_not_shown_whether_or_not_there_are_any()
    {
        await using HealthHarness harness = TenantScenario();

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope with { TenantId = "acme" }, HealthReportRequest.Regular, Token);

        report.Refusal.Should().Be(HealthRefusal.None);
        report.Findings.Should().BeEmpty();
        report.WithheldCount.Should().Be(3, "C04, C06 and T14 each found one");

        foreach (string code in DatabaseWide)
        {
            HealthCheckStatus row = report.Checks.Single(x => x.Code == code);

            row.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} ran", code);
            row.FindingCount.Should().Be(0, "{0} shows nothing to this visitor", code);
            row.FindingsWithheld.Should().BeTrue("{0} is behind a gate this visitor is short of", code);
            HealthPresentation.OutcomeLabel(row.Outcome, row.FindingCount, row.FindingsWithheld)
                .Should().Be("checked, findings not shown to you", "{0}'s row does not say whether it found anything", code);
        }

        HealthCheckStatus ungated = report.Checks.Single(static x => x.Code == "K90");
        ungated.FindingsWithheld.Should().BeFalse();
        HealthPresentation.OutcomeLabel(ungated.Outcome, ungated.FindingCount, ungated.FindingsWithheld)
            .Should().Be("checked, nothing found", "an ungated check that found nothing may say so");
    }

    /// <summary>The quiet half: through the gate the same rows count what they found, and none is marked.</summary>
    [Fact]
    public async Task Through_the_database_gate_the_same_rows_count_their_findings_and_none_is_marked()
    {
        await using HealthHarness harness = TenantScenario();
        harness.Policies.Allow(static _ => true);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.WithheldCount.Should().Be(0);
        report.Checks.Should().OnlyContain(static x => !x.FindingsWithheld);
        report.Checks.Where(static x => x.Code is "C04" or "C06" or "T14").Should().OnlyContain(static x => x.FindingCount == 1);

        HealthCheckStatus quiet = report.Checks.Single(static x => x.Code == "T90");
        HealthPresentation.OutcomeLabel(quiet.Outcome, quiet.FindingCount, quiet.FindingsWithheld).Should().Be("checked, nothing found");

        HealthCheckStatus found = report.Checks.Single(static x => x.Code == "C06");
        HealthPresentation.OutcomeLabel(found.Outcome, found.FindingCount, found.FindingsWithheld).Should().Be("checked, 1 finding");
    }

    /// <summary>
    /// A passed check whose findings were withheld one by one: a finding about a tenant needs the activity gate, which a
    /// default studio is short of, so the row says how many it shows and that others are not shown.
    /// </summary>
    [Fact]
    public async Task A_passed_check_that_withholds_a_tenants_finding_says_how_many_are_shown_and_that_others_are_not()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Checks);
        harness.Script.Judge["K90"] = static (_, _) => HealthVerdict.Of(
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Warning, Title = "About the store", WhyItMatters = "Scripted.", Subject = "store" },
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Warning, Title = "About globex", WhyItMatters = "Scripted.", Subject = "globex", TenantId = "globex" });

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        HealthCheckStatus row = report.Checks.Single(static x => x.Code == "K90");
        row.FindingCount.Should().Be(1);
        row.FindingsWithheld.Should().BeTrue();
        HealthPresentation.OutcomeLabel(row.Outcome, row.FindingCount, row.FindingsWithheld)
            .Should().Be("checked, 1 shown, others not shown to you");

        report.WithheldCount.Should().Be(1);
        report.Findings.Select(static x => x.Finding.Title).Should().Equal("About the store");
    }

    /// <summary>The other half of a tenant finding's gate: through the activity gate, another tenant's finding is still withheld.</summary>
    [Fact]
    public async Task Another_tenants_finding_is_withheld_one_by_one_and_the_row_says_so()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: Checks,
            studio: static options => options.Capabilities.ViewDatabaseActivity = true,
            policies: true);
        harness.Script.Judge["K90"] = static (_, _) => HealthVerdict.Of(
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Warning, Title = "About acme", WhyItMatters = "Scripted.", Subject = "acme", TenantId = "acme" },
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Warning, Title = "About globex", WhyItMatters = "Scripted.", Subject = "globex", TenantId = "globex" });
        harness.Policies.Allow(static resource => resource.TenantId != "globex");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        HealthCheckStatus row = report.Checks.Single(static x => x.Code == "K90");
        HealthPresentation.OutcomeLabel(row.Outcome, row.FindingCount, row.FindingsWithheld)
            .Should().Be("checked, 1 shown, others not shown to you");

        // And every finding shown: nothing is marked.
        harness.Policies.Allow(static _ => true);
        HealthReport whole = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        HealthCheckStatus all = whole.Checks.Single(static x => x.Code == "K90");
        HealthPresentation.OutcomeLabel(all.Outcome, all.FindingCount, all.FindingsWithheld).Should().Be("checked, 2 findings");
    }

    /// <summary>The badge's glance knows how many it leaves out - the notice's number - and counts none of them.</summary>
    [Fact]
    public async Task The_glance_counts_what_this_visitor_may_see_and_says_how_many_it_leaves_out()
    {
        await using HealthHarness harness = TenantScenario();
        StudioScope tenant = harness.Scope with { TenantId = "acme" };

        await harness.Health.GetReportAsync(tenant, HealthReportRequest.Regular, Token);

        HealthGlance glance = (await harness.Health.GlanceAsync(tenant, Token))!;

        glance.Critical.Should().Be(0, "C06's Critical is behind the database-wide gate");
        glance.Warning.Should().Be(0);
        glance.Worst.Should().BeNull("a badge is never coloured by findings the visitor may not see");
        glance.Withheld.Should().Be(3);

        harness.Policies.Allow(static _ => true);
        HealthGlance whole = (await harness.Health.GlanceAsync(harness.Scope, Token))!;

        whole.Critical.Should().Be(1);
        whole.Warning.Should().Be(2);
        whole.Withheld.Should().Be(0);
    }

    /// <summary>
    /// SEC2-fix item 4: whether a check wrote a note says something of what it saw - T03, S02, R01 and D04 write one only
    /// on some sweeps - so short of the gate a row with a note and a row without one read exactly alike.
    /// </summary>
    [Fact]
    public async Task Short_of_the_gate_a_row_does_not_say_whether_its_check_wrote_a_note()
    {
        await using HealthHarness harness = TenantScenario();
        harness.Script.Judge["C04"] = static (_, _) => HealthVerdict.Quiet("3 sessions of role reporting are idle");
        harness.Script.Judge["C06"] = static (_, _) => HealthVerdict.Quiet(note: null);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope with { TenantId = "acme" }, HealthReportRequest.Regular, Token);

        HealthCheckStatus noted = report.Checks.Single(static x => x.Code == "C04");
        HealthCheckStatus silent = report.Checks.Single(static x => x.Code == "C06");

        noted.Note.Should().BeNull("not the note, and not a \"withheld\" in its place");
        (noted with { Code = silent.Code, Title = silent.Title, DocsUrl = silent.DocsUrl })
            .Should().BeEquivalentTo(silent, "a row short of its gate says nothing its check's sweep decided");

        // The quiet half: through the gate the note is the check's, and shown.
        harness.Policies.Allow(static _ => true);
        HealthReport whole = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        whole.Checks.Single(static x => x.Code == "C04").Note.Should().Be("3 sessions of role reporting are idle");
        whole.Checks.Single(static x => x.Code == "C06").Note.Should().BeNull();
    }

    /// <summary>
    /// SEC2-fix item 3: the glance's withheld count is the last regular sweep's. A recent deeper run's withheld findings
    /// are in the Health page's notice, and not in the badge's tooltip.
    /// </summary>
    [Fact]
    public async Task The_glance_counts_what_the_last_regular_sweep_withholds_and_the_report_a_recent_deeper_run_as_well()
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static options => options.KnownTenantIds.Add("acme"), policies: true);
        harness.Script.Finds("T90", HealthSeverity.Warning, "Database-wide finding");
        harness.Script.Finds("E90", HealthSeverity.Warning, "Deeper finding");

        // Somebody through the database-wide gate runs the deeper checks.
        await harness.Health.GetReportAsync(harness.Scope, new HealthReportRequest(Deeper: HealthDeeperChecks.Run), Token);

        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");
        StudioScope tenant = harness.Scope with { TenantId = "acme" };

        HealthReport report = await harness.Health.GetReportAsync(tenant, HealthReportRequest.Regular, Token);
        HealthGlance glance = (await harness.Health.GlanceAsync(tenant, Token))!;

        report.WithheldCount.Should().Be(2, "T90 from the regular sweep and E90 from the recent deeper run");
        glance.Withheld.Should().Be(1, "the badge reads the last regular sweep only");
    }

    [Theory]
    [InlineData(nameof(HealthCheckOutcome.Checked), 0, false, "checked, nothing found")]
    [InlineData(nameof(HealthCheckOutcome.Checked), 1, false, "checked, 1 finding")]
    [InlineData(nameof(HealthCheckOutcome.Checked), 3, false, "checked, 3 findings")]
    [InlineData(nameof(HealthCheckOutcome.Checked), 0, true, "checked, findings not shown to you")]
    [InlineData(nameof(HealthCheckOutcome.Checked), 2, true, "checked, 2 shown, others not shown to you")]
    [InlineData(nameof(HealthCheckOutcome.NotApplicable), 0, true, "not applicable on this host")]
    [InlineData(nameof(HealthCheckOutcome.Failed), 0, true, "could not check")]
    public void An_outcome_reads_nothing_found_only_when_nothing_was_withheld(string outcome, int findings, bool withheld, string expected)
    {
        HealthPresentation.OutcomeLabel(Enum.Parse<HealthCheckOutcome>(outcome), findings, withheld).Should().Be(expected);
    }

    [HealthCheck("C04", HealthArea.Connections, HealthCostTier.Catalog, "Idle in transaction (scripted)")]
    private sealed class ScriptedIdleInTransactionCheck : ScriptedCheck;

    [HealthCheck("C06", HealthArea.Connections, HealthCostTier.Catalog, "Lock waits and blocking chains (scripted)")]
    private sealed class ScriptedLockWaitCheck : ScriptedCheck;

    [HealthCheck("T14", HealthArea.Tables, HealthCostTier.Catalog, "Vacuum held back (scripted)")]
    private sealed class ScriptedHorizonCheck : ScriptedCheck;
}
