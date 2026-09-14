using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// No Razor expression is written so that the parser reads it as an e-mail address.
/// </summary>
/// <remarks>
/// <para>
/// Razor treats an <c>@</c> with a word character on both sides as an e-mail address and emits the whole
/// thing as literal text, with no warning and no error. So <c>v@Version</c> in a table cell reached the
/// screen as the nine characters <c>v@Version</c>, and the Overview's stream list printed
/// <c>v@stream.Version.ToString(CultureInfo.InvariantCulture)</c> beside every row. Both survived a
/// component test suite and a public API snapshot; a browser found them in a minute, which is the
/// argument for the browser suite in one sentence.
/// </para>
/// <para>
/// The fix is always the explicit expression form — <c>v@(Version)</c> — which is never mistaken for an
/// address. `EventCard` had it right all along, which is how the two that did not were spotted.
/// </para>
/// </remarks>
public partial class NoImplicitEmailExpressionTests
{
    [Fact]
    public void No_razor_file_glues_an_expression_to_the_word_before_it()
    {
        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(
            RepositoryRoot.Combine("src"), "*.razor", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);

            foreach (Match match in EmailShapedExpression().Matches(text))
            {
                int line = text.Take(match.Index).Count(static c => c == '\n') + 1;

                offenders.Add($"{Path.GetFileName(file)}:{line} '{match.Value}'");
            }
        }

        offenders.Should().BeEmpty(
            "Razor reads an '@' with a word character on either side as an e-mail address and emits the " +
            "expression as text - write it as @(expression) instead");
    }

    /// <summary>
    /// The scan sees what it claims to see: the two shapes that were really on screen are found, and the
    /// explicit form beside them is not.
    /// </summary>
    [Theory]
    [InlineData("<td>v@Version</td>", true)]
    [InlineData("<span>v@stream.Version.ToString(CultureInfo.InvariantCulture)</span>", true)]
    [InlineData("<td>v@(Version)</td>", false)]
    [InlineData("<span>@Version</span>", false)]
    [InlineData("<a href=\"@Href\">@Text</a>", false)]
    [InlineData("@* a comment mentioning v@Version would be a false positive *@", true)]
    public void The_scan_finds_the_shape_it_is_about(string markup, bool offending) =>
        EmailShapedExpression().IsMatch(markup).Should().Be(offending, markup);

    /// <summary>
    /// A word character, then <c>@</c>, then the start of an identifier — Razor's own e-mail heuristic.
    /// </summary>
    /// <remarks>
    /// Deliberately not excluding comments: an <c>@</c> in a comment that matches this shape is worth
    /// rewriting too, because the next person to copy that line out of the comment gets the bug.
    /// </remarks>
    [GeneratedRegex(@"[A-Za-z0-9_]@[A-Za-z_]", RegexOptions.None, 5000)]
    private static partial Regex EmailShapedExpression();
}
