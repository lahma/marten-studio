using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-1-FIX follow-up 2: a store that built and whose databases cannot be listed (an unreachable master database) is not in the
/// header's picker - it has no database to select - but it must not vanish: the catalog keeps it apart, for a visitor the store policy
/// passes for the store as a whole, and <see cref="StudioState" /> counts it.
/// </summary>
public class StudioScopeCatalogUnlistableTests
{
    private const string FirstConnection = "Host=scope1-unlistable-a.invalid;Database=one;Username=u;Password=p";

    private const string SecondConnection = "Host=scope1-unlistable-b.invalid;Database=two;Username=u;Password=p";

    private const string StorePolicy = "store-policy";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_store_whose_databases_cannot_be_listed_is_kept_apart_and_not_in_the_picker()
    {
        await using Harness harness = Harness.Create(storePolicy: null);

        StoreListings listings = await harness.Catalog.ListStoresAsync(Token);

        listings.Listed.Select(static x => x.Key).Should().Equal(nameof(IUnlistableCatalogStore));
        listings.Unlistable.Select(static x => x.Key).Should().Equal(MartenStoreRegistry.DefaultStoreKey);
    }

    [Fact]
    public async Task It_is_kept_only_for_a_visitor_the_store_policy_passes_for_the_store()
    {
        await using Harness harness = Harness.Create(StorePolicy);
        harness.Policies.Allow(static resource => resource.StoreName != MartenStoreRegistry.DefaultStoreKey);

        StoreListings listings = await harness.Catalog.ListStoresAsync(Token);

        listings.Unlistable.Should().BeEmpty("a store the visitor may not see is absent, not named as one that failed");
        listings.Listed.Select(static x => x.Key).Should().Equal(nameof(IUnlistableCatalogStore));
    }

    [Fact]
    public async Task A_store_with_no_database_the_visitor_may_see_is_still_absent()
    {
        await using Harness harness = Harness.Create(StorePolicy);

        // The other store lists, and the policy refuses every one of its databases (but not the store as a whole, which is asked with
        // an empty database identity): it is not "could not be listed" - it listed fine - so it is hidden.
        harness.Policies.Allow(static resource => resource.DatabaseIdentifier is null or "" || resource.StoreName == MartenStoreRegistry.DefaultStoreKey);

        StoreListings listings = await harness.Catalog.ListStoresAsync(Token);

        listings.Listed.Should().BeEmpty();
        listings.Unlistable.Select(static x => x.Key).Should().Equal(MartenStoreRegistry.DefaultStoreKey);
    }

    [Fact]
    public async Task The_state_counts_an_unlistable_store_as_a_store_the_visitor_may_see()
    {
        await using Harness harness = Harness.Create(storePolicy: null);

        var state = new StudioState(harness.Catalog, NullLogger<StudioState>.Instance, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        await state.EnsureInitializedAsync(Token);

        state.AvailableStores.Select(static x => x.Key).Should().Equal([nameof(IUnlistableCatalogStore)], "only a listable store is selectable");
        state.UnlistableStores.Select(static x => x.Key).Should().Equal(MartenStoreRegistry.DefaultStoreKey);
        state.KnownStoreCount.Should().Be(2);
        MartenStudioDescriptor.Instance.Navigation.SelectMany(static x => x.Items).Single(static x => x.Label == "Stores")
            .IsShownInScope!(state).Should().BeTrue("the Stores page is where a store that could not be listed is explained");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private Harness(ServiceProvider provider, StudioScopeCatalog catalog, TestStoreAuthorizationService policies)
        {
            this.provider = provider;
            Catalog = catalog;
            Policies = policies;
        }

        public StudioScopeCatalog Catalog { get; }

        public TestStoreAuthorizationService Policies { get; }

        public static Harness Create(string? storePolicy)
        {
            var options = new MartenStudioOptions { StoreAuthorizationPolicy = storePolicy };

            var services = new ServiceCollection();

            // The default store: a master table's shape, whose enumeration fails as an unreachable master database does.
            services.AddMarten(x =>
            {
                x.MultiTenantedDatabases(tenancy => tenancy.AddMultipleTenantDatabase(FirstConnection, "tenants-a").ForTenants("acme"));
                ObservedTenancy observed = ObservedTenancy.Over(x.Tenancy, masterTableShape: true);
                observed.OnBuildDatabases = static () => throw new InvalidOperationException("The master database did not answer.");
                x.Tenancy = (ITenancy) (object) observed;
            });

            services.AddMartenStore<IUnlistableCatalogStore>(x =>
            {
                x.MultiTenantedDatabases(tenancy => tenancy.AddMultipleTenantDatabase(SecondConnection, "tenants-b").ForTenants("globex"));
                x.DatabaseSchemaName = "scope1_unlistable";
            });

            ServiceProvider provider = services.BuildServiceProvider();

            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();
            StudioAuthorization authorization = MartenSpine.Authorization(Options.Create(options), policies, users);

            var (_, catalog, _) = MartenSpine.Scope(
                Options.Create(options),
                new MartenStoreRegistry(services),
                authorization,
                new TenantDiscovery(Options.Create(options), NullLogger<TenantDiscovery>.Instance),
                provider);

            return new Harness(provider, catalog, policies);
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();
    }
}

/// <summary>A store whose databases list fine. At namespace level, as Marten generates its implementation from it.</summary>
public interface IUnlistableCatalogStore : IDocumentStore;
