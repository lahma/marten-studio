using Bunit;

using MartenStudio.Components.Pages.Health;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Health;

/// <summary>A scripted statement check: tier 3, behind the activity gate, as the registry requires.</summary>
[HealthCheck("T91", HealthArea.Tables, HealthCostTier.Statements, "Scripted statements", Gate = HealthGate.Activity)]
internal sealed class ScriptedStatementCheck : ScriptedCheck;

/// <summary>
/// The statement checks gated both ways (acceptance 2): nothing of tier 3 runs or is shown short of
/// <c>ViewDatabaseActivity</c>, a statement's text reaches the page only masked for its visitor, a statement sweep's
/// history stays its own - and the Health page's button says all of that.
/// </summary>
public class StatementChecksGateTests
{
    private const string RawStatement = "select s.salary from hr.payroll s join studio_sql.mt_doc_order o on o.id = s.order_id";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static HealthHarness Scripted(Action<MartenStudioOptions>? studio = null)
    {
        HealthHarness harness = HealthHarness.Create(checks: [.. HealthHarness.ScriptedChecks, typeof(ScriptedStatementCheck)], studio: studio, policies: true);

        harness.Script.Finds("T91", HealthSeverity.Warning, "An expensive statement", static x =>
        {
            x.Subject = "4242";
            x.Evidence.Add(HealthEvidence.StatementText("Statement", RawStatement, truncated: false));
            x.Evidence.Add(new HealthEvidence("Calls", "1,000"));
        });

        return harness;
    }

    [Fact]
    public async Task Without_the_activity_gate_the_statement_checks_neither_run_nor_show_not_even_somebody_elses_run()
    {
        await using HealthHarness harness = Scripted();

        // A run somebody else started, still cached.
        await harness.Cache.GetAsync(harness.Resolved, HealthSweepKind.Statements, harness.Scope.DatabaseId, Token);
        harness.Script.Runs.Remove("T91");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, new HealthReportRequest(Statements: HealthDeeperChecks.Run), Token);

        harness.Script.Runs.Should().NotContainKey("T91", "the request to run is the service's to refuse, whatever the page sent");
        report.StatementChecksExist.Should().BeTrue();
        report.StatementChecksAllowed.Should().BeFalse();
        report.StatementChecksRefused.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");
        report.StatementsMeasuredAt.Should().BeNull();
        report.Checks.Should().NotContain(static x => x.Code == "T91", "not even the row: a tier-3 run is not shown short of its gate");
        report.Findings.Should().NotContain(static x => x.Code == "T91");
        report.WithheldCount.Should().Be(0, "nothing of it is counted either");
    }

    [Fact]
    public async Task A_write_policy_that_refuses_ViewDatabaseActivity_refuses_the_statement_checks_too()
    {
        await using HealthHarness harness = Scripted(static options => options.Capabilities.ViewDatabaseActivity = true);
        harness.Policies.Allow(static resource => resource.Capability is null);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, new HealthReportRequest(Statements: HealthDeeperChecks.Run), Token);

        harness.Script.Runs.Should().NotContainKey("T91");
        report.StatementChecksAllowed.Should().BeFalse();
        report.StatementChecksRefused.Should().Contain("write policy");
    }

    [Fact]
    public async Task Through_the_gate_the_button_runs_them_and_a_statement_is_masked_before_it_is_shown()
    {
        await using HealthHarness harness = Scripted(static options => options.Capabilities.ViewDatabaseActivity = true);

        HealthReport cached = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        harness.Script.Runs.Should().NotContainKey("T91", "only the button starts a statement sweep");
        cached.StatementChecksAllowed.Should().BeTrue();

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, new HealthReportRequest(Statements: HealthDeeperChecks.Run), Token);

        harness.Script.Runs.Should().ContainKey("T91");
        report.StatementsMeasuredAt.Should().NotBeNull();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "T91").Subject;
        HealthEvidence statement = finding.Evidence.Single(static x => x.Label == "Statement");

        // This harness's database cannot be reached, so the visitor's schema gate cannot be read: the text is withheld
        // rather than shown unmasked - and whatever the outcome, the raw text never reaches the page.
        statement.Value.Should().Be(SessionQueryText.SchemasUncheckedNote);
        statement.Statement.Should().BeNull("what the page receives carries only what this visitor may read");
        finding.Evidence.Should().NotContain(static x => x.Value.Contains("hr.payroll", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(new HealthEvidence("Calls", "1,000"));

        // And showing it is audited, as session details are.
        harness.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .Should().Contain(static x => x.Action == HealthService.ActivityAction && x.Message != null && x.Message.Contains("T91", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_statement_sweeps_samples_go_to_a_history_of_their_own_that_no_other_check_reads()
    {
        await using HealthHarness harness = Scripted(static options => options.Capabilities.ViewDatabaseActivity = true);
        harness.Script.Gather["T91"] = static (_, _) => Task.FromResult(new ScriptedFacts("T91", [new HealthSamplePart("statements.calls:1", 42)], null));

        HealthHistoryView? seenByRegular = null;
        harness.Script.Judge["K90"] = (_, evaluation) =>
        {
            seenByRegular = evaluation.History;
            return HealthVerdict.Quiet();
        };

        HealthHistoryView? seenByStatements = null;
        harness.Script.Judge["T91"] = (_, evaluation) =>
        {
            seenByStatements = evaluation.History;
            return HealthVerdict.Quiet();
        };

        string database = harness.Resolved.Database.Id.Identity;

        await harness.SweepAsync(HealthSweepKind.Statements);
        await harness.SweepAsync();

        harness.History.Read("default", database).Samples.Should().NotContain(static x => x.Values.ContainsKey("statements.calls:1"));
        harness.History.Read("default", HealthHistory.RingOf(database, HealthSweepKind.Statements)).Samples
            .Should().ContainSingle().Which.Values.Should().ContainKey("statements.calls:1");

        seenByStatements!.Trend("statements.calls:1").Points.Should().ContainSingle("a statement evaluator reads the statement history");
        seenByRegular!.Trend("statements.calls:1").Points.Should().BeEmpty("a regular evaluator never sees it");
    }

    // ----------------------------------------------------------------------------------------------
    // The page's half
    // ----------------------------------------------------------------------------------------------

    private static async Task<StudioComponentContext> ReadyAsync()
    {
        StudioComponentContext context = new();
        await context.ReadyAsync();
        return context;
    }

    [Fact]
    public async Task With_no_statement_check_the_page_offers_no_button()
    {
        await using StudioComponentContext context = await ReadyAsync();

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-checks").Should().ContainSingle());
        page.FindAll(".ms-health-statements").Should().BeEmpty();
        page.FindAll(".ms-health-statements-note").Should().BeEmpty();
    }

    [Fact]
    public async Task A_visitor_who_may_not_run_the_statement_checks_sees_the_button_off_and_why()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with
        {
            StatementChecksExist = true,
            StatementChecksAllowed = false,
            StatementChecksRefused = "needs MartenStudioOptions.Capabilities.ViewDatabaseActivity",
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-statements").HasAttribute("disabled").Should().BeTrue());
        page.Find(".ms-health-statements").GetAttribute("title").Should().Contain("ViewDatabaseActivity");
        page.Find(".ms-health-statements-note").TextContent.Should().Contain("off for you").And.Contain("ViewDatabaseActivity");
    }

    [Fact]
    public async Task Through_the_gate_the_button_runs_the_statement_checks_and_only_the_button_does()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with { StatementChecksExist = true, StatementChecksAllowed = true };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-statements").HasAttribute("disabled").Should().BeFalse());
        page.Find(".ms-health-statements-note").TextContent.Should().Contain("has not run here recently");
        context.Health.Requests.Should().OnlyContain(static x => x.Statements == HealthDeeperChecks.IfCached);

        context.Health.Report = context.Health.Report with { StatementsMeasuredAt = context.Health.Report.MeasuredAt };
        page.Find(".ms-health-statements").Click();

        page.WaitForAssertion(() => context.Health.Requests.Should().Contain(static x => x.Statements == HealthDeeperChecks.Run));
        page.WaitForAssertion(() => page.Find(".ms-health-statements-note").TextContent.Should().Contain("last ran"));
    }
}
