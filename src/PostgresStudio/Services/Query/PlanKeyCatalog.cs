namespace PostgresStudio.Services.Query;

/// <summary>The groups the node detail lists the keys Postgres printed under.</summary>
internal enum PlanKeyGroup
{
    /// <summary>What the node is, what it reads and the conditions it applies.</summary>
    Node,

    /// <summary>What the planner expected.</summary>
    Estimate,

    /// <summary>What ran, under <c>ANALYZE</c>.</summary>
    Actual,

    /// <summary>Blocks touched, under <c>BUFFERS</c>.</summary>
    Buffers,

    /// <summary>Time spent reading and writing blocks, and write-ahead log produced.</summary>
    Io,

    /// <summary>Parallel workers.</summary>
    Workers,

    /// <summary>Sorts, hashes, aggregates and memoize caches: what they used, and where.</summary>
    SortHashMemoize,

    /// <summary>Just-in-time compilation: the statement's, not a node's.</summary>
    Jit,

    /// <summary>A key the catalog has no sentence for.</summary>
    Other,
}

/// <summary>One key's group and its one-line sentence.</summary>
/// <param name="Group">Where the node detail lists it.</param>
/// <param name="Text">What it means, in a sentence.</param>
internal sealed record PlanKeyMeaning(PlanKeyGroup Group, string Text);

/// <summary>
/// What each key <c>EXPLAIN (FORMAT JSON)</c> prints means, in one sentence, and which group of the node detail it is listed
/// under (X3, issue #28). One static dictionary: the sentences are the studio's own words - never Postgres' - and
/// <c>PlanKeyCatalogTests</c> holds it to explain every key <see cref="QueryPlanReader" /> reads (<see cref="PlanKeys.Known" />).
/// </summary>
/// <remarks>
/// A key is looked up by its leaf name - a nested key the reader flattens, <c>Full-sort Groups › Group Count</c>, is explained
/// by <c>Group Count</c> - and a key the catalog does not know is listed under <see cref="PlanKeyGroup.Other" /> with no sentence,
/// never guessed at.
/// </remarks>
internal static class PlanKeyCatalog
{
    private static readonly Dictionary<string, PlanKeyMeaning> Meanings = Build();

    /// <summary>The group a key is listed under, and its sentence, or <see langword="null" /> for a key the catalog does not know.</summary>
    /// <param name="key">The key as the node printed it, or flattened: <c>Workers[0] › Actual Rows</c>.</param>
    public static PlanKeyMeaning? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Meanings.TryGetValue(key, out PlanKeyMeaning? exact))
        {
            return exact;
        }

        int separator = key.LastIndexOf(" › ", StringComparison.Ordinal);

        return separator >= 0 && Meanings.TryGetValue(key[(separator + 3)..], out PlanKeyMeaning? leaf) ? leaf : null;
    }

    /// <summary>Every key the catalog explains.</summary>
    public static IReadOnlyCollection<string> Keys => Meanings.Keys;

    /// <summary>A group's heading.</summary>
    public static string Title(PlanKeyGroup group) => group switch
    {
        PlanKeyGroup.Node => "The node",
        PlanKeyGroup.Estimate => "Estimate",
        PlanKeyGroup.Actual => "Actual",
        PlanKeyGroup.Buffers => "Buffers",
        PlanKeyGroup.Io => "I/O and WAL",
        PlanKeyGroup.Workers => "Workers",
        PlanKeyGroup.SortHashMemoize => "Sort, hash and memoize",
        PlanKeyGroup.Jit => "JIT",
        _ => "Other keys",
    };

    private static Dictionary<string, PlanKeyMeaning> Build()
    {
        Dictionary<string, PlanKeyMeaning> meanings = new(StringComparer.Ordinal);

        void Add(PlanKeyGroup group, string key, string text) => meanings.Add(key, new PlanKeyMeaning(group, text));

        // ---- The node ------------------------------------------------------------------------------------------------
        Add(PlanKeyGroup.Node, PlanKeys.NodeType, "The kind of step this is: a scan, a join, a sort, an aggregate and so on.");
        Add(PlanKeyGroup.Node, PlanKeys.ParentRelationship, "How this node feeds the one above it: as its outer or inner side, as an InitPlan or SubPlan, or as a member of an append.");
        Add(PlanKeyGroup.Node, PlanKeys.SubplanName, "The name Postgres gave this subplan, which the node above refers to.");
        Add(PlanKeyGroup.Node, PlanKeys.Schema, "The schema of the relation this node reads, printed under VERBOSE.");
        Add(PlanKeyGroup.Node, PlanKeys.RelationName, "The table, view or other relation this node reads.");
        Add(PlanKeyGroup.Node, PlanKeys.Alias, "The name the statement gives this relation.");
        Add(PlanKeyGroup.Node, PlanKeys.IndexName, "The index this node searches.");
        Add(PlanKeyGroup.Node, PlanKeys.FunctionName, "The set-returning function a function scan calls.");
        Add(PlanKeyGroup.Node, PlanKeys.CteName, "The common table expression a CTE scan reads.");
        Add(PlanKeyGroup.Node, PlanKeys.Operation, "What a ModifyTable does: Insert, Update, Delete or Merge.");
        Add(PlanKeyGroup.Node, PlanKeys.Strategy, "How an aggregate or a set operation works: plain, sorted, hashed or mixed.");
        Add(PlanKeyGroup.Node, PlanKeys.JoinType, "The kind of join: inner, left, right, full, semi or anti.");
        Add(PlanKeyGroup.Node, PlanKeys.PartialMode, "A parallel aggregate's part: Partial in each worker, Finalize in the leader.");
        Add(PlanKeyGroup.Node, PlanKeys.ParallelAware, "Whether the node itself shares its work between parallel processes.");
        Add(PlanKeyGroup.Node, PlanKeys.AsyncCapable, "Whether a foreign scan can run asynchronously under an Append.");
        Add(PlanKeyGroup.Node, PlanKeys.Plans, "The nodes below this one.");
        Add(PlanKeyGroup.Node, PlanKeys.Filter, "A condition applied to each row after it was read; rows that fail it are discarded.");
        Add(PlanKeyGroup.Node, PlanKeys.IndexCond, "The condition the index itself answers, so only matching entries are read.");
        Add(PlanKeyGroup.Node, PlanKeys.RecheckCond, "The condition a bitmap scan tests again on each row it fetches, because a bitmap can be lossy.");
        Add(PlanKeyGroup.Node, PlanKeys.HashCond, "The equality a hash join builds its hash table on and probes with.");
        Add(PlanKeyGroup.Node, PlanKeys.MergeCond, "The equality a merge join walks both sorted inputs by.");
        Add(PlanKeyGroup.Node, PlanKeys.JoinFilter, "A condition applied to each joined pair of rows, after the join's own match.");
        Add(PlanKeyGroup.Node, PlanKeys.OneTimeFilter, "A condition that does not depend on any row, tested once: when it is false the node returns nothing.");
        Add(PlanKeyGroup.Node, PlanKeys.SortKey, "The expressions the rows are sorted by.");
        Add(PlanKeyGroup.Node, PlanKeys.GroupKey, "The expressions the rows are grouped by.");
        Add(PlanKeyGroup.Node, PlanKeys.Disabled, "PostgreSQL 18: a planner setting (enable_*) disabled this kind of node, and it was used anyway.");
        Add(PlanKeyGroup.Node, "Output", "The columns and expressions this node hands up, printed under VERBOSE.");
        Add(PlanKeyGroup.Node, "Scan Direction", "Whether an index was read forward or backward.");
        Add(PlanKeyGroup.Node, "Order By", "An ordering an index scan returns rows in, such as a nearest-neighbour distance.");
        Add(PlanKeyGroup.Node, "TID Cond", "The row locations a TID scan reads directly.");
        Add(PlanKeyGroup.Node, "Inner Unique", "Whether each outer row can match at most one inner row, so the join stops at the first match.");
        Add(PlanKeyGroup.Node, "Function Call", "The function call a function scan evaluates.");
        Add(PlanKeyGroup.Node, "Target Tables", "The tables a write modifies.");
        Add(PlanKeyGroup.Node, "Subplans Removed", "How many partitions or subplans run-time pruning dropped before the node ran.");
        Add(PlanKeyGroup.Node, "Planned Partitions", "PostgreSQL 18: how many partitions planning kept after pruning, before run-time pruning dropped any.");
        Add(PlanKeyGroup.Node, "Query Identifier", "The statement's query id, the number pg_stat_statements groups it under.");

        // ---- Estimate ------------------------------------------------------------------------------------------------
        Add(PlanKeyGroup.Estimate, PlanKeys.StartupCost, "The planner's estimate of the cost before the node can return its first row, in its arbitrary cost units.");
        Add(PlanKeyGroup.Estimate, PlanKeys.TotalCost, "The planner's estimate of the cost to return every row, this node and everything below it, in its arbitrary cost units.");
        Add(PlanKeyGroup.Estimate, PlanKeys.PlanRows, "The rows the planner expected this node to return in one execution.");
        Add(PlanKeyGroup.Estimate, PlanKeys.PlanWidth, "The average width of a row the planner expected, in bytes.");

        // ---- Actual --------------------------------------------------------------------------------------------------
        Add(PlanKeyGroup.Actual, PlanKeys.ActualStartupTime, "The time to the first row, in milliseconds, averaged over this node's executions.");
        Add(PlanKeyGroup.Actual, PlanKeys.ActualTotalTime, "The time to the last row, in milliseconds, averaged over this node's executions: multiply by the loops for its total.");
        Add(PlanKeyGroup.Actual, PlanKeys.ActualRows, "The rows this node returned, averaged over its executions.");
        Add(PlanKeyGroup.Actual, PlanKeys.ActualLoops, "How many times the node ran: the inner side of a nested loop runs once for each outer row.");
        Add(PlanKeyGroup.Actual, PlanKeys.RowsRemovedByFilter, "Rows the node read and its filter discarded, averaged over its executions: a large number beside a small result is work done for nothing.");
        Add(PlanKeyGroup.Actual, PlanKeys.RowsRemovedByIndexRecheck, "Rows a bitmap scan fetched and its recheck condition discarded, because the bitmap was lossy.");
        Add(PlanKeyGroup.Actual, PlanKeys.RowsRemovedByJoinFilter, "Joined pairs the join filter discarded, averaged over the join's executions.");
        Add(PlanKeyGroup.Actual, PlanKeys.HeapFetches, "How many times an index-only scan had to visit the table because the row's page was not marked all-visible: vacuum sets those marks.");
        Add(PlanKeyGroup.Actual, PlanKeys.ExactHeapBlocks, "Table pages a bitmap heap scan remembered exactly, row by row.");
        Add(PlanKeyGroup.Actual, PlanKeys.LossyHeapBlocks, "Table pages a bitmap heap scan remembered only as whole pages because the bitmap outgrew work_mem, so every row on them was tested again.");
        Add(PlanKeyGroup.Actual, PlanKeys.IndexSearches, "PostgreSQL 18: how many times the scan descended the index.");
        Add(PlanKeyGroup.Actual, "Tuples Inserted", "Rows an INSERT ... ON CONFLICT actually inserted.");
        Add(PlanKeyGroup.Actual, "Conflicting Tuples", "Rows an INSERT ... ON CONFLICT found already there.");

        // ---- Buffers -------------------------------------------------------------------------------------------------
        Add(PlanKeyGroup.Buffers, PlanKeys.SharedHitBlocks, "Blocks of shared tables and indexes found in Postgres' buffer cache, this node and everything below it.");
        Add(PlanKeyGroup.Buffers, PlanKeys.SharedReadBlocks, "Blocks of shared tables and indexes read from outside the buffer cache: the operating system's cache or the disk.");
        Add(PlanKeyGroup.Buffers, PlanKeys.SharedDirtiedBlocks, "Blocks the node changed for the first time since they were last written out.");
        Add(PlanKeyGroup.Buffers, PlanKeys.SharedWrittenBlocks, "Blocks the node had to write out itself to make room in the cache.");
        Add(PlanKeyGroup.Buffers, PlanKeys.LocalHitBlocks, "Blocks of temporary tables found in the session's local buffers.");
        Add(PlanKeyGroup.Buffers, PlanKeys.LocalReadBlocks, "Blocks of temporary tables read from outside the local buffers.");
        Add(PlanKeyGroup.Buffers, PlanKeys.LocalDirtiedBlocks, "Blocks of temporary tables the node changed.");
        Add(PlanKeyGroup.Buffers, PlanKeys.LocalWrittenBlocks, "Blocks of temporary tables the node wrote out.");
        Add(PlanKeyGroup.Buffers, PlanKeys.TempReadBlocks, "Blocks read back from temporary files: a sort, hash or materialisation that did not fit in memory.");
        Add(PlanKeyGroup.Buffers, PlanKeys.TempWrittenBlocks, "Blocks written to temporary files: a sort, hash or materialisation that did not fit in memory.");

        // ---- I/O and WAL ---------------------------------------------------------------------------------------------
        Add(PlanKeyGroup.Io, PlanKeys.IoReadTime, "Milliseconds spent reading blocks, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.SharedIoReadTime, "Milliseconds spent reading shared blocks, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.LocalIoReadTime, "Milliseconds spent reading local blocks, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.TempIoReadTime, "Milliseconds spent reading temporary files, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.IoWriteTime, "Milliseconds spent writing blocks, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.SharedIoWriteTime, "Milliseconds spent writing shared blocks, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.LocalIoWriteTime, "Milliseconds spent writing local blocks, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.TempIoWriteTime, "Milliseconds spent writing temporary files, measured only when track_io_timing is on.");
        Add(PlanKeyGroup.Io, PlanKeys.WalRecords, "Write-ahead-log records this node produced, under the WAL option.");
        Add(PlanKeyGroup.Io, PlanKeys.WalFpi, "Full-page images this node wrote to the log: the first change to a page after a checkpoint logs the whole page.");
        Add(PlanKeyGroup.Io, PlanKeys.WalBytes, "Bytes of write-ahead log this node produced.");

        // ---- Workers -------------------------------------------------------------------------------------------------
        Add(PlanKeyGroup.Workers, PlanKeys.WorkersPlanned, "The parallel workers the planner wanted for this Gather.");
        Add(PlanKeyGroup.Workers, PlanKeys.WorkersLaunched, "The parallel workers the Gather actually got; fewer than planned means the server had none to spare.");
        Add(PlanKeyGroup.Workers, PlanKeys.Workers, "Each parallel worker's own figures for this node.");
        Add(PlanKeyGroup.Workers, "Worker Number", "Which parallel worker these figures are of; the leader is not listed.");
        Add(PlanKeyGroup.Workers, "Single Copy", "Whether the Gather runs its child in one worker only, so the leader does not.");

        // ---- Sort, hash and memoize ----------------------------------------------------------------------------------
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.SortMethod, "How the sort ran: quicksort and top-N heapsort in memory, external merge or external sort on disk.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.SortSpaceType, "Whether the sort kept its rows in memory or on disk.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.SortSpaceUsed, "How much memory or disk the sort used, in kilobytes.");
        Add(PlanKeyGroup.SortHashMemoize, "Presorted Key", "The leading sort columns an incremental sort already received in order.");
        Add(PlanKeyGroup.SortHashMemoize, "Full-sort Groups", "Incremental sort: the groups it sorted completely.");
        Add(PlanKeyGroup.SortHashMemoize, "Pre-sorted Groups", "Incremental sort: the groups it sorted after the first rows were already in order.");
        Add(PlanKeyGroup.SortHashMemoize, "Group Count", "How many groups an incremental sort sorted.");
        Add(PlanKeyGroup.SortHashMemoize, "Sort Methods Used", "The sort methods an incremental sort used across its groups.");
        Add(PlanKeyGroup.SortHashMemoize, "Sort Space Memory", "An incremental sort's memory use.");
        Add(PlanKeyGroup.SortHashMemoize, "Sort Space Disk", "An incremental sort's disk use.");
        Add(PlanKeyGroup.SortHashMemoize, "Average Sort Space Used", "The average space a group's sort used, in kilobytes.");
        Add(PlanKeyGroup.SortHashMemoize, "Peak Sort Space Used", "The most space any group's sort used, in kilobytes.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.HashBuckets, "How many buckets the hash table ended with.");
        Add(PlanKeyGroup.SortHashMemoize, "Original Hash Buckets", "The buckets the hash table was planned with.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.HashBatches, "How many batches the hash was split into: more than one means it did not fit in work_mem and spilled to temporary files.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.OriginalHashBatches, "The batches the hash was planned with; the executor doubles them when the table outgrows work_mem.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.PeakMemoryUsage, "The most memory the hash table or the memoize cache used, in kilobytes.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.HashAggBatches, "How many batches a hash aggregate spilled to: more than one means its table did not fit in memory.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.DiskUsage, "How much temporary-file space a hash aggregate used, in kilobytes.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.Storage, "PostgreSQL 18: where a materialising node kept its rows, in memory or on disk.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.MaximumStorage, "PostgreSQL 18: the most a materialising node kept at once, in kilobytes.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.CacheHits, "Memoize: how many lookups found the parameters in its cache and skipped the inner side.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.CacheMisses, "Memoize: how many lookups had to run the inner side and cache its result.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.CacheEvictions, "Memoize: how many cached results were thrown away to make room, because the cache was full.");
        Add(PlanKeyGroup.SortHashMemoize, PlanKeys.CacheOverflows, "Memoize: how many times one lookup's result alone was larger than the whole cache.");
        Add(PlanKeyGroup.SortHashMemoize, "Cache Key", "The parameters a memoize node caches the inner side's rows under.");
        Add(PlanKeyGroup.SortHashMemoize, "Cache Mode", "Whether a memoize node caches by the parameters (binary) or by their equality (logical).");

        // ---- JIT -----------------------------------------------------------------------------------------------------
        Add(PlanKeyGroup.Jit, PlanKeys.Jit, "Just-in-time compilation of this statement's expressions and tuple deforming into machine code.");
        Add(PlanKeyGroup.Jit, PlanKeys.Functions, "How many functions JIT compiled for the statement.");
        Add(PlanKeyGroup.Jit, PlanKeys.Timing, "How long each stage of JIT took, in milliseconds.");
        Add(PlanKeyGroup.Jit, PlanKeys.Total, "The time JIT took in all, in milliseconds: it is spent before the statement runs, and counted in the execution time.");
        Add(PlanKeyGroup.Jit, "Options", "Which JIT stages were on: inlining, optimization, expressions and deforming.");
        Add(PlanKeyGroup.Jit, "Inlining", "Whether JIT inlined the bodies of functions the statement calls.");
        Add(PlanKeyGroup.Jit, "Optimization", "Whether JIT ran its expensive optimization passes.");
        Add(PlanKeyGroup.Jit, "Expressions", "Whether JIT compiled expression evaluation.");
        Add(PlanKeyGroup.Jit, "Deforming", "Whether JIT compiled the unpacking of rows into columns.");
        Add(PlanKeyGroup.Jit, "Generation", "Milliseconds JIT spent generating code.");
        Add(PlanKeyGroup.Jit, "Emission", "Milliseconds JIT spent turning code into machine instructions.");
        Add(PlanKeyGroup.Jit, "Deform", "Milliseconds JIT spent generating code to deform rows.");

        // ---- The statement, around the plan --------------------------------------------------------------------------
        Add(PlanKeyGroup.Actual, PlanKeys.Plan, "The statement's plan: the root node, with the nodes below it.");
        Add(PlanKeyGroup.Actual, PlanKeys.PlanningTime, "The time Postgres spent planning the statement, in milliseconds.");
        Add(PlanKeyGroup.Actual, PlanKeys.ExecutionTime, "The time the statement took to run, in milliseconds, JIT and triggers included.");
        Add(PlanKeyGroup.Actual, PlanKeys.Triggers, "The triggers that fired for a write, with their time and number of calls.");
        Add(PlanKeyGroup.Actual, PlanKeys.TriggerName, "The name of a trigger that fired.");
        Add(PlanKeyGroup.Actual, PlanKeys.Relation, "The table a trigger fired on.");
        Add(PlanKeyGroup.Actual, PlanKeys.Time, "The milliseconds a trigger, or the serialization step, took.");
        Add(PlanKeyGroup.Actual, PlanKeys.Calls, "How many times a trigger fired.");
        Add(PlanKeyGroup.Estimate, PlanKeys.Settings, "The planner settings that differ from the server's defaults, printed under the SETTINGS option.");
        Add(PlanKeyGroup.Estimate, PlanKeys.Planning, "What planning used, printed under the BUFFERS and MEMORY options.");
        Add(PlanKeyGroup.Estimate, PlanKeys.MemoryUsed, "PostgreSQL 17: the memory planning used, in bytes.");
        Add(PlanKeyGroup.Actual, PlanKeys.Serialization, "PostgreSQL 17: the cost of turning the result into what a client would receive, under the SERIALIZE option.");
        Add(PlanKeyGroup.Actual, PlanKeys.OutputVolume, "PostgreSQL 17: how much data serializing the result produced, in kilobytes.");

        return meanings;
    }
}
