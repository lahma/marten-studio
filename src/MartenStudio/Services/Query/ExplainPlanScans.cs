using System.Text.Json;

namespace MartenStudio.Services.Query;

/// <summary>One scan in a plan, as <c>EXPLAIN (FORMAT JSON)</c> reports it.</summary>
/// <param name="NodeType">The plan node: <c>Seq Scan</c>, <c>Index Scan</c>, <c>Bitmap Heap Scan</c>, ...</param>
/// <param name="Relation">The table it reads, when the node names one - unqualified, as a plan without <c>VERBOSE</c> prints it.</param>
/// <param name="Index">The index it uses, when it uses one.</param>
/// <param name="Filter">
/// The <c>Filter</c> it applies to every row it reads, as Postgres deparses it - <c>((data -&gt;&gt; 'Color'::text) = $1)</c>
/// - or <see langword="null" />.
/// </param>
/// <param name="IndexCondition">The <c>Index Cond</c> or <c>Recheck Cond</c> an index answers, or <see langword="null" />.</param>
internal sealed record ExplainPlanScan(string NodeType, string? Relation, string? Index, string? Filter, string? IndexCondition);

/// <summary>
/// The scans in an <c>EXPLAIN (FORMAT JSON)</c> plan, outermost first - the one reader of a plan's JSON, shared by
/// the Query page's LINQ advice and the T02 health check.
/// </summary>
/// <remarks>
/// Extracted from F7's <c>LinqIndexAdvisor.ReadScans</c> unchanged in what it walks and how far, so the LINQ panel draws
/// what it drew before; it now also keeps each scan's filter and index condition, which T02 maps back to members.
/// </remarks>
internal static class ExplainPlanScans
{
    /// <summary>The most scans read out of one plan.</summary>
    public const int MaxScans = 20;

    /// <summary>The deepest a walk goes into nested plans.</summary>
    private const int MaxDepth = 64;

    /// <summary>The scans in <paramref name="planJson" />; none when it is not the JSON Postgres writes.</summary>
    public static IReadOnlyList<ExplainPlanScan> Read(string planJson)
    {
        ArgumentNullException.ThrowIfNull(planJson);

        List<ExplainPlanScan> scans = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(planJson);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement statement in root.EnumerateArray())
                {
                    if (statement.ValueKind == JsonValueKind.Object && statement.TryGetProperty("Plan", out JsonElement plan))
                    {
                        Walk(plan, scans, 0);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A plan that is not the JSON Postgres writes says nothing that can be drawn.
        }

        return scans;
    }

    private static void Walk(JsonElement node, List<ExplainPlanScan> scans, int depth)
    {
        if (scans.Count >= MaxScans || depth > MaxDepth || node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? type = Text(node, "Node Type");
        string? relation = Text(node, "Relation Name");
        string? index = Text(node, "Index Name");

        if (type is not null && (relation is not null || index is not null))
        {
            scans.Add(new ExplainPlanScan(type, relation, index, Text(node, "Filter"), Text(node, "Index Cond") ?? Text(node, "Recheck Cond")));
        }

        if (node.TryGetProperty("Plans", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                Walk(child, scans, depth + 1);
            }
        }
    }

    private static string? Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
