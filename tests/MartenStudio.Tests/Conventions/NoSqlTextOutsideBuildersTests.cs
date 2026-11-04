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
/// purpose: a command whose text is a string literal - plain, interpolated, verbatim or raw - given to <c>CommandText =</c> or
/// <c>CommandText +=</c>, to <c>new NpgsqlCommand(…)</c> (the literal may start the next line), to a target-typed <c>new("select …", c)</c>
/// (taken only for a literal that opens with a SQL verb and is followed by a connection, transaction or data source, since
/// <c>new("Update batch size", Number(x))</c> is a label, not a command), or to a data source's
/// <c>CreateCommand("…")</c> / <c>CreateBatchCommand("…")</c>. A statement a builder returned is a call, not a literal, and passes. What it
/// does not see is a literal held in a variable first (<c>var sql = "select …"; new NpgsqlCommand(sql, c)</c>); review is what keeps that
/// out, and <see cref="The_scan_sees_each_way_of_writing_a_statement_into_a_command" /> proves each pattern is not vacuous.
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

    /// <summary>A string literal's opening: plain, interpolated, verbatim, or the first quotes of a raw one.</summary>
    private const string LiteralStart = """"[$@]*"+"""";

    /// <summary>A literal that opens with a SQL verb, for the shapes that are not recognisably a command by their name.</summary>
    private const string SqlLiteralStart = """"[$@]*"+\s*(?:select|with|insert|update|delete|set|call|create|alter|drop|lock|explain|vacuum|analyze|truncate|copy|begin|show)\b"""";

    [GeneratedRegex(@"(?:CommandText\s*\+?=\s*|new\s+(?:Npgsql)?(?:Batch)?Command\s*\(\s*|Create(?:Batch)?Command\s*\(\s*)" + LiteralStart, RegexOptions.CultureInvariant)]
    private static partial Regex NamedCommand();

    [GeneratedRegex(@"\bnew\s*\(\s*" + SqlLiteralStart + @"[^;]*""\s*,\s*\w*(?:onn|ransaction|ataSource)\w*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TargetTypedCommand();

    /// <summary>The lines (zero-based) of <paramref name="lines" /> that put a literal statement into a command.</summary>
    internal static IReadOnlyList<int> Offences(IReadOnlyList<string> lines)
    {
        List<int> found = [];

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].TrimStart();

            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('*') || (line.StartsWith('@') && line.Contains("*@", StringComparison.Ordinal)))
            {
                continue;
            }

            // A statement may begin on the line after the call or the assignment.
            string joined = i + 1 < lines.Count && EndsOpen(lines[i]) ? lines[i] + " " + lines[i + 1].TrimStart() : lines[i];

            if (NamedCommand().IsMatch(joined) || TargetTypedCommand().IsMatch(joined))
            {
                found.Add(i);
            }
        }

        return found;
    }

    private static bool EndsOpen(string line)
    {
        string trimmed = line.TrimEnd();

        return trimmed.EndsWith('(') || trimmed.EndsWith('=');
    }

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

            offenders.AddRange(Offences(File.ReadAllLines(file)).Select(line => relative + ":" + (line + 1)));
        }

        offenders.Should().BeEmpty("SQL text is built in an Internal/Sql builder and nowhere else (hard rule 4): " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("command.CommandText = $\"select tenant_id from ({branches})\";", true)]
    [InlineData("command.CommandText =\n            $\"select tenant_id\";", true)]
    [InlineData("command.CommandText += \" order by 1\";", true)]
    [InlineData("var c = new NpgsqlCommand(\"select 1\", connection);", true)]
    [InlineData("var c = new NpgsqlCommand(\"\"\"select 1\"\"\", connection);", true)]
    [InlineData("var c = new NpgsqlCommand(\n    \"select 1\",\n    connection);", true)]
    [InlineData("NpgsqlCommand c = new(\"select 1\", connection);", true)]
    [InlineData("NpgsqlCommand c = new(\n    @\"Select 1\", connection);", true)]
    [InlineData("await using var c = dataSource.CreateCommand(\"select 1\");", true)]
    [InlineData("await using var b = dataSource.CreateBatchCommand(\"select 1\");", true)]
    [InlineData("command.CommandText = TenantIdQueries.DistinctTenantIds(tables, 200);", false)]
    [InlineData("command.CommandText += builder.Suffix();", false)]
    [InlineData("var c = new NpgsqlCommand(sql, connection);", false)]
    [InlineData("var c = dataSource.CreateCommand(sql);", false)]
    [InlineData("var pair = new(\"alpha\", \"beta\");", false)]
    [InlineData("var cell = new(\"selected tenant\", title);", false)]
    [InlineData("// command.CommandText = \"select 1\";", false)]
    [InlineData("/// new NpgsqlCommand(\"select 1\") is the shape.", false)]
    public void The_scan_sees_each_way_of_writing_a_statement_into_a_command(string source, bool offends)
    {
        Offences(source.Split('\n')).Any().Should().Be(offends);
    }

    [Fact]
    public void A_literal_held_in_a_variable_first_is_the_documented_gap()
    {
        Offences("var sql = \"select 1\";\nvar c = new NpgsqlCommand(sql, connection);".Split('\n')).Should().BeEmpty(
            "the scan reads a literal handed to the command, not one a variable carried; review keeps that out");
    }
}
