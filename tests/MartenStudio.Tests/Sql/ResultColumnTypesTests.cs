using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// 1.2-VEC: which result columns of a console statement are requested as text, chosen from the described columns and from who
/// owns their types. The describe and the requests themselves are asserted against a real pgvector in
/// <c>PgVectorConsoleLiveTests</c>.
/// </summary>
public class ResultColumnTypesTests
{
    private const uint VectorOid = 16_500;
    private const uint HalfvecOid = 16_501;
    private const uint SparsevecOid = 16_502;

    private static readonly IReadOnlyDictionary<long, string> Owned = new Dictionary<long, string>
    {
        [VectorOid] = "vector",
        [HalfvecOid] = "halfvec",
        [SparsevecOid] = "sparsevec",
    };

    private static readonly IReadOnlyDictionary<long, string> None = new Dictionary<long, string>();

    private static DescribedColumn Readable(string name, uint oid = 23) => new(oid, name, true);

    private static DescribedColumn Unreadable(string name, uint oid) => new(oid, name, false);

    [Fact]
    public void The_catalog_is_asked_only_about_columns_the_driver_cannot_read_or_that_are_named_like_pgvector_s()
    {
        DescribedColumn[] columns =
        [
            Readable("int4"),
            Readable("text", 25),
            Unreadable("vector", VectorOid),
            Unreadable("cube", 17_000),
            Readable("halfvec", HalfvecOid),
            Unreadable("vector", VectorOid),
            Unreadable("<unknown>", 0),
        ];

        ResultColumnTypes.Candidates(columns).Should().Equal(
            [VectorOid, HalfvecOid, 17_000L],
            "distinct, ordered, readable ordinary types left out, and an OID the reader did not report left out");
    }

    [Fact]
    public void A_statement_with_nothing_unusual_asks_for_nothing_and_no_round_trip()
    {
        DescribedColumn[] columns = [Readable("int4"), Readable("text", 25), Readable("jsonb", 3802)];

        ResultColumnTypes.Candidates(columns).Should().BeEmpty();

        ResultTextPlan plan = ResultColumnTypes.Plan(columns, None);

        plan.UnknownResultTypes.Should().BeNull("the command is left exactly as it was");
        plan.Vectors.Should().HaveCount(3).And.OnlyContain(static x => x == null);
    }

    [Fact]
    public void A_pgvector_column_is_requested_as_text_whatever_the_driver_can_read()
    {
        DescribedColumn[] columns =
        [
            Readable("int4"),
            Unreadable("vector", VectorOid),
            Unreadable("halfvec", HalfvecOid),
            Readable("sparsevec", SparsevecOid),
        ];

        ResultTextPlan plan = ResultColumnTypes.Plan(columns, Owned);

        // A host that called UseVector() still gets Postgres's text, so every host's grid has one shape to collapse.
        plan.UnknownResultTypes.Should().Equal(false, true, true, true);
        plan.Vectors.Select(static x => x?.Kind).Should().Equal(
            new PgVectorKind?[] { null, PgVectorKind.Vector, PgVectorKind.HalfVector, PgVectorKind.SparseVector });
        plan.Vectors.Where(static x => x is not null).Select(static x => x!.Dimensions).Should().OnlyContain(static x => x == null);
    }

    [Fact]
    public void Another_unknown_type_is_requested_as_text_and_is_not_a_vector()
    {
        DescribedColumn[] columns = [Unreadable("cube", 17_000), Readable("int4"), Unreadable("hstore", 17_001)];

        ResultTextPlan plan = ResultColumnTypes.Plan(columns, None);

        plan.UnknownResultTypes.Should().Equal(true, false, true);
        plan.Vectors.Should().OnlyContain(static x => x == null);
    }

    [Fact]
    public void A_type_called_vector_that_the_vector_extension_does_not_own_is_not_pgvector_s()
    {
        // Readable (an enum, say): left as the driver reads it. Unreadable: text, but still not a vector.
        ResultTextPlan readable = ResultColumnTypes.Plan([Readable("vector", 18_000)], None);

        readable.UnknownResultTypes.Should().BeNull();
        readable.Vectors.Single().Should().BeNull();

        ResultTextPlan unreadable = ResultColumnTypes.Plan([Unreadable("vector", 18_000)], None);

        unreadable.UnknownResultTypes.Should().Equal(true);
        unreadable.Vectors.Single().Should().BeNull();
    }

    [Fact]
    public void An_owned_type_with_a_name_that_is_not_one_of_the_three_is_not_a_vector()
    {
        // pgvector also owns array types and operator support types in pg_type terms; none is a value type the grid collapses.
        Dictionary<long, string> owned = new() { [19_000] = "bit_something" };

        ResultTextPlan plan = ResultColumnTypes.Plan([Unreadable("bit_something", 19_000)], owned);

        plan.UnknownResultTypes.Should().Equal(true);
        plan.Vectors.Single().Should().BeNull();
    }

    [Fact]
    public void A_column_the_reader_gave_no_type_for_is_never_matched_by_accident()
    {
        ResultTextPlan plan = ResultColumnTypes.Plan([new DescribedColumn(0, "<unknown>", false)], Owned);

        plan.UnknownResultTypes.Should().Equal(true);
        plan.Vectors.Single().Should().BeNull();
    }

    [Fact]
    public void The_ownership_statement_is_catalog_only_and_takes_the_oids_as_a_parameter()
    {
        ResultColumnTypes.PgVectorTypesSql.Should().Contain("@oids").And.Contain("pg_catalog.pg_depend").And.Contain("e.extname = 'vector'");
        ResultColumnTypes.PgVectorTypesSql.Should().NotContain("information_schema");
    }

    /// <summary>
    /// A result keeps a budget of vector text in all: past it a vector cell falls back to the kilobyte of an ordinary cell and
    /// is marked cut, so many vector columns over many rows cannot hold the per-cell cap times every cell.
    /// </summary>
    [Fact]
    public void A_result_wide_budget_of_vector_text_sends_the_cells_past_it_back_to_the_kilobyte()
    {
        var formatter = new SqlValueFormatter(new SqlValueFormatterOptions { MaxVectorLength = 5_000, MaxVectorResultLength = 12_000 });
        string text = "[" + new string('1', 3_000) + "]";
        long used = 0;

        List<SqlCell> cells = [];

        for (int i = 0; i < 6; i++)
        {
            cells.Add(formatter.FormatPgVector(text, ref used));
        }

        cells.Take(3).Should().OnlyContain(static x => !x.IsTruncated && x.Text.Length == 3_002, "three cells fit in the budget whole");
        cells.Skip(3).Should().OnlyContain(static x => x.IsTruncated && x.Text.Length == 1_025,
            "the fourth would pass 12 000 characters: the kilobyte and the mark of a cut");
        cells[5].FullLength.Should().Be(3_002);

        new SqlValueFormatterOptions().MaxVectorResultLength.Should().Be(8L * 1024 * 1024);
    }

    [Fact]
    public void A_connection_prepares_statements_automatically_when_its_string_says_Max_Auto_Prepare()
    {
        using var off = new Npgsql.NpgsqlConnection("Host=h;Database=d");
        using var on = new Npgsql.NpgsqlConnection("Host=h;Database=d;Max Auto Prepare=50;Auto Prepare Min Usages=2");
        using var zero = new Npgsql.NpgsqlConnection("Host=h;Database=d;Max Auto Prepare=0");

        ReadOnlySqlSession.AutoPrepares(off).Should().BeFalse();
        ReadOnlySqlSession.AutoPrepares(on).Should().BeTrue();
        ReadOnlySqlSession.AutoPrepares(zero).Should().BeFalse();
    }

    [Theory]
    [InlineData("seg", "(cannot read seg) - cast it to ::text in the statement to see it")]
    [InlineData("public.vector", "(cannot read public.vector) - this connection prepares statements automatically (Max Auto Prepare), so the console cannot ask for text: cast it to ::text in the statement to see it")]
    [InlineData("halfvec", "(cannot read halfvec) - this connection prepares statements automatically (Max Auto Prepare), so the console cannot ask for text: cast it to ::text in the statement to see it")]
    public void A_cell_that_could_not_be_read_says_how_to_see_it(string type, string expected) =>
        ReadOnlySqlSession.Unreadable(type).Should().Be(expected);

    [Fact]
    public void A_vector_is_cut_at_its_own_cap_and_an_ordinary_cell_at_the_kilobyte()
    {
        var formatter = new SqlValueFormatter(new SqlValueFormatterOptions { MaxVectorLength = 30 });
        string text = "[" + string.Join(',', Enumerable.Range(0, 40)) + "]";

        long used = 0;
        SqlCell vector = formatter.FormatPgVector(text, ref used);

        vector.IsTruncated.Should().BeTrue();
        vector.Text.Should().Be(text[..30] + "…");
        vector.FullLength.Should().Be(text.Length);
        vector.Kind.Should().Be(SqlCellKind.Text);
        used.Should().Be(30);

        formatter.FormatPgVector("[1,2,3]", ref used).Should().Be(new SqlCell("[1,2,3]", SqlCellKind.Text, false, 7));
        used.Should().Be(37);
        formatter.Format(new string('x', 2000)).IsTruncated.Should().BeTrue("an ordinary cell keeps the kilobyte");
        new SqlValueFormatterOptions().MaxVectorLength.Should().BeGreaterThan(15_000, "an embedding of 1536 dimensions fits whole");
    }
}
