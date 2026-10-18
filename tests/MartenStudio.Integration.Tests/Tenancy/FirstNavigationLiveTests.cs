using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// F8-D3: a first navigation of the studio changes nothing - for each tenancy shape whose enumeration is a walk
/// of what the store already holds, and for a master-table tenancy whose lookup table is already applied.
/// </summary>
/// <remarks>
/// <para>
/// Each test creates the databases its stores use, prepares them through a store instance of its own, reads a
/// <see cref="ServerSnapshot" />, opens the studio over a cold instance of the same configuration, navigates
/// (<see cref="TenancyStudio" />), and reads the snapshot again. The two must be equal: no database, no object
/// in any non-system schema of those databases - by name, oid, columns, constraints, triggers and comments - and
/// no row of a lookup table, by <c>xmin</c>. The databases are the test's own, which is what lets the snapshot
/// read every schema while other test classes run alongside; they are dropped afterwards.
/// </para>
/// <para>
/// Run at the floor and at the latest Marten (<c>-p:MartenStudioMartenVersion=9.50.2</c>, or
/// <c>dotnet fallout Test --marten-version latest</c>); the shapes and the answer are the same at both.
/// </para>
/// <para>
/// <b>Deliberately absent:</b> a master-table tenancy whose table is <em>not</em> there yet, and a single-server
/// tenancy whose databases are not. There the first enumeration per store instance applies the table, or
/// creates the databases, and the studio can be that first caller - AGENTS.md D27's known limit, which
/// <see cref="EnumerationAppliesLiveTests" /> pins rather than hides. A sharded tenancy is absent for the
/// same reason as the master table, and because its pool needs more than one database to mean anything.
/// </para>
/// </remarks>
public class FirstNavigationLiveTests(PostgresFixture postgres)
{
    private const string Acme = "acme";

    private const string Globex = "globex";

    private const string Initech = "initech";

    /// <summary>The document schema, inside each test's own database.</summary>
    private const string Schema = "app";

    /// <summary>The event schema, inside each test's own database.</summary>
    private const string Events = "app_events";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The strongest form for the shape every host starts with: nothing is there, and nothing is created.
    /// </summary>
    [PostgresFact]
    public async Task A_single_database_over_empty_schemas_is_left_empty()
    {
        await using OwnedDatabases owned = await OwnAsync("single_cold");
        string database = owned[0];

        await TenancyDatabases.ExecuteAsync(database, $"create schema {Schema}; create schema {Events};", Token);
        void Configure(StoreOptions options) => SingleDatabase(options, database);

        ServerSnapshot before = await SnapshotAsync(owned, lookupTable: null);
        before.Objects.Should().NotContain(
            static x => x.Contains(": relation ", StringComparison.Ordinal),
            "the premise: two empty schemas and nothing in them");

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned, lookupTable: null));

        navigation.Databases.Should().ContainSingle();
        navigation.Overview.Stores.Should().ContainSingle().Which.Databases.Should().ContainSingle();
    }

    [PostgresFact]
    public async Task A_single_database_with_data_is_left_as_it_was()
    {
        await using OwnedDatabases owned = await OwnAsync("single");
        string database = owned[0];
        void Configure(StoreOptions options) => SingleDatabase(options, database);

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await WriteAsync(seed, Acme);
            await WriteAsync(seed, Globex);
        }

        ServerSnapshot before = await SnapshotAsync(owned, lookupTable: null);

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned, lookupTable: null));

        navigation.TenantLists.Should().ContainSingle().Which.Source.Should().Be(
            TenantListSource.Queried, "a single database's tenants exist only as data");
        navigation.Tenants.Should().Equal(Acme, Globex);
    }

    [PostgresFact]
    public async Task A_single_database_with_tenant_partitioned_events_is_left_as_it_was()
    {
        await using OwnedDatabases owned = await OwnAsync("partitioned");
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            SingleDatabase(options, database);

            // What UseTenantPartitionedEvents requires: conjoined event tenancy and a quick append mode.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Policies.AllDocumentsAreMultiTenanted();
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            // An append for an unregistered tenant is refused: there is no partition and no sequence for it. Initech is
            // registered and has written nothing, so only the tenant-partition table knows it.
            await seed.Advanced.AddMartenManagedTenantsAsync(Token, Acme, Globex, Initech);

            await WriteAsync(seed, Acme);
            await WriteAsync(seed, Globex);
        }

        ServerSnapshot before = await SnapshotAsync(owned, lookupTable: null);

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned, lookupTable: null));

        navigation.Databases.Should().ContainSingle();

        // CL1 item 8: the describe already read mt_tenant_partitions onto the main descriptor, and that answer is the
        // listing now - it used to be thrown away for a scan of the tenanted tables, which never sees a tenant that has
        // written nothing.
        navigation.TenantLists.Should().ContainSingle().Which.Source.Should().Be(
            TenantListSource.Descriptor, "the tenancy's own registered list answers, as it does for every other shape that has one");
        navigation.Tenants.Should().Equal(Acme, Globex, Initech);
    }

    [PostgresFact]
    public async Task A_static_multi_tenancy_is_left_as_it_was()
    {
        await using OwnedDatabases owned = await OwnAsync("static_a", "static_b");
        string first = owned[0];
        string second = owned[1];

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(first, "f8d3-static-a").ForTenants(Acme, Globex);
                tenancy.AddMultipleTenantDatabase(second, "f8d3-static-b").ForTenants(Initech);
            });

            Documents(options);
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await WriteAsync(seed, Acme);
            await WriteAsync(seed, Globex);
            await WriteAsync(seed, Initech);
        }

        ServerSnapshot before = await SnapshotAsync(owned, lookupTable: null);

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned, lookupTable: null));

        navigation.Databases.Should().HaveCount(2);
        navigation.Overview.Stores.Should().ContainSingle().Which.Databases.Should().HaveCount(2);
        navigation.TenantLists.Should().OnlyContain(
            static x => x.Source == TenantListSource.Descriptor,
            "a static multi-tenancy describes its tenants from its configuration");
        navigation.Tenants.Should().BeEquivalentTo([Acme, Globex, Initech]);
    }

    /// <summary>
    /// The master-table case that is not the known limit: the lookup table is there, so the cold store's first
    /// enumeration applies nothing - Weasel finds no difference - and no tenant is seeded, so nothing is upserted.
    /// What remains is reads, and the tenants come from each database's own list (F8-D3 C4).
    /// </summary>
    [PostgresFact]
    public async Task A_master_table_tenancy_whose_table_is_applied_is_left_as_it_was()
    {
        await using OwnedDatabases owned = await OwnAsync("master", "master_globex");
        string master = owned[0];
        string globex = owned[1];

        const string tenants = "tenants";
        string lookupTable = SqlIdentifier.Qualify(tenants, "mt_tenant_databases");

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
            {
                tenancy.ConnectionString = master;
                tenancy.SchemaName = tenants;
            });

            Documents(options);
        }

        // Prepared through a store of its own. Its first enumeration applies the lookup table - the known limit,
        // and not what this test is about - then the tenants are registered and their databases built.
        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            _ = await seed.Storage.AllDatabases();

            var tenancy = (MasterTableTenancy) seed.Options.Tenancy;
            await tenancy.AddDatabaseRecordAsync(Acme, master);
            await tenancy.AddDatabaseRecordAsync(Globex, globex);

            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await WriteAsync(seed, Acme);
            await WriteAsync(seed, Globex);
        }

        ServerSnapshot before = await SnapshotAsync(owned, lookupTable);
        before.Rows.Should().HaveCount(2, "the premise: two registered tenants");
        before.Objects.Should().Contain(
            static x => x.Contains("relation tenants.mt_tenant_databases ", StringComparison.Ordinal),
            "the premise: the table is applied");

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned, lookupTable));

        navigation.Databases.Should().HaveCount(2);
        navigation.Overview.Stores.Should().ContainSingle()
            .Which.Databases.Should().HaveCount(2, "one card per tenant database, from one enumeration");
        navigation.TenantLists.Should().OnlyContain(
            static x => x.Source == TenantListSource.Descriptor,
            "each database's own tenant list, which the enumeration just reconciled (F8-D3 C4)");
        navigation.Tenants.Should().BeEquivalentTo([Acme, Globex]);
    }

    /// <summary>A single-database store: a conjoined document, and an event store that is not.</summary>
    private static void SingleDatabase(StoreOptions options, string connectionString)
    {
        options.Connection(connectionString);
        Documents(options);
    }

    private static void Documents(StoreOptions options)
    {
        options.DatabaseSchemaName = Schema;
        options.Events.DatabaseSchemaName = Events;
        options.Schema.For<TenancyNote>().MultiTenanted();
        options.Events.AddEventType<TenancyNoteWritten>();
    }

    /// <summary>One document and one stream for a tenant, through the seeding store - never the studio's.</summary>
    private static async Task WriteAsync(DocumentStore store, string tenantId)
    {
        await using IDocumentSession session = store.LightweightSession(tenantId);
        session.Store(new TenancyNote { Id = Guid.NewGuid(), Text = tenantId });
        session.Events.StartStream(Guid.NewGuid(), new TenancyNoteWritten(tenantId));
        await session.SaveChangesAsync(Token);
    }

    private static async Task<FirstNavigation> NavigateAsync(Action<StoreOptions> configure)
    {
        await using TenancyStudio studio = TenancyStudio.Start(configure);
        return await studio.FirstNavigationAsync(Token);
    }

    /// <summary>Databases of this test's own, named under <see cref="ServerSnapshot.FirstNavigationPrefix" />.</summary>
    private Task<OwnedDatabases> OwnAsync(params string[] names) =>
        OwnedDatabases.CreateAsync(postgres, [.. names.Select(static x => ServerSnapshot.FirstNavigationPrefix + x)], Token);

    private static Task<ServerSnapshot> SnapshotAsync(OwnedDatabases owned, string? lookupTable) =>
        ServerSnapshot.ReadAsync(owned.ConnectionStrings, ServerSnapshot.FirstNavigationPrefix, lookupTable, Token);
}
