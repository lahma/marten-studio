using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using static PostgresStudio.Tests.Server.ServerTestFacts;

namespace PostgresStudio.Tests.Server;

/// <summary>K05-K07 against permutations of the studio's options, in each studio's own option names (W5-S2).</summary>
public class StudioLintCheckTests
{
    private static StudioLintFacts Options(
        bool readOnly = false,
        StudioCapability[]? on = null,
        bool consoleRole = false,
        bool everySchema = false,
        bool postgres = false) =>
        new(
            readOnly,
            on ?? [],
            consoleRole,
            everySchema,
            postgres ? "PostgresStudioOptions" : "MartenStudioOptions",
            postgres ? "AddPostgresStudio" : "AddMartenStudio",
            postgres ? "PostgresStudioCapabilities" : "MartenStudioCapabilities");

    // ---- K05 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void K05_RunSql_on_without_a_console_role_is_a_Warning_whose_remedy_is_the_option_line()
    {
        HealthFinding finding = new ConsoleWithoutRoleCheck().Evaluate(Options(on: [StudioCapability.RunSql]), Evaluation())
            .ShouldBeWellFormedFor<ConsoleWithoutRoleCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.WhyItMatters.Should().Contain("MartenStudioOptions.Capabilities.RunSql").And.Contain("MartenStudioOptions.SqlConsoleRole");
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Configuration);
        finding.Remedy.Code.Should().Contain("AddMartenStudio(").And.Contain("options.SqlConsoleRole = ");
        finding.Remedy.Explanation.Should().Contain("MartenStudioOptions.SqlConsoleRole");
    }

    [Fact]
    public void K05_in_Postgres_Studio_names_its_own_options()
    {
        HealthFinding finding = new ConsoleWithoutRoleCheck().Evaluate(Options(on: [StudioCapability.RunSql], postgres: true), Evaluation(postgres: true))
            .Findings.Single();

        finding.WhyItMatters.Should().Contain("PostgresStudioOptions.SqlConsoleRole").And.NotContain("Marten");
        finding.Remedy!.Code.Should().Contain("AddPostgresStudio(");
    }

    [Theory]
    [InlineData(true, true, false, "ReadOnly is true")]
    [InlineData(false, false, false, "is off")]
    [InlineData(false, true, true, "narrows")]
    public void K05_is_quiet_under_ReadOnly_with_the_console_off_and_with_a_console_role(bool readOnly, bool runSql, bool role, string why)
    {
        HealthVerdict verdict = new ConsoleWithoutRoleCheck().Evaluate(
            Options(readOnly, runSql ? [StudioCapability.RunSql] : [StudioCapability.BrowseDatabase], role),
            Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(why);
    }

    [Fact]
    public void K05_never_names_the_console_role_at_its_gate_which_is_None()
    {
        HealthCheckRegistry.Describe(typeof(ConsoleWithoutRoleCheck)).Gate.Should().Be(HealthGate.None);
        HealthCheckRegistry.Describe(typeof(ConsoleWithoutRoleCheck)).Tier.Should().Be(HealthCostTier.InProcess);

        // The role's name is the database-wide gate's (D29); the facts carry only whether one is set.
        typeof(StudioLintFacts).GetProperty(nameof(StudioLintFacts.SqlConsoleRole))!.PropertyType.Should().Be<bool>();
    }

    // ---- K06 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void K06_the_browser_on_every_schema_without_a_role_is_event_9230s_condition_as_a_Warning()
    {
        HealthFinding finding = new BrowserWithoutRoleCheck().Evaluate(Options(on: [StudioCapability.BrowseDatabase], everySchema: true), Evaluation())
            .ShouldBeWellFormedFor<BrowserWithoutRoleCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.WhyItMatters.Should().Contain("event 9230").And.Contain("MartenStudioOptions.BrowsableSchemas");
        finding.Remedy!.Code.Should().Contain("options.SqlConsoleRole = ");
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    public void K06_is_quiet_under_ReadOnly_without_the_capability_with_exact_schemas_and_with_a_role(bool readOnly, bool browse, bool everySchema, bool role)
    {
        new BrowserWithoutRoleCheck()
            .Evaluate(Options(readOnly, browse ? [StudioCapability.BrowseDatabase] : [StudioCapability.RunSql], role, everySchema), Evaluation())
            .Findings.Should().BeEmpty();
    }

    // ---- K07 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production", "Critical")]
    [InlineData("Development", "Warning")]
    [InlineData(null, "Warning")]
    public void K07_anonymous_with_a_capability_on_is_Critical_in_Production_and_a_Warning_elsewhere(string? environment, string expected)
    {
        HealthSeverity severity = Enum.Parse<HealthSeverity>(expected);
        HealthFinding finding = new AnonymousStudioCheck()
            .Evaluate(new AnonymousStudioFacts(Options(on: [StudioCapability.RunSql, StudioCapability.EditDocuments]), true, environment), Evaluation())
            .ShouldBeWellFormedFor<AnonymousStudioCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(severity);
        finding.Evidence.Should().Contain(x => x.Label == "Capabilities on" && x.Value == "RunSql, EditDocuments");
        finding.Remedy!.Code.Should().Contain("app.MapMartenStudio().RequireAuthorization();").And.Contain("new MartenStudioCapabilities()");
    }

    [Fact]
    public void K07_in_Postgres_Studio_maps_its_own_studio()
    {
        new AnonymousStudioCheck()
            .Evaluate(new AnonymousStudioFacts(Options(on: [StudioCapability.RunSql], postgres: true), true, "Production"), Evaluation(postgres: true))
            .Findings.Single().Remedy!.Code.Should().Contain("app.MapPostgresStudio()").And.Contain("new PostgresStudioCapabilities()");
    }

    [Theory]
    [InlineData(null, false, false, "no record")]
    [InlineData(false, false, false, "not served anonymously")]
    [InlineData(true, false, true, "every capability off")]
    [InlineData(true, true, false, "every capability off (MartenStudioOptions.ReadOnly)")]
    public void K07_is_quiet_without_anonymous_pages_without_a_capability_and_under_ReadOnly(bool? anonymous, bool readOnly, bool noCapability, string why)
    {
        HealthVerdict verdict = new AnonymousStudioCheck().Evaluate(
            new AnonymousStudioFacts(Options(readOnly, noCapability ? [] : [StudioCapability.RunSql]), anonymous, "Production"),
            Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(why);
    }

    [Fact]
    public void The_three_lints_run_in_both_studios_from_the_shared_assembly_with_no_I_O()
    {
        foreach (Type check in (Type[]) [typeof(ConsoleWithoutRoleCheck), typeof(BrowserWithoutRoleCheck), typeof(AnonymousStudioCheck)])
        {
            HealthCheckInfo info = HealthCheckRegistry.Describe(check);
            info.Area.Should().Be(HealthArea.Configuration);
            info.Tier.Should().Be(HealthCostTier.InProcess);
            info.Gate.Should().Be(HealthGate.None);
            check.Assembly.Should().BeSameAs(typeof(HealthCheckRegistry).Assembly, "the shared studio's, so both studios run it");
        }
    }
}
