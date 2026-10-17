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
/// Each test prepares its database through a store instance of its own, reads a <see cref="ServerSnapshot" />,
/// opens the studio over a cold instance of the same configuration, navigates (<see cref="TenancyStudio" />),
/// and reads the snapshot again. The two must be equal: no database, no schema object - by name, oid and
/// columns - and no row of a lookup table, by <c>xmin</c>.
/// </para>
/// <para>
/// Run at the floor and at the latest Marten (<c>-p:MartenStudioMartenVersion=9.50.2</c>, or
/// <c>dotnet fallout Test --marten-version latest</c>); the shapes and the answer are the same at both.
/// </para>
/// <para>
/// <b>Deliberately absent:</b> a master-table tenancy whose table is <em>not</em> there yet, and a single-server
/// tenancy whose databases are not. There the first enumeration in the process applies the table, or creates
/// the databases, and the studio can be that first caller - AGENTS.md D27's known limit, which
/// <see cref="EnumerationAppliesLiveTests" /> pins rather than hides. A sharded tenancy is absent for the
/// same reason as the master table, and because its pool needs more than one database to mean anything.
/// </para>
/// </remarks>
public class FirstNavigationLiveTests(PostgresFixture postgres)
{
    private const string Acme = "acme";

    private const string Globex = "globex";

    private const string Initech = "initech";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The strongest form for the shape every host starts with: nothing is there, and nothing is created.
    /// </summary>
    [PostgresFact]
    public async Task A_single_database_over_empty_schemas_is_left_empty()
    {
        (string schema, string events) = await SchemasAsync("f8d3_nav_single_cold");
        void Configure(StoreOptions options) => SingleDatabase(options, schema, events);

        IReadOnlyList<(string, string[])> databases = [(postgres.ConnectionString, new[] { schema, events })];
        ServerSnapshot before = await SnapshotAsync(databases, lookupTable: null);
        before.Objects.Should().HaveCount(2, "the premise: two empty schemas and nothing in them");

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(databases, lookupTable: null));

        navigation.Databases.Should().ContainSingle();
        navigation.Overview.Stores.Should().ContainSingle().Which.Databases.Should().ContainSingle();
    }

    [PostgresFact]
    public async Task A_single_database_with_data_is_left_as_it_was()
    {
        (string schema, string events) = await SchemasAsync("f8d3_nav_single");
        void Configure(StoreOptions options) => SingleDatabase(options, schema, events);

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await WriteAsync(seed, Acme);
            await WriteAsync(seed, Globex);
        }

        IReadOnlyList<(string, string[])> databases = [(postgres.ConnectionString, new[] { schema, events })];
        ServerSnapshot before = await SnapshotAsync(databases, lookupTable: null);

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(databases, lookupTable: null));

        navigation.TenantLists.Should().ContainSingle().Which.Source.Should().Be(
            TenantListSource.Queried, "a single database's tenants exist only as data");
        navigation.Tenants.Should().Equal(Acme, Globex);
    }

    [PostgresFact]
    public async Task A_single_database_with_tenant_partitioned_events_is_left_as_it_was()
    {
        (string schema, string events) = await SchemasAsync("f8d3_nav_partitioned");
        void Configure(StoreOptions options)
        {
            SingleDatabase(options, schema, events);

            // What UseTenantPartitionedEvents requires: conjoined event tenancy and a quick append mode.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Policies.AllDocumentsAreMultiTenanted();
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            // An append for an unregistered tenant is refused: there is no partition and no sequence for it.
            await seed.Advanced.AddMartenManagedTenantsAsync(Token, Acme, Globex);

            await WriteAsync(seed, Acme);
            await WriteAsync(seed, Globex);
        }

        IReadOnlyList<(string, string[])> databases = [(postgres.ConnectionString, new[] { schema, events })];
        ServerSnapshot before = await SnapshotAsync(databases, lookupTable: null);

        FirstNavigation navigation = await NavigateAsync(Configure);

        before.ShouldBeUnchangedIn(await SnapshotAsync(databases, lookupTable: null));

        navigation.Databases.Should().ContainSingle();
        navigation.Tenants.Should().Equal(Acme, Globex);
    }

    [PostgresFact]
    public async Task A_static_multi_tenancy_is_left_as_it_was()
    {
        (string schema, string events) = await SchemasAsync("f8d3_nav_static");
        string secondName = ServerSnapshot.FirstNavigationPrefix + "static_b";
        string second = await TenancyDatabases.CreateAsync(postgres, secondName, Token);

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(postgres.ConnectionString, "f8d3-static-a").ForTenants(Acme, Globex);
                tenancy.AddMultipleTenantDatabase(second, "f8d3-static-b").ForTenants(Initech);
            });

            Documents(options, schema, events);
        }

        try
        {
            await using (DocumentStore seed = DocumentStore.For(Configure))
            {
                await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
                await WriteAsync(seed, Acme);
                await WriteAsync(seed, Globex);
                await WriteAsync(seed, Initech);
            }

            IReadOnlyList<(string, string[])> databases =
            [
                (postgres.ConnectionString, new[] { schema, events }),
                (second, new[] { schema, events }),
            ];

            ServerSnapshot before = await SnapshotAsync(databases, lookupTable: null);

            FirstNavigation navigation = await NavigateAsync(Configure);

            before.ShouldBeUnchangedIn(await SnapshotAsync(databases, lookupTable: null));

            navigation.Databases.Should().HaveCount(2);
            navigation.Overview.Stores.Should().ContainSingle().Which.Databases.Should().HaveCount(2);
            navigation.TenantLists.Should().OnlyContain(
                static x => x.Source == TenantListSource.Descriptor,
                "a static multi-tenancy describes its tenants from its configuration");
            navigation.Tenants.Should().BeEquivalentTo([Acme, Globex, Initech]);
        }
        finally
        {
            await TenancyDatabases.DropAsync(postgres, secondName, CancellationToken.None);
        }
    }

    /// <summary>
    /// The master-table case that is not the known limit: the lookup table is there, so the cold store's first
    /// enumeration applies nothing - Weasel finds no difference - and no tenant is seeded, so nothing is upserted.
    /// What remains is reads, and the tenants come from each database's own list (F8-D3 C4).
    /// </summary>
    [PostgresFact]
    public async Task A_master_table_tenancy_whose_table_is_applied_is_left_as_it_was()
    {
        (string schema, string events) = await SchemasAsync("f8d3_nav_master");
        string master = await postgres.CreateSchemaAsync("f8d3_nav_master_tenants", Token);
        string lookupTable = SqlIdentifier.Qualify(master, "mt_tenant_databases");

        string globexName = ServerSnapshot.FirstNavigationPrefix + "master_globex";
        string globex = await TenancyDatabases.CreateAsync(postgres, globexName, Token);

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
            {
                tenancy.ConnectionString = postgres.ConnectionString;
                tenancy.SchemaName = master;
            });

            Documents(options, schema, events);
        }

        try
        {
            // Prepared through a store of its own. Its first enumeration applies the lookup table - the known
            // limit, and not what this test is about - then the tenants are registered and their databases built.
            await using (DocumentStore seed = DocumentStore.For(Configure))
            {
                _ = await seed.Storage.AllDatabases();

                var tenancy = (MasterTableTenancy) seed.Options.Tenancy;
                await tenancy.AddDatabaseRecordAsync(Acme, postgres.ConnectionString);
                await tenancy.AddDatabaseRecordAsync(Globex, globex);

                await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
                await WriteAsync(seed, Acme);
                await WriteAsync(seed, Globex);
            }

            IReadOnlyList<(string, string[])> databases =
            [
                (postgres.ConnectionString, new[] { master, schema, events }),
                (globex, new[] { schema, events }),
            ];

            ServerSnapshot before = await SnapshotAsync(databases, lookupTable);
            before.Rows.Should().HaveCount(2, "the premise: two registered tenants");
            (await TenancyDatabases.RelationExistsAsync(postgres, lookupTable, Token)).Should().BeTrue("the premise: the table is applied");

            FirstNavigation navigation = await NavigateAsync(Configure);

            before.ShouldBeUnchangedIn(await SnapshotAsync(databases, lookupTable));

            navigation.Databases.Should().HaveCount(2);
            navigation.Overview.Stores.Should().ContainSingle()
                .Which.Databases.Should().HaveCount(2, "one card per tenant database, from one enumeration");
            navigation.TenantLists.Should().OnlyContain(
                static x => x.Source == TenantListSource.Descriptor,
                "each database's own tenant list, which the enumeration just reconciled (F8-D3 C4)");
            navigation.Tenants.Should().BeEquivalentTo([Acme, Globex]);
        }
        finally
        {
            await TenancyDatabases.DropAsync(postgres, globexName, CancellationToken.None);
        }
    }

    /// <summary>A single-database store: a conjoined document, and an event store that is not.</summary>
    private void SingleDatabase(StoreOptions options, string schema, string events)
    {
        options.Connection(postgres.ConnectionString);
        Documents(options, schema, events);
    }

    private static void Documents(StoreOptions options, string schema, string events)
    {
        options.DatabaseSchemaName = schema;
        options.Events.DatabaseSchemaName = events;
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

    private async Task<(string Schema, string Events)> SchemasAsync(string name) =>
        (await postgres.CreateSchemaAsync(name, Token), await postgres.CreateSchemaAsync(name + "_events", Token));

    private static Task<ServerSnapshot> SnapshotAsync(IReadOnlyList<(string, string[])> databases, string? lookupTable) =>
        ServerSnapshot.ReadAsync(databases, ServerSnapshot.FirstNavigationPrefix, lookupTable, Token);
}
