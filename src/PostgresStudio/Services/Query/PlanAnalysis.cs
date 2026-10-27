using System.Globalization;

using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Query;

/// <summary>What a heat-coloured plan colours each node by (X3, issue #28).</summary>
internal enum PlanHeat
{
    /// <summary>Nothing: the share bars keep their one colour.</summary>
    None,

    /// <summary>The node's own time, or its own cost when the plan was not timed.</summary>
    SelfTime,

    /// <summary>How far the planner's row estimate was off.</summary>
    Misestimate,

    /// <summary>The blocks the node itself read from outside the cache.</summary>
    BuffersRead,

    /// <summary>The temporary-file blocks the node itself read and wrote.</summary>
    TempIo,
}

/// <summary>One node's heat: the value, how far it is up the plan's own scale, the step of the colour scale, and the value as text.</summary>
/// <param name="Value">The node's number.</param>
/// <param name="Fraction">Where it is between none and the plan's hottest node, from 0 to 1.</param>
/// <param name="Level">The step of the colour scale: 0 for none, 1 to 4 from cool to hot.</param>
/// <param name="Text">The value in words, so colour is never the only signal.</param>
internal sealed record PlanHeatCell(double Value, double Fraction, int Level, string Text);

/// <summary>
/// What a node itself did, less what the nodes below it did: Postgres prints buffer counters and I/O times cumulatively, as it
/// prints a cost, so a node's own are its total less its children's, never below zero (the arithmetic of
/// <see cref="PlanNode.ExclusiveCost" />). <see langword="null" /> where the node printed no buffers.
/// </summary>
/// <param name="SharedHit">Shared blocks found in the cache.</param>
/// <param name="SharedRead">Shared blocks read from outside the cache.</param>
/// <param name="LocalRead">Local blocks read.</param>
/// <param name="TempRead">Temporary-file blocks read.</param>
/// <param name="TempWritten">Temporary-file blocks written.</param>
/// <param name="IoMs">Milliseconds spent reading and writing blocks, or <see langword="null" /> when the plan was not timed for I/O.</param>
internal sealed record PlanNodeOwn(long SharedHit, long SharedRead, long LocalRead, long TempRead, long TempWritten, double? IoMs)
{
    /// <summary>A node's own figures, or <see langword="null" /> when it printed no buffers.</summary>
    public static PlanNodeOwn? Of(PlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Buffers is not { } buffers)
        {
            return null;
        }

        long Own(Func<PlanBuffers, long> pick) =>
            Math.Max(0, pick(buffers) - node.Children.Sum(x => x.Buffers is { } below ? pick(below) : 0));

        double? io = node.IoReadMs is null && node.IoWriteMs is null
            ? null
            : Math.Max(0, (node.IoReadMs ?? 0) + (node.IoWriteMs ?? 0) - node.Children.Sum(static x => (x.IoReadMs ?? 0) + (x.IoWriteMs ?? 0)));

        return new PlanNodeOwn(
            Own(static x => x.SharedHit),
            Own(static x => x.SharedRead),
            Own(static x => x.LocalRead),
            Own(static x => x.TempRead),
            Own(static x => x.TempWritten),
            io);
    }
}

/// <summary>
/// The pure arithmetic behind the plan analyzer's views: the critical path, the heat of each node, and the plan's summary. No
/// markup, no database - the page draws what these say and the tests hold them to the plans they are given.
/// </summary>
internal static class PlanAnalysis
{
    /// <summary>The blocks Postgres counts a page as: 8 kB, unless the server was built otherwise (rare, and never reported).</summary>
    public const int BlockBytes = 8192;

    /// <summary>The query-string and select value for each heat.</summary>
    public static string Key(PlanHeat heat) => heat switch
    {
        PlanHeat.SelfTime => "self-time",
        PlanHeat.Misestimate => "misestimate",
        PlanHeat.BuffersRead => "buffers-read",
        PlanHeat.TempIo => "temp-io",
        _ => "none",
    };

    /// <summary>The heat a select value names; <see cref="PlanHeat.None" /> for anything else.</summary>
    public static PlanHeat ParseHeat(string? key) => key switch
    {
        "self-time" => PlanHeat.SelfTime,
        "misestimate" => PlanHeat.Misestimate,
        "buffers-read" => PlanHeat.BuffersRead,
        "temp-io" => PlanHeat.TempIo,
        _ => PlanHeat.None,
    };

    /// <summary>Every heat, in the order the select lists them.</summary>
    public static IReadOnlyList<PlanHeat> Heats { get; } =
        [PlanHeat.None, PlanHeat.SelfTime, PlanHeat.Misestimate, PlanHeat.BuffersRead, PlanHeat.TempIo];

    /// <summary>A heat's name for the select.</summary>
    public static string Label(PlanHeat heat, QueryPlan? plan = null) => heat switch
    {
        PlanHeat.SelfTime => plan is { Timed: false } ? "Self cost" : "Self time",
        PlanHeat.Misestimate => "Row misestimate",
        PlanHeat.BuffersRead => "Buffers read",
        PlanHeat.TempIo => "Temp I/O",
        _ => "None",
    };

    /// <summary>
    /// The nodes that carry most of the time: from the root, the child with the largest inclusive time - or the largest total
    /// cost, when the plan was not timed - and then that node's, until a node has no child that did anything.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The node ids on the path, root first in walk order.</returns>
    public static IReadOnlyList<int> CriticalPath(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<int> path = [];
        bool timed = plan.Timed;
        PlanNode? node = plan.Root;

        while (node is not null)
        {
            path.Add(node.Id);

            node = node.Children
                .Select(x => (Node: x, Weight: timed ? x.InclusiveMs : x.TotalCost))
                .Where(static x => x.Weight is > 0)
                .OrderByDescending(static x => x.Weight)
                .ThenBy(static x => x.Node.Id)
                .Select(static x => x.Node)
                .FirstOrDefault();
        }

        return path;
    }

    /// <summary>
    /// Each node's heat under <paramref name="heat" />, by node id; a node with nothing to say - an estimate-only plan under
    /// misestimate, a node that printed no buffers - has no entry.
    /// </summary>
    public static IReadOnlyDictionary<int, PlanHeatCell> HeatMap(QueryPlan plan, PlanHeat heat)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<int, PlanHeatCell> cells = [];

        if (heat == PlanHeat.None)
        {
            return cells;
        }

        List<(PlanNode Node, double Value)> values = [];

        foreach (PlanNode node in plan.Nodes)
        {
            if (Value(plan, node, heat) is { } value)
            {
                values.Add((node, value));
            }
        }

        double max = values.Count == 0 ? 0 : values.Max(static x => x.Value);

        foreach ((PlanNode node, double value) in values)
        {
            // A misestimate is read on a log scale, a thousandfold being as far as the scale goes; the rest are shares of the
            // plan's own hottest node, so a plan with nothing hot in it has nothing coloured.
            double fraction = heat == PlanHeat.Misestimate
                ? Math.Clamp(Math.Log10(Math.Max(value, 1)) / 3, 0, 1)
                : max > 0 ? Math.Clamp(value / max, 0, 1) : 0;

            int level = value <= (heat == PlanHeat.Misestimate ? 1 : 0) ? 0 : Math.Clamp((int) Math.Ceiling(fraction * 4), 1, 4);

            cells[node.Id] = new PlanHeatCell(value, fraction, level, Describe(plan, heat, value));
        }

        return cells;
    }

    /// <summary>What the heat is, and how its scale reads, in words: the legend under the select.</summary>
    public static string Legend(QueryPlan plan, PlanHeat heat)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return heat switch
        {
            PlanHeat.SelfTime => plan.Timed
                ? "Colour is each node's own time - its total less the nodes below it - as a share of the plan's slowest node, in quarters."
                : "Colour is each node's own estimated cost - its total less the nodes below it - as a share of the plan's costliest node, in quarters; run Explain analyze with timing for time.",
            PlanHeat.Misestimate => plan.Analyzed
                ? "Colour is how far the planner's row estimate was off, per loop: ×1 is exact, and a thousandfold is as hot as it goes, in quarters of the logarithm."
                : "Colour is how far the planner's row estimate was off, which a plan that did not run cannot say: run Explain analyze.",
            PlanHeat.BuffersRead => plan.HasBuffers
                ? "Colour is the blocks each node itself read from outside the cache - its own, less the nodes below it - as a share of the plan's most, in quarters."
                : "Colour is the blocks each node read from outside the cache, which this plan did not count: ask for BUFFERS.",
            PlanHeat.TempIo => plan.HasBuffers
                ? "Colour is the temporary-file blocks each node itself read and wrote, as a share of the plan's most, in quarters: nothing is coloured when nothing spilled."
                : "Colour is the temporary-file blocks each node read and wrote, which this plan did not count: ask for BUFFERS.",
            _ => string.Empty,
        };
    }

    private static double? Value(QueryPlan plan, PlanNode node, PlanHeat heat) => heat switch
    {
        PlanHeat.SelfTime => plan.Timed ? node.ExclusiveMs : node.ExclusiveCost,
        PlanHeat.Misestimate => node.MisestimateRatio,
        PlanHeat.BuffersRead => PlanNodeOwn.Of(node) is { } own ? own.SharedRead + own.LocalRead : null,
        PlanHeat.TempIo => PlanNodeOwn.Of(node) is { } own ? own.TempRead + own.TempWritten : null,
        _ => null,
    };

    private static string Describe(QueryPlan plan, PlanHeat heat, double value) => heat switch
    {
        PlanHeat.SelfTime => plan.Timed ? Milliseconds(value) + " of its own" : Cost(value) + " of its own cost",
        PlanHeat.Misestimate => Ratio(value) + " off",
        PlanHeat.BuffersRead => TableText.Count(value) + (value == 1 ? " block read" : " blocks read"),
        PlanHeat.TempIo => TableText.Count(value) + (value == 1 ? " temp block" : " temp blocks"),
        _ => string.Empty,
    };

    /// <summary>The plan's own numbers, as the summary strip and the Markdown export print them.</summary>
    public static PlanSummary Summarize(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        PlanBuffers? buffers = plan.Root.Buffers;
        long hits = buffers?.SharedHit ?? 0;
        long reads = buffers?.SharedRead ?? 0;

        return new PlanSummary
        {
            Analyzed = plan.Analyzed,
            EstimatedCost = plan.Root.TotalCost,
            PlanningMs = plan.PlanningMs,
            ExecutionMs = plan.ExecutionMs,
            JitMs = plan.JitMs,
            JitShare = plan.JitMs is { } jit && plan.ExecutionMs is > 0 and { } execution ? jit / execution : null,
            TriggersMs = plan.TriggersMs,
            RowsReturned = plan.RowsReturned,
            SharedHit = buffers is null ? null : hits,
            SharedRead = buffers is null ? null : reads,
            HitRatio = buffers is not null && hits + reads > 0 ? (double) hits / (hits + reads) : null,
            TempWrittenBlocks = buffers?.TempWritten,
            WalRecords = plan.Root.WalRecords,
            WalBytes = plan.Root.WalBytes,
            PlanningMemoryBytes = plan.PlanningMemoryBytes,
            SerializationMs = plan.SerializationMs,
            NodeCount = plan.NodeCount,
            Truncated = plan.Truncated,
        };
    }

    // ---- Words and numbers shared by the strip, the table and the Markdown --------------------------------------------

    /// <summary>A time, as the tree writes it: <c>0.042 ms</c>, <c>18.2 ms</c>, <c>1.5 s</c>.</summary>
    public static string Milliseconds(double ms) =>
        ms < 1 ? ms.ToString("0.###", CultureInfo.InvariantCulture) + " ms"
        : ms < 1000 ? ms.ToString("0.#", CultureInfo.InvariantCulture) + " ms"
        : (ms / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " s";

    /// <summary>A planner cost, to two decimals.</summary>
    public static string Cost(double value) => value.ToString("#,0.00", CultureInfo.InvariantCulture);

    /// <summary>A misestimate: <c>×73</c>, <c>×1.4</c>.</summary>
    public static string Ratio(double ratio) =>
        "×" + (ratio >= 100 ? Math.Round(ratio).ToString("N0", CultureInfo.InvariantCulture) : ratio.ToString("0.#", CultureInfo.InvariantCulture));

    /// <summary>A count of blocks as the data it is: <c>8,707 blocks (68.0 MiB)</c>.</summary>
    public static string Blocks(long blocks) =>
        TableText.Count(blocks) + (blocks == 1 ? " block" : " blocks") + " (" + TableText.Bytes(blocks * BlockBytes) + ")";

    /// <summary>A number of bytes, as a size.</summary>
    public static string Bytes(double bytes) => TableText.Bytes((long) Math.Round(bytes));
}

/// <summary>A plan's headline numbers (X3's summary strip): each is <see langword="null" /> where Postgres printed nothing.</summary>
internal sealed record PlanSummary
{
    /// <summary>Whether the plan ran.</summary>
    public bool Analyzed { get; init; }

    /// <summary>The root's total cost.</summary>
    public double? EstimatedCost { get; init; }

    /// <summary>The planning time, in milliseconds.</summary>
    public double? PlanningMs { get; init; }

    /// <summary>The execution time, in milliseconds.</summary>
    public double? ExecutionMs { get; init; }

    /// <summary>The time JIT took, in milliseconds.</summary>
    public double? JitMs { get; init; }

    /// <summary>JIT's share of the execution time, from 0 to 1.</summary>
    public double? JitShare { get; init; }

    /// <summary>The time the triggers took, in milliseconds.</summary>
    public double? TriggersMs { get; init; }

    /// <summary>The rows the statement returned.</summary>
    public double? RowsReturned { get; init; }

    /// <summary>Shared blocks found in the cache, over the whole plan.</summary>
    public long? SharedHit { get; init; }

    /// <summary>Shared blocks read from outside the cache, over the whole plan.</summary>
    public long? SharedRead { get; init; }

    /// <summary>Hits over hits and reads, from 0 to 1.</summary>
    public double? HitRatio { get; init; }

    /// <summary>The temporary-file blocks the plan wrote.</summary>
    public long? TempWrittenBlocks { get; init; }

    /// <summary>The write-ahead-log records the plan produced, under <c>WAL</c>.</summary>
    public double? WalRecords { get; init; }

    /// <summary>The bytes of write-ahead log the plan produced, under <c>WAL</c>.</summary>
    public double? WalBytes { get; init; }

    /// <summary>The memory planning used, in bytes, under <c>MEMORY</c>.</summary>
    public double? PlanningMemoryBytes { get; init; }

    /// <summary>The time serializing the result took, in milliseconds, under <c>SERIALIZE</c>.</summary>
    public double? SerializationMs { get; init; }

    /// <summary>How many nodes were read.</summary>
    public int NodeCount { get; init; }

    /// <summary>Whether the read stopped at a bound.</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// The strip's items in the order it draws them, each with its label and its value as text - <see cref="string.Empty" />
    /// where Postgres printed nothing, so a blank is a blank and never a zero. WAL, planning memory and serialization are
    /// there only when they were asked for, so a plan that did not ask has no blank for them.
    /// </summary>
    public IReadOnlyList<(string Label, string Value)> Items
    {
        get
        {
            List<(string Label, string Value)> items =
            [
                ("Estimated cost", EstimatedCost is { } cost ? PlanAnalysis.Cost(cost) : string.Empty),
                ("Planning time", PlanningMs is { } planning ? PlanAnalysis.Milliseconds(planning) : string.Empty),
                ("Execution time", ExecutionMs is { } execution ? PlanAnalysis.Milliseconds(execution) : string.Empty),
                ("JIT", JitMs is { } jit ? PlanAnalysis.Milliseconds(jit) + (JitShare is { } share ? " (" + TableText.Percent(share) + " of execution)" : string.Empty) : string.Empty),
                ("Triggers", TriggersMs is { } triggers ? PlanAnalysis.Milliseconds(triggers) : string.Empty),
                ("Rows returned", RowsReturned is { } rows ? TableText.Count(rows) : string.Empty),
                ("Shared hit", SharedHit is { } hit ? PlanAnalysis.Blocks(hit) : string.Empty),
                ("Shared read", SharedRead is { } read ? PlanAnalysis.Blocks(read) : string.Empty),
                ("Cache hit ratio", HitRatio is { } ratio ? TableText.Percent(ratio) : string.Empty),
                ("Temp written", TempWrittenBlocks is { } temp ? PlanAnalysis.Blocks(temp) : string.Empty),
            ];

            if (WalRecords is { } records)
            {
                items.Add(("WAL records", TableText.Count(records)));
            }

            if (WalBytes is { } bytes)
            {
                items.Add(("WAL bytes", PlanAnalysis.Bytes(bytes)));
            }

            if (PlanningMemoryBytes is { } memory)
            {
                items.Add(("Planning memory", PlanAnalysis.Bytes(memory)));
            }

            if (SerializationMs is { } serialization)
            {
                items.Add(("Serialization", PlanAnalysis.Milliseconds(serialization)));
            }

            return items;
        }
    }
}
