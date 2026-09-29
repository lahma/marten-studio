using Bunit;

using Marten;
using Marten.Storage;

using MartenStudio.Components.Layout;
using MartenStudio.Services;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Services;

/// <summary>
/// DB-0-fix, item 10: the header's tenant selector is a listing like the store and database pickers, and
/// it is filtered through <c>StoreAuthorizationPolicy</c> like them.
/// </summary>
/// <remarks>
/// <para>
/// Plan section 4.2 (v1) says <c>StudioState</c> holds authorization-filtered listings and that the count of
/// tenants is itself withheld. The store and database listings were filtered through
/// <c>StudioAuthorization.FilterAsync</c>; the tenant listing was not - <c>StudioScopeCatalog.DescribeAsync</c>
/// handed <c>TenantDiscovery</c>'s answer straight to the header. A visitor whose policy allows one tenant
/// of two was therefore shown both names, could pick the other, and was refused by the layout only after
/// picking it.
/// </para>
/// <para>
/// The real catalog over a store that is never connected to: <c>KnownTenantIds</c> answers discovery
/// without a query, which is also the tier where the listing is one list for every visitor.
/// </para>
/// </remarks>
public class StudioScopeCatalogTenantFilterTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-tenant-filter.invalid;Database=none;Username=none;Password=none";

    private const string StorePolicy = "store-policy";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_visitor_allowed_one_tenant_of_two_is_offered_that_one_only()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);
        harness.Policies.Allow(static resource => resource.TenantId is null or "acme");

        StoreScopeFacts facts = await harness.Catalog.DescribeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, Token);

        facts.ShowTenantSelector.Should().BeTrue("the store has a conjoined document type");
        facts.Tenants.Ids.Should().Equal(["acme"], "globex is a tenant this visitor may not address, and its name is not theirs to learn");
        facts.Tenants.Source.Should().Be(TenantListSource.Configured, "filtering narrows the list; it does not change where it came from");
    }

    /// <summary>
    /// Each id is asked about exactly as the resolver would ask when that scope is resolved: the store
    /// policy, this store, this database, that tenant, and no capability.
    /// </summary>
    [Fact]
    public async Task Each_tenant_is_asked_about_as_the_scope_it_would_become()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);

        await harness.Catalog.DescribeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, Token);

        harness.Policies.Calls.Should().Contain(
        [
            (StorePolicy, new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, "acme", null)),
            (StorePolicy, new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, "globex", null)),
        ]);
    }

    /// <summary>
    /// And the state's own check - that <c>SetScopeAsync</c> ignores what the last listing did not carry -
    /// now keeps a refused tenant out of the active scope instead of passing it to the layout to refuse.
    /// </summary>
    [Fact]
    public async Task A_refused_tenant_picked_anyway_never_becomes_the_active_scope()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);
        harness.Policies.Allow(static resource => resource.TenantId is null or "acme");

        var state = new StudioState(harness.Catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
        await state.EnsureInitializedAsync(Token);

        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, "globex", Token);
        state.ActiveScope!.TenantId.Should().BeNull("a tenant the listing withheld is not one the state accepts");

        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, "acme", Token);
        state.ActiveScope!.TenantId.Should().Be("acme");
    }

    /// <summary>The anti-vacuity half: with no store policy, every tenant is listed, as before.</summary>
    [Fact]
    public async Task Without_a_store_policy_every_tenant_is_listed()
    {
        await using Harness harness = await Harness.CreateAsync(storePolicy: null);
        harness.Policies.DenyEverything();

        StoreScopeFacts facts = await harness.Catalog.DescribeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, Token);

        facts.Tenants.Ids.Should().Equal("acme", "globex");
        harness.Policies.Calls.Should().BeEmpty("nothing is asked of a policy that is not configured");
    }

    /// <summary>
    /// A truncated listing is a free-text box, because it is the only way to reach a tenant past the first
    /// two hundred - and its hint says how many tenants there are. With a store policy, it does not.
    /// </summary>
    [Fact]
    public async Task The_free_text_tenant_box_withholds_the_count_under_a_store_policy()
    {
        await using var context = new StudioComponentContext().WithPolicy("default");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: true, TenantListSource.Queried));

        IRenderedComponent<ScopeSelector> selector = context.Render<ScopeSelector>();

        string? hint = selector.WaitForElement("#ms-tenant-select").GetAttribute("title");
        hint.Should().Be("Type the tenant id").And.NotContain(TenantDiscovery.MaxListedTenants.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Without_a_store_policy_the_free_text_box_still_says_why_it_is_a_box()
    {
        await using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: true, TenantListSource.Queried));

        IRenderedComponent<ScopeSelector> selector = context.Render<ScopeSelector>();

        selector.WaitForElement("#ms-tenant-select").GetAttribute("title")
            .Should().Be($"More than {TenantDiscovery.MaxListedTenants} tenants - type the tenant id");
    }

    /// <summary>A document type that is tenanted per row, so that a tenant selector means something.</summary>
    public sealed class TenantedThing
    {
        public Guid Id { get; set; }
    }

    /// <summary>The real catalog, over a conjoined store with two known tenants and a policy the test holds.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        private Harness(ServiceProvider provider, StudioScopeCatalog catalog, TestStoreAuthorizationService policies, string databaseId)
        {
            this.provider = provider;
            Catalog = catalog;
            Policies = policies;
            DatabaseId = databaseId;
        }

        public StudioScopeCatalog Catalog { get; }

        public TestStoreAuthorizationService Policies { get; }

        public string DatabaseId { get; }

        public static async Task<Harness> CreateAsync(string? storePolicy)
        {
            var options = new MartenStudioOptions { StoreAuthorizationPolicy = storePolicy };
            options.KnownTenantIds.Add("acme");
            options.KnownTenantIds.Add("globex");

            var services = new ServiceCollection();
            services.AddMarten(x =>
            {
                x.Connection(DummyConnectionString);
                x.Schema.For<TenantedThing>().MultiTenanted();
            });

            ServiceProvider provider = services.BuildServiceProvider();

            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();
            var authorization = new StudioAuthorization(Options.Create(options), policies, users);

            var catalog = new StudioScopeCatalog(
                Options.Create(options),
                new MartenStoreRegistry(services),
                authorization,
                new TenantDiscovery(Options.Create(options), NullLogger<TenantDiscovery>.Instance),
                provider,
                NullLogger<StudioScopeCatalog>.Instance);

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(provider, catalog, policies, databases[0].Id.Identity);
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();
    }
}
