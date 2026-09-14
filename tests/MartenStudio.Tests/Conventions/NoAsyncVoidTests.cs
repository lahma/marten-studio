using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Enforces AGENTS.md hard rule 6: no <c>async void</c> anywhere in <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// In Blazor Server an <c>async void</c> handler's exception has nowhere to go. It escapes the
/// synchronization context, is not observed by the renderer, and takes down the circuit - the user sees
/// the page stop responding with no error anywhere, and the server log gets an unhandled exception with
/// no component in the stack. Every component event in this codebase is a <c>Func&lt;Task&gt;</c> or a
/// <c>private async Task</c> handler with a <c>try</c>/<c>catch</c> that puts the failure on screen.
/// </para>
/// <para>
/// The scanner strips comments and string literals first (<see cref="SourceScanner"/>, shared with the
/// hard rule 14 scan), so prose that mentions the phrase - including this very rule, quoted in a doc
/// comment - does not fail the build. That stripping is exactly what makes the test capable of being
/// silently vacuous, so
/// <see cref="The_scanner_finds_a_real_async_void_and_ignores_one_in_a_comment_or_a_string"/> checks the
/// scanner against known-good and known-bad inputs before the real scan is trusted.
/// </para>
/// </remarks>
public class NoAsyncVoidTests
{
    private static readonly Regex AsyncVoid =
        new(@"\basync\s+void\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    [Fact]
    public void No_source_file_declares_an_async_void()
    {
        var files = SourceScanner.ShippedSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        var offenders = files
            .Where(x => AsyncVoid.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))))
            .Select(SourceScanner.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.Should().BeEmpty(
            "hard rule 6: an async void handler's exception kills the Blazor circuit silently. Offenders: " +
            string.Join(", ", offenders));
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail.</summary>
    [Theory]
    [InlineData("class C { async void Handler() { } }", true)]
    [InlineData("class C { public async void OnClick(object s, EventArgs e) { } }", true)]
    [InlineData("class C { async  \n    void Wrapped() { } }", true)]
    [InlineData("class C { async Task Handler() { } }", false)]
    [InlineData("// never write async void here\nclass C { }", false)]
    [InlineData("/* async void */\nclass C { }", false)]
    [InlineData("class C { string s = \"async void\"; }", false)]
    [InlineData("class C { string s = @\"async void\"; }", false)]
    [InlineData("@* Razor prose: never async void *@\n<p>ok</p>", false)]
    [InlineData("@* Marten's rule *@\n@code { async void Handler() { } }", true)]
    [InlineData("<p class='x'>it's fine</p>\n@code { async void Handler() { } }", true)]
    public void The_scanner_finds_a_real_async_void_and_ignores_one_in_a_comment_or_a_string(
        string source, bool expected)
    {
        AsyncVoid.IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().Be(expected);
    }
}
