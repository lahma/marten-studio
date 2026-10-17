using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Query;

/// <summary>
/// Nothing in <c>src/</c> builds a LINQ statement with Marten's <c>ToCommand</c> or <c>ExplainAsync</c>, which create
/// the queried type's table on the way (hard rule 14).
/// </summary>
/// <remarks>
/// <para>
/// <c>ToCommand</c> is <c>MartenLinqQueryable.ToPreviewCommand</c>, which ends with
/// <c>Session.Database.EnsureStorageExists(documentType)</c>; <c>ExplainAsync</c> calls the same method (Marten
/// <c>V9.39.0</c>, <c>V9.50.2</c>). LINQ mode builds the same statement through <c>MartenLinqCommand</c> without that
/// step, and a later "simplification" back to <c>ToCommand</c> would be a studio that creates tables because somebody
/// pressed Run - working, and quietly wrong. <c>MartenLinqCommandTests</c> proves the difference without a database;
/// this keeps the call from coming back.
/// </para>
/// <para>
/// The scan strips comments and strings first, so this file's own subject can be written about - which is what could
/// make it vacuous, so <see cref="The_scanner_finds_a_real_call_and_ignores_one_in_prose" /> proves it still bites.
/// </para>
/// </remarks>
public partial class LinqNoToCommandTests
{
    [Fact]
    public void No_shipped_file_calls_ToCommand_or_ExplainAsync()
    {
        string[] files = SourceScanner.ShippedSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] offenders =
        [
            .. files
                .Where(file => Call().IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(file))))
                .Select(SourceScanner.Relative)
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty(
            "ToCommand and ExplainAsync run EnsureStorageExists, which creates and migrates the table; build Marten's SQL " +
            "through MartenLinqCommand. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scanner_finds_a_real_call_and_ignores_one_in_prose()
    {
        Call().IsMatch(SourceScanner.StripCommentsAndStrings("var command = query.ToCommand();")).Should().BeTrue();
        Call().IsMatch(SourceScanner.StripCommentsAndStrings("var plan = await query.ExplainAsync(token: token);")).Should().BeTrue();
        Call().IsMatch(SourceScanner.StripCommentsAndStrings("// never query.ToCommand() here")).Should().BeFalse();
        Call().IsMatch(SourceScanner.StripCommentsAndStrings("string why = \"query.ToCommand() creates tables\";")).Should().BeFalse();
        Call().IsMatch(SourceScanner.StripCommentsAndStrings("/// <c>ToCommand</c> is ToPreviewCommand")).Should().BeFalse();
    }

    [GeneratedRegex(@"\.\s*(?:ToCommand|ExplainAsync)\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Call();
}
