using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// XSO-fix2: every finding a tables-and-indexes check builds says which schemas outside the store's own it names
/// (<c>HealthFinding.GatedSchemas</c>), so that <c>HealthService</c> can withhold it from a visitor the database
/// browser's gate does not admit to them (AGENTS.md D27).
/// </summary>
/// <remarks>
/// <para>
/// A cheap backstop, not a proof: it reads source. Every <c>new HealthFinding { ... }</c> initializer in a check file
/// whose <c>[HealthCheck]</c> is in <c>HealthArea.Tables</c> must assign <c>GatedSchemas</c>, unless its file is in
/// <see cref="Exempt" /> with the reason it needs none. A new T-check that names a table and forgets the line fails
/// here, which is what the line's absence would otherwise do silently: show a table in a withheld schema by name.
/// </para>
/// <para>
/// Comments and string literals are stripped first (<see cref="SourceScanner" />), so a <c>GatedSchemas</c> that only
/// appears in prose does not count - which is also what could make the scan silently vacuous, hence
/// <see cref="The_scanner_finds_an_initializer_without_the_line_and_accepts_one_with_it" />.
/// </para>
/// </remarks>
public class TableFindingsCarryGatedSchemasTests
{
    /// <summary>The Tables-area check files that build findings without the line, and why each needs none.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["HiloHeadroomCheck.cs"] = "T11 is about mt_hilo, which is always in the store's document schema",
        ["HorizonHeldCheck.cs"] = "T14 names who holds the horizon back and only counts the store's tables",
        ["TransactionIdAgeCheck.cs"] = "T12 is database-wide and names a table outside the store's schemas generically (MartenTable.NoteName)",
        ["MultixactAgeCheck.cs"] = "T13 is database-wide and names a table outside the store's schemas generically (MartenTable.NoteName)",
        ["AutovacuumWorkersCheck.cs"] = "T16 is about the server's autovacuum workers and names no table",
    };

    private static readonly Regex TablesArea =
        new(@"\[HealthCheck\([^\]]*\bHealthArea\.Tables\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary><c>new HealthFinding { ... }</c>, or a method declared to return one whose body is <c>=&gt; new() { ... }</c>.</summary>
    private static readonly Regex NewFinding =
        new(@"(?:\bnew\s+HealthFinding\s*|\bHealthFinding\s+\w+\s*\([^)]*\)\s*=>\s*new\s*\(\s*\)\s*)\{", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    private static readonly Regex SetsGatedSchemas =
        new(@"\bGatedSchemas\s*=(?!=)", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_finding_a_tables_check_builds_says_which_gated_schemas_it_names()
    {
        string[] checks = TablesCheckFiles();
        checks.Length.Should().BeGreaterThanOrEqualTo(15, "scanning too few check files would pass for the wrong reason");

        // Both studios keep Tables-area checks since the move - the shared ones over any database, MartenStudio's over a
        // store (T11's mt_hilo) - so a scan of either folder alone would judge part of the area.
        foreach (string project in StudioProjects.Names)
        {
            checks.Should().Contain(
                x => StudioProjects.ProjectOf(SourceScanner.Relative(x)) == project,
                "{0} has Tables-area checks, and they are scanned too", project);
        }

        int initializers = 0;
        List<string> offenders = [];

        foreach (string file in checks)
        {
            string stripped = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));
            List<bool> found = Initializers(stripped);

            if (Exempt.ContainsKey(Path.GetFileName(file)))
            {
                continue;
            }

            initializers += found.Count;

            if (found.Any(static x => !x))
            {
                offenders.Add(SourceScanner.Relative(file));
            }
        }

        // Fourteen when this was written: T01, T02, T03 (two), T04, T05, T06 (two), T07, T08, T09, T10, T22 and T23.
        initializers.Should().BeGreaterThanOrEqualTo(14, "the scan has to see the initializers it judges");
        offenders.Should().BeEmpty(
            "a T-finding that names a table must carry HealthFinding.GatedSchemas (or its file must be exempt, with a reason). Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>An exemption names a Tables-area check file that still exists - a stale entry would exempt nothing, or the wrong file.</summary>
    [Fact]
    public void Every_exemption_is_a_tables_check_file_that_builds_findings()
    {
        // An exemption is keyed by file name, so a name is one file across both studios or the key exempts two.
        Dictionary<string, string> byName = TablesCheckFiles().ToDictionary(static x => Path.GetFileName(x), StringComparer.Ordinal);

        foreach ((string name, string reason) in Exempt)
        {
            byName.Should().ContainKey(name, "the exemption for {0} ({1}) must name a Tables-area check", name, reason);
            Initializers(SourceScanner.StripCommentsAndStrings(File.ReadAllText(byName[name]))).Should().NotBeEmpty(
                "{0} builds a finding, or it needs no exemption", name);
        }
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail, and to tell prose from code.</summary>
    [Theory]
    [InlineData("return new HealthFinding { Code = \"T99\", GatedSchemas = t.GatedSchemas, };", true)]
    [InlineData("return new HealthFinding\n{\n    Code = \"T99\",\n    Evidence = [new HealthEvidence(\"a\", \"b\")],\n    Remedy = new HealthRemedy(\"x\", \"y\") { },\n    GatedSchemas = [.. a.Select(static x => x.Schema)],\n};", true)]
    [InlineData("return new HealthFinding { Code = \"T99\" };", false)]
    [InlineData("return new HealthFinding { Code = \"T99\", // GatedSchemas = t.GatedSchemas,\n };", false)]
    [InlineData("return new HealthFinding { Code = \"T99\", Title = \"GatedSchemas = x\" };", false)]
    [InlineData("return new HealthFinding { Code = \"T99\", Evidence = [new HealthEvidence(\"a\", \"{\")], };", false)]
    [InlineData("bool same = finding.GatedSchemas == other; return new HealthFinding { Code = \"T99\" };", false)]
    [InlineData("private static HealthFinding Finding(\n    TableFacts table,\n    HealthEvaluation evaluation) => new()\n{\n    Code = \"T99\",\n};", false)]
    [InlineData("private static HealthFinding Finding(TableFacts table) => new()\n{\n    Code = \"T99\",\n    GatedSchemas = table.Table.GatedSchemas,\n};", true)]
    public void The_scanner_finds_an_initializer_without_the_line_and_accepts_one_with_it(string source, bool sets)
    {
        List<bool> found = Initializers(SourceScanner.StripCommentsAndStrings(source));

        found.Should().ContainSingle().Which.Should().Be(sets);
    }

    /// <summary>
    /// Every check file in either studio's <c>Services/Health/Checks</c> whose <c>[HealthCheck]</c> is in the Tables area.
    /// </summary>
    private static string[] TablesCheckFiles() =>
        [.. StudioProjects.Files("*.cs", SearchOption.TopDirectoryOnly, "Services", "Health", "Checks")
            .Where(static x => TablesArea.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))))];

    /// <summary>For each <c>new HealthFinding { ... }</c> in stripped source, whether its own braces assign <c>GatedSchemas</c>.</summary>
    private static List<bool> Initializers(string stripped)
    {
        List<bool> result = [];

        foreach (Match match in NewFinding.Matches(stripped))
        {
            int open = match.Index + match.Length - 1;
            int depth = 0;
            int end = stripped.Length;

            for (int i = open; i < stripped.Length; i++)
            {
                if (stripped[i] == '{')
                {
                    depth++;
                }
                else if (stripped[i] == '}' && --depth == 0)
                {
                    end = i;
                    break;
                }
            }

            result.Add(SetsGatedSchemas.IsMatch(stripped[open..end]));
        }

        return result;
    }
}
