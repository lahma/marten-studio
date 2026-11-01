using MartenStudio.Tests.Conventions;

using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The plan analyzer's corpus (X3, issue #28): <c>PlanCorpus/*.json</c>, each file one <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c>
/// output as PostgreSQL 17 prints it - for each of P09 to P14 one plan that fires it and one that does not, and
/// <c>analyzed-join.json</c>, the plan the views, the Markdown and the speedscope export are tested on, whose numbers are chosen
/// so that every own time, own cost and own buffer count can be read off by hand.
/// </summary>
internal static class PlanFixtures
{
    /// <summary>The prefix of 0.10-B's pair fixtures, which are plans meant to be compared and are not on <see cref="Names" />.</summary>
    private const string PairPrefix = "pair-";

    /// <summary>The names of every fixture, without the extension, but for the pairs'.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        .. Directory.GetFiles(CorpusPath(), "*.json").Select(static x => Path.GetFileNameWithoutExtension(x))
            .Where(static x => !x.StartsWith(PairPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal),
    ];

    /// <summary>One fixture's text.</summary>
    public static string Get(string name) => File.ReadAllText(Path.Combine(CorpusPath(), name + ".json"));

    /// <summary>One fixture, read.</summary>
    public static QueryPlan Plan(string name) =>
        QueryPlanReader.Read(Get(name)).Plan ?? throw new InvalidOperationException("The fixture " + name + " is not a plan.");

    /// <summary>
    /// One plan of 0.10-B's pairs (<c>PlanCorpus/pair-*.json</c>): plans meant to be compared, named apart so the corpus tests that run
    /// every insight over every fixture do not meet them.
    /// </summary>
    public static QueryPlan Pair(string name) =>
        QueryPlanReader.Read(GetPair(name)).Plan
        ?? throw new InvalidOperationException("The pair fixture " + name + " is not a plan.");

    /// <summary>One of the pairs' text.</summary>
    public static string GetPair(string name) => File.ReadAllText(Path.Combine(CorpusPath(), PairPrefix + name + ".json"));

    private static string CorpusPath() => RepositoryRoot.Combine("tests", "PostgresStudio.Tests", "Statements", "PlanCorpus");
}
