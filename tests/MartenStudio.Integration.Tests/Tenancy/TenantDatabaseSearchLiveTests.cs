using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Documents;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// TENANT-DB: a link that names a tenant and no database opens the database that holds the tenant, in a store that keeps each tenant in
/// a database of its own, and looking for it changes nothing on the server.
/// </summary>
/// <remarks>
/// A static multi-database store of two databases (acme and globex in the first, initech in the second), over real Postgres. A circuit
/// opens on the first database, as a freshly opened studio does; <see cref="StudioState.SetScopeAsync" /> is called as the layout and the
/// document and stream pages call it for a link (<c>?store=&amp;tenant=</c>, no <c>db</c>). The search asks the scope picker's own
/// listings, so the proof that it created no database and applied no migration is the one <see cref="FirstNavigationLiveTests" /> uses:
/// a <see cref="ServerSnapshot" /> before and after.
/// </remarks>
public class TenantDatabaseSearchLiveTests(PostgresFixture postgres)
{
    private const string Prefix = "f8d3_tenantdb_";

    private const string Acme = "acme";

    private const string Globex = "globex";

    private const string Initech = "initech";

    private const string Schema = "app";

    private const string Events = "app_events";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_tenant_named_with_no_database_opens_the_database_that_holds_it_and_changes_nothing()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "a", Prefix + "b"], Token);
        string first = owned[0];
        string second = owned[1];

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(first, "f8d3-tenantdb-a").ForTenants(Acme, Globex);
                tenancy.AddMultipleTenantDatabase(second, "f8d3-tenantdb-b").ForTenants(Initech);
            });

            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Events;
            options.Schema.For<TenancyNote>().MultiTenanted();
            options.Events.AddEventType<TenancyNoteWritten>();
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            foreach (string tenant in (string[])[Acme, Globex, Initech])
            {
                await using IDocumentSession session = seed.LightweightSession(tenant);
                session.Store(new TenancyNote { Id = Guid.NewGuid(), Text = tenant });
                await session.SaveChangesAsync(Token);
            }
        }

        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, null, Token);

        await using TenancyStudio studio = TenancyStudio.Start(Configure);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        IServiceProvider services = circuit.ServiceProvider;

        var state = services.GetRequiredService<StudioState>();
        var catalog = services.GetRequiredService<IStudioScopeCatalog>();
        var resolver = services.GetRequiredService<StudioScopeResolver>();

        await state.EnsureInitializedAsync(Token);
        state.AvailableDatabases.Should().HaveCount(2, "the premise: a database per group of tenants");

        // Which of the two databases is listed first is the tenancy's order, not the configuration's: take the tenants as the picker lists them.
        string usual = state.ActiveScope!.DatabaseId;
        string[] local = [.. (await catalog.DescribeAsync(MartenStoreRegistry.DefaultStoreKey, usual, Token)).Tenants.Ids];
        string foreign = new[] { Acme, Globex, Initech }.First(x => !local.Contains(x));
        string foreignDatabase = await DatabaseListingAsync(state, catalog, foreign);
        foreignDatabase.Should().NotBe(usual, "the premise: the tenant is not in the database a freshly opened studio is on");

        // A tenant of the database the circuit is on: nothing to search for.
        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, null, local[0], Token);
        state.ActiveScope.Should().Be(new StudioScope(MartenStoreRegistry.DefaultStoreKey, usual, local[0]));

        // A tenant of the other one: the link names no database, and the studio finds it.
        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, null, foreign, Token);
        state.ActiveScope.Should().Be(new StudioScope(MartenStoreRegistry.DefaultStoreKey, foreignDatabase, foreign));
        state.AvailableTenants.Ids.Should().Contain(foreign, "the header describes the database the tenant was found in");

        // The page that opens it resolves the scope, as every data call does.
        _ = await resolver.ResolveAsync(state.ActiveScope, null, Token);

        // A tenant no database holds: the database stays where it was and the scope is all tenants, not the tenant the circuit was on.
        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, null, "umbrella", Token);
        state.ActiveScope.Should().Be(new StudioScope(MartenStoreRegistry.DefaultStoreKey, foreignDatabase, null));

        // An address that names both is reopened exactly: the named database lacks the tenant, so the tenant is dropped.
        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, usual, foreign, Token);
        state.ActiveScope.Should().Be(new StudioScope(MartenStoreRegistry.DefaultStoreKey, usual, null));

        // The link that leads here: this store keeps its rows per tenant, so it names the tenant (and no database).
        var links = studio.Services.GetRequiredService<IStudioRouteLinks>();
        string url = links.ToDocument(new StudioLinkScope(null, foreign), "tenancynote", Guid.NewGuid().ToString())!;
        url.Should().Contain("tenant=" + foreign).And.NotContain("db=");

        before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, null, Token));
    }

    /// <summary>
    /// ZW38. In Marten 9.57.0 a static multi-database store leaves single-tenanted documents single-tenanted: no database lists a tenant,
    /// so the scope search cannot find one from a listing. The configuration says which database serves each tenant, and a link for a tenant
    /// of the other database opens that tenant's document - not the id in the database on screen - with nothing created or migrated.
    /// </summary>
    [PostgresFact]
    public async Task A_static_store_with_single_tenanted_documents_links_to_the_document_in_the_tenants_own_database()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "single_a", Prefix + "single_b"], Token);
        string first = owned[0];
        string second = owned[1];

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(first, "f8d3-tenantdb-single-a").ForTenants(Acme);
                tenancy.AddMultipleTenantDatabase(second, "f8d3-tenantdb-single-b").ForTenants(Initech);
            });

            options.DatabaseSchemaName = Schema;
            options.Schema.For<TenancyNote>();
        }

        // The same id in both databases, as two tenants' records share a surrogate: the link must open the tenant's own.
        Guid shared = Guid.NewGuid();
        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            foreach (string tenant in (string[])[Acme, Initech])
            {
                await using IDocumentSession session = seed.LightweightSession(tenant);
                session.Store(new TenancyNote { Id = shared, Text = "of " + tenant });
                await session.SaveChangesAsync(Token);
            }
        }

        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, null, Token);

        await using TenancyStudio studio = TenancyStudio.Start(Configure);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        IServiceProvider services = circuit.ServiceProvider;

        var state = services.GetRequiredService<StudioState>();
        var documents = services.GetRequiredService<IDocumentDataService>();
        var links = services.GetRequiredService<IStudioRouteLinks>();

        await state.EnsureInitializedAsync(Token);
        state.AvailableDatabases.Should().HaveCount(2, "the premise: a database per tenant");
        state.ShowTenantSelector.Should().BeFalse("the premise: documents are single-tenanted, so no database lists a tenant");

        // A page whose current database is one tenant's, and a link for the other tenant's document.
        string firstName = new NpgsqlConnectionStringBuilder(first).Database!;
        string usualName = state.AvailableDatabases.Single(x => x.Identity == state.ActiveScope!.DatabaseId).Name;
        string foreign = usualName.Contains(firstName, StringComparison.OrdinalIgnoreCase) ? Initech : Acme;

        string url = links.ToDocument(new StudioLinkScope(null, foreign), "tenancy_note", shared.ToString())!;
        url.Should().Contain("tenant=" + foreign).And.NotContain("db=", "the link never names a database");

        // The page a link opens applies the scope from the address: a store, a tenant and no database.
        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, null, foreign, Token);

        state.ActiveScope!.DatabaseId.Should().NotBe(
            state.AvailableDatabases.Single(x => x.Name == usualName).Identity,
            "the tenant's database is not the one the circuit was on");
        state.ActiveScope.TenantId.Should().BeNull("rows are single-tenanted: the database is the scope");

        DocumentDetailResult opened = await documents.GetDocumentAsync(state.ActiveScope, "tenancy_note", shared.ToString(), Token);

        opened.Found.Should().BeTrue(opened.NotFound?.Message);
        opened.Detail!.Json.Should().Contain("of " + foreign, "the document of the tenant's own database, not the same id in the one on screen");

        // A tenant nobody declared leaves the circuit where it is, with no tenant.
        string landed = state.ActiveScope.DatabaseId;
        await state.SetScopeAsync(MartenStoreRegistry.DefaultStoreKey, null, "umbrella", Token);
        state.ActiveScope.Should().Be(new StudioScope(MartenStoreRegistry.DefaultStoreKey, landed, null));

        before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, null, Token));
    }

    /// <summary>The identity of the database whose tenant listing carries <paramref name="tenant" />, read as the scope picker reads it.</summary>
    private static async Task<string> DatabaseListingAsync(StudioState state, IStudioScopeCatalog catalog, string tenant)
    {
        foreach (DatabaseListing database in state.AvailableDatabases)
        {
            StoreScopeFacts facts = await catalog.DescribeAsync(MartenStoreRegistry.DefaultStoreKey, database.Identity, Token);
            if (facts.Tenants.Ids.Contains(tenant))
            {
                return database.Identity;
            }
        }

        throw new InvalidOperationException($"No database lists {tenant}.");
    }
}
