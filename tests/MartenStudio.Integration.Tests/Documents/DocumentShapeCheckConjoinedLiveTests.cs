using Marten;
using Marten.Schema;

using MartenStudio.Services.Shapes;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// 1.2-SHAPE: the documents' shape check under conjoined tenancy. The same id exists in two tenants and only tenant B's copy no
/// longer fits: tenant A's scope finds nothing, tenant B's finds it, and with no tenant in scope the example carries tenant B -
/// which is only possible if a key is read as (tenant, id). The collection is small (complete, whole): the sampled cases - a tenant's
/// spread, and the edges alone with every tenant in scope - are <c>DocumentShapeCheckLargeConjoinedLiveTests</c>'.
/// </summary>
public class DocumentShapeCheckConjoinedLiveTests(DocumentShapeCheckConjoinedLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentShapeCheckConjoinedLiveTests.Fixture>
{
    private readonly Fixture data = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: one conjoined type, three documents in each of two tenants.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The id both tenants have.</summary>
        public Guid Shared { get; } = Guid.NewGuid();

        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options) =>
            options.Schema.For<ShapeConjoinedDoc>().MultiTenanted();

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;

            foreach (string tenant in new[] { "acme", "globex" })
            {
                await using IDocumentSession session = Marten.Store.LightweightSession(tenant);
                session.Store(
                    new ShapeConjoinedDoc { Id = Shared, Quantity = 1 },
                    new ShapeConjoinedDoc { Id = Guid.NewGuid(), Quantity = 2 },
                    new ShapeConjoinedDoc { Id = Guid.NewGuid(), Quantity = 3 });
                await session.SaveChangesAsync(token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);
            await using NpgsqlCommand command = new(
                $"update \"{Schema}\".mt_doc_shape_conjoined_doc set data = jsonb_set(data, '{{Quantity}}', '\"x\"') "
                + $"where tenant_id = 'globex' and id = '{Shared}'",
                connection);
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private async Task<ShapeCheckResult> CheckAsync(string? tenant)
    {
        using MartenFixture.ScopedService<IDocumentShapeCheck> check = Marten.Resolve<IDocumentShapeCheck>();
        string alias = Store.Options.FindOrResolveDocumentType(typeof(ShapeConjoinedDoc)).Alias;

        return await check.Service.CheckAsync(MartenFixture.ScopeFor(tenant), alias, false, Token);
    }

    [PostgresFact]
    public async Task Tenant_a_finds_nothing_because_the_bad_row_is_tenant_bs()
    {
        ShapeCheckResult result = await CheckAsync("acme");

        result.Error.Should().BeNull();
        result.Examined.Should().Be(3);
        result.Types.Single().Failures.Should().BeEmpty();
        result.Types.Single().Members.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Tenant_b_finds_it_and_the_example_is_tenant_bs()
    {
        ShapeCheckResult result = await CheckAsync("globex");

        result.Examined.Should().Be(3);
        ShapeFailureGroup failure = result.Types.Single().Failures.Should().ContainSingle().Subject;
        failure.Example.Should().Be(new ShapeItemKey(data.Shared.ToString(), "globex"));
        result.Statements.Select(static x => x.Sql).Should().Contain(x => x.Contains("\"tenant_id\" = @tenant", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task With_no_tenant_in_scope_every_tenant_is_read_and_the_example_carries_the_tenant_whose_copy_is_bad()
    {
        ShapeCheckResult result = await CheckAsync(null);

        result.Examined.Should().Be(6);
        result.Types.Single().Examined.Should().Be(6);
        ShapeFailureGroup failure = result.Types.Single().Failures.Should().ContainSingle().Subject;
        failure.Count.Should().Be(1, "tenant A's copy of the same id is fine: a key is (tenant, id)");
        failure.Example.TenantId.Should().Be("globex");
        failure.Example.Id.Should().Be(data.Shared.ToString());
        result.Statements.Select(static x => x.Sql).Should().Contain(x => x.Contains("unnest(@tenants, @ids)", StringComparison.Ordinal));
    }
}

[DocumentAlias("shape_conjoined_doc")]
public class ShapeConjoinedDoc
{
    public Guid Id { get; set; }

    public int Quantity { get; set; }
}
