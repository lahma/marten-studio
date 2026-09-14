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
    /// That is the case a naive estimator gets wrong by showing "about -1 documents" on a new store's very
    /// first page, which is why the threshold exists.
    /// </summary>
    [PostgresFact]
    public async Task A_table_that_has_never_been_analysed_falls_through_to_the_exact_count()
    {
        var estimator = new CountEstimator();

        await using var connection = await OpenAsync();

        var estimate = await estimator.EstimateAsync(connection, Schema, "mt_doc_thing");

        estimate.IsUnavailable.Should().BeFalse();
        estimate.Value.Should().Be(0, "Postgres reports -1 for a table with no statistics, which is not a count");

        var count = await estimator.CountAsync(connection, Schema, "mt_doc_thing");

        count.IsEstimate.Should().BeFalse();
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
