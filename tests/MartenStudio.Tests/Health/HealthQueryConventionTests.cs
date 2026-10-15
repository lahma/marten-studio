using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Hard rule 4 for health checks: a health statement is built under <c>Internal/Sql/</c> and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// A check's gatherer hands <c>context.Sql</c> a <c>HealthQuery</c>, and a <c>HealthQuery</c> is SQL text. The
/// rule that makes the studio's SQL reviewable is that the one place an identifier becomes SQL text is the
/// builders' folder, so a check that wrote <c>new HealthQuery("select ... " + name)</c> in its own file would
/// be the second place. This scans every shipped C# and Razor file outside <c>src/MartenStudio/Internal/Sql/</c>
/// for a <c>HealthQuery</c> being made - by <c>new HealthQuery(</c>, by a target-typed <c>new(</c> into a
/// <c>HealthQuery</c> variable, or by a target-typed <c>new(</c> handed straight to the runner.
/// </para>
/// <para>
/// The scan strips comments and strings first, which is what lets prose quote the pattern - and what could
/// make it vacuous, so <see cref="The_scan_finds_a_query_built_outside_the_builders_and_ignores_prose" /> proves
/// it still fails.
/// </para>
/// </remarks>
public class HealthQueryConventionTests
{
    private static readonly Regex Construction = new(
        @"\bnew\s+HealthQuery\s*\(|\bHealthQuery\??\s+\w+\s*=\s*new\s*\(|\bSql\s*\.\s*(?:QueryAsync|ScalarAsync)\s*(?:<[^>]*>)?\s*\(\s*new\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private const string Builders = "src/MartenStudio/Internal/Sql/";

    [Fact]
    public void Every_health_query_is_built_under_Internal_Sql()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        List<string> offenders =
        [
            .. files
                .Select(static file => (Path: SourceScanner.Relative(file), Text: File.ReadAllText(file)))
                .Where(static x => !x.Path.StartsWith(Builders, StringComparison.Ordinal) && Builds(x.Text))
                .Select(static x => x.Path),
        ];

        offenders.Should().BeEmpty(
            "a health statement is SQL text, and SQL text is built in Internal/Sql/ and nowhere else (hard rule 4): "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("var q = new HealthQuery(\"select 1\");", true)]
    [InlineData("HealthQuery q = new(\"select 1\", []);", true)]
    [InlineData("HealthQuery? q = new(\"select 1\");", true)]
    [InlineData("await context.Sql.QueryAsync(new(\"select 1\"), Map, token);", true)]
    [InlineData("await context.Sql.ScalarAsync<long>(new(\"select 1\"), token);", true)]
    [InlineData("await context.Sql.QueryAsync(HealthTableQueries.Stats(schemas), Map, token);", false)]
    [InlineData("// new HealthQuery(\"select 1\")\nclass C { }", false)]
    [InlineData("/// <c>new HealthQuery(sql)</c>\nclass C { }", false)]
    [InlineData("string s = \"new HealthQuery(x)\";", false)]
    [InlineData("internal sealed record HealthQuery(string Sql, IReadOnlyList<NpgsqlParameter> Parameters)", false)]
    public void The_scan_finds_a_query_built_outside_the_builders_and_ignores_prose(string source, bool builds)
    {
        Builds(source).Should().Be(builds);
    }

    private static bool Builds(string source) => Construction.IsMatch(SourceScanner.StripCommentsAndStrings(source));
}
