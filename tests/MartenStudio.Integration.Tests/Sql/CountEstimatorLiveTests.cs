using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// D8 against a real database: the estimate is free, the exact count is not, and
/// <c>ExactCountThreshold</c> decides which one a dashboard tile pays for.
/// </summary>
/// <remarks>
/// <para>
/// <b>What P2-perf changed here.</b> The estimator used to answer the whole question itself with
/// <c>CountAsync</c>, and every caller in the studio built it without a threshold — so the option was
/// documented behaviour nobody had. The decision now lives in <see cref="CountEstimator.MayCountExactlyAsync" />
/// and is made by the three callers that would otherwise each have their own rule, and the interesting
/// case is the one the estimate cannot answer: a table Postgres has never analysed, where "how many rows"
/// is settled by <c>select 1 … offset N limit 1</c> rather than by counting.
/// </para>
/// </remarks>
public class CountEstimatorLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private const int ThingRows = 1_000;

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $$"""
            create table "{{Schema}}".mt_doc_thing (id uuid not null primary key, data jsonb not null);
            insert into "{{Schema}}".mt_doc_thing
            select gen_random_uuid(), '{}'::jsonb from generate_series(1, {{ThingRows}});
            """);
    }

    /// <summary>
    /// A freshly seeded table has never been analysed, and Postgres reports <c>reltuples = -1</c> for it.
    /// </summary>
    /// <remarks>
    /// That number is not a count and it is not zero: it means nobody has measured this table. Reporting
    /// it as <c>Estimate(0)</c> put "~0 documents" over a page of a thousand rows on a new store's very
    /// first page (P2-fix H2), so the estimator says <c>Unknown</c> and the caller decides whether the
    /// exact count is worth paying for.
    /// </remarks>
    [PostgresFact]
    public async Task A_table_that_has_never_been_analysed_is_unknown_rather_than_zero()
    {
        await using NpgsqlConnection connection = await OpenAsync();

        DocumentCount estimate = await new CountEstimator()
            .EstimateAsync(connection, Schema, "mt_doc_thing", Token);

        estimate.IsUnknown.Should().BeTrue("Postgres reports -1 for a table with no statistics");
        estimate.IsEstimate.Should().BeFalse("there is no estimate - that is the whole point");
        estimate.Value.Should().Be(0);
    }

    /// <summary>
    /// A never-analysed table is settled by the probe, not by counting it.
    /// </summary>
    /// <remarks>
    /// The probe is <c>select 1 … offset @threshold limit 1</c>, whose work is the threshold and never the
    /// collection: at a threshold of ten it stops after eleven rows of a thousand-row table and says "more
    /// than ten", and at ten thousand it reads the thousand that are there, finds no eleven-thousandth row
    /// and says "small enough to count". That is the whole difference between a collections rail that
    /// costs twenty-five bounded reads on a freshly restored database and one that costs twenty-five
    /// sequential scans.
    /// </remarks>
    [PostgresFact]
    public async Task A_never_analysed_table_is_probed_rather_than_counted()
    {
        await using NpgsqlConnection connection = await OpenAsync();

        DocumentCount unknown = await new CountEstimator()
            .EstimateAsync(connection, Schema, "mt_doc_thing", Token);

        var stingy = new CountEstimator { ExactCountThreshold = 10 };
        var generous = new CountEstimator { ExactCountThreshold = 10_000 };

        (await stingy.IsAboveThresholdAsync(connection, Schema, "mt_doc_thing", Token))
            .Should().BeTrue("a thousand rows is more than ten");
        (await generous.IsAboveThresholdAsync(connection, Schema, "mt_doc_thing", Token))
            .Should().BeFalse("a thousand rows is fewer than ten thousand");

        (await stingy.MayCountExactlyAsync(connection, Schema, "mt_doc_thing", unknown, Token))
            .Should().BeFalse();
        (await generous.MayCountExactlyAsync(connection, Schema, "mt_doc_thing", unknown, Token))
            .Should().BeTrue();
    }

    /// <summary>
    /// Once the table is analysed the threshold is answered from <c>pg_class</c> alone, with no probe.
    /// </summary>
    [PostgresFact]
    public async Task Once_the_table_is_analysed_the_estimate_decides_and_nothing_touches_the_table()
    {
        await using NpgsqlConnection connection = await OpenAsync();

        await ExecuteAsync(connection, $"analyze \"{Schema}\".mt_doc_thing");

        DocumentCount estimate = await new CountEstimator()
            .EstimateAsync(connection, Schema, "mt_doc_thing", Token);

        estimate.IsEstimate.Should().BeTrue();
        estimate.Value.Should().BeInRange(900, 1100);

        // No connection at all: an estimate that is already in hand settles the question without a read,
        // and this call would throw if it ever stopped being true.
        (await new CountEstimator { ExactCountThreshold = 10 }
            .MayCountExactlyAsync(null!, Schema, "mt_doc_thing", estimate, Token))
            .Should().BeFalse("a thousand rows is past a threshold of ten");

        (await new CountEstimator { ExactCountThreshold = 10_000 }
            .MayCountExactlyAsync(null!, Schema, "mt_doc_thing", estimate, Token))
            .Should().BeTrue();
    }

    /// <summary>"Cannot report" is a value, not an exception on a dashboard tile (plan §4.8).</summary>
    [PostgresFact]
    public async Task A_table_that_is_not_there_is_unavailable_rather_than_zero_and_is_never_counted()
    {
        await using NpgsqlConnection connection = await OpenAsync();

        DocumentCount count = await new CountEstimator()
            .EstimateAsync(connection, Schema, "mt_doc_gone", Token);

        count.IsUnavailable.Should().BeTrue();
        count.IsUnknown.Should().BeFalse("the table is gone, which is not the same as unmeasured");
        count.Value.Should().Be(0);
        count.IsEstimate.Should().BeFalse();

        (await new CountEstimator().MayCountExactlyAsync(null!, Schema, "mt_doc_gone", count, Token))
            .Should().BeFalse("there is nothing there to count");
    }

    /// <summary>
    /// The exact count narrows to the tenant and to the soft-delete tri-state the page is on.
    /// </summary>
    /// <remarks>
    /// There is no tenant-scoped equivalent of the estimate — <c>reltuples</c> describes the whole table
    /// and nothing narrower — which is why an in-scope number is always an exact one, and why the
    /// collections rail hands a tenant-scoped visitor <c>Unknown</c> rather than a whole-table estimate
    /// that would be a cardinality disclosure about the other tenants.
    /// </remarks>
    [PostgresFact]
    public async Task The_exact_count_is_scoped_to_the_tenant_and_the_tri_state()
    {
        await using NpgsqlConnection connection = await OpenAsync();

        await ExecuteAsync(connection, $$"""
            create table "{{Schema}}".mt_doc_tenanted (
                id uuid not null,
                data jsonb not null,
                tenant_id varchar not null,
                mt_deleted boolean not null default false,
                primary key (tenant_id, id)
            );
            insert into "{{Schema}}".mt_doc_tenanted (id, data, tenant_id)
            select gen_random_uuid(), '{}'::jsonb, 'acme' from generate_series(1, 7);
            insert into "{{Schema}}".mt_doc_tenanted (id, data, tenant_id, mt_deleted)
            select gen_random_uuid(), '{}'::jsonb, 'acme', true from generate_series(1, 2);
            insert into "{{Schema}}".mt_doc_tenanted (id, data, tenant_id)
            select gen_random_uuid(), '{}'::jsonb, 'globex' from generate_series(1, 3);
            """);

        DocumentTableInfo table = await DiscoverAsync(connection, "mt_doc_tenanted");
        var estimator = new CountEstimator();

        DocumentCount everything = await estimator.CountExactAsync(
            connection, table, null, DeletedFilter.Include, null, Token);

        everything.Value.Should().Be(12);
        everything.IsEstimate.Should().BeFalse();

        DocumentCount acme = await estimator.CountExactAsync(
            connection, table, "acme", DeletedFilter.Include, TimeSpan.FromSeconds(10), Token);

        acme.Value.Should().Be(9);

        DocumentCount acmeLive = await estimator.CountExactAsync(
            connection, table, "acme", DeletedFilter.Exclude, null, Token);

        acmeLive.Value.Should().Be(7, "two of acme's nine rows are soft-deleted");

        DocumentCount globex = await estimator.CountExactAsync(
            connection, table, "globex", DeletedFilter.Include, null, Token);

        globex.Value.Should().Be(3);
    }

    /// <summary>The probe quotes its identifiers and parameterises the one value it has.</summary>
    [Fact]
    public void The_threshold_probe_quotes_everything_and_parameterises_the_threshold() =>
        CountEstimator.AboveThresholdSql("studio_sql", "mt_doc_thing").Should().Be(
            "select 1 from \"studio_sql\".\"mt_doc_thing\" offset @threshold limit 1");

    [PostgresFact]
    public async Task A_schema_or_table_with_an_odd_name_is_still_quoted_correctly()
    {
        await using NpgsqlConnection connection = await OpenAsync();

        await ExecuteAsync(connection, $"create table \"{Schema}\".\"Mixed Case\" (id int)");

        DocumentCount estimate = await new CountEstimator()
            .EstimateAsync(connection, Schema, "Mixed Case", Token);

        estimate.IsUnavailable.Should().BeTrue("an empty table has never been analysed either");
        estimate.IsUnknown.Should().BeTrue();

        // And the probe reaches it, which is the half that would break on a quoting mistake.
        (await new CountEstimator { ExactCountThreshold = 0 }
            .IsAboveThresholdAsync(connection, Schema, "Mixed Case", Token))
            .Should().BeFalse("there are no rows at all");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The table as the studio would describe one it found in <c>information_schema</c> and no mapping
    /// claims - which is the only way these hand-made tables can be described, and the same path the
    /// documents browser's "Discovered (unregistered)" band uses.
    /// </summary>
    private async Task<DocumentTableInfo> DiscoverAsync(NpgsqlConnection connection, string table)
    {
        TableColumns columns = await new ColumnCatalog().GetAsync(connection, Schema, table, Token);

        return DocumentTableInfo.FromDiscoveredTable(Schema, table, columns.Columns);
    }
}
