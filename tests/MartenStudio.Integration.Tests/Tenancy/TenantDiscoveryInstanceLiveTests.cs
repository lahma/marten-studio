using Marten;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// F8-D3 C4, live: tenant discovery reads a master-table database's tenant from the instance the caller's
/// enumeration handed it, and not from the master table.
/// </summary>
/// <remarks>
/// The master table is taken away after that enumeration, so any later read of it fails with <c>42P01</c>. The
/// anti-vacuity half is asserted first: describing the tenancy now throws. Discovery then still answers the
/// tenant from Marten's own list - which a describe call could not have done - rather than falling through to
/// the query tier, which here would find nothing.
/// </remarks>
public class TenantDiscoveryInstanceLiveTests(PostgresFixture postgres)
{
    private const string Acme = "acme";

    /// <summary>The two master-table rows that share one connection string.</summary>
    private static readonly string[] Twins = ["t1", "t2"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_master_table_tenant_is_discovered_without_reading_the_master_table()
    {
        string schema = await postgres.CreateSchemaAsync("f8d3_discovery_master", Token);
        string master = await postgres.CreateSchemaAsync("f8d3_discovery_master_tenants", Token);

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
            {
                tenancy.ConnectionString = postgres.ConnectionString;
                tenancy.SchemaName = master;
            });

            options.DatabaseSchemaName = schema;
            options.Schema.For<TenancyNote>().MultiTenanted();
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            _ = await seed.Storage.AllDatabases();
            await ((MasterTableTenancy) seed.Options.Tenancy).AddDatabaseRecordAsync(Acme, postgres.ConnectionString);
        }

        // The studio's own store, cold, and the enumeration every caller of discovery has just made.
        await using DocumentStore store = DocumentStore.For(Configure);
        IMartenDatabase database = (await store.Storage.AllDatabases()).Should().ContainSingle().Subject;

        await TenancyDatabases.ExecuteAsync(
            postgres,
            $"alter table {SqlIdentifier.Qualify(master, "mt_tenant_databases")} rename to mt_tenant_databases_away",
            Token);

        await FluentActions.Awaiting(async () => await store.Options.Tenancy.DescribeDatabasesAsync(Token))
            .Should().ThrowAsync<PostgresException>()
            .Where(static x => x.SqlState == PostgresErrorCodes.UndefinedTable, "the premise: describing reads the master table");

        var discovery = new TenantDiscovery(Options.Create(new MartenStudioOptions()), NullLogger<TenantDiscovery>.Instance);
        TenantList tenants = await discovery.DiscoverAsync(MartenStoreRegistry.DefaultStoreKey, store, database, [database], Token);

        tenants.Source.Should().Be(TenantListSource.Descriptor, "the instance's own tenant list answered");
        tenants.Ids.Should().Equal(Acme);
    }

    /// <summary>
    /// F8-D3-fix, live: two master-table rows with one connection string. Marten builds two instances over one
    /// database; the studio sees one database, offers both rows' tenants for it, resolves a scope naming the
    /// second row's tenant, and draws one Overview card.
    /// </summary>
    [PostgresFact]
    public async Task Two_master_table_rows_on_one_connection_string_are_one_database_with_both_tenants()
    {
        string schema = await postgres.CreateSchemaAsync("f8d3_discovery_twins", Token);
        string master = await postgres.CreateSchemaAsync("f8d3_discovery_twins_tenants", Token);

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
            {
                tenancy.ConnectionString = postgres.ConnectionString;
                tenancy.SchemaName = master;
            });

            options.DatabaseSchemaName = schema;
            options.Schema.For<TenancyNote>().MultiTenanted();
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            _ = await seed.Storage.AllDatabases();

            var tenancy = (MasterTableTenancy) seed.Options.Tenancy;
            await tenancy.AddDatabaseRecordAsync("t1", postgres.ConnectionString);
            await tenancy.AddDatabaseRecordAsync("t2", postgres.ConnectionString);
        }

        await using TenancyStudio studio = TenancyStudio.Start(Configure);

        IReadOnlyList<IMartenDatabase> instances = await studio.Store.Storage.AllDatabases();
        instances.Should().HaveCount(2, "the premise: one instance per row");
        instances.Select(static x => x.Id.Identity).Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().ContainSingle("the premise: one database");

        // Resolves the scope of every tenant the header offers, which is the deep link a visitor would follow.
        FirstNavigation navigation = await studio.FirstNavigationAsync(Token);

        navigation.TenantLists.Should().OnlyContain(static x => x.Ids.SequenceEqual(Twins),
            "the header offers both rows' tenants for the one database");
        navigation.Tenants.Distinct().Should().BeEquivalentTo(Twins);
        navigation.Overview.Stores.Should().ContainSingle()
            .Which.Databases.Should().ContainSingle("one database, one card - two under one @key end the circuit");
    }
}
