using System.Text;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Reads the checked-in <c>src/</c> tree as code rather than as text, for the conventions that are
/// enforced by scanning it.
/// </summary>
/// <remarks>
/// <para>
/// Two rules need this - "no <c>async void</c>" (hard rule 6) and "no schema-building Marten call on a
/// read path" (hard rule 14) - and both are about what the code <em>does</em>, so both have to ignore
/// what it <em>says</em>. This codebase documents its rules in the doc comments of the very files they
/// apply to: <c>ProjectionProgressQueries</c> explains at length why nothing calls
/// <c>FetchHighestEventSequenceNumber</c>, and a scanner that could not tell that paragraph from a call
/// would make the rule unwritable.
/// </para>
/// <para>
/// Sharing the pass is what keeps the two scanners honest about the same set of cases. It lives here
/// rather than on either test so that neither owns it, and
/// <see cref="NoAsyncVoidTests.The_scanner_finds_a_real_async_void_and_ignores_one_in_a_comment_or_a_string" />
/// and <see cref="NoSchemaBuildingCallTests.The_scanner_finds_a_real_call_and_ignores_one_in_a_comment_or_a_string" />
/// are both anti-vacuity checks on it.
/// </para>
/// </remarks>
internal static class SourceScanner
{
    /// <summary>Every C# and Razor file of the shipped library, build output excluded.</summary>
    /// <remarks>
    /// Returned as an array and asserted non-empty by every caller: a scan of nothing passes, and passing
    /// for that reason is the failure mode a conventions test exists to avoid.
    /// </remarks>
    public static string[] ShippedSourceFiles() =>
        [.. Directory
            .EnumerateFiles(RepositoryRoot.Combine("src"), "*.*", SearchOption.AllDirectories)
            .Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                        x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(x => !IsBuildOutput(x))];

    /// <summary>A path as the report should name it: relative to the repository root, with forward slashes.</summary>
    public static string Relative(string path) =>
        Path.GetRelativePath(RepositoryRoot.FullPath, path).Replace('\\', '/');

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
    public static string StripCommentsAndStrings(string source)
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

    private static bool IsBuildOutput(string path) =>
        Relative(path).Split('/').Any(x => x is "bin" or "obj");

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
