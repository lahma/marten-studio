using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Query;

/// <summary>
/// P15 to P20 (0.10-A, issue #28): the insights that read what the database knows beside the plan (<see cref="PlanFacts" />), and
/// two that read the plan alone. Pure evaluators over (plan, facts?): without facts the four that need them find nothing and are
/// listed as not judged.
/// </summary>
internal static partial class PlanInsights
{
    /// <summary>P15's "stale": this share of the table's rows changed since it was last analyzed, or more.</summary>
    public const double StaleShare = 0.10;

    /// <summary>P16's fewest columns a filter compares for them to be correlated.</summary>
    public const int CorrelatedMinColumns = 2;

    /// <summary>P18's fewest partitions scanned: three of three is a small table, not a missing predicate.</summary>
    public const int UnprunedMinScanned = 4;

    /// <summary>P19's default: the rows Postgres assumes a set-returning function returns when it knows nothing (<c>pg_proc.prorows</c>).</summary>
    public const double DefaultFunctionRowEstimate = 1_000;

    /// <summary>P20's share: triggers took this much of the execution time, or more.</summary>
    public const double TriggerShare = 0.30;

    /// <summary>
    /// P15: a node P01 flags whose rows - or the rows of a node below it - come from a table that was never analyzed or that has
    /// changed by <see cref="StaleShare" /> or more since it was: the planner's row counts for it are out of date. One finding for
    /// each such table, on the node with the worst misestimate that reads it.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> StaleStatisticsBehindMisestimates(QueryPlan plan, PlanFacts? facts)
    {
        if (facts is null || !plan.Analyzed)
        {
            return [];
        }

        List<PlanInsightFinding> findings = [];
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach ((PlanNode node, double expected, double actual, double ratio) in MisestimatedNodes(plan)
            .OrderByDescending(static x => x.Ratio)
            .ThenBy(static x => x.Node.Id))
        {
            foreach (PlanNode scan in node.DescendantsAndSelf())
            {
                if (facts.Of(scan) is not { } relation || StaleReason(relation, facts.ReadAt) is not { } reason || !reported.Add(relation.Label))
                {
                    continue;
                }

                findings.Add(Finding(
                    StaleStatistics,
                    node,
                    node.Description + " was planned for " + Rows(expected) + " rows a loop and returned " + Rows(actual) + Loops(node) + ": "
                    + Times(ratio) + (actual > expected ? " more" : " fewer") + " than expected. " + relation.Label + " " + reason
                    + ", so the planner's row counts for it are out of date.",
                    StaleRemedy(relation)) with
                {
                    RelatedPage = PlanInsightPage.Maintenance,
                });

                if (findings.Count >= MaxPerInsight)
                {
                    return findings;
                }
            }
        }

        return findings;
    }

    /// <summary>Why a table's statistics are out of date, in the middle of a sentence, or <see langword="null" /> when they are not.</summary>
    internal static string? StaleReason(PlanRelationFacts relation, DateTimeOffset now)
    {
        if (relation.NeverAnalyzed && relation.EstimatedRows is null)
        {
            return "has never been analyzed - Postgres has no row estimate for it";
        }

        double basis = Math.Max(relation.EstimatedRows ?? relation.LiveTuples ?? 0, 1);

        if (relation.ModifiedSinceAnalyze is { } changed && changed >= basis * StaleShare)
        {
            return "has had " + Count(changed) + " rows changed (" + TableText.Percent(changed / basis) + " of its ~" + Count(Math.Round(basis)) + ") since it was last analyzed, "
                + (relation.AnalyzedAt is { } at ? TableText.Ago(at, now) : "which the statistics collector has no record of");
        }

        return null;
    }

    private static string StaleRemedy(PlanRelationFacts relation)
    {
        string analyze = MigrationSqlText.Analyze(relation.Schema, relation.Name) is { } statement
            ? ": " + statement
            : " on the table";
        string tune = MigrationSqlText.SetStorageParameter(relation.Schema, relation.Name, "autovacuum_analyze_scale_factor", "0.02") is { } alter
            ? " (" + alter + ")"
            : string.Empty;

        return "Analyze it, so the planner sees the table as it is now" + analyze + " It takes only a SHARE UPDATE EXCLUSIVE lock, so reads and "
            + "writes carry on; it is text for your own migrations or a maintenance window - the studio never runs it. To keep it current, give the table "
            + "a lower autovacuum_analyze_scale_factor" + tune + ": autovacuum analyzes a table once 10% of its rows have changed unless "
            + "somebody set otherwise. Operations › Maintenance shows when autovacuum will next reach it.";
    }

    /// <summary>
    /// P16: a scan P01 flags whose filter compares two or more columns of the table together, with no extended statistics covering
    /// any two of them: the planner multiplies each column's selectivity as if the columns were independent. Columns joined by
    /// <c>OR</c> are not read as together.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> CorrelatedColumnFilters(QueryPlan plan, PlanFacts? facts, PlanFactsTargets? targets)
    {
        if (facts is null || targets is null || !plan.Analyzed)
        {
            return [];
        }

        List<PlanInsightFinding> findings = [];

        foreach ((PlanNode node, double expected, double actual, double ratio) in MisestimatedNodes(plan)
            .OrderByDescending(static x => x.Ratio)
            .ThenBy(static x => x.Node.Id))
        {
            if (node.Schema is not { } schema || node.RelationName is not { } name || facts.Of(node) is not { } relation)
            {
                continue;
            }

            PlanRelationRef own = new(schema, name);

            List<string> columns =
            [
                .. targets.ColumnsOf(node.Id)
                    .Where(x => x.Relation == own && x.Source is PlanConditionSource.Filter or PlanConditionSource.IndexCondition)
                    .Select(static x => x.Column)
                    .Distinct(StringComparer.Ordinal),
            ];

            if (columns.Count < CorrelatedMinColumns
                || PlanConditionText.HasWord(node.Filter, "or")
                || PlanConditionText.HasWord(node.IndexCondition, "or")
                || PlanConditionText.HasWord(node.RecheckCondition, "or")
                || relation.ExtendedStatistics.Any(x => x.Columns.Count(columns.Contains) >= 2))
            {
                continue;
            }

            string create = MigrationSqlText.CreateStatistics(schema, name, columns) is { } statement
                ? ": " + statement + " and then ANALYZE the table (" + (MigrationSqlText.Analyze(schema, name) ?? "ANALYZE") + ")"
                : " (CREATE STATISTICS) over " + string.Join(", ", columns) + ", and then ANALYZE the table";

            findings.Add(Finding(
                CorrelatedColumns,
                node,
                node.Description + " was planned for " + Rows(expected) + " rows a loop and returned " + Rows(actual) + Loops(node) + ": " + Times(ratio)
                + (actual > expected ? " more" : " fewer") + " than expected. It compares " + string.Join(", ", columns) + " together, and no extended statistics "
                + "cover them, so the planner multiplies each column's selectivity as if the columns were independent - wrong wherever one tells you "
                + "something about the other (a city and its postcode, a status and the date it was set).",
                "Extended statistics tell the planner how the columns relate" + create + ". It is text for your own migrations - the studio never runs it. "
                + "The kinds named (dependencies, ndistinct, mcv) cover equality filters, the number of distinct combinations a GROUP BY sees, and the most common "
                + "combinations; they are filled in by the next ANALYZE."));

            if (findings.Count >= MaxPerInsight)
            {
                break;
            }
        }

        return findings;
    }

    /// <summary>
    /// P17: a <c>Filter</c> or <c>Join Filter</c> that wraps a column in a function, or casts it to a type that is not its own, where
    /// that column leads an index of its table: the index cannot serve a condition on the expression. A cast between <c>varchar</c> and
    /// <c>text</c> is the same bytes and is no wrapper. An <c>Index Cond</c> is not read: an index that serves a condition is already
    /// being used, and one that wraps its column is an expression index.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> WrappedIndexedColumns(QueryPlan plan, PlanFacts? facts, PlanFactsTargets? targets)
    {
        if (facts is null || targets is null)
        {
            return [];
        }

        List<PlanInsightFinding> findings = [];
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (PlanNode node in plan.Nodes)
        {
            foreach (PlanNodeColumn used in targets.ColumnsOf(node.Id).Where(static x => x.Source is PlanConditionSource.Filter or PlanConditionSource.JoinFilter))
            {
                PlanRelationFacts? relation = facts.Find(used.Relation.Schema, used.Relation.Name);

                if (relation is not { State: PlanRelationState.Read }
                    || relation.FindColumn(used.Column) is not { } column
                    || WrapperText(used.Reference, column.Type) is not { } wrapper
                    || relation.Indexes.FirstOrDefault(x => x.IsValid && string.Equals(x.LeadingColumn, used.Column, StringComparison.Ordinal)) is not { } index
                    || !reported.Add(relation.Label + "\u0001" + used.Column + "\u0001" + wrapper))
                {
                    continue;
                }

                bool expressionIndex = relation.Indexes.Any(static x => x.HasExpressions);

                findings.Add(Finding(
                    WrappedIndexedColumn,
                    node,
                    node.Description + " filters on " + wrapper + " of " + relation.Label + "." + used.Column + ", which leads the index " + index.Name
                    + " (" + string.Join(", ", index.KeyColumns.Select(static x => x ?? "(expression)")) + "): an index serves a condition on the column, not on "
                    + "a function or a cast of it, so " + index.Name + " cannot be used for this condition.",
                    WrapperRemedy(used.Reference, column.Type, expressionIndex)));

                if (findings.Count >= MaxPerInsight)
                {
                    return findings;
                }
            }
        }

        return findings;
    }

    /// <summary>How a column is wrapped, in words, when the wrapper defeats an index - or <see langword="null" /> when it does not.</summary>
    internal static string? WrapperText(PlanConditionColumn reference, string? columnType)
    {
        if (reference.Function is { } function)
        {
            return function + "()";
        }

        if (reference.CastType is { } cast && columnType is not null && !SameBytes(columnType, cast))
        {
            return "a cast to " + cast;
        }

        return null;
    }

    /// <summary>
    /// Whether casting a column of type <paramref name="columnType" /> to <paramref name="castType" /> leaves the column as it is: the
    /// same type (a size ignored), or <c>varchar</c> and <c>text</c>, which share their bytes and their operator class.
    /// </summary>
    internal static bool SameBytes(string columnType, string castType)
    {
        string column = BaseType(columnType);
        string cast = BaseType(castType);

        if (string.Equals(column, cast, StringComparison.Ordinal))
        {
            return true;
        }

        return IsText(column) && IsText(cast);

        static bool IsText(string type) => type is "text" or "character varying" or "varchar";
    }

    private static string BaseType(string type)
    {
        string lower = type.Trim().ToLowerInvariant();
        int size = lower.IndexOf('(', StringComparison.Ordinal);

        return size < 0 ? lower : lower[..size].TrimEnd() + lower[(lower.IndexOf(')', size) + 1)..];
    }

    private static string WrapperRemedy(PlanConditionColumn reference, string? columnType, bool expressionIndex)
    {
        string caveat = expressionIndex
            ? " The table has an index on an expression: if it is on this one, a plan that used it would show it in an Index Cond."
            : string.Empty;

        if (reference.Function is { } function)
        {
            return "Compare the column as it is - move the " + function + "() to the value it is compared with, where that is possible - or build an index on "
                + "the expression the filter uses (an expression index, " + function + "(" + reference.Column + ")): a btree on the expression serves a condition on "
                + "exactly that expression. An index on the bare column stays useful for conditions that do not wrap it." + caveat;
        }

        return "Cast the other side of the comparison - the constant or the parameter - to the column's type" + (columnType is null ? string.Empty : " (" + columnType + ")")
            + " instead of casting the column, or index the cast expression. A range on the column itself (created_at >= '2026-01-01' and created_at < '2026-01-02' "
            + "for a day) uses the plain index where a cast to date cannot." + caveat;
    }

    /// <summary>
    /// P18: an <c>Append</c> or <c>Merge Append</c> that reads <see cref="UnprunedMinScanned" /> partitions or more, more than half
    /// of its partitioned table's partitions, with no <c>Subplans Removed</c>: nothing narrowed the partitions down, plan time or run
    /// time. Says whether any scanned partition's condition mentions a column of the partition key.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> UnprunedPartitions(QueryPlan plan, PlanFacts? facts, PlanFactsTargets? targets)
    {
        if (facts is null || targets is null)
        {
            return [];
        }

        List<PlanInsightFinding> findings = [];

        foreach (PlanNode append in plan.Nodes.Where(static x => x.NodeType is "Append" or "Merge Append"))
        {
            if (SubplansRemoved(append) > 0)
            {
                continue;
            }

            List<(PlanNode Child, PlanPartitionFact Partition)> partitions =
            [
                .. append.Children
                    .Where(static x => !x.NeverExecuted)
                    .Select(x => (Child: x, Partition: facts.Of(x)?.Partition))
                    .Where(static x => x.Partition is not null)
                    .Select(static x => (x.Child, x.Partition!)),
            ];

            IGrouping<(string Schema, string Name), (PlanNode Child, PlanPartitionFact Partition)>? biggest = partitions
                .GroupBy(static x => (x.Partition.ParentSchema, x.Partition.ParentName))
                .OrderByDescending(static x => x.Count())
                .FirstOrDefault();

            if (biggest is null)
            {
                continue;
            }

            int scanned = biggest.Count();
            int total = biggest.Max(static x => x.Partition.Partitions);

            if (scanned < UnprunedMinScanned || scanned * 2 <= total)
            {
                continue;
            }

            IReadOnlyList<string> key = biggest.First().Partition.KeyColumns;
            bool mentionsKey = biggest.Any(x => targets.ColumnsOf(x.Child.Id).Any(c => key.Contains(c.Column, StringComparer.Ordinal)));
            string keyText = key.Count == 0 ? "its partition key" : string.Join(", ", key);

            findings.Add(Finding(
                NoPartitionPruning,
                append,
                append.Description + " reads " + Count(scanned) + " of the " + Count(total) + " partitions of " + biggest.Key.Schema + "." + biggest.Key.Name
                + ", and Postgres removed none of them (no Subplans Removed): " + (mentionsKey
                    ? "the statement filters on the partition key (" + keyText + "), but not in a form Postgres could prune by."
                    : "nothing in the statement filters on the partition key (" + keyText + ")."),
                mentionsKey
                    ? "Postgres prunes partitions on a condition that compares the partition key as it is with a value: a function or a cast of the key (date_trunc('day', ts), "
                      + "ts::date), an OR across other columns or a comparison with another column defeats it. Compare the key itself with constants or parameters - for a "
                      + "timestamp key, a range (ts >= … and ts < …) - and a parameter is pruned at run time (Subplans Removed) when the statement is planned generically."
                    : "Add a condition on the partition key (" + keyText + ") to the statement, comparing the key itself with constants or parameters; with one, Postgres reads "
                      + "only the partitions the condition can match - at plan time for constants, at run time for parameters. When every partition really is needed, "
                      + "this plan is as good as it gets."));

            if (findings.Count >= MaxPerInsight)
            {
                break;
            }
        }

        return findings;
    }

    /// <summary>The partitions an <c>Append</c> or <c>Merge Append</c> removed at run time, from its <c>Subplans Removed</c>.</summary>
    internal static double SubplansRemoved(PlanNode node) =>
        node.Properties.FirstOrDefault(static x => string.Equals(x.Key, "Subplans Removed", StringComparison.Ordinal)) is { } property
        && double.TryParse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double removed)
            ? removed
            : 0;

    /// <summary>
    /// P19: a <c>Function Scan</c> the planner expects to return exactly <see cref="DefaultFunctionRowEstimate" /> rows: the default it
    /// assumes for a set-returning function it knows nothing about. Under <c>ANALYZE</c> a function that did return that many is
    /// not flagged - the guess was right.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> DefaultRowFunctionScans(QueryPlan plan) =>
        plan.Nodes
            .Where(static x => x.NodeType == "Function Scan" && x.PlanRows == DefaultFunctionRowEstimate && !x.NeverExecuted)
            .Where(static x => x.ActualRows is not { } actual || x.ActualLoops is not { } loops || Math.Round(actual * loops) != DefaultFunctionRowEstimate)
            .Take(MaxPerInsight)
            .Select(static x => Finding(
                DefaultFunctionRows,
                x,
                x.Description + " is expected to return " + Count(DefaultFunctionRowEstimate) + " rows, which is what Postgres assumes of any set-returning function it has no better "
                + "figure for" + (x.ActualRows is { } actual && x.ActualLoops is { } loops ? ", and it returned " + Rows(actual * loops) : string.Empty)
                + ": every join and sort above it was planned for that number.",
                "A function you wrote takes its estimate from its ROWS clause: ALTER FUNCTION … ROWS n (or CREATE FUNCTION … ROWS n) with the typical result size tells the planner. "
                + "One that returns a few rows or millions is planned for a thousand until it does. For a function of Postgres' or an extension's, put its result in a "
                + "subquery or a CTE the planner can see through, or filter and join in a form that does not depend on the estimate."));

    /// <summary>P21's ratio: the generic plan's total cost is at least this many times the custom plan's.</summary>
    public const double GenericCostRatio = 10;

    /// <summary>
    /// P21 (0.10-B): the generic plan of a statement costs <see cref="GenericCostRatio" /> times what its custom plan does or more. A
    /// prepared statement is planned for its first five runs with the actual values; from then on Postgres switches to the generic plan
    /// when its cost is not much above the custom plans' average - so a generic plan this far above is one a server left to
    /// <c>plan_cache_mode = auto</c> would still avoid, and one <c>force_generic_plan</c> (which a driver or a pooler can impose) would not.
    /// It judges two plans of the same statement, which only the Plan page's comparison has: it is never part of <see cref="Evaluate" />.
    /// </summary>
    /// <param name="generic">The generic plan.</param>
    /// <param name="custom">The custom plan, for the values the page was given.</param>
    /// <returns>The finding, on the generic plan's root, or <see langword="null" />.</returns>
    public static PlanInsightFinding? GenericVersusCustom(QueryPlan generic, QueryPlan custom)
    {
        ArgumentNullException.ThrowIfNull(generic);
        ArgumentNullException.ThrowIfNull(custom);

        if (generic.Root.TotalCost is not { } genericCost || custom.Root.TotalCost is not { } customCost || customCost <= 0
            || genericCost < customCost * GenericCostRatio)
        {
            return null;
        }

        return Finding(
            GenericWorseThanCustom,
            generic.Root,
            "The generic plan's estimated cost is " + CostText(genericCost) + " against " + CostText(customCost) + " for the custom plan: "
            + Times(genericCost / customCost) + " as much. Planned without the values, Postgres could not see how selective they are.",
            "A prepared statement runs on the generic plan once Postgres judges it no worse than the custom ones - the sessions of a pooler "
            + "or a driver that prepares every statement get it, and a plan this far off is the slow query nobody can reproduce by typing the "
            + "values in. Where it hurts, plan_cache_mode = force_custom_plan for the role or the session (ALTER ROLE … SET plan_cache_mode) plans "
            + "each execution for its values; or give the planner what it lacks - an index, extended statistics, a statement that does not "
            + "depend on one skewed value - so the generic plan is a good one.");
    }

    private static string CostText(double cost) => cost.ToString("#,0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// P20: the triggers that fired took <see cref="TriggerShare" /> of the execution time or more: the statement spends its time
    /// in them, not in the plan the tree draws. Under <c>ANALYZE</c> only - a plain plan fires none.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> DominatingTriggers(QueryPlan plan)
    {
        if (!plan.Analyzed || plan.TriggersMs is not { } triggers || plan.ExecutionMs is not { } execution || execution <= 0 || triggers < execution * TriggerShare)
        {
            return [];
        }

        string slowest = string.Join(
            ", ",
            plan.Triggers
                .Where(static x => x.TimeMs is not null)
                .OrderByDescending(static x => x.TimeMs)
                .Take(3)
                .Select(static x => x.Name + (x.Relation is { } on ? " on " + on : string.Empty) + " (" + Milliseconds(x.TimeMs!.Value)
                    + (x.Calls is { } calls ? ", " + Count(calls) + " " + Plural(calls, "call", "calls") : string.Empty) + ")"));

        return
        [
            Finding(
                TriggerTime,
                plan.Root,
                "The triggers that fired took " + Milliseconds(triggers) + " - " + TableText.Percent(triggers / execution) + " of the " + Milliseconds(execution)
                + " the statement ran for: " + slowest + ". The plan above them is not where the time went.",
                "Trigger time is not in any node's time. Look at what the slowest trigger's function does for each row it fires on - a lookup without an index on its key, "
                + "a write to another table, an audit insert - and whether a statement-level trigger (FOR EACH STATEMENT, with a transition table) can do it once for the "
                + "whole statement instead of once a row. A foreign key is a trigger too: an unindexed referencing column makes every delete or update of the referenced "
                + "row scan the referencing table."),
        ];
    }
}
