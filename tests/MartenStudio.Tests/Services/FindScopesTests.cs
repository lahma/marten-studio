using JasperFx.Descriptors;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Find;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Support;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-3-FU2: which tenants a Find reads across, decided once per Find. With a store policy and no tenant selected it is always
/// the tenants the header lists for the visitor - however many there are, or whether discovery answered - so that a Find says the same
/// thing to a visitor in every store; and it does not depend on the event store being describable.
/// </summary>
public class FindScopesTests
{
    private static readonly StudioScope Whole = new("default", "primary", null);

    private sealed class ThrowingCatalog : IStudioScopeCatalog
    {
        public Task<StoreListings> ListStoresAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("the policy threw");

        public Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default) => throw new InvalidOperationException("the policy threw");

        public Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("the policy threw");

        public void Invalidate()
        {
        }
    }

    private static StudioAuthorization Authorization(bool policy)
    {
        MartenStudioOptions options = new() { StoreAuthorizationPolicy = policy ? "Visitors" : null };

        return new StudioAuthorization(
            new MartenStudioSettings(Microsoft.Extensions.Options.Options.Create(options)),
            MartenStudioDescriptor.Instance,
            new TestStoreAuthorizationService(),
            new TestAuthenticationStateProvider());
    }

    private static FakeStudioScopeCatalog Catalog(TenantList? tenants, bool selector = true)
    {
        FakeStudioScopeCatalog catalog = new FakeStudioScopeCatalog().WithStore("default", "Default", DatabaseCardinality.Single, "primary");
        if (selector && tenants is not null)
        {
            catalog.WithTenants("default", tenants);
        }

        return catalog;
    }

    private static Task<FindTenants?> TenantsAsync(bool policy, StudioScope scope, IStudioScopeCatalog catalog, FakeEventDataService? events = null) =>
        new FindScopes(events ?? new FakeEventDataService(), catalog, Authorization(policy)).TenantsAsync(scope, FindQuery.Parse("x"), CancellationToken.None);

    [Fact]
    public async Task With_no_policy_nothing_is_narrowed_and_the_catalog_is_not_asked()
    {
        FindTenants? tenants = await TenantsAsync(policy: false, Whole, new ThrowingCatalog());

        tenants.Should().BeSameAs(FindTenants.Unrestricted);
    }

    [Fact]
    public async Task With_a_policy_and_a_tenant_selected_the_tenant_is_the_restriction()
    {
        FindTenants? tenants = await TenantsAsync(policy: true, Whole with { TenantId = "acme" }, new ThrowingCatalog());

        tenants.Should().BeSameAs(FindTenants.Unrestricted);
    }

    [Fact]
    public async Task With_a_policy_and_no_tenant_the_visitor_is_read_in_the_tenants_the_header_lists()
    {
        FindTenants? tenants = await TenantsAsync(policy: true, Whole, Catalog(new TenantList(["acme", "globex"], false, TenantListSource.Configured)));

        tenants!.Only.Should().Equal("acme", "globex");
    }

    [Fact]
    public async Task A_store_with_no_tenant_selector_has_nothing_tenanted_to_restrict()
    {
        FindTenants? tenants = await TenantsAsync(policy: true, Whole, Catalog(tenants: null, selector: false));

        tenants.Should().BeSameAs(FindTenants.Unrestricted);
    }

    [Fact]
    public async Task The_restriction_is_the_same_kind_of_answer_whether_the_list_was_cut_off_empty_or_unavailable()
    {
        FindTenants? complete = await TenantsAsync(policy: true, Whole, Catalog(new TenantList(["acme"], false, TenantListSource.Queried)));
        FindTenants? cutOff = await TenantsAsync(policy: true, Whole, Catalog(new TenantList(["acme"], true, TenantListSource.Queried)));
        FindTenants? empty = await TenantsAsync(policy: true, Whole, Catalog(new TenantList([], false, TenantListSource.Queried)));
        FindTenants? unavailable = await TenantsAsync(policy: true, Whole, Catalog(TenantList.Unavailable));

        complete!.Only.Should().Equal("acme");
        // Being cut off at 200 changes nothing a visitor under a policy can tell.
        cutOff!.Only.Should().Equal("acme");
        empty!.Only.Should().BeEmpty();
        unavailable!.Only.Should().BeEmpty("discovery not answering reads no tenant, and says the same");
    }

    [Fact]
    public async Task A_scope_that_names_no_database_fails_closed_under_a_policy_and_the_catalog_is_not_asked()
    {
        StudioScope noDatabase = Whole with { DatabaseId = string.Empty };

        (await TenantsAsync(policy: true, noDatabase, new ThrowingCatalog())).Should().BeNull("an empty tenant list for it would read as no tenant");
        (await TenantsAsync(policy: false, noDatabase, new ThrowingCatalog())).Should().BeSameAs(FindTenants.Unrestricted, "without a policy there is nothing to read");
    }

    [Fact]
    public async Task A_catalog_that_throws_is_a_restriction_that_could_not_be_read_never_an_unrestricted_one()
    {
        (await TenantsAsync(policy: true, Whole, new ThrowingCatalog())).Should().BeNull();
    }

    [Fact]
    public async Task The_restriction_does_not_depend_on_the_event_store_and_the_context_does()
    {
        FakeEventDataService events = new() { Shape = EventStoreShape.Unavailable(new EventDataError("the database is down", null, false)) };
        FakeStudioScopeCatalog catalog = Catalog(new TenantList(["acme"], false, TenantListSource.Configured));
        FindScopes scopes = new(events, catalog, Authorization(policy: true));
        FindQuery query = FindQuery.Parse("x");

        (await scopes.TenantsAsync(Whole, query, CancellationToken.None))!.Only.Should().Equal("acme");
        (await scopes.ContextAsync(Whole, query, CancellationToken.None)).Should().BeNull("with no event store shape there is no event context");
    }
}
