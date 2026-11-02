using System.Globalization;
using System.Text.RegularExpressions;

namespace PostgresStudio.Services.Query;

/// <summary>
/// Reads the default <b>text</b> format of <c>EXPLAIN</c> - what <c>psql</c> prints and what people paste - into the same
/// <see cref="QueryPlan" /> <see cref="QueryPlanReader" /> reads from JSON, so every view, insight, export and comparison works on it
/// unchanged (1.0-P3A, issue #28).
/// </summary>
/// <remarks>
/// <para>
/// <b>The format, as <c>explain.c</c> writes it.</b> A node is one heading line - <c>Hash Left Join  (cost=a..b rows=r width=w) (actual
/// time=a..b rows=r loops=l)</c> - then the lines of its keys, indented two columns past the heading's text, then its children, each
/// opened by <c>-&gt;</c> at the column its parent's keys start at. A subplan's children come after a header line (<c>InitPlan 1
/// (returns $0)</c>, <c>SubPlan 2</c>, <c>CTE x</c>) at the same column, whose child's arrow is two further in. The statement's own
/// lines - <c>Planning Time</c>, <c>Execution Time</c>, <c>JIT:</c> and its indented block, <c>Settings:</c>, <c>Planning:</c>,
/// <c>Serialization:</c>, a trigger's time - follow at column zero. A plan pasted with a common indent (<c>psql</c>'s one leading space,
/// a log's prefix), under <c>QUERY PLAN</c> and its rule, or after <c>auto_explain</c>'s <c>Query Text:</c> is read all the same: the
/// reader starts at the first line that is a node and takes that line's indent as column zero.
/// </para>
/// <para>
/// <b>Same model, same words.</b> A heading is taken apart into the fields JSON names - <c>Parallel Hash Left Join</c> is a <c>Hash
/// Join</c>, parallel aware, <c>Join Type</c> <c>Left</c>; <c>HashAggregate</c> is an <c>Aggregate</c> with <c>Strategy</c> <c>Hashed</c> -
/// and the heading as printed stays its <see cref="PlanNode.Label" />. Names are unquoted and unqualified the way JSON gives them
/// (<c>on public."My Table" t</c> is schema <c>public</c>, relation <c>My Table</c>, alias <c>t</c>). What the text does not say, the
/// reader infers only where the rule is fixed: how a child feeds its parent (<c>Outer</c> and <c>Inner</c> under a join, <c>Member</c>
/// under an append, <c>Subquery</c> under a subquery scan, <c>InitPlan</c> and <c>SubPlan</c> from the header) and a join's
/// <c>Inner</c> type. A text plan prints no <c>Buffers</c> line for a node whose counters are all zero, so when any node printed one, a
/// node without one is read as zeros, as JSON prints them. A text plan carries a schema only under <c>VERBOSE</c>.
/// </para>
/// <para>
/// <b>Never throws, and keeps what it cannot read.</b> A line in a node that is not <c>Key: value</c>, or that sits where no node is, is
/// kept verbatim in <see cref="PlanNode.UnreadLines" /> (and one around the plan in <see cref="QueryPlan.UnreadLines" />); a key it
/// knows nothing about is kept as a property with its text. Text with no node in it is a <see cref="QueryPlanRead.Failure" />. The scans
/// are linear (<see cref="RegexOptions.NonBacktracking" />), the text is bounded as the JSON is - <see cref="QueryPlanReader.MaxCharacters" />
/// characters, <see cref="QueryPlanReader.MaxNodes" /> nodes and <see cref="QueryPlanReader.MaxDepth" /> levels, past which the plan is
/// drawn as far as it was read and says so (<see cref="QueryPlan.Truncated" />) - and any failure of the reader itself is a refusal.
/// </para>
/// <para>
/// <b>Masking is the caller's,</b> as for JSON: <c>mask</c> is applied to every name and condition, and a masked read keeps neither the
/// properties nor the unread lines, which can name what the mask hides.
/// </para>
/// </remarks>
internal static class QueryPlanTextReader
{
    /// <summary>What a read says about text that holds no plan.</summary>
    internal const string NotAPlan = "This is not the text EXPLAIN prints in its default format: no plan node was found in it.";

    private const int MaxUnreadPerNode = 50;
    private const int MaxUnreadAround = 200;

    private const RegexOptions Linear = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly Regex CostGroup = new(
        @"\(cost=(?<s>[0-9]+(?:\.[0-9]+)?)\.\.(?<t>[0-9]+(?:\.[0-9]+)?) rows=(?<r>[0-9]+) width=(?<w>[0-9]+)\)", Linear);

    private static readonly Regex ActualGroup = new(
        @"\(actual (?:time=(?<s>[0-9]+(?:\.[0-9]+)?)\.\.(?<t>[0-9]+(?:\.[0-9]+)?) )?rows=(?<r>[0-9]+(?:\.[0-9]+)?) loops=(?<l>[0-9]+)\)", Linear);

    private static readonly Regex NumberText = new(@"-?[0-9]+(?:\.[0-9]+)?", Linear);
    private static readonly Regex Pair = new(@"(?<k>[A-Za-z]+)=(?<v>-?[0-9]+(?:\.[0-9]+)?)", Linear);
    private static readonly Regex Timed = new(@"(?<k>[A-Za-z]+) (?<v>[0-9]+(?:\.[0-9]+)?) ms", Linear);
    private static readonly Regex Setting = new(@"(?<k>[A-Za-z_][A-Za-z0-9_.]*) = '(?<v>(?:[^']|'')*)'", Linear);
    private static readonly Regex Originally = new(@"\(originally (?<o>[0-9]+)\)", Linear);
    private static readonly Regex ReadWrite = new(@"(?<k>read|write)=(?<v>[0-9]+(?:\.[0-9]+)?)", Linear);

    /// <summary>The first words of every node heading Postgres prints, for a plan printed with neither costs nor timing.</summary>
    private static readonly HashSet<string> HeadingWords = new(StringComparer.Ordinal)
    {
        "Result", "ProjectSet", "Insert", "Update", "Delete", "Merge", "Append", "Recursive", "BitmapAnd", "BitmapOr", "Nested",
        "Hash", "Seq", "Sample", "Gather", "Index", "Bitmap", "Tid", "Subquery", "Function", "Table", "Values", "CTE", "Named",
        "WorkTable", "Foreign", "Custom", "Materialize", "Memoize", "Sort", "Incremental", "Group", "Aggregate", "GroupAggregate",
        "HashAggregate", "MixedAggregate", "WindowAgg", "Unique", "SetOp", "HashSetOp", "LockRows", "Limit",
    };

    /// <summary>The keys whose line holds several <c>Key: value</c> pairs, which Postgres separates with two spaces.</summary>
    private static readonly HashSet<string> StatKeys = new(StringComparer.Ordinal)
    {
        "Sort Method", "Sort Methods", "Buckets", "Batches", "Hits", "Storage", "HashAgg Batches", "Peak Memory Usage",
        "Full-sort Groups", "Pre-sorted Groups", "Planned Partitions",
    };

    /// <summary>The plan <paramref name="text" /> holds, or why it holds none.</summary>
    /// <param name="text">What <c>EXPLAIN</c> printed, in its default format.</param>
    /// <param name="mask">Applied to every name and condition read, or <see langword="null" /> for none.</param>
    public static QueryPlanRead Read(string? text, Func<string, string>? mask = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return QueryPlanRead.Failed("There is no plan.");
        }

        if (text.Length > QueryPlanReader.MaxCharacters)
        {
            return QueryPlanRead.Failed(QueryPlanReader.TooLarge(text.Length));
        }

        try
        {
            return new Parser(mask).Parse(text);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The reader is bounded and linear, so this is a defect of its own and never the paste's fault; a paste that reaches it is
            // still refused rather than answered with an exception (the fuzz test asserts none does).
            return QueryPlanRead.Failed(NotAPlan);
        }
    }

    // ---- One node while it is being read -------------------------------------------------------------------------------

    private sealed class Node
    {
        public required int Depth { get; init; }

        public required int DetailColumn { get; init; }

        public bool Dropped { get; init; }

        public string Label { get; set; } = string.Empty;

        public string NodeType { get; set; } = "(unknown)";

        public string? Strategy { get; set; }

        public string? PartialMode { get; set; }

        public string? Operation { get; set; }

        public string? JoinType { get; set; }

        public bool Parallel { get; set; }

        public string? PendingRelationship { get; set; }

        public string? SubplanName { get; set; }

        public string? Schema { get; set; }

        public string? RelationName { get; set; }

        public string? Alias { get; set; }

        public string? IndexName { get; set; }

        public string? FunctionName { get; set; }

        public string? CteName { get; set; }

        public double? StartupCost { get; set; }

        public double? TotalCost { get; set; }

        public double? PlanRows { get; set; }

        public double? PlanWidth { get; set; }

        public double? ActualStartupMs { get; set; }

        public double? ActualTotalMs { get; set; }

        public double? ActualRows { get; set; }

        public double? ActualLoops { get; set; }

        public bool NeverExecuted { get; set; }

        public string? Filter { get; set; }

        public string? IndexCondition { get; set; }

        public string? RecheckCondition { get; set; }

        public string? HashCondition { get; set; }

        public string? MergeCondition { get; set; }

        public string? JoinFilter { get; set; }

        public string? OneTimeFilter { get; set; }

        public double? RowsRemovedByFilter { get; set; }

        public double? RowsRemovedByIndexRecheck { get; set; }

        public double? RowsRemovedByJoinFilter { get; set; }

        public List<string>? SortKey { get; set; }

        public List<string>? GroupKey { get; set; }

        public string? SortMethod { get; set; }

        public string? SortSpaceType { get; set; }

        public double? SortSpaceUsedKb { get; set; }

        public double? WorkerSortDiskKb { get; set; }

        public double? HashBuckets { get; set; }

        public double? HashBatches { get; set; }

        public double? OriginalHashBatches { get; set; }

        public double? PeakMemoryKb { get; set; }

        public double? HashAggBatches { get; set; }

        public double? DiskUsageKb { get; set; }

        public double? HeapFetches { get; set; }

        public double? CacheHits { get; set; }

        public double? CacheMisses { get; set; }

        public double? CacheEvictions { get; set; }

        public double? CacheOverflows { get; set; }

        public double? WalRecords { get; set; }

        public double? WalFpi { get; set; }

        public double? WalBytes { get; set; }

        public double? ExactHeapBlocks { get; set; }

        public double? LossyHeapBlocks { get; set; }

        public double? WorkersPlanned { get; set; }

        public double? WorkersLaunched { get; set; }

        public bool? Disabled { get; set; }

        public double? IndexSearches { get; set; }

        public string? Storage { get; set; }

        public double? MaximumStorageKb { get; set; }

        public PlanBuffers? Buffers { get; set; }

        public double? IoReadMs { get; set; }

        public double? IoWriteMs { get; set; }

        /// <summary>Keys the text printed, in the order it printed them, as the detail panel lists them.</summary>
        public List<PlanProperty> Extra { get; } = [];

        public List<string> Unread { get; } = [];

        public List<Node> Children { get; } = [];
    }

    /// <summary>A <c>InitPlan</c>, <c>SubPlan</c> or <c>CTE</c> header, waiting for the node under it.</summary>
    private sealed record Pending(int Column, string Name, string Relationship);

    // ---- The parser ----------------------------------------------------------------------------------------------------

    private sealed class Parser(Func<string, string>? mask)
    {
        private readonly List<Node> stack = [];
        private readonly List<string> unreadAround = [];
        private readonly List<PlanTrigger> triggers = [];
        private readonly List<PlanProperty> settings = [];
        private readonly List<PlanProperty> jitProperties = [];

        private Node? root;
        private Pending? pending;
        private int count;
        private bool truncated;
        private int statements = 1;
        private bool anyBuffers;
        private bool anyTime;

        private double? planningMs;
        private double? executionMs;
        private double? jitFunctions;
        private double? jitMs;
        private double? planningMemoryBytes;
        private double? serializationMs;
        private double? serializationKb;

        public QueryPlanRead Parse(string text)
        {
            string[] lines = text.Split('\n');
            int at = 0;

            while (at < lines.Length && !IsNodeStart(Clean(lines[at]).TrimStart()))
            {
                at++;
            }

            if (at >= lines.Length)
            {
                return QueryPlanRead.Failed(NotAPlan);
            }

            int baseIndent = LeadingSpaces(Clean(lines[at]));
            bool trailer = false;
            string? section = null;

            for (int i = at; i < lines.Length; i++)
            {
                string raw = Clean(lines[i]);

                if (raw.Length == 0 || raw.AsSpan().Trim().IsEmpty)
                {
                    continue;
                }

                int lead = LeadingSpaces(raw);
                int indent = Math.Max(0, lead - baseIndent);
                string body = raw[lead..];

                if (root is null)
                {
                    StartNode(body, indent);
                    continue;
                }

                if (!trailer)
                {
                    if (body.StartsWith("->", StringComparison.Ordinal))
                    {
                        StartNode(body, indent);
                    }
                    else if (indent > 0 && IsSubplanHeader(body))
                    {
                        SubplanHeader(body, indent);
                    }
                    else if (indent > 0)
                    {
                        Detail(body);
                    }
                    else
                    {
                        trailer = true;
                    }
                }

                if (trailer)
                {
                    if (!Trailer(body, indent, ref section))
                    {
                        break;
                    }
                }
            }

            if (root is null)
            {
                return QueryPlanRead.Failed(NotAPlan);
            }

            NormalizeBuffers();

            PlanNode tree = Build(root, null, 0, new Counter());
            QueryPlanReader.ComputeCosts(tree);
            QueryPlanReader.ComputeTimes(tree);

            return QueryPlanRead.Of(new QueryPlan(tree, count, truncated, statements)
            {
                FromText = true,
                PlanningMs = planningMs,
                ExecutionMs = executionMs,
                Triggers = triggers,
                JitFunctions = jitFunctions,
                JitMs = jitMs,
                Settings = mask is null ? settings : [],
                JitProperties = mask is null ? jitProperties : [],
                PlanningMemoryBytes = planningMemoryBytes,
                SerializationMs = serializationMs,
                SerializationKb = serializationKb,
                UnreadLines = mask is null ? unreadAround : [],
            });
        }

        // ---- Lines -------------------------------------------------------------------------------------------------------

        private static string Clean(string line) =>
            line.Contains('\t', StringComparison.Ordinal) ? line.TrimEnd().Replace("\t", "    ", StringComparison.Ordinal) : line.TrimEnd();

        private static int LeadingSpaces(string line)
        {
            int i = 0;

            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            return i;
        }

        /// <summary>Whether a line (without its indent) opens a node: an arrow, or a heading with the marks of one.</summary>
        private static bool IsNodeStart(string body)
        {
            if (body.Length == 0)
            {
                return false;
            }

            if (body.StartsWith("->", StringComparison.Ordinal))
            {
                return true;
            }

            if (body.Contains(" (cost=", StringComparison.Ordinal) || body.Contains(" (actual ", StringComparison.Ordinal)
                || body.Contains(" (never executed)", StringComparison.Ordinal))
            {
                return true;
            }

            if (body.Contains(": ", StringComparison.Ordinal) || body.EndsWith(':'))
            {
                return false;
            }

            ReadOnlySpan<char> rest = body;

            while (true)
            {
                int space = rest.IndexOf(' ');
                ReadOnlySpan<char> word = space < 0 ? rest : rest[..space];

                if (space >= 0 && word is "Parallel" or "Async" or "Partial" or "Finalize")
                {
                    rest = rest[(space + 1)..];
                    continue;
                }

                return HeadingWords.Contains(word.ToString());
            }
        }

        private static bool IsSubplanHeader(string body) =>
            body.StartsWith("InitPlan ", StringComparison.Ordinal)
            || body.StartsWith("SubPlan ", StringComparison.Ordinal)
            || body.StartsWith("CTE ", StringComparison.Ordinal);

        private void SubplanHeader(string body, int column)
        {
            // The header sits at its parent's key column; whatever is deeper has ended.
            while (stack.Count > 1 && stack[^1].DetailColumn > column)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            string relationship = body.StartsWith("SubPlan ", StringComparison.Ordinal) ? "SubPlan" : "InitPlan";
            pending = new Pending(column, body, relationship);
        }

        // ---- A node ------------------------------------------------------------------------------------------------------

        private void StartNode(string body, int arrowColumn)
        {
            string heading = body;
            int textColumn = arrowColumn;

            if (body.StartsWith("->", StringComparison.Ordinal))
            {
                int i = 2;

                while (i < body.Length && body[i] == ' ')
                {
                    i++;
                }

                heading = body[i..];
                textColumn = arrowColumn + i;

                if (heading.Length == 0)
                {
                    Around(body);
                    return;
                }
            }

            Node? parent = null;

            if (root is not null)
            {
                while (stack.Count > 1 && stack[^1].DetailColumn > arrowColumn)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                parent = stack[^1];
            }

            Pending? header = pending is { } p && arrowColumn > p.Column ? p : null;
            pending = null;

            bool dropped = false;

            if (parent is not null)
            {
                if (parent.Dropped)
                {
                    dropped = true;
                }
                else if (count >= QueryPlanReader.MaxNodes || parent.Depth + 1 > QueryPlanReader.MaxDepth)
                {
                    dropped = true;
                    truncated = true;
                }
            }

            Node node = new() { Depth = parent is null ? 0 : parent.Depth + 1, DetailColumn = textColumn + 2, Dropped = dropped };

            if (header is not null)
            {
                node.PendingRelationship = header.Relationship;
                node.SubplanName = M(header.Name);
            }

            if (!dropped)
            {
                count++;
                ReadHeading(node, heading);
                parent?.Children.Add(node);
            }

            if (root is null)
            {
                root = node;
            }

            stack.Add(node);
        }

        private void ReadHeading(Node node, string heading)
        {
            int cut = Marker(heading);
            string head = (cut < 0 ? heading : heading[..cut]).TrimEnd();
            string tail = cut < 0 ? string.Empty : heading[cut..];

            ReadTail(node, tail);

            // What the heading names: "<name> [using <index>] [on <target> [<alias>]]".
            int usingAt = IndexOutsideQuotes(head, " using ", 0);
            int onAt = IndexOutsideQuotes(head, " on ", 0);
            int nameEnd = new[] { usingAt, onAt }.Where(static x => x >= 0).DefaultIfEmpty(head.Length).Min();
            string name = head[..nameEnd].Trim();

            string? indexName = null;
            List<string>? target = null;
            string? alias = null;

            if (usingAt >= 0)
            {
                int start = usingAt + " using ".Length;
                List<string> parts = ReadName(head, ref start);

                if (parts.Count > 0)
                {
                    indexName = parts[^1];
                }

                onAt = IndexOutsideQuotes(head, " on ", Math.Max(0, start - 1));
            }

            if (onAt >= 0)
            {
                int start = onAt + " on ".Length;
                target = ReadName(head, ref start);
                alias = ReadName(head, ref start) is { Count: > 0 } second ? second[^1] : null;
            }

            Classify(node, name);
            Target(node, indexName, target, alias);
        }

        /// <summary>Where the costs and actuals of a heading begin: the first <c> (cost=</c>, <c> (actual </c> or <c> (never executed)</c>.</summary>
        private static int Marker(string heading)
        {
            int best = -1;

            foreach (string marker in (ReadOnlySpan<string>) [" (cost=", " (actual ", " (never executed)"])
            {
                int at = heading.IndexOf(marker, StringComparison.Ordinal);

                if (at >= 0 && (best < 0 || at < best))
                {
                    best = at;
                }
            }

            return best;
        }

        private void ReadTail(Node node, string tail)
        {
            if (tail.Length == 0)
            {
                return;
            }

            Match cost = CostGroup.Match(tail);

            if (cost.Success)
            {
                node.StartupCost = D(cost.Groups["s"].Value);
                node.TotalCost = D(cost.Groups["t"].Value);
                node.PlanRows = D(cost.Groups["r"].Value);
                node.PlanWidth = D(cost.Groups["w"].Value);
            }

            if (tail.Contains("(never executed)", StringComparison.Ordinal))
            {
                node.ActualLoops = 0;
                node.ActualRows = 0;
                node.NeverExecuted = true;
                return;
            }

            Match actual = ActualGroup.Match(tail);

            if (actual.Success)
            {
                node.ActualRows = D(actual.Groups["r"].Value);
                node.ActualLoops = D(actual.Groups["l"].Value);

                if (actual.Groups["s"].Success)
                {
                    node.ActualStartupMs = D(actual.Groups["s"].Value);
                    node.ActualTotalMs = D(actual.Groups["t"].Value);
                    anyTime = true;
                }
            }
        }

        /// <summary>The heading's name taken apart into JSON's <c>Node Type</c> and the fields JSON gives it.</summary>
        private static void Classify(Node node, string name)
        {
            string text = name;

            // PostgreSQL prints "Index Scan Backward"; JSON says "Index Scan" and "Scan Direction": "Backward".
            if (text.EndsWith(" Backward", StringComparison.Ordinal))
            {
                text = text[..^" Backward".Length];
                node.Extra.Add(new PlanProperty("Scan Direction", "Backward"));
            }

            // "HashSetOp Intersect All": the command is a key of its own in JSON.
            foreach (string command in (ReadOnlySpan<string>) ["Intersect All", "Except All", "Intersect", "Except"])
            {
                if ((text.StartsWith("SetOp ", StringComparison.Ordinal) || text.StartsWith("HashSetOp ", StringComparison.Ordinal))
                    && text.EndsWith(" " + command, StringComparison.Ordinal))
                {
                    text = text[..^(command.Length + 1)];
                    node.Extra.Add(new PlanProperty("Command", command));
                    break;
                }
            }

            node.Label = text;

            string kind = text;
            string? partial = null;

            while (true)
            {
                if (kind.StartsWith("Parallel ", StringComparison.Ordinal))
                {
                    node.Parallel = true;
                    kind = kind["Parallel ".Length..];
                }
                else if (kind.StartsWith("Async ", StringComparison.Ordinal))
                {
                    kind = kind["Async ".Length..];
                }
                else if (kind.StartsWith("Partial ", StringComparison.Ordinal))
                {
                    partial = "Partial";
                    kind = kind["Partial ".Length..];
                }
                else if (kind.StartsWith("Finalize ", StringComparison.Ordinal))
                {
                    partial = "Finalize";
                    kind = kind["Finalize ".Length..];
                }
                else
                {
                    break;
                }
            }

            switch (kind)
            {
                case "Aggregate":
                case "GroupAggregate":
                case "HashAggregate":
                case "MixedAggregate":
                    node.NodeType = "Aggregate";
                    node.Strategy = kind switch
                    {
                        "GroupAggregate" => "Sorted",
                        "HashAggregate" => "Hashed",
                        "MixedAggregate" => "Mixed",
                        _ => "Plain",
                    };
                    node.PartialMode = partial ?? "Simple";
                    return;

                case "SetOp":
                case "HashSetOp":
                    node.NodeType = "SetOp";
                    node.Strategy = kind == "HashSetOp" ? "Hashed" : "Sorted";
                    return;

                case "Insert" or "Update" or "Delete" or "Merge":
                    node.NodeType = "ModifyTable";
                    node.Operation = kind;
                    return;

                case "Nested Loop":
                    node.NodeType = "Nested Loop";
                    node.JoinType = "Inner";
                    return;
            }

            foreach ((string prefix, string type) in (ReadOnlySpan<(string, string)>) [("Nested Loop", "Nested Loop"), ("Hash", "Hash Join"), ("Merge", "Merge Join")])
            {
                if (TryJoin(kind, prefix, out string joinType))
                {
                    node.NodeType = type;
                    node.JoinType = joinType;
                    return;
                }
            }

            node.NodeType = kind;
        }

        /// <summary>Whether <paramref name="kind" /> is <c>&lt;prefix&gt; Join</c> (an inner join) or <c>&lt;prefix&gt; &lt;type&gt; Join</c>, and which type.</summary>
        private static bool TryJoin(string kind, string prefix, out string joinType)
        {
            joinType = string.Empty;

            if (!kind.StartsWith(prefix + " ", StringComparison.Ordinal) || !kind.EndsWith(" Join", StringComparison.Ordinal))
            {
                return false;
            }

            if (kind.Length == prefix.Length + " Join".Length)
            {
                joinType = "Inner";
                return true;
            }

            int start = prefix.Length + 1;
            int end = kind.Length - " Join".Length;

            if (end <= start)
            {
                return false;
            }

            joinType = kind[start..end];
            return true;
        }

        /// <summary>
        /// What the heading's <c>using</c> and <c>on</c> name, as JSON names it: a relation scan's schema, relation and alias; a function
        /// scan's function; a CTE scan's CTE; a subquery or values scan's alias; an index scan's index. A scan with no alias printed
        /// carries its own name as the alias, as JSON does.
        /// </summary>
        private void Target(Node node, string? indexName, List<string>? target, string? alias)
        {
            node.IndexName = indexName is null ? null : M(indexName);

            if (target is null or { Count: 0 })
            {
                return;
            }

            string name = target[^1];
            string? schema = target.Count > 1 ? target[^2] : null;

            switch (node.NodeType)
            {
                case "Bitmap Index Scan":
                    node.IndexName = M(name);
                    return;

                case "Function Scan" or "Table Function Scan":
                    node.Schema = schema is null ? null : M(schema);
                    node.FunctionName = M(name);
                    node.Alias = M(alias ?? name);
                    return;

                case "CTE Scan" or "WorkTable Scan" or "Named Tuplestore Scan":
                    node.CteName = M(name);
                    node.Alias = M(alias ?? name);
                    return;

                case "Subquery Scan" or "Values Scan":
                    node.Alias = M(name);
                    return;

                default:
                    node.Schema = schema is null ? null : M(schema);
                    node.RelationName = M(name);
                    node.Alias = M(alias ?? name);
                    return;
            }
        }

        // ---- A node's own lines ------------------------------------------------------------------------------------------

        private void Detail(string line)
        {
            if (stack.Count == 0 || stack[^1].Dropped)
            {
                return;
            }

            Node node = stack[^1];

            if (line.StartsWith("Worker ", StringComparison.Ordinal) && WorkerLine(node, line))
            {
                return;
            }

            int colon = line.IndexOf(": ", StringComparison.Ordinal);

            if (colon < 0 && line.EndsWith(':'))
            {
                colon = line.Length - 1;
            }

            if (colon <= 0 || colon > 60 || line.AsSpan(0, colon).IndexOfAny('(', ';', '\'') >= 0)
            {
                Unread(node, line);
                return;
            }

            string key = line[..colon];
            string value = colon + 2 <= line.Length ? line[(colon + 2)..].Trim() : string.Empty;

            if (StatKeys.Contains(key))
            {
                Stats(node, line);
                return;
            }

            switch (key)
            {
                case "Filter":
                    node.Filter = M(value);
                    return;
                case "Index Cond":
                    node.IndexCondition = M(value);
                    return;
                case "Recheck Cond":
                    node.RecheckCondition = M(value);
                    return;
                case "Hash Cond":
                    node.HashCondition = M(value);
                    return;
                case "Merge Cond":
                    node.MergeCondition = M(value);
                    return;
                case "Join Filter":
                    node.JoinFilter = M(value);
                    return;
                case "One-Time Filter":
                    node.OneTimeFilter = M(value);
                    return;
                case "Rows Removed by Filter":
                    node.RowsRemovedByFilter = N(value);
                    return;
                case "Rows Removed by Index Recheck":
                    node.RowsRemovedByIndexRecheck = N(value);
                    return;
                case "Rows Removed by Join Filter":
                    node.RowsRemovedByJoinFilter = N(value);
                    return;
                case "Heap Fetches":
                    node.HeapFetches = N(value);
                    return;
                case "Index Searches":
                    node.IndexSearches = N(value);
                    return;
                case "Workers Planned":
                    node.WorkersPlanned = N(value);
                    return;
                case "Workers Launched":
                    node.WorkersLaunched = N(value);
                    return;
                case "Disabled":
                    node.Disabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    return;
                case "Sort Key" when node.SortKey is null:
                    node.SortKey = ExpressionList(value);
                    return;
                case "Group Key" when node.GroupKey is null:
                    node.GroupKey = ExpressionList(value);
                    return;
                case "Heap Blocks":
                    foreach (Match match in Pair.Matches(value))
                    {
                        switch (match.Groups["k"].Value.Trim())
                        {
                            case "exact":
                                node.ExactHeapBlocks = D(match.Groups["v"].Value);
                                break;
                            case "lossy":
                                node.LossyHeapBlocks = D(match.Groups["v"].Value);
                                break;
                        }
                    }

                    return;
                case "Buffers":
                    node.Buffers = Buffers(value);
                    anyBuffers = true;
                    return;
                case "I/O Timings":
                    IoTimings(node, value);
                    return;
                case "WAL":
                    foreach (Match match in Pair.Matches(value))
                    {
                        double? number = D(match.Groups["v"].Value);

                        switch (match.Groups["k"].Value.Trim())
                        {
                            case "records":
                                node.WalRecords = number;
                                break;
                            case "fpi":
                                node.WalFpi = number;
                                break;
                            case "bytes":
                                node.WalBytes = number;
                                break;
                        }
                    }

                    return;
                default:
                    Extra(node, key, value);
                    return;
            }
        }

        /// <summary>A line of several statistics - <c>Sort Method: quicksort  Memory: 25kB</c>, <c>Buckets: 1024  Batches: 1  Memory Usage: 9kB</c>.</summary>
        private static void Stats(Node node, string line)
        {
            bool hash = line.StartsWith("Buckets:", StringComparison.Ordinal);
            bool sort = line.StartsWith("Sort Method", StringComparison.Ordinal);

            foreach (string piece in line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = piece.IndexOf(": ", StringComparison.Ordinal);

                if (colon <= 0)
                {
                    node.Extra.Add(new PlanProperty(piece, string.Empty));
                    continue;
                }

                string key = piece[..colon];
                string value = piece[(colon + 2)..].Trim();

                switch (key)
                {
                    case "Sort Method":
                        node.SortMethod = value;
                        break;
                    case "Memory" when sort:
                        node.SortSpaceType = "Memory";
                        node.SortSpaceUsedKb = N(value);
                        break;
                    case "Disk" when sort:
                        node.SortSpaceType = "Disk";
                        node.SortSpaceUsedKb = N(value);
                        break;
                    case "Buckets":
                        node.HashBuckets = N(value);
                        break;
                    case "Batches" when hash:
                        node.HashBatches = N(value);

                        if (Originally.Match(value) is { Success: true } original)
                        {
                            node.OriginalHashBatches = D(original.Groups["o"].Value);
                        }

                        break;
                    case "Batches":
                    case "HashAgg Batches":
                        node.HashAggBatches = N(value);
                        break;
                    case "Memory Usage":
                    case "Peak Memory Usage":
                        node.PeakMemoryKb = N(value);
                        break;
                    case "Disk Usage":
                        node.DiskUsageKb = N(value);
                        break;
                    case "Hits":
                        node.CacheHits = N(value);
                        break;
                    case "Misses":
                        node.CacheMisses = N(value);
                        break;
                    case "Evictions":
                        node.CacheEvictions = N(value);
                        break;
                    case "Overflows":
                        node.CacheOverflows = N(value);
                        break;
                    case "Storage":
                        node.Storage = value;
                        break;
                    case "Maximum Storage":
                        node.MaximumStorageKb = N(value);
                        break;
                    default:
                        node.Extra.Add(new PlanProperty(key, value));
                        break;
                }
            }
        }

        /// <summary>A parallel worker's line: only a worker's sort that went to disk is modelled; the rest is kept as a property.</summary>
        private static bool WorkerLine(Node node, string line)
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 0)
            {
                return false;
            }

            string worker = line[..colon];
            string rest = line[(colon + 1)..].Trim();

            if (rest.StartsWith("Sort Method", StringComparison.Ordinal) && rest.Contains("Disk:", StringComparison.Ordinal))
            {
                double used = N(rest[(rest.IndexOf("Disk:", StringComparison.Ordinal) + "Disk:".Length)..]) ?? 0;
                node.WorkerSortDiskKb = node.WorkerSortDiskKb is { } seen ? Math.Max(seen, used) : used;
            }

            Extra(node, worker, rest);
            return true;
        }

        private static PlanBuffers Buffers(string value)
        {
            long[] counters = new long[10];

            foreach (string group in value.Split(','))
            {
                string kind = group.TrimStart();
                int space = kind.IndexOf(' ', StringComparison.Ordinal);

                if (space <= 0)
                {
                    continue;
                }

                int baseAt = kind[..space] switch
                {
                    "shared" => 0,
                    "local" => 4,
                    "temp" => 8,
                    _ => -1,
                };

                if (baseAt < 0)
                {
                    continue;
                }

                foreach (Match match in Pair.Matches(kind[space..]))
                {
                    int offset = (baseAt, match.Groups["k"].Value.Trim()) switch
                    {
                        (8, "read") => 0,
                        (8, "written") => 1,
                        (_, "hit") => 0,
                        (_, "read") => 1,
                        (_, "dirtied") => 2,
                        (_, "written") => 3,
                        _ => -1,
                    };

                    if (offset >= 0 && double.TryParse(match.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                        && number is > 0 and < long.MaxValue)
                    {
                        counters[baseAt + offset] = (long) number;
                    }
                }
            }

            return new PlanBuffers(counters[0], counters[1], counters[2], counters[3], counters[4], counters[5], counters[6], counters[7], counters[8], counters[9]);
        }

        private static void IoTimings(Node node, string value)
        {
            foreach (Match match in ReadWrite.Matches(value))
            {
                double number = D(match.Groups["v"].Value) ?? 0;

                if (match.Groups["k"].Value == "read")
                {
                    node.IoReadMs = (node.IoReadMs ?? 0) + number;
                }
                else
                {
                    node.IoWriteMs = (node.IoWriteMs ?? 0) + number;
                }
            }
        }

        private List<string> ExpressionList(string value)
        {
            if (value.Trim() == "()")
            {
                return [];
            }

            List<string> items = [];
            int depth = 0;
            char quote = '\0';
            int start = 0;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        if (i + 1 < value.Length && value[i + 1] == quote)
                        {
                            i++;
                        }
                        else
                        {
                            quote = '\0';
                        }
                    }

                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                }
                else if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    depth = Math.Max(0, depth - 1);
                }
                else if (c == ',' && depth == 0)
                {
                    items.Add(M(value[start..i].Trim()));
                    start = i + 1;
                }
            }

            items.Add(M(value[start..].Trim()));
            return items;
        }

        private static void Extra(Node node, string key, string value)
        {
            if (node.Extra.Count < QueryPlanReader.MaxProperties)
            {
                node.Extra.Add(new PlanProperty(key, Clip(value)));
            }
        }

        private static void Unread(Node node, string line)
        {
            if (node.Unread.Count < MaxUnreadPerNode)
            {
                node.Unread.Add(Clip(line));
            }
        }

        private void Around(string line)
        {
            if (unreadAround.Count < MaxUnreadAround)
            {
                unreadAround.Add(Clip(line));
            }
        }

        // ---- The statement's own lines, after the tree -------------------------------------------------------------------

        /// <summary>One line at or after the tree's end; <see langword="false" /> once another statement's plan begins.</summary>
        private bool Trailer(string body, int indent, ref string? section)
        {
            if (indent == 0)
            {
                section = null;

                if (body.StartsWith("Planning Time:", StringComparison.Ordinal))
                {
                    planningMs = N(body);
                }
                else if (body.StartsWith("Execution Time:", StringComparison.Ordinal))
                {
                    executionMs = N(body);
                }
                else if (body == "Planning:")
                {
                    section = "Planning";
                }
                else if (body == "JIT:")
                {
                    section = "JIT";
                }
                else if (body.StartsWith("Settings:", StringComparison.Ordinal))
                {
                    ReadSettings(body["Settings:".Length..]);
                }
                else if (body.StartsWith("Serialization:", StringComparison.Ordinal))
                {
                    section = "Serialization";
                    serializationMs = Pair.Matches(body).FirstOrDefault(static x => x.Groups["k"].Value.Trim() == "time") is { } time ? D(time.Groups["v"].Value) : null;
                    serializationKb = Pair.Matches(body).FirstOrDefault(static x => x.Groups["k"].Value.Trim() == "output") is { } output ? D(output.Groups["v"].Value) : null;
                }
                else if (body.StartsWith("Trigger ", StringComparison.Ordinal) && ReadTrigger(body))
                {
                    // Read.
                }
                else if (IsRowsFooter(body) || body.StartsWith("Query Identifier:", StringComparison.Ordinal))
                {
                    // psql's "(5 rows)", and the query id VERBOSE prints: the JSON reader has no use for it either.
                }
                else if (IsNodeStart(body) && !body.StartsWith("->", StringComparison.Ordinal))
                {
                    statements++;
                    return false;
                }
                else
                {
                    Around(body);
                }

                return true;
            }

            switch (section)
            {
                case "JIT":
                    JitLine(body);
                    break;
                case "Planning" or "Serialization" when body.StartsWith("Buffers:", StringComparison.Ordinal):
                    // Their buffers are the statement's own, which no node carries; the JSON reader ignores them as well.
                    break;
                case "Planning" when body.StartsWith("Memory:", StringComparison.Ordinal):
                    // The text says "used=12kB" and JSON's "Memory Used" says 12 - kilobytes, as measured on 17 and 18 - so the same
                    // number is kept, as the JSON reader keeps it: both readers carry what JSON carries.
                    planningMemoryBytes = Pair.Matches(body).FirstOrDefault(static x => x.Groups["k"].Value.Trim() == "used") is { } used
                        ? D(used.Groups["v"].Value)
                        : null;
                    break;
                default:
                    Around(body);
                    break;
            }

            return true;
        }

        private static bool IsRowsFooter(string body) =>
            body.Length > 4 && body[0] == '(' && (body.EndsWith(" rows)", StringComparison.Ordinal) || body.EndsWith(" row)", StringComparison.Ordinal));

        private void ReadSettings(string list)
        {
            foreach (Match match in Setting.Matches(list))
            {
                settings.Add(new PlanProperty(match.Groups["k"].Value, match.Groups["v"].Value.Replace("''", "'", StringComparison.Ordinal)));
            }
        }

        private bool ReadTrigger(string body)
        {
            // "Trigger <name> [on <relation>]: time=<ms> calls=<n>" (no time with TIMING off), and "Trigger <name> for constraint <constraint>".
            int marker = body.LastIndexOf(": time=", StringComparison.Ordinal);

            if (marker < 0)
            {
                // With TIMING off only the calls are printed.
                marker = body.LastIndexOf(": calls=", StringComparison.Ordinal);
            }

            if (marker < 0)
            {
                return false;
            }

            string name = body["Trigger ".Length..marker];
            string? relation = null;

            // "Trigger RI_ConstraintTrigger_c_16539 for constraint children_parent_id_fkey": JSON's name is the first part.
            int constraint = name.IndexOf(" for constraint ", StringComparison.Ordinal);

            if (constraint > 0)
            {
                name = name[..constraint];
            }

            int on = name.LastIndexOf(" on ", StringComparison.Ordinal);

            if (on >= 0)
            {
                relation = name[(on + 4)..];
                name = name[..on];
            }

            double? time = null;
            double? calls = null;

            foreach (Match match in Pair.Matches(body[marker..]))
            {
                switch (match.Groups["k"].Value.Trim())
                {
                    case "time":
                        time = D(match.Groups["v"].Value);
                        break;
                    case "calls":
                        calls = D(match.Groups["v"].Value);
                        break;
                }
            }

            triggers.Add(new PlanTrigger(M(name), relation is null ? null : M(relation), time, calls));
            return true;
        }

        private void JitLine(string body)
        {
            int colon = body.IndexOf(": ", StringComparison.Ordinal);

            if (colon <= 0)
            {
                Around(body);
                return;
            }

            string key = body[..colon];
            string value = body[(colon + 2)..].Trim();

            switch (key)
            {
                case "Functions":
                    jitFunctions = N(value);
                    jitProperties.Add(new PlanProperty("Functions", value));
                    break;

                case "Options":
                    foreach (string option in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int space = option.IndexOf(' ', StringComparison.Ordinal);

                        if (space > 0)
                        {
                            jitProperties.Add(new PlanProperty("Options › " + option[..space], option[(space + 1)..]));
                        }
                    }

                    break;

                case "Timing":
                    foreach (Match match in Timed.Matches(value))
                    {
                        string name = match.Groups["k"].Value.Trim().TrimStart('(', ',', ' ');

                        if (name.Length == 0)
                        {
                            continue;
                        }

                        jitProperties.Add(new PlanProperty("Timing › " + name, match.Groups["v"].Value));

                        if (name == "Total")
                        {
                            jitMs = D(match.Groups["v"].Value);
                        }
                    }

                    break;

                default:
                    Around(body);
                    break;
            }
        }

        // ---- The tree, as the model ----------------------------------------------------------------------------------------

        /// <summary>
        /// Text prints a <c>Buffers</c> line for a node only when a counter is above zero, and JSON prints every counter of every node when
        /// <c>BUFFERS</c> is on - so once any node printed a line, a node without one is read as zeros. A never-executed node's timing is
        /// zero in JSON when the plan was timed.
        /// </summary>
        private void NormalizeBuffers()
        {
            if (root is null || (!anyBuffers && !anyTime))
            {
                return;
            }

            PlanBuffers zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            Stack<Node> pendingNodes = new();
            pendingNodes.Push(root);

            while (pendingNodes.Count > 0)
            {
                Node node = pendingNodes.Pop();

                if (anyBuffers)
                {
                    node.Buffers ??= zero;
                }

                if (anyTime && node.NeverExecuted)
                {
                    node.ActualStartupMs = 0;
                    node.ActualTotalMs = 0;
                }

                foreach (Node child in node.Children)
                {
                    pendingNodes.Push(child);
                }
            }
        }

        /// <summary>Hands out the walk's ids: the root is 0, then each node before its children.</summary>
        private sealed class Counter
        {
            public int Next;
        }

        private PlanNode Build(Node node, Node? parent, int sibling, Counter counter)
        {
            int id = counter.Next++;
            List<PlanNode> children = [];
            int ordinary = 0;

            foreach (Node child in node.Children)
            {
                int index = child.PendingRelationship is null ? ordinary++ : -1;
                children.Add(Build(child, node, index, counter));
            }

            string? relationship = node.PendingRelationship ?? (parent is null ? null : Relationship(parent.NodeType, sibling));
            List<PlanProperty> properties = mask is null ? Properties(node, relationship) : [];

            return new PlanNode
            {
                Id = id,
                Depth = node.Depth,
                NodeType = node.NodeType,
                Label = node.Label,
                ParentRelationship = relationship,
                SubplanName = node.SubplanName,
                Schema = node.Schema,
                RelationName = node.RelationName,
                Alias = node.Alias,
                IndexName = node.IndexName,
                FunctionName = node.FunctionName,
                CteName = node.CteName,
                Operation = node.Operation,
                Strategy = node.Strategy,
                JoinType = node.JoinType,
                PartialMode = node.PartialMode,
                ParallelAware = node.Parallel,
                StartupCost = node.StartupCost,
                TotalCost = node.TotalCost,
                PlanRows = node.PlanRows,
                PlanWidth = node.PlanWidth,
                ActualStartupMs = node.ActualStartupMs,
                ActualTotalMs = node.ActualTotalMs,
                ActualRows = node.ActualRows,
                ActualLoops = node.ActualLoops,
                Filter = node.Filter,
                IndexCondition = node.IndexCondition,
                RecheckCondition = node.RecheckCondition,
                HashCondition = node.HashCondition,
                MergeCondition = node.MergeCondition,
                JoinFilter = node.JoinFilter,
                OneTimeFilter = node.OneTimeFilter,
                RowsRemovedByFilter = node.RowsRemovedByFilter,
                RowsRemovedByIndexRecheck = node.RowsRemovedByIndexRecheck,
                RowsRemovedByJoinFilter = node.RowsRemovedByJoinFilter,
                SortKey = node.SortKey ?? [],
                GroupKey = node.GroupKey ?? [],
                SortMethod = node.SortMethod,
                SortSpaceType = node.SortSpaceType,
                SortSpaceUsedKb = node.SortSpaceUsedKb,
                WorkerSortDiskKb = node.WorkerSortDiskKb,
                HashBuckets = node.HashBuckets,
                HashBatches = node.HashBatches,
                OriginalHashBatches = node.OriginalHashBatches,
                PeakMemoryKb = node.PeakMemoryKb,
                HashAggBatches = node.HashAggBatches,
                DiskUsageKb = node.DiskUsageKb,
                HeapFetches = node.HeapFetches,
                CacheHits = node.CacheHits,
                CacheMisses = node.CacheMisses,
                CacheEvictions = node.CacheEvictions,
                CacheOverflows = node.CacheOverflows,
                WalRecords = node.WalRecords,
                WalFpi = node.WalFpi,
                WalBytes = node.WalBytes,
                ExactHeapBlocks = node.ExactHeapBlocks,
                LossyHeapBlocks = node.LossyHeapBlocks,
                WorkersPlanned = node.WorkersPlanned,
                WorkersLaunched = node.WorkersLaunched,
                Disabled = node.Disabled,
                IndexSearches = node.IndexSearches,
                Storage = node.Storage,
                MaximumStorageKb = node.MaximumStorageKb,
                Buffers = node.Buffers,
                IoReadMs = node.IoReadMs,
                IoWriteMs = node.IoWriteMs,
                Properties = properties,
                UnreadLines = mask is null ? node.Unread : [],
                Children = children,
            };
        }

        /// <summary>How a child feeds its parent, where the text does not say: <c>explain.c</c>'s fixed rules.</summary>
        private static string Relationship(string parentType, int index) => parentType switch
        {
            "Nested Loop" or "Hash Join" or "Merge Join" or "Recursive Union" or "SetOp" => index == 0 ? "Outer" : "Inner",
            "Append" or "Merge Append" or "BitmapAnd" or "BitmapOr" => "Member",
            "Subquery Scan" => "Subquery",
            _ => "Outer",
        };

        /// <summary>The keys of the node as JSON would have printed them, then the ones only the text printed, for the node detail.</summary>
        private static List<PlanProperty> Properties(Node node, string? relationship)
        {
            List<PlanProperty> all = [];

            void Add(string key, string? value)
            {
                if (value is not null && all.Count < QueryPlanReader.MaxProperties)
                {
                    all.Add(new PlanProperty(key, Clip(value)));
                }
            }

            void AddNumber(string key, double? value) => Add(key, value is { } v ? v.ToString("0.###############", CultureInfo.InvariantCulture) : null);

            Add(PlanKeys.NodeType, node.NodeType);
            Add(PlanKeys.ParentRelationship, relationship);
            Add(PlanKeys.SubplanName, node.SubplanName);
            Add(PlanKeys.Schema, node.Schema);
            Add(PlanKeys.RelationName, node.RelationName);
            Add(PlanKeys.Alias, node.Alias);
            Add(PlanKeys.IndexName, node.IndexName);
            Add(PlanKeys.FunctionName, node.FunctionName);
            Add(PlanKeys.CteName, node.CteName);
            Add(PlanKeys.Operation, node.Operation);
            Add(PlanKeys.Strategy, node.Strategy);
            Add(PlanKeys.JoinType, node.JoinType);
            Add(PlanKeys.PartialMode, node.PartialMode);
            Add(PlanKeys.ParallelAware, node.Parallel ? "true" : null);
            AddNumber(PlanKeys.StartupCost, node.StartupCost);
            AddNumber(PlanKeys.TotalCost, node.TotalCost);
            AddNumber(PlanKeys.PlanRows, node.PlanRows);
            AddNumber(PlanKeys.PlanWidth, node.PlanWidth);
            AddNumber(PlanKeys.ActualStartupTime, node.ActualStartupMs);
            AddNumber(PlanKeys.ActualTotalTime, node.ActualTotalMs);
            AddNumber(PlanKeys.ActualRows, node.ActualRows);
            AddNumber(PlanKeys.ActualLoops, node.ActualLoops);
            Add(PlanKeys.Filter, node.Filter);
            Add(PlanKeys.IndexCond, node.IndexCondition);
            Add(PlanKeys.RecheckCond, node.RecheckCondition);
            Add(PlanKeys.HashCond, node.HashCondition);
            Add(PlanKeys.MergeCond, node.MergeCondition);
            Add(PlanKeys.JoinFilter, node.JoinFilter);
            Add(PlanKeys.OneTimeFilter, node.OneTimeFilter);
            Add(PlanKeys.SortKey, node.SortKey is { Count: > 0 } sort ? string.Join(", ", sort) : null);
            Add(PlanKeys.GroupKey, node.GroupKey is { Count: > 0 } group ? string.Join(", ", group) : null);
            AddNumber(PlanKeys.RowsRemovedByFilter, node.RowsRemovedByFilter);
            AddNumber(PlanKeys.RowsRemovedByIndexRecheck, node.RowsRemovedByIndexRecheck);
            AddNumber(PlanKeys.RowsRemovedByJoinFilter, node.RowsRemovedByJoinFilter);
            AddNumber(PlanKeys.HeapFetches, node.HeapFetches);
            AddNumber(PlanKeys.ExactHeapBlocks, node.ExactHeapBlocks);
            AddNumber(PlanKeys.LossyHeapBlocks, node.LossyHeapBlocks);
            AddNumber(PlanKeys.IndexSearches, node.IndexSearches);
            Add(PlanKeys.SortMethod, node.SortMethod);
            Add(PlanKeys.SortSpaceType, node.SortSpaceType);
            AddNumber(PlanKeys.SortSpaceUsed, node.SortSpaceUsedKb);
            AddNumber(PlanKeys.HashBuckets, node.HashBuckets);
            AddNumber(PlanKeys.HashBatches, node.HashBatches);
            AddNumber(PlanKeys.OriginalHashBatches, node.OriginalHashBatches);
            AddNumber(PlanKeys.PeakMemoryUsage, node.PeakMemoryKb);
            AddNumber(PlanKeys.HashAggBatches, node.HashAggBatches);
            AddNumber(PlanKeys.DiskUsage, node.DiskUsageKb);
            Add(PlanKeys.Storage, node.Storage);
            AddNumber(PlanKeys.MaximumStorage, node.MaximumStorageKb);
            AddNumber(PlanKeys.CacheHits, node.CacheHits);
            AddNumber(PlanKeys.CacheMisses, node.CacheMisses);
            AddNumber(PlanKeys.CacheEvictions, node.CacheEvictions);
            AddNumber(PlanKeys.CacheOverflows, node.CacheOverflows);
            AddNumber(PlanKeys.WorkersPlanned, node.WorkersPlanned);
            AddNumber(PlanKeys.WorkersLaunched, node.WorkersLaunched);
            Add(PlanKeys.Disabled, node.Disabled is { } disabled ? (disabled ? "true" : "false") : null);

            if (node.Buffers is { } buffers)
            {
                AddNumber(PlanKeys.SharedHitBlocks, buffers.SharedHit);
                AddNumber(PlanKeys.SharedReadBlocks, buffers.SharedRead);
                AddNumber(PlanKeys.SharedDirtiedBlocks, buffers.SharedDirtied);
                AddNumber(PlanKeys.SharedWrittenBlocks, buffers.SharedWritten);
                AddNumber(PlanKeys.LocalHitBlocks, buffers.LocalHit);
                AddNumber(PlanKeys.LocalReadBlocks, buffers.LocalRead);
                AddNumber(PlanKeys.LocalDirtiedBlocks, buffers.LocalDirtied);
                AddNumber(PlanKeys.LocalWrittenBlocks, buffers.LocalWritten);
                AddNumber(PlanKeys.TempReadBlocks, buffers.TempRead);
                AddNumber(PlanKeys.TempWrittenBlocks, buffers.TempWritten);
            }

            AddNumber(PlanKeys.IoReadTime, node.IoReadMs);
            AddNumber(PlanKeys.IoWriteTime, node.IoWriteMs);
            AddNumber(PlanKeys.WalRecords, node.WalRecords);
            AddNumber(PlanKeys.WalFpi, node.WalFpi);
            AddNumber(PlanKeys.WalBytes, node.WalBytes);

            foreach (PlanProperty extra in node.Extra)
            {
                if (all.Count >= QueryPlanReader.MaxProperties)
                {
                    break;
                }

                all.Add(extra);
            }

            return all;
        }

        // ---- Small helpers -----------------------------------------------------------------------------------------------

        private string M(string text) => mask is null ? text : mask(text);

        private static string Clip(string text) =>
            text.Length <= QueryPlanReader.MaxPropertyCharacters ? text : text[..QueryPlanReader.MaxPropertyCharacters] + "…";

        private static double? D(string? text) =>
            text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
                ? value
                : null;

        /// <summary>The first number in <paramref name="text" />: <c>25kB</c> is 25, <c>4 (originally 1)</c> is 4.</summary>
        private static double? N(string? text) =>
            text is null ? null : NumberText.Match(text) is { Success: true } match ? D(match.Value) : null;

        /// <summary>Where <paramref name="needle" /> is in <paramref name="text" />, outside a double-quoted identifier, from <paramref name="from" />.</summary>
        private static int IndexOutsideQuotes(string text, string needle, int from)
        {
            bool quoted = false;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && i >= from && string.CompareOrdinal(text, i, needle, 0, needle.Length) == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Reads one possibly qualified, possibly quoted name from <paramref name="text" /> at <paramref name="at" /> - <c>public.orders</c>,
        /// <c>"My Table"</c>, <c>pg_catalog.generate_series</c> - unquoted, and skips the spaces after it. An empty list at the end.
        /// </summary>
        private static List<string> ReadName(string text, ref int at)
        {
            List<string> parts = [];

            while (at < text.Length && text[at] == ' ')
            {
                at++;
            }

            while (at < text.Length)
            {
                System.Text.StringBuilder part = new();

                if (text[at] == '"')
                {
                    at++;

                    while (at < text.Length)
                    {
                        if (text[at] == '"')
                        {
                            if (at + 1 < text.Length && text[at + 1] == '"')
                            {
                                part.Append('"');
                                at += 2;
                                continue;
                            }

                            at++;
                            break;
                        }

                        part.Append(text[at++]);
                    }
                }
                else
                {
                    while (at < text.Length && text[at] is not (' ' or '.'))
                    {
                        part.Append(text[at++]);
                    }
                }

                parts.Add(part.ToString());

                if (at < text.Length && text[at] == '.')
                {
                    at++;
                    continue;
                }

                break;
            }

            while (at < text.Length && text[at] == ' ')
            {
                at++;
            }

            return parts;
        }
    }
}
