using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.0-HARD: which visible views and functions a drill-in plan would reach a withheld schema through, resolved to a fixed point
/// from the catalog walk's edges and the string bodies of SQL functions, and matched against a text by whole word, ignoring case.
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

        StatementCrossing.Crossing(edges, [], Hr).Should().BeEquivalentTo(["v_vips", "v_golden"],
            "a withheld table is the plan's own check's to hide; a function or view is inlined or expanded into conditions");
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

        IReadOnlySet<string> crossing = StatementCrossing.Crossing(edges, functions, Hr);

        crossing.Should().Contain(["v_vips", "vipness", "via_view", "v_through", "v_further"]);
        crossing.Should().NotContain("plain");
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

    [Fact]
    public void Nothing_crosses_when_nothing_is_withheld_and_no_edge_reaches_a_withheld_schema()
    {
        StatementCrossing.Crossing([], [new SqlFunctionSource("f", "select hr.x()")], new HashSet<string>()).Should().BeEmpty();
        StatementCrossing.NamesAny("select * from v", new HashSet<string>()).Should().BeFalse();
    }
}
