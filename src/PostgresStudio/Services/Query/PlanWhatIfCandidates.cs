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

            if (columns.Count == 0 || MigrationSqlText.CreateIndex(node.Schema, table, columns) is not { } definition)
            {
                continue;
            }

            string key = HypotheticalIndexCandidates.Key(node.Schema, table, columns);

            if (!keys.Add(key))
            {
                continue;
            }

            found.Add(new HypotheticalIndex
            {
                Key = key,
                Definition = definition,
                Caption = "An index on " + (node.Schema is null ? table : node.Schema + "." + table) + " (" + string.Join(", ", columns) + ")",
                Origin = HypotheticalIndexOrigin.Plan,
                Schema = node.Schema,
                Table = table,
                Columns = columns,
                Note = DatabaseObjectClassifier.IsMartenName(table) ? HypotheticalIndexCandidates.MartenTableNote : null,
            });
        }

        return found;
    }
}
