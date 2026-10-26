using System.Text;
using System.Text.RegularExpressions;

using MartenStudio.Services;
using MartenStudio.Tests.Conventions;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The code vocabulary's convention (API design §1.10 and §5.2, D31): <c>docs/health/codes.md</c> is the list of every code
/// there is, held to the registered checks of both studios - this is the one project that sees both check assemblies.
/// Every registered code is a row with the same area, variants and studios; every row is registered or retired; no retired
/// code is registered; and the table's canonical rendering - the stable columns, not the wording - is a reviewed Verify
/// baseline, so editing or deleting a row is a reviewed change under hard rule 7's process.
/// </summary>
public class HealthCodesTests
{
    /// <summary>The header the parser looks for. The table is the first one in the file with exactly these columns.</summary>
    internal const string Header = "| Code | Title | Area | Variants | Subject | Studios | Since | Retired |";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex CodeCell = new(@"\A\[(?<code>[A-Z][0-9]{2})\]\((?<link>[a-z]+\.md#[a-z0-9_-]+)\)\z", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Variant = new(@"\A`(?<name>[a-z]+(?:-[a-z]+)*)`\z", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Release = new(@"\A[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>One row of the table, as written.</summary>
    internal sealed record CodeRow(
        string Code,
        string Link,
        string Title,
        string Area,
        IReadOnlyList<string> Variants,
        string Subject,
        string Studios,
        string Since,
        string? Retired);

    /// <summary>One registered check, as the table must describe it.</summary>
    internal sealed record RegisteredCode(string Code, string Title, HealthArea Area, IReadOnlyList<string> Variants, string Studios, string Link);

    /// <summary>Every check either studio registers: <c>both</c> when Postgres Studio runs it too, <c>Marten</c> otherwise.</summary>
    internal static IReadOnlyList<RegisteredCode> Registered()
    {
        HealthCheckRegistry marten = new(MartenStudioDescriptor.Instance);
        HealthCheckRegistry postgres = new(PostgresStudioDescriptor.Instance);
        HashSet<string> inMarten = [.. marten.All.Select(static x => x.Info.Code)];
        HashSet<string> inPostgres = [.. postgres.All.Select(static x => x.Info.Code)];

        return
        [
            .. marten.All.Concat(postgres.All)
                .Select(static x => x.Info)
                .DistinctBy(static x => x.Code)
                .OrderBy(static x => x.Code, StringComparer.Ordinal)
                .Select(x => new RegisteredCode(
                    x.Code,
                    x.Title,
                    x.Area,
                    x.Variants,
                    (inMarten.Contains(x.Code), inPostgres.Contains(x.Code)) switch
                    {
                        (true, true) => "both",
                        (true, false) => "Marten",
                        _ => "Postgres",
                    },
                    HealthCheckInfo.AreaFileName(x.Area) + "#" + x.DocsAnchor)),
        ];
    }

    /// <summary>The rows of the first table under <see cref="Header" /> outside a code fence.</summary>
    internal static IReadOnlyList<CodeRow> Parse(string markdown)
    {
        List<CodeRow> rows = [];
        bool fenced = false;
        bool inTable = false;
        bool separatorSeen = false;

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                continue;
            }

            if (!inTable)
            {
                if (rows.Count == 0 && string.Equals(line, Header, StringComparison.Ordinal))
                {
                    inTable = true;
                }

                continue;
            }

            if (!separatorSeen)
            {
                separatorSeen = true;
                continue;
            }

            if (!line.StartsWith("| ", StringComparison.Ordinal) || !line.EndsWith(" |", StringComparison.Ordinal))
            {
                break;
            }

            string[] cells = line[2..^2].Split(" | ");

            if (cells.Length != 8)
            {
                throw new FormatException("A row of codes.md has " + cells.Length + " cells, not 8: " + line);
            }

            Match code = CodeCell.Match(cells[0]);

            if (!code.Success)
            {
                throw new FormatException("A row of codes.md does not start with [Code](area.md#anchor): " + line);
            }

            rows.Add(new CodeRow(
                code.Groups["code"].Value,
                code.Groups["link"].Value,
                cells[1],
                cells[2],
                cells[3] == "-" ? [] : [.. cells[3].Split(", ").Select(VariantName)],
                cells[4],
                cells[5],
                cells[6],
                cells[7] == "-" ? null : cells[7]));
        }

        return rows;

        static string VariantName(string cell)
        {
            Match match = Variant.Match(cell);
            return match.Success ? match.Groups["name"].Value : throw new FormatException("A variant in codes.md is not `lower-case-words`: " + cell);
        }
    }

    /// <summary>Everything wrong with the table against the registered checks, one line each.</summary>
    internal static List<string> Problems(IReadOnlyList<CodeRow> rows, IReadOnlyList<RegisteredCode> registered)
    {
        List<string> problems = [];
        Dictionary<string, RegisteredCode> byCode = registered.ToDictionary(static x => x.Code, StringComparer.Ordinal);

        foreach (IGrouping<string, CodeRow> duplicate in rows.GroupBy(static x => x.Code, StringComparer.Ordinal).Where(static x => x.Count() > 1))
        {
            problems.Add(duplicate.Key + " has " + duplicate.Count() + " rows; a code has one.");
        }

        for (int i = 1; i < rows.Count; i++)
        {
            if (string.CompareOrdinal(rows[i - 1].Code, rows[i].Code) >= 0)
            {
                problems.Add(rows[i].Code + " is out of order after " + rows[i - 1].Code + "; the table is sorted by code.");
            }
        }

        foreach (RegisteredCode check in registered)
        {
            CodeRow? row = rows.FirstOrDefault(x => x.Code == check.Code);

            if (row is null)
            {
                problems.Add(check.Code + " is registered and has no row in docs/health/codes.md.");
                continue;
            }

            if (row.Retired is not null)
            {
                problems.Add(check.Code + " is marked retired in " + row.Retired + " and is still registered; a retired code is never reassigned.");
            }

            if (row.Area != check.Area.ToString())
            {
                problems.Add(check.Code + "'s row says area " + row.Area + "; the check's is " + check.Area + ".");
            }

            if (!row.Variants.SequenceEqual(check.Variants, StringComparer.Ordinal))
            {
                problems.Add(check.Code + "'s row lists variants [" + string.Join(", ", row.Variants) + "]; the check's are [" + string.Join(", ", check.Variants) + "].");
            }

            if (row.Studios != check.Studios)
            {
                problems.Add(check.Code + "'s row says studios " + row.Studios + "; it runs in " + check.Studios + ".");
            }

            if (row.Title != check.Title)
            {
                problems.Add(check.Code + "'s row is titled '" + row.Title + "'; the check is '" + check.Title + "'.");
            }

            if (row.Link != check.Link)
            {
                problems.Add(check.Code + "'s row links " + row.Link + "; its heading is at " + check.Link + ".");
            }
        }

        foreach (CodeRow row in rows)
        {
            if (!byCode.ContainsKey(row.Code) && row.Retired is null)
            {
                problems.Add(row.Code + " has a row and is neither registered nor marked retired.");
            }

            if (HealthCheckRegistry.AreaOf(row.Code[0])?.ToString() != row.Area)
            {
                problems.Add(row.Code + "'s letter does not name area " + row.Area + "; a code never moves area.");
            }

            if (!Release.IsMatch(row.Since))
            {
                problems.Add(row.Code + "'s Since is '" + row.Since + "', not a release such as 0.5.");
            }

            if (row.Retired is { } retired && !Release.IsMatch(retired))
            {
                problems.Add(row.Code + "'s Retired is '" + retired + "', not '-' or a release such as 0.6.");
            }

            if (row.Subject.Length == 0)
            {
                problems.Add(row.Code + " names no subject kind; '-' is for a finding about the whole.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The table's stable columns, one line per code: what a host's wording keys on. The title and the anchor are wording,
    /// and are held to the registry rather than to the baseline.
    /// </summary>
    internal static string Canonical(IReadOnlyList<CodeRow> rows)
    {
        StringBuilder text = new();

        foreach (CodeRow row in rows)
        {
            text.Append(row.Code)
                .Append(" | ").Append(row.Area)
                .Append(" | ").Append(row.Variants.Count == 0 ? "-" : string.Join(", ", row.Variants))
                .Append(" | ").Append(row.Subject)
                .Append(" | ").Append(row.Studios)
                .Append(" | since ").Append(row.Since)
                .Append(" | ").Append(row.Retired is null ? "current" : "retired in " + row.Retired)
                .Append('\n');
        }

        return text.ToString();
    }

    private static IReadOnlyList<CodeRow> Table() => Parse(File.ReadAllText(RepositoryRoot.Combine("docs", "health", "codes.md")));

    [Fact]
    public void Every_registered_code_has_its_row_and_every_row_is_registered_or_retired()
    {
        IReadOnlyList<CodeRow> rows = Table();
        IReadOnlyList<RegisteredCode> registered = Registered();

        registered.Should().HaveCountGreaterThan(70, "both studios' checks are found, or this compares nothing");
        rows.Should().HaveCountGreaterThanOrEqualTo(registered.Count);

        List<string> problems = Problems(rows, registered);
        problems.Should().BeEmpty(string.Join(" | ", problems));
    }

    [Fact]
    public void Both_studios_checks_are_in_the_table()
    {
        IReadOnlyList<CodeRow> rows = Table();

        rows.Should().Contain(static x => x.Code == "D01" && x.Studios == "Marten", "a Marten check's row is there");
        rows.Should().Contain(static x => x.Code == "T03" && x.Studios == "both", "a shared check's row is there");
        rows.Single(static x => x.Code == "C08").Variants.Should().Equal("deadlocks", "rollbacks", "temp-files");
    }

    [Fact]
    public Task The_tables_stable_columns_are_the_reviewed_baseline() =>
        Verify(Canonical(Table()), extension: "txt")
            .UseDirectory("../Verify")
            .UseFileName("HealthCodesTests");

    // -----------------------------------------------------------------------------------------------
    // Anti-vacuity: the parser finds the table and nothing else, and every rule catches its offender
    // -----------------------------------------------------------------------------------------------

    private const string Separator = "|---|---|---|---|---|---|---|---|";

    private static string Markdown(params string[] rows) =>
        "# Codes\n\n```\n" + Header + "\n" + Separator + "\n| [Z99](x.md#z99) | fenced | Tables | - | - | both | 0.5 | - |\n```\n\n"
        + Header + "\n" + Separator + "\n" + string.Join("\n", rows) + "\n\nProse.\n\n"
        + Header + "\n" + Separator + "\n| [T98](tables.md#t98-second) | second table | Tables | - | - | both | 0.5 | - |\n";

    private const string GoodRow = "| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | `index`, `partitions` | `index`: index | both | 0.4 | - |";

    private static readonly RegisteredCode T03 = new("T03", "Invalid index", HealthArea.Tables, ["index", "partitions"], "both", "tables.md#t03-invalid-index");

    [Fact]
    public void The_parser_reads_the_first_table_and_ignores_a_fenced_one_and_a_later_one()
    {
        IReadOnlyList<CodeRow> rows = Parse(Markdown(GoodRow));

        rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new CodeRow(
            "T03", "tables.md#t03-invalid-index", "Invalid index", "Tables", ["index", "partitions"], "`index`: index", "both", "0.4", null));
        Problems(rows, [T03]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "is registered and has no row")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Server | `index`, `partitions` | index | both | 0.4 | - |", "says area Server")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | `index` | index | both | 0.4 | - |", "lists variants [index]")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | `partitions`, `index` | index | both | 0.4 | - |", "lists variants [partitions, index]")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | `index`, `partitions` | index | Marten | 0.4 | - |", "says studios Marten")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | `index`, `partitions` | index | both | 0.4 | 0.6 |", "is still registered")]
    [InlineData("| [T03](tables.md#t03-bad-anchor) | Invalid index | Tables | `index`, `partitions` | index | both | 0.4 | - |", "its heading is at")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid indexes | Tables | `index`, `partitions` | index | both | 0.4 | - |", "is titled")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | `index`, `partitions` | index | both | soon | - |", "not a release")]
    public void A_row_that_does_not_describe_its_check_is_caught(string row, string problem)
    {
        Problems(Parse(Markdown(row)), [T03]).Should().Contain(x => x.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_for_no_registered_check_must_be_retired_and_a_retired_row_stays()
    {
        const string Unregistered = "| [T97](tables.md#t97-gone) | Gone | Tables | - | table | both | 0.4 | - |";
        const string Retired = "| [T97](tables.md#t97-gone) | Gone | Tables | - | table | both | 0.4 | 0.6 |";

        Problems(Parse(Markdown(GoodRow, Unregistered)), [T03]).Should().ContainSingle()
            .Which.Should().Contain("neither registered nor marked retired");
        Problems(Parse(Markdown(GoodRow, Retired)), [T03]).Should().BeEmpty("a retired check keeps its row");
    }

    [Fact]
    public void A_duplicate_an_unsorted_row_and_a_moved_area_are_caught()
    {
        const string Again = GoodRow;
        const string Earlier = "| [T01](tables.md#t01-x) | x | Tables | - | table | both | 0.4 | 0.5 |";
        const string Moved = "| [T99](server.md#t99-x) | x | Server | - | - | both | 0.4 | 0.5 |";

        List<string> problems = Problems(Parse(Markdown(GoodRow, Again, Earlier, Moved)), [T03]);

        problems.Should().Contain(static x => x.Contains("T03 has 2 rows", StringComparison.Ordinal))
            .And.Contain(static x => x.Contains("T01 is out of order", StringComparison.Ordinal))
            .And.Contain(static x => x.Contains("T99's letter does not name area Server", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("| T03 | Invalid index | Tables | - | - | both | 0.4 | - |")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | index | - | both | 0.4 | - |")]
    [InlineData("| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | - | both | 0.4 | - |")]
    public void A_malformed_row_fails_the_parse_rather_than_passing_for_absent(string row)
    {
        Action parse = () => Parse(Markdown(row));
        parse.Should().Throw<FormatException>();
    }
}
