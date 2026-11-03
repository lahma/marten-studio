using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Services.Configuration;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Configuration;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Services.Health;

using ConfigurationPage = MartenStudio.Components.Pages.Configuration.Configuration;

namespace MartenStudio.Tests.Health;

/// <summary>A scripted check with a real code, so a host may accept it (an accepted key must be a check of the studio).</summary>
[HealthCheck("K05", HealthArea.Configuration, HealthCostTier.InProcess, "Scripted K05", Gate = HealthGate.None)]
internal sealed class ScriptedAcceptableCheck : ScriptedCheck;

/// <summary>An Activity-gated scripted check with a real code.</summary>
[HealthCheck("C04", HealthArea.Connections, HealthCostTier.Catalog, "Scripted C04", Gate = HealthGate.Activity)]
internal sealed class ScriptedAcceptableActivityCheck : ScriptedCheck;

/// <summary>
/// Accepted checks (issue #35): the option's validation, the sweep's marking, and what an accepted finding is counted in -
/// nothing - through the real health service, the page and the Configuration list.
/// </summary>
public class AcceptedChecksTests
{
    private const string Reason = "Roles are managed by the platform team; the console is admin-only.";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static readonly Type[] Checks = [typeof(ScriptedAcceptableCheck), typeof(ScriptedAcceptableActivityCheck), typeof(ScriptedConfigurationCheck)];

    private static MartenStudioOptions Resolve(Action<MartenStudioOptions> configure)
    {
        ServiceCollection services = new();
        services.AddMartenStudio(configure);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
    }

    private static HealthHarness Accepting(Action<MartenStudioOptions>? more = null, bool policies = false) =>
        HealthHarness.Create(
            Checks,
            studio: options =>
            {
                options.AcceptedChecks["K05"] = Reason;
                more?.Invoke(options);
            },
            policies: policies);

    // ---- Validation ----------------------------------------------------------------------------------------------

    [Fact]
    public void AcceptedChecks_is_empty_case_insensitive_and_takes_any_check_of_this_studio()
    {
        new MartenStudioOptions().AcceptedChecks.Should().NotBeNull().And.BeEmpty();

        // D01 is Marten Studio's own check and K05 the shared engine's: both are in this studio's registry.
        MartenStudioOptions options = Resolve(static x =>
        {
            x.AcceptedChecks["d01"] = "the daemon is not run by this process";
            x.AcceptedChecks["K05"] = "managed roles";
        });

        options.AcceptedChecks["D01"].Should().Be("the daemon is not run by this process");
        options.AcceptedChecks.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("X99")]
    [InlineData("K5")]
    [InlineData("")]
    public void An_accepted_key_that_is_not_a_check_is_refused_naming_the_key_and_nothing_else(string key)
    {
        Action resolve = () => Resolve(x => x.AcceptedChecks[key] = "a reason");

        OptionsValidationException refusal = resolve.Should().Throw<OptionsValidationException>().Which;

        refusal.Failures.Should().ContainSingle().Which.Should()
            .Be("MartenStudioOptions.AcceptedChecks key '" + key + "' is not a health check code of this studio");
    }

    [Theory]
    [InlineData("K10")]
    [InlineData("k10")]
    public void An_entry_for_a_retired_check_is_accepted_and_ignored_so_a_host_that_accepted_it_still_starts(string key)
    {
        MartenStudioOptions options = Resolve(x =>
        {
            x.AcceptedChecks[key] = "the schema is named with a hyphen on purpose";
            x.AcceptedChecks["K05"] = "managed roles";
        });

        options.AcceptedChecks.Should().HaveCount(2, "the entry is kept, and nothing reads it");
    }

    [Fact]
    public void A_retired_entry_is_logged_once_per_code_at_Information_with_event_9490_however_often_the_options_are_validated()
    {
        CapturingLoggerProvider captured = new();
        ServiceCollection services = new();
        services.AddSingleton(captured.CreateFactory());
        services.AddMartenStudio(static x =>
        {
            x.AcceptedChecks["K10"] = "an old reason";
            x.AcceptedChecks["k10"] = "the same code again";
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
        _ = provider.GetRequiredService<IOptionsMonitor<MartenStudioOptions>>().CurrentValue;
        using (IServiceScope scope = provider.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<MartenStudioOptions>>().Value;
        }

        CapturedLogEntry line = captured.Entries.Where(static x => x.EventId.Id == 9490).Should().ContainSingle().Subject;
        line.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Information);
        line.Message.Should().Be("AcceptedChecks lists K10, which was retired in 1.1 and no longer runs; the entry has no effect and can be removed.");
    }

    [Fact]
    public void No_retired_entry_means_no_9490_line_and_an_unknown_code_is_still_refused_beside_one()
    {
        CapturingLoggerProvider captured = new();
        ServiceCollection services = new();
        services.AddSingleton(captured.CreateFactory());
        services.AddMartenStudio(static x => x.AcceptedChecks["K05"] = "managed roles");
        using ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;

        captured.Entries.Should().NotContain(static x => x.EventId.Id == 9490);

        Action both = () => Resolve(static x =>
        {
            x.AcceptedChecks["K10"] = "retired";
            x.AcceptedChecks["X99"] = "a typo";
        });

        both.Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle()
            .Which.Should().Be("MartenStudioOptions.AcceptedChecks key 'X99' is not a health check code of this studio");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_reason_is_refused(string reason)
    {
        Action resolve = () => Resolve(x => x.AcceptedChecks["K05"] = reason);

        resolve.Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle()
            .Which.Should().StartWith("MartenStudioOptions.AcceptedChecks['K05'] needs a reason");
    }

    [Fact]
    public void A_reason_over_500_characters_is_refused_and_one_of_500_is_not()
    {
        Resolve(static x => x.AcceptedChecks["K05"] = new string('r', 500)).AcceptedChecks["K05"].Should().HaveLength(500);

        Action resolve = () => Resolve(static x => x.AcceptedChecks["K05"] = new string('r', 501));

        resolve.Should().Throw<OptionsValidationException>().Which.Failures.Should().ContainSingle()
            .Which.Should().Be("MartenStudioOptions.AcceptedChecks['K05'] has a reason longer than 500 characters");
    }

    // ---- The sweep -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_check_still_runs_and_its_finding_is_marked_accepted_with_the_reason()
    {
        await using HealthHarness harness = Accepting();
        harness.Script.Finds("K05", HealthSeverity.Warning, "The console reads as the data source's role");
        harness.Script.Finds("K90", HealthSeverity.Warning, "Not accepted");

        HealthSweep sweep = await harness.SweepAsync();

        harness.Script.Runs["K05"].Should().Be(1, "an accepted check is still run");

        HealthFinding accepted = sweep.Results.Single(static x => x.Check.Code == "K05").Findings.Should().ContainSingle().Subject;
        accepted.Accepted.Should().BeTrue();
        accepted.AcceptedReason.Should().Be(Reason);
        accepted.Severity.Should().Be(HealthSeverity.Warning, "acceptance never rewrites what the finding says");

        HealthFinding other = sweep.Results.Single(static x => x.Check.Code == "K90").Findings.Should().ContainSingle().Subject;
        other.Accepted.Should().BeFalse();
        other.AcceptedReason.Should().BeNull();
    }

    [Fact]
    public async Task A_check_that_now_passes_shows_as_passing_as_usual()
    {
        await using HealthHarness harness = Accepting();

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Should().BeEmpty();
        HealthCheckStatus row = report.Checks.Single(static x => x.Code == "K05");
        row.Outcome.Should().Be(HealthCheckOutcome.Checked);
        row.AcceptedCount.Should().Be(0);
        row.AcceptedReason.Should().BeNull("a passing check carries no acceptance");
        HealthPresentation.OutcomeLabel(row.Outcome, row.FindingCount).Should().Be("checked, nothing found");
    }

    // ---- The report, the badge, the banners -----------------------------------------------------------------------

    [Fact]
    public async Task The_report_keeps_the_accepted_finding_marks_its_row_and_counts_it_in_no_finding_count()
    {
        await using HealthHarness harness = Accepting();
        harness.Script.Finds("K05", HealthSeverity.Warning, "Accepted warning");
        harness.Script.Finds("K90", HealthSeverity.Warning, "A real warning");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Should().HaveCount(2, "an accepted finding is shown, not hidden");
        HealthReportFinding accepted = report.Findings.Single(static x => x.Code == "K05");
        accepted.Accepted.Should().BeTrue();
        accepted.AcceptedReason.Should().Be(Reason);
        accepted.Counts(static _ => false).Should().BeFalse();
        report.Findings.Single(static x => x.Code == "K90").Counts(static _ => false).Should().BeTrue();

        report.Worst.Should().Be(HealthSeverity.Warning, "from the unaccepted one");

        HealthCheckStatus row = report.Checks.Single(static x => x.Code == "K05");
        row.AcceptedCount.Should().Be(1);
        row.AcceptedReason.Should().Be(Reason);
        row.FindingCount.Should().Be(0, "the row counts no accepted finding");
        row.FindingsWithheld.Should().BeFalse();
        report.Checks.Single(static x => x.Code == "K90").FindingCount.Should().Be(1);
    }

    [Fact]
    public async Task An_accepted_critical_alone_gives_a_report_with_no_worst_state()
    {
        await using HealthHarness harness = Accepting();
        harness.Script.Finds("K05", HealthSeverity.Critical, "Accepted critical");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Should().ContainSingle();
        report.Worst.Should().BeNull();
    }

    [Fact]
    public async Task The_badge_the_toast_and_the_banners_leave_an_accepted_finding_out()
    {
        await using HealthHarness harness = Accepting();
        harness.Script.Finds("K05", HealthSeverity.Critical, "Accepted critical");
        harness.Script.Finds("K90", HealthSeverity.Warning, "A real warning");

        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        HealthGlance? glance = await harness.Health.GlanceAsync(harness.Scope, Token);

        glance.Should().NotBeNull();
        glance.Critical.Should().Be(0, "an accepted Critical is not a Critical");
        glance.Warning.Should().Be(1);
        glance.Criticals.Should().BeEmpty("the toast reads this list");
        glance.Worst.Should().Be(HealthSeverity.Warning);

        (await harness.Health.CachedFindingsAsync(harness.Scope, ["K05", "K90"], Token)).Select(static x => x.Code)
            .Should().Equal(["K90"], "a banner is not drawn for an accepted finding");
    }

    [Fact]
    public async Task An_accepted_finding_alone_leaves_the_badge_with_nothing_to_draw()
    {
        await using HealthHarness harness = Accepting();
        harness.Script.Finds("K05", HealthSeverity.Warning, "Accepted warning");

        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        HealthGlance? glance = await harness.Health.GlanceAsync(harness.Scope, Token);

        glance!.Worst.Should().BeNull();
        glance.Warning.Should().Be(0);
    }

    [Fact]
    public async Task A_browser_mute_and_acceptance_are_independent_and_acceptance_wins_for_counting()
    {
        await using HealthHarness harness = Accepting();
        harness.Script.Finds("K05", HealthSeverity.Warning, "Accepted warning");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        HealthReportFinding accepted = report.Findings.Single();

        accepted.Counts(static _ => false).Should().BeFalse("accepted, not muted");
        accepted.Counts(static _ => true).Should().BeFalse("accepted and muted");
        new HealthReportFinding(accepted.Finding with { Accepted = false, AcceptedReason = null }, [], accepted.MuteKey, "u", true, null)
            .Counts(static _ => true).Should().BeFalse("muted, not accepted: still out, as before");
    }

    // ---- Gates ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_accepted_finding_a_gate_withholds_stays_withheld_and_its_reason_is_not_shown()
    {
        await using HealthHarness harness = Accepting(static options => options.AcceptedChecks["C04"] = "Pool sizing is the other team's call.", policies: true);
        harness.Script.Finds("C04", HealthSeverity.Warning, "Activity finding");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Should().BeEmpty();
        report.WithheldCount.Should().Be(1, "an accepted finding a visitor's gate withholds is still counted as withheld");

        HealthCheckStatus row = report.Checks.Single(static x => x.Code == "C04");
        row.BelowGate.Should().NotBeNull();
        row.AcceptedCount.Should().Be(0);
        row.AcceptedReason.Should().BeNull("the reason is shown only where the finding itself would be");
        row.FindingsWithheld.Should().BeTrue();
    }

    // ---- The page, the Configuration list ---------------------------------------------------------------------------

    private static StudioComponentContext NewConfigurationContext()
    {
        StudioComponentContext context = new(services => services.AddSingleton<IConfigurationService>(new FakeConfigurationService()));
        context.WithStores("default");
        return context;
    }

    [Fact]
    public async Task The_health_page_shows_the_accepted_check_in_its_row_with_the_reason_and_draws_no_card()
    {
        await using StudioComponentContext context = new();
        await context.ReadyAsync();

        HealthFinding finding = FakeHealthService.Finding("K05", HealthSeverity.Warning, "The console reads as the data source's role") with
        {
            Accepted = true,
            AcceptedReason = "<b>managed</b> roles",
        };

        context.Health.Report = context.Health.Report with
        {
            Findings = [new HealthReportFinding(finding, finding.Evidence, HealthService.MuteKeyOf("default", "localhost.marten", finding), "https://example.invalid/docs", true, null)],
            Checks =
            [
                new HealthCheckStatus("K05", "SQL console without a narrowed role", HealthArea.Configuration, HealthCostTier.InProcess, HealthCheckOutcome.Checked, 0, null, null, null, "https://example.invalid/docs", [])
                {
                    AcceptedCount = 1,
                    AcceptedReason = finding.AcceptedReason,
                },
                new HealthCheckStatus("K01", "AutoCreate in Production", HealthArea.Configuration, HealthCostTier.InProcess, HealthCheckOutcome.Checked, 0, null, null, null, "https://example.invalid/docs", []),
            ],
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-checks").Should().ContainSingle());
        page.FindAll(".ms-health-finding").Should().BeEmpty("an accepted finding is not drawn as a card that asks for attention");
        page.Find(".ms-health-empty .ms-empty-title").TextContent.Trim().Should().Be("Nothing to report");

        var accepted = page.Find("tr.ms-table-row.ms-health-check-checked:has(.ms-health-accepted)");
        accepted.QuerySelector(".ms-health-accepted-label")!.TextContent.Trim().Should().Be("Accepted");
        accepted.QuerySelector(".ms-health-accepted")!.TextContent.Should().Contain(": <b>managed</b> roles", "the reason is host text, encoded");
        accepted.QuerySelector(".ms-health-accepted b").Should().BeNull("never markup");
        accepted.QuerySelector(".ms-health-outcome")!.TextContent.Trim().Should().Be("checked, nothing found");

        page.FindAll(".ms-health-accepted").Should().ContainSingle("a passing check shows as passing as usual");
    }

    [Fact]
    public async Task The_overview_counts_no_accepted_finding()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithStore();
        await context.ReadyAsync();

        context.Health.WithFindings(
            FakeHealthService.Finding("K05", HealthSeverity.Critical, "Accepted") with { Accepted = true, AcceptedReason = Reason });

        var overview = context.Render<Overview>();

        overview.WaitForAssertion(() => overview.FindAll(".ms-overview-health-empty").Should().ContainSingle());
        overview.Find(".ms-overview-health-empty").TextContent.Should().Contain("Nothing to report");
        overview.FindAll(".ms-overview-health-list").Should().BeEmpty();
    }

    [Fact]
    public void The_configuration_page_lists_each_accepted_check_with_its_reason_as_text()
    {
        using StudioComponentContext context = NewConfigurationContext();
        context.Options.AcceptedChecks["k05"] = "<i>managed</i>";
        context.Options.AcceptedChecks["D01"] = "daemon runs elsewhere";

        var page = context.Render<ConfigurationPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-accepted-checks").Should().ContainSingle());

        string[][] rows = [.. page.FindAll(".ms-accepted-checks-table tbody tr").Select(static r => r.QuerySelectorAll("td").Select(static c => c.TextContent.Trim()).ToArray())];

        rows.Select(static r => r[0]).Should().Equal("D01", "K05");
        rows.Single(static r => r[0] == "K05")[2].Should().Be("<i>managed</i>");
        page.FindAll(".ms-accepted-checks i").Should().BeEmpty("the reason is encoded, never markup");
    }

    [Fact]
    public void The_configuration_page_does_not_list_a_retired_entry_as_a_check_that_runs()
    {
        using StudioComponentContext context = NewConfigurationContext();
        context.Options.AcceptedChecks["K10"] = "an old reason";
        context.Options.AcceptedChecks["K05"] = "managed roles";

        var page = context.Render<ConfigurationPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-accepted-checks").Should().ContainSingle());
        page.FindAll(".ms-accepted-checks-table tbody tr").Select(static r => r.QuerySelector("td")!.TextContent.Trim()).Should().Equal("K05");
    }

    [Fact]
    public void The_configuration_page_has_no_accepted_section_when_nothing_is_accepted()
    {
        using StudioComponentContext context = NewConfigurationContext();

        var page = context.Render<ConfigurationPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-config-card").Should().NotBeEmpty());
        page.FindAll(".ms-accepted-checks").Should().BeEmpty();
    }
}
