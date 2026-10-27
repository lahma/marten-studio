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
    /// <summary>The names of every fixture, without the extension.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        .. Directory.GetFiles(CorpusPath(), "*.json").Select(static x => Path.GetFileNameWithoutExtension(x)).Order(StringComparer.Ordinal),
    ];

    /// <summary>One fixture's text.</summary>
    public static string Get(string name) => File.ReadAllText(Path.Combine(CorpusPath(), name + ".json"));

    /// <summary>One fixture, read.</summary>
    public static QueryPlan Plan(string name) =>
        QueryPlanReader.Read(Get(name)).Plan ?? throw new InvalidOperationException("The fixture " + name + " is not a plan.");

    private static string CorpusPath() => RepositoryRoot.Combine("tests", "PostgresStudio.Tests", "Statements", "PlanCorpus");
}
