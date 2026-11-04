using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-1 S3: <c>GetOverviewAsync(storeKey)</c> builds, asks the policy about and enumerates only that store, so the
/// Overview's one store card costs one enumeration whatever the host registered. The default store's factory counts each time it is
/// asked for the store - and throws, so a build of it could not be missed - and the other store's tenancy is wrapped in
/// <see cref="ObservedTenancy" />, which counts every <c>BuildDatabases()</c> - what <c>AllDatabases()</c> is.
/// </summary>
public class StoreInfoServiceFilterTests
{
    private const string FirstConnectionString = "Host=scope1-filter-a.invalid;Database=one;Username=u;Password=p";

    private const string SecondConnectionString = "Host=scope1-filter-b.invalid;Database=two;Username=u;Password=p";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Asking_for_one_store_resolves_and_enumerates_only_that_store()
    {
        await using Harness harness = Harness.Create();

        StudioOverview overview = await harness.Info.GetOverviewAsync(nameof(IScopeOneOtherStore), Token);

        overview.Stores.Select(static x => x.Key).Should().Equal(nameof(IScopeOneOtherStore));
        harness.DefaultBuilds.Should().Be(0, "the default store's factory is never asked for the store");
        harness.OtherEnumerations.Should().Be(1, "the wanted store is enumerated once");
    }

    [Fact]
    public async Task The_key_matches_in_any_case()
    {
        await using Harness harness = Harness.Create();

        StudioOverview overview = await harness.Info.GetOverviewAsync(nameof(IScopeOneOtherStore).ToUpperInvariant(), Token);

        overview.Stores.Should().ContainSingle().Which.Key.Should().Be(nameof(IScopeOneOtherStore));
    }

    [Fact]
    public async Task A_key_nothing_registered_under_answers_no_store_and_builds_none()
    {
        await using Harness harness = Harness.Create();

        StudioOverview overview = await harness.Info.GetOverviewAsync("ghost", Token);

        overview.Stores.Should().BeEmpty();
        harness.DefaultBuilds.Should().Be(0);
        harness.OtherEnumerations.Should().Be(0);
    }

    /// <summary>The premise: with no key every store is built, which is what the Stores page does.</summary>
    [Fact]
    public async Task With_no_key_every_store_the_visitor_may_see_is_read()
    {
        await using Harness harness = Harness.Create();

        StudioOverview overview = await harness.Info.GetOverviewAsync(cancellationToken: Token);

        overview.Stores.Select(static x => x.Key).Should().BeEquivalentTo([MartenStoreRegistry.DefaultStoreKey, nameof(IScopeOneOtherStore)]);
        harness.DefaultBuilds.Should().Be(1, "the premise: with no key the default store's factory is asked");
        harness.OtherEnumerations.Should().Be(1);
    }

    /// <summary>A real studio container over two stores that never connect; the default store's factory is counted.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;
        private readonly Counter defaultBuilds;
        private readonly Counter otherEnumerations;

        private Harness(ServiceProvider provider, Counter defaultBuilds, Counter otherEnumerations)
        {
            this.provider = provider;
            this.defaultBuilds = defaultBuilds;
            this.otherEnumerations = otherEnumerations;
            scope = provider.CreateScope();
        }

        public IStoreInfoService Info => scope.ServiceProvider.GetRequiredService<IStoreInfoService>();

        public int DefaultBuilds => defaultBuilds.Value;

        public int OtherEnumerations => otherEnumerations.Value;

        public static Harness Create()
        {
            Counter defaultBuilds = new();
            Counter otherEnumerations = new();

            TestAuthenticationStateProvider users = new();
            users.SignIn("tester");

            ServiceCollection services = new();
            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddMarten(options =>
            {
                options.MultiTenantedDatabases(static tenancy => tenancy.AddMultipleTenantDatabase(FirstConnectionString, "tenants-a").ForTenants("acme"));
                options.DatabaseSchemaName = "scope1_default";
            });
            services.AddMartenStore<IScopeOneOtherStore>(options =>
            {
                options.MultiTenantedDatabases(static tenancy => tenancy.AddMultipleTenantDatabase(SecondConnectionString, "tenants-b").ForTenants("globex"));
                options.DatabaseSchemaName = "scope1_other";
                options.Tenancy = (ITenancy) (object) CountedTenancy(options.Tenancy, otherEnumerations);
            });
            services.AddMartenStudio(static options => options.QueryTimeout = TimeSpan.FromSeconds(5));

            // The default store cannot be had: its factory counts the ask and throws, which the registry reports as a store that will not build.
            services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(_ =>
            {
                defaultBuilds.Increment();
                throw new InvalidOperationException("The default store must not be resolved for another store's card.");
            }));

            services.AddSingleton<IAuthorizationService>(new TestStoreAuthorizationService());
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            return new Harness(services.BuildServiceProvider(), defaultBuilds, otherEnumerations);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }

        private static ObservedTenancy CountedTenancy(ITenancy tenancy, Counter builds)
        {
            ObservedTenancy observed = ObservedTenancy.Over(tenancy, masterTableShape: false);
            observed.OnBuildDatabases = builds.Increment;
            return observed;
        }
    }

    private sealed class Counter
    {
        private int value;

        public int Value => Volatile.Read(ref value);

        public void Increment() => Interlocked.Increment(ref value);
    }
}

/// <summary>A second store an application registered. At namespace level, as Marten generates its implementation from it.</summary>
public interface IScopeOneOtherStore : IDocumentStore;
