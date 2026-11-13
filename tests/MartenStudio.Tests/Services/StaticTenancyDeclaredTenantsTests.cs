using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Links;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Services;

/// <summary>
/// ZW38: a static multi-database tenancy declares in configuration which database serves each tenant, so the scope search places a tenant in
/// its database from that declaration where documents and events are single-tenanted and no database lists a tenant at all - and a tenant it
/// cannot place is a miss that never falls back to reading the database on screen.
/// </summary>
/// <remarks>
/// The real catalog and the real Marten source over a static tenancy whose connection strings are never connected to: the declaration is read
/// from memory (<c>DescribeDatabasesAsync</c> on a <c>StaticMultiTenancy</c>), which is the property under test.
/// </remarks>
public class StaticTenancyDeclaredTenantsTests
{
    private const string Key = MartenStoreRegistry.DefaultStoreKey;

    private const string FirstDatabase = "zw38_first";

    private const string SecondDatabase = "zw38_second";

    private const string StorePolicy = "store-policy";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_premise_with_single_tenanted_rows_no_database_lists_a_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);

        foreach (string identity in (string[])[harness.First, harness.Second])
        {
            StoreScopeFacts facts = await harness.Catalog.DescribeAsync(Key, identity, Token);

            facts.ShowTenantSelector.Should().BeFalse();
            facts.Tenants.Source.Should().Be(TenantListSource.Unavailable);
        }
    }

    [Fact]
    public async Task A_declared_tenant_is_placed_in_its_database_with_no_tenant_in_the_scope()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);

        DeclaredTenantPlacement initech = await harness.Catalog.PlaceDeclaredTenantAsync(Key, harness.Both, "initech", Token);
        DeclaredTenantPlacement globex = await harness.Catalog.PlaceDeclaredTenantAsync(Key, harness.Both, "globex", Token);

        initech.Declared.Should().BeTrue();
        initech.Hit!.DatabaseId.Should().Be(harness.Second);
        globex.Hit!.DatabaseId.Should().Be(harness.First);
        initech.Hit.Facts.ShowTenantSelector.Should().BeFalse("rows are single-tenanted: the database is the scope");
        initech.Hit.Facts.Tenants.Source.Should().Be(TenantListSource.Unavailable);
    }

    [Fact]
    public async Task The_usual_database_is_considered_so_a_tenant_declared_there_is_placed_not_a_miss()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);

        // The catalog has no "usual" database: every database the visitor may see is a candidate, and the one that declares the tenant is it.
        (await harness.Catalog.PlaceDeclaredTenantAsync(Key, [harness.First], "globex", Token)).Hit!.DatabaseId.Should().Be(harness.First);
        (await harness.Catalog.PlaceDeclaredTenantAsync(Key, [harness.Second], "initech", Token)).Hit!.DatabaseId.Should().Be(harness.Second);
    }

    /// <summary>The three causes of a miss, which the catalog answers with the same value.</summary>
    public static TheoryData<string> MissCauses => new() { "undeclared", "refused", "declared twice" };

    [Theory]
    [MemberData(nameof(MissCauses))]
    public async Task A_miss_is_one_value_whatever_its_cause(string cause)
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, policy: cause == "refused", declaredTwice: cause == "declared twice");
        if (cause == "refused")
        {
            harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second);
        }

        string tenant = cause switch { "undeclared" => "umbrella", "refused" => "initech", _ => "acme" };

        DeclaredTenantPlacement placement = await harness.Catalog.PlaceDeclaredTenantAsync(Key, cause == "refused" ? [harness.First] : harness.Both, tenant, Token);

        placement.Should().Be(DeclaredTenantPlacement.NotPlaced, cause);
    }

    [Fact]
    public async Task A_database_the_policy_refuses_is_a_miss_even_when_the_catalog_is_given_it()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, policy: true);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second);

        // Named to the catalog, as a caller that did not filter would: the catalog asks (store, db, no tenant) itself.
        (await harness.Catalog.PlaceDeclaredTenantAsync(Key, harness.Both, "initech", Token)).Should().Be(DeclaredTenantPlacement.NotPlaced);
        harness.Policies.Calls.Should().Contain((StorePolicy, new MartenStoreResource(Key, harness.Second, null, null)));
    }

    [Fact]
    public async Task A_tenant_declared_twice_is_a_miss_counted_before_any_policy_is_asked()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, policy: true, declaredTwice: true);
        int before = harness.Policies.Calls.Count;

        (await harness.Catalog.PlaceDeclaredTenantAsync(Key, harness.Both, "acme", Token)).Should().Be(DeclaredTenantPlacement.NotPlaced);
        harness.Policies.Calls.Count.Should().Be(before, "the answer cannot depend on the visitor: no policy question was needed");
    }

    [Fact]
    public async Task The_declaration_search_has_no_database_bound()
    {
        // 40 databases, the tenant in the 40th: past the tenant-listing search's bound of 32, which the declaration does not have. (That the
        // declaration is described once per search is TenantDiscovery.DeclaringDatabasesAsync's single DescribeDatabasesAsync call, held by
        // TenancyEnumerationCallTests; a tenancy that counts calls cannot be built, as StoreOptions creates StaticMultiTenancy itself.)
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, extraDatabases: 38);

        DeclaredTenantPlacement placement = await harness.Catalog.PlaceDeclaredTenantAsync(Key, harness.Everything, "tenant-39", Token);

        placement.Hit!.DatabaseId.Should().Be(harness.Everything[^1]);
    }

    [Fact]
    public async Task A_static_source_with_single_tenanted_rows_declares_tenants_though_it_shows_no_selector()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);

        harness.Sources.TryResolve(harness.Sources.Registrations()[0], out IStudioSource? source).IsAvailable.Should().BeTrue();

        source!.DeclaresTenants.Should().BeTrue();
        source.ShowsTenantSelector.Should().BeFalse("nothing else would have found the tenant: no listing exists");
    }

    [Fact]
    public async Task A_state_opens_the_database_that_declares_the_tenant_and_keeps_no_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);
        StudioState state = await harness.StateAsync();

        string usual = state.ActiveScope!.DatabaseId;
        (string foreignTenant, string foreignDatabase) = usual == harness.First ? ("initech", harness.Second) : ("globex", harness.First);

        await state.SetScopeAsync(Key, null, foreignTenant, Token);

        state.ActiveScope.Should().Be(new StudioScope(Key, foreignDatabase, null));
        state.LinkScope.Should().Be(state.ActiveScope);
        state.IsTenantUnplaced(Key, null, foreignTenant).Should().BeFalse("it was placed");
    }

    [Fact]
    public async Task A_state_keeps_the_usual_database_for_a_tenant_declared_there_and_it_is_not_a_miss()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);
        StudioState state = await harness.StateAsync();

        string usual = state.ActiveScope!.DatabaseId;
        string ownTenant = usual == harness.First ? "globex" : "initech";

        await state.SetScopeAsync(Key, null, ownTenant, Token);

        state.ActiveScope.Should().Be(new StudioScope(Key, usual, null));
        state.IsTenantUnplaced(Key, null, ownTenant).Should().BeFalse("the usual database declares it: the id is read in the tenant's own database");
    }

    [Theory]
    [MemberData(nameof(MissCauses))]
    public async Task A_state_that_cannot_place_the_tenant_stays_where_it_is_and_marks_the_address_unplaced(string cause)
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, policy: cause == "refused", declaredTwice: cause == "declared twice");
        if (cause == "refused")
        {
            harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second);
        }

        StudioState state = await harness.StateAsync();
        string usual = state.ActiveScope!.DatabaseId;
        string tenant = cause switch { "undeclared" => "umbrella", "refused" => "initech", _ => "acme" };

        await state.SetScopeAsync(Key, null, tenant, Token);

        state.ActiveScope.Should().Be(new StudioScope(Key, usual, null), cause);
        state.IsTenantUnplaced(Key, null, tenant).Should().BeTrue(cause);
        state.IsTenantUnplaced(Key, null, "someone-else").Should().BeFalse("only the address that was not placed");
        state.IsTenantUnplaced(Key, usual, tenant).Should().BeFalse("an address that names a database is never unplaced");
    }

    [Fact]
    public async Task The_next_scope_change_clears_the_unplaced_address()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);
        StudioState state = await harness.StateAsync();
        string usual = state.ActiveScope!.DatabaseId;

        await state.SetScopeAsync(Key, null, "umbrella", Token);
        state.IsTenantUnplaced(Key, null, "umbrella").Should().BeTrue("the premise");

        await state.SetScopeAsync(Key, usual, null, Token);

        state.IsTenantUnplaced(Key, null, "umbrella").Should().BeFalse();
    }

    [Fact]
    public async Task The_same_address_is_not_searched_again_whatever_the_answer_was()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, policy: true);
        StudioState state = await harness.StateAsync();

        await state.SetScopeAsync(Key, null, "umbrella", Token);
        int asked = harness.Policies.Calls.Count;
        await state.SetScopeAsync(Key, null, "umbrella", Token);
        await state.SetScopeAsync(Key, null, "umbrella", Token);

        harness.Policies.Calls.Count.Should().Be(asked, "a page that re-applies its own address on every parameter set does not search again");
        state.IsTenantUnplaced(Key, null, "umbrella").Should().BeTrue("and the answer stands");

        // A placed tenant is remembered too.
        string usual = state.ActiveScope!.DatabaseId;
        string foreignTenant = usual == harness.First ? "initech" : "globex";
        await state.SetScopeAsync(Key, null, foreignTenant, Token);
        asked = harness.Policies.Calls.Count;
        await state.SetScopeAsync(Key, null, foreignTenant, Token);

        harness.Policies.Calls.Count.Should().Be(asked);
        state.IsTenantUnplaced(Key, null, foreignTenant).Should().BeFalse();
    }

    [Fact]
    public async Task A_state_does_not_open_a_database_the_visitor_may_not_see()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, policy: true);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second);
        StudioState state = await harness.StateAsync();

        state.AvailableDatabases.Select(static x => x.Identity).Should().Equal(harness.First);

        await state.SetScopeAsync(Key, null, "initech", Token);

        state.ActiveScope.Should().Be(
            new StudioScope(Key, harness.First, null),
            "initech is declared in a database this visitor cannot see: a miss, and nothing about it is shown");
        state.IsTenantUnplaced(Key, null, "initech").Should().BeTrue();
    }

    [Fact]
    public async Task An_address_that_names_a_database_is_reopened_exactly()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);
        StudioState state = await harness.StateAsync();

        await state.SetScopeAsync(Key, harness.First, "initech", Token);

        state.ActiveScope.Should().Be(
            new StudioScope(Key, harness.First, null),
            "no search: the named database stays and the tenant it does not list is dropped");
        state.IsTenantUnplaced(Key, null, "initech").Should().BeFalse();
    }

    [Fact]
    public async Task With_tenanted_rows_the_declaration_places_the_tenant_and_the_policy_filtered_listing_must_carry_it()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: true, policy: true);

        DeclaredTenantPlacement placement = await harness.Catalog.PlaceDeclaredTenantAsync(Key, harness.Both, "initech", Token);
        placement.Hit!.DatabaseId.Should().Be(harness.Second);
        placement.Hit.Facts.Tenants.Ids.Should().Contain("initech");

        // The policy lets this visitor address no tenant in the second database: the declaration places initech there, and the listing refuses it.
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second || resource.TenantId is null);
        (await harness.Catalog.PlaceDeclaredTenantAsync(Key, harness.Both, "initech", Token)).Should().Be(DeclaredTenantPlacement.NotPlaced);
    }

    [Fact]
    public async Task A_one_database_store_declares_nothing()
    {
        ServiceCollection services = new();
        services.AddMarten(x =>
        {
            x.Connection("Host=zw38-single.invalid;Database=none;Username=none;Password=none");
            x.Schema.For<Order>();
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        IOptions<MartenStudioOptions> options = Options.Create(new MartenStudioOptions());
        var users = new TestAuthenticationStateProvider();
        users.SignIn("tester");

        var (sources, catalog, _) = MartenSpine.Scope(
            options,
            new MartenStoreRegistry(services),
            MartenSpine.Authorization(options, new TestStoreAuthorizationService(), users),
            new TenantDiscovery(options, NullLogger<TenantDiscovery>.Instance),
            provider);

        sources.TryResolve(sources.Registrations()[0], out IStudioSource? source).IsAvailable.Should().BeTrue();
        IReadOnlyList<IStudioDatabase> databases = await source!.DatabasesAsync(Token);

        source.DeclaresTenants.Should().BeFalse("a one-database store declares nothing");
        (await source.DatabasesDeclaringAsync("acme", databases, Token)).Should().BeEmpty();
        (await catalog.PlaceDeclaredTenantAsync(Key, [databases[0].Identity], "acme", Token)).Should().Be(DeclaredTenantPlacement.NotDeclared);
    }

    /// <summary>A static tenancy of two databases - acme and globex in the first, initech in the second - never connected to.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private Harness(
            ServiceProvider provider,
            MartenStudioSourceProvider sources,
            StudioScopeCatalog catalog,
            TestStoreAuthorizationService policies,
            string[] identities)
        {
            this.provider = provider;
            Sources = sources;
            Catalog = catalog;
            Policies = policies;
            Everything = identities;
        }

        public MartenStudioSourceProvider Sources { get; }

        public StudioScopeCatalog Catalog { get; }

        public TestStoreAuthorizationService Policies { get; }

        /// <summary>Every database's identity: the first two are <see cref="First" /> and <see cref="Second" />.</summary>
        public string[] Everything { get; }

        /// <summary>The identity of the database that declares acme and globex.</summary>
        public string First => Everything[0];

        /// <summary>The identity of the database that declares initech.</summary>
        public string Second => Everything[1];

        /// <summary>The first two databases.</summary>
        public string[] Both => [First, Second];

        /// <param name="tenantedRows">Whether <c>Order</c> is multi-tenanted.</param>
        /// <param name="policy">Whether the store policy is on, asked of <see cref="Policies" />.</param>
        /// <param name="declaredTwice">Whether acme is declared in the second database as well as the first.</param>
        /// <param name="extraDatabases">Further databases, each declaring one tenant named <c>tenant-N</c>, N from 2.</param>
        public static async Task<Harness> CreateAsync(bool tenantedRows, bool policy = false, bool declaredTwice = false, int extraDatabases = 0)
        {
            ServiceCollection services = new();
            services.AddMarten(x =>
            {
                x.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddMultipleTenantDatabase($"Host=zw38.invalid;Database={FirstDatabase};Username=none;Password=none", "zw38-first").ForTenants("acme", "globex");
                    tenancy.AddMultipleTenantDatabase($"Host=zw38.invalid;Database={SecondDatabase};Username=none;Password=none", "zw38-second")
                        .ForTenants(declaredTwice ? ["initech", "acme"] : ["initech"]);

                    for (int i = 0; i < extraDatabases; i++)
                    {
                        tenancy.AddMultipleTenantDatabase($"Host=zw38.invalid;Database=zw38_extra_{i};Username=none;Password=none", "zw38-extra-" + i).ForTenants("tenant-" + (i + 2));
                    }
                });

                if (tenantedRows)
                {
                    x.Schema.For<Order>().MultiTenanted();
                }
                else
                {
                    x.Schema.For<Order>();
                }
            });

            ServiceProvider provider = services.BuildServiceProvider();

            var options = Options.Create(new MartenStudioOptions { StoreAuthorizationPolicy = policy ? StorePolicy : null });
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();
            var (sources, catalog, _) = MartenSpine.Scope(
                options,
                new MartenStoreRegistry(services),
                MartenSpine.Authorization(options, policies, users),
                new TenantDiscovery(options, NullLogger<TenantDiscovery>.Instance),
                provider);

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            string[] names = [FirstDatabase, SecondDatabase, .. Enumerable.Range(0, extraDatabases).Select(static i => "zw38_extra_" + i)];
            return new Harness(
                provider,
                sources,
                catalog,
                policies,
                [.. names.Select(name => databases.Single(x => x.Id.Name == name).Id.Identity)]);
        }

        /// <summary>A circuit's state over the catalog, opened on the tenancy's first listed database.</summary>
        public async Task<StudioState> StateAsync()
        {
            StudioState state = new(Catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
            await state.EnsureInitializedAsync(Token);
            return state;
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();
    }
}
