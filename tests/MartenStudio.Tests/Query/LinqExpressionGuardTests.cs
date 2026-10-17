using System.Linq.Expressions;
using System.Reflection;

using Marten;
using Marten.Services;

using MartenStudio.Services.Query;

// Every lambda below is an expression tree spelling a shape Dynamic LINQ produces - `Name.ToLower()`,
// `StartsWith("A")`, `Length.ToString()` - or one the guard must refuse. The culture-less, string-argument
// overloads these rules would swap in are different methods, and so a different tree from the one under test - as
// an in-list's inline array is, which a static field would turn into a member read.
#pragma warning disable CA1304, CA1305, CA1311, CA1847, CA1861, CA1862, CA1866

namespace MartenStudio.Tests.Query;

/// <summary>
/// The third wall on its own: hand-built expression trees, every shape Marten would evaluate in this process, put to
/// the studio's guard with no parser in front of it. Runs at the floor and at the latest alike.
/// </summary>
/// <remarks>
/// The anti-vacuity half is <see cref="Marten_does_evaluate_a_closed_subtree_in_process_which_is_the_threat" />: the
/// same expression handed straight to Marten's own statement builder runs the probe, so a guard that refuses it is
/// refusing something real.
/// </remarks>
public class LinqExpressionGuardTests
{
    private static readonly LinqExpressionGuard Guard = LinqTestStore.Guard<LinqOrder>();

    private static readonly string[] OneElement = ["a"];

    private static string? Check(Expression<Func<LinqOrder, bool>> predicate) => Guard.CheckPredicate(predicate);

    private static string? Key<TKey>(Expression<Func<LinqOrder, TKey>> key) => Guard.CheckOrderKey(key);

    public static TheoryData<string, Expression<Func<LinqOrder, bool>>> Accepted() => new()
    {
        { "string equality", x => x.Name == "a" },
        { "decimal comparison and and", x => x.Name == "a" && x.Total > 100m },
        { "nested member and StartsWith", x => x.Customer.Name.StartsWith("A") },
        { "case method then Contains", x => x.Name.ToLower().Contains("x") },
        { "Equals with a comparison", x => x.Name.Equals("a", StringComparison.OrdinalIgnoreCase) },
        { "a collection of objects", x => x.Lines.Any(l => l.Quantity > 5) },
        { "a collection of scalars", x => x.Tags.Any(t => t == "vip") },
        { "Count with a condition", x => x.Lines.Count(l => l.Sku == "A") > 1 },
        { "nullable is null", x => x.Priority == null },
        { "negation", x => !x.Rush },
        { "an enum compared by value", x => x.Status == LinqOrderStatus.Open },
        { "modulo", x => x.Total % 2m == 0m },
        { "a string's length", x => x.Name.Length > 3 },
        { "member against member", x => x.Name == x.Customer.Name },
        { "or and not together", x => !(x.Rush || x.Notes == null) },
        { "a nested object is null", x => x.Customer == null },

        // `Name in ("a", "b")`: Dynamic LINQ's in-list is this exact shape (ParseIn, 1.7.4). Spelled as a static
        // call because C# 14 binds `array.Contains(x)` to MemoryExtensions over a span, which is not that shape.
        { "an in-list of literals", x => Enumerable.Contains(new[] { "a", "b" }, x.Name) },
        { "an in-list of enums", x => Enumerable.Contains(new[] { LinqOrderStatus.Open, LinqOrderStatus.Shipped }, x.Status) },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void The_dialect_the_studio_offers_is_accepted(string what, Expression<Func<LinqOrder, bool>> predicate)
    {
        Check(predicate).Should().BeNull(what);
    }

    public static TheoryData<string, Expression<Func<LinqOrder, bool>>, string> Refused()
    {
        string local = "a";

        return new()
        {
            { "a static method of the host's", x => x.Name == HostileProbe.Fire("never"), LinqExpressionGuard.UnnamedType },
            { "a framework static method", x => x.Name == File.ReadAllText("c:/x"), "File.ReadAllText" },
            { "a static property", x => x.Name == Environment.MachineName, "static member" },
            { "a static field", x => x.Name == string.Empty, "static member" },
            { "a captured variable", x => x.Name == local, "not the document" },
            { "a method on a literal", x => x.Name == "x".PadLeft(10), "PadLeft" },
            { "a constructor", x => x.Name == new string('a', 3), "New" },
            { "a constructed date", x => x.PlacedAt > new DateTime(2024, 1, 1), "New" },
            { "a conditional", x => x.Rush ? x.Name == "a" : x.Name == "b", "Conditional" },
            { "a type literal", x => x.Name.GetType() == typeof(string), "op_Equality" },
            { "a member of a method's result", x => x.Name.GetType().Name == "String", "method's result" },
            { "an invoked delegate", x => ((Func<string>) (() => "a"))() == x.Name, "Invoke" },
            { "an array index", x => x.Tags[0] == "a", "ArrayIndex" },
            { "a list indexer", x => x.Lines[0].Quantity > 1, "not the document" },
            { "a member of type Type", x => x.Kind!.Name == "a", "Kind" },
            { "a member of type object", x => x.Payload == null, "Object" },
            { "a dictionary", x => x.Attributes.Count > 0, "Attributes" },
            { "a type with a converter of its own", x => x.Converted.Value == "a", "Converted" },
            { "a type test", x => x.Customer is LinqCustomer, "TypeIs" },
            { "a type cast", x => (x.Customer as object) != null, "Object" },
            { "coalescing", x => (x.Notes ?? "a") == "a", "Coalesce" },
            { "arithmetic", x => x.Total + 1m > 5m, "Add" },
            { "a method on a number", x => x.Name.Length.ToString() == "3", "ToString" },
            { "a non-literal argument", x => x.Name.StartsWith(x.Notes!), "literal" },
            { "a projection", x => x.Lines.Select(l => l.Quantity).Sum() > 0, "Sum" },
            { "string concatenation", x => x.Name == string.Concat("a", "b"), "Concat" },
            { "a culture-taking overload", x => x.Name.ToLower(System.Globalization.CultureInfo.InvariantCulture) == "a", "overload" },

            // The array of an in-list is evaluated in this process; only literals may be in it.
            { "an in-list with a member in it", x => Enumerable.Contains(new[] { "a", x.Notes! }, x.Name), "literal" },
            { "an in-list with a call in it", x => Enumerable.Contains(new[] { HostileProbe.Fire("never") }, x.Name), "literal" },
            { "an in-list that looks for a literal", x => Enumerable.Contains(new[] { "a", "b" }, "a"), "not a member" },
            { "a span's Contains", x => new[] { "a", "b" }.Contains(x.Name), "MemoryExtensions" },
        };
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Every_shape_outside_the_allow_list_is_refused(string what, Expression<Func<LinqOrder, bool>> predicate, string mentions)
    {
        string? refusal = Check(predicate);

        refusal.Should().NotBeNull(what);
        refusal.Should().Contain(mentions, what);
    }

    /// <summary>A refusal is shown to whoever typed the text, so it names no type the graph leaves out or the host hides.</summary>
    [Fact]
    public void A_refusal_never_names_a_type_the_graph_leaves_out_or_the_host_hides()
    {
        LinqExpressionGuard hiding = LinqTestStore.Guard<LinqOrder>(false, typeof(LinqSecret));

        hiding.CheckPredicate((Expression<Func<LinqOrder, bool>>) (x => x.Secret!.Code == "x"))
            .Should().NotBeNull().And.NotContain(nameof(LinqSecret));
        Check(x => x.Converted.Value == "a").Should().NotBeNull().And.NotContain(nameof(LinqConverted));
        Check(x => x.Converted == null).Should().NotBeNull().And.NotContain(nameof(LinqConverted));
        Check(x => x.Name == HostileProbe.Fire("never")).Should().NotBeNull().And.NotContain(nameof(HostileProbe));
    }

    [Fact]
    public void The_guard_never_evaluates_what_it_refuses()
    {
        string token = HostileProbe.NewToken();
        Expression<Func<LinqOrder, bool>> hostile = x => x.Name == HostileProbe.Fire(token);

        Check(hostile).Should().NotBeNull();
        HostileProbe.Fired(token).Should().Be(0, "the guard reads the tree and runs nothing in it");
    }

    /// <summary>
    /// Anti-vacuity: the same shape, given to Marten's own statement builder with no guard in front of it, is run by
    /// Marten in this process while it builds the SQL - so the guard above is refusing something real.
    /// </summary>
    [Fact]
    public void Marten_does_evaluate_a_closed_subtree_in_process_which_is_the_threat()
    {
        using DocumentStore store = LinqTestStore.Create();
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        string token = HostileProbe.NewToken();
        IQueryable<LinqOrder> query = session.Query<LinqOrder>().Where(x => x.Name == HostileProbe.Fire(token));

        MartenLinqCommand.Build(query, session.TenantId);

        HostileProbe.Fired(token).Should().BeGreaterThan(0,
            "Marten's ReduceToConstant compiles and invokes any operand that does not read the document");
    }

    [Fact]
    public void A_hidden_document_type_cannot_be_reached_through_a_member()
    {
        Expression<Func<LinqOrder, bool>> throughSecret = x => x.Secret!.Code == "a";

        LinqTestStore.Guard<LinqOrder>().CheckPredicate(throughSecret).Should().BeNull("the type is visible here");
        LinqTestStore.Guard<LinqOrder>(false, typeof(LinqSecret)).CheckPredicate(throughSecret)
            .Should().NotBeNull("a member whose type the host hides is not part of the graph");
    }

    [Fact]
    public void An_enum_stored_as_its_name_is_refused_an_ordering_comparison_and_an_ordering()
    {
        LinqExpressionGuard byName = LinqTestStore.Guard<LinqOrder>(jsonEnumsAsName: true);
        LinqExpressionGuard byValue = LinqTestStore.Guard<LinqOrder>(jsonEnumsAsName: false);

        Expression<Func<LinqOrder, bool>> range = x => x.Status > LinqOrderStatus.Open;
        Expression<Func<LinqOrder, bool>> equal = x => x.Status == LinqOrderStatus.Shipped;
        Expression<Func<LinqOrder, LinqOrderStatus>> order = x => x.Status;

        byName.CheckPredicate(range).Should().Contain("alphabetical");
        byName.CheckPredicate(equal).Should().BeNull("equality is unaffected by how the enum is stored");
        byName.CheckOrderKey(order).Should().Contain("alphabetical");

        byValue.CheckPredicate(range).Should().BeNull();
        byValue.CheckOrderKey(order).Should().BeNull();
    }

    [Fact]
    public void A_duplicated_field_decides_an_enum_by_its_column()
    {
        var guard = new LinqExpressionGuard(LinqTestStore.Graph<LinqOrder>(), static path => path is ["Status"] ? false : null, jsonEnumsAsName: true);
        Expression<Func<LinqOrder, bool>> range = x => x.Status > LinqOrderStatus.Open;

        guard.CheckPredicate(range).Should().BeNull("a duplicated column of integers compares by value whatever the JSON holds");
    }

    [Fact]
    public void An_ordering_is_a_member_path_to_a_scalar_and_nothing_else()
    {
        Key(x => x.Total).Should().BeNull();
        Key(x => x.Customer.Name).Should().BeNull();
        Key(x => x.Tags).Should().NotBeNull("a collection cannot be sorted");
        Key(x => x.Customer).Should().NotBeNull("an object cannot be sorted");
        Key(x => x.Name.ToLower()).Should().NotBeNull("an ordering is a member, not a call");
        Key(x => HostileProbe.Fire("never")).Should().NotBeNull();
    }

    [Fact]
    public void The_node_count_is_capped()
    {
        ParameterExpression x = Expression.Parameter(typeof(LinqOrder), "x");

        // Balanced, so the tree is shallow and it is the count rather than the depth that is over its limit.
        List<Expression> level =
        [
            .. Enumerable.Range(0, LinqQueryLimits.MaxNodes)
                .Select(_ => (Expression) Expression.Equal(Expression.Property(x, nameof(LinqOrder.Name)), Expression.Constant("a"))),
        ];

        while (level.Count > 1)
        {
            level = [.. level.Chunk(2).Select(static pair => pair.Length == 2 ? Expression.OrElse(pair[0], pair[1]) : pair[0])];
        }

        Guard.CheckPredicate(Expression.Lambda<Func<LinqOrder, bool>>(level[0], x)).Should().Contain("too complex");
    }

    [Fact]
    public void Shapes_no_lambda_can_spell_are_refused_too()
    {
        ParameterExpression x = Expression.Parameter(typeof(LinqOrder), "x");
        MemberExpression name = Expression.Property(x, nameof(LinqOrder.Name));
        ParameterExpression variable = Expression.Variable(typeof(string), "v");

        Expression[] bodies =
        [
            // A block, an assignment and a variable.
            Expression.Block([variable], Expression.Assign(variable, name), Expression.Equal(variable, Expression.Constant("a"))),

            // An object constant converted to a string.
            Expression.Equal(name, Expression.Convert(Expression.Constant("a", typeof(object)), typeof(string))),

            // Two Type constants.
            Expression.Equal(Expression.Constant(typeof(string), typeof(Type)), Expression.Constant(typeof(int), typeof(Type))),

            // A constant of the host's own type.
            Expression.Equal(Expression.Constant(new LinqCustomer()), Expression.Property(x, nameof(LinqOrder.Customer))),

            // A quoted lambda.
            Expression.Equal(
                Expression.Quote(Expression.Lambda<Func<string>>(Expression.Constant("a"))),
                Expression.Constant(null, typeof(Expression<Func<string>>))),

            // The length of an array constant.
            Expression.Equal(Expression.ArrayLength(Expression.Constant(OneElement)), Expression.Constant(1)),

            // Two literals, which no lambda can spell because the compiler folds them.
            Expression.Equal(Expression.Constant("a"), Expression.Constant("b")),
        ];

        foreach (Expression body in bodies)
        {
            Expression predicate = body.Type == typeof(bool) ? body : Expression.Equal(body, Expression.Constant(true));

            Guard.CheckPredicate(Expression.Lambda<Func<LinqOrder, bool>>(predicate, x)).Should().NotBeNull(body.ToString());
        }
    }

    [Fact]
    public void A_composed_query_may_be_nothing_but_Where_and_OrderBy_over_the_document()
    {
        using DocumentStore store = LinqTestStore.Create();
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        IQueryable<LinqOrder> source = session.Query<LinqOrder>();

        Guard.CheckComposed(source.Where(x => x.Rush).OrderBy(x => x.Total).ThenByDescending(x => x.Name).Expression, source.Expression)
            .Should().BeNull();

        Guard.CheckComposed(source.Select(x => x).Expression, source.Expression).Should().Contain("other than Where");
        Guard.CheckComposed(source.Where((x, i) => i > 1).Expression, source.Expression).Should().Contain("other than Where");
        Guard.CheckComposed(source.Take(5).Expression, source.Expression).Should().Contain("other than Where");
        Guard.CheckComposed(source.Where(x => x.Name == HostileProbe.Fire("never")).Expression, source.Expression)
            .Should().Contain(LinqExpressionGuard.UnnamedType, "a host type outside the graph is never named to the visitor")
            .And.NotContain("HostileProbe");
    }

    [Fact]
    public void The_rule_validates_a_body_once_and_then_lets_each_of_its_nodes_pass()
    {
        Func<Expression, string?> rule = Guard.RuleFor(LinqClause.Where);
        Expression<Func<LinqOrder, bool>> predicate = x => x.Name == "a" && x.Lines.Any(l => l.Quantity > 5);

        rule(predicate.Body).Should().BeNull();

        foreach (Expression node in Nodes(predicate.Body))
        {
            rule(node).Should().BeNull("a node of an accepted body, shown again by JasperFx's walk: " + node);
        }

        // A different body shown to the same rule is a new clause, validated whole.
        Expression<Func<LinqOrder, bool>> hostile = x => x.Name == HostileProbe.Fire("never");
        rule(hostile.Body).Should().NotBeNull();
    }

    private static List<Expression> Nodes(Expression root)
    {
        var collector = new NodeCollector();
        collector.Visit(root);
        return collector.Nodes;
    }

    private sealed class NodeCollector : ExpressionVisitor
    {
        public List<Expression> Nodes { get; } = [];

        public override Expression? Visit(Expression? node)
        {
            if (node is not null)
            {
                Nodes.Add(node);
            }

            return base.Visit(node);
        }
    }

    [Fact]
    public void Every_member_the_guard_lets_through_is_one_the_graph_declares()
    {
        // The graph is compared by metadata token: a property found through a derived type and through its base is one
        // member, and a different member of the same name is not.
        LinqDocumentGraph graph = LinqTestStore.Graph<LinqOrder>();
        PropertyInfo name = typeof(LinqOrder).GetProperty(nameof(LinqOrder.Name))!;
        PropertyInfo customerName = typeof(LinqCustomer).GetProperty(nameof(LinqCustomer.Name))!;

        graph.TryGetMember(typeof(LinqOrder), name, out _).Should().BeTrue();
        graph.TryGetMember(typeof(LinqOrder), customerName, out _).Should().BeFalse("LinqCustomer.Name is not LinqOrder.Name");
        graph.TryGetMember(typeof(LinqCustomer), customerName, out _).Should().BeTrue();
    }
}
