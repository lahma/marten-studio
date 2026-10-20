using System.Linq.Expressions;
using System.Reflection;

using MartenStudio.Internal.Sql;
using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Query;

/// <summary>
/// The index the LINQ a visitor typed needs: the members it filters and orders on, put to the existing
/// <see cref="IndexAdvisor" />, and set beside what <c>EXPLAIN</c> says Postgres would actually do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves, on purpose.</b> The advisor reads index definitions against the members the predicate names, and
/// answers with the <c>StoreOptions</c> line that would declare the index - <c>Duplicate(x =&gt; x.Customer.Name)</c>
/// - because that is something the host can act on, and an index created by hand is dropped by the next
/// <c>CreateOrUpdate</c> (hard rule 14). The plan is the other half: the scans Postgres chose for this statement
/// with these statistics, which can disagree with the advice for good reasons (a small table, a predicate that
/// matches most rows), and the panel says so when they do.
/// </para>
/// <para>
/// Only members read from the document itself are advised on. A condition inside <c>Any(…)</c> reads an element of
/// a JSON array, which is the containment question (a GIN index on <c>data</c>) rather than a column question.
/// </para>
/// </remarks>
internal static class LinqIndexAdvisor
{
    /// <summary>The most scans read out of one plan.</summary>
    internal const int MaxScans = ExplainPlanScans.MaxScans;

    /// <summary>The advice for a composed query.</summary>
    /// <param name="composed">The expression JasperFx composed, already through the guard.</param>
    /// <param name="source">The expression it was composed onto - where the walk stops.</param>
    /// <param name="idMember">The document's id member, which is the primary key rather than a JSON path.</param>
    /// <param name="table">The document table, reconciled with its physical columns.</param>
    /// <param name="indexes">What <c>pg_indexes</c> says the table has.</param>
    /// <param name="jsonName">A member name as the serializer writes it into the JSON.</param>
    /// <param name="planJson">The plan, when one was fetched.</param>
    public static LinqIndexAdvice Build(
        Expression composed,
        Expression source,
        MemberInfo? idMember,
        DocumentTableInfo table,
        IReadOnlyList<PostgresIndex> indexes,
        Func<string, string> jsonName,
        string? planJson)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(jsonName);

        var collector = new Collector(idMember, jsonName);
        Expression node = composed;

        while (!ReferenceEquals(node, source) && node is MethodCallExpression call && call.Arguments.Count == 2)
        {
            if (call.Arguments[1] is UnaryExpression { Operand: LambdaExpression lambda } && lambda.Parameters.Count == 1)
            {
                if (call.Method.Name == nameof(Queryable.Where))
                {
                    collector.Predicate(lambda);
                }
                else
                {
                    collector.Ordering(lambda);
                }
            }

            node = call.Arguments[0];
        }

        List<LinqIndexVerdict> verdicts = [];
        var worst = IndexVerdictLevel.Green;

        foreach ((string subject, DocumentPredicate predicate) in collector.Predicates)
        {
            IndexVerdict verdict = IndexAdvisor.Evaluate(table, indexes, predicate);
            verdicts.Add(new LinqIndexVerdict(subject, verdict));
            worst = verdict.Level > worst ? verdict.Level : worst;
        }

        foreach ((string subject, DocumentColumn sort) in collector.Sorts)
        {
            IndexVerdict verdict = IndexAdvisor.EvaluateSort(table, indexes, sort);
            verdicts.Add(new LinqIndexVerdict("order by " + subject, verdict));
            worst = verdict.Level > worst ? verdict.Level : worst;
        }

        IReadOnlyList<LinqPlanScan> scans = planJson is null ? [] : ReadScans(planJson);

        return new LinqIndexAdvice(worst, verdicts, scans, Note(worst, verdicts.Count, scans, table.Table));
    }

    /// <summary>The scans in an <c>EXPLAIN (FORMAT JSON)</c> plan, outermost first - read by the shared <see cref="ExplainPlanScans" />.</summary>
    internal static IReadOnlyList<LinqPlanScan> ReadScans(string planJson) =>
        [.. ExplainPlanScans.Read(planJson).Select(static x => new LinqPlanScan(x.NodeType, x.Relation, x.Index))];

    private static string? Note(IndexVerdictLevel worst, int verdicts, IReadOnlyList<LinqPlanScan> scans, string table)
    {
        if (scans.Count == 0 || verdicts == 0)
        {
            return null;
        }

        bool sequential = scans.Any(x => x.NodeType == "Seq Scan" && string.Equals(x.Relation, table, StringComparison.Ordinal));
        bool indexed = scans.Any(x => x.Index is not null);

        if (worst == IndexVerdictLevel.Green && sequential)
        {
            return "An index can serve this predicate, and Postgres still plans to read the whole table - usual for a " +
                   "small table, or a predicate that matches most of its rows.";
        }

        if (worst == IndexVerdictLevel.Red && sequential)
        {
            return "Postgres plans to read the whole table, as the advice says it must.";
        }

        if (worst == IndexVerdictLevel.Red && indexed)
        {
            return "Postgres found an index for part of this, which the advice above does not cover.";
        }

        return null;
    }

    /// <summary>Reads the members a guarded predicate filters on and a guarded ordering orders by.</summary>
    private sealed class Collector(MemberInfo? idMember, Func<string, string> jsonName)
    {
        public List<(string Subject, DocumentPredicate Predicate)> Predicates { get; } = [];

        public List<(string Subject, DocumentColumn Sort)> Sorts { get; } = [];

        private readonly HashSet<string> seen = new(StringComparer.Ordinal);

        public void Predicate(LambdaExpression lambda) => Visit(lambda.Body, lambda.Parameters[0]);

        public void Ordering(LambdaExpression lambda)
        {
            if (Chain(lambda.Body, lambda.Parameters[0]) is not { } chain)
            {
                return;
            }

            if (IsId(chain))
            {
                Sorts.Add(("id", DocumentColumn.ById));
                return;
            }

            Sorts.Add((Dotted(chain), new DocumentColumn.JsonPath([.. chain.Select(x => jsonName(x.Name))])));
        }

        private void Visit(Expression expression, ParameterExpression document)
        {
            switch (expression)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logical:
                    Visit(logical.Left, document);
                    Visit(logical.Right, document);
                    break;

                case UnaryExpression { NodeType: ExpressionType.Not } negation:
                    Visit(negation.Operand, document);
                    break;

                case BinaryExpression comparison when Operator(comparison.NodeType) is { } op:
                    if ((Chain(comparison.Left, document) ?? Chain(comparison.Right, document)) is { } chain)
                    {
                        Add(chain, IsId(chain)
                            ? new DocumentPredicate.IdEquals(string.Empty)
                            : new DocumentPredicate.FieldCompare(Path(chain), op, new SearchValue.Text(string.Empty)));
                    }

                    break;

                case MethodCallExpression { Object: { } receiver } call
                    when call.Method.DeclaringType == typeof(string)
                         && call.Method.Name is nameof(string.StartsWith) or nameof(string.EndsWith) or nameof(string.Contains):
                    if (Chain(receiver, document) is { } like)
                    {
                        Add(like, new DocumentPredicate.FieldLike(Path(like), string.Empty));
                    }

                    break;

                // `Status in ("Open", "Shipped")`: an equality on the member, as far as an index is concerned.
                case MethodCallExpression { Arguments: [NewArrayExpression or ConstantExpression { Value: Array }, var value] } call
                    when call.Method.DeclaringType == typeof(Enumerable) && call.Method.Name == nameof(Enumerable.Contains):
                    if (Chain(value, document) is { } member)
                    {
                        Add(member, IsId(member)
                            ? new DocumentPredicate.IdEquals(string.Empty)
                            : new DocumentPredicate.FieldCompare(Path(member), ComparisonOperator.Equal, new SearchValue.Text(string.Empty)));
                    }

                    break;

                case MethodCallExpression call when call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count > 0:
                    if (Chain(call.Arguments[0], document) is { } collection)
                    {
                        // A condition on an element of a JSON array is a containment question: the GIN index on data.
                        Add(collection, new DocumentPredicate.Contains("{}"));
                    }

                    break;
            }
        }

        private void Add(List<MemberInfo> chain, DocumentPredicate predicate)
        {
            string subject = Dotted(chain);

            if (seen.Add(subject + "\u001f" + predicate.GetType().Name))
            {
                Predicates.Add((subject, predicate));
            }
        }

        private bool IsId(List<MemberInfo> chain) =>
            chain.Count == 1 && idMember is not null
            && chain[0].Module == idMember.Module && chain[0].MetadataToken == idMember.MetadataToken;

        private string[] Path(List<MemberInfo> chain) => [.. chain.Select(x => jsonName(x.Name))];

        private static string Dotted(List<MemberInfo> chain) => string.Join('.', chain.Select(static x => x.Name));

        /// <summary>The member chain from the document, through any case method or conversion, or <see langword="null" />.</summary>
        private static List<MemberInfo>? Chain(Expression expression, ParameterExpression document)
        {
            Expression current = expression;

            while (true)
            {
                switch (current)
                {
                    case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                        current = convert.Operand;
                        continue;

                    case MethodCallExpression { Object: { } receiver } call when call.Method.DeclaringType == typeof(string)
                                                                            && call.Arguments.Count == 0:
                        current = receiver;
                        continue;

                    // Dynamic LINQ spells `Name > "M"` as string.Compare(Name, "M") > 0.
                    case MethodCallExpression { Object: null, Arguments.Count: 2 } compare
                        when compare.Method.DeclaringType == typeof(string)
                             && compare.Method.Name is nameof(string.Compare) or nameof(string.CompareOrdinal):
                        current = compare.Arguments[0] is ConstantExpression ? compare.Arguments[1] : compare.Arguments[0];
                        continue;
                }

                break;
            }

            List<MemberInfo> members = [];

            while (current is MemberExpression { Expression: { } owner } member)
            {
                // A framework member at the end - a string's Length, a collection's Count - is not part of the path.
                if (member.Member.DeclaringType is { } declaring
                    && (declaring == typeof(string) || declaring.Namespace?.StartsWith("System", StringComparison.Ordinal) == true))
                {
                    members.Clear();
                }
                else
                {
                    members.Add(member.Member);
                }

                current = owner;
            }

            if (!ReferenceEquals(current, document) || members.Count == 0)
            {
                return null;
            }

            members.Reverse();
            return members;
        }

        private static ComparisonOperator? Operator(ExpressionType type) => type switch
        {
            ExpressionType.Equal => ComparisonOperator.Equal,
            ExpressionType.NotEqual => ComparisonOperator.NotEqual,
            ExpressionType.GreaterThan => ComparisonOperator.GreaterThan,
            ExpressionType.GreaterThanOrEqual => ComparisonOperator.GreaterThanOrEqual,
            ExpressionType.LessThan => ComparisonOperator.LessThan,
            ExpressionType.LessThanOrEqual => ComparisonOperator.LessThanOrEqual,
            _ => null,
        };
    }
}
