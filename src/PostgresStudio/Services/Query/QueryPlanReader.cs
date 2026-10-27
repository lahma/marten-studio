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
            PlanNode root = walk.Node(first.GetProperty(PlanKeys.Plan), 0);

            ComputeCosts(root);
            ComputeTimes(root);

            return QueryPlanRead.Of(new QueryPlan(root, walk.Count, walk.Truncated, statements.Count)
            {
                PlanningMs = Number(first, PlanKeys.PlanningTime),
                ExecutionMs = Number(first, PlanKeys.ExecutionTime),
                Triggers = Triggers(first, mask),
                JitFunctions = Jit(first, PlanKeys.Functions),
                JitMs = JitTotal(first),

                // What the statement printed around the plan, kept as text only for an unmasked read: a setting's value can
                // be a schema's name, which a masked plan must never carry.
                Settings = mask is null ? StatementObject(first, PlanKeys.Settings) : [],
                JitProperties = mask is null ? StatementObject(first, PlanKeys.Jit) : [],
                PlanningMemoryBytes = Nested(first, PlanKeys.Planning, PlanKeys.MemoryUsed),
                SerializationMs = Nested(first, PlanKeys.Serialization, PlanKeys.Time),
                SerializationKb = Nested(first, PlanKeys.Serialization, PlanKeys.OutputVolume),
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
                Collect(statement.GetProperty(PlanKeys.Plan), relations, 0);
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

            if (Text(node, PlanKeys.RelationName) is { } relation)
            {
                found.Add((Text(node, PlanKeys.Schema), relation));
            }

            if (node.TryGetProperty(PlanKeys.Plans, out JsonElement children) && children.ValueKind == JsonValueKind.Array)
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
            && root.TryGetProperty(PlanKeys.Plan, out JsonElement single)
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
                    && statement.TryGetProperty(PlanKeys.Plan, out JsonElement plan)
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
            string type = Text(node, PlanKeys.NodeType) ?? "(unknown)";

            List<PlanNode> children = [];

            if (node.TryGetProperty(PlanKeys.Plans, out JsonElement plans) && plans.ValueKind == JsonValueKind.Array)
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
                ParentRelationship = Text(node, PlanKeys.ParentRelationship),
                SubplanName = Masked(node, PlanKeys.SubplanName),
                Schema = Masked(node, PlanKeys.Schema),
                RelationName = Masked(node, PlanKeys.RelationName),
                Alias = Masked(node, PlanKeys.Alias),
                IndexName = Masked(node, PlanKeys.IndexName),
                FunctionName = Masked(node, PlanKeys.FunctionName),
                CteName = Masked(node, PlanKeys.CteName),
                Operation = Text(node, PlanKeys.Operation),
                Strategy = Text(node, PlanKeys.Strategy),
                JoinType = Text(node, PlanKeys.JoinType),
                PartialMode = Text(node, PlanKeys.PartialMode),
                ParallelAware = Flag(node, PlanKeys.ParallelAware) == true,
                StartupCost = Number(node, PlanKeys.StartupCost),
                TotalCost = Number(node, PlanKeys.TotalCost),
                PlanRows = Number(node, PlanKeys.PlanRows),
                PlanWidth = Number(node, PlanKeys.PlanWidth),
                ActualStartupMs = Number(node, PlanKeys.ActualStartupTime),
                ActualTotalMs = Number(node, PlanKeys.ActualTotalTime),
                ActualRows = Number(node, PlanKeys.ActualRows),
                ActualLoops = Number(node, PlanKeys.ActualLoops),
                Filter = Masked(node, PlanKeys.Filter),
                IndexCondition = Masked(node, PlanKeys.IndexCond),
                RecheckCondition = Masked(node, PlanKeys.RecheckCond),
                HashCondition = Masked(node, PlanKeys.HashCond),
                MergeCondition = Masked(node, PlanKeys.MergeCond),
                JoinFilter = Masked(node, PlanKeys.JoinFilter),
                OneTimeFilter = Masked(node, PlanKeys.OneTimeFilter),
                RowsRemovedByFilter = Number(node, PlanKeys.RowsRemovedByFilter),
                RowsRemovedByIndexRecheck = Number(node, PlanKeys.RowsRemovedByIndexRecheck),
                RowsRemovedByJoinFilter = Number(node, PlanKeys.RowsRemovedByJoinFilter),
                SortKey = Texts(node, PlanKeys.SortKey),
                GroupKey = Texts(node, PlanKeys.GroupKey),
                SortMethod = Text(node, PlanKeys.SortMethod),
                SortSpaceType = Text(node, PlanKeys.SortSpaceType),
                SortSpaceUsedKb = Number(node, PlanKeys.SortSpaceUsed),
                WorkerSortDiskKb = WorkerSortDisk(node),
                HashBuckets = Number(node, PlanKeys.HashBuckets),
                HashBatches = Number(node, PlanKeys.HashBatches),
                OriginalHashBatches = Number(node, PlanKeys.OriginalHashBatches),
                PeakMemoryKb = Number(node, PlanKeys.PeakMemoryUsage),
                HashAggBatches = Number(node, PlanKeys.HashAggBatches),
                DiskUsageKb = Number(node, PlanKeys.DiskUsage),
                HeapFetches = Number(node, PlanKeys.HeapFetches),
                CacheHits = Number(node, PlanKeys.CacheHits),
                CacheMisses = Number(node, PlanKeys.CacheMisses),
                CacheEvictions = Number(node, PlanKeys.CacheEvictions),
                CacheOverflows = Number(node, PlanKeys.CacheOverflows),
                WalRecords = Number(node, PlanKeys.WalRecords),
                WalFpi = Number(node, PlanKeys.WalFpi),
                WalBytes = Number(node, PlanKeys.WalBytes),
                ExactHeapBlocks = Number(node, PlanKeys.ExactHeapBlocks),
                LossyHeapBlocks = Number(node, PlanKeys.LossyHeapBlocks),
                WorkersPlanned = Number(node, PlanKeys.WorkersPlanned),
                WorkersLaunched = Number(node, PlanKeys.WorkersLaunched),
                Disabled = Flag(node, PlanKeys.Disabled),
                IndexSearches = Number(node, PlanKeys.IndexSearches),
                Storage = Text(node, PlanKeys.Storage),
                MaximumStorageKb = Number(node, PlanKeys.MaximumStorage),
                Buffers = Buffers(node),
                IoReadMs = Sum(node, PlanKeys.IoReadTime, PlanKeys.SharedIoReadTime, PlanKeys.LocalIoReadTime, PlanKeys.TempIoReadTime),
                IoWriteMs = Sum(node, PlanKeys.IoWriteTime, PlanKeys.SharedIoWriteTime, PlanKeys.LocalIoWriteTime, PlanKeys.TempIoWriteTime),

                // Every key the node printed, as text, for the detail panel: an unmasked read only, because a key the reader
                // does not model - Output, Cache Key, a function call - can name what a mask is there to hide.
                Properties = mask is null ? PropertiesOf(node) : [],
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
            "Aggregate" => Text(node, PlanKeys.Strategy) switch
            {
                "Sorted" => "GroupAggregate",
                "Hashed" => "HashAggregate",
                "Mixed" => "MixedAggregate",
                _ => "Aggregate",
            },
            "SetOp" => Text(node, PlanKeys.Strategy) == "Hashed" ? "HashSetOp" : "SetOp",
            "ModifyTable" => Text(node, PlanKeys.Operation) ?? type,
            "Nested Loop" => Join("Nested Loop", Text(node, PlanKeys.JoinType), loop: true),
            "Hash Join" => Join("Hash", Text(node, PlanKeys.JoinType), loop: false),
            "Merge Join" => Join("Merge", Text(node, PlanKeys.JoinType), loop: false),
            _ => type,
        };

        if (type == "Aggregate")
        {
            name = Text(node, PlanKeys.PartialMode) switch
            {
                "Partial" => "Partial " + name,
                "Finalize" => "Finalize " + name,
                _ => name,
            };
        }

        if (Flag(node, PlanKeys.ParallelAware) == true)
        {
            name = "Parallel " + name;
        }

        if (Flag(node, PlanKeys.AsyncCapable) == true)
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
        if (!node.TryGetProperty(PlanKeys.SharedHitBlocks, out _) && !node.TryGetProperty(PlanKeys.TempWrittenBlocks, out _)
            && !node.TryGetProperty(PlanKeys.LocalHitBlocks, out _))
        {
            return null;
        }

        return new PlanBuffers(
            Long(node, PlanKeys.SharedHitBlocks),
            Long(node, PlanKeys.SharedReadBlocks),
            Long(node, PlanKeys.SharedDirtiedBlocks),
            Long(node, PlanKeys.SharedWrittenBlocks),
            Long(node, PlanKeys.LocalHitBlocks),
            Long(node, PlanKeys.LocalReadBlocks),
            Long(node, PlanKeys.LocalDirtiedBlocks),
            Long(node, PlanKeys.LocalWrittenBlocks),
            Long(node, PlanKeys.TempReadBlocks),
            Long(node, PlanKeys.TempWrittenBlocks));

        static long Long(JsonElement node, string property) =>
            Number(node, property) is { } value && value is > 0 and < long.MaxValue ? (long) value : 0;
    }

    /// <summary>The most disk any worker's sort used, in kB, when a worker's sort spilled.</summary>
    private static double? WorkerSortDisk(JsonElement node)
    {
        if (!node.TryGetProperty(PlanKeys.Workers, out JsonElement workers) || workers.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        double? most = null;

        foreach (JsonElement worker in workers.EnumerateArray())
        {
            if (worker.ValueKind == JsonValueKind.Object && Text(worker, PlanKeys.SortSpaceType) == "Disk")
            {
                double used = Number(worker, PlanKeys.SortSpaceUsed) ?? 0;
                most = most is { } seen ? Math.Max(seen, used) : used;
            }
        }

        return most;
    }

    private static List<PlanTrigger> Triggers(JsonElement statement, Func<string, string>? mask)
    {
        if (!statement.TryGetProperty(PlanKeys.Triggers, out JsonElement triggers) || triggers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<PlanTrigger> read = [];

        foreach (JsonElement trigger in triggers.EnumerateArray())
        {
            if (trigger.ValueKind == JsonValueKind.Object && Text(trigger, PlanKeys.TriggerName) is { } name)
            {
                string? relation = Text(trigger, PlanKeys.Relation);
                read.Add(new PlanTrigger(
                    mask is null ? name : mask(name),
                    relation is null || mask is null ? relation : mask(relation),
                    Number(trigger, PlanKeys.Time),
                    Number(trigger, PlanKeys.Calls)));
            }
        }

        return read;
    }

    private static double? Jit(JsonElement statement, string property) =>
        statement.TryGetProperty(PlanKeys.Jit, out JsonElement jit) && jit.ValueKind == JsonValueKind.Object ? Number(jit, property) : null;

    private static double? JitTotal(JsonElement statement) =>
        statement.TryGetProperty(PlanKeys.Jit, out JsonElement jit) && jit.ValueKind == JsonValueKind.Object
            && jit.TryGetProperty(PlanKeys.Timing, out JsonElement timing) && timing.ValueKind == JsonValueKind.Object
            ? Number(timing, PlanKeys.Total)
            : null;

    /// <summary>A number inside an object of the statement - <c>Planning</c>'s memory, <c>Serialization</c>'s time - or <see langword="null" />.</summary>
    private static double? Nested(JsonElement statement, string objectName, string property) =>
        statement.TryGetProperty(objectName, out JsonElement inner) && inner.ValueKind == JsonValueKind.Object ? Number(inner, property) : null;

    /// <summary>The most properties kept for one node's detail, and the longest value each keeps.</summary>
    internal const int MaxProperties = 200;

    /// <summary>The longest a property's value is kept, in characters.</summary>
    internal const int MaxPropertyCharacters = 2_000;

    /// <summary>One statement-level object (<c>Settings</c>, <c>JIT</c>) as flattened properties, or none.</summary>
    private static List<PlanProperty> StatementObject(JsonElement statement, string name)
    {
        List<PlanProperty> properties = [];

        if (statement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            Flatten(value, null, 0, properties);
        }

        return properties;
    }

    /// <summary>
    /// Every scalar key <paramref name="node" /> printed, as text, in the order Postgres printed them: <c>Plans</c> is the
    /// children, read by the walk, and every other value is kept - nested objects and arrays flattened to
    /// <c>Parent › Child</c> and <c>Parent[0] › Child</c>, bounded at <see cref="MaxProperties" /> keys.
    /// </summary>
    private static List<PlanProperty> PropertiesOf(JsonElement node)
    {
        List<PlanProperty> properties = [];
        Flatten(node, null, 0, properties);
        return properties;
    }

    private static void Flatten(JsonElement element, string? prefix, int depth, List<PlanProperty> into)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (into.Count >= MaxProperties)
            {
                return;
            }

            if (depth == 0 && property.Name == PlanKeys.Plans)
            {
                continue;
            }

            string key = prefix is null ? property.Name : prefix + " › " + property.Name;
            Add(property.Value, key, depth, into);
        }
    }

    private static void Add(JsonElement value, string key, int depth, List<PlanProperty> into)
    {
        if (into.Count >= MaxProperties)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                into.Add(new PlanProperty(key, Clip(value.GetString() ?? string.Empty)));
                break;

            case JsonValueKind.Number:
                into.Add(new PlanProperty(key, value.GetRawText()));
                break;

            case JsonValueKind.True:
                into.Add(new PlanProperty(key, "true"));
                break;

            case JsonValueKind.False:
                into.Add(new PlanProperty(key, "false"));
                break;

            case JsonValueKind.Object when depth < 3:
                Flatten(value, key, depth + 1, into);
                break;

            case JsonValueKind.Array:
                if (value.EnumerateArray().All(static x => x.ValueKind is JsonValueKind.String or JsonValueKind.Number))
                {
                    // A list of scalars - a sort key, a group key, the methods an incremental sort used - is one value.
                    into.Add(new PlanProperty(key, Clip(string.Join(", ", value.EnumerateArray().Take(50).Select(static x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())))));
                }
                else if (depth < 3)
                {
                    int index = 0;

                    foreach (JsonElement item in value.EnumerateArray().Take(16))
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            Flatten(item, key + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", depth + 1, into);
                        }

                        index++;
                    }
                }

                break;
        }
    }

    private static string Clip(string text) =>
        text.Length <= MaxPropertyCharacters ? text : text[..MaxPropertyCharacters] + "…";

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
