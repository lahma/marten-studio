using Marten;
using Marten.Storage;

using MartenStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-2 T5: <c>DatabaseReach.IsExclusivelyTenants</c>, the synchronous form the Tenants overview places its rows with, answers
/// as <c>IsExclusivelyTenantsAsync</c> does on each of the four conditions - the async form makes its own enumeration and then asks it.
/// </summary>
public class DatabaseReachExclusiveTests
{
    private const string One = "Host=scope2-reach-1.invalid;Database=one;Username=u;Password=p";

    private const string Two = "Host=scope2-reach-2.invalid;Database=two;Username=u;Password=p";

    private const string Three = "Host=scope2-reach-3.invalid;Database=three;Username=u;Password=p";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_database_listing_exactly_the_tenant_is_exclusive_and_one_listing_others_is_not()
    {
        await using DocumentStore store = DocumentStore.For(static options => options.MultiTenantedDatabases(static tenancy =>
        {
            tenancy.AddMultipleTenantDatabase(One, "one").ForTenants("acme");
            tenancy.AddMultipleTenantDatabase(Two, "two").ForTenants("globex", "initech");
        }));

        IReadOnlyList<IMartenDatabase> listed = await store.Storage.AllDatabases();
        IMartenDatabase alone = Holding(listed, "acme");
        IMartenDatabase shared = Holding(listed, "globex");

        await AgreesAsync(store, alone, "acme", listed, expected: true);
        await AgreesAsync(store, alone, "globex", listed, expected: false, because: "the database does not list that tenant");
        await AgreesAsync(store, shared, "globex", listed, expected: false, because: "it lists several tenants");
    }

    [Fact]
    public async Task The_default_database_is_never_exclusive()
    {
        await using DocumentStore store = DocumentStore.For(static options => options.MultiTenantedDatabases(static tenancy =>
        {
            tenancy.AddMultipleTenantDatabase(One, "one").ForTenants("acme").AsDefault();
            tenancy.AddMultipleTenantDatabase(Two, "two").ForTenants("globex");
        }));

        IReadOnlyList<IMartenDatabase> listed = await store.Storage.AllDatabases();

        await AgreesAsync(store, Holding(listed, "acme"), "acme", listed, expected: false, because: "untenanted writes land in the default database");
        await AgreesAsync(store, Holding(listed, "globex"), "globex", listed, expected: true);
    }

    [Fact]
    public async Task A_sharded_tenancy_is_pooled_by_design_and_nothing_in_it_is_exclusive()
    {
        await using DocumentStore store = DocumentStore.For(static options => options.MultiTenantedWithShardedDatabases(static x => x.ConnectionString = Three));

        MartenDatabase database = new(
            store.Options,
            Npgsql.NpgsqlDataSource.Create(One),
            "shard-1");
        database.TenantIds.Add("acme");

        DatabaseReach.IsExclusivelyTenants(store, database, "acme", [database]).Should().BeFalse();
        (await DatabaseReach.IsExclusivelyTenantsAsync(store, database, "acme", Token)).Should().BeFalse();
    }

    [Fact]
    public async Task A_twin_instance_over_the_same_database_that_lists_another_tenant_makes_it_not_exclusive()
    {
        await using DocumentStore store = DocumentStore.For(static options => options.MultiTenantedDatabases(static tenancy =>
            tenancy.AddMultipleTenantDatabase(One, "one").ForTenants("acme")));

        IMartenDatabase first = Holding(await store.Storage.AllDatabases(), "acme");
        var twin = new MartenDatabase(store.Options, Npgsql.NpgsqlDataSource.Create(One), "twin");
        twin.TenantIds.Add("globex");

        DatabaseReach.IsExclusivelyTenants(store, first, "acme", [first]).Should().BeTrue("alone, it is exclusive");
        DatabaseReach.IsExclusivelyTenants(store, first, "acme", [first, twin]).Should().BeFalse("another instance of the same database lists globex");
    }

    /// <summary>The database that lists <paramref name="tenant" />: a static tenancy enumerates its databases in hash order.</summary>
    private static IMartenDatabase Holding(IReadOnlyList<IMartenDatabase> listed, string tenant) =>
        listed.First(x => x.TenantIds.Contains(tenant));

    private static async Task AgreesAsync(
        IDocumentStore store,
        IMartenDatabase database,
        string tenant,
        IReadOnlyList<IMartenDatabase> listed,
        bool expected,
        string? because = null)
    {
        DatabaseReach.IsExclusivelyTenants(store, database, tenant, listed).Should().Be(expected, because);
        (await DatabaseReach.IsExclusivelyTenantsAsync(store, database, tenant, Token)).Should().Be(expected, "the async form agrees with the synchronous one");
    }
}
