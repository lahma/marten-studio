using JasperFx.Descriptors;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// 1.2-SCOPE-1: the Stores page's reads against a live server - three stores (a static two-database one, a one-database one and one
/// that will not build) - and the guarantee behind the page: opening it creates nothing and starts no sweep, and "Check" runs
/// the one regular sweep a database's Overview would, which also creates nothing.
/// </summary>
public class StoresLiveTests(PostgresFixture postgres)
{
    private const string Prefix = "scope1_stores_";

    private const string Schema = "app";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_stores_page_reads_every_store_its_reason_and_its_databases_without_changing_the_server()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "a", Prefix + "b", Prefix + "c"], Token);

        await using (DocumentStore seed = DocumentStore.For(options => Configure(options, owned)))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        ServerSnapshot before = await SnapshotAsync(owned);

        await using TenancyStudio studio = TenancyStudio.Start(options => Configure(options, owned), services => AddOtherStores(services, owned));
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();

        // What the Stores page does when the circuit renders it: one enumeration with no key, and one cache-only glance per database.
        StudioOverview overview = await circuit.ServiceProvider.GetRequiredService<IStoreInfoService>().GetOverviewAsync(cancellationToken: Token);

        overview.Stores.Select(static x => x.Key).Should().BeEquivalentTo(
            [MartenStoreRegistry.DefaultStoreKey, nameof(IStoresLiveSecondStore), nameof(IStoresLiveBrokenStore)]);

        StoreOverview first = overview.Stores.Single(static x => x.Key == MartenStoreRegistry.DefaultStoreKey);
        first.IsAvailable.Should().BeTrue();
        first.Cardinality.Should().Be(DatabaseCardinality.StaticMultiple);
        first.Tenancy.Kind.Should().Be(StoreTenancyKind.Static);
        first.Tenancy.Words.Should().Be("A database per tenant, configured");
        first.Databases.Should().HaveCount(2).And.OnlyContain(static x => x.Error == null);
        first.Databases.Select(static x => x.Name).Should().BeEquivalentTo([Prefix + "a", Prefix + "b"]);
        first.ConjoinedDocumentTypes.Should().Be(1);
        first.TenantPartitionedEvents.Should().BeFalse();
        first.Daemon.Kind.Should().Be(StoreDaemonHostingKind.NoAsyncWork);

        StoreOverview second = overview.Stores.Single(static x => x.Key == nameof(IStoresLiveSecondStore));
        second.IsAvailable.Should().BeTrue();
        second.Tenancy.Kind.Should().Be(StoreTenancyKind.Single);
        second.Databases.Should().ContainSingle().Which.Name.Should().Be(Prefix + "c");

        StoreOverview broken = overview.Stores.Single(static x => x.Key == nameof(IStoresLiveBrokenStore));
        broken.IsAvailable.Should().BeFalse("the store was registered and will not build");
        broken.UnavailableMessage.Should().NotBeNullOrWhiteSpace("the page prints the reason");
        broken.Databases.Should().BeEmpty();

        var health = circuit.ServiceProvider.GetRequiredService<IHealthService>();
        foreach (StoreOverview store in overview.Stores)
        {
            foreach (DatabaseOverview database in store.Databases)
            {
                (await health.GlanceAsync(new StudioScope(store.Key, database.Identity, null), Token)).Should().BeNull("no sweep has run");
            }
        }

        studio.Services.GetRequiredService<HealthSweepCache>().SweepCount.Should().Be(0, "looking at the page starts no sweep");

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned));
    }

    [PostgresFact]
    public async Task Check_runs_one_regular_sweep_for_one_database_and_creates_nothing()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "a", Prefix + "b", Prefix + "c"], Token);

        await using (DocumentStore seed = DocumentStore.For(options => Configure(options, owned)))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        ServerSnapshot before = await SnapshotAsync(owned);

        await using TenancyStudio studio = TenancyStudio.Start(options => Configure(options, owned), services => AddOtherStores(services, owned));
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();

        StudioOverview overview = await circuit.ServiceProvider.GetRequiredService<IStoreInfoService>().GetOverviewAsync(cancellationToken: Token);
        DatabaseOverview target = overview.Stores.Single(static x => x.Key == MartenStoreRegistry.DefaultStoreKey).Databases[0];
        var scope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, target.Identity, null);

        var health = circuit.ServiceProvider.GetRequiredService<IHealthService>();

        HealthGlance? glance = await health.CheckAsync(scope, Token);

        glance.Should().NotBeNull("a regular sweep ran and the visitor may see its result");
        studio.Services.GetRequiredService<HealthSweepCache>().SweepCount.Should().Be(1, "one database, one sweep");

        (await health.GlanceAsync(scope, Token)).Should().NotBeNull("the cache-only read now finds the sweep Check started");

        DatabaseOverview other = overview.Stores.Single(static x => x.Key == MartenStoreRegistry.DefaultStoreKey).Databases[1];
        (await health.GlanceAsync(scope with { DatabaseId = other.Identity }, Token)).Should().BeNull("the other database was not swept");

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned));
    }

    private static void Configure(StoreOptions options, OwnedDatabases owned)
    {
        options.DatabaseSchemaName = Schema;
        options.MultiTenantedDatabases(tenancy =>
        {
            tenancy.AddMultipleTenantDatabase(owned[0], "tenants-a").ForTenants("acme");
            tenancy.AddMultipleTenantDatabase(owned[1], "tenants-b").ForTenants("globex");
        });

        options.Schema.For<TenancyNote>().MultiTenanted();
        options.Events.AddEventType<TenancyNoteWritten>();
    }

    private static void AddOtherStores(IServiceCollection services, OwnedDatabases owned)
    {
        services.AddMartenStore<IStoresLiveSecondStore>(options =>
        {
            options.Connection(owned[2]);
            options.DatabaseSchemaName = "second";
            options.Schema.For<TenancyNote>();
        });

        // Registered with no connection: building it fails, and the page says why.
        services.AddMartenStore<IStoresLiveBrokenStore>(static options => options.DatabaseSchemaName = "broken");
    }

    private static Task<ServerSnapshot> SnapshotAsync(OwnedDatabases owned) =>
        ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, lookupTable: null, Token);
}

/// <summary>A second store an application registered. At namespace level, as Marten generates its implementation from it.</summary>
public interface IStoresLiveSecondStore : IDocumentStore;

/// <summary>A store an application registered that cannot build.</summary>
public interface IStoresLiveBrokenStore : IDocumentStore;
