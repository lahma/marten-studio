using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// D8 against a real database: the estimate is free, the exact count is not, and the threshold decides
/// which one a dashboard tile pays for.
/// </summary>
public class CountEstimatorLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $$"""
            create table "{{Schema}}".mt_doc_thing (id uuid not null primary key, data jsonb not null);
            insert into "{{Schema}}".mt_doc_thing
            select gen_random_uuid(), '{}'::jsonb from generate_series(1, 1000);
            """);
    }

    /// <summary>
    /// A freshly seeded table has never been analysed, and Postgres reports <c>reltuples = -1</c> for it.
    /// </summary>
    /// <remarks>
    /// That number is not a count and it is not zero: it means nobody has measured this table. Reporting
    /// it as <c>Estimate(0)</c> put "~0 documents" over a page of a thousand rows on a new store's very
    /// first page (P2-fix H2), so the estimator says <c>Unknown</c> and the caller decides whether the
    /// exact count is worth paying for. <c>CountAsync</c> decides that it is.
    /// </remarks>
    [PostgresFact]
    public async Task A_table_that_has_never_been_analysed_is_unknown_and_falls_through_to_the_exact_count()
    {
        var estimator = new CountEstimator();

        await using var connection = await OpenAsync();

        var estimate = await estimator.EstimateAsync(connection, Schema, "mt_doc_thing");

        estimate.IsUnknown.Should().BeTrue("Postgres reports -1 for a table with no statistics");
        estimate.IsEstimate.Should().BeFalse("there is no estimate - that is the whole point");
        estimate.Value.Should().Be(0);

        var count = await estimator.CountAsync(connection, Schema, "mt_doc_thing");

        count.IsEstimate.Should().BeFalse();
        count.IsUnknown.Should().BeFalse();
        count.Value.Should().Be(1000);
    }

    [PostgresFact]
    public async Task Once_the_table_is_analysed_the_estimate_is_used_and_is_marked_as_one()
    {
        await using var connection = await OpenAsync();

        await ExecuteAsync(connection, $"analyze \"{Schema}\".mt_doc_thing");

        var estimator = new CountEstimator { ExactCountThreshold = 10 };

        var count = await estimator.CountAsync(connection, Schema, "mt_doc_thing");

        count.IsEstimate.Should().BeTrue("1000 is past the threshold, so the cheap answer stands");
        count.Value.Should().BeInRange(900, 1100);
        count.IsUnavailable.Should().BeFalse();
    }

    [PostgresFact]
    public async Task A_small_table_is_counted_exactly()
    {
        await using var connection = await OpenAsync();

        await ExecuteAsync(connection, $$"""
            create table "{{Schema}}".mt_doc_small (id uuid not null primary key, data jsonb not null);
            insert into "{{Schema}}".mt_doc_small values (gen_random_uuid(), '{}');
            analyze "{{Schema}}".mt_doc_small;
            """);

        var count = await new CountEstimator().CountAsync(connection, Schema, "mt_doc_small");

        count.IsEstimate.Should().BeFalse();
        count.Value.Should().Be(1);
    }

    /// <summary>"Cannot report" is a value, not an exception on a dashboard tile (plan §4.8).</summary>
    [PostgresFact]
    public async Task A_table_that_is_not_there_is_unavailable_rather_than_zero()
    {
        await using var connection = await OpenAsync();

        var count = await new CountEstimator().CountAsync(connection, Schema, "mt_doc_gone");

        count.IsUnavailable.Should().BeTrue();
        count.Value.Should().Be(0);
        count.IsEstimate.Should().BeFalse();
    }

    /// <summary>
    /// The exact count is the one read in the studio whose cost grows with how big the problem already is,
    /// so it takes a tenant to narrow it and a budget to bound it. There is no tenant-scoped equivalent of
    /// the estimate — <c>reltuples</c> describes the whole table and nothing narrower — which is why
    /// <c>CountAsync</c> takes neither and this overload takes both.
    /// </summary>
    [PostgresFact]
    public async Task The_exact_count_can_be_scoped_to_a_tenant_and_given_a_budget()
    {
        await using var connection = await OpenAsync();

        await ExecuteAsync(connection, $$"""
            create table "{{Schema}}".mt_doc_tenanted (
                id uuid not null,
                data jsonb not null,
                tenant_id varchar not null,
                primary key (tenant_id, id)
            );
            insert into "{{Schema}}".mt_doc_tenanted select gen_random_uuid(), '{}'::jsonb, 'acme'
            from generate_series(1, 7);
            insert into "{{Schema}}".mt_doc_tenanted select gen_random_uuid(), '{}'::jsonb, 'globex'
            from generate_series(1, 3);
            """);

        var estimator = new CountEstimator();

        var everything = await estimator.CountExactAsync(connection, Schema, "mt_doc_tenanted");

        everything.Value.Should().Be(10);

        var acme = await estimator.CountExactAsync(
            connection, Schema, "mt_doc_tenanted", "acme", TimeSpan.FromSeconds(10));

        acme.Value.Should().Be(7);
        acme.IsEstimate.Should().BeFalse();

        var globex = await estimator.CountExactAsync(
            connection, Schema, "mt_doc_tenanted", "globex", null);

        globex.Value.Should().Be(3);
    }

    [Fact]
    public void The_exact_count_query_parameterises_the_tenant_and_quotes_everything_else()
    {
        CountEstimator.ExactSql("studio_sql", "mt_doc_thing").Should().Be(
            "select count(*) from \"studio_sql\".\"mt_doc_thing\"");

        CountEstimator.ExactSql("studio_sql", "mt_doc_thing", "acme").Should().Be(
            "select count(*) from \"studio_sql\".\"mt_doc_thing\" where \"tenant_id\" = @tenant",
            "the tenant is a parameter, and the predicate only appears when there is one - this method is " +
            "given a table name rather than a DocumentTableInfo and cannot ask whether the column exists");
    }

    [PostgresFact]
    public async Task A_schema_or_table_with_an_odd_name_is_still_quoted_correctly()
    {
        await using var connection = await OpenAsync();

        await ExecuteAsync(connection, $"create table \"{Schema}\".\"Mixed Case\" (id int)");

        var count = await new CountEstimator().CountAsync(connection, Schema, "Mixed Case");

        count.IsUnavailable.Should().BeFalse();
        count.Value.Should().Be(0);
    }
}
