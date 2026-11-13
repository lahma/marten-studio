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
/// its database from that declaration where documents and events are single-tenanted and no database lists a tenant at all.
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

        TenantDatabaseHit? initech = await harness.Catalog.FindTenantDatabaseAsync(Key, [harness.First, harness.Second], "initech", Token);
        TenantDatabaseHit? globex = await harness.Catalog.FindTenantDatabaseAsync(Key, [harness.First, harness.Second], "globex", Token);

        initech!.DatabaseId.Should().Be(harness.Second);
        globex!.DatabaseId.Should().Be(harness.First);
        initech.Facts.ShowTenantSelector.Should().BeFalse("rows are single-tenanted: the database is the scope");
        initech.Facts.Tenants.Source.Should().Be(TenantListSource.Unavailable);
    }

    [Fact]
    public async Task A_tenant_nobody_declared_is_a_miss()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);

        (await harness.Catalog.FindTenantDatabaseAsync(Key, [harness.First, harness.Second], "umbrella", Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_tenant_declared_in_a_database_that_was_not_offered_is_a_miss()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);

        (await harness.Catalog.FindTenantDatabaseAsync(Key, [harness.First], "initech", Token)).Should().BeNull();
    }

    [Fact]
    public async Task A_database_the_store_policy_refuses_is_a_miss_even_when_it_is_named()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, StorePolicy);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second);

        // Named to the catalog, as a caller that did not filter would: the catalog asks (store, db, no tenant) itself.
        (await harness.Catalog.FindTenantDatabaseAsync(Key, [harness.First, harness.Second], "initech", Token)).Should().BeNull();
        harness.Policies.Calls.Should().Contain((StorePolicy, new MartenStoreResource(Key, harness.Second, null, null)));
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
    }

    [Fact]
    public async Task A_state_keeps_the_database_on_screen_for_a_tenant_nobody_declared()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false);
        StudioState state = await harness.StateAsync();
        string usual = state.ActiveScope!.DatabaseId;

        await state.SetScopeAsync(Key, null, "umbrella", Token);

        state.ActiveScope.Should().Be(new StudioScope(Key, usual, null));
    }

    [Fact]
    public async Task A_state_does_not_open_a_database_the_visitor_may_not_see()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: false, StorePolicy);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second);
        StudioState state = await harness.StateAsync();

        state.AvailableDatabases.Select(static x => x.Identity).Should().Equal(harness.First);

        await state.SetScopeAsync(Key, null, "initech", Token);

        state.ActiveScope.Should().Be(
            new StudioScope(Key, harness.First, null),
            "initech is declared in a database this visitor cannot see: a miss, and nothing about it is shown");
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
    }

    [Fact]
    public async Task With_tenanted_rows_the_declaration_places_the_tenant_and_the_policy_filtered_listing_must_carry_it()
    {
        await using Harness harness = await Harness.CreateAsync(tenantedRows: true, StorePolicy);

        TenantDatabaseHit? hit = await harness.Catalog.FindTenantDatabaseAsync(Key, [harness.First, harness.Second], "initech", Token);
        hit!.DatabaseId.Should().Be(harness.Second);
        hit.Facts.Tenants.Ids.Should().Contain("initech");

        // The policy lets this visitor address no tenant in the second database: the declaration places initech there, and the listing refuses it.
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Second || resource.TenantId is null);
        (await harness.Catalog.FindTenantDatabaseAsync(Key, [harness.First, harness.Second], "initech", Token)).Should().BeNull();
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

        var (sources, _, _) = MartenSpine.Scope(
            options,
            new MartenStoreRegistry(services),
            MartenSpine.Authorization(options, new TestStoreAuthorizationService(), users),
            new TenantDiscovery(options, NullLogger<TenantDiscovery>.Instance),
            provider);

        sources.TryResolve(sources.Registrations()[0], out IStudioSource? source).IsAvailable.Should().BeTrue();
        IReadOnlyList<IStudioDatabase> databases = await source!.DatabasesAsync(Token);

        source.DeclaresTenants.Should().BeFalse("a one-database store declares nothing");
        (await source.DeclaredTenantsAsync(databases[0], databases, Token)).Should().Be(TenantList.Unavailable);
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
            string first,
            string second)
        {
            this.provider = provider;
            Sources = sources;
            Catalog = catalog;
            Policies = policies;
            First = first;
            Second = second;
        }

        public MartenStudioSourceProvider Sources { get; }

        public StudioScopeCatalog Catalog { get; }

        public TestStoreAuthorizationService Policies { get; }

        /// <summary>The identity of the database that declares acme and globex.</summary>
        public string First { get; }

        /// <summary>The identity of the database that declares initech.</summary>
        public string Second { get; }

        public static async Task<Harness> CreateAsync(bool tenantedRows, string? storePolicy = null)
        {
            ServiceCollection services = new();
            services.AddMarten(x =>
            {
                x.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddMultipleTenantDatabase($"Host=zw38.invalid;Database={FirstDatabase};Username=none;Password=none", "zw38-first").ForTenants("acme", "globex");
                    tenancy.AddMultipleTenantDatabase($"Host=zw38.invalid;Database={SecondDatabase};Username=none;Password=none", "zw38-second").ForTenants("initech");
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

            var options = Options.Create(new MartenStudioOptions { StoreAuthorizationPolicy = storePolicy });
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

            return new Harness(
                provider,
                sources,
                catalog,
                policies,
                databases.Single(static x => x.Id.Name == FirstDatabase).Id.Identity,
                databases.Single(static x => x.Id.Name == SecondDatabase).Id.Identity);
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
