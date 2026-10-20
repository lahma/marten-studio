using Marten;
using Marten.Storage;

using MartenStudio.Integration.Tests.Events;
using MartenStudio.Services;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// SEC1 L1, live: two master-table rows on one connection string are two Marten instances over one database, and a
/// scope naming a tenant resolves to the instance built from that tenant's row - the one Marten itself opens that
/// tenant's sessions on - rather than to the first instance with the identity.
/// </summary>
/// <remarks>
/// <para>
/// The twins are F8-D3-fix's (<see cref="TenantDiscoveryInstanceLiveTests" />): the studio sees one database and offers
/// both rows' tenants for it. Before the fix every tenant of it resolved to the first instance, so the second row's
/// tenant ran on the first row's data source - its role, its <c>Options=-c</c>, its pool - which a host with per-tenant
/// roles or row-level security keyed on one does not intend.
/// </para>
/// <para>
/// Each tenant's half is the other's control: with the instances in either order, one of the two tenants is the one the
/// first instance does not list, and that is the half the old resolution got wrong.
/// </para>
/// </remarks>
public class TwinDatabaseResolutionLiveTests(PostgresFixture postgres)
{
    private static readonly string[] Twins = ["t1", "t2"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Each_twin_rows_tenant_resolves_to_the_instance_built_from_its_row()
    {
        string schema = await postgres.CreateSchemaAsync("sec1_twin_resolution", Token);
        string master = await postgres.CreateSchemaAsync("sec1_twin_resolution_tenants", Token);

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
            foreach (string tenant in Twins)
            {
                await tenancy.AddDatabaseRecordAsync(tenant, postgres.ConnectionString);
            }
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddMarten(Configure);
        services.AddMartenStudio(static options => options.AuthorizationPolicy = null);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        IReadOnlyList<IMartenDatabase> instances = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();
        instances.Should().HaveCount(2, "the premise: one instance per row");
        string identity = instances.Select(static x => x.Id.Identity).Distinct(StringComparer.OrdinalIgnoreCase)
            .Should().ContainSingle("the premise: one database").Subject;

        StudioScopeResolver resolver = circuit.ServiceProvider.GetRequiredService<StudioScopeResolver>();

        foreach (string tenant in Twins)
        {
            IMartenDatabase own = instances.Single(x => x.TenantIds.Contains(tenant));

            ResolvedScope resolved = await resolver.ResolveAsync(new StudioScope(MartenStoreRegistry.DefaultStoreKey, identity, tenant), null, Token);

            resolved.Database.Should().BeSameAs(own, "{0}'s scope runs on the instance built from {0}'s row", tenant);
            resolved.Database.TenantIds.Should().Contain(tenant);
        }

        // With no tenant it is the first instance with the identity, the one the picker lists - which of the two that
        // is depends on the order the master table hands its rows back, so here it is only one of them, and
        // TenantDiscoveryInstanceTests pins which.
        ResolvedScope whole = await resolver.ResolveAsync(new StudioScope(MartenStoreRegistry.DefaultStoreKey, identity, null), null, Token);
        instances.Should().Contain(whole.Database);
    }
}
