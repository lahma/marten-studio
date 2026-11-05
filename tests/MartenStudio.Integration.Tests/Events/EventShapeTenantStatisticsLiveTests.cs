using Marten;

using MartenStudio.Services.Shapes;

using Npgsql;

using static MartenStudio.Integration.Tests.Events.EventShapeLiveSupport;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2-UX-FIX-FU3 (F1): the planner's statistics span the whole of <c>mt_events</c>, and Marten stamps <c>tenant_id</c> on every event
/// whatever the tenancy style - so on a store with <b>conjoined documents and single-tenanted events</b>, where the studio still offers a
/// tenant selector, an alias only tenant B's events carry would be named to tenant A by the statistics. With any tenant selected they are
/// never read: the answer is <c>NotForATenant</c> and Load counts (tenant-scoped) is the way. With every tenant in scope they are.
/// No test here loads the counted listing, so none is ever in hand.
/// </summary>
public class EventShapeTenantStatisticsLiveTests(EventShapeTenantStatisticsLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<EventShapeTenantStatisticsLiveTests.Fixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: documents conjoined, events single-tenanted, one alias nothing maps carried only by tenant B's events.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Events.MapEventType<ShapeOrderPlaced>("shape_order");

            // One conjoined document type, so the scope carries tenants; the events stay Marten's default, Single - and so must every
            // aggregate they fold into, which Marten refuses to mix (so no AllDocumentsAreMultiTenanted: the sample's aggregates are Single).
            options.Schema.For<PoisonNote>().MultiTenanted();
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            Guid globexStream = Guid.NewGuid();

            foreach (string tenant in new[] { "acme", "globex" })
            {
                await using IDocumentSession session = Marten.Store.LightweightSession(tenant);
                session.Events.StartStream(
                    tenant == "globex" ? globexStream : Guid.NewGuid(),
                    new ShapeOrderPlaced { Total = 1 },
                    new ShapeOrderPlaced { Total = 2 });

                // A conjoined document for the tenant, which is how the studio comes to know the tenant.
                session.Store(new PoisonNote { Id = Guid.NewGuid(), Text = tenant });
                await session.SaveChangesAsync(token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);

            // Twice, so the statistics hold it, stamped with tenant B's id as Marten stamps an event appended from that tenant's session.
            for (int i = 0; i < 2; i++)
            {
                await InsertAsync(connection, Schema, globexStream, 50 + i, "globex_only_v1", "Shop.Gone, Gone", $"{{\"Total\":{i}}}", "globex", token);
            }

            await ExecuteAsync(connection, $"analyze {EventsTable(Schema)}", token);
        }
    }

    private async Task<UnregisteredAliases> ListAsync(string? tenant)
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();
        return await check.Service.ListUnregisteredAsync(MartenFixture.ScopeFor(tenant), Token);
    }

    [PostgresFact]
    public async Task With_a_tenant_selected_the_statistics_are_never_read_and_the_other_tenants_alias_is_not_named()
    {
        UnregisteredAliases acme = await ListAsync("acme");

        acme.Source.Should().Be(UnregisteredAliasSource.NotForATenant);
        acme.Names.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task With_every_tenant_in_scope_the_statistics_name_it()
    {
        UnregisteredAliases all = await ListAsync(null);

        all.Source.Should().Be(UnregisteredAliasSource.Statistics);
        all.Names.Should().Contain("globex_only_v1");
    }

    [PostgresFact]
    public async Task A_check_under_another_tenant_neither_confirms_nor_runs_a_name_only_the_statistics_could_have_given_it()
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();

        ShapeCheckResult acme = await check.Service.CheckAsync(MartenFixture.ScopeFor("acme"), "globex_only_v1", false, Token);

        acme.Coverage.Should().Be(ShapeCoverage.NotChecked);
        acme.CoverageReason.Should().Be(ShapeCheckText.CouldNotConfirm);
        acme.Statements.Should().BeEmpty("nothing of tenant B's was read for tenant A");

        ShapeCheckResult all = await check.Service.CheckAsync(MartenFixture.ScopeFor(null), "globex_only_v1", false, Token);

        all.Types.Single().Failures.Should().ContainSingle().Which.Kind.Should().Be(ShapeFailureKind.UnknownEventType);
    }
}
