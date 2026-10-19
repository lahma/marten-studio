using Marten;

using MartenStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// H8-fix F5: the one helper the health export, C02 and the daemon card list a store's databases with - one
/// answer per tenancy shape, and none of them a connection.
/// </summary>
/// <remarks>
/// Every store here is built over a refused address, so an answer that needed the database would fail rather
/// than pass. A master-table tenancy that holds a tenant needs a real master table to hold one, so that half is
/// <c>HealthExportLiveTests.A_master_table_tenancy_is_checked_from_what_it_holds_and_never_enumerated</c>.
/// </remarks>
public class HeldDatabasesTests
{
    private const string Refused = "Host=127.0.0.1;Port=1;Database=held_main;Username=none;Password=none;Timeout=2";

    private static string RefusedTo(string database) => Refused.Replace("held_main", database, StringComparison.Ordinal);

    [Fact]
    public async Task A_single_database_is_configuration()
    {
        await using DocumentStore store = DocumentStore.For(static options => options.Connection(Refused));

        HeldDatabaseList listed = await HeldDatabases.ListAsync(store);

        listed.Shape.Should().Be(TenancyShape.Configured);
        listed.Databases.Should().ContainSingle();
    }

    [Fact]
    public async Task A_static_tenancy_is_configuration_one_database_per_identity()
    {
        await using DocumentStore store = DocumentStore.For(static options => options.MultiTenantedDatabases(static tenancy =>
        {
            tenancy.AddSingleTenantDatabase(RefusedTo("held_a"), "a");
            tenancy.AddSingleTenantDatabase(RefusedTo("held_b"), "b");
            tenancy.AddSingleTenantDatabase(RefusedTo("held_b") + ";Application Name=again", "c");
        }));

        HeldDatabaseList listed = await HeldDatabases.ListAsync(store);

        listed.Shape.Should().Be(TenancyShape.Configured);
        listed.Databases.Select(static x => x.Id.Name).Should().BeEquivalentTo(["held_a", "held_b"], "two tenants over one database are one database");
    }

    [Fact]
    public async Task A_single_server_tenancy_that_has_built_nothing_holds_nothing()
    {
        await using DocumentStore store = DocumentStore.For(static options =>
            options.MultiTenantedWithSingleServer(Refused, static tenancy => tenancy.WithTenants("a", "b")));

        HeldDatabaseList listed = await HeldDatabases.ListAsync(store);

        listed.Shape.Should().Be(TenancyShape.SingleServer);
        listed.IsKnown.Should().BeTrue();
        listed.Databases.Should().BeEmpty("asking what it has built is not building it - CREATE DATABASE would have been refused");
    }

    [Fact]
    public async Task A_master_table_tenancy_that_has_resolved_nothing_holds_nothing()
    {
        await using DocumentStore store = DocumentStore.For(static options =>
            options.MultiTenantedDatabasesWithMasterDatabaseTable(Refused, "tenants"));

        HeldDatabaseList listed = await HeldDatabases.ListAsync(store);

        listed.Shape.Should().Be(TenancyShape.Held);
        listed.Databases.Should().BeEmpty("the master table was never read - a read would have been refused");
    }

    [Fact]
    public async Task A_sharded_tenancy_that_has_loaded_no_pool_holds_nothing()
    {
        await using DocumentStore store = DocumentStore.For(static options =>
            options.MultiTenantedWithShardedDatabases(static sharding => sharding.ConnectionString = Refused));

        HeldDatabaseList listed = await HeldDatabases.ListAsync(store);

        listed.Shape.Should().Be(TenancyShape.Held);
        listed.Databases.Should().BeEmpty();
    }
}
