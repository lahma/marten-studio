using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// What an evaluator test asserts of every verdict it gets: each finding is coded for its check and of a variant the check
/// lists (<see cref="HealthCheckAttribute.Variants" />) - the rules the sweep refuses a verdict for, held in the unit test
/// so a check is never born speaking a variant it did not declare.
/// </summary>
/// <remarks>
/// Usage: <c>verdict.ShouldBeWellFormedFor&lt;JitShareCheck&gt;()</c>, after the evaluator ran.
/// </remarks>
internal static class HealthVerdictAssertions
{
    /// <summary>Asserts every finding of <paramref name="verdict" /> is one <typeparamref name="TCheck" /> may make; returns the verdict.</summary>
    public static HealthVerdict ShouldBeWellFormedFor<TCheck>(this HealthVerdict verdict)
        where TCheck : IHealthCheck
    {
        ArgumentNullException.ThrowIfNull(verdict);

        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(TCheck));

        foreach (HealthFinding finding in verdict.Findings)
        {
            finding.Code.Should().Be(info.Code, "a check speaks only for its own code");
            HealthCheckRegistry.FindingProblems(info, finding).Should().BeEmpty();
        }

        verdict.Findings.Select(static x => x.Key).Should().OnlyHaveUniqueItems("each finding has a key of its own");
        return verdict;
    }
}
