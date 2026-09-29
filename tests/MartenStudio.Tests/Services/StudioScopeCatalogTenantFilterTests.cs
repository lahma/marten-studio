using Bunit;

using Marten;
using Marten.Storage;

using MartenStudio.Components.Layout;
using MartenStudio.Services;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// DB-0-fix-2, F7: under a policy the listing is never claimed to be complete, whether or not
    /// discovery hit its cap - "there may be tenants this list does not show" is true for every visitor a
    /// policy filters, and saying it only when discovery found more than two hundred told them it had.
    /// </summary>
    [Fact]
    public async Task Under_a_store_policy_the_tenant_listing_never_claims_to_be_complete()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);
        harness.Policies.Allow(static resource => resource.TenantId is null or "acme");

        StoreScopeFacts facts = await harness.Catalog.DescribeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, Token);

        facts.Tenants.Ids.Should().Equal("acme");
        facts.Tenants.IsTruncated.Should().BeTrue("two known tenants were not truncated, and the visitor is not told so");
    }

    /// <summary>
    /// What is typed into "Other…" becomes the scope, so an allowed tenant past the cap can be reached - and
    /// one the policy refuses is refused where every scope is, by the resolver and the layout's own check,
    /// never by a listing that would have to name it to refuse it.
    /// </summary>
    [Fact]
    public async Task A_typed_tenant_is_resolved_and_refused_like_any_other_scope()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);
        harness.Policies.Allow(static resource => resource.TenantId is null or "acme");

        var state = new StudioState(harness.Catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
        await state.EnsureInitializedAsync(Token);

        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, "acme", Token);
        state.ActiveScope!.TenantId.Should().Be("acme");

        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, "globex", Token);
        state.ActiveScope!.TenantId.Should().Be("globex", "a typed id is taken as typed, whatever the store holds");
        (await harness.Authorization.IsAuthorizedAsync(state.ActiveScope, capability: null, Token))
            .Should().BeFalse("and the layout's own check refuses it, as the resolver does on every data call");

        state.AvailableTenants.Ids.Should().NotContain("globex", "the listing still never names it");
    }

    /// <summary>
    /// DB-0-fix-2, F8: a store policy that throws while the discovered tenants are filtered is the host's
    /// authorization handler failing, not tenant discovery. It was logged as 9219
    /// <c>TenantDiscoveryFailed</c>, which sends whoever reads it to the database. It is 9221 now,
    /// throttled, and nothing is listed: a policy that could not answer has allowed nothing.
    /// </summary>
    [Fact]
    public async Task A_policy_that_throws_while_tenants_are_filtered_is_event_9221_and_lists_nothing()
    {
        var logs = new CapturingLoggerProvider();
        await using Harness harness = await Harness.CreateAsync(StorePolicy, logs);
        harness.Policies.Allow(static resource => resource.TenantId is null
            ? true
            : throw new InvalidOperationException("The host's tenant handler could not reach its directory."));

        for (int ask = 0; ask < 5; ask++)
        {
            StoreScopeFacts facts = await harness.Catalog.DescribeAsync(MartenStoreRegistry.DefaultStoreKey, harness.DatabaseId, Token);

            facts.Tenants.Ids.Should().BeEmpty();
            facts.Tenants.Source.Should().Be(TenantListSource.Unavailable);
        }

        logs.Entries.Should().NotContain(static x => x.EventId.Id == 9219, "discovery worked; the policy did not");
        logs.Entries.Where(static x => x.EventId.Id == 9221).Should().HaveCount(5);
        logs.Entries.Where(static x => x.EventId.Id == 9221 && x.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Should().ContainSingle("throttled: the header asks on every scope change in every circuit");
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
    /// DB-0-fix-2, F7: under a store policy the tenant control is always the filtered list with an
    /// "Other…" entry, and never the free-text box - which said "more than two hundred" to anyone who
    /// looked, and hid the tenants the visitor could have picked from a list.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Under_a_store_policy_the_tenant_control_is_the_filtered_list_with_Other(bool truncated)
    {
        await using var context = new StudioComponentContext().WithPolicy("default");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme", "initech"], IsTruncated: truncated, TenantListSource.Queried));

        IRenderedComponent<ScopeSelector> selector = context.Render<ScopeSelector>();

        var control = selector.WaitForElement("#ms-tenant-select");
        control.TagName.Should().Be("SELECT", "a list, whatever the store holds");
        control.QuerySelectorAll("option").Select(static x => x.TextContent.Trim())
            .Should().Equal("All tenants", "acme", "initech", "Other…");

        selector.FindAll("#ms-tenant-other").Should().BeEmpty("the box opens only when Other… is chosen");
        selector.Markup.Should().NotContain(
            TenantDiscovery.MaxListedTenants.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "how many tenants the store has is not the visitor's to learn");
    }

    /// <summary>
    /// Choosing "Other…" opens a box and changes nothing; what is typed there becomes the scope, and the
    /// list keeps "Other…" selected for a tenant it does not name.
    /// </summary>
    [Fact]
    public async Task Choosing_Other_opens_a_box_whose_tenant_becomes_the_scope()
    {
        await using var context = new StudioComponentContext().WithPolicy("default");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: true, TenantListSource.Queried));

        IRenderedComponent<ScopeSelector> selector = context.Render<ScopeSelector>();

        selector.WaitForElement("#ms-tenant-select").Change(" (other)");
        context.State.ActiveScope!.TenantId.Should().BeNull("choosing Other… is not choosing a tenant");

        selector.WaitForElement("#ms-tenant-other").Change("tenant-past-the-cap");

        selector.WaitForAssertion(() =>
        {
            context.State.ActiveScope!.TenantId.Should().Be("tenant-past-the-cap");
            selector.Find("#ms-tenant-other").GetAttribute("value").Should().Be("tenant-past-the-cap");
            selector.Find("#ms-tenant-select option:checked").TextContent.Trim().Should().Be("Other…");
        });

        selector.Find("#ms-tenant-select").Change("acme");
        selector.WaitForAssertion(() =>
        {
            context.State.ActiveScope!.TenantId.Should().Be("acme");
            selector.FindAll("#ms-tenant-other").Should().BeEmpty();
        });
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

        private Harness(
            ServiceProvider provider,
            StudioScopeCatalog catalog,
            TestStoreAuthorizationService policies,
            StudioAuthorization authorization,
            string databaseId)
        {
            this.provider = provider;
            Catalog = catalog;
            Policies = policies;
            Authorization = authorization;
            DatabaseId = databaseId;
        }

        public StudioScopeCatalog Catalog { get; }

        public TestStoreAuthorizationService Policies { get; }

        /// <summary>The same questions the layout asks of the active scope.</summary>
        public StudioAuthorization Authorization { get; }

        public string DatabaseId { get; }

        /// <param name="storePolicy">The store policy's name, or <see langword="null" /> for none.</param>
        /// <param name="logs">Where the catalog's log goes, through a real throttle, when a test reads it.</param>
        public static async Task<Harness> CreateAsync(string? storePolicy, CapturingLoggerProvider? logs = null)
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
                logs is null
                    ? NullLogger<StudioScopeCatalog>.Instance
                    : logs.CreateFactory().CreateLogger<StudioScopeCatalog>(),
                new StudioLogThrottle(new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero))));

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(provider, catalog, policies, authorization, databases[0].Id.Identity);
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();
    }
}
