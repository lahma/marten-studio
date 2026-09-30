using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Configuration;

/// <summary>
/// DB-0-fix-2, F5: the Configuration page's database card is a listing, and it is filtered like every
/// other one.
/// </summary>
/// <remarks>
/// <para>
/// <c>ITenancy.DescribeDatabasesAsync</c> answers for the whole store, and <c>/marten/config</c> printed
/// every database's server and name and every tenant's id to any visitor the scope admitted - the same
/// disclosure DB-0-fix closed in the scope selector. Databases are asked about as
/// <c>(store, database, null)</c> and tenants as <c>(store, database, tenant)</c>, with the store policy and
/// no capability, exactly as the selector asks.
/// </para>
/// <para>
/// The real service over a statically multi-tenanted store that is never connected to: its descriptors
/// are answered from memory.
/// </para>
/// </remarks>
public class ConfigurationFilteringTests
{
    private const string PrimaryConnectionString =
        "Host=marten-studio-config-filter.invalid;Database=primary;Username=none;Password=none;Timeout=2";

    private const string SecondaryConnectionString =
        "Host=marten-studio-config-filter.invalid;Database=secondary;Username=none;Password=none;Timeout=2";

    private const string StorePolicy = "studio-store";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_visitor_sees_only_the_databases_and_tenants_the_store_policy_allows()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);
        harness.Policies.Allow(resource =>
            resource.DatabaseIdentifier == harness.Primary && resource.TenantId is null or "acme");

        StudioConfiguration configuration = await harness.DescribeAsync();

        ConfiguredDatabase only = configuration.Store.Databases.Should().ContainSingle(
            "the secondary is a database this visitor may not address, and its name and server are not theirs to learn").Subject;

        only.DatabaseName.Should().Be("primary");
        only.TenantIds.Should().Equal(["acme"], "initech lives here too, and is a tenant this visitor may not address");
        configuration.Store.DatabaseNotice.Should().BeNull("nothing failed; two things were withheld, and nothing says so");
    }

    /// <summary>Each database and each tenant is asked about as the scope it would become.</summary>
    [Fact]
    public async Task Each_database_and_tenant_is_asked_about_as_the_scope_it_would_become()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);

        await harness.DescribeAsync();

        harness.Policies.Calls.Should().Contain(
        [
            (StorePolicy, new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, harness.Primary, null, null)),
            (StorePolicy, new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, harness.Secondary, null, null)),
            (StorePolicy, new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, harness.Primary, "acme", null)),
            (StorePolicy, new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, harness.Primary, "initech", null)),
            (StorePolicy, new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, harness.Secondary, "globex", null)),
        ]);
    }

    /// <summary>A tenant is not asked about at all on a database the visitor may not address.</summary>
    [Fact]
    public async Task A_refused_database_does_not_have_its_tenants_asked_about()
    {
        await using Harness harness = await Harness.CreateAsync(StorePolicy);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier == harness.Primary);

        await harness.DescribeAsync();

        harness.Policies.Calls.Should().NotContain(x => x.Resource.TenantId == "globex");
    }

    /// <summary>The anti-vacuity half: with no store policy the card lists everything, as before.</summary>
    [Fact]
    public async Task Without_a_store_policy_every_database_and_tenant_is_listed()
    {
        await using Harness harness = await Harness.CreateAsync(storePolicy: null);
        harness.Policies.DenyEverything();

        StudioConfiguration configuration = await harness.DescribeAsync();

        configuration.Store.Databases.Select(static x => x.DatabaseName).Should().BeEquivalentTo(["primary", "secondary"]);
        configuration.Store.Databases.SelectMany(static x => x.TenantIds).Should().BeEquivalentTo(["acme", "initech", "globex"]);
        harness.Policies.Calls.Should().BeEmpty("nothing is asked of a policy that is not configured");
    }

    /// <summary>A studio over a two-database store with three tenants, and the policy in the test's hand.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, string primary, string secondary)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Policies = policies;
            Primary = primary;
            Secondary = secondary;
        }

        public TestStoreAuthorizationService Policies { get; }

        public string Primary { get; }

        public string Secondary { get; }

        public Task<StudioConfiguration> DescribeAsync() =>
            scope.ServiceProvider.GetRequiredService<IConfigurationService>()
                .DescribeAsync(new StudioScope(MartenStoreRegistry.DefaultStoreKey, Primary, null), Token);

        public static async Task<Harness> CreateAsync(string? storePolicy)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(options => options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(PrimaryConnectionString, "primary").ForTenants("acme", "initech");
                tenancy.AddMultipleTenantDatabase(SecondaryConnectionString, "secondary").ForTenants("globex");
            }));

            services.AddMartenStudio(options => options.StoreAuthorizationPolicy = storePolicy);

            // After AddMartenStudio, so these win over what the framework registrations contributed. The
            // Postgres version is the Overview's to fetch, and nothing here is listening for it.
            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);
            services.AddSingleton<IStoreInfoService>(new FakeStoreInfoService());

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(
                provider,
                policies,
                databases.Single(static x => x.Id.Name == "primary").Id.Identity,
                databases.Single(static x => x.Id.Name == "secondary").Id.Identity);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
