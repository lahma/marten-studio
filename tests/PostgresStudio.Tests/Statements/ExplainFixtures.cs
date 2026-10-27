using System.Text.Json;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The PROBE packet's 108 <c>EXPLAIN (FORMAT JSON)</c> outputs (2026-10-26): nine shapes on PostgreSQL 14 to 18 and 19's first
/// release candidate, plain and under <c>ANALYZE, BUFFERS</c>, each run in <c>begin read only</c> against the same two tables -
/// kept in one file, <c>ExplainFixtures.json</c>, each value the fixture's text verbatim under its file's name
/// (<c>&lt;major&gt;-&lt;shape&gt;-&lt;plain|analyze&gt;</c>).
/// </summary>
/// <remarks>
/// The shapes are what the probe asked for, not always what the planner gave: on every major <c>bitmap-lossy</c> came out as a
/// sequential scan and <c>hashjoin</c> as a merge join, so no fixture holds a lossy bitmap, a hash that split, or a parallel
/// plan short of workers. The tests build those by hand.
/// </remarks>
internal static class ExplainFixtures
{
    /// <summary>Every major the probe explained on, oldest first.</summary>
    public static readonly string[] Majors = ["14", "15", "16", "17", "18", "19rc1"];

    /// <summary>Every shape the probe asked for.</summary>
    public static readonly string[] Shapes =
        ["bitmap-lossy", "cte", "hashjoin", "index", "nestloop", "offset", "parallel", "seqfilter", "sort-spill"];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Loaded = new(Load);

    /// <summary>Every fixture's text, by name.</summary>
    public static IReadOnlyDictionary<string, string> All => Loaded.Value;

    /// <summary>One fixture's text.</summary>
    public static string Get(string name) => All[name];

    /// <summary>One fixture's name.</summary>
    public static string Name(string major, string shape, bool analyze) => major + "-" + shape + "-" + (analyze ? "analyze" : "plain");

    /// <summary>Whether a fixture was explained under <c>ANALYZE</c>.</summary>
    public static bool IsAnalyze(string name) => name.EndsWith("-analyze", StringComparison.Ordinal);

    /// <summary>A fixture's major.</summary>
    public static string MajorOf(string name) => name[..name.IndexOf('-', StringComparison.Ordinal)];

    /// <summary>A fixture's shape.</summary>
    public static string ShapeOf(string name)
    {
        string rest = name[(name.IndexOf('-', StringComparison.Ordinal) + 1)..];
        return rest[..rest.LastIndexOf('-')];
    }

    /// <summary>Every fixture's name, for a theory.</summary>
    public static TheoryData<string> Names() => [.. All.Keys.Order(StringComparer.Ordinal)];

    private static Dictionary<string, string> Load()
    {
        string path = RepositoryRoot.Combine("tests", "PostgresStudio.Tests", "Statements", "ExplainFixtures.json");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.EnumerateObject().ToDictionary(static x => x.Name, static x => x.Value.GetRawText(), StringComparer.Ordinal);
    }
}
