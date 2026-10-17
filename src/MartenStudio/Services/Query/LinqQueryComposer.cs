using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

using Marten;
using Marten.Linq.SoftDeletes;
using Marten.Services;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Services.Query;

/// <summary>Everything one composition needs, so it can be reached with one reflective call.</summary>
/// <param name="Store">The resolved store.</param>
/// <param name="SessionOptions">
/// <c>SessionOptions.ForDatabase(tenant, database)</c>, or <c>ForDatabase(database)</c> with no tenant (hard rule 15).
/// </param>
/// <param name="ClrType">The document type - a root mapping's or a subclass's - that <c>Query&lt;T&gt;()</c> closes over.</param>
/// <param name="Adapter">JasperFx's <c>DynamicQuery</c>, reached by reflection.</param>
/// <param name="Guard">The studio's guard for this type.</param>
/// <param name="Where">The predicate text, already screened.</param>
/// <param name="OrderBy">The ordering text, already screened.</param>
/// <param name="Deleted">Marten's own soft-delete filter to apply: none, <c>MaybeDeleted()</c> or <c>IsDeleted()</c>.</param>
/// <param name="CrossTenant">Whether to apply <c>AnyTenant()</c>: a conjoined collection with no tenant in scope.</param>
/// <param name="Offset">Where the page starts.</param>
/// <param name="PageSize">The page size; one more row is asked for.</param>
/// <param name="TimestampTz">
/// For a member path read from the document: whether it is a duplicated field stored as <c>timestamptz</c>, which
/// wants a UTC <see cref="DateTime" /> as it is - everything else Marten reads as <c>timestamp without time zone</c>.
/// <see langword="null" /> treats every path as the latter.
/// </param>
internal sealed record LinqComposition(
    IDocumentStore Store,
    SessionOptions SessionOptions,
    Type ClrType,
    DynamicLinqAdapter Adapter,
    LinqExpressionGuard Guard,
    string Where,
    string OrderBy,
    DeletedFilter Deleted,
    bool CrossTenant,
    int Offset,
    int PageSize,
    Func<IReadOnlyList<string>, bool>? TimestampTz = null);

/// <summary>What composition produced: Marten's command and the expressions the advice reads, or a refusal.</summary>
/// <param name="Command">Marten's statement - built, never executed by Marten.</param>
/// <param name="Composed">The expression JasperFx composed, through the guard.</param>
/// <param name="Source">The expression it was composed onto, where every walk of the visitor's half stops.</param>
/// <param name="Refusal">Why there is no statement.</param>
/// <param name="Unexpected">An exception the pipeline did not expect, for the application log.</param>
internal sealed record ComposedLinqQuery(
    NpgsqlCommand? Command,
    Expression? Composed,
    Expression? Source,
    LinqRefusal? Refusal,
    Exception? Unexpected)
{
    /// <summary>A composition that stopped, and where.</summary>
    public static ComposedLinqQuery Refused(LinqRefusal refusal, Exception? unexpected = null) =>
        new(null, null, null, refusal, unexpected);
}

/// <summary>
/// The pipeline from screened text to Marten's statement, with no database anywhere in it: <c>Query&lt;T&gt;()</c>,
/// Marten's own tenant and soft-delete terms, the visitor's text through JasperFx and the guard, the tie-breaker,
/// the page, and Marten's SQL for all of it.
/// </summary>
/// <remarks>
/// <para>
/// Its own class so that the hostile-input theory can run the whole of it - JasperFx's parse, the studio's rule
/// inside it, the composed walk, and Marten's translation at the end - in the fast suite with no Postgres:
/// <c>DocumentStore.For</c> opens no connection, a session opens none until something runs, and nothing here runs
/// anything. The one generic method is closed over the mapped type at run time; everything inside it is ordinary
/// typed LINQ.
/// </para>
/// <para>
/// A lightweight session rather than a query session for one reason: its storage selects <c>d.id</c>
/// (<c>IdColumn.ShouldSelect</c> is false only for <c>StorageStyle.QueryOnly</c>, read at Marten 9.50.2), which is
/// what puts a link on each row. Nothing is stored on it, nothing is saved, and it is disposed before the statement
/// runs. Its <c>TenantId</c> is the one Marten's own tenant filter binds.
/// </para>
/// </remarks>
internal static class LinqQueryComposer
{
    private static readonly MethodInfo ComposeOfT = typeof(LinqQueryComposer)
        .GetMethod(nameof(ComposeCore), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Composes, closing <c>Query&lt;T&gt;()</c> over <see cref="LinqComposition.ClrType" />.</summary>
    public static ComposedLinqQuery Compose(LinqComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        try
        {
            return (ComposedLinqQuery) ComposeOfT.MakeGenericMethod(composition.ClrType).Invoke(null, [composition])!;
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    private static ComposedLinqQuery ComposeCore<T>(LinqComposition composition)
        where T : notnull
    {
        using IDocumentSession session = composition.Store.LightweightSession(composition.SessionOptions);

        IQueryable<T> query = session.Query<T>();

        query = composition.Deleted switch
        {
            DeletedFilter.Include => query.Where(static x => x.MaybeDeleted()),
            DeletedFilter.Only => query.Where(static x => x.IsDeleted()),
            _ => query,
        };

        if (composition.CrossTenant)
        {
            query = query.Where(static x => x.AnyTenant());
        }

        // Everything above this point is the studio's; everything composed onto it is the visitor's, and every walk
        // of the visitor's half stops here.
        Expression source = query.Expression;

        DynamicLinqApplied<T> applied = composition.Adapter.Apply(query, composition.Where, composition.OrderBy, composition.Guard);

        if (applied.Refusal is not null || applied.Query is null)
        {
            return ComposedLinqQuery.Refused(applied.Refusal ?? new LinqRefusal(LinqStage.Parse, LinqClause.None, "The query was not accepted."));
        }

        // The second walk, over what JasperFx actually returned - independent of whether it called its shape rules.
        if (composition.Guard.CheckComposed(applied.Query.Expression, source) is { } refusal)
        {
            return ComposedLinqQuery.Refused(new LinqRefusal(LinqStage.Guard, LinqClause.None, "The query " + refusal));
        }

        // Dates, first: the value the visitor typed. A literal with no offset is meant as UTC, and this JasperFx may
        // have read it in the server's time zone instead (DynamicLinqAdapter.ReadsDatesInServerTime) - so each such
        // constant is put back to what its literal names, read as UTC, which is what JasperFx's own converter would
        // have produced. Two literals the server read as one instant but that name different ones cannot be told
        // apart, and are refused rather than guessed.
        IQueryable<T> dated = applied.Query;

        if (composition.Adapter.ReadsDatesInServerTime)
        {
            if (DateLiterals.Corrections(composition.Where) is not { } corrections)
            {
                return ComposedLinqQuery.Refused(new LinqRefusal(LinqStage.Parse, LinqClause.Where,
                    "The predicate has two dates that this server reads as the same moment but that name different ones - " +
                    "one with an offset and one without. Give both an offset, as in \"2024-01-01T00:00:00Z\"."));
            }

            dated = DateLiterals.Apply(dated, corrections);
        }

        // In-lists: Marten binds an array value as one parameter (= ANY) but cannot translate the array initialiser
        // Dynamic LINQ writes for `Status in ("Open", "Shipped")`, so the array is built here from the literals'
        // values - set into it one by one, never compiled.
        IQueryable<T> listed = InLists.Adapt(dated);

        // Then their Kind: Marten binds a DateTime member as `timestamp without time zone`, which Npgsql refuses a UTC
        // value for. Marten's own diagnostics read rewrites the constant's Kind (DocumentStore.DateTimeKindAdapter,
        // 9.48+, internal); this is the same rewrite, and an in-list's dates get it too. None of these steps changes
        // anything but a literal's value, its Kind or the form an in-list's literals take, and the result is walked
        // again.
        IQueryable<T> composedQuery = DateTimeKinds.Adapt(listed, composition.TimestampTz);
        Expression expression = composedQuery.Expression;

        if (!ReferenceEquals(expression, applied.Query.Expression)
            && composition.Guard.CheckComposed(expression, source) is { } adapted)
        {
            return ComposedLinqQuery.Refused(new LinqRefusal(LinqStage.Guard, LinqClause.None, "The query " + adapted));
        }

        IQueryable<T> ordered = string.IsNullOrWhiteSpace(composition.OrderBy)
            ? composedQuery.OrderBySql(LinqQuerySql.IdTieBreaker)
            : composedQuery.ThenBySql(LinqQuerySql.IdTieBreaker);

        if (composition.CrossTenant)
        {
            ordered = ordered.ThenBySql(LinqQuerySql.TenantTieBreaker);
        }

        IQueryable<T> page = ordered.Skip(composition.Offset).Take(composition.PageSize + 1);

        MartenLinqStatement statement;

        try
        {
            // The first time Marten sees the expression.
            statement = MartenLinqCommand.Build(page, session.TenantId);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return TranslationFailure(exception);
        }

        if (statement.DocumentTypes.Any(static x => x != typeof(T)))
        {
            return ComposedLinqQuery.Refused(new LinqRefusal(LinqStage.Guard, LinqClause.None,
                "The query would read another document type besides " + typeof(T).Name + ", which LINQ mode does not allow."));
        }

        return new ComposedLinqQuery(statement.Command, expression, source, null, null);
    }

    /// <summary>
    /// Marten refusing a shape it cannot translate - in Marten's own first line when it is Marten's own refusal, and
    /// as a type name otherwise.
    /// </summary>
    private static ComposedLinqQuery TranslationFailure(Exception exception)
    {
        if (exception is Marten.Exceptions.BadLinqExpressionException or NotSupportedException)
        {
            string first = exception.Message.Split('\n', 2)[0].Trim();

            return ComposedLinqQuery.Refused(new LinqRefusal(LinqStage.Translate, LinqClause.None,
                "Marten cannot translate this query: " + (first.Length <= 300 ? first : first[..300] + "…")));
        }

        return ComposedLinqQuery.Refused(
            new LinqRefusal(LinqStage.Translate, LinqClause.None,
                "Marten could not translate this query (" + exception.GetType().Name + "). The application log has the details."),
            exception);
    }

    /// <summary>
    /// Each <see cref="DateTime" /> constant Dynamic LINQ read from a literal in the server's time zone, put back to the
    /// value the literal names read as UTC (unless it carries an offset of its own).
    /// </summary>
    /// <remarks>
    /// Keyed on the value Dynamic LINQ produced, which is recomputed from each double-quoted string the screen
    /// accepted exactly as its <c>DateTimeOffsetConverter</c> reads it - <c>DateTimeOffset.Parse</c> under the
    /// invariant culture, then <c>UtcDateTime</c>. A predicate has no other source of <see cref="DateTime" />
    /// constants: the guard refuses a constructor, a static member and every method that could make one.
    /// </remarks>
    internal sealed class DateLiterals : ExpressionVisitor
    {
        private readonly IReadOnlyDictionary<DateTime, DateTime> corrections;

        private DateLiterals(IReadOnlyDictionary<DateTime, DateTime> corrections) => this.corrections = corrections;

        /// <summary>
        /// For each date the predicate's strings name, the value Dynamic LINQ produced and the one meant - or
        /// <see langword="null" /> when two literals Dynamic LINQ read as the same instant mean different ones.
        /// </summary>
        public static Dictionary<DateTime, DateTime>? Corrections(string? where)
        {
            var corrections = new Dictionary<DateTime, DateTime>();

            foreach (string literal in LinqTextScreen.StringLiterals(where))
            {
                string text = literal.Trim();

                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset local)
                    || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTime meant))
                {
                    continue;
                }

                // Recorded even when the two agree - "2023-12-31T22:00:00Z" beside "2024-01-01" on a server two hours
                // east is exactly the pair that cannot be told apart, and it is told apart here or nowhere.
                DateTime read = local.UtcDateTime;

                if (corrections.TryGetValue(read, out DateTime earlier) && earlier != meant)
                {
                    return null;
                }

                corrections[read] = meant;
            }

            return corrections;
        }

        public static IQueryable<T> Apply<T>(IQueryable<T> query, IReadOnlyDictionary<DateTime, DateTime> corrections)
        {
            if (corrections.All(static x => x.Key == x.Value))
            {
                return query;
            }

            Expression adapted = new DateLiterals(corrections).Visit(query.Expression)!;

            return ReferenceEquals(adapted, query.Expression) ? query : query.Provider.CreateQuery<T>(adapted);
        }

        protected override Expression VisitConstant(ConstantExpression node) =>
            node.Value is DateTime value && corrections.TryGetValue(value, out DateTime meant) && meant != value
                ? Expression.Constant(DateTime.SpecifyKind(meant, DateTimeKind.Utc), node.Type)
                : node;
    }

    /// <summary>
    /// Each in-list's array initialiser made the array it initialises, from the values of its literals.
    /// </summary>
    /// <remarks>
    /// Only an initialiser whose every element is a constant - or a constant converted to its own nullable type, whose
    /// value is the constant's - is rewritten, which is every in-list the guard accepts; anything else is left as it is,
    /// and Marten then refuses to translate it.
    /// </remarks>
    internal sealed class InLists : ExpressionVisitor
    {
        public static IQueryable<T> Adapt<T>(IQueryable<T> query)
        {
            Expression adapted = new InLists().Visit(query.Expression)!;

            return ReferenceEquals(adapted, query.Expression) ? query : query.Provider.CreateQuery<T>(adapted);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Enumerable)
                && node.Method.Name == nameof(Enumerable.Contains)
                && node.Arguments is [NewArrayExpression { NodeType: ExpressionType.NewArrayInit } set, var value]
                && set.Type.GetElementType() is { } element
                && Values(set, element) is { } array)
            {
                return node.Update(null, [Expression.Constant(array, set.Type), Visit(value)]);
            }

            return base.VisitMethodCall(node);
        }

        private static Array? Values(NewArrayExpression set, Type element)
        {
            var array = Array.CreateInstance(element, set.Expressions.Count);

            for (int i = 0; i < set.Expressions.Count; i++)
            {
                Expression item = set.Expressions[i];

                if (item is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } convert
                    && Nullable.GetUnderlyingType(convert.Type) == convert.Operand.Type)
                {
                    item = convert.Operand;
                }

                if (item is not ConstantExpression constant)
                {
                    return null;
                }

                array.SetValue(constant.Value, i);
            }

            return array;
        }
    }

    /// <summary>
    /// A UTC <see cref="DateTime" /> constant compared with a <see cref="DateTime" /> member of the document, made the
    /// Kind Marten will bind it as - Marten 9.48+'s <c>DateTimeKindAdapter</c>, which is internal, applied the same way.
    /// </summary>
    /// <remarks>
    /// The wall-clock value is kept and only the Kind dropped: a UTC <see cref="DateTime" /> is serialized with its
    /// <c>Z</c>, and the cast to <c>timestamp without time zone</c> keeps that wall clock. A duplicated field created
    /// as <c>timestamptz</c> is left alone, because Npgsql wants exactly the UTC value there. A member inside a
    /// collection's lambda is left alone too, as Marten's adapter leaves it.
    /// </remarks>
    private sealed class DateTimeKinds : ExpressionVisitor
    {
        private readonly Type document;
        private readonly Func<IReadOnlyList<string>, bool>? timestampTz;

        private DateTimeKinds(Type document, Func<IReadOnlyList<string>, bool>? timestampTz)
        {
            this.document = document;
            this.timestampTz = timestampTz;
        }

        public static IQueryable<T> Adapt<T>(IQueryable<T> query, Func<IReadOnlyList<string>, bool>? timestampTz)
        {
            Expression adapted = new DateTimeKinds(typeof(T), timestampTz).Visit(query.Expression)!;

            return ReferenceEquals(adapted, query.Expression) ? query : query.Provider.CreateQuery<T>(adapted);
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual)
            {
                if (IsUnspecifiedMember(node.Left) && UtcConstant(node.Right) is { } right)
                {
                    return node.Update(node.Left, node.Conversion, right);
                }

                if (IsUnspecifiedMember(node.Right) && UtcConstant(node.Left) is { } left)
                {
                    return node.Update(left, node.Conversion, node.Right);
                }
            }

            return base.VisitBinary(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // An in-list of dates, after InLists has made it an array value: the same rewrite, element by element.
            if (node.Method.DeclaringType == typeof(Enumerable)
                && node.Method.Name == nameof(Enumerable.Contains)
                && node.Arguments is [ConstantExpression { Value: Array values } set, var value]
                && (set.Type == typeof(DateTime[]) || set.Type == typeof(DateTime?[]))
                && IsUnspecifiedMember(value))
            {
                var unspecified = Array.CreateInstance(set.Type.GetElementType()!, values.Length);

                for (int i = 0; i < values.Length; i++)
                {
                    unspecified.SetValue(
                        values.GetValue(i) is DateTime { Kind: DateTimeKind.Utc } utc ? DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) : values.GetValue(i),
                        i);
                }

                return node.Update(null, [Expression.Constant(unspecified, set.Type), value]);
            }

            return base.VisitMethodCall(node);
        }

        private bool IsUnspecifiedMember(Expression expression)
        {
            if (Unconvert(expression) is not MemberExpression member
                || (member.Type != typeof(DateTime) && member.Type != typeof(DateTime?)))
            {
                return false;
            }

            List<string> path = [];
            Expression? current = member;

            while (current is MemberExpression link)
            {
                path.Insert(0, link.Member.Name);
                current = link.Expression;
            }

            return current is ParameterExpression parameter
                   && parameter.Type == document
                   && timestampTz?.Invoke(path) != true;
        }

        private static ConstantExpression? UtcConstant(Expression expression) =>
            Unconvert(expression) is ConstantExpression { Value: DateTime { Kind: DateTimeKind.Utc } utc }
                ? Expression.Constant(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), expression.Type)
                : null;

        private static Expression Unconvert(Expression expression) =>
            expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: null } convert
                ? Unconvert(convert.Operand)
                : expression;
    }
}
