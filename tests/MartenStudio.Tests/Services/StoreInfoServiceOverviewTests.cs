using JasperFx.Descriptors;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Services;

/// <summary>
/// F8-D3 C3: the Overview asks each store for its databases once per load, and puts each database to the
/// store policy, instead of resolving every one.
/// </summary>
/// <remarks>
/// <para>
/// Each store's own tenancy is wrapped in <see cref="ObservedTenancy" />, which counts every
/// <c>BuildDatabases()</c> - what <c>AllDatabases()</c> is. Before this the count was N + 2 per store per
/// load: the store's empty scope resolved, an enumeration of its own, then one resolution per database. On a
/// master-table or sharded tenancy each of those is a read of the lookup table (AGENTS.md D27).
/// </para>
/// <para>
/// Nothing here has a database behind it. The server version read fails fast against an <c>.invalid</c>
/// host and is drawn as "unknown", which is a value (plan section 4.8); no assertion here is about it.
/// </para>
/// </remarks>
public class StoreInfoServiceOverviewTests
{
    private const string FirstConnectionString = "Host=f8d3-overview-a.invalid;Database=one;Username=u;Password=p";

    private const string SecondConnectionString = "Host=f8d3-overview-b.invalid;Database=two;Username=u;Password=p";

    private const string StorePolicy = "store-policy";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_overview_load_enumerates_a_store_once_and_puts_each_database_to_the_store_policy()
    {
        await using Harness harness = Harness.Create(static tenancy =>
        {
            tenancy.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme");
            tenancy.AddMultipleTenantDatabase(SecondConnectionString, "tenants-b").ForTenants("globex");
        });

        IReadOnlyList<string> identities = await harness.IdentitiesAsync();
        identities.Should().HaveCount(2).And.OnlyHaveUniqueItems("the premise: two databases");

        int before = harness.Builds;

        StudioOverview overview = await harness.Overview.GetOverviewAsync(Token);

        (harness.Builds - before).Should().Be(1, "one enumeration per store per load; it used to be N + 2");

        StoreOverview store = overview.Stores.Should().ContainSingle().Subject;
        store.IsAvailable.Should().BeTrue();
        store.Databases.Select(static x => x.Identity).Should().Equal(identities);
        store.Databases.Should().OnlyContain(static x => x.Error == null);

        List<MartenStoreResource> expected = [new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null, null)];
        expected.AddRange(identities.Select(static x => new MartenStoreResource(MartenStoreRegistry.DefaultStoreKey, x, null, null)));

        harness.Policies.Calls.Select(static x => x.Resource).Should().Equal(
            expected,
            "the store as a whole first, then each database with no tenant - exactly the questions the resolver asked");
        harness.Policies.Calls.Should().OnlyContain(static x => x.Policy == StorePolicy);
    }

    [Fact]
    public async Task A_store_the_policy_refuses_is_absent_and_nothing_of_it_is_enumerated()
    {
        await using Harness harness = Harness.Create(static tenancy =>
            tenancy.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme"));

        harness.Policies.DenyEverything();

        StudioOverview overview = await harness.Overview.GetOverviewAsync(Token);

        overview.Stores.Should().BeEmpty("a store the visitor may not see is absent, not drawn with a refusal");
        harness.Builds.Should().Be(0, "nothing about a store is enumerated before its policy has said yes");
    }

    [Fact]
    public async Task A_database_the_policy_refuses_is_absent_and_the_rest_are_drawn()
    {
        await using Harness harness = Harness.Create(static tenancy =>
        {
            tenancy.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme");
            tenancy.AddMultipleTenantDatabase(SecondConnectionString, "tenants-b").ForTenants("globex");
        });

        IReadOnlyList<string> identities = await harness.IdentitiesAsync();
        string refused = identities[1];
        harness.Policies.Allow(x => !string.Equals(x.DatabaseIdentifier, refused, StringComparison.Ordinal));

        StudioOverview overview = await harness.Overview.GetOverviewAsync(Token);

        overview.Stores.Should().ContainSingle()
            .Which.Databases.Select(static x => x.Identity).Should().Equal(identities[0]);
    }

    /// <summary>
    /// Two databases with one identity are one database to the studio, and the page keys its cards by that
    /// identity: two cards under one Blazor <c>@key</c> end the circuit rather than the render (AGENTS.md hard
    /// rule 10). Two master-table rows with one connection string are that shape; a static tenancy with one
    /// connection string under two names is the same shape without a master table.
    /// </summary>
    [Fact]
    public async Task Two_databases_with_one_identity_are_one_card_and_one_question()
    {
        await using Harness harness = Harness.Create(static tenancy =>
        {
            tenancy.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme");
            tenancy.AddMultipleTenantDatabase(FirstConnectionString, "tenants-b").ForTenants("globex");
        });

        IReadOnlyList<string> identities = await harness.IdentitiesAsync();
        identities.Should().HaveCount(2, "the premise: two database objects");
        identities.Distinct(StringComparer.OrdinalIgnoreCase).Should().ContainSingle("the premise: one identity");

        StudioOverview overview = await harness.Overview.GetOverviewAsync(Token);

        overview.Stores.Should().ContainSingle()
            .Which.Databases.Should().ContainSingle("the page keys its cards by identity")
            .Which.Identity.Should().Be(identities[0], "the first, which is the one the resolver matches");

        harness.Policies.Calls.Count(x => string.Equals(x.Resource.DatabaseIdentifier, identities[0], StringComparison.Ordinal))
            .Should().Be(1, "one database, one question");
    }

    /// <summary>
    /// A store whose databases cannot be listed - a master database that does not answer - breaks its own
    /// region and nothing else. It used to fail the whole page: the resolver's enumeration came first, and
    /// what it threw was not one of the three exceptions the Overview caught.
    /// </summary>
    [Fact]
    public async Task A_store_whose_databases_cannot_be_listed_breaks_its_own_card_and_nothing_else()
    {
        await using Harness harness = Harness.Create(
            static tenancy => tenancy.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme"),
            static services => services.AddMartenStore<IUnlistableDatabasesStore>(options =>
            {
                options.MultiTenantedDatabases(tenancy =>
                    tenancy.AddMultipleTenantDatabase(SecondConnectionString, "unlistable").ForTenants("globex"));
                options.DatabaseSchemaName = "unlistable";

                // A master table's shape - no default, a dynamic cardinality - whose enumeration fails, which is
                // what an unreachable master database looks like from here.
                ObservedTenancy observed = ObservedTenancy.Over(options.Tenancy, masterTableShape: true);
                observed.OnBuildDatabases = static () => throw new InvalidOperationException("The master database did not answer.");
                options.Tenancy = (ITenancy) (object) observed;
            }));

        StudioOverview overview = await harness.Overview.GetOverviewAsync(Token);

        overview.Stores.Select(static x => x.Key).Should().BeEquivalentTo(
            [MartenStoreRegistry.DefaultStoreKey, nameof(IUnlistableDatabasesStore)],
            "the healthy store is drawn as well");

        overview.Stores.Single(static x => x.Key == MartenStoreRegistry.DefaultStoreKey)
            .Databases.Should().ContainSingle().Which.Error.Should().BeNull();

        StoreOverview broken = overview.Stores.Single(static x => x.Key == nameof(IUnlistableDatabasesStore));
        broken.IsAvailable.Should().BeTrue("the store was built; only its databases could not be listed");
        broken.Cardinality.Should().Be(DatabaseCardinality.DynamicMultiple);

        DatabaseOverview card = broken.Databases.Should().ContainSingle().Subject;
        card.Error.Should().Contain("did not answer");
        card.Identity.Should().BeEmpty("no database could be read, so none is named");

        harness.Logs.Entries.Should().Contain(
            static x => x.EventId.Id == 9213 && x.Level == LogLevel.Warning,
            "event 9213, a Warning the first time in its window");
    }

    /// <summary>A real studio container over stores that never connect.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;
        private readonly BuildCounter builds;

        private Harness(
            ServiceProvider provider,
            IServiceScope scope,
            BuildCounter builds,
            TestStoreAuthorizationService policies,
            CapturingLoggerProvider logs)
        {
            this.provider = provider;
            this.scope = scope;
            this.builds = builds;
            Policies = policies;
            Logs = logs;
        }

        public TestStoreAuthorizationService Policies { get; }

        public CapturingLoggerProvider Logs { get; }

        /// <summary>How many times the default store's tenancy was enumerated.</summary>
        public int Builds => builds.Count;

        public IStoreInfoService Overview => scope.ServiceProvider.GetRequiredService<IStoreInfoService>();

        /// <summary>The default store's database identities, in the order Marten lists them.</summary>
        public async Task<IReadOnlyList<string>> IdentitiesAsync()
        {
            IReadOnlyList<IMartenDatabase> databases =
                await scope.ServiceProvider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return [.. databases.Select(static x => x.Id.Identity)];
        }

        public static Harness Create(Action<IStaticMultiTenancy> databases, Action<IServiceCollection>? extra = null)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();
            var logs = new CapturingLoggerProvider();
            var builds = new BuildCounter();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddMarten(options =>
            {
                options.MultiTenantedDatabases(databases);
                options.DatabaseSchemaName = "overview";

                ObservedTenancy observed = ObservedTenancy.Over(options.Tenancy, masterTableShape: false);
                observed.OnBuildDatabases = builds.Increment;
                options.Tenancy = (ITenancy) (object) observed;
            });
            services.AddMartenStudio(static options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.QueryTimeout = TimeSpan.FromSeconds(5);
            });

            extra?.Invoke(services);

            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            return new Harness(provider, provider.CreateScope(), builds, policies, logs);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }

    /// <summary>A thread-safe count, handed to the tenancy before the harness that reads it exists.</summary>
    private sealed class BuildCounter
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Increment() => Interlocked.Increment(ref count);
    }
}

/// <summary>
/// A store an application registered whose databases cannot be listed. At namespace level, as
/// <c>IElsewhereStore</c> is, because Marten generates the ancillary store's implementation from it.
/// </summary>
public interface IUnlistableDatabasesStore : IDocumentStore;
