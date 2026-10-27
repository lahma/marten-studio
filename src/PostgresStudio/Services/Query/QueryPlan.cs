namespace PostgresStudio.Services.Query;

/// <summary>A plan node's buffer counters, as <c>EXPLAIN (ANALYZE, BUFFERS)</c> reports them - including the nodes below it.</summary>
/// <param name="SharedHit">Shared blocks found in the buffer cache.</param>
/// <param name="SharedRead">Shared blocks read from outside the cache.</param>
/// <param name="SharedDirtied">Shared blocks this node dirtied.</param>
/// <param name="SharedWritten">Shared blocks this node wrote out.</param>
/// <param name="LocalHit">Local (temporary table) blocks found in the cache.</param>
/// <param name="LocalRead">Local blocks read.</param>
/// <param name="LocalDirtied">Local blocks dirtied.</param>
/// <param name="LocalWritten">Local blocks written.</param>
/// <param name="TempRead">Temporary-file blocks read: a sort, a hash or a materialisation that did not fit in memory.</param>
/// <param name="TempWritten">Temporary-file blocks written.</param>
internal sealed record PlanBuffers(
    long SharedHit,
    long SharedRead,
    long SharedDirtied,
    long SharedWritten,
    long LocalHit,
    long LocalRead,
    long LocalDirtied,
    long LocalWritten,
    long TempRead,
    long TempWritten)
{
    /// <summary>Whether any counter is above zero.</summary>
    public bool Any =>
        SharedHit > 0 || SharedRead > 0 || SharedDirtied > 0 || SharedWritten > 0 || LocalHit > 0 || LocalRead > 0
        || LocalDirtied > 0 || LocalWritten > 0 || TempRead > 0 || TempWritten > 0;
}

/// <summary>A trigger's time under <c>EXPLAIN ANALYZE</c>.</summary>
/// <param name="Name">The trigger's name.</param>
/// <param name="Relation">The table it fired on, when the plan names it.</param>
/// <param name="TimeMs">Its time, in milliseconds.</param>
/// <param name="Calls">How many times it fired.</param>
internal sealed record PlanTrigger(string Name, string? Relation, double? TimeMs, double? Calls);

/// <summary>
/// One node of a plan as <c>EXPLAIN (FORMAT JSON)</c> printed it, read defensively: every property Postgres may leave out is
/// nullable, an unknown property is ignored, and a property of an unexpected type reads as absent.
/// </summary>
/// <remarks>
/// <para>
/// <b>What PostgreSQL 14 to 19 print, measured.</b> The PROBE packet (2026-10-26) explained nine shapes on every major, plain and
/// under <c>ANALYZE, BUFFERS</c>: the top-level keys are the same everywhere, and a node's keys differ only by 18's additions -
/// <c>Disabled</c>, <c>Index Searches</c>, <c>Storage</c> and <c>Maximum Storage</c> - which are read when present and drawn only
/// then. From 18 <c>Actual Rows</c> carries two decimals (<c>10.00</c>), so every number is read as a <see cref="double" />.
/// </para>
/// <para>
/// <b>Shares are computed, not read.</b> <see cref="ExclusiveCost" /> is the node's total cost less its children's - Postgres'
/// costs are cumulative - and <see cref="ExclusiveMs" /> the same over the wall time a node's per-loop average implies (a node
/// that ran in parallel workers is divided by the processes that shared it). Both are clamped at zero, so a nested loop's
/// repeated inner side is charged to the loop, as every plan viewer does.
/// </para>
/// </remarks>
internal sealed class PlanNode
{
    /// <summary>Its place in the walk: 0 for the root, then each node before its children.</summary>
    public required int Id { get; init; }

    /// <summary>How deep it is: 0 for the root.</summary>
    public required int Depth { get; init; }

    /// <summary>The node, as <c>Node Type</c> names it, or <c>(unknown)</c>.</summary>
    public required string NodeType { get; init; }

    /// <summary>The node as <c>EXPLAIN</c>'s text format heads it: <c>Parallel Seq Scan</c>, <c>Hash Left Join</c>, <c>HashAggregate</c>.</summary>
    public required string Label { get; init; }

    /// <summary>How it feeds its parent: <c>Outer</c>, <c>Inner</c>, <c>InitPlan</c>, <c>SubPlan</c>, <c>Member</c>...</summary>
    public string? ParentRelationship { get; init; }

    /// <summary>Its name as a subplan: <c>InitPlan 1</c>, <c>SubPlan 2</c>, <c>CTE x</c>.</summary>
    public string? SubplanName { get; init; }

    /// <summary>The schema of the relation it reads - printed only under <c>VERBOSE</c>.</summary>
    public string? Schema { get; init; }

    /// <summary>The relation it reads, unqualified.</summary>
    public string? RelationName { get; init; }

    /// <summary>The relation's alias in the statement.</summary>
    public string? Alias { get; init; }

    /// <summary>The index it uses.</summary>
    public string? IndexName { get; init; }

    /// <summary>The function a function scan calls.</summary>
    public string? FunctionName { get; init; }

    /// <summary>The common table expression a CTE scan reads.</summary>
    public string? CteName { get; init; }

    /// <summary>What a <c>ModifyTable</c> does: <c>Insert</c>, <c>Update</c>, <c>Delete</c>, <c>Merge</c>.</summary>
    public string? Operation { get; init; }

    /// <summary>An aggregate's or a set operation's strategy: <c>Plain</c>, <c>Sorted</c>, <c>Hashed</c>, <c>Mixed</c>.</summary>
    public string? Strategy { get; init; }

    /// <summary>A join's type: <c>Inner</c>, <c>Left</c>, <c>Semi</c>...</summary>
    public string? JoinType { get; init; }

    /// <summary>An aggregate's part in a parallel plan: <c>Partial</c>, <c>Finalize</c>, <c>Simple</c>.</summary>
    public string? PartialMode { get; init; }

    /// <summary>Whether it is a parallel-aware node.</summary>
    public bool ParallelAware { get; init; }

    /// <summary>The planner's cost before the first row.</summary>
    public double? StartupCost { get; init; }

    /// <summary>The planner's cost to the last row.</summary>
    public double? TotalCost { get; init; }

    /// <summary>The rows the planner expected, per loop.</summary>
    public double? PlanRows { get; init; }

    /// <summary>The width the planner expected of a row, in bytes.</summary>
    public double? PlanWidth { get; init; }

    /// <summary>The time to the first row, per loop, under <c>ANALYZE</c>.</summary>
    public double? ActualStartupMs { get; init; }

    /// <summary>The time to the last row, per loop, under <c>ANALYZE</c>.</summary>
    public double? ActualTotalMs { get; init; }

    /// <summary>The rows it returned, per loop, under <c>ANALYZE</c>.</summary>
    public double? ActualRows { get; init; }

    /// <summary>How many times it ran, under <c>ANALYZE</c>.</summary>
    public double? ActualLoops { get; init; }

    /// <summary>Its <c>Filter</c>.</summary>
    public string? Filter { get; init; }

    /// <summary>Its <c>Index Cond</c>.</summary>
    public string? IndexCondition { get; init; }

    /// <summary>Its <c>Recheck Cond</c>.</summary>
    public string? RecheckCondition { get; init; }

    /// <summary>Its <c>Hash Cond</c>.</summary>
    public string? HashCondition { get; init; }

    /// <summary>Its <c>Merge Cond</c>.</summary>
    public string? MergeCondition { get; init; }

    /// <summary>Its <c>Join Filter</c>.</summary>
    public string? JoinFilter { get; init; }

    /// <summary>Its <c>One-Time Filter</c>.</summary>
    public string? OneTimeFilter { get; init; }

    /// <summary>The rows its filter discarded, per loop.</summary>
    public double? RowsRemovedByFilter { get; init; }

    /// <summary>The rows a bitmap recheck discarded, per loop.</summary>
    public double? RowsRemovedByIndexRecheck { get; init; }

    /// <summary>The rows a join filter discarded, per loop.</summary>
    public double? RowsRemovedByJoinFilter { get; init; }

    /// <summary>Its sort key.</summary>
    public IReadOnlyList<string> SortKey { get; init; } = [];

    /// <summary>Its group key.</summary>
    public IReadOnlyList<string> GroupKey { get; init; } = [];

    /// <summary>A sort's method: <c>quicksort</c>, <c>top-N heapsort</c>, <c>external merge</c>...</summary>
    public string? SortMethod { get; init; }

    /// <summary>Where a sort kept its rows: <c>Memory</c> or <c>Disk</c>.</summary>
    public string? SortSpaceType { get; init; }

    /// <summary>How much a sort used, in kB.</summary>
    public double? SortSpaceUsedKb { get; init; }

    /// <summary>The most disk any one parallel worker's sort used, in kB, when one spilled.</summary>
    public double? WorkerSortDiskKb { get; init; }

    /// <summary>A hash's buckets.</summary>
    public double? HashBuckets { get; init; }

    /// <summary>A hash's batches: more than one is a hash that did not fit in <c>work_mem</c>.</summary>
    public double? HashBatches { get; init; }

    /// <summary>The batches the hash was planned with.</summary>
    public double? OriginalHashBatches { get; init; }

    /// <summary>A hash's or a sort's peak memory, in kB.</summary>
    public double? PeakMemoryKb { get; init; }

    /// <summary>A hash aggregate's batches.</summary>
    public double? HashAggBatches { get; init; }

    /// <summary>A hash aggregate's disk use, in kB.</summary>
    public double? DiskUsageKb { get; init; }

    /// <summary>The heap pages a bitmap heap scan kept exactly.</summary>
    public double? ExactHeapBlocks { get; init; }

    /// <summary>The heap pages a bitmap heap scan kept only as whole pages, every row on them rechecked.</summary>
    public double? LossyHeapBlocks { get; init; }

    /// <summary>The parallel workers a <c>Gather</c> planned.</summary>
    public double? WorkersPlanned { get; init; }

    /// <summary>The parallel workers a <c>Gather</c> was given, under <c>ANALYZE</c>.</summary>
    public double? WorkersLaunched { get; init; }

    /// <summary>PostgreSQL 18: whether a planner setting disabled this node's kind and it was used anyway.</summary>
    public bool? Disabled { get; init; }

    /// <summary>PostgreSQL 18: how many times an index scan descended its index.</summary>
    public double? IndexSearches { get; init; }

    /// <summary>PostgreSQL 18: where a materialising node kept its rows, <c>Memory</c> or <c>Disk</c>.</summary>
    public string? Storage { get; init; }

    /// <summary>PostgreSQL 18: the most it kept there, in kB.</summary>
    public double? MaximumStorageKb { get; init; }

    /// <summary>Its buffers, under <c>BUFFERS</c>.</summary>
    public PlanBuffers? Buffers { get; init; }

    /// <summary>The time it spent reading blocks, in milliseconds, under <c>track_io_timing</c>.</summary>
    public double? IoReadMs { get; init; }

    /// <summary>The time it spent writing blocks, in milliseconds, under <c>track_io_timing</c>.</summary>
    public double? IoWriteMs { get; init; }

    /// <summary>The nodes below it, outer first.</summary>
    public IReadOnlyList<PlanNode> Children { get; init; } = [];

    /// <summary>Its own cost: its total less its children's, never below zero.</summary>
    public double? ExclusiveCost { get; internal set; }

    /// <summary>Its share of the plan's cost.</summary>
    public double? CostShare { get; internal set; }

    /// <summary>The wall time it and the nodes below it took, in milliseconds, under <c>ANALYZE</c>.</summary>
    public double? InclusiveMs { get; internal set; }

    /// <summary>Its own wall time: <see cref="InclusiveMs" /> less its children's, never below zero.</summary>
    public double? ExclusiveMs { get; internal set; }

    /// <summary>Its share of the plan's time, under <c>ANALYZE</c>.</summary>
    public double? TimeShare { get; internal set; }

    /// <summary>Whether it was analyzed and never ran (<c>Actual Loops</c> zero).</summary>
    public bool NeverExecuted => ActualLoops is 0;

    /// <summary>What it reads, as <c>EXPLAIN</c>'s text format says it: <c>using t_pkey on public.t o</c>, <c>on generate_series g</c>.</summary>
    public string? Target
    {
        get
        {
            string? relation = RelationName is { } name ? (Schema is { } schema ? schema + "." + name : name) : null;
            string? target = relation ?? FunctionName ?? CteName;
            string? alias = Alias is { } a && !string.Equals(a, RelationName ?? FunctionName ?? CteName, StringComparison.Ordinal) ? a : null;

            string? on = target is null ? alias : alias is null ? target : target + " " + alias;
            string? index = IndexName;

            return (index, on) switch
            {
                (null, null) => null,
                (null, _) => "on " + on,
                (_, null) => "on " + index,
                _ => "using " + index + " on " + on,
            };
        }
    }

    /// <summary>The node in a sentence: its label and what it reads.</summary>
    public string Description => Target is { } target ? Label + " " + target : Label;

    /// <summary>Every node from this one down, this one first.</summary>
    public IEnumerable<PlanNode> DescendantsAndSelf()
    {
        Stack<PlanNode> pending = new();
        pending.Push(this);

        while (pending.Count > 0)
        {
            PlanNode node = pending.Pop();
            yield return node;

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                pending.Push(node.Children[i]);
            }
        }
    }
}

/// <summary>A plan as <c>EXPLAIN (FORMAT JSON)</c> printed it: its root and what Postgres said about the whole.</summary>
/// <param name="Root">The root node.</param>
/// <param name="NodeCount">How many nodes were read.</param>
/// <param name="Truncated">Whether the read stopped at <see cref="QueryPlanReader.MaxNodes" /> or <see cref="QueryPlanReader.MaxDepth" />.</param>
/// <param name="Statements">How many statements Postgres planned: more than one under a rewrite rule, of which the first is read.</param>
internal sealed record QueryPlan(PlanNode Root, int NodeCount, bool Truncated, int Statements)
{
    /// <summary>The planning time, in milliseconds, when Postgres printed it (<c>ANALYZE</c>, or <c>SUMMARY</c>).</summary>
    public double? PlanningMs { get; init; }

    /// <summary>The execution time, in milliseconds, under <c>ANALYZE</c>.</summary>
    public double? ExecutionMs { get; init; }

    /// <summary>The triggers that fired, under <c>ANALYZE</c>.</summary>
    public IReadOnlyList<PlanTrigger> Triggers { get; init; } = [];

    /// <summary>How many functions JIT compiled, when it ran.</summary>
    public double? JitFunctions { get; init; }

    /// <summary>The time JIT took, in milliseconds, when it ran under <c>ANALYZE</c>.</summary>
    public double? JitMs { get; init; }

    /// <summary>Whether the plan was analyzed: its root carries <c>Actual Loops</c>.</summary>
    public bool Analyzed => Root.ActualLoops is not null;

    /// <summary>Whether it was analyzed with its timing on, so time shares are there to draw.</summary>
    public bool Timed => Root.TimeShare is not null;

    /// <summary>Whether any node carries buffer counters.</summary>
    public bool HasBuffers => Nodes.Any(static x => x.Buffers is not null);

    /// <summary>Every node, the root first, each before its children.</summary>
    public IEnumerable<PlanNode> Nodes => Root.DescendantsAndSelf();
}

/// <summary>What reading a plan came to: the plan, or why there is none.</summary>
/// <param name="Plan">The plan, or <see langword="null" />.</param>
/// <param name="Failure">Why there is no plan, or <see langword="null" />.</param>
internal sealed record QueryPlanRead(QueryPlan? Plan, string? Failure)
{
    /// <summary>A read that found a plan.</summary>
    public static QueryPlanRead Of(QueryPlan plan) => new(plan, null);

    /// <summary>A read that found none.</summary>
    public static QueryPlanRead Failed(string failure) => new(null, failure);
}
