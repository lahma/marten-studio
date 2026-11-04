using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Hard rule 4, as text: no statement is written as a literal into a command outside the builders' folders
/// (<c>src/PostgresStudio.Diagnostics/Internal/Sql/</c>, <c>src/PostgresStudio/Internal/Sql/</c>, <c>src/MartenStudio/Internal/Sql/</c>).
/// </summary>
/// <remarks>
/// <para>
/// Added by 1.2-SCOPE-2, which moved <c>TenantDiscovery</c>'s tier-3 statement out of <c>Services/</c>: it had been there since F8, and
/// <c>HealthQueryConventionTests</c> could not see it, because that fence holds <em>health</em> statements (the <c>HealthQuery</c> type and
/// the builders' namespaces) and a statement assigned straight to <c>CommandText</c> is neither. This is the missing half, narrow on
/// purpose: a command whose text is a string literal - plain, interpolated, verbatim or raw - assigned to <c>CommandText</c> or handed to
/// <c>new NpgsqlCommand(…)</c>. A statement a builder returned is a call, not a literal, and passes. What it does not see is a literal
/// built in a variable first; review is what keeps that out, and <see cref="The_scan_sees_the_statement_that_used_to_be_in_TenantDiscovery" />
/// proves the pattern is not vacuous.
/// </para>
/// </remarks>
public partial class NoSqlTextOutsideBuildersTests
{
    private static readonly string[] BuilderFolders =
    [
        "src/PostgresStudio.Diagnostics/Internal/Sql/",
        "src/PostgresStudio/Internal/Sql/",
        "src/MartenStudio/Internal/Sql/",
    ];

    [GeneratedRegex("""" (CommandText\s*=\s*|new\s+(?:Npgsql)?(?:Batch)?Command\s*\(\s*)[$@]*(?:"|""") """", RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex LiteralCommand();

    [Fact]
    public void No_command_is_given_a_literal_statement_outside_the_builders()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty();

        List<string> offenders = [];

        foreach (string file in files)
        {
            string relative = SourceScanner.Relative(file);

            if (BuilderFolders.Any(folder => relative.StartsWith(folder, StringComparison.Ordinal)))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();

                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*') || line.StartsWith('@') && line.Contains("*@", StringComparison.Ordinal))
                {
                    continue;
                }

                if (LiteralCommand().IsMatch(lines[i]) || (i + 1 < lines.Length && EndsInAssignment(lines[i]) && LiteralCommand().IsMatch(lines[i] + lines[i + 1].TrimStart())))
                {
                    offenders.Add(relative + ":" + (i + 1));
                }
            }
        }

        offenders.Should().BeEmpty("SQL text is built in an Internal/Sql builder and nowhere else (hard rule 4): " + string.Join(", ", offenders));
    }

    private static bool EndsInAssignment(string line) => line.TrimEnd().EndsWith("CommandText =", StringComparison.Ordinal);

    [Theory]
    [InlineData("command.CommandText = $\"select tenant_id from ({branches})\";", true)]
    [InlineData("command.CommandText =\n            $\"select tenant_id\";", true)]
    [InlineData("var c = new NpgsqlCommand(\"select 1\", connection);", true)]
    [InlineData("var c = new NpgsqlCommand(\"\"\"select 1\"\"\", connection);", true)]
    [InlineData("command.CommandText = TenantIdQueries.DistinctTenantIds(tables, 200);", false)]
    [InlineData("var c = new NpgsqlCommand(sql, connection);", false)]
    [InlineData("// command.CommandText = \"select 1\";", false)]
    public void The_scan_sees_the_statement_that_used_to_be_in_TenantDiscovery(string source, bool offends)
    {
        string[] lines = source.Split('\n');
        bool found = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimStart();

            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            found |= LiteralCommand().IsMatch(lines[i]) || (i + 1 < lines.Length && EndsInAssignment(lines[i]) && LiteralCommand().IsMatch(lines[i] + lines[i + 1].TrimStart()));
        }

        found.Should().Be(offends);
    }
}
