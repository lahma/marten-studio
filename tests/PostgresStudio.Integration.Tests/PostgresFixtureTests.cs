using DotNet.Testcontainers.Containers;

namespace PostgresStudio.Integration.Tests;

/// <summary>
/// The container-reuse switch, and the reuse identity that keeps this suite's container apart from every other one.
/// </summary>
/// <remarks>
/// The switch is worth a test because the variable it reads used to be <c>TESTCONTAINERS_REUSE_ENABLE</c>, which
/// Testcontainers 4.x does not read, so a run that set it shared a container while believing it had one to itself. The
/// identity is worth one because two containers built from the same image defaults hash alike, and a reusing start
/// attaches to whichever is running: that is how month-old suite data reached the Marten sample's <c>dotnet run</c>.
/// </remarks>
public class PostgresFixtureTests
{
    [Fact]
    public void The_switch_is_the_repositorys_own_variable_and_not_a_Testcontainers_one() =>
        PostgresFixture.ReuseEnvironmentVariable.Should().Be(
            "MARTENSTUDIO_PG_REUSE",
            "one variable governs every container this repository starts (design §7.3)");

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("  false  ")]
    public void False_and_zero_turn_reuse_off(string value) =>
        PostgresFixture.IsReuseDisabled(value).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("falsey")]
    public void Anything_else_keeps_the_default(string? value) =>
        PostgresFixture.IsReuseDisabled(value).Should().BeFalse();

    [Fact]
    public void Continuous_integration_never_reuses()
    {
        if (DockerAvailability.OnContinuousIntegration)
        {
            PostgresFixture.ShouldReuse.Should().BeFalse("a CI agent is thrown away anyway");
        }
        else
        {
            PostgresFixture.ShouldReuse.Should().Be(
                !PostgresFixture.IsReuseDisabled(Environment.GetEnvironmentVariable(PostgresFixture.ReuseEnvironmentVariable)),
                "off CI the variable is the only thing that decides");
        }
    }

    /// <summary>
    /// The suite's container and the Postgres Studio sample's throwaway one hash differently whether or not reuse is asked
    /// for, and the suite's carries its label - which the Marten suite's fixture, built from the same image, does not, so
    /// the two suites' containers never hash alike either.
    /// </summary>
    /// <remarks>A <c>[PostgresFact]</c> only because Testcontainers' builder needs a Docker endpoint to build at all.</remarks>
    [PostgresFact]
    public async Task The_suites_container_has_a_reuse_identity_of_its_own()
    {
        await using PostgreSqlContainerPair pair = new(PostgresFixture.CreateContainer(reuse: true), PostgresStudio.Sample.EphemeralPostgres.CreateContainer(reuse: true));

        ContainerReuse.ConfigurationOf(pair.Suite).Labels.Should().Contain(PostgresFixture.ReuseLabel, PostgresFixture.ReuseLabelValue);
        ContainerReuse.HashOf(pair.Suite).Should().NotBe(ContainerReuse.HashOf(pair.Sample),
            "a reusing sample start must never attach to the suite's container, nor the suite to the sample's");

        await using PostgreSqlContainerPair unreused = new(PostgresFixture.CreateContainer(reuse: false), PostgresStudio.Sample.EphemeralPostgres.CreateContainer(reuse: false));
        ContainerReuse.HashOf(unreused.Suite).Should().Be(ContainerReuse.HashOf(pair.Suite), "the reuse flag is not part of the identity");
    }

    /// <summary>Two built, never started, containers, disposed together.</summary>
    private sealed class PostgreSqlContainerPair(IContainer suite, IContainer sample) : IAsyncDisposable
    {
        public IContainer Suite { get; } = suite;

        public IContainer Sample { get; } = sample;

        public async ValueTask DisposeAsync()
        {
            await Suite.DisposeAsync();
            await Sample.DisposeAsync();
        }
    }
}
