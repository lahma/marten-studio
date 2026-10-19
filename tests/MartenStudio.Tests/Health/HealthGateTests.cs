using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Health;

using Npgsql;

namespace MartenStudio.Tests.Health;

/// <summary>
/// What each visitor is shown of the one shared sweep: the gate per check and per piece of evidence (D27,
/// decision 4).
/// </summary>
/// <remarks>
/// <para>
/// Three scripted findings cover the three gates: K90 (<see cref="HealthGate.None" />) carries one piece of
/// evidence behind <see cref="HealthGate.Activity" />, T90 is behind <see cref="HealthGate.Database" />, and
/// C90 is behind <see cref="HealthGate.Activity" /> as a whole. <c>ViewDatabaseActivity</c>'s three refusals -
/// the capability off, <c>ReadOnly</c>, the write policy - are asserted here rather than in
/// <c>CapabilityGatingMatrixTests</c>, because the health report redacts rather than refuses: a visitor short
/// of the gate sees a count and no names.
/// </para>
/// </remarks>
public class HealthGateTests
{
    private const string Pid = "pid 4242";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static HealthHarness Scripted(Action<MartenStudioOptions>? studio = null)
    {
        HealthHarness harness = HealthHarness.Create(studio: studio, policies: true);

        harness.Script.Finds("K90", HealthSeverity.Warning, "Configuration finding", static x =>
        {
            x.Evidence.Add(new HealthEvidence("Sessions", "3"));
            x.Evidence.Add(new HealthEvidence("Oldest session", Pid, HealthGate.Activity));
        });
        harness.Script.Finds("T90", HealthSeverity.Warning, "Database-wide finding");
        harness.Script.Finds("C90", HealthSeverity.Critical, "Activity finding");

        return harness;
    }

    [Fact]
    public async Task With_the_capability_off_activity_is_withheld_and_named_as_the_option_to_set()
    {
        await using HealthHarness harness = Scripted();

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Select(static x => x.Code).Should().BeEquivalentTo(["K90", "T90"]);
        report.WithheldCount.Should().Be(1);
        report.WithheldReason.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");

        HealthEvidence pid = report.Findings.Single(static x => x.Code == "K90").Evidence.Single(static x => x.Label == "Oldest session");
        pid.Value.Should().NotContain(Pid).And.StartWith("withheld");
        report.Findings.Single(static x => x.Code == "K90").Evidence.Should().Contain(new HealthEvidence("Sessions", "3"),
            "a count is shown without the gate; only the names are behind it");

        harness.Policies.Calls.Should().NotContain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity),
            "a capability that is off is answered before any policy is asked");
    }

    [Fact]
    public async Task ReadOnly_withholds_activity_and_says_it_is_the_master_switch()
    {
        await using HealthHarness harness = Scripted(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Select(static x => x.Code).Should().NotContain("C90");
        report.WithheldReason.Should().Contain("MartenStudioOptions.ReadOnly");
    }

    [Fact]
    public async Task A_write_policy_that_refuses_ViewDatabaseActivity_withholds_it_and_is_asked_with_no_tenant()
    {
        await using HealthHarness harness = Scripted(static options => options.Capabilities.ViewDatabaseActivity = true);
        harness.Policies.Allow(static resource => resource.Capability is null);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Select(static x => x.Code).Should().NotContain("C90");
        report.WithheldReason.Should().Contain("write policy");

        harness.Policies.Calls.Where(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity))
            .Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null && x.Policy == HealthHarness.WritePolicy,
                "the activity gate is asked of the database as a whole, through the write policy");
    }

    [Fact]
    public async Task Through_every_gate_everything_is_shown()
    {
        await using HealthHarness harness = Scripted(static options => options.Capabilities.ViewDatabaseActivity = true);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        // Worst first, then by area: Critical C90, then the two Warnings - Tables before Configuration.
        report.Findings.Select(static x => x.Code).Should().Equal("C90", "T90", "K90");
        report.WithheldCount.Should().Be(0);
        report.Findings.Single(static x => x.Code == "K90").Evidence.Should().Contain(new HealthEvidence("Oldest session", Pid, HealthGate.Activity));
    }

    /// <summary>
    /// A visitor the store policy lets see one tenant, and not the database as a whole, sees the configuration
    /// findings - which say nothing the Configuration page does not - and nothing database-wide.
    /// </summary>
    [Fact]
    public async Task A_tenant_scoped_visitor_sees_configuration_and_not_database_wide_numbers()
    {
        await using HealthHarness harness = Scripted(static options => options.KnownTenantIds.Add("acme"));
        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");

        StudioScope tenantScope = harness.Scope with { TenantId = "acme" };

        HealthReport report = await harness.Health.GetReportAsync(tenantScope, HealthReportRequest.Regular, Token);

        report.Refusal.Should().Be(HealthRefusal.None);
        report.Findings.Select(static x => x.Code).Should().Equal("K90");
        report.WithheldCount.Should().Be(2);
        report.WithheldReason.Should().Contain("as a whole", "T90 needs the store policy for the database with no tenant")
            .And.Contain("ViewDatabaseActivity", "C90 needs the activity gate as well");
    }

    [Fact]
    public async Task A_scope_the_store_policy_refuses_is_a_refusal_value_and_starts_no_sweep()
    {
        await using HealthHarness harness = Scripted();
        harness.Policies.DenyEverything();

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Refusal.Should().Be(HealthRefusal.StorePolicy);
        report.Findings.Should().BeEmpty();
        harness.Cache.SweepCount.Should().Be(0, "the scope is resolved before anything is swept");
    }

    /// <summary>A finding about one tenant names it, so it needs the activity gate and that tenant's store policy.</summary>
    [Fact]
    public async Task A_finding_that_names_a_tenant_needs_the_activity_gate_and_that_tenant()
    {
        await using HealthHarness harness = HealthHarness.Create(
            studio: static options => options.Capabilities.ViewDatabaseActivity = true,
            policies: true);
        harness.Script.Finds("K90", HealthSeverity.Warning, "About globex", static x => x.TenantId = "globex");
        harness.Policies.Allow(static resource => resource.TenantId != "globex");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Should().BeEmpty("the visitor may not see globex, so they do not learn its name from a finding");
        report.WithheldCount.Should().Be(1);
        report.WithheldReason.Should().Be("names a tenant the store policy does not show you");
    }

    /// <summary>
    /// Review item 3: a status row carries a note, a failure and statements, and a note may say what a finding
    /// would have - so a row is shown at its check's gate, and short of it is code, title, tier and outcome.
    /// </summary>
    [Fact]
    public async Task A_status_row_is_shown_at_its_checks_gate()
    {
        await using HealthHarness harness = HealthHarness.Create(policies: true);
        harness.Script.Judge["C90"] = static (_, _) => HealthVerdict.Quiet("3 sessions of role reporting are idle");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        HealthCheckStatus activity = report.Checks.Single(static x => x.Code == "C90");
        activity.Outcome.Should().Be(HealthCheckOutcome.Checked);
        activity.Note.Should().BeNull("short of the gate not even whether the check wrote a note is shown (SEC2-fix item 4)");
        activity.Statements.Should().BeEmpty();

        report.Checks.Single(static x => x.Code == "K90").Note.Should().Be("scripted: nothing to say", "an ungated row is shown whole");
    }

    /// <summary>Review item 5: the studio's cost is a database-wide number, gated like one.</summary>
    [Fact]
    public async Task The_studio_cost_is_withheld_from_a_visitor_short_of_the_database_gate()
    {
        await using HealthHarness harness = Scripted(static options => options.KnownTenantIds.Add("acme"));
        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");

        HealthReport tenant = await harness.Health.GetReportAsync(harness.Scope with { TenantId = "acme" }, HealthReportRequest.Regular, Token);

        tenant.Cost.Should().BeNull();
        tenant.CostWithheld.Should().Contain("as a whole");

        harness.Policies.Allow(static _ => true);
        HealthReport whole = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        whole.Cost.Should().NotBeNull();
        whole.CostWithheld.Should().BeNull();
    }

    /// <summary>
    /// Review item 11: the banners' read - the findings with these codes from the last regular sweep, through
    /// the page's gates, and never a sweep of its own.
    /// </summary>
    [Fact]
    public async Task The_banner_read_is_the_last_sweeps_findings_with_those_codes_and_never_sweeps()
    {
        await using HealthHarness harness = Scripted();

        (await harness.Health.CachedFindingsAsync(harness.Scope, ["K90", "T90"], Token)).Should().BeEmpty(
            "with no sweep yet there is nothing to show, and nothing is started to find out");
        harness.Cache.SweepCount.Should().Be(0);

        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        int sweeps = harness.Cache.SweepCount;

        IReadOnlyList<HealthReportFinding> banner = await harness.Health.CachedFindingsAsync(harness.Scope, ["T90", "C90"], Token);

        banner.Select(static x => x.Code).Should().Equal(["T90"], "C90 is behind the activity gate this visitor is short of, and K90 was not asked for");
        harness.Cache.SweepCount.Should().Be(sweeps);
        (await harness.Health.CachedFindingsAsync(harness.Scope, [], Token)).Should().BeEmpty();
    }

    /// <summary>
    /// H1-fix item 1, the refusing side: a tier-2 sweep reads a slice of the data in every table it checks, so a
    /// visitor the store policy admits for one tenant only cannot start one by asking - the service reads the
    /// request as "if cached" and says why - and is shown a run somebody else started only through the
    /// per-finding gates.
    /// </summary>
    [Fact]
    public async Task A_visitor_short_of_the_database_gate_cannot_start_the_deeper_checks()
    {
        await using HealthHarness harness = Scripted(static options => options.KnownTenantIds.Add("acme"));
        harness.Script.Finds("E90", HealthSeverity.Warning, "Deeper finding");
        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");

        StudioScope tenantScope = harness.Scope with { TenantId = "acme" };
        HealthReportRequest run = new(Deeper: HealthDeeperChecks.Run);

        HealthReport refused = await harness.Health.GetReportAsync(tenantScope, run, Token);

        refused.Refusal.Should().Be(HealthRefusal.None);
        refused.DeeperChecksExist.Should().BeTrue();
        refused.DeeperChecksAllowed.Should().BeFalse();
        refused.DeeperChecksRefused.Should().Contain("as a whole");
        refused.DeeperMeasuredAt.Should().BeNull("nothing was started, and nothing was cached");
        harness.Script.Runs.Should().NotContainKey("E90", "the request alone never starts a tier-2 sweep");

        // A visitor through the gate runs it; the tenant visitor then sees that run's time, and none of its findings.
        harness.Policies.Allow(static _ => true);
        await harness.Health.GetReportAsync(harness.Scope, run, Token);
        harness.Script.Runs["E90"].Should().Be(1);

        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");
        HealthReport later = await harness.Health.GetReportAsync(tenantScope, run, Token);

        harness.Script.Runs["E90"].Should().Be(1, "asking again does not start another");
        later.DeeperMeasuredAt.Should().NotBeNull("a recent run is shown");
        later.Findings.Select(static x => x.Code).Should().NotContain("E90", "E90's findings are behind the database-wide gate");
        later.WithheldCount.Should().Be(refused.WithheldCount + 1, "E90's finding is counted as withheld, not shown");
    }

    /// <summary>H1-fix item 1, the admitting side: through the database-wide gate, "Run" runs the tier-2 checks.</summary>
    [Fact]
    public async Task A_visitor_through_the_database_gate_starts_the_deeper_checks()
    {
        await using HealthHarness harness = Scripted();
        harness.Script.Finds("E90", HealthSeverity.Warning, "Deeper finding");

        HealthReport cached = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        cached.DeeperChecksAllowed.Should().BeTrue();
        cached.DeeperChecksRefused.Should().BeNull();
        harness.Script.Runs.Should().NotContainKey("E90", "a regular report never runs tier 2, whoever reads it");

        HealthReport run = await harness.Health.GetReportAsync(harness.Scope, new HealthReportRequest(Deeper: HealthDeeperChecks.Run), Token);

        harness.Script.Runs["E90"].Should().Be(1);
        run.DeeperMeasuredAt.Should().Be(harness.Clock.GetUtcNow());
        run.Findings.Select(static x => x.Code).Should().Contain("E90");
    }

    /// <summary>
    /// H1-fix item 5, at the report: "Postgres said:" labels Postgres' words and nothing else, and an exception
    /// message the studio did not write is not on the page at all.
    /// </summary>
    [Fact]
    public async Task Only_Postgres_words_are_labelled_as_Postgres_and_other_messages_stay_in_the_log()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Throws("K90", new InvalidOperationException("table acme.secret_orders is odd"));
        harness.Script.Throws("K91", new PostgresException("relation \"acme.mt_doc_x\" does not exist", "ERROR", "ERROR", PostgresErrorCodes.UndefinedTable));

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Checks.Single(static x => x.Code == "K90").Problem.Should()
            .Contain("InvalidOperationException").And.Contain("event 9222").And.NotContain("Postgres").And.NotContain("acme");

        report.Checks.Single(static x => x.Code == "K91").Problem.Should().Contain("Postgres said",
            "masked for this visitor, or withheld when the schema list cannot be read - either way, labelled as Postgres'");
    }

    /// <summary>
    /// H1-fix item 5: a connection error's own words (Npgsql's, the socket's) name the database's address, so a
    /// visitor short of the database-wide gate is told the check could not connect and no more.
    /// </summary>
    [Fact]
    public async Task A_connection_errors_words_are_shown_only_through_the_database_wide_gate()
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static options => options.KnownTenantIds.Add("acme"), policies: true);
        harness.Script.Gather["K90"] = static async (context, token) =>
        {
            await context.Sql.ScalarAsync<int>(new HealthQuery("select 1"), token);
            return new ScriptedFacts("K90", [], null);
        };
        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");

        HealthReport tenant = await harness.Health.GetReportAsync(harness.Scope with { TenantId = "acme" }, HealthReportRequest.Regular, Token);

        string? shortOfGate = tenant.Checks.Single(static x => x.Code == "K90").Problem;
        shortOfGate.Should().Contain("could not connect").And.Contain("connection error said is withheld").And.Contain("as a whole")
            .And.NotContain("127.0.0.1").And.NotContain("Postgres said");

        harness.Policies.Allow(static _ => true);
        HealthReport whole = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        whole.Checks.Single(static x => x.Code == "K90").Problem.Should().Contain("The connection error said: ")
            .And.NotContain("Postgres said", "Npgsql's words are not Postgres'");
    }

    /// <summary>Review item 4: two keys whose anchors read alike still get an element id each.</summary>
    [Fact]
    public async Task Two_findings_whose_anchors_read_alike_get_an_id_each()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Judge["K90"] = static (_, _) => HealthVerdict.Of(
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Info, Title = "Spaced", WhyItMatters = "x", Subject = "a b" },
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Info, Title = "Hyphened", WhyItMatters = "x", Subject = "a-b" });

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Select(static x => x.Anchor).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        report.Findings.Select(static x => x.MuteKey).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// XSO-fix: a finding that names a table in a schema outside the store's own is shown only past the database
    /// browser's gate for that schema (D27). This harness's store refuses every connection, so the gate cannot be read -
    /// and the finding is withheld, counted and given its reason, on the report and on the badge alike, while a finding
    /// that names no such schema is shown as before.
    /// </summary>
    [Fact]
    public async Task A_finding_naming_a_table_outside_the_stores_schemas_is_withheld_when_the_browsers_gate_cannot_be_read()
    {
        await using HealthHarness harness = HealthHarness.Create(policies: true);
        harness.Script.Finds("T90", HealthSeverity.Warning, "About an extended table", static x => x.GatedSchemas.Add("xso_own"));
        harness.Script.Finds("K90", HealthSeverity.Warning, "About the configuration");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        report.Findings.Select(static x => x.Code).Should().Equal("K90");
        report.WithheldCount.Should().Be(1);
        report.WithheldReason.Should().Be(HealthService.UncheckedSchemaReason);

        HealthCheckStatus row = report.Checks.Single(static x => x.Code == "T90");
        row.FindingCount.Should().Be(0);
        row.FindingsWithheld.Should().BeTrue("a withheld finding is never a \"nothing found\"");

        HealthGlance glance = (await harness.Health.GlanceAsync(harness.Scope, Token))!;
        glance.Warning.Should().Be(1, "the badge resolves no scope, so it cannot ask the browser's gate and does not count the finding");
        glance.Withheld.Should().Be(1);
    }

    /// <summary>The badge's glance goes through the same gates, and is never a sweep.</summary>
    [Fact]
    public async Task The_glance_counts_what_this_visitor_may_see_and_has_not_muted()
    {
        await using HealthHarness harness = Scripted();

        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        int sweeps = harness.Cache.SweepCount;

        HealthGlance glance = (await harness.Health.GlanceAsync(harness.Scope, Token))!;

        glance.Critical.Should().Be(0, "the Critical one is behind the activity gate this visitor is short of");
        glance.Warning.Should().Be(2);
        glance.Worst.Should().Be(HealthSeverity.Warning);

        harness.Cache.SweepCount.Should().Be(sweeps, "a glance reads the last sweep and starts none");
    }

    [Fact]
    public async Task A_glance_at_a_sweep_older_than_its_freshness_is_nothing()
    {
        await using HealthHarness harness = Scripted();

        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        harness.Clock.Advance(TimeSpan.FromMinutes(6));

        (await harness.Health.GlanceAsync(harness.Scope, Token)).Should().BeNull(
            "a badge from a sweep nobody has refreshed in five minutes would be stale news");
    }
}
