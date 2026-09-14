using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The two pure halves of counting: what a <c>reltuples</c> of <c>-1</c> means, and what an exact count
/// scoped to a page's tenant and soft-delete state looks like as SQL.
/// </summary>
public class CountEstimatorTests
{
    /// <summary>
    /// "Never analysed" is a third answer, and it is not zero.
    /// </summary>
    /// <remarks>
    /// Postgres reports <c>reltuples = -1</c> until the first <c>ANALYZE</c> — the state every freshly
    /// seeded collection is in — and mapping it to <c>Estimate(0)</c> drew "~0 documents" over a page of
    /// rows. It is also not <see cref="DocumentCount.Unavailable"/>: the table is certainly there, which
    /// is what lets a caller decide the exact count is worth paying for.
    /// </remarks>
    [Fact]
    public void An_unmeasured_table_is_unknown_rather_than_zero_or_unavailable()
    {
        DocumentCount.Unknown.IsUnknown.Should().BeTrue();
        DocumentCount.Unknown.IsEstimate.Should().BeFalse();
        DocumentCount.Unknown.Value.Should().Be(0);

        // A kind of "no number", so every caller that already draws nothing draws this correctly.
        DocumentCount.Unknown.IsUnavailable.Should().BeTrue();

        // ... and still distinguishable from a read that was refused.
        DocumentCount.Unavailable.IsUnknown.Should().BeFalse();
        DocumentCount.Unknown.Should().NotBe(DocumentCount.Unavailable);
    }

    [Fact]
    public void An_estimate_and_an_exact_count_say_which_they_are()
    {
        DocumentCount.Estimate(12).IsEstimate.Should().BeTrue();
        DocumentCount.Estimate(12).IsUnknown.Should().BeFalse();
        DocumentCount.Exact(12).IsEstimate.Should().BeFalse();
        DocumentCount.Exact(12).IsUnavailable.Should().BeFalse();
    }

    [Fact]
    public void A_whole_table_count_has_no_predicate_at_all() =>
        CountEstimator.ExactSql(SqlTestTables.FullyFeatured(), null, DeletedFilter.Include).Should().Be(
            "select count(*) from \"studio_sql\".\"mt_doc_sqltestcustomer\"");

    [Fact]
    public void The_tenant_is_a_parameter_and_the_column_comes_from_the_table() =>
        CountEstimator.ExactSql(SqlTestTables.FullyFeatured(), "tenant_id", DeletedFilter.Include).Should().Be(
            "select count(*) from \"studio_sql\".\"mt_doc_sqltestcustomer\" where \"tenant_id\" = @tenant");

    // The filter is named rather than typed because DeletedFilter is internal and a public xunit theory
    // method may not take one (CS0051).
    [Theory]
    [InlineData("Exclude", "\"mt_deleted\" = false")]
    [InlineData("Only", "\"mt_deleted\" = true")]
    public void The_soft_delete_tri_state_counts_the_rows_the_list_is_showing(string filter, string expected)
    {
        var sql = CountEstimator.ExactSql(
            SqlTestTables.FullyFeatured(), "tenant_id", Enum.Parse<DeletedFilter>(filter));

        sql.Should().Be(
            "select count(*) from \"studio_sql\".\"mt_doc_sqltestcustomer\" where \"tenant_id\" = @tenant and " +
            expected);
    }

    [Fact]
    public void A_collection_with_no_soft_delete_column_has_no_soft_delete_predicate() =>
        CountEstimator.ExactSql(SqlTestTables.MetadataLess(), null, DeletedFilter.Exclude).Should().Be(
            "select count(*) from \"studio_sql\".\"mt_doc_sqltestnote\"");
}
