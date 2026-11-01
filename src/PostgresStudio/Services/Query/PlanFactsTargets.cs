namespace PostgresStudio.Services.Query;

/// <summary>A relation a plan names, by the schema and name it printed.</summary>
/// <param name="Schema">The schema; present only in a plan run with <c>VERBOSE</c>.</param>
/// <param name="Name">The relation.</param>
internal readonly record struct PlanRelationRef(string Schema, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Schema + "." + Name;
}

/// <summary>Which of a node's texts a column was found in.</summary>
internal enum PlanConditionSource
{
    /// <summary><c>Filter</c>.</summary>
    Filter,

    /// <summary><c>Index Cond</c> and <c>Recheck Cond</c>: what an index serves.</summary>
    IndexCondition,

    /// <summary><c>Hash Cond</c> and <c>Merge Cond</c>: a join's keys.</summary>
    Join,

    /// <summary><c>Join Filter</c>.</summary>
    JoinFilter,

    /// <summary><c>Sort Key</c>.</summary>
    Sort,

    /// <summary><c>Group Key</c>.</summary>
    Group,
}

/// <summary>A column of a relation a node filters, joins, sorts or groups on, and how the node wraps it.</summary>
/// <param name="Relation">The relation it belongs to.</param>
/// <param name="Reference">The reference as the node's text writes it.</param>
/// <param name="Source">The text it was found in.</param>
internal sealed record PlanNodeColumn(PlanRelationRef Relation, PlanConditionColumn Reference, PlanConditionSource Source)
{
    /// <summary>The column.</summary>
    public string Column => Reference.Column;
}

/// <summary>
/// What a plan asks the database about (0.10-A, issue #28): the relations it scans, by the schema and name the plan printed, and for
/// each the columns its nodes filter, join, sort and group on - found in the deparsed text of the conditions, never in the
/// statement the visitor typed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a plan run with <c>VERBOSE</c> names a schema</b>, and only a relation with its schema is resolved: an unqualified name
/// would resolve through whichever <c>search_path</c> the facts read has, which is not the statement's. The scans that name none are
/// counted (<see cref="Unqualified" />), and the panel says so.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="MaxRelations" /> relations and <see cref="MaxColumnsPerRelation" /> columns each; the plan itself
/// is bounded already (<see cref="QueryPlanReader.MaxNodes" />).
/// </para>
/// </remarks>
internal sealed class PlanFactsTargets
{
    /// <summary>The most relations one facts read takes; the rest say "not read".</summary>
    public const int MaxRelations = 50;

    /// <summary>The most columns of one relation whose statistics are read.</summary>
    public const int MaxColumnsPerRelation = 32;

    private readonly Dictionary<int, IReadOnlyList<PlanNodeColumn>> nodeColumns;

    private PlanFactsTargets(
        IReadOnlyList<PlanRelationRef> relations,
        int notRead,
        int unqualified,
        IReadOnlyDictionary<PlanRelationRef, IReadOnlyList<string>> columns,
        Dictionary<int, IReadOnlyList<PlanNodeColumn>> nodeColumns)
    {
        Relations = relations;
        NotRead = notRead;
        Unqualified = unqualified;
        Columns = columns;
        this.nodeColumns = nodeColumns;
    }

    /// <summary>The relations to read, in the plan's order.</summary>
    public IReadOnlyList<PlanRelationRef> Relations { get; }

    /// <summary>How many relations the plan names past <see cref="MaxRelations" />.</summary>
    public int NotRead { get; }

    /// <summary>How many relation-scanning nodes name no schema.</summary>
    public int Unqualified { get; }

    /// <summary>The columns of each relation the plan's nodes use, distinct, in the order they first appear.</summary>
    public IReadOnlyDictionary<PlanRelationRef, IReadOnlyList<string>> Columns { get; }

    /// <summary>The columns node <paramref name="nodeId" /> filters, joins, sorts or groups on, with their relations.</summary>
    public IReadOnlyList<PlanNodeColumn> ColumnsOf(int nodeId) =>
        nodeColumns.TryGetValue(nodeId, out IReadOnlyList<PlanNodeColumn>? found) ? found : [];

    /// <summary>Reads what <paramref name="plan" /> asks about.</summary>
    /// <param name="plan">The plan.</param>
    public static PlanFactsTargets From(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<PlanRelationRef> relations = [];
        HashSet<PlanRelationRef> seen = [];
        int notRead = 0;
        int unqualified = 0;
        Dictionary<string, PlanRelationRef?> aliases = new(StringComparer.Ordinal);

        foreach (PlanNode node in plan.Nodes)
        {
            if (node.RelationName is not { } name)
            {
                continue;
            }

            if (node.Schema is not { } schema)
            {
                unqualified++;
                continue;
            }

            PlanRelationRef relation = new(schema, name);

            // An alias two different relations share (in two subqueries) names neither.
            string alias = node.Alias ?? name;
            aliases[alias] = aliases.TryGetValue(alias, out PlanRelationRef? existing) && existing != relation ? null : relation;

            if (!seen.Add(relation))
            {
                continue;
            }

            if (relations.Count < MaxRelations)
            {
                relations.Add(relation);
            }
            else
            {
                notRead++;
            }
        }

        HashSet<PlanRelationRef> kept = [.. relations];
        Dictionary<int, IReadOnlyList<PlanNodeColumn>> perNode = [];
        Dictionary<PlanRelationRef, List<string>> perRelation = [];

        foreach (PlanNode node in plan.Nodes)
        {
            List<PlanNodeColumn> columns = Of(node, aliases);

            if (columns.Count == 0)
            {
                continue;
            }

            perNode[node.Id] = columns;

            foreach (PlanNodeColumn column in columns.Where(x => kept.Contains(x.Relation)))
            {
                if (!perRelation.TryGetValue(column.Relation, out List<string>? list))
                {
                    perRelation[column.Relation] = list = [];
                }

                if (list.Count < MaxColumnsPerRelation && !list.Contains(column.Column, StringComparer.Ordinal))
                {
                    list.Add(column.Column);
                }
            }
        }

        return new PlanFactsTargets(
            relations,
            notRead,
            unqualified,
            perRelation.ToDictionary(static x => x.Key, static x => (IReadOnlyList<string>) x.Value),
            perNode);
    }

    /// <summary>
    /// The columns <paramref name="node" /> uses: a reference qualified by an alias belongs to that alias's relation; an unqualified one
    /// to the relation the node scans, or - on a join, sort or aggregate - to the only relation scanned beneath it.
    /// </summary>
    private static List<PlanNodeColumn> Of(PlanNode node, Dictionary<string, PlanRelationRef?> aliases)
    {
        List<(string? Text, PlanConditionSource Source)> texts =
        [
            (node.Filter, PlanConditionSource.Filter),
            (node.IndexCondition, PlanConditionSource.IndexCondition),
            (node.RecheckCondition, PlanConditionSource.IndexCondition),
            (node.HashCondition, PlanConditionSource.Join),
            (node.MergeCondition, PlanConditionSource.Join),
            (node.JoinFilter, PlanConditionSource.JoinFilter),
            .. node.SortKey.Select(static x => ((string?) x, PlanConditionSource.Sort)),
            .. node.GroupKey.Select(static x => ((string?) x, PlanConditionSource.Group)),
        ];

        if (texts.All(static x => string.IsNullOrEmpty(x.Text)))
        {
            return [];
        }

        PlanRelationRef? only = null;

        if (node.RelationName is { } name && node.Schema is { } schema)
        {
            only = new PlanRelationRef(schema, name);
        }
        else
        {
            List<PlanRelationRef> below =
            [
                .. node.DescendantsAndSelf()
                    .Where(static x => x.RelationName is not null && x.Schema is not null)
                    .Select(static x => new PlanRelationRef(x.Schema!, x.RelationName!))
                    .Distinct(),
            ];

            if (below.Count == 1)
            {
                only = below[0];
            }
        }

        List<PlanNodeColumn> columns = [];

        foreach ((string? text, PlanConditionSource source) in texts)
        {
            foreach (PlanConditionColumn reference in PlanConditionText.Columns(text))
            {
                PlanRelationRef? relation = reference.Qualifier is { } qualifier
                    ? (aliases.TryGetValue(qualifier, out PlanRelationRef? aliased) ? aliased : null)
                    : only;

                if (relation is { } resolved)
                {
                    PlanNodeColumn column = new(resolved, reference, source);

                    if (!columns.Contains(column))
                    {
                        columns.Add(column);
                    }
                }
            }
        }

        return columns;
    }
}
