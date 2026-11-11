using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// 1.2-HANG for KStudio (REVIEW-SCAF follow-up 3): a test that holds a gate shut never awaits it without a bound. xunit has no timeout of its
/// own, so a fast-suite test that waits on a <see cref="TaskCompletionSource" /> nobody completes is not a failure but a test host sitting idle
/// until the CI job is cancelled, with no word about which test it was. A bound on the await - <c>Bounded()</c> (<c>TestBounds</c>), or
/// <c>WaitAsync(cancellationToken)</c> where the caller's token ends the read - turns the same mistake into a
/// <see cref="TimeoutException" /> on the test that held the gate. The scan covers both KStudio test projects' sources (the live suite links the
/// contract files, which are scanned where they live).
/// </summary>
public class KStudioNoUnboundedGateAwaitTests
{
    private static readonly Regex UnboundedAwait = new(
        @"\bawait\s+(?:[A-Za-z_]\w*\.)*(?:[A-Za-z_]\w*\.Task|gate|hold|release|proceed|never)\s*(?:;|\)|\.ConfigureAwait|\.ContinueWith)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly string[] Projects = ["KStudio.Tests", "KStudio.Integration.Tests"];

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
            "an await of a gate needs a bound, so a gate nobody opens fails its test instead of hanging the suite: 'await gate.Task.Bounded()'. Offenders: "
            + string.Join("; ", offenders));
    }

    /// <summary>The anti-vacuity check: the pattern has to be able to fail, and has to let a bounded await through.</summary>
    [Theory]
    [InlineData("class C { async Task M() { await gate.Task; } }", true)]
    [InlineData("class C { async Task M() { await this.Gate.Task; } }", true)]
    [InlineData("class C { async Task M() { return await gate.Task; } }", true)]
    [InlineData("class C { async Task M() { await gate; } }", true)]
    [InlineData("class C { async Task M() { await release.Task.ConfigureAwait(false); } }", true)]
    [InlineData("class C { async Task M() { (await gate.Task).Should(); } }", true)]
    [InlineData("class C { async Task M() { await gate.Task.Bounded(); } }", false)]
    [InlineData("class C { async Task M() { await gate.Bounded(); } }", false)]
    [InlineData("class C { async Task M() { await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false); } }", false)]
    [InlineData("class C { async Task M() { await started.Task.WaitAsync(Generous, Token); } }", false)]
    [InlineData("class C { async Task M() { await Task.WhenAll(first, second); } }", false)]
    [InlineData("// await gate.Task;\nclass C { }", false)]
    [InlineData("class C { string s = \"await gate.Task;\"; }", false)]
    public void The_pattern_finds_an_unbounded_await_and_lets_a_bounded_one_through(string source, bool expected)
    {
        UnboundedAwait.IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().Be(expected);
    }

    private static string[] TestSourceFiles() =>
        [
            .. Projects
                .Select(static project => RepositoryRoot.Combine("tests", project))
                .Where(Directory.Exists)
                .SelectMany(static root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
        ];
}
