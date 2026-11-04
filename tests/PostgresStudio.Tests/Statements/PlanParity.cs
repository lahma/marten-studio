using System.Globalization;

using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// Compares two reads of one plan, field by field, for what both carry: the text-format reader against the JSON reader (1.0-P3A).
/// Shared by the fast suite's corpus tests and the matrix parity test, which links this file.
/// </summary>
internal static class PlanParity
{
    /// <summary>
    /// How far two executions of one statement may differ in the blocks they touched (1.2-FLK). The two plans of a parity run come
    /// from two executions, and the blocks an execution touches (hint bits set by the first run, a page the other found pruned) move
    /// by a few units and by a share of a large count: PostgreSQL 19, sort-spill case, 837 blocks touched from text and 834 from JSON (publish run 41628950778). The larger of
    /// 8 and 2 % of the larger count is far under any difference a reader bug makes (a field dropped or read from the wrong line is off
    /// by the whole count), and it is applied only to <c>executed</c> plans: a plain <c>EXPLAIN</c> runs nothing and stays exact.
    /// Only blocks touched get it: actual rows and loops are what the statement determines, never varied between two runs on record,
    /// and this live test is the text reader's only parity check for them, so they stay exact. Times are never compared, and the
    /// shape, the estimates and the node set are exact in every mode.
    /// </summary>
    /// <param name="expected">The count from one execution.</param>
    /// <param name="actual">The count from the other.</param>
    /// <returns>The largest difference that still counts as the same.</returns>
    internal static double ExecutedTolerance(double? expected, double? actual)
        => Math.Max(8, 0.02 * Math.Max(Math.Abs(expected ?? 0), Math.Abs(actual ?? 0)));

    /// <summary>Everything that differs between <paramref name="json" /> and <paramref name="text" />, as sentences.</summary>
    /// <param name="json">The plan read from JSON.</param>
    /// <param name="text">The plan read from text.</param>
    /// <param name="actuals">Whether the statement's own measurements (rows, loops, rows removed...) are compared.</param>
    /// <param name="timed">Whether the plan was timed, so a time must be there.</param>
    /// <param name="label">What is compared, for each sentence.</param>
    /// <param name="executed">
    /// Whether the two plans come from two executions of the statement, so that the blocks they touched are compared within
    /// <see cref="ExecutedTolerance" /> instead of exactly (1.2-FLK).
    /// </param>
    public static IReadOnlyList<string> Compare(QueryPlan json, QueryPlan text, bool actuals, bool timed, string label, bool executed = false)
    {
        List<string> problems = [];

        void Same(string what, object? expected, object? actual)
        {
            if (!Equals(expected, actual) && problems.Count < 400)
            {
                problems.Add(label + ": " + what + " is " + Show(actual) + " from text, " + Show(expected) + " from JSON");
            }
        }

        void Near(string what, double? expected, double? actual, double tolerance)
        {
            bool same = expected is null && actual is null
                || expected is { } e && actual is { } a && Math.Abs(e - a) <= tolerance;

            if (!same && problems.Count < 400)
            {
                problems.Add(label + ": " + what + " is " + Show(actual) + " from text, " + Show(expected) + " from JSON");
            }
        }

        void Count(string what, double? expected, double? actual, double exactTolerance)
            => Near(what, expected, actual, executed ? Math.Max(exactTolerance, ExecutedTolerance(expected, actual)) : exactTolerance);

        // The cost or estimate of a timed or analyzed plan is the same plan's, so it is compared exactly in every mode.
        PlanNode[] left = [.. json.Nodes];
        PlanNode[] right = [.. text.Nodes];

        Same("the node count", left.Length, right.Length);
        Same("the statement count", json.Statements, text.Statements);
        Same("whether it is analyzed", json.Analyzed, text.Analyzed);
        Same("whether it is timed", json.Timed, text.Timed);

        if (left.Length == right.Length)
        {
            for (int i = 0; i < left.Length; i++)
            {
                PlanNode j = left[i];
                PlanNode t = right[i];
                string at = "node " + i + " (" + j.Description + ")";

                Same(at + " depth", j.Depth, t.Depth);
                Same(at + " type", j.NodeType, t.NodeType);
                Same(at + " label", j.Label, t.Label);
                Same(at + " parent relationship", j.ParentRelationship, t.ParentRelationship);
                Same(at + " subplan", j.SubplanName, t.SubplanName);
                Same(at + " schema", j.Schema, t.Schema);
                Same(at + " relation", j.RelationName, t.RelationName);
                Same(at + " alias", j.Alias, t.Alias);
                Same(at + " index", j.IndexName, t.IndexName);
                Same(at + " function", j.FunctionName, t.FunctionName);
                Same(at + " CTE", j.CteName, t.CteName);
                Same(at + " operation", j.Operation, t.Operation);
                Same(at + " strategy", j.Strategy, t.Strategy);
                Same(at + " join type", j.JoinType, t.JoinType);
                Same(at + " partial mode", j.PartialMode, t.PartialMode);
                Same(at + " parallel", j.ParallelAware, t.ParallelAware);
                Near(at + " startup cost", j.StartupCost, t.StartupCost, 0.0051);
                Near(at + " total cost", j.TotalCost, t.TotalCost, 0.0051);
                Near(at + " plan rows", j.PlanRows, t.PlanRows, 0.0051);
                Near(at + " plan width", j.PlanWidth, t.PlanWidth, 0.0051);
                Same(at + " filter", j.Filter, t.Filter);
                Same(at + " index condition", j.IndexCondition, t.IndexCondition);
                Same(at + " recheck condition", j.RecheckCondition, t.RecheckCondition);
                Same(at + " hash condition", j.HashCondition, t.HashCondition);
                Same(at + " merge condition", j.MergeCondition, t.MergeCondition);
                Same(at + " join filter", j.JoinFilter, t.JoinFilter);
                Same(at + " one-time filter", j.OneTimeFilter, t.OneTimeFilter);
                Same(at + " sort key", string.Join(" | ", j.SortKey), string.Join(" | ", t.SortKey));
                Same(at + " group key", string.Join(" | ", j.GroupKey), string.Join(" | ", t.GroupKey));
                Same(at + " workers planned", j.WorkersPlanned, t.WorkersPlanned);
                Same(at + " output", Property(j, "Output"), Property(t, "Output"));
                Same(at + " subplans removed", Property(j, "Subplans Removed") ?? "0", Property(t, "Subplans Removed") ?? "0");
                Same(at + " disabled", j.Disabled ?? false, t.Disabled ?? false);

                Same(at + " is analyzed", j.ActualLoops is not null, t.ActualLoops is not null);

                if (j.ActualLoops is not null && t.ActualLoops is not null)
                {
                    Same(at + " has a time", j.ActualTotalMs is not null, t.ActualTotalMs is not null);

                    if (timed)
                    {
                        Same(at + " has a total time", j.ActualTotalMs is not null, t.ActualTotalMs is not null);
                    }
                }

                // A text plan prints a Buffers line only for a node that touched a block: a plan that touched none prints none.
                if (json.Root.Buffers is { Any: true })
                {
                    Same(at + " has buffers", j.Buffers is not null, t.Buffers is not null);
                }

                if (!actuals)
                {
                    continue;
                }

                Near(at + " actual rows", j.ActualRows, t.ActualRows, 0.0051);
                Near(at + " actual loops", j.ActualLoops, t.ActualLoops, 0.0051);
                Near(at + " rows removed by filter", j.RowsRemovedByFilter ?? 0, t.RowsRemovedByFilter ?? 0, 0.0051);
                Near(at + " rows removed by recheck", j.RowsRemovedByIndexRecheck ?? 0, t.RowsRemovedByIndexRecheck ?? 0, 0.0051);
                Near(at + " rows removed by join filter", j.RowsRemovedByJoinFilter ?? 0, t.RowsRemovedByJoinFilter ?? 0, 0.0051);
                Same(at + " sort method", j.SortMethod, t.SortMethod);
                Same(at + " sort space type", j.SortSpaceType, t.SortSpaceType);
                Near(at + " sort space", j.SortSpaceUsedKb, t.SortSpaceUsedKb, 1.5);
                Near(at + " hash buckets", j.HashBuckets, t.HashBuckets, 0.0051);
                Near(at + " hash batches", j.HashBatches, t.HashBatches, 0.0051);
                Near(at + " hashagg batches", j.HashAggBatches, t.HashAggBatches, 0.0051);
                Near(at + " heap fetches", j.HeapFetches ?? 0, t.HeapFetches ?? 0, 0.0051);
                Near(at + " exact heap blocks", j.ExactHeapBlocks ?? 0, t.ExactHeapBlocks ?? 0, 0.0051);
                Near(at + " lossy heap blocks", j.LossyHeapBlocks ?? 0, t.LossyHeapBlocks ?? 0, 0.0051);
                Near(at + " cache hits", j.CacheHits, t.CacheHits, 0.0051);
                Near(at + " cache misses", j.CacheMisses, t.CacheMisses, 0.0051);
                Near(at + " workers launched", j.WorkersLaunched, t.WorkersLaunched, 0.0051);

                if (j.Buffers is { } jb && t.Buffers is { } tb)
                {
                    // Which blocks were hit and which were read differs between two runs, and so, by a few, does their total:
                    // two executions are compared within ExecutedTolerance.
                    Count(at + " shared blocks touched", jb.SharedHit + jb.SharedRead, tb.SharedHit + tb.SharedRead, 2);
                    Same(at + " temp written blocks", jb.TempWritten > 0, tb.TempWritten > 0);
                }
            }
        }

        Near("the planning memory", json.PlanningMemoryBytes, text.PlanningMemoryBytes, 1024);
        Same("whether it has a planning time", json.PlanningMs is not null, text.PlanningMs is not null);
        Same("whether it has an execution time", json.ExecutionMs is not null, text.ExecutionMs is not null);
        Same("the settings", string.Join(", ", json.Settings.Select(static x => x.Key + "=" + x.Value)), string.Join(", ", text.Settings.Select(static x => x.Key + "=" + x.Value)));
        Same("the trigger count", json.Triggers.Count, text.Triggers.Count);

        if (json.Triggers.Count == text.Triggers.Count)
        {
            for (int i = 0; i < json.Triggers.Count; i++)
            {
                Same("trigger " + i + " name", json.Triggers[i].Name, text.Triggers[i].Name);

                // The text names a trigger's table only when the statement has more than one result relation.
                if (text.Triggers[i].Relation is not null)
                {
                    Same("trigger " + i + " relation", json.Triggers[i].Relation, text.Triggers[i].Relation);
                }

                Same("trigger " + i + " calls", json.Triggers[i].Calls, text.Triggers[i].Calls);
            }
        }

        Same("whether it has JIT", json.JitFunctions is not null, text.JitFunctions is not null);
        Same("the JIT functions", json.JitFunctions, text.JitFunctions);
        return problems;
    }

    private static string? Property(PlanNode node, string key) => node.Properties.FirstOrDefault(x => x.Key == key)?.Value;

    private static string Show(object? value) => value switch
    {
        null => "none",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        _ => "“" + value + "”",
    };
}
