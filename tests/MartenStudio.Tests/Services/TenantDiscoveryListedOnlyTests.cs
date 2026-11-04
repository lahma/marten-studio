using Marten;
using Marten.Storage;

using MartenStudio.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-2 T3: <c>TenantDiscovery.ListedOnlyAsync</c> is tier 2 and never tier 3 - what Marten itself lists for a database, from the
/// enumeration already in hand, and no tenant-column scan in any database. Every database here is unreachable: a tier-3 query would
/// have thrown, been swallowed and answered nothing, so these tests state what each shape does answer.
/// </summary>
public class TenantDiscoveryListedOnlyTests
{
    private const string Master = "Host=scope2-master.invalid;Database=master;Username=u;Password=p";

    private const string Tenant = "Host=scope2-tenant.invalid;Database=tenant;Username=u;Password=p";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TenantDiscovery Create() =>
        new(Options.Create(new MartenStudioOptions()), NullLogger<TenantDiscovery>.Instance, TimeProvider.System);

    [Fact]
    public async Task A_master_table_database_lists_the_tenant_it_was_built_from_without_being_described()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(Master, "tenants");
        options.Schema.For<Note>().MultiTenanted();

        await using var store = new DocumentStore(options);
        await using MartenDatabase database = new(options, NpgsqlDataSource.Create(Tenant), "acme");
        database.TenantIds.Add("acme");

        TenantList? listed = await Create().ListedOnlyAsync(store, database, [database], Token);

        listed.Should().NotBeNull();
        listed!.Source.Should().Be(TenantListSource.Descriptor);
        listed.Ids.Should().Equal("acme");
    }

    [Fact]
    public async Task A_database_that_lists_nothing_is_null_and_no_tenant_table_is_queried()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(Master, "tenants");
        options.Schema.For<Note>().MultiTenanted();

        await using var store = new DocumentStore(options);
        await using MartenDatabase database = new(options, NpgsqlDataSource.Create(Tenant), "empty");

        TenantList? listed = await Create().ListedOnlyAsync(store, database, [database], Token);

        listed.Should().BeNull("tier 3 would have scanned the tenant columns; this tier answers 'none listed' and stops");
    }

    [Fact]
    public async Task A_static_tenancy_is_described_from_its_configuration()
    {
        await using DocumentStore store = DocumentStore.For(static options =>
        {
            options.MultiTenantedDatabases(static tenancy => tenancy.AddMultipleTenantDatabase(Tenant, "tenants-a").ForTenants("globex", "acme"));
            options.Schema.For<Note>().MultiTenanted();
        });

        IReadOnlyList<IMartenDatabase> all = await store.Storage.AllDatabases();

        TenantList? listed = await Create().ListedOnlyAsync(store, all[0], all, Token);

        listed.Should().NotBeNull();
        listed!.Ids.Should().Equal("acme", "globex");
    }

    [Fact]
    public async Task A_single_database_with_no_tenant_registrations_answers_null_rather_than_scanning()
    {
        await using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(Tenant);
            options.Schema.For<Note>().MultiTenanted();
        });

        IReadOnlyList<IMartenDatabase> all = await store.Storage.AllDatabases();

        (await Create().ListedOnlyAsync(store, all[0], all, Token)).Should().BeNull();
    }

    public sealed class Note
    {
        public Guid Id { get; set; }
    }
}
