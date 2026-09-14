using System.Text;
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
/// The scanner strips comments and string literals first, so prose that mentions the phrase - including
/// this very rule, quoted in a doc comment - does not fail the build. That stripping is exactly what
/// makes the test capable of being silently vacuous, so
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
        var sourceDirectory = RepositoryRoot.Combine("src");

        var files = Directory
            .EnumerateFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                        x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(x => !IsBuildOutput(x))
            .ToArray();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        var offenders = files
            .Where(x => AsyncVoid.IsMatch(StripCommentsAndStrings(File.ReadAllText(x))))
            .Select(x => Path.GetRelativePath(RepositoryRoot.FullPath, x).Replace('\\', '/'))
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
        AsyncVoid.IsMatch(StripCommentsAndStrings(source)).Should().Be(expected);
    }

    private static bool IsBuildOutput(string path)
    {
        var relative = Path.GetRelativePath(RepositoryRoot.FullPath, path).Replace('\\', '/');

        return relative.Split('/').Any(x => x is "bin" or "obj");
    }

    /// <summary>
    /// Replaces every comment and string literal with spaces, keeping the file's length and line breaks so
    /// the result still reads as the same code with the prose removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A character-by-character pass rather than a regular expression: the cases that matter - a quote
    /// inside a comment, a comment marker inside a string, an escaped quote, a verbatim string's doubled
    /// quote, a Razor <c>@* *@</c> block - are precisely the ones a regex gets wrong, and getting them
    /// wrong here means either a false failure nobody can fix or a rule that quietly stops applying.
    /// </para>
    /// <para>
    /// A <c>'</c> only opens a character literal when a closing <c>'</c> follows within eight characters -
    /// the length of the longest one C# can write, <c>'A'</c>. Without that guard the apostrophe in a
    /// word like <em>Marten's</em>, which is ordinary prose in a .razor file, would blank everything up to
    /// the next apostrophe anywhere in the file, and the rule would stop applying to whatever fell inside.
    /// </para>
    /// </remarks>
    private static string StripCommentsAndStrings(string source)
    {
        var result = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                index = BlankUntilNewLine(source, result, index);
            }
            else if (current == '/' && next == '*')
            {
                index = BlankUntil(source, result, index + 2, "*/");
            }
            else if (current == '@' && next == '*')
            {
                index = BlankUntil(source, result, index + 2, "*@");
            }
            else if (current == '@' && next == '"')
            {
                index = BlankVerbatimString(source, result, index + 2);
            }
            else if (current == '"')
            {
                index = BlankQuoted(source, result, index, '"');
            }
            else if (current == '\'' && IsCharacterLiteral(source, index))
            {
                index = BlankQuoted(source, result, index, '\'');
            }
            else
            {
                result.Append(current);
                index++;
            }
        }

        return result.ToString();
    }

    private static bool IsCharacterLiteral(string source, int index)
    {
        var limit = Math.Min(source.Length, index + 9);

        for (var i = index + 1; i < limit; i++)
        {
            if (source[i] == '\n')
            {
                return false;
            }

            if (source[i] == '\'' && source[i - 1] != '\\')
            {
                return true;
            }
        }

        return false;
    }

    private static int BlankUntilNewLine(string source, StringBuilder result, int index)
    {
        while (index < source.Length && source[index] != '\n')
        {
            result.Append(' ');
            index++;
        }

        return index;
    }

    private static int BlankUntil(string source, StringBuilder result, int index, string terminator)
    {
        result.Append("  ");

        while (index < source.Length &&
               !(source[index] == terminator[0] &&
                 index + 1 < source.Length &&
                 source[index + 1] == terminator[1]))
        {
            result.Append(source[index] == '\n' ? '\n' : ' ');
            index++;
        }

        result.Append("  ");
        return Math.Min(index + 2, source.Length);
    }

    private static int BlankVerbatimString(string source, StringBuilder result, int index)
    {
        result.Append("  ");

        while (index < source.Length)
        {
            if (source[index] == '"')
            {
                // A doubled quote is an escaped quote and does not end the literal.
                if (index + 1 < source.Length && source[index + 1] == '"')
                {
                    result.Append("  ");
                    index += 2;
                    continue;
                }

                result.Append(' ');
                return index + 1;
            }

            result.Append(source[index] == '\n' ? '\n' : ' ');
            index++;
        }

        return index;
    }

    private static int BlankQuoted(string source, StringBuilder result, int index, char quote)
    {
        result.Append(' ');
        index++;

        while (index < source.Length && source[index] != quote)
        {
            if (source[index] == '\\' && index + 1 < source.Length)
            {
                result.Append("  ");
                index += 2;
                continue;
            }

            result.Append(source[index] == '\n' ? '\n' : ' ');
            index++;
        }

        if (index < source.Length)
        {
            result.Append(' ');
            index++;
        }

        return index;
    }
}
