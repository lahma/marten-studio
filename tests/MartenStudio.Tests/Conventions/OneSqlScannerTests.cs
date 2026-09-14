using System.Globalization;
using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// There is one lexical scanner under <c>Internal/Sql/</c>, and no second opinion about where a Postgres
/// line comment ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a test and not a review note.</b> The console guard and the clause composer each used to
/// carry a private copy of the same walk. Three adversarial reviews of Mode A found the same class of
/// defect three times, and each time it had to be fixed in both copies: a dollar-quote tag that may not
/// begin with a digit, and then a <c>--</c> comment that ended at LF but not at CR. Postgres' own lexer
/// spells <c>non_newline</c> as <c>[^\n\r]</c>, so a scanner that runs on to the line feed hides everything
/// after a lone carriage return from the statement-separator rule, the bracket balance, the function
/// denylist and the sort-list check <em>while Postgres runs it</em> - which was measured as a cross-tenant
/// read from the mode that needs no capability at all.
/// </para>
/// <para>
/// Two halves, both cheap to check and impossible to notice in review. A character literal <c>'\n'</c> may
/// appear in <c>Internal/Sql/</c> only where a statement is being <em>written</em> (an
/// <c>Append('\n')</c>), or on a line that also names <c>'\r'</c>; and <c>'\r'</c> itself appears in
/// <c>SqlLexer.cs</c> and nowhere else, because deciding where a comment ends is the lexer's job and there
/// is one of it.
/// </para>
/// <para>
/// The stripping the first half does is exactly what could make it silently vacuous, so
/// <see cref="The_scan_finds_a_line_feed_only_terminator_and_leaves_a_written_newline_alone" /> checks the
/// predicate against known-good and known-bad text before the real scan is trusted.
/// </para>
/// </remarks>
public class OneSqlScannerTests
{
    /// <summary>The file that owns the lexing. Everything else in the folder asks it.</summary>
    private const string TheLexer = "SqlLexer.cs";

    /// <summary>A <c>'\n'</c> character literal, however it is spaced.</summary>
    private static readonly Regex LineFeedLiteral = new(@"'\\n'", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>A <c>'\r'</c> character literal, however it is spaced.</summary>
    private static readonly Regex CarriageReturnLiteral =
        new(@"'\\r'", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>Writing a newline into a statement the studio is composing, which is not a scan at all.</summary>
    private static readonly Regex WrittenNewline =
        new(@"Append\(\s*'\\n'\s*\)", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>
    /// Every line of <paramref name="source" /> that tests a character against <c>'\n'</c> without also
    /// naming <c>'\r'</c>, one-based.
    /// </summary>
    internal static IReadOnlyList<int> LineFeedOnlyTerminators(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<int> offenders = [];
        string[] lines = source.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            // What is left after the writes are taken out is a line that *reads* a newline, and a line that
            // reads one has to know about both spellings.
            string line = WrittenNewline.Replace(lines[i], string.Empty);

            if (LineFeedLiteral.IsMatch(line) && !CarriageReturnLiteral.IsMatch(line))
            {
                offenders.Add(i + 1);
            }
        }

        return offenders;
    }

    private static string[] SqlSourceFiles() =>
        [.. Directory.EnumerateFiles(RepositoryRoot.Combine("src", "MartenStudio", "Internal", "Sql"), "*.cs")];

    [Fact]
    public void No_scan_in_Internal_Sql_ends_a_line_comment_at_a_line_feed_alone()
    {
        string[] files = SqlSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        List<string> offenders = [];

        foreach (string file in files)
        {
            foreach (int line in LineFeedOnlyTerminators(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetFileName(file) + ":" + line.ToString(CultureInfo.InvariantCulture));
            }
        }

        offenders.Should().BeEmpty(
            "Postgres ends a '--' comment at a carriage return as well as a line feed (its lexer's " +
            "non_newline is [^\\n\\r]), so a scan that only knows about '\\n' hides live SQL from every " +
            "rule. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Only_the_lexer_has_an_opinion_about_where_a_line_comment_ends()
    {
        string[] files = SqlSourceFiles();

        files.Should().Contain(x => Path.GetFileName(x) == TheLexer, "the lexer is the file this rule is about");

        List<string> offenders =
        [
            .. files
                .Where(x => Path.GetFileName(x) != TheLexer)
                .Where(x => CarriageReturnLiteral.IsMatch(File.ReadAllText(x)))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty(
            "where a comment, a string or a dollar body ends is SqlLexer's answer and there is one of it - " +
            "a second copy is how the same defect came to be fixed twice. Offenders: " +
            string.Join(", ", offenders));

        CarriageReturnLiteral.IsMatch(File.ReadAllText(RepositoryRoot.Combine(
                "src", "MartenStudio", "Internal", "Sql", TheLexer)))
            .Should().BeTrue("otherwise nobody knows about the carriage return at all, which is the defect");
    }

    /// <summary>The anti-vacuity half: the predicate has to be able to fail, and able not to.</summary>
    [Theory]
    [InlineData(@"while (i < sql.Length && sql[i] != '\n') { i++; }", 1)]
    [InlineData(@"while (i < sql.Length && sql[i] is not '\n') { i++; }", 1)]
    [InlineData(@"if (c == '\n') { return i; }", 1)]
    [InlineData(@"int end = text.IndexOf('\n');", 1)]
    [InlineData(@"while (i < sql.Length && sql[i] is not ('\n' or '\r')) { i++; }", 0)]
    [InlineData(@"if (c == '\n' || c == '\r') { return i; }", 0)]
    [InlineData(@"sql.Append('\n');", 0)]
    [InlineData(@"sql.Append(""from "").Append(table.QualifiedName).Append('\n');", 0)]
    [InlineData(@"sql.Append( '\n' );", 0)]
    [InlineData("// Postgres spells non_newline as [^\\n\\r], which is prose and not code", 0)]
    [InlineData("var ok = 1;\nwhile (sql[i] != '\\n') { i++; }", 1)]
    public void The_scan_finds_a_line_feed_only_terminator_and_leaves_a_written_newline_alone(
        string source,
        int expected) =>
        LineFeedOnlyTerminators(source).Should().HaveCount(expected, source);

    /// <summary>
    /// And the scan really is looking at the code that matters: the lexer's own terminator is a line the
    /// predicate would have flagged had it named only the line feed.
    /// </summary>
    [Fact]
    public void The_lexers_own_terminator_names_both_spellings()
    {
        string lexer = File.ReadAllText(
            RepositoryRoot.Combine("src", "MartenStudio", "Internal", "Sql", TheLexer));

        LineFeedOnlyTerminators(lexer).Should().BeEmpty();

        lexer.Should().Contain(@"is not ('\n' or '\r')",
            "that one expression is the whole carriage-return fix, and it is the line this suite exists to pin");
    }
}
