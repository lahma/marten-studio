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

    // ------------------------------------------------------------------------------------------------
    // ExactCountThreshold (P2-perf deliverable 2)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A declined exact count keeps the number it had, and says why it is the only one on offer.
    /// </summary>
    /// <remarks>
    /// The point of the whole deliverable in one assertion: this is a <em>value</em>. The estimate is still
    /// there, the page still draws a number, and the sentence beside it names the option that produced it —
    /// as against the alternative, which was for the studio to start a sequential scan because somebody
    /// clicked, or to show a <c>~</c> with no account of why the "=" they pressed did nothing.
    /// </remarks>
    [Fact]
    public void A_declined_exact_count_keeps_the_estimate_and_names_the_option()
    {
        DocumentCount declined = DocumentCount.RefusedExact(DocumentCount.Estimate(5_000_000), 100_000);

        declined.IsExactRefused.Should().BeTrue();
        declined.IsEstimate.Should().BeTrue("the estimate is still the answer");
        declined.IsUnavailable.Should().BeFalse("declining to count is not a failure to read");
        declined.Value.Should().Be(5_000_000);
        declined.ExactRefusedAbove.Should().Be(100_000);
        declined.Reason.Should().Contain("Estimate only")
            .And.Contain("100,000")
            .And.Contain("ExactCountThreshold");
    }

    /// <summary>The same marking applied to a table that has no estimate at all.</summary>
    /// <remarks>
    /// The never-analysed case is the one the rail meets on a freshly restored database, and it has to
    /// stay <see cref="DocumentCount.IsUnknown"/> so that every caller which already draws "no number"
    /// goes on drawing it — while gaining the sentence that says a count was available and was declined.
    /// </remarks>
    [Fact]
    public void A_declined_count_on_a_never_analysed_table_is_still_unknown()
    {
        DocumentCount declined = DocumentCount.RefusedExact(DocumentCount.Unknown, 100_000);

        declined.IsUnknown.Should().BeTrue();
        declined.IsUnavailable.Should().BeTrue("a kind of 'no number', as Unknown always was");
        declined.IsExactRefused.Should().BeTrue();
        declined.Reason.Should().NotBeNull();
    }

    /// <summary>A count declined on other grounds says so in its own words.</summary>
    /// <remarks>
    /// The rail declines for two more reasons that are not about a row count — a heap already too large to
    /// read while drawing navigation, and a conjoined collection a tenant-scoped visitor is not owed a
    /// whole-table number for. "Refused above 100,000 rows" would be a false account of either.
    /// </remarks>
    [Fact]
    public void A_count_declined_on_other_grounds_carries_its_own_sentence()
    {
        DocumentCount declined = DocumentCount.RefusedExact(DocumentCount.Unknown, 0, "Because of something else.");

        declined.Reason.Should().Be("Because of something else.");
        declined.Reason.Should().NotContain("ExactCountThreshold");
    }

    /// <summary>A count nobody declined has nothing to say.</summary>
    [Fact]
    public void An_ordinary_count_has_no_reason()
    {
        DocumentCount.Estimate(12).Reason.Should().BeNull();
        DocumentCount.Exact(12).Reason.Should().BeNull();
        DocumentCount.Unknown.Reason.Should().BeNull();
        DocumentCount.Unavailable.Reason.Should().BeNull();
    }

    /// <summary>
    /// An estimate already in hand settles the threshold question without touching the table.
    /// </summary>
    /// <remarks>
    /// The <see langword="null"/> connection is the assertion, not an accident: it is what makes "a
    /// collection with an estimate costs no read to decline" a fact the test would notice losing. Only the
    /// never-analysed case is allowed to go to the database, and it does so through the bounded probe.
    /// </remarks>
    [Theory]
    [InlineData(99_999L, true)]
    [InlineData(100_000L, true)]
    [InlineData(100_001L, false)]
    public async Task An_estimate_decides_the_threshold_with_no_read_at_all(long rows, bool mayCount)
    {
        var estimator = new CountEstimator { ExactCountThreshold = 100_000 };

        (await estimator.MayCountExactlyAsync(
                null!, "studio_sql", "mt_doc_sqltestcustomer", DocumentCount.Estimate(rows), TestContext.Current.CancellationToken))
            .Should().Be(mayCount);
    }

    /// <summary>A table that could not be read at all is not counted either.</summary>
    [Fact]
    public async Task An_unavailable_table_is_never_counted() =>
        (await new CountEstimator().MayCountExactlyAsync(
                null!, "studio_sql", "mt_doc_gone", DocumentCount.Unavailable, TestContext.Current.CancellationToken))
            .Should().BeFalse();

    /// <summary>
    /// The probe quotes its identifiers and parameterises the only value it has (hard rule 4).
    /// </summary>
    /// <remarks>
    /// <c>offset @threshold limit 1</c> rather than <c>count(*)</c> is the whole trick: the work is the
    /// threshold and never the collection, so asking "is this bigger than a hundred thousand rows" costs
    /// the same on a ten-million-row table as on a hundred-and-one-row one.
    /// </remarks>
    [Fact]
    public void The_threshold_probe_is_bounded_by_the_threshold_and_quotes_its_identifiers()
    {
        CountEstimator.AboveThresholdSql("studio sql", "Mixed Case").Should().Be(
            "select 1 from \"studio sql\".\"Mixed Case\" offset @threshold limit 1");

        // And a name carrying a quote is refused rather than escaped, like every other identifier here.
        Action quoted = () => CountEstimator.AboveThresholdSql("studio_sql", "mt_doc_\"odd\"");

        quoted.Should().Throw<ArgumentException>();
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
