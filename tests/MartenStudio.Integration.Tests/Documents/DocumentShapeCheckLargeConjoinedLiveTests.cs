using Marten;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Shapes;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// 1.2-SHAPE-A-FIX: the documents' shape check on a conjoined collection big enough to be sampled - three tenants of 20,000 documents
/// after <c>ANALYZE</c>. Marten's primary key there is <c>(tenant_id, id)</c>, so a tenant's sample probes it, and with every tenant in
/// scope the edges are read in primary-key order (index scans, not a sort of the whole table) and no spread is attempted.
/// </summary>
public class DocumentShapeCheckLargeConjoinedLiveTests(DocumentShapeCheckLargeConjoinedLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentShapeCheckLargeConjoinedLiveTests.Fixture>
{
    private static readonly string[] Tenants = ["acme", "globex", "initech"];

    private readonly Fixture data = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: one conjoined type, 20,000 documents in each of three tenants, one corrupt row at each end of the key order.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The corrupt row with the lowest key in primary-key order: acme's lowest id.</summary>
        public Guid FirstInKeyOrder { get; private set; }

        /// <summary>The corrupt row with the highest key in primary-key order: the last tenant's highest id.</summary>
        public Guid LastInKeyOrder { get; private set; }

        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options) =>
            options.Schema.For<ShapeBigConjoinedDoc>().MultiTenanted();

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;

            foreach (string tenant in Tenants)
            {
                await Marten.Store.BulkInsertAsync(
                    tenant,
                    Enumerable.Range(0, 20_000).Select(static i => new ShapeBigConjoinedDoc { Id = Guid.NewGuid(), Quantity = i }).ToList(),
                    cancellation: token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);
            string table = $"\"{Schema}\".mt_doc_shape_big_conjoined_doc";

            await ExecuteAsync(connection, $"analyze {table}", token);

            FirstInKeyOrder = (Guid) (await ScalarAsync(connection, $"select id from {table} where tenant_id = 'acme' order by id limit 1", token))!;
            LastInKeyOrder = (Guid) (await ScalarAsync(connection, $"select id from {table} where tenant_id = 'initech' order by id desc limit 1", token))!;

            await ExecuteAsync(
                connection,
                $"update {table} set data = jsonb_set(data, '{{Quantity}}', '\"x\"') "
                + $"where (tenant_id = 'acme' and id = '{FirstInKeyOrder}') or (tenant_id = 'initech' and id = '{LastInKeyOrder}')",
                token);
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
        {
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(token);
        }

        private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken token)
        {
            await using NpgsqlCommand command = new(sql, connection);
            return await command.ExecuteScalarAsync(token);
        }
    }

    private async Task<ShapeCheckResult> CheckAsync(string? tenant)
    {
        using MartenFixture.ScopedService<IDocumentShapeCheck> check = Marten.Resolve<IDocumentShapeCheck>();
        string alias = Store.Options.FindOrResolveDocumentType(typeof(ShapeBigConjoinedDoc)).Alias;

        return await check.Service.CheckAsync(MartenFixture.ScopeFor(tenant), alias, false, Token);
    }

    [PostgresFact]
    public async Task A_tenants_sample_is_sampled_and_holds_only_that_tenants_documents()
    {
        ShapeCheckResult result = await CheckAsync("acme");

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled);
        result.Examined.Should().BeInRange(100, ShapeCheckLimits.SampleSize);
        result.SpreadPart.Should().BeGreaterThan(50, "with a tenant in scope the spread is probed through (tenant_id, id)");
        result.CoverageReason.Should().BeNull();

        ShapeTypeResult type = result.Types.Single();
        type.Examined.Should().Be(result.Examined);

        // The corrupt row of acme is its lowest id, so it is in the sample - and initech's corrupt row, which sits in the same
        // table, is not: a tenant's sample holds only that tenant's documents.
        ShapeFailureGroup failure = type.Failures.Should().ContainSingle().Subject;
        failure.Count.Should().Be(1);
        failure.Example.Should().Be(new ShapeItemKey(data.FirstInKeyOrder.ToString(), "acme"));
    }

    [PostgresFact]
    public async Task With_every_tenant_in_scope_only_the_edges_in_key_order_are_read_and_the_result_says_so_well_inside_the_budget()
    {
        ShapeCheckResult result = await CheckAsync(null);

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Sampled, "not Partial: the edges are index scans and finish");
        result.CoverageReason.Should().Be(ShapeCheckText.AllTenantsEdgesOnly);
        result.SpreadPart.Should().Be(0);
        result.Examined.Should().Be(2 * ShapeCheckLimits.Edge);
        result.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "the 5 second statement timeout is nowhere near");
        result.Statements.Select(static x => x.Sql).Should().NotContain(x => x.Contains("histogram", StringComparison.Ordinal));

        ShapeTypeResult type = result.Types.Single();
        type.Failures.Should().ContainSingle().Which.Count.Should().Be(2);
        type.Failures[0].Example.Should().Be(new ShapeItemKey(data.FirstInKeyOrder.ToString(), "acme"), "the first in key order is the lowest tenant's lowest id");
        result.LowestPart.Should().Be(ShapeCheckLimits.Edge);
        result.HighestPart.Should().Be(ShapeCheckLimits.Edge);
    }

    [PostgresFact]
    public async Task The_all_tenant_edges_are_index_scans_of_the_primary_key_and_the_row_read_computes_the_text_once()
    {
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(Store.Options.FindOrResolveDocumentType(typeof(ShapeBigConjoinedDoc)));

        using NpgsqlCommand edges = DocumentShapeQueries.BuildSampledKeys(table, null, null, ShapeCheckLimits.Edge, ShapeCheckLimits.Spread, 30);
        string edgePlan = await ExplainAsync(edges);

        edgePlan.Should().Contain("Index Scan").And.NotContain("Seq Scan").And.NotContain("Sort");

        List<object> ids = [.. Enumerable.Range(0, 200).Select(static _ => (object) Guid.NewGuid())];
        using NpgsqlCommand rows = DocumentShapeQueries.BuildRows(
            table, "acme", null, ids, [.. ids.Select(static _ => (string?) "acme")], ShapeCheckLimits.MaxItemBytes, 30);
        string rowPlan = await ExplainAsync(rows);

        // Computed once a row, by the lateral's own Result node; the case and the size read its output (the double parentheses
        // EXPLAIN prints for a reference to a child's column) instead of rendering the jsonb again.
        rowPlan.Split("Output: (d.data)::text", StringSplitOptions.None).Length.Should().Be(2);
        rowPlan.Should().Contain("octet_length(((d.data)::text))").And.Contain("THEN ((d.data)::text)");
    }

    private async Task<string> ExplainAsync(NpgsqlCommand source)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new("explain (verbose, costs off) " + source.CommandText, connection);

        foreach (NpgsqlParameter parameter in source.Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value });
        }

        List<string> lines = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }
}

[DocumentAlias("shape_big_conjoined_doc")]
public class ShapeBigConjoinedDoc
{
    public Guid Id { get; set; }

    public int Quantity { get; set; }
}
