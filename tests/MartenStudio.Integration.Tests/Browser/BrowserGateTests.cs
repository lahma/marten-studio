namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// The D19 gate, as a truth table rather than as a hope.
/// </summary>
/// <remarks>
/// <para>
/// The interesting branch is the one that only ever happens on CI, which is exactly the branch nobody
/// checks: a gate written the wrong way round skips on the build server and the suite reports green
/// forever without having run. <see cref="BrowserAvailability.Decide" /> is therefore a pure function of
/// the two facts the decision is made from, and both branches are asserted here on a machine that is
/// neither CI nor missing a browser — without setting an environment variable, which would be
/// process-wide state every other test in the run shares.
/// </para>
/// <para>
/// These are plain facts: they need no Docker, no browser and no host, so they run in every
/// <c>dotnet test</c> and fail immediately if the rule is ever inverted.
/// </para>
/// </remarks>
public class BrowserGateTests
{
    [Theory]
    [InlineData(true, false, BrowserGate.Run)]
    [InlineData(true, true, BrowserGate.Run)]
    [InlineData(false, false, BrowserGate.Skip)]
    [InlineData(false, true, BrowserGate.Throw)]
    public void A_missing_browser_skips_locally_and_fails_on_continuous_integration(
        bool chromiumInstalled,
        bool onContinuousIntegration,
        BrowserGate expected)
    {
        BrowserAvailability.Decide(chromiumInstalled, onContinuousIntegration).Should().Be(expected);
    }

    /// <summary>
    /// The skip reason has to carry a command that works. <c>Browsers</c> is <c>OnlyWhenDynamic</c>, so
    /// pointing somebody at a target that would skip is the same failure one layer up.
    /// </summary>
    [Fact]
    public void The_skip_reason_names_the_command_that_installs_the_browser()
    {
        BrowserAvailability.SkipReason.Should().Contain(BrowserAvailability.InstallCommand);
        BrowserAvailability.SkipReason.Should().Contain(BrowserAvailability.RawInstallCommand);
        BrowserAvailability.SkipReason.Should().Contain(
            "docker info",
            "one skip reason covers both gates, so it has to name both of them");
    }

    /// <summary>
    /// The gate the fixture actually calls: it throws on CI without a browser and on nothing else, and
    /// the message says why a failure is the right answer.
    /// </summary>
    [Fact]
    public void The_fixture_gate_throws_only_on_continuous_integration_without_a_browser()
    {
        Action missingOnCi = static () => BrowserAvailability.ThrowIfMissing(
            chromiumInstalled: false, onContinuousIntegration: true);

        missingOnCi.Should().Throw<InvalidOperationException>()
            .WithMessage("*worse than a failure*")
            .And.Message.Should().Contain("Browsers target", "the message has to name the step that installs it");

        Action missingLocally = static () => BrowserAvailability.ThrowIfMissing(
            chromiumInstalled: false, onContinuousIntegration: false);
        missingLocally.Should().NotThrow("a developer without Chromium gets a skip, not a failure");

        Action presentOnCi = static () => BrowserAvailability.ThrowIfMissing(
            chromiumInstalled: true, onContinuousIntegration: true);
        presentOnCi.Should().NotThrow();
    }

    /// <summary>
    /// The probe and the environment are read through the same decision, so a machine that has the
    /// browser runs the scenarios and one that does not skips them — and neither depends on which
    /// variables happen to be exported in the shell.
    /// </summary>
    [Fact]
    public void The_live_decision_agrees_with_the_probe()
    {
        BrowserGate live = BrowserAvailability.Decide(
            BrowserAvailability.ChromiumInstalled,
            DockerAvailability.OnContinuousIntegration);

        if (BrowserAvailability.ChromiumInstalled)
        {
            live.Should().Be(BrowserGate.Run);
        }
        else
        {
            live.Should().Be(
                DockerAvailability.OnContinuousIntegration ? BrowserGate.Throw : BrowserGate.Skip);
        }
    }
}
