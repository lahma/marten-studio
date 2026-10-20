using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// MartenStudio's <see cref="IHealthPlanAdvisor" />: what a sequential scan of a document table filters on - a duplicated
/// column, a JSON path whose keys are literals, a containment test - judged by the Query page's own index advisor
/// (<see cref="IndexAdvisor" />) against the indexes the sweep read.
/// </summary>
/// <remarks>
/// Pure: the document type is the one MartenStudio described the table with (<see cref="MartenTables.Table" />), and the
/// filter is the plan's deparsed text. A table that is not a visible document type's is not one it advises on.
/// </remarks>
internal sealed partial class MartenStatementPlanAdvisor : IHealthPlanAdvisor
{
    /// <summary>The one instance: it holds nothing.</summary>
    public static MartenStatementPlanAdvisor Instance { get; } = new();

    /// <inheritdoc />
    public HealthScanAdvice? AdviseSequentialScan(StudioTable table, string? filter, IReadOnlyList<IndexFacts> indexesOnTable)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(indexesOnTable);

        if (table.Kind != StudioTableKind.DocumentTable || table.DocumentType is not { } type)
        {
            return null;
        }

        DocumentTableInfo info = DocumentTableInfo.FromDocumentType(type);
        Filter read = FilterOf(filter, info);

        IReadOnlyList<PostgresIndex> onTable = [.. indexesOnTable.Select(static x => new PostgresIndex(x.Row.Name, x.Row.Definition))];
        List<HealthIndexVerdict> verdicts = [];

        foreach ((string subject, DocumentPredicate predicate) in read.Members)
        {
            IndexVerdict verdict = IndexAdvisor.Evaluate(info, onTable, predicate);

            if (verdict.Level != IndexVerdictLevel.Green && verdict.Suggestion is { } suggestion)
            {
                verdicts.Add(new HealthIndexVerdict(subject, verdict.Reason, suggestion));
            }
        }

        return new HealthScanAdvice([.. read.Members.Select(static x => x.Subject)], read.UnnamedMember, verdicts);
    }

    /// <summary>What a whole-table read filters on, as far as its deparsed filter says.</summary>
    /// <param name="Members">Each member it filters on, with the predicate the advisor judges.</param>
    /// <param name="UnnamedMember">Whether it filters on a JSON member whose key is a placeholder.</param>
    internal sealed record Filter(IReadOnlyList<(string Subject, DocumentPredicate Predicate)> Members, bool UnnamedMember);

    /// <summary>
    /// The members <paramref name="filter" /> - a plan's deparsed <c>Filter</c> - reads: a duplicated column by its name, a
    /// JSON path whose keys are literals, a containment test; and whether it reads a JSON member through a placeholder.
    /// </summary>
    internal static Filter FilterOf(string? filter, DocumentTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (string.IsNullOrWhiteSpace(filter))
        {
            return new Filter([], false);
        }

        // Postgres parenthesises every operator it deparses; none of the shapes below needs them.
        string flat = filter.Replace('(', ' ').Replace(')', ' ');
        List<(string Subject, DocumentPredicate Predicate)> members = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool unnamed = false;

        void Add(string subject, DocumentPredicate predicate)
        {
            if (seen.Add(subject + "\u001f" + predicate.GetType().Name))
            {
                members.Add((subject, predicate));
            }
        }

        foreach (Match match in JsonChain().Matches(flat))
        {
            List<string> path = [];
            bool placeholder = false;

            foreach (Match step in JsonStep().Matches(match.Groups["chain"].Value))
            {
                string key = step.Groups["key"].Value;

                if (key.StartsWith('$'))
                {
                    placeholder = true;
                    break;
                }

                path.Add(key[1..^1].Replace("''", "'", StringComparison.Ordinal));
            }

            if (placeholder)
            {
                unnamed = true;
                continue;
            }

            bool like = Like().IsMatch(flat[(match.Index + match.Length)..]);
            string subject = string.Join('.', path);

            Add(subject, like
                ? new DocumentPredicate.FieldLike(path, string.Empty)
                : new DocumentPredicate.FieldCompare(path, ComparisonOperator.Equal, new SearchValue.Text(string.Empty)));
        }

        foreach (Match match in JsonPathOperator().Matches(flat))
        {
            if (!match.Groups["path"].Success)
            {
                unnamed = true;
                continue;
            }

            string[] path = match.Groups["path"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (path.Length > 0)
            {
                Add(string.Join('.', path), new DocumentPredicate.FieldCompare(path, ComparisonOperator.Equal, new SearchValue.Text(string.Empty)));
            }
        }

        if (Containment().IsMatch(flat))
        {
            Add("JSON containment (@>)", new DocumentPredicate.Contains("{}"));
        }

        // A duplicated column by its own name, outside any literal - which is where a JSON key would be.
        string unquoted = Literal().Replace(flat, "''");

        foreach (DuplicatedColumnInfo column in table.DuplicatedColumns)
        {
            if (Regex.IsMatch(
                    unquoted,
                    "(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\\.)?" + Regex.Escape(column.ColumnName) + "(?![A-Za-z0-9_$])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)))
            {
                Add(column.MemberPath + " (duplicated column " + column.ColumnName + ")",
                    new DocumentPredicate.FieldCompare(column.MemberPath.Split('.'), ComparisonOperator.Equal, new SearchValue.Text(string.Empty)));
            }
        }

        return new Filter(members, unnamed);
    }

    /// <summary><c>data -&gt; 'A' -&gt;&gt; 'B'</c>, a key a literal or a placeholder, optionally qualified by an alias.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data(?<chain>(?:\s*->>?\s*(?:'(?:[^']|'')*'|\$[0-9]+)(?:\s*::\s*[a-z]+)?)+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonChain();

    /// <summary>One step of a <see cref="JsonChain" />.</summary>
    [GeneratedRegex(@"->>?\s*(?<key>'(?:[^']|'')*'|\$[0-9]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonStep();

    /// <summary><c>data #&gt;&gt; '{A,B}'</c>, or with a placeholder for the path.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data\s*#>>?\s*(?:'\{(?<path>[^}']*)\}'|\$[0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonPathOperator();

    /// <summary><c>data @&gt;</c>: a containment test on the whole document.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data\s*@>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Containment();

    /// <summary>What a LIKE (<c>~~</c>) or ILIKE (<c>~~*</c>) after a JSON path looks like, casts allowed between.</summary>
    [GeneratedRegex(@"^\s*(?:::\s*[a-z]+\s*)*~~", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Like();

    /// <summary>A string literal.</summary>
    [GeneratedRegex("'(?:[^']|'')*'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Literal();
}
