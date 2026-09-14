using Marten;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Services;

/// <summary>
/// Tenant discovery, tiers one and two. Tier three is a bounded query against <c>mt_streams</c> and
/// needs a real Postgres, so it belongs to the integration suite (P2).
/// </summary>
public class TenantDiscoveryTest
{
    private const string DummyConnectionString =
        "Host=marten-studio-tenants-test.invalid;Database=none;Username=none;Password=none";

    private const string FirstConnectionString = "Host=h1.invalid;Database=none;Username=u;Password=p";

    private const string SecondConnectionString = "Host=h2.invalid;Database=other;Username=u;Password=p";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TenantDiscovery Create(MartenStudioOptions options, TimeProvider? time = null) =>
        new(Options.Create(options), NullLogger<TenantDiscovery>.Instance, time ?? TimeProvider.System);

    private static (IDocumentStore Store, ServiceProvider Provider) BuildStore(Action<StoreOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddMarten(configure);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IDocumentStore>(), provider);
    }

    // -------------------------------------------------------------------------------------------
    // Tier 1: the host said so
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Configured_tenant_ids_are_used_and_nothing_is_discovered()
    {
        var options = new MartenStudioOptions();
        options.KnownTenantIds.Add("acme");
        options.KnownTenantIds.Add("globex");

        var (store, provider) = BuildStore(x => x.Connection(DummyConnectionString));
        using var _ = provider;

        var tenants = await Create(options).DiscoverAsync("default", store, store.Storage.Database, Token);

        tenants.Source.Should().Be(TenantListSource.Configured);
        tenants.Ids.Should().Equal("acme", "globex");
        tenants.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task Discovery_turned_off_and_nothing_configured_is_unavailable_rather_than_empty()
    {
        var options = new MartenStudioOptions { DiscoverTenantIds = false };

        var (store, provider) = BuildStore(x => x.Connection(DummyConnectionString));
        using var _ = provider;

        var tenants = await Create(options).DiscoverAsync("default", store, store.Storage.Database, Token);

        tenants.Source.Should().Be(TenantListSource.Unavailable,
            "'cannot report' is a value, drawn differently from 'there are none'");
        tenants.Ids.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------
    // Tier 2: Marten's own database descriptors
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A statically multi-tenanted store knows its tenants from configuration, so they can be listed
    /// without touching the database at all.
    /// </summary>
    [Fact]
    public async Task Static_multi_tenancy_is_answered_from_Martens_own_descriptors()
    {
        var (store, provider) = BuildStore(x => x.MultiTenantedDatabases(databases =>
        {
            databases.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme", "globex");
            databases.AddMultipleTenantDatabase(SecondConnectionString, "tenants-b").ForTenants("initech");
        }));
        using var _ = provider;

        // Marten's own database identity is composed from the server and the database name
        // ("h1!invalid.none"), not from the logical name the host gave it ("tenants-a").
        var databases = await store.Storage.AllDatabases();
        var first = databases.Single(x => x.Id.Server.Equals("h1.invalid", StringComparison.OrdinalIgnoreCase));

        var tenants = await Create(new MartenStudioOptions()).DiscoverAsync("default", store, first, Token);

        tenants.Source.Should().Be(TenantListSource.Descriptor);
        tenants.Ids.Should().Equal("acme", "globex");
        tenants.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task The_descriptors_of_one_database_do_not_leak_into_another()
    {
        var (store, provider) = BuildStore(x => x.MultiTenantedDatabases(databases =>
        {
            databases.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme", "globex");
            databases.AddMultipleTenantDatabase(SecondConnectionString, "tenants-b").ForTenants("initech");
        }));
        using var _ = provider;

        var databases = await store.Storage.AllDatabases();
        var second = databases.Single(x => x.Id.Server.Equals("h2.invalid", StringComparison.OrdinalIgnoreCase));

        var tenants = await Create(new MartenStudioOptions()).DiscoverAsync("default", store, second, Token);

        tenants.Ids.Should().Equal("initech");
    }

    /// <summary>
    /// A single-tenant store has no tenants to discover, and the event store is not conjoined, so tier
    /// three is not even attempted - which is what keeps a single-tenant studio from issuing a
    /// <c>select distinct</c> nobody asked for.
    /// </summary>
    [Fact]
    public async Task A_single_tenant_store_with_a_single_tenant_event_store_reports_unavailable()
    {
        var (store, provider) = BuildStore(x => x.Connection(DummyConnectionString));
        using var _ = provider;

        var tenants = await Create(new MartenStudioOptions()).DiscoverAsync("default", store, store.Storage.Database, Token);

        tenants.Source.Should().Be(TenantListSource.Unavailable);
    }

    [Fact]
    public async Task The_answer_is_cached_for_a_minute_per_store_and_database()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var options = new MartenStudioOptions();

        var (store, provider) = BuildStore(x => x.MultiTenantedDatabases(databases =>
            databases.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme")));
        using var _ = provider;

        var discovery = Create(options, time);
        var database = (await store.Storage.AllDatabases())[0];

        var first = await discovery.DiscoverAsync("default", store, database, Token);
        var second = await discovery.DiscoverAsync("default", store, database, Token);

        second.Should().BeSameAs(first, "the second call inside the window is the cached list");

        time.Advance(TimeSpan.FromSeconds(61));

        var third = await discovery.DiscoverAsync("default", store, database, Token);
        third.Should().NotBeSameAs(first, "the window has passed");
        third.Ids.Should().Equal(first.Ids);
    }

    [Fact]
    public async Task Invalidate_forgets_the_cache_for_the_refresh_button()
    {
        var options = new MartenStudioOptions();
        var (store, provider) = BuildStore(x => x.MultiTenantedDatabases(databases =>
            databases.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme")));
        using var _ = provider;

        var discovery = Create(options, new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        var database = (await store.Storage.AllDatabases())[0];

        var first = await discovery.DiscoverAsync("default", store, database, Token);
        discovery.Invalidate();
        var second = await discovery.DiscoverAsync("default", store, database, Token);

        second.Should().NotBeSameAs(first);
    }

    // -------------------------------------------------------------------------------------------
    // What the selector is allowed to accept
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void A_null_tenant_is_always_acceptable_because_it_means_all_of_them()
    {
        TenantDiscovery.IsAcceptable(TenantList.Unavailable, null).Should().BeTrue();
        TenantDiscovery.IsAcceptable(TenantList.Unavailable, "   ").Should().BeTrue();
    }

    [Fact]
    public void Only_a_listed_tenant_is_acceptable_while_the_listing_is_complete()
    {
        var list = new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured);

        TenantDiscovery.IsAcceptable(list, "acme").Should().BeTrue();
        TenantDiscovery.IsAcceptable(list, "ACME").Should().BeFalse("tenant ids are ordinal, the way Marten stores them");
        TenantDiscovery.IsAcceptable(list, "initech").Should().BeFalse();
    }

    /// <summary>
    /// A truncated listing is the case the free-text box exists for, so an unlisted id has to be
    /// accepted - within reason.
    /// </summary>
    [Fact]
    public void A_truncated_listing_accepts_a_plausible_unlisted_tenant()
    {
        var list = new TenantList(["acme"], IsTruncated: true, TenantListSource.Queried);

        TenantDiscovery.IsAcceptable(list, "initech").Should().BeTrue();
        TenantDiscovery.IsAcceptable(list, " padded ").Should().BeFalse();
        TenantDiscovery.IsAcceptable(list, new string('t', 201)).Should().BeFalse();
        TenantDiscovery.IsAcceptable(list, new string('t', 200)).Should().BeTrue();
    }
}
