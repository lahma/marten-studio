using System.Linq.Expressions;
using System.Reflection;

namespace MartenStudio.Services.Query;

/// <summary>
/// The third of LINQ mode's walls: the studio's own walk of every parsed clause, refusing any node that is not
/// on its own allow-list - before Marten sees the expression.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it protects against.</b> Marten's LINQ provider evaluates, in this process, every part of an
/// expression that does not reference the document: <c>SimpleExpression</c> sends any operand with no parameter at
/// its start to <c>LinqInternalExtensions.ReduceToConstant</c>, which reads static fields and properties by
/// reflection and otherwise compiles the subtree with FastExpressionCompiler and invokes it (read at Marten
/// <c>V9.39.0</c> and <c>V9.50.2</c>). So <c>Status == System.IO.File.ReadAllText("c:/x")</c>, were it ever to
/// parse, would be run by the server - the shape of CVE-2023-32571 against System.Linq.Dynamic.Core. JasperFx's
/// <c>DynamicQuery</c> already walks every clause against an allow-list of its own (JasperFx 2.84.0 to 2.89.0,
/// unchanged); this is a second, independent one, so that no single library's allow-list is the only thing in the
/// way.
/// </para>
/// <para>
/// <b>What it allows - and it is a closed list, every other node is refused:</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// a member read only as part of a chain that starts at a lambda parameter - the document, or the element inside
/// <c>Any</c> / <c>All</c> / <c>Count</c> - and only of members in the document's own graph
/// (<see cref="LinqDocumentGraph" />), plus a string's or an array's length, a collection's count and a
/// nullable's <c>HasValue</c> / <c>Value</c>. No static member, and no member of a constant;
/// </description></item>
/// <item><description>
/// constants of the literal types - text (at most <see cref="LinqQueryLimits.MaxLiteralLength" /> characters), the
/// numbers, <see cref="bool" />, <see cref="char" />, <see cref="Guid" />, the date and time types, an enum that is
/// the type of a member in the graph, and <see cref="StringComparison" /> - and <see langword="null" />. No closure,
/// array, type, delegate, queryable or any other object;
/// </description></item>
/// <item><description>
/// <c>and</c>, <c>or</c> and <c>not</c> over booleans; the six comparisons, each with at least one side that
/// reads the document; <c>%</c> of a member by a constant; a conversion between the scalar types with no
/// user-defined operator; a negated numeric constant; a framework operator method only on the framework's own
/// scalar types (<c>string.op_Equality</c>, <c>decimal.op_GreaterThan</c>, ...);
/// </description></item>
/// <item><description>
/// on a string that reads the document: <c>StartsWith</c>, <c>EndsWith</c>, <c>Contains</c> and <c>Equals</c> with
/// a constant (and optionally a <see cref="StringComparison" />), <c>ToLower</c> / <c>ToUpper</c> and their
/// invariant forms, <c>CompareTo</c>, and <c>string.Compare</c> / <c>string.CompareOrdinal</c> of it with a
/// constant (Dynamic LINQ's own spelling of <c>&lt;</c> and <c>&gt;</c> on strings);
/// </description></item>
/// <item><description>
/// on a collection member: <c>Any()</c>, <c>Any(…)</c>, <c>All(…)</c>, <c>Count()</c>, <c>Count(…)</c> and
/// <c>Contains(constant)</c>, a lambda only as their argument, whose own parameter is the element;
/// </description></item>
/// <item><description>
/// an in-list, <c>Enumerable.Contains</c> over an array of literals of the member's type with a member of the
/// document as the value looked for - as Dynamic LINQ's initialiser, or as the array value the composer builds
/// from it (<see cref="LinqQueryComposer.InLists" />).
/// </description></item>
/// </list>
/// <para>
/// So the only parts of an accepted expression that do not reference the document are literals, conversions
/// of literals, negated literals and an in-list's array of literals: nothing Marten evaluates in process can do
/// more than read a constant.
/// </para>
/// <para>
/// <b>Where it runs - twice.</b> As the <em>first</em> shape rule of the <c>DynamicQueryPolicy</c> handed to
/// JasperFx (<see cref="RuleFor" />): JasperFx shows a rule each node top-down, starting at a clause's body, so the
/// first node the rule sees is the root of a clause and it validates the whole subtree there - before JasperFx's
/// own <c>SqlNullSemantics</c> rule, which reads values out of member-of-constant shapes by reflection, ever looks
/// at a descendant. And again, independently, over the whole composed expression JasperFx returns
/// (<see cref="CheckComposed" />), so that a JasperFx that stopped calling its shape rules, or composed something
/// other than <c>Where</c> and <c>OrderBy</c>, is still caught before Marten.
/// </para>
/// <para>
/// <b>Enums stored as names.</b> Marten's own rule (<c>DocumentStore.StringEnumMembers</c>, 9.48+) is internal, so
/// the studio applies the same one: an ordering comparison, or an ordering, on an enum that this store keeps as
/// its name would be alphabetical rather than by value, and is refused. A duplicated field decides by its column
/// type; anything read from the JSON by the mapping's <c>EnumStorage</c>.
/// </para>
/// </remarks>
internal sealed class LinqExpressionGuard
{
    private static readonly HashSet<Type> OperatorTypes =
    [
        typeof(string), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly),
        typeof(TimeSpan), typeof(Guid),
    ];

    private static readonly HashSet<string> StringInstanceMethods = new(StringComparer.Ordinal)
    {
        nameof(string.StartsWith), nameof(string.EndsWith), nameof(string.Contains), nameof(string.Equals),
        nameof(string.ToLower), nameof(string.ToUpper), nameof(string.ToLowerInvariant), nameof(string.ToUpperInvariant),
        nameof(string.CompareTo),
    };

    private static readonly HashSet<string> CaseMethods = new(StringComparer.Ordinal)
    {
        nameof(string.ToLower), nameof(string.ToUpper), nameof(string.ToLowerInvariant), nameof(string.ToUpperInvariant),
    };

    private static readonly HashSet<string> EnumerableMethods = new(StringComparer.Ordinal)
    {
        nameof(Enumerable.Any), nameof(Enumerable.All), nameof(Enumerable.Count), nameof(Enumerable.Contains),
    };

    /// <summary>The <see cref="Queryable" /> operators a composed query may be made of, by metadata token.</summary>
    private static readonly Dictionary<int, LinqClause> QueryableOperators = BuildQueryableOperators();

    private readonly LinqDocumentGraph graph;
    private readonly Func<IReadOnlyList<string>, bool?> duplicatedEnumAsName;
    private readonly bool jsonEnumsAsName;

    /// <summary>Creates the guard for one document type.</summary>
    /// <param name="graph">The members a predicate may read.</param>
    /// <param name="duplicatedEnumAsName">
    /// For a member path read from the document itself: <see langword="true" /> when it is a duplicated field whose
    /// column holds the enum's name, <see langword="false" /> when one holds its number, and <see langword="null" />
    /// when it is not a duplicated field and is read from the JSON.
    /// </param>
    /// <param name="jsonEnumsAsName">Whether the mapping writes enums into the JSON as names (<c>EnumStorage.AsString</c>).</param>
    public LinqExpressionGuard(
        LinqDocumentGraph graph,
        Func<IReadOnlyList<string>, bool?> duplicatedEnumAsName,
        bool jsonEnumsAsName)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(duplicatedEnumAsName);

        this.graph = graph;
        this.duplicatedEnumAsName = duplicatedEnumAsName;
        this.jsonEnumsAsName = jsonEnumsAsName;
    }

    /// <summary>
    /// A shape rule for JasperFx's policy, for one clause: the first node of each clause body it is shown is
    /// validated whole, and every node of a validated body passes from then on.
    /// </summary>
    /// <remarks>
    /// One per <c>Apply</c> call, never shared: it remembers what it validated. It never throws - a check that
    /// fails for a reason of its own refuses.
    /// </remarks>
    public Func<Expression, string?> RuleFor(LinqClause clause)
    {
        var validated = new HashSet<Expression>(ReferenceEqualityComparer.Instance);

        return node =>
        {
            try
            {
                if (validated.Contains(node))
                {
                    return null;
                }

                // A node this rule has not seen is the body of a clause JasperFx has just parsed: the Where
                // predicate, or one ordering key. Its parameter is the document's - the one free parameter in it.
                if (FreeRootParameter(node, out string? problem) is not { } parameter)
                {
                    return problem;
                }

                var walker = new Walker(this, parameter);
                string? verdict = clause == LinqClause.OrderBy ? walker.OrderKey(node) : walker.Predicate(node);

                if (verdict is null)
                {
                    // Every node of an accepted body passes when JasperFx shows it next - recorded by a plain walk of
                    // the whole subtree, so a node the guard's own walk reached by a different route is not taken
                    // for the root of a new clause.
                    new Recorder(validated).Visit(node);
                }

                return verdict;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return "could not be checked by the studio, so it was refused.";
            }
        };
    }

    /// <summary>
    /// Walks everything <paramref name="composed" /> adds on top of <paramref name="source" />: it must be nothing
    /// but <c>Queryable.Where</c>, <c>OrderBy</c>, <c>OrderByDescending</c>, <c>ThenBy</c> and
    /// <c>ThenByDescending</c> over quoted lambdas of the document type, each of which passes the guard.
    /// </summary>
    /// <returns>Why it is refused, or <see langword="null" />.</returns>
    public string? CheckComposed(Expression composed, Expression source)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(source);

        Expression node = composed;
        int calls = 0;

        while (!ReferenceEquals(node, source))
        {
            if (++calls > LinqQueryLimits.MaxOrderKeys + 1)
            {
                return "was composed into more operators than a predicate and an ordering make.";
            }

            if (node is not MethodCallExpression { Method.IsGenericMethod: true } call
                || call.Method.DeclaringType != typeof(Queryable)
                || call.Arguments.Count != 2
                || !QueryableOperators.TryGetValue(call.Method.GetGenericMethodDefinition().MetadataToken, out LinqClause clause)
                || call.Method.GetGenericArguments()[0] != graph.Root)
            {
                return "was composed into an operator other than Where, OrderBy and ThenBy.";
            }

            if (call.Arguments[1] is not UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda }
                || lambda.Parameters.Count != 1
                || lambda.Parameters[0].Type != graph.Root)
            {
                return "was composed with an argument that is not a lambda over the document.";
            }

            var walker = new Walker(this, lambda.Parameters[0]);
            string? refusal = clause == LinqClause.OrderBy ? walker.OrderKey(lambda.Body) : walker.Predicate(lambda.Body);

            if (refusal is not null)
            {
                return refusal;
            }

            node = call.Arguments[0];
        }

        return null;
    }

    /// <summary>The guard's verdict on one predicate lambda, as a test drives it.</summary>
    public string? CheckPredicate(LambdaExpression lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);

        return lambda.Parameters.Count == 1 && lambda.Parameters[0].Type == graph.Root
            ? new Walker(this, lambda.Parameters[0]).Predicate(lambda.Body)
            : "is not a predicate over the document.";
    }

    /// <summary>The guard's verdict on one ordering key lambda, as a test drives it.</summary>
    public string? CheckOrderKey(LambdaExpression lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);

        return lambda.Parameters.Count == 1 && lambda.Parameters[0].Type == graph.Root
            ? new Walker(this, lambda.Parameters[0]).OrderKey(lambda.Body)
            : "is not an ordering over the document.";
    }

    /// <summary>
    /// The one parameter a clause body reads that no lambda inside it binds - the document - or the reason there
    /// is not exactly one of the document's type.
    /// </summary>
    private ParameterExpression? FreeRootParameter(Expression body, out string? problem)
    {
        var finder = new FreeParameters();
        finder.Visit(body);

        problem = null;

        if (finder.Free.Count == 0)
        {
            // A clause that reads nothing - `true` - still needs a parameter to walk against; a fresh one of the
            // document's type is bound by nothing, so nothing in the body can match it.
            return Expression.Parameter(graph.Root, "document");
        }

        if (finder.Free.Count > 1 || finder.Free[0].Type != graph.Root)
        {
            problem = "reads something that is not the document.";
            return null;
        }

        return finder.Free[0];
    }

    private bool EnumStoredAsName(IReadOnlyList<string> pathFromDocument, bool readFromDocument) =>
        readFromDocument
            ? duplicatedEnumAsName(pathFromDocument) ?? jsonEnumsAsName
            : jsonEnumsAsName;

    private static Dictionary<int, LinqClause> BuildQueryableOperators()
    {
        var operators = new Dictionary<int, LinqClause>();

        foreach (MethodInfo method in typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!method.IsGenericMethodDefinition || method.GetParameters().Length != 2)
            {
                continue;
            }

            // The lambda overloads with one parameter: Expression<Func<TSource, bool>> for Where (not the indexed
            // Func<TSource, int, bool>), Expression<Func<TSource, TKey>> for the orderings.
            Type lambda = method.GetParameters()[1].ParameterType;
            if (!lambda.IsGenericType
                || lambda.GetGenericTypeDefinition() != typeof(Expression<>)
                || lambda.GetGenericArguments()[0] is not { IsGenericType: true } func
                || func.GetGenericTypeDefinition() != typeof(Func<,>))
            {
                continue;
            }

            switch (method.Name)
            {
                case nameof(Queryable.Where):
                    operators[method.MetadataToken] = LinqClause.Where;
                    break;

                case nameof(Queryable.OrderBy):
                case nameof(Queryable.OrderByDescending):
                case nameof(Queryable.ThenBy):
                case nameof(Queryable.ThenByDescending):
                    operators[method.MetadataToken] = LinqClause.OrderBy;
                    break;
            }
        }

        return operators;
    }

    /// <summary>What a refusal says in place of a type it may not name.</summary>
    internal const string UnnamedType = "a type LINQ mode does not query";

    /// <summary>
    /// A type's name for a refusal - only when the visitor may see it: a framework type (public knowledge), an enum
    /// of the graph's, or a type of the document's own graph that the host does not hide. Anything else - a host type
    /// the graph leaves out, a hidden document type - is <see cref="UnnamedType" />, because the refusal is shown to
    /// whoever typed the text and a host's type names are not theirs to learn this way.
    /// </summary>
    private string Name(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;
        string suffix = ReferenceEquals(underlying, type) ? string.Empty : "?";

        bool visible = IsFramework(underlying)
                       || (underlying.IsEnum ? graph.IsGraphEnum(underlying) : !graph.IsHidden(underlying) && graph.IsGraphType(underlying));

        return visible ? underlying.Name + suffix : UnnamedType;
    }

    /// <summary><c>Owner.Member</c> for a refusal, or a phrase naming neither when the owner may not be named.</summary>
    private string Qualified(Type? owner, string member) =>
        owner is not null && Name(owner) is var name && !string.Equals(name, UnnamedType, StringComparison.Ordinal)
            ? name + "." + member
            : "a member of " + UnnamedType;

    private static bool IsFramework(Type type) =>
        type.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
        && (type.Assembly == typeof(object).Assembly || type.Assembly.GetName().Name?.StartsWith("System.", StringComparison.Ordinal) == true);

    private static Expression StripConversions(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Method: null } convert)
        {
            expression = convert.Operand;
        }

        return expression;
    }

    /// <summary>One walk of one clause body.</summary>
    private sealed class Walker
    {
        private readonly LinqExpressionGuard guard;
        private readonly ParameterExpression document;
        private readonly List<ParameterExpression> elements = [];
        private int nodes;

        public Walker(LinqExpressionGuard guard, ParameterExpression document)
        {
            this.guard = guard;
            this.document = document;
        }

        private LinqDocumentGraph Graph => guard.graph;

        /// <summary>A predicate body: a boolean the document decides.</summary>
        public string? Predicate(Expression body)
        {
            if (body.Type != typeof(bool))
            {
                return "is not a condition: it produces a " + guard.Name(body.Type) + " rather than true or false.";
            }

            return Visit(body, 0);
        }

        /// <summary>An ordering key body: a member path to a scalar, and nothing else.</summary>
        public string? OrderKey(Expression body)
        {
            Expression key = StripConversions(body);

            if (key is not MemberExpression member)
            {
                return "orders by something other than a member of the document.";
            }

            if (Chain(member, 0, out MemberChain? chain) is { } refusal)
            {
                return refusal;
            }

            if (!chain!.FromDocument)
            {
                return "orders by something other than a member of the document.";
            }

            if (!LinqDocumentGraph.IsScalar(member.Type))
            {
                return "orders by " + chain.Path + ", which is not a value that can be sorted.";
            }

            if (IsEnum(member.Type) && guard.EnumStoredAsName(chain.Members, readFromDocument: true))
            {
                return "orders by the enum member " + chain.Path + ", which this store keeps as its name, so the order would be " +
                       "alphabetical rather than by value. Order by another member, or filter on it with == or in.";
            }

            return nodes > LinqQueryLimits.MaxNodes ? TooComplex() : null;
        }

        private string? Visit(Expression? expression, int depth)
        {
            if (expression is null)
            {
                return "has an empty part.";
            }

            if (++nodes > LinqQueryLimits.MaxNodes)
            {
                return TooComplex();
            }

            if (depth > LinqQueryLimits.MaxExpressionDepth)
            {
                return "nests too deeply.";
            }

            if (!TypeAllowed(expression))
            {
                return "produces a value of type " + guard.Name(expression.Type) + ", which a query predicate does not use.";
            }

            return expression switch
            {
                ParameterExpression parameter => VisitParameter(parameter),
                ConstantExpression constant => VisitConstant(constant),
                MemberExpression member => Chain(member, depth, out _),
                UnaryExpression unary => VisitUnary(unary, depth),
                BinaryExpression binary => VisitBinary(binary, depth),
                MethodCallExpression call => VisitCall(call, depth),
                _ => "uses an expression of kind " + expression.NodeType + ", which LINQ mode does not allow.",
            };
        }

        private bool TypeAllowed(Expression expression)
        {
            Type type = expression.Type;

            if (type == typeof(bool) || type == typeof(bool?) || type == typeof(StringComparison) || LinqDocumentGraph.IsScalar(type))
            {
                return true;
            }

            // The untyped null constant: Dynamic LINQ's `null` before it is promoted to the member's type.
            if (type == typeof(object))
            {
                return expression is ConstantExpression { Value: null };
            }

            return LinqDocumentGraph.Classify(type) is LinqTypeKind.Object or LinqTypeKind.Collection && !Graph.IsHidden(type);
        }

        private string? VisitParameter(ParameterExpression parameter)
        {
            // A parameter on its own - not as the start of a member chain - is the element of a collection of
            // scalars inside Any(...): Tags.Any(it == "vip"). The document itself is never a value.
            return elements.Contains(parameter) && LinqDocumentGraph.IsScalar(parameter.Type)
                ? null
                : "uses the document or an element as a value; compare one of its members instead.";
        }

        private string? VisitConstant(ConstantExpression constant)
        {
            if (constant.Value is null)
            {
                return constant.Type.IsValueType && Nullable.GetUnderlyingType(constant.Type) is null
                    ? "has a null of a type that cannot be null."
                    : null;
            }

            Type actual = constant.Value.GetType();
            Type declared = Nullable.GetUnderlyingType(constant.Type) ?? constant.Type;

            if (actual != declared)
            {
                return "has a constant whose value is not of its declared type.";
            }

            if (actual.IsEnum)
            {
                return actual == typeof(StringComparison) || Graph.IsGraphEnum(actual)
                    ? null
                    : "has a constant of the enum " + guard.Name(actual) + ", which no member of this document has.";
            }

            if (!LinqDocumentGraph.IsScalar(actual))
            {
                return "has a constant of type " + guard.Name(actual) + ", which is not a literal.";
            }

            return constant.Value is string { Length: > LinqQueryLimits.MaxLiteralLength }
                ? "has a string longer than " + LinqQueryLimits.MaxLiteralLength + " characters."
                : null;
        }

        private string? VisitUnary(UnaryExpression unary, int depth)
        {
            switch (unary.NodeType)
            {
                case ExpressionType.Not when unary.Method is null && (unary.Type == typeof(bool) || unary.Type == typeof(bool?)):
                    return Visit(unary.Operand, depth + 1);

                case ExpressionType.Convert or ExpressionType.ConvertChecked when unary.Method is null:
                    return ConversionAllowed(unary.Operand.Type, unary.Type)
                        ? Visit(unary.Operand, depth + 1)
                        : "converts " + guard.Name(unary.Operand.Type) + " to " + guard.Name(unary.Type) + ", which LINQ mode does not allow.";

                case ExpressionType.ArrayLength:
                    return unary.Operand is MemberExpression array
                        ? Chain(array, depth + 1, out _)
                        : "reads the length of something that is not a member of the document.";

                case ExpressionType.Negate or ExpressionType.NegateChecked when unary.Method is null:
                    return StripConversions(unary.Operand) is ConstantExpression { Value: not null } literal
                           && LinqDocumentGraph.IsScalar(literal.Type)
                           && literal.Type != typeof(string)
                        ? Visit(unary.Operand, depth + 1)
                        : "negates something that is not a number.";

                default:
                    return unary.Method is not null
                        ? "calls the operator " + guard.Qualified(unary.Method.DeclaringType, unary.Method.Name) + ", which LINQ mode does not allow."
                        : "uses an expression of kind " + unary.NodeType + ", which LINQ mode does not allow.";
            }
        }

        private string? VisitBinary(BinaryExpression binary, int depth)
        {
            if (binary.Conversion is not null)
            {
                return "uses a conversion lambda inside an operator, which LINQ mode does not allow.";
            }

            switch (binary.NodeType)
            {
                case ExpressionType.AndAlso or ExpressionType.OrElse when binary.Method is null:
                    return Visit(binary.Left, depth + 1) ?? Visit(binary.Right, depth + 1);

                case ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan
                    or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual:
                {
                    if (!OperatorAllowed(binary.Method))
                    {
                        return "compares with the operator " + guard.Qualified(binary.Method!.DeclaringType, binary.Method.Name) +
                               ", which LINQ mode does not allow.";
                    }

                    if (!Reads(binary.Left) && !Reads(binary.Right))
                    {
                        return "compares two values neither of which is a member of the document.";
                    }

                    if ((Visit(binary.Left, depth + 1) ?? Visit(binary.Right, depth + 1)) is { } refusal)
                    {
                        return refusal;
                    }

                    return binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual
                        ? null
                        : EnumRange(binary.Left) ?? EnumRange(binary.Right);
                }

                case ExpressionType.Modulo:
                    if (!OperatorAllowed(binary.Method))
                    {
                        return "uses an operator LINQ mode does not allow.";
                    }

                    return Reads(binary.Left) && StripConversions(binary.Right) is ConstantExpression
                        ? Visit(binary.Left, depth + 1) ?? Visit(binary.Right, depth + 1)
                        : "uses % other than as a member % a number.";

                default:
                    return "uses the operator " + binary.NodeType + ", which LINQ mode does not allow.";
            }
        }

        private string? EnumRange(Expression side)
        {
            if (StripConversions(side) is not MemberExpression member || !IsEnum(member.Type))
            {
                return null;
            }

            // Already validated as a chain by the caller; read again for its path and where it starts.
            if (Chain(member, 0, out MemberChain? chain, count: false) is not null || chain is null)
            {
                return null;
            }

            return guard.EnumStoredAsName(chain.Members, chain.FromDocument)
                ? "compares the enum member " + chain.Path + " with an ordering operator, but this store keeps it as its name, " +
                  "so the comparison would be alphabetical rather than by value. Use == / != or in (…) with the values you want."
                : null;
        }

        private string? VisitCall(MethodCallExpression call, int depth)
        {
            MethodInfo method = call.Method;

            if (method.DeclaringType == typeof(string))
            {
                return method.IsStatic ? VisitStringCompare(call, depth) : VisitStringMethod(call, depth);
            }

            if (method.DeclaringType == typeof(Enumerable) && method.IsStatic && method.IsGenericMethod && EnumerableMethods.Contains(method.Name))
            {
                return VisitEnumerable(call, depth);
            }

            if (!method.IsStatic
                && method.Name == nameof(ICollection<int>.Contains)
                && method.GetParameters().Length == 1
                && call.Object is MemberExpression owner
                && LinqDocumentGraph.Classify(owner.Type) == LinqTypeKind.Collection
                && method.DeclaringType is { IsGenericType: true } declaring
                && declaring.Namespace is "System.Collections.Generic")
            {
                return Chain(owner, depth + 1, out _) ?? Literal(call.Arguments[0], depth);
            }

            return "calls " + guard.Qualified(method.DeclaringType, method.Name) + ", which LINQ mode does not allow.";
        }

        private string? VisitStringMethod(MethodCallExpression call, int depth)
        {
            MethodInfo method = call.Method;
            ParameterInfo[] parameters = method.GetParameters();

            if (!StringInstanceMethods.Contains(method.Name))
            {
                return "calls string." + method.Name + ", which LINQ mode does not allow.";
            }

            bool shapeAllowed = CaseMethods.Contains(method.Name)
                ? parameters.Length == 0
                : parameters.Length switch
                {
                    1 => parameters[0].ParameterType == typeof(string)
                         || (parameters[0].ParameterType == typeof(char) && method.Name != nameof(string.CompareTo)),
                    2 => parameters[0].ParameterType == typeof(string)
                         && parameters[1].ParameterType == typeof(StringComparison)
                         && method.Name != nameof(string.CompareTo),
                    _ => false,
                };

            if (!shapeAllowed)
            {
                return "calls an overload of string." + method.Name + " that LINQ mode does not allow.";
            }

            if (call.Object is null || !ReadsAsChain(call.Object))
            {
                return "calls string." + method.Name + " on something that is not a member of the document.";
            }

            if (Visit(call.Object, depth + 1) is { } refusal)
            {
                return refusal;
            }

            foreach (Expression argument in call.Arguments)
            {
                if (Literal(argument, depth) is { } argumentRefusal)
                {
                    return argumentRefusal;
                }
            }

            return null;
        }

        /// <summary>
        /// <c>string.Compare(a, b)</c> and <c>string.CompareOrdinal(a, b)</c> - how Dynamic LINQ spells <c>&lt;</c>
        /// and <c>&gt;</c> between strings - with one side read from the document and the other a literal.
        /// </summary>
        private string? VisitStringCompare(MethodCallExpression call, int depth)
        {
            MethodInfo method = call.Method;

            if (method.Name is not (nameof(string.Compare) or nameof(string.CompareOrdinal))
                || method.GetParameters() is not [{ ParameterType: var first }, { ParameterType: var second }]
                || first != typeof(string)
                || second != typeof(string))
            {
                return "calls string." + method.Name + ", which LINQ mode does not allow.";
            }

            Expression left = call.Arguments[0];
            Expression right = call.Arguments[1];

            if (ReadsAsChain(left))
            {
                return Visit(left, depth + 1) ?? Literal(right, depth);
            }

            return ReadsAsChain(right)
                ? Literal(left, depth) ?? Visit(right, depth + 1)
                : "compares two strings neither of which is a member of the document.";
        }

        private string? VisitEnumerable(MethodCallExpression call, int depth)
        {
            MethodInfo method = call.Method;

            // `Status in ("Open", "Shipped")` is Dynamic LINQ's in-list: Enumerable.Contains over an array it builds
            // from the literals, with the member as the value looked for (ParseIn, 1.7.4) - and, once the composer has
            // made the array a value (LinqQueryComposer.InLists), the same call over that array.
            if (method.Name == nameof(Enumerable.Contains)
                && call.Arguments is [var set, var value]
                && set is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } or ConstantExpression { Value: Array })
            {
                return VisitInList(method, set, value, depth);
            }

            if (call.Arguments[0] is not MemberExpression collection
                || LinqDocumentGraph.Classify(collection.Type) != LinqTypeKind.Collection
                || LinqDocumentGraph.ElementTypeOf(collection.Type) is not { } element
                || method.GetGenericArguments() is not [var elementArgument]
                || elementArgument != element)
            {
                return "calls " + method.Name + " on something that is not a collection member of the document.";
            }

            if (Chain(collection, depth + 1, out _) is { } refusal)
            {
                return refusal;
            }

            switch (method.Name, call.Arguments.Count)
            {
                case (nameof(Enumerable.Any), 1):
                case (nameof(Enumerable.Count), 1):
                    return null;

                case (nameof(Enumerable.Contains), 2):
                    return Literal(call.Arguments[1], depth);

                case (nameof(Enumerable.Any), 2):
                case (nameof(Enumerable.All), 2):
                case (nameof(Enumerable.Count), 2):
                {
                    if (call.Arguments[1] is not LambdaExpression lambda
                        || lambda.Parameters is not [var parameter]
                        || parameter.Type != element
                        || lambda.Body.Type != typeof(bool))
                    {
                        return "calls " + method.Name + " with something other than a condition on the element.";
                    }

                    elements.Add(parameter);

                    try
                    {
                        return Visit(lambda.Body, depth + 1);
                    }
                    finally
                    {
                        elements.Remove(parameter);
                    }
                }

                default:
                    return "calls an overload of " + method.Name + " that LINQ mode does not allow.";
            }
        }

        /// <summary>
        /// An in-list: every element a literal of the member's own type, and the member read from the document.
        /// </summary>
        /// <remarks>
        /// The array is the one closed subtree this admits. Marten translates an array <em>value</em> as one array
        /// parameter (<c>= ANY(:p0)</c>) but not an array initialiser, so the composer builds the array from the
        /// literals itself - setting each constant's value into it, never compiling anything - and this walk accepts
        /// either form: an initialiser whose every element is a literal, or an array constant whose every element is
        /// one. Each element is held to the literal rule and counted.
        /// </remarks>
        private string? VisitInList(MethodInfo method, Expression set, Expression value, int depth)
        {
            int count = set is NewArrayExpression init ? init.Expressions.Count : ((Array) ((ConstantExpression) set).Value!).Length;

            if (method.GetGenericArguments() is not [var element]
                || set.Type.GetElementType() != element
                || value.Type != element
                || count == 0)
            {
                return "uses a form of in (…) that LINQ mode does not allow.";
            }

            if (!ReadsAsChain(value))
            {
                return "looks for something that is not a member of the document in an in (…) list.";
            }

            if (set is NewArrayExpression initializer)
            {
                foreach (Expression item in initializer.Expressions)
                {
                    if (Literal(item, depth) is { } refusal)
                    {
                        return refusal;
                    }
                }
            }
            else
            {
                foreach (object? item in (Array) ((ConstantExpression) set).Value!)
                {
                    if (Visit(Expression.Constant(item, element), depth + 1) is { } refusal)
                    {
                        return refusal;
                    }
                }
            }

            return Visit(value, depth + 1);
        }

        /// <summary>An argument that must be a literal: a constant, possibly converted.</summary>
        private string? Literal(Expression argument, int depth) =>
            StripConversions(argument) is ConstantExpression
                ? Visit(argument, depth + 1)
                : "passes something other than a literal value to a method.";

        /// <summary>
        /// Walks one member chain to the parameter it starts at, checking every member against the graph.
        /// </summary>
        private string? Chain(MemberExpression member, int depth, out MemberChain? chain, bool count = true)
        {
            chain = null;

            var links = new List<MemberExpression>();
            Expression? current = member;

            while (current is MemberExpression link)
            {
                if (link.Expression is null)
                {
                    return "reads the static member " + guard.Qualified(link.Member.DeclaringType, link.Member.Name) +
                           "; LINQ mode reads only the document's own members.";
                }

                links.Add(link);
                current = link.Expression;
            }

            current = current is null ? null : StripConversions(current);

            if (current is not ParameterExpression root || !(ReferenceEquals(root, document) || elements.Contains(root)))
            {
                return "reads a member of something that is not the document - a literal, a method's result or a type.";
            }

            if (count)
            {
                nodes += links.Count;
                if (nodes > LinqQueryLimits.MaxNodes)
                {
                    return TooComplex();
                }
            }

            links.Reverse();

            var names = new List<string>(links.Count);

            for (int i = 0; i < links.Count; i++)
            {
                MemberExpression link = links[i];

                if (link.Member is PropertyInfo property && (property.GetGetMethod() is not { IsStatic: false } || property.GetIndexParameters().Length > 0))
                {
                    return "reads " + link.Member.Name + ", which is not a readable instance property.";
                }

                if (link.Member is not (PropertyInfo or FieldInfo { IsStatic: false, IsPublic: true }))
                {
                    return "reads " + link.Member.Name + ", which is not a public property or field.";
                }

                Type owner = link.Expression!.Type;

                if (FrameworkMember(owner, link.Member, isLast: i == links.Count - 1))
                {
                    names.Add(link.Member.Name);
                    continue;
                }

                if (!Graph.TryGetMember(owner, link.Member, out _))
                {
                    return "reads " + link.Member.Name + ", which is not a member LINQ mode can query on " + guard.Name(owner) + ".";
                }

                names.Add(link.Member.Name);
            }

            chain = new MemberChain(names, string.Join('.', names), ReferenceEquals(root, document));

            return depth > LinqQueryLimits.MaxExpressionDepth ? "nests too deeply." : null;
        }

        /// <summary>
        /// The members of framework types a predicate may read: a string's or an array's <c>Length</c>, a
        /// collection's <c>Count</c>, a nullable scalar's <c>HasValue</c> and <c>Value</c>.
        /// </summary>
        private static bool FrameworkMember(Type owner, MemberInfo member, bool isLast)
        {
            if (member is not PropertyInfo property)
            {
                return false;
            }

            if (owner == typeof(string))
            {
                return isLast && property.Name == nameof(string.Length) && property.DeclaringType == typeof(string);
            }

            if (Nullable.GetUnderlyingType(owner) is { } underlying)
            {
                return LinqDocumentGraph.IsScalar(underlying)
                       && property.DeclaringType == owner
                       && property.Name is "HasValue" or "Value";
            }

            if (LinqDocumentGraph.Classify(owner) == LinqTypeKind.Collection)
            {
                return isLast
                       && property.PropertyType == typeof(int)
                       && (property.Name == "Count" || (owner.IsArray && property.Name == "Length"))
                       && property.DeclaringType is { } declaring
                       && (declaring.IsArray || declaring == typeof(Array) || declaring.Namespace is "System.Collections.Generic" or "System.Collections.ObjectModel");
            }

            return false;
        }

        private bool ReadsAsChain(Expression expression)
        {
            Expression current = StripConversions(expression);

            // Name.ToLower().StartsWith("a"): a case method over a chain is still the chain.
            while (current is MethodCallExpression { Object: { } receiver } call
                   && call.Method.DeclaringType == typeof(string)
                   && CaseMethods.Contains(call.Method.Name))
            {
                current = StripConversions(receiver);
            }

            if (current is ParameterExpression parameter)
            {
                return elements.Contains(parameter) && LinqDocumentGraph.IsScalar(parameter.Type);
            }

            while (current is MemberExpression { Expression: { } owner })
            {
                current = StripConversions(owner);
            }

            return current is ParameterExpression root && (ReferenceEquals(root, document) || elements.Contains(root));
        }

        private bool Reads(Expression expression) => new Reader(document, elements).Reads(expression);

        private static bool OperatorAllowed(MethodInfo? method) =>
            method is null
            || (method.IsStatic && method.IsSpecialName && method.Name.StartsWith("op_", StringComparison.Ordinal)
                && method.DeclaringType is { } declaring && OperatorTypes.Contains(declaring));

        private static bool ConversionAllowed(Type from, Type to)
        {
            Type source = Nullable.GetUnderlyingType(from) ?? from;
            Type target = Nullable.GetUnderlyingType(to) ?? to;

            if (source == target)
            {
                return LinqDocumentGraph.IsScalar(source) || source == typeof(bool) || source == typeof(StringComparison);
            }

            if (source.IsEnum || target.IsEnum)
            {
                Type enumType = source.IsEnum ? source : target;
                Type other = source.IsEnum ? target : source;

                return LinqDocumentGraph.IsScalar(enumType) && other == Enum.GetUnderlyingType(enumType);
            }

            return IsNumeric(source) && IsNumeric(target);
        }

        private static bool IsNumeric(Type type) =>
            type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong)
            || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(char);

        private static bool IsEnum(Type type) => (Nullable.GetUnderlyingType(type) ?? type).IsEnum;

        private static string TooComplex() => "is too complex: more than " + LinqQueryLimits.MaxNodes + " expression nodes.";
    }

    /// <summary>One member chain, as the enum rule and the index advice read it.</summary>
    /// <param name="Members">The member names from the start of the chain outward.</param>
    /// <param name="Path">The same, dotted.</param>
    /// <param name="FromDocument">Whether it starts at the document rather than at a collection's element.</param>
    private sealed record MemberChain(IReadOnlyList<string> Members, string Path, bool FromDocument);

    /// <summary>Whether an expression reads the document or an element in scope anywhere inside it.</summary>
    private sealed class Reader(ParameterExpression document, List<ParameterExpression> elements) : ExpressionVisitor
    {
        private bool found;

        public bool Reads(Expression expression)
        {
            found = false;
            Visit(expression);
            return found;
        }

        public override Expression? Visit(Expression? node) => found ? node : base.Visit(node);

        protected override Expression VisitParameter(ParameterExpression node)
        {
            found |= ReferenceEquals(node, document) || elements.Contains(node);
            return node;
        }
    }

    /// <summary>Every node of an accepted clause body, so the rule lets each pass when JasperFx shows it.</summary>
    private sealed class Recorder(HashSet<Expression> validated) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is not null)
            {
                validated.Add(node);
            }

            return base.Visit(node);
        }
    }

    /// <summary>The parameters a body reads that no lambda inside it binds.</summary>
    private sealed class FreeParameters : ExpressionVisitor
    {
        private readonly List<ParameterExpression> bound = [];

        public List<ParameterExpression> Free { get; } = [];

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            bound.AddRange(node.Parameters);

            try
            {
                return base.VisitLambda(node);
            }
            finally
            {
                foreach (ParameterExpression parameter in node.Parameters)
                {
                    bound.Remove(parameter);
                }
            }
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!bound.Contains(node) && !Free.Contains(node))
            {
                Free.Add(node);
            }

            return node;
        }
    }
}
