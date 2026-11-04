using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// 1.2-HANG: a test that holds a gate shut never awaits it without a bound. Since 1.2-HANG-2 the scan covers both fast suites, each
/// with its own <c>TestBounds</c>.
/// </summary>
/// <remarks>
/// <para>
/// xunit has no timeout of its own and neither has <c>dotnet test</c> without <c>--blame-hang</c>, so a fast-suite test that waits on
/// a <see cref="TaskCompletionSource" /> nobody completes is not a failure: it is a test host sitting idle until the CI job is
/// cancelled, with no word about which test it was. That happened twice before 1.2 (a held counts read in
/// <c>OverviewTests.A_read_that_was_in_flight_when_the_scope_moved_does_not_land</c> that the wrong one of two overlapping runs
/// took). A bound on the await of the gate - <c>Bounded()</c> (<c>TestBounds</c>), or <c>WaitAsync(cancellationToken)</c> where the
/// caller's token ends the read - turns the same mistake into a <see cref="TimeoutException" /> on the test that held the gate.
/// </para>
/// <para>
/// The scan is for the await of a gate in the fast suites' own sources:<c>await x.Task;</c>, and an await of a variable called
/// <c>gate</c>, <c>hold</c> or <c>release</c> (a <see cref="Task" /> handed in), with nothing after it. It cannot see a test that
/// awaits the service's task, which a gate it holds feeds; the fakes' and work lambdas' gates being bounded is what ends that wait.
/// </para>
/// </remarks>
public class NoUnboundedGateAwaitTests
{
    private static readonly Regex UnboundedAwait = new(
        @"\bawait\s+(?:[A-Za-z_]\w*\.)*(?:[A-Za-z_]\w*\.Task|gate|hold|release|proceed|never)\s*(?:;|\)|\.ConfigureAwait|\.ContinueWith)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly string[] FastSuites = ["MartenStudio.Tests", "PostgresStudio.Tests"];

    [Fact]
    public void No_test_or_fake_awaits_a_gate_without_a_bound()
    {
        string[] files = TestSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] offenders =
        [
            .. files
                .SelectMany(static file => UnboundedAwait
                    .Matches(SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)))
                    .Select(match => SourceScanner.Relative(file) + ": " + match.Value))
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty(
            "an await of a gate needs a bound, so a gate nobody opens fails its test instead of hanging the suite: " +
            "'await gate.Task.Bounded()'. Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>The anti-vacuity check: the pattern has to be able to fail, and has to let a bounded await through.</summary>
    [Theory]
    [InlineData("class C { async Task M() { await gate.Task; } }", true)]
    [InlineData("class C { async Task M() { await Gate.Task; } }", true)]
    [InlineData("class C { async Task M() { await this.Gate.Task; } }", true)]
    [InlineData("class C { async Task M() { return await gate.Task; } }", true)]
    [InlineData("class C { async Task M() { await gate; } }", true)]
    [InlineData("class C { async Task M() { await release.Task.ConfigureAwait(false); } }", true)]
    [InlineData("class C { async Task M() { (await gate.Task).Should(); } }", true)]
    [InlineData("class C { async Task M() { await gate.Task.Bounded(); } }", false)]
    [InlineData("class C { async Task M() { await gate.Bounded(); } }", false)]
    [InlineData("class C { async Task M() { await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false); } }", false)]
    [InlineData("class C { async Task M() { await gate.WaitAsync(timeout, token); } }", false)]
    [InlineData("class C { async Task M() { await started.Task.WaitAsync(Generous, Token); } }", false)]
    [InlineData("class C { async Task M() { await Task.WhenAll(first, second); } }", false)]
    [InlineData("class C { async Task M() { await release.Task.ContinueWith(static _ => 0, TaskScheduler.Default); } }", true)]
    [InlineData("// await gate.Task;\nclass C { }", false)]
    [InlineData("class C { string s = \"await gate.Task;\"; }", false)]
    public void The_pattern_finds_an_unbounded_await_and_lets_a_bounded_one_through(string source, bool expected)
    {
        UnboundedAwait.IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().Be(expected);
    }

    /// <summary>Both fast suites' sources (1.2-HANG-2): this one guard scans <c>tests/MartenStudio.Tests</c> and <c>tests/PostgresStudio.Tests</c>.</summary>
    private static string[] TestSourceFiles() =>
        [.. FastSuites
            .SelectMany(static project => Directory.EnumerateFiles(RepositoryRoot.Combine("tests", project), "*.cs", SearchOption.AllDirectories))
            .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
}
