using System.Text;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.0-HARD and RVH-FIX: which visible objects a drill-in plan would reach a withheld schema through, resolved to a fixed point
/// from the catalog walk's edges and the string bodies of SQL functions, and matched against a text by identifier as Postgres
/// keeps it, ignoring case.
/// </summary>
public class StatementCrossingTests
{
    private static readonly IReadOnlySet<string> Hr = new HashSet<string>(StringComparer.Ordinal) { "hr" };

    [Fact]
    public void A_view_that_reaches_a_withheld_function_or_view_crosses_and_one_that_reaches_only_a_withheld_table_does_not()
    {
        CrossingEdge[] edges =
        [
            new("v_vips", "is_vip", Withheld: true, Expands: true),
            new("v_golden", "v_gold", Withheld: true, Expands: true),
            new("v_salaries", "salaries", Withheld: true, Expands: false),
            new("v_orders", "orders", Withheld: false, Expands: false),
        ];

        StatementCrossing.Resolve(edges, [], Hr).Names.Should().BeEquivalentTo(["v_vips", "v_golden"],
            "a withheld table is the plan's own check to hide; a function or view is inlined or expanded into conditions");
    }

    [Fact]
    public void A_string_bodied_function_crosses_when_its_body_names_the_schema_or_something_that_crosses_and_so_does_what_reaches_it()
    {
        CrossingEdge[] edges =
        [
            new("v_vips", "is_vip", Withheld: true, Expands: true),
            new("v_through", "vipness", Withheld: false, Expands: true),
            new("v_further", "v_through", Withheld: false, Expands: true),
        ];
        SqlFunctionSource[] functions =
        [
            new("vipness", "select t is not null and hr.is_vip(t)"),
            new("via_view", "select count(*) from public.v_vips"),
            new("plain", "select 1"),
            new("quoted", "select \"HR\".x()"),
        ];

        CrossingResult result = StatementCrossing.Resolve(edges, functions, Hr);

        result.Converged.Should().BeTrue();
        result.Names.Should().Contain(["v_vips", "vipness", "via_view", "v_through", "v_further"]);
        result.Names.Should().NotContain("plain");
        result.Names.Should().Contain("quoted",
            "\"HR\" is another schema than hr to Postgres, but the text's own mask spells a schema case-insensitively: refused, which costs a plan and leaks nothing");
    }

    /// <summary>RVH-FIX: an operator is recorded in a view's dependencies, not its function, and a statement can use one directly.</summary>
    [Fact]
    public void An_operator_whose_function_is_withheld_crosses_and_so_does_a_view_that_uses_it()
    {
        CrossingEdge[] edges =
        [
            new("###", "hr_matches", Withheld: true, Expands: true),
            new("v_hits", "###", Withheld: false, Expands: true),
            new("+++", "pg_add", Withheld: false, Expands: true),
        ];

        CrossingResult result = StatementCrossing.Resolve(edges, [], Hr);

        result.Names.Should().BeEquivalentTo(["###", "v_hits"]);
        StatementCrossing.NamesAny("select o.id from public.orders o where o.note ### 'x'", result.Names).Should().BeTrue("the symbol, as spelled");
        StatementCrossing.NamesAny("select 1 +++ 2", result.Names).Should().BeFalse("an operator whose function is visible and crosses nothing");
    }

    /// <summary>RVH-FIX: a string-bodied function that uses such an operator crosses with it.</summary>
    [Fact]
    public void A_string_bodied_function_using_a_crossing_operator_crosses()
    {
        CrossingEdge[] edges = [new("###", "hr_matches", Withheld: true, Expands: true)];
        SqlFunctionSource[] functions = [new("looks", "select $1 ### 'x'"), new("plain", "select $1 + 1")];

        StatementCrossing.Resolve(edges, functions, Hr).Names.Should().BeEquivalentTo(["###", "looks"]);
    }

    /// <summary>RVH-FIX: a table with a policy (or a function with a parameter default) that reaches a withheld function crosses with what reads it.</summary>
    [Fact]
    public void A_table_whose_policy_reaches_a_withheld_function_crosses_and_so_does_a_view_over_it()
    {
        CrossingEdge[] edges =
        [
            new("docs", "hr_can_see", Withheld: true, Expands: true),
            new("v_docs", "docs", Withheld: false, Expands: false),
            new("lookup", "hr_default", Withheld: true, Expands: true),
        ];

        StatementCrossing.Resolve(edges, [], Hr).Names.Should().BeEquivalentTo(["docs", "v_docs", "lookup"]);
    }

    /// <summary>RVH-FIX: a set still growing when the rounds run out is not an answer - the caller refuses.</summary>
    [Fact]
    public void A_walk_that_was_still_growing_at_its_bound_is_not_converged_and_one_that_finished_is()
    {
        // Listed last to first, so each round adds exactly one link of the chain.
        SqlFunctionSource[] functions =
        [
            new("f4", "select f3()"),
            new("f3", "select f2()"),
            new("f2", "select f1()"),
            new("f1", "select hr.x()"),
        ];

        CrossingResult cut = StatementCrossing.Resolve([], functions, Hr, maxRounds: 2);
        cut.Converged.Should().BeFalse("the last round still added a function");
        cut.Names.Should().NotContain("f4", "the set the bound left is incomplete, which is why it is not an answer");

        StatementCrossing.Resolve([], functions, Hr, maxRounds: 3).Converged.Should().BeFalse(
            "a last round that still grew the set proves nothing, even when the set happens to be whole");

        CrossingResult whole = StatementCrossing.Resolve([], functions, Hr, maxRounds: 4);
        whole.Converged.Should().BeTrue();
        whole.Names.Should().Contain(["f1", "f2", "f3", "f4"]);

        StatementCrossing.Resolve([], functions, Hr).Converged.Should().BeTrue("the real bound is far past any chain here");
    }

    [Theory]
    [InlineData("select v.note from public.v_vips v where v.id = $1", true)]
    [InlineData("select v.note from public.\"V_VIPS\" v", true)]
    [InlineData("select 1 -- v_vips", true)]
    [InlineData("select v.note from public.v_vips2 v", false)]
    [InlineData("select x_v_vips from t", false)]
    [InlineData("select 1", false)]
    public void A_text_names_a_crossing_object_by_whole_word_ignoring_case_wherever_it_stands(string text, bool names) =>
        StatementCrossing.NamesAny(text, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "v_vips" }).Should().Be(names);

    [Fact]
    public void A_name_that_is_not_one_word_is_looked_for_as_spelled()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "My View" };

        StatementCrossing.NamesAny("select * from public.\"My View\"", names).Should().BeTrue();
        StatementCrossing.NamesAny("select * from public.my_view", names).Should().BeFalse();
    }

    /// <summary>RVH-FIX: a quoted identifier's <c>""</c> is one <c>"</c>, as the catalog has it.</summary>
    [Fact]
    public void A_quoted_identifiers_doubled_quote_is_one_quote()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "a\"b" };

        StatementCrossing.NamesAny("select * from public.\"a\"\"b\"", names).Should().BeTrue();
        StatementCrossing.NamesAny("select * from public.\"a\"\"b\" x where \"c\" = 1", names).Should().BeTrue();
        StatementCrossing.NamesAny("select * from public.ab", names).Should().BeFalse();
        StatementCrossing.Words("select \"it's \"\"x\"\"\"").Should().Contain("it's \"x\"");
    }

    /// <summary>RVH-FIX: Postgres keeps 63 bytes of an identifier (NAMEDATALEN - 1), cut at a character, and compares that.</summary>
    [Theory]
    [InlineData(70, "x", 63)]
    [InlineData(63, "x", 63)]
    [InlineData(40, "é", 31)]
    [InlineData(20, "😀", 15)]
    public void An_identifier_is_cut_to_63_bytes_of_UTF8_at_a_character(int repeat, string unit, int keptUnits)
    {
        string name = string.Concat(Enumerable.Repeat(unit, repeat));
        string kept = string.Concat(Enumerable.Repeat(unit, keptUnits));

        StatementCrossing.Cut(name).Should().Be(kept);
        Encoding.UTF8.GetByteCount(StatementCrossing.Cut(name)).Should().BeLessThanOrEqualTo(StatementCrossing.MaxIdentifierBytes);

        // The catalog holds the cut name; the text spells the long one, and Postgres compares what it kept.
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { kept };
        StatementCrossing.NamesAny("select * from public." + name, names).Should().BeTrue();
        StatementCrossing.NamesAny("select * from public.\"" + name + "\"", names).Should().BeTrue();
        StatementCrossing.NamesAny("select * from public.other_" + name, names).Should().BeFalse("a different name is a different name");
    }

    [Fact]
    public void Nothing_crosses_when_nothing_is_withheld_and_no_edge_reaches_a_withheld_schema()
    {
        CrossingResult none = StatementCrossing.Resolve([], [new SqlFunctionSource("f", "select hr.x()")], new HashSet<string>());

        none.Names.Should().BeEmpty();
        none.Converged.Should().BeTrue();
        StatementCrossing.NamesAny("select * from v", new HashSet<string>()).Should().BeFalse();
    }
}
