using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Query;

/// <summary>
/// 0.10-B (issue #28): the indexes the Plan page offers a "What if?" in place of - X2's candidates for the plan
/// (<see cref="HypotheticalIndexCandidates.FromPlan" />: P04's sequential scan with a filter, P05's nested loop over a sequential
/// scan), and the index P10's finding asks for: the plain columns the filter of an index or bitmap scan compares, which the filter
/// threw rows away on. Every one is written by <see cref="MigrationSqlText.CreateIndex" /> from a table and its columns, so the
/// service can build it again from those parts and imagine nothing else. P17's remedy - an expression index, or the parameter's type
/// - is not an index that builder writes, and is not offered. Pure.
/// </summary>
internal static class PlanWhatIfCandidates
{
    /// <summary>The indexes <paramref name="plan" /> suggests, the first <see cref="HypotheticalIndexCandidates.MaxCandidates" />.</summary>
    /// <param name="plan">The plan on screen.</param>
    /// <param name="text">The studio's words, which P10's evaluator takes.</param>
    public static IReadOnlyList<HypotheticalIndex> For(QueryPlan plan, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(text);

        List<HypotheticalIndex> found = [.. HypotheticalIndexCandidates.FromPlan(plan)];
        HashSet<string> keys = new(found.Select(static x => x.Key), StringComparer.Ordinal);

        foreach (PlanInsightFinding finding in PlanInsights.DiscardingFilters(plan, text))
        {
            if (found.Count >= HypotheticalIndexCandidates.MaxCandidates)
            {
                break;
            }

            PlanNode? node = plan.Nodes.FirstOrDefault(x => x.Id == finding.NodeId);

            if (node is not { RelationName: { } table, Filter: { } filter })
            {
                continue;
            }

            IReadOnlyList<string> columns = HypotheticalIndexCandidates.Columns(filter, node.Alias ?? table);

            if (Candidate(node.Schema, table, columns) is { } candidate && keys.Add(candidate.Key))
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>
    /// The index on <paramref name="columns" /> of <paramref name="table" />, as <see cref="MigrationSqlText.CreateIndex" /> writes it
    /// - or <see langword="null" /> when there is no column or a name cannot be printed honestly.
    /// </summary>
    /// <param name="schema">The table's schema, when the plan printed it.</param>
    /// <param name="table">The table.</param>
    /// <param name="columns">The plain columns the plan's condition compares.</param>
    internal static HypotheticalIndex? Candidate(string? schema, string table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0 || MigrationSqlText.CreateIndex(schema, table, columns) is not { } definition)
        {
            return null;
        }

        return new HypotheticalIndex
        {
            Key = HypotheticalIndexCandidates.Key(schema, table, columns),
            Definition = definition,
            Caption = "An index on " + (schema is null ? table : schema + "." + table) + " (" + string.Join(", ", columns) + ")",
            Origin = HypotheticalIndexOrigin.Plan,
            Schema = schema,
            Table = table,
            Columns = columns,
            Note = DatabaseObjectClassifier.IsMartenName(table) ? HypotheticalIndexCandidates.MartenTableNote : null,
        };
    }
}

/// <summary>
/// 1.2-INDEX-A: the indexes one plan of the index advisor's workload suggests - X2's two shapes (P04's sequential scan with a
/// filter, P05's nested loop over a sequential scan) and P10's shape without <c>ANALYZE</c>: the plain columns the filter of an
/// index or bitmap scan compares, read off the node itself because a plain plan has no rows removed to count. Every index is
/// written by <see cref="MigrationSqlText.CreateIndex" />. Not P17's expression-index remedy, which no builder writes, and not P26's
/// JSON filter, which is MartenStudio's (<see cref="IIndexCandidateContributor" />). Pure.
/// </summary>
internal static class WorkloadIndexCandidates
{
    /// <summary>The indexes <paramref name="plan" /> suggests on tables it names the schema of, the first <see cref="HypotheticalIndexCandidates.MaxCandidates" />.</summary>
    /// <param name="plan">A plan drawn with <c>VERBOSE</c>, so each relation names its schema.</param>
    public static IReadOnlyList<HypotheticalIndex> FromPlan(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<HypotheticalIndex> found = [.. HypotheticalIndexCandidates.FromPlan(plan)];
        HashSet<string> keys = new(found.Select(static x => x.Key), StringComparer.Ordinal);

        foreach (PlanNode node in plan.Nodes)
        {
            if (found.Count >= HypotheticalIndexCandidates.MaxCandidates)
            {
                break;
            }

            if (node.NodeType is not ("Index Scan" or "Index Only Scan" or "Bitmap Heap Scan") || node.RelationName is not { } table || node.Filter is not { } filter)
            {
                continue;
            }

            if (PlanWhatIfCandidates.Candidate(node.Schema, table, HypotheticalIndexCandidates.Columns(filter, node.Alias ?? table)) is { } candidate && keys.Add(candidate.Key))
            {
                found.Add(candidate);
            }
        }

        // The advisor finds a table by the schema and name a plan prints: an index it cannot place is not tried.
        return [.. found.Where(static x => x.Schema is { Length: > 0 })];
    }

    /// <summary>
    /// The planner's cost of the costliest node of <paramref name="plan" /> that reads <paramref name="schema" />.<paramref name="table" />:
    /// what an index on that table could save at most, in the plan's own unit.
    /// </summary>
    public static double ScanCost(QueryPlan plan, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(plan);

        double cost = 0;

        foreach (PlanNode node in plan.Nodes)
        {
            if (Reads(node, schema, table) && node.TotalCost is { } total && total > cost)
            {
                cost = total;
            }
        }

        return cost;
    }

    /// <summary>Whether <paramref name="plan" /> has a node that reads <paramref name="schema" />.<paramref name="table" />.</summary>
    public static bool Reads(QueryPlan plan, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Nodes.Any(x => Reads(x, schema, table));
    }

    private static bool Reads(PlanNode node, string schema, string table) =>
        string.Equals(node.Schema, schema, StringComparison.Ordinal) && string.Equals(node.RelationName, table, StringComparison.Ordinal);
}
