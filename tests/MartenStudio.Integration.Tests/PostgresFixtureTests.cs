namespace MartenStudio.Integration.Tests;

/// <summary>
/// The container-reuse switch, which needs no container of its own.
/// </summary>
/// <remarks>
/// Worth a test for one reason: the variable this reads used to be <c>TESTCONTAINERS_REUSE_ENABLE</c>,
/// which Testcontainers for .NET 4.x does not read at all, so every run that set it believed it had a
/// container to itself and was in fact sharing one with every other run on the machine. A misspelt name
/// fails silently in exactly the same way, and the only thing that catches that is naming it here.
/// </remarks>
public class PostgresFixtureTests
{
    [Fact]
    public void The_switch_is_this_projects_own_variable_and_not_a_Testcontainers_one() =>
        PostgresFixture.ReuseEnvironmentVariable.Should().Be(
            "MARTENSTUDIO_PG_REUSE",
            "Testcontainers 4.x reads no TESTCONTAINERS_REUSE_ENABLE, so reuse has to be decided here");

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("  false  ")]
    public void False_and_zero_turn_reuse_off(string value) =>
        PostgresFixture.IsReuseDisabled(value).Should().BeTrue();

    /// <summary>
    /// Everything else leaves the default alone. A variable nobody set, and a value somebody fat-fingered,
    /// must not quietly change how the suite runs in one direction or the other.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("no")]
    [InlineData("falsey")]
    public void Anything_else_keeps_the_default(string? value) =>
        PostgresFixture.IsReuseDisabled(value).Should().BeFalse();

    /// <summary>CI never reuses, whatever the variable says.</summary>
    [Fact]
    public void Continuous_integration_never_reuses()
    {
        if (DockerAvailability.OnContinuousIntegration)
        {
            PostgresFixture.ShouldReuse.Should().BeFalse(
                "a CI agent is thrown away anyway, and a leaked container is somebody's leaked quota");
        }
        else
        {
            PostgresFixture.ShouldReuse.Should().Be(
                !PostgresFixture.IsReuseDisabled(
                    Environment.GetEnvironmentVariable(PostgresFixture.ReuseEnvironmentVariable)),
                "off CI the variable is the only thing that decides");
        }
    }
}
