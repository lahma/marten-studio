using System.Reflection;

namespace PostgresStudio.Services.Query;

/// <summary>
/// Every key of <c>EXPLAIN (FORMAT JSON)</c> that <see cref="QueryPlanReader" /> reads by name, spelled once - the reader uses
/// these constants and nothing else, so <see cref="Known" /> is the reader's vocabulary by construction, and
/// <see cref="PlanKeyCatalog" /> is held to explain every one of them (<c>PlanKeyCatalogTests</c>).
/// </summary>
/// <remarks>
/// A key the reader does not model is still shown in the node detail, because the unmasked reader keeps every scalar property a
/// node printed (<see cref="PlanNode.Properties" />); the catalog explains those it knows, and says nothing of the rest.
/// </remarks>
internal static class PlanKeys
{
    // ---- A node: what it is and what it reads --------------------------------------------------------------------------

    public const string NodeType = "Node Type";
    public const string ParentRelationship = "Parent Relationship";
    public const string SubplanName = "Subplan Name";
    public const string Schema = "Schema";
    public const string RelationName = "Relation Name";
    public const string Alias = "Alias";
    public const string IndexName = "Index Name";
    public const string FunctionName = "Function Name";
    public const string CteName = "CTE Name";
    public const string Operation = "Operation";
    public const string Strategy = "Strategy";
    public const string JoinType = "Join Type";
    public const string PartialMode = "Partial Mode";
    public const string ParallelAware = "Parallel Aware";
    public const string AsyncCapable = "Async Capable";
    public const string Plans = "Plans";
    public const string Filter = "Filter";
    public const string IndexCond = "Index Cond";
    public const string RecheckCond = "Recheck Cond";
    public const string HashCond = "Hash Cond";
    public const string MergeCond = "Merge Cond";
    public const string JoinFilter = "Join Filter";
    public const string OneTimeFilter = "One-Time Filter";
    public const string SortKey = "Sort Key";
    public const string GroupKey = "Group Key";
    public const string Disabled = "Disabled";

    // ---- The planner's estimate ----------------------------------------------------------------------------------------

    public const string StartupCost = "Startup Cost";
    public const string TotalCost = "Total Cost";
    public const string PlanRows = "Plan Rows";
    public const string PlanWidth = "Plan Width";

    // ---- What ran (ANALYZE) --------------------------------------------------------------------------------------------

    public const string ActualStartupTime = "Actual Startup Time";
    public const string ActualTotalTime = "Actual Total Time";
    public const string ActualRows = "Actual Rows";
    public const string ActualLoops = "Actual Loops";
    public const string RowsRemovedByFilter = "Rows Removed by Filter";
    public const string RowsRemovedByIndexRecheck = "Rows Removed by Index Recheck";
    public const string RowsRemovedByJoinFilter = "Rows Removed by Join Filter";
    public const string HeapFetches = "Heap Fetches";
    public const string ExactHeapBlocks = "Exact Heap Blocks";
    public const string LossyHeapBlocks = "Lossy Heap Blocks";
    public const string IndexSearches = "Index Searches";

    // ---- Sorts, hashes, aggregates and memoize -------------------------------------------------------------------------

    public const string SortMethod = "Sort Method";
    public const string SortSpaceType = "Sort Space Type";
    public const string SortSpaceUsed = "Sort Space Used";
    public const string HashBuckets = "Hash Buckets";
    public const string HashBatches = "Hash Batches";
    public const string OriginalHashBatches = "Original Hash Batches";
    public const string PeakMemoryUsage = "Peak Memory Usage";
    public const string HashAggBatches = "HashAgg Batches";
    public const string DiskUsage = "Disk Usage";
    public const string Storage = "Storage";
    public const string MaximumStorage = "Maximum Storage";
    public const string CacheHits = "Cache Hits";
    public const string CacheMisses = "Cache Misses";
    public const string CacheEvictions = "Cache Evictions";
    public const string CacheOverflows = "Cache Overflows";

    // ---- Parallel workers ----------------------------------------------------------------------------------------------

    public const string WorkersPlanned = "Workers Planned";
    public const string WorkersLaunched = "Workers Launched";
    public const string Workers = "Workers";

    // ---- Buffers and I/O (BUFFERS) -------------------------------------------------------------------------------------

    public const string SharedHitBlocks = "Shared Hit Blocks";
    public const string SharedReadBlocks = "Shared Read Blocks";
    public const string SharedDirtiedBlocks = "Shared Dirtied Blocks";
    public const string SharedWrittenBlocks = "Shared Written Blocks";
    public const string LocalHitBlocks = "Local Hit Blocks";
    public const string LocalReadBlocks = "Local Read Blocks";
    public const string LocalDirtiedBlocks = "Local Dirtied Blocks";
    public const string LocalWrittenBlocks = "Local Written Blocks";
    public const string TempReadBlocks = "Temp Read Blocks";
    public const string TempWrittenBlocks = "Temp Written Blocks";
    public const string IoReadTime = "I/O Read Time";
    public const string SharedIoReadTime = "Shared I/O Read Time";
    public const string LocalIoReadTime = "Local I/O Read Time";
    public const string TempIoReadTime = "Temp I/O Read Time";
    public const string IoWriteTime = "I/O Write Time";
    public const string SharedIoWriteTime = "Shared I/O Write Time";
    public const string LocalIoWriteTime = "Local I/O Write Time";
    public const string TempIoWriteTime = "Temp I/O Write Time";

    // ---- Write-ahead log (WAL) -----------------------------------------------------------------------------------------

    public const string WalRecords = "WAL Records";
    public const string WalFpi = "WAL FPI";
    public const string WalBytes = "WAL Bytes";

    // ---- The statement, around the plan --------------------------------------------------------------------------------

    public const string Plan = "Plan";
    public const string PlanningTime = "Planning Time";
    public const string ExecutionTime = "Execution Time";
    public const string Triggers = "Triggers";
    public const string TriggerName = "Trigger Name";
    public const string Relation = "Relation";
    public const string Time = "Time";
    public const string Calls = "Calls";
    public const string Jit = "JIT";
    public const string Functions = "Functions";
    public const string Timing = "Timing";
    public const string Total = "Total";
    public const string Settings = "Settings";
    public const string Planning = "Planning";
    public const string MemoryUsed = "Memory Used";
    public const string Serialization = "Serialization";
    public const string OutputVolume = "Output Volume";

    private static readonly Lazy<IReadOnlyList<string>> Keys = new(static () =>
        [.. typeof(PlanKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(static x => (string) x.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)]);

    /// <summary>Every key the reader reads by name, each once.</summary>
    public static IReadOnlyList<string> Known => Keys.Value;
}
