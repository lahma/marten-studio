using Marten;
using Marten.Storage;

using MartenStudio.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// Tier 3 of tenant discovery against a real store: the tenant ids that exist as data rather than as
/// configuration.
/// </summary>
/// <remarks>
/// The shape this is about is the common one and was the one that did not work: conjoined
/// <em>documents</em> and a single-tenant event store. Tenancy is a per-document-type setting in Marten,
/// so a store like the sample's — a multi-tenanted <c>Invoice</c> alongside events that are not tenanted
/// at all — keeps its tenant ids only in the document tables. Asking <c>mt_streams</c> and nothing else
/// found none, the selector offered none, and <c>StudioScopeResolver</c> then refused every tenant the
/// visitor could have typed; the sample only appeared to work because it sets <c>KnownTenantIds</c>.
/// </remarks>
public class TenantDiscoveryLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A conjoined document type. The event store around it stays single-tenant.</summary>
    private sealed class TenantedInvoice
    {
        public Guid Id { get; set; }

        public string Number { get; set; } = string.Empty;
    }

    /// <summary>A document type that is not tenanted, so its table has no tenant column to read.</summary>
    private sealed class GlobalNote
    {
        public Guid Id { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    private DocumentStore CreateStore()
    {
        return DocumentStore.For(options =>
        {
            options.Connection(Fixture.ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Schema;

            // Conjoined documents, single-tenant events: the combination that found nothing.
            options.Schema.For<TenantedInvoice>().MultiTenanted();
            options.Schema.For<GlobalNote>();
        });
    }

    private static TenantDiscovery CreateDiscovery(Action<MartenStudioOptions>? configure = null)
    {
        MartenStudioOptions options = new();
        configure?.Invoke(options);

        // No KnownTenantIds: the point is that nothing was configured.
        options.KnownTenantIds.Should().BeEmpty();

        return new TenantDiscovery(Options.Create(options), NullLogger<TenantDiscovery>.Instance);
    }

    private static async Task WriteAsync(DocumentStore store, string tenantId, string number)
    {
        await using IDocumentSession session = store.LightweightSession(tenantId);
        session.Store(new TenantedInvoice { Id = Guid.NewGuid(), Number = number });
        await session.SaveChangesAsync(Token);
    }

    [PostgresFact]
    public async Task Conjoined_documents_give_up_their_tenants_even_when_the_event_store_is_single_tenant()
    {
        await using DocumentStore store = CreateStore();

        await WriteAsync(store, "acme", "INV-1");
        await WriteAsync(store, "globex", "INV-2");

        IMartenDatabase database = (await store.Storage.AllDatabases()).Single();

        TenantList tenants = await CreateDiscovery().DiscoverAsync("default", store, database, Token);

        tenants.Source.Should().Be(TenantListSource.Queried);
        tenants.Ids.Should().Equal("acme", "globex");
        tenants.IsTruncated.Should().BeFalse();
    }

    /// <summary>
    /// The selector has to be relevant before it is filled, and that question is asked of the document
    /// types as well as of the event store.
    /// </summary>
    [PostgresFact]
    public async Task A_store_with_a_conjoined_document_type_has_a_tenant_scope()
    {
        await using DocumentStore store = CreateStore();

        // Touch the database so the store is built the way a page would have built it.
        await WriteAsync(store, "acme", "INV-1");

        CreateDiscovery().IsTenantScopeRelevant(store).Should().BeTrue();
    }

    /// <summary>
    /// A document type the host hid is hidden in the data layer too, not only in navigation — so its
    /// tenants are not discovered on its behalf either.
    /// </summary>
    [PostgresFact]
    public async Task A_hidden_document_types_tenants_are_not_discovered()
    {
        await using DocumentStore store = CreateStore();

        await WriteAsync(store, "acme", "INV-1");

        IMartenDatabase database = (await store.Storage.AllDatabases()).Single();

        TenantDiscovery discovery = CreateDiscovery(options =>
            options.IsDocumentTypeVisible = static type => type != typeof(TenantedInvoice));

        TenantList tenants = await discovery.DiscoverAsync("default", store, database, Token);

        tenants.Source.Should().Be(TenantListSource.Unavailable, "nothing tenanted is left to read");
    }

    /// <summary>
    /// A host that named its tenants has answered, and nothing is queried — which is also the only tier
    /// that can list a tenant that has no rows yet.
    /// </summary>
    [PostgresFact]
    public async Task Configured_tenants_still_win_over_anything_discovered()
    {
        await using DocumentStore store = CreateStore();

        await WriteAsync(store, "acme", "INV-1");

        IMartenDatabase database = (await store.Storage.AllDatabases()).Single();

        TenantDiscovery discovery = CreateDiscoveryWithKnownTenants("alpha", "beta");

        TenantList tenants = await discovery.DiscoverAsync("default", store, database, Token);

        tenants.Source.Should().Be(TenantListSource.Configured);
        tenants.Ids.Should().Equal("alpha", "beta");
    }

    private static TenantDiscovery CreateDiscoveryWithKnownTenants(params string[] tenantIds)
    {
        MartenStudioOptions options = new();
        foreach (string tenantId in tenantIds)
        {
            options.KnownTenantIds.Add(tenantId);
        }

        return new TenantDiscovery(Options.Create(options), NullLogger<TenantDiscovery>.Instance);
    }
}
