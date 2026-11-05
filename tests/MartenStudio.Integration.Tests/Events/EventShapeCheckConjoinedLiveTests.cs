using Marten;

using MartenStudio.Services.Events;
using MartenStudio.Services.Shapes;

using Npgsql;

using static MartenStudio.Integration.Tests.Events.EventShapeLiveSupport;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2-SHAPE-B: the events' shape check on a conjoined event store. Only tenant B's copy of an event no longer fits: tenant A's scope
/// finds nothing, tenant B's finds it, and with no tenant in scope the example carries tenant B. An event type only tenant B has is not
/// a name tenant A's scope can check - the page lists it for B and not for A.
/// </summary>
public class EventShapeCheckConjoinedLiveTests(EventShapeCheckConjoinedLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<EventShapeCheckConjoinedLiveTests.Fixture>
{
    private readonly Fixture data = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The store: conjoined events, three <c>shape_order</c> events in each of two tenants, one of tenant B's corrupt.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The corrupt event's sequence.</summary>
        public long BadSequence { get; private set; }

        /// <summary>The stream the corrupt event is in.</summary>
        public Guid BadStream { get; private set; }

        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Events.MapEventType<ShapeOrderPlaced>("shape_order");
            options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
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
                    new ShapeOrderPlaced { Total = 2 },
                    new ShapeOrderPlaced { Total = 3 });
                await session.SaveChangesAsync(token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);
            string events = EventsTable(Schema);

            BadSequence = (long) (await ScalarAsync(
                connection, $"select seq_id from {events} where tenant_id = 'globex' and type = 'shape_order' order by seq_id limit 1", token))!;
            BadStream = globexStream;

            await ExecuteAsync(connection, $"update {events} set data = jsonb_set(data, '{{Total}}', '\"x\"') where seq_id = {BadSequence}", token);

            // A type only tenant B has, that no registered type answers to.
            // Twice: a value seen once is neither a common value nor a histogram bound, and the statistics do not name it.
            await InsertAsync(connection, Schema, globexStream, 50, "globex_only_v1", "Shop.Gone, Gone", "{\"Total\":1}", "globex", token);
            await InsertAsync(connection, Schema, globexStream, 51, "globex_only_v1", "Shop.Gone, Gone", "{\"Total\":2}", "globex", token);

            await ExecuteAsync(connection, $"analyze {events}", token);
        }
    }

    private async Task<ShapeCheckResult> CheckAsync(string? tenant, string eventType = "shape_order")
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();
        return await check.Service.CheckAsync(MartenFixture.ScopeFor(tenant), eventType, false, Token);
    }

    [PostgresFact]
    public async Task Tenant_a_finds_nothing_because_the_bad_event_is_tenant_bs()
    {
        ShapeCheckResult result = await CheckAsync("acme");

        result.Error.Should().BeNull();
        result.Coverage.Should().Be(ShapeCoverage.Complete);
        result.Examined.Should().Be(3);
        result.Types.Single().Failures.Should().BeEmpty();
        result.Statements.Select(static x => x.Sql).Should().Contain(x => x.Contains("\"tenant_id\" = @tenant", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Tenant_b_finds_it_and_the_example_is_tenant_bs_stream_at_its_version()
    {
        ShapeCheckResult result = await CheckAsync("globex");

        result.Examined.Should().Be(3);

        ShapeFailureGroup failure = result.Types.Single().Failures.Should().ContainSingle().Subject;

        failure.Example.Should().Be(new ShapeItemKey(data.BadStream.ToString(), "globex", data.BadSequence, 1));
    }

    [PostgresFact]
    public async Task With_no_tenant_in_scope_every_tenant_is_read_and_the_example_carries_the_tenant_whose_event_is_bad()
    {
        ShapeCheckResult result = await CheckAsync(null);

        result.Examined.Should().Be(6);

        ShapeFailureGroup failure = result.Types.Single().Failures.Should().ContainSingle().Subject;

        failure.Count.Should().Be(1);
        failure.Example.TenantId.Should().Be("globex");
        failure.Example.Sequence.Should().Be(data.BadSequence);
    }

    [PostgresFact]
    public async Task A_type_only_another_tenant_has_is_not_one_this_tenants_scope_can_check()
    {
        // Each tenant's visitor loads the counts, which is tenant-scoped (the page's own button): that is where an unregistered type is known.
        using (MartenFixture.ScopedService<IEventDataService> events = Marten.Resolve<IEventDataService>())
        {
            foreach (string tenant in new[] { "acme", "globex" })
            {
                (await events.Service.ListEventTypesAsync(MartenFixture.ScopeFor(tenant), withCounts: true, Token)).Error.Should().BeNull();
            }
        }

        ShapeCheckResult mine = await CheckAsync("acme", "globex_only_v1");
        ShapeCheckResult theirs = await CheckAsync("globex", "globex_only_v1");

        mine.Coverage.Should().Be(ShapeCoverage.NotChecked);
        mine.CoverageReason.Should().Be(ShapeCheckText.NoSuchEventType("globex_only_v1"));
        mine.Statements.Should().BeEmpty();

        theirs.Types.Single().Failures.Should().ContainSingle().Which.Kind.Should().Be(ShapeFailureKind.UnknownEventType);
    }

    /// <summary>
    /// 1.2-UX-FIX: the planner's statistics span every tenant's events, so with a tenant selected they would name another tenant's event
    /// types. They are not read for a tenant (and no listing of that tenant's is in hand here); with every tenant in scope they are.
    /// </summary>
    [PostgresFact]
    public async Task The_statistics_are_not_used_for_a_tenant_and_are_for_every_tenant()
    {
        using MartenFixture.ScopedService<IEventShapeCheck> check = Marten.Resolve<IEventShapeCheck>();

        UnregisteredAliases acme = await check.Service.ListUnregisteredAsync(MartenFixture.ScopeFor("acme"), Token);
        UnregisteredAliases all = await check.Service.ListUnregisteredAsync(MartenFixture.ScopeFor(null), Token);

        // Whether a counted listing of the tenant's is in hand depends on the other tests; what may never happen is the statistics.
        acme.Source.Should().NotBe(UnregisteredAliasSource.Statistics);
        acme.Names.Should().NotContain("globex_only_v1", "tenant A's scope never learns of a type only tenant B has");

        all.Source.Should().Be(UnregisteredAliasSource.Statistics);
        all.Names.Should().Contain("globex_only_v1");
    }
}
