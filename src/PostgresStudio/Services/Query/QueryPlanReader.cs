using System.Globalization;
using System.Text.Json;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Services.Query;

/// <summary>
/// Reads the JSON <c>EXPLAIN (FORMAT JSON)</c> prints into a <see cref="QueryPlan" />: the one reader every plan the studio
/// draws goes through - the SQL console's, Query Mode A's and the Statements page's drill-in (X1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Defensive by construction.</b> An unknown property is ignored, a missing one is a blank, a property of the wrong JSON
/// type reads as absent, and text that is not a plan is a <see cref="QueryPlanRead.Failure" /> rather than an exception. A
/// number may come as a JSON number or as numeric text; neither is trusted to be an integer.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="MaxCharacters" /> characters are parsed, <see cref="MaxNodes" /> nodes read and
/// <see cref="MaxDepth" /> levels walked; past either bound the plan is drawn as far as it was read and says so
/// (<see cref="QueryPlan.Truncated" />). The JSON parser's own depth is raised well past that: every plan level is two JSON
/// levels (the node and its <c>Plans</c> array), the default of 64 would refuse a 32-way join, and a plan deeper than the walk
/// is drawn to the walk's bound rather than refused. The parser does not recurse, and the walk stops at its own bound.
/// </para>
/// <para>
/// <b>Masking is the caller's.</b> <c>mask</c> is applied to every name and condition the plan prints - never to Postgres'
/// own vocabulary (node types, strategies), which a withheld schema's name could otherwise mask by accident. The Statements
/// page's drill-in passes the visitor's mask; the console and Mode A pass none, because their plans are <c>RunSql</c>
/// output, which D27 leaves unmasked as it leaves the console's rows.
/// </para>
/// </remarks>
internal static class QueryPlanReader
{
    /// <summary>The most characters of plan JSON read: as many as the console keeps whole.</summary>
    public const int MaxCharacters = ExplainStatements.MaxPlanCharacters;

    /// <summary>The most nodes read.</summary>
    public const int MaxNodes = 2_000;

    /// <summary>The deepest a read walks.</summary>
    public const int MaxDepth = 200;

    /// <summary>What a read says about text that is not a plan.</summary>
    internal const string NotAPlan = "This is not the JSON EXPLAIN (FORMAT JSON) prints, so it cannot be drawn as a plan.";

    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = 4_096,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The plan <paramref name="json" /> holds, or why it holds none.</summary>
    /// <param name="json">What <c>EXPLAIN (FORMAT JSON)</c> printed.</param>
    /// <param name="mask">Applied to every name and condition read, or <see langword="null" /> for none.</param>
    public static QueryPlanRead Read(string? json, Func<string, string>? mask = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return QueryPlanRead.Failed("There is no plan.");
        }

        if (json.Length > MaxCharacters)
        {
            return QueryPlanRead.Failed(TooLarge(json.Length));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, Options);

            if (!TryStatements(document.RootElement, out List<JsonElement> statements))
            {
                return QueryPlanRead.Failed(NotAPlan);
            }

            JsonElement first = statements[0];
            Walk walk = new(mask);
            PlanNode root = walk.Node(first.GetProperty("Plan"), 0);

            ComputeCosts(root);
            ComputeTimes(root);

            return QueryPlanRead.Of(new QueryPlan(root, walk.Count, walk.Truncated, statements.Count)
            {
                PlanningMs = Number(first, "Planning Time"),
                ExecutionMs = Number(first, "Execution Time"),
                Triggers = Triggers(first, mask),
                JitFunctions = Jit(first, "Functions"),
                JitMs = JitTotal(first),
            });
        }
        catch (JsonException)
        {
            return QueryPlanRead.Failed(NotAPlan);
        }
    }

    /// <summary>What a plan too large to read says.</summary>
    internal static string TooLarge(long characters) =>
        "The plan is " + characters.ToString("N0", CultureInfo.InvariantCulture) + " characters of JSON, more than the "
        + MaxCharacters.ToString("N0", CultureInfo.InvariantCulture) + " the studio draws.";

    /// <summary>
    /// Every relation the plan reads, as printed - its schema (under <c>VERBOSE</c>) and its name - unmasked, for a caller that
    /// must decide whether a visitor may see the plan at all before it masks anything. Empty for text that is not a plan.
    /// </summary>
    public static IReadOnlyList<(string? Schema, string Relation)> Relations(string? json)
    {
        List<(string? Schema, string Relation)> relations = [];

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxCharacters)
        {
            return relations;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, Options);

            if (!TryStatements(document.RootElement, out List<JsonElement> statements))
            {
                return relations;
            }

            foreach (JsonElement statement in statements)
            {
                Collect(statement.GetProperty("Plan"), relations, 0);
            }
        }
        catch (JsonException)
        {
            relations.Clear();
        }

        return relations;

        static void Collect(JsonElement node, List<(string? Schema, string Relation)> found, int depth)
        {
            if (node.ValueKind != JsonValueKind.Object || depth > MaxDepth)
            {
                return;
            }

            if (Text(node, "Relation Name") is { } relation)
            {
                found.Add((Text(node, "Schema"), relation));
            }

            if (node.TryGetProperty("Plans", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    Collect(child, found, depth + 1);
                }
            }
        }
    }

    /// <summary>The statements of a plan document: an array of objects that each carry a <c>Plan</c> object.</summary>
    private static bool TryStatements(JsonElement root, out List<JsonElement> statements)
    {
        statements = [];

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Plan", out JsonElement single)
            && single.ValueKind == JsonValueKind.Object)
        {
            // One statement's object without its array: what a reader hands over that took the array apart.
            statements.Add(root);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement statement in root.EnumerateArray())
            {
                if (statement.ValueKind == JsonValueKind.Object
                    && statement.TryGetProperty("Plan", out JsonElement plan)
                    && plan.ValueKind == JsonValueKind.Object)
                {
                    statements.Add(statement);
                }
            }
        }

        return statements.Count > 0;
    }

    /// <summary>One walk: the next node's id, and whether a bound stopped it.</summary>
    private sealed class Walk(Func<string, string>? mask)
    {
        public int Count { get; private set; }

        public bool Truncated { get; private set; }

        public PlanNode Node(JsonElement node, int depth)
        {
            int id = Count++;
            string type = Text(node, "Node Type") ?? "(unknown)";

            List<PlanNode> children = [];

            if (node.TryGetProperty("Plans", out JsonElement plans) && plans.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in plans.EnumerateArray())
                {
                    if (child.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (Count >= MaxNodes || depth + 1 > MaxDepth)
                    {
                        Truncated = true;
                        break;
                    }

                    children.Add(Node(child, depth + 1));
                }
            }

            return new PlanNode
            {
                Id = id,
                Depth = depth,
                NodeType = type,
                Label = Label(node, type),
                ParentRelationship = Text(node, "Parent Relationship"),
                SubplanName = Masked(node, "Subplan Name"),
                Schema = Masked(node, "Schema"),
                RelationName = Masked(node, "Relation Name"),
                Alias = Masked(node, "Alias"),
                IndexName = Masked(node, "Index Name"),
                FunctionName = Masked(node, "Function Name"),
                CteName = Masked(node, "CTE Name"),
                Operation = Text(node, "Operation"),
                Strategy = Text(node, "Strategy"),
                JoinType = Text(node, "Join Type"),
                PartialMode = Text(node, "Partial Mode"),
                ParallelAware = Flag(node, "Parallel Aware") == true,
                StartupCost = Number(node, "Startup Cost"),
                TotalCost = Number(node, "Total Cost"),
                PlanRows = Number(node, "Plan Rows"),
                PlanWidth = Number(node, "Plan Width"),
                ActualStartupMs = Number(node, "Actual Startup Time"),
                ActualTotalMs = Number(node, "Actual Total Time"),
                ActualRows = Number(node, "Actual Rows"),
                ActualLoops = Number(node, "Actual Loops"),
                Filter = Masked(node, "Filter"),
                IndexCondition = Masked(node, "Index Cond"),
                RecheckCondition = Masked(node, "Recheck Cond"),
                HashCondition = Masked(node, "Hash Cond"),
                MergeCondition = Masked(node, "Merge Cond"),
                JoinFilter = Masked(node, "Join Filter"),
                OneTimeFilter = Masked(node, "One-Time Filter"),
                RowsRemovedByFilter = Number(node, "Rows Removed by Filter"),
                RowsRemovedByIndexRecheck = Number(node, "Rows Removed by Index Recheck"),
                RowsRemovedByJoinFilter = Number(node, "Rows Removed by Join Filter"),
                SortKey = Texts(node, "Sort Key"),
                GroupKey = Texts(node, "Group Key"),
                SortMethod = Text(node, "Sort Method"),
                SortSpaceType = Text(node, "Sort Space Type"),
                SortSpaceUsedKb = Number(node, "Sort Space Used"),
                WorkerSortDiskKb = WorkerSortDisk(node),
                HashBuckets = Number(node, "Hash Buckets"),
                HashBatches = Number(node, "Hash Batches"),
                OriginalHashBatches = Number(node, "Original Hash Batches"),
                PeakMemoryKb = Number(node, "Peak Memory Usage"),
                HashAggBatches = Number(node, "HashAgg Batches"),
                DiskUsageKb = Number(node, "Disk Usage"),
                ExactHeapBlocks = Number(node, "Exact Heap Blocks"),
                LossyHeapBlocks = Number(node, "Lossy Heap Blocks"),
                WorkersPlanned = Number(node, "Workers Planned"),
                WorkersLaunched = Number(node, "Workers Launched"),
                Disabled = Flag(node, "Disabled"),
                IndexSearches = Number(node, "Index Searches"),
                Storage = Text(node, "Storage"),
                MaximumStorageKb = Number(node, "Maximum Storage"),
                Buffers = Buffers(node),
                IoReadMs = Sum(node, "I/O Read Time", "Shared I/O Read Time", "Local I/O Read Time", "Temp I/O Read Time"),
                IoWriteMs = Sum(node, "I/O Write Time", "Shared I/O Write Time", "Local I/O Write Time", "Temp I/O Write Time"),
                Children = children,
            };
        }

        private string? Masked(JsonElement node, string property) =>
            Text(node, property) is { } text ? (mask is null ? text : mask(text)) : null;

        private List<string> Texts(JsonElement node, string property)
        {
            if (!node.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            List<string> texts = [];

            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                {
                    texts.Add(mask is null ? text : mask(text));
                }
            }

            return texts;
        }
    }

    /// <summary>
    /// The node's heading as <c>EXPLAIN</c>'s text format writes it (<c>explain.c</c>'s <c>ExplainNode</c>): a join's type, an
    /// aggregate's strategy and part, <c>Parallel</c>, <c>Async</c>.
    /// </summary>
    private static string Label(JsonElement node, string type)
    {
        string name = type switch
        {
            "Aggregate" => Text(node, "Strategy") switch
            {
                "Sorted" => "GroupAggregate",
                "Hashed" => "HashAggregate",
                "Mixed" => "MixedAggregate",
                _ => "Aggregate",
            },
            "SetOp" => Text(node, "Strategy") == "Hashed" ? "HashSetOp" : "SetOp",
            "ModifyTable" => Text(node, "Operation") ?? type,
            "Nested Loop" => Join("Nested Loop", Text(node, "Join Type"), loop: true),
            "Hash Join" => Join("Hash", Text(node, "Join Type"), loop: false),
            "Merge Join" => Join("Merge", Text(node, "Join Type"), loop: false),
            _ => type,
        };

        if (type == "Aggregate")
        {
            name = Text(node, "Partial Mode") switch
            {
                "Partial" => "Partial " + name,
                "Finalize" => "Finalize " + name,
                _ => name,
            };
        }

        if (Flag(node, "Parallel Aware") == true)
        {
            name = "Parallel " + name;
        }

        if (Flag(node, "Async Capable") == true)
        {
            name = "Async " + name;
        }

        return name;

        // "Nested Loop" and "Hash Join" for an inner join; "Nested Loop Left Join" and "Hash Left Join" for any other.
        static string Join(string head, string? joinType, bool loop) => joinType is null or "Inner"
            ? (loop ? head : head + " Join")
            : head + " " + joinType + " Join";
    }

    /// <summary>The node's buffers, or <see langword="null" /> when it printed none.</summary>
    private static PlanBuffers? Buffers(JsonElement node)
    {
        if (!node.TryGetProperty("Shared Hit Blocks", out _) && !node.TryGetProperty("Temp Written Blocks", out _)
            && !node.TryGetProperty("Local Hit Blocks", out _))
        {
            return null;
        }

        return new PlanBuffers(
            Long(node, "Shared Hit Blocks"),
            Long(node, "Shared Read Blocks"),
            Long(node, "Shared Dirtied Blocks"),
            Long(node, "Shared Written Blocks"),
            Long(node, "Local Hit Blocks"),
            Long(node, "Local Read Blocks"),
            Long(node, "Local Dirtied Blocks"),
            Long(node, "Local Written Blocks"),
            Long(node, "Temp Read Blocks"),
            Long(node, "Temp Written Blocks"));

        static long Long(JsonElement node, string property) =>
            Number(node, property) is { } value && value is > 0 and < long.MaxValue ? (long) value : 0;
    }

    /// <summary>The most disk any worker's sort used, in kB, when a worker's sort spilled.</summary>
    private static double? WorkerSortDisk(JsonElement node)
    {
        if (!node.TryGetProperty("Workers", out JsonElement workers) || workers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        double? most = null;

        foreach (JsonElement worker in workers.EnumerateArray())
        {
            if (worker.ValueKind == JsonValueKind.Object && Text(worker, "Sort Space Type") == "Disk")
            {
                double used = Number(worker, "Sort Space Used") ?? 0;
                most = most is { } seen ? Math.Max(seen, used) : used;
            }
        }

        return most;
    }

    private static List<PlanTrigger> Triggers(JsonElement statement, Func<string, string>? mask)
    {
        if (!statement.TryGetProperty("Triggers", out JsonElement triggers) || triggers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PlanTrigger> read = [];

        foreach (JsonElement trigger in triggers.EnumerateArray())
        {
            if (trigger.ValueKind == JsonValueKind.Object && Text(trigger, "Trigger Name") is { } name)
            {
                string? relation = Text(trigger, "Relation");
                read.Add(new PlanTrigger(
                    mask is null ? name : mask(name),
                    relation is null || mask is null ? relation : mask(relation),
                    Number(trigger, "Time"),
                    Number(trigger, "Calls")));
            }
        }

        return read;
    }

    private static double? Jit(JsonElement statement, string property) =>
        statement.TryGetProperty("JIT", out JsonElement jit) && jit.ValueKind == JsonValueKind.Object ? Number(jit, property) : null;

    private static double? JitTotal(JsonElement statement) =>
        statement.TryGetProperty("JIT", out JsonElement jit) && jit.ValueKind == JsonValueKind.Object
            && jit.TryGetProperty("Timing", out JsonElement timing) && timing.ValueKind == JsonValueKind.Object
            ? Number(timing, "Total")
            : null;

    /// <summary>Each node's own cost and its share of the plan's: its total less its children's, never below zero.</summary>
    private static void ComputeCosts(PlanNode root)
    {
        double sum = 0;

        foreach (PlanNode node in root.DescendantsAndSelf())
        {
            if (node.TotalCost is not { } total)
            {
                continue;
            }

            double children = node.Children.Sum(static x => x.TotalCost ?? 0);
            node.ExclusiveCost = Math.Max(0, total - children);
            sum += node.ExclusiveCost.Value;
        }

        if (sum <= 0)
        {
            return;
        }

        foreach (PlanNode node in root.DescendantsAndSelf())
        {
            node.CostShare = node.ExclusiveCost is { } own ? own / sum : null;
        }
    }

    /// <summary>
    /// Each node's wall time, own time and share, under <c>ANALYZE</c> with timing: a per-loop average times its loops, divided
    /// by the processes that shared it when it ran below a <c>Gather</c> (its workers and the leader).
    /// </summary>
    private static void ComputeTimes(PlanNode root)
    {
        if (root.ActualTotalMs is null || root.ActualLoops is null)
        {
            return;
        }

        Inclusive(root, 1);

        double sum = 0;

        foreach (PlanNode node in root.DescendantsAndSelf())
        {
            if (node.InclusiveMs is not { } inclusive)
            {
                continue;
            }

            double children = node.Children.Sum(static x => x.InclusiveMs ?? 0);
            node.ExclusiveMs = Math.Max(0, inclusive - children);
            sum += node.ExclusiveMs.Value;
        }

        if (sum <= 0)
        {
            return;
        }

        foreach (PlanNode node in root.DescendantsAndSelf())
        {
            node.TimeShare = node.ExclusiveMs is { } own ? own / sum : null;
        }

        static void Inclusive(PlanNode node, double processes)
        {
            node.InclusiveMs = node.ActualTotalMs is { } total && node.ActualLoops is { } loops
                ? total * loops / Math.Max(processes, 1)
                : null;

            double below = node.NodeType is "Gather" or "Gather Merge"
                ? (node.WorkersLaunched ?? node.WorkersPlanned ?? 0) + 1
                : processes;

            foreach (PlanNode child in node.Children)
            {
                Inclusive(child, below);
            }
        }
    }

    internal static string? Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static double? Number(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed) => parsed,
            _ => null,
        };
    }

    private static bool? Flag(JsonElement node, string property) =>
        node.TryGetProperty(property, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>The sum of whichever of <paramref name="properties" /> the node prints, or <see langword="null" /> for none.</summary>
    private static double? Sum(JsonElement node, params string[] properties)
    {
        double? sum = null;

        foreach (string property in properties)
        {
            if (Number(node, property) is { } value)
            {
                sum = (sum ?? 0) + value;
            }
        }

        return sum;
    }
}
