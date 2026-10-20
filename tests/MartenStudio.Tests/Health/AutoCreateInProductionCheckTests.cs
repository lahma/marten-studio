using JasperFx;

using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// K01's judgement, over stated facts and no Postgres (the <c>IndexAdvice.Join</c> precedent).
/// </summary>
public class AutoCreateInProductionCheckTests
{
    private static readonly HealthEvaluation Evaluation = HealthEvaluation.At(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

    private static HealthVerdict Evaluate(AutoCreate mode, string? environment) =>
        new AutoCreateInProductionCheck().Evaluate(new AutoCreateFacts(mode, environment), Evaluation);

    [Theory]
    [InlineData(AutoCreate.All)]
    [InlineData(AutoCreate.CreateOrUpdate)]
    public void A_mode_that_alters_existing_objects_in_Production_is_a_warning(AutoCreate mode)
    {
        HealthVerdict verdict = Evaluate(mode, "Production");

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("K01");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("AutoCreate", mode.ToString()));
        finding.Evidence.Should().Contain(new HealthEvidence("Environment", "Production"));
        finding.Remedy.Should().NotBeNull();
        finding.Remedy!.Code.Should().Contain("AutoCreate.None").And.Contain("ApplyAllDatabaseChangesOnStartup()");
        finding.Remedy.Requires.Should().BeNull("every line of K01's remedy exists at the floor");
    }

    [Fact]
    public void The_environment_name_is_compared_the_way_IsProduction_compares_it()
    {
        Evaluate(AutoCreate.All, "production").Findings.Should().ContainSingle();
    }

    /// <summary>
    /// Hard rule 14, the wording half: nothing may describe CreateOrUpdate as additive - and K01 is the check
    /// whose whole point is that it is not.
    /// </summary>
    [Fact]
    public void Nothing_K01_says_calls_CreateOrUpdate_additive()
    {
        HealthFinding finding = Evaluate(AutoCreate.CreateOrUpdate, "Production").Findings.Single();

        string all = finding.Title + " " + finding.WhyItMatters + " " + finding.Caveat + " " + finding.Remedy!.Explanation;

        all.Should().Contain("not additive");
        all.Replace("not additive", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Should().NotContainEquivalentOf("additive", "the only thing K01 may say about additivity is that there is none");
        all.Should().NotContainEquivalentOf("never destroy", "JasperFx's own summary of CreateOrUpdate is the claim hard rule 14 refutes");
    }

    [Theory]
    [InlineData(AutoCreate.CreateOnly)]
    [InlineData(AutoCreate.None)]
    public void A_mode_that_never_alters_an_existing_object_is_quiet_even_in_Production(AutoCreate mode)
    {
        HealthVerdict verdict = Evaluate(mode, "Production");

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(mode.ToString());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Any_environment_but_Production_is_quiet(string environment)
    {
        HealthVerdict verdict = Evaluate(AutoCreate.All, environment);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(environment);
    }

    /// <summary>
    /// An absent <c>IHostEnvironment</c> - or one that names no environment, which is what a container that
    /// never ran a host builder answers - is a value: the check says it cannot tell, and finds nothing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_host_with_no_environment_is_quiet_and_says_why(string? environment)
    {
        HealthVerdict verdict = Evaluate(AutoCreate.All, environment);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("IHostEnvironment");
    }
}
