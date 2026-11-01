using System.Globalization;

using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Query;

/// <summary>
/// What the database knows about one node's relation and columns (0.10-A, issue #28): the Facts panel's per-node view, built once
/// for a plan and its facts.
/// </summary>
/// <remarks>
/// A node is shown the facts of the relation it scans, and the statistics of the columns it filters, joins, sorts or groups on -
/// found by <see cref="PlanFactsTargets" /> in the deparsed condition texts, and only where the column belongs to a relation whose
/// facts were read. A relation the visitor may not see (<see cref="PlanRelationState.Withheld" />) shows its label
/// (<see cref="PlanFacts.Withheld" />) and nothing else.
/// </remarks>
internal sealed class PlanNodeFacts
{
    private readonly PlanFacts facts;
    private readonly PlanFactsTargets targets;

    /// <summary>Builds the per-node view of <paramref name="facts" />.</summary>
    /// <param name="plan">The plan the facts were read for.</param>
    /// <param name="facts">The facts.</param>
    public PlanNodeFacts(QueryPlan plan, PlanFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        this.facts = facts;
        targets = PlanFactsTargets.From(plan);
    }

    /// <summary>The relation <paramref name="node" /> scans, as read - or <see langword="null" /> when it scans none or none was read.</summary>
    public PlanRelationFacts? RelationOf(PlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return facts.Find(node.Schema, node.RelationName);
    }

    /// <summary>
    /// The columns <paramref name="node" /> uses, each with the statistics read for it, where they were: one entry for each
    /// (text it was found in, relation, column), the first reference's.
    /// </summary>
    public IReadOnlyList<(PlanNodeColumn Used, PlanRelationFacts Relation, PlanColumnFact? Column)> ColumnsOf(PlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        List<(PlanNodeColumn, PlanRelationFacts, PlanColumnFact?)> list = [];
        HashSet<(PlanConditionSource, PlanRelationRef, string)> seen = [];

        foreach (PlanNodeColumn used in targets.ColumnsOf(node.Id))
        {
            // The targets keep one entry for each reference, wrapping and all (P17 reads that); a column the node uses twice in one text
            // - ((a > 0) AND ((a)::text = '1')) - is one column here, and a key a page draws it by is unique.
            if (facts.Find(used.Relation.Schema, used.Relation.Name) is { State: PlanRelationState.Read } relation
                && seen.Add((used.Source, used.Relation, used.Column)))
            {
                list.Add((used, relation, relation.FindColumn(used.Column)));
            }
        }

        return list;
    }

    /// <summary>
    /// A line for the node table: the scanned relation's estimate and how long ago it was analyzed, or the withheld label. Empty for a
    /// node with nothing to say.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="now">The moment the facts are described at.</param>
    public string Summary(PlanNode node, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (RelationOf(node) is not { } relation)
        {
            return string.Empty;
        }

        return PlanFactsText.RelationLine(relation, now);
    }
}

/// <summary>The Facts panel's words and numbers: pure, so a test reads them without rendering.</summary>
internal static class PlanFactsText
{
    /// <summary>One relation in a line: <c>public.orders: ~1,200,000 rows, analyzed 3 days ago, 14% changed since</c>.</summary>
    /// <param name="relation">The relation.</param>
    /// <param name="now">The moment it is described at.</param>
    public static string RelationLine(PlanRelationFacts relation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(relation);

        switch (relation.State)
        {
            case PlanRelationState.Withheld:
                return PlanFacts.Withheld + ": its schema is not one you may see, so nothing is read about it.";
            case PlanRelationState.NotResolved:
            case PlanRelationState.NotRead:
                return relation.Label + ": " + (relation.Note ?? "not read.");
        }

        List<string> parts = [];

        parts.Add(relation.EstimatedRows is { } rows ? "~" + TableText.Count(Math.Round(rows)) + " rows" : "no row estimate");

        if (relation.NeverAnalyzed)
        {
            parts.Add("never analyzed");
        }
        else if (relation.AnalyzedAt is { } at)
        {
            parts.Add("analyzed " + TableText.Span(now - at) + " ago");
        }

        if (relation.ModifiedSinceAnalyze is { } changed and > 0)
        {
            double basis = Math.Max(relation.EstimatedRows ?? relation.LiveTuples ?? 0, 1);
            parts.Add(TableText.Count(changed) + " changed since (" + TableText.Percent(changed / basis) + ")");
        }

        return relation.Label + ": " + string.Join(", ", parts);
    }

    /// <summary>
    /// <c>n_distinct</c> as a person reads it: a positive count is a count, a negative one is a share of the table's rows
    /// (<c>-1</c> is "every row differs").
    /// </summary>
    /// <param name="distinct"><c>pg_stats.n_distinct</c>.</param>
    public static string Distinct(double? distinct) => distinct switch
    {
        null => "-",
        -1 => "all rows differ",
        < 0 => TableText.Percent(-distinct.Value) + " of the rows",
        _ => TableText.Count(Math.Round(distinct.Value)),
    };

    /// <summary>A share of nulls, as a percentage.</summary>
    /// <param name="fraction"><c>pg_stats.null_frac</c>.</param>
    public static string NullShare(double? fraction) => fraction is { } value ? TableText.Percent(value) : "-";

    /// <summary>A correlation, to two decimals.</summary>
    /// <param name="correlation"><c>pg_stats.correlation</c>.</param>
    public static string Correlation(double? correlation) =>
        correlation is { } value ? value.ToString("0.##", CultureInfo.InvariantCulture) : "-";

    /// <summary>A column's statistics in one line, for the node's detail.</summary>
    /// <param name="column">The column's facts, or <see langword="null" /> when none were read.</param>
    public static string ColumnLine(PlanColumnFact? column)
    {
        if (column is null)
        {
            return "no statistics were read for it";
        }

        if (!column.HasStatistics)
        {
            return "not analyzed: Postgres keeps no statistics for it yet";
        }

        return "distinct " + Distinct(column.Distinct) + ", nulls " + NullShare(column.NullFraction) + ", correlation " + Correlation(column.Correlation);
    }

    /// <summary>An index's keys: <c>(customer_id, created_at)</c>, an expression as <c>(expression)</c>.</summary>
    /// <param name="index">The index.</param>
    public static string Keys(PlanIndexFact index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return "(" + string.Join(", ", index.KeyColumns.Select(static x => x ?? "(expression)")) + ")";
    }

    /// <summary>What the panel says where the most common values and histogram were not read.</summary>
    /// <param name="relation">The relation.</param>
    public static string? ValuesNote(PlanRelationFacts relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        return relation.Values switch
        {
            PlanValuesState.NeedsBrowse => "Most common values and histogram bounds need BrowseDatabase: they are values from the table's rows.",
            PlanValuesState.Refused => "Most common values and histogram bounds are not shown: " + (relation.ValuesNote ?? "the database browser may not read this relation's rows."),
            _ => null,
        };
    }
}
