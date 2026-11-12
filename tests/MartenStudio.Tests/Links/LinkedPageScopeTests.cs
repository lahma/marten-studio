using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Documents;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

using DocumentPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using StreamPage = MartenStudio.Components.Pages.Events.StreamDetail;
using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Links;

/// <summary>
/// D-CORR 7.2: what a document page and a stream page do with the address a module's link opens - <c>?store=&amp;tenant=</c>, no
/// <c>db</c> - which is the scope the link carries, because a module knows a store and a tenant and no database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the tenant lives in one database</b> (conjoined tables, the tenants the header lists for that database), the page keeps
/// the tenant and the database it was already on: the link works.
/// </para>
/// <para>
/// <b>Where each tenant has a database of its own, it does not.</b> <see cref="StudioState.SetScopeAsync" /> picks the database from
/// the address's <c>db</c>, else the visitor's current one, else the store's first, and then accepts the tenant only if <em>that</em>
/// database's listing carries it. Nothing looks the tenant up across the store's databases, so a link to a tenant whose database
/// is not the one on screen opens the wrong database with no tenant. The last test records that as it is; D-CORR 7.2 assumed the
/// page resolves the database from the tenant, and it does not. Changing it is a D9 change in the shared shell, which this packet
/// reports and does not make.
/// </para>
/// </remarks>
public class LinkedPageScopeTests
{
    private const string DocumentId = "8f1d5a6e-0000-0000-0000-000000000001";

    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task A_document_page_opened_with_store_and_tenant_and_no_db_keeps_the_tenant_of_a_conjoined_store()
    {
        using DocumentsComponentContext context = new();
        context.Catalog.WithTenants("default", new TenantList(["acme", "globex"], false, TenantListSource.Queried));
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        context.Navigate($"marten/documents/customer/doc?store=default&tenant=globex&id={DocumentId}");
        context.Render<DocumentPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        StudioScope? scope = context.State.ActiveScope;
        scope.Should().NotBeNull();
        scope!.StoreKey.Should().Be("default");
        scope.TenantId.Should().Be("globex");
        scope.DatabaseId.Should().Be("localhost.marten", "the database it was on; the link named none");
    }

    [Fact]
    public async Task A_stream_page_opened_with_store_and_tenant_and_no_db_keeps_the_tenant_of_a_conjoined_store()
    {
        using StudioComponentContext context = new();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");
        context.Catalog.WithTenants("default", new TenantList(["acme", "globex"], false, TenantListSource.Queried));
        context.EventData.StreamStates[StreamId] = Stream("globex");

        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        context.Navigate($"marten/events/streams/s?store=default&tenant=globex&id={StreamId}");
        context.Render<StreamPage>();

        StudioScope? scope = context.State.ActiveScope;
        scope.Should().NotBeNull();
        scope!.TenantId.Should().Be("globex");
        scope.DatabaseId.Should().Be("localhost.marten");
    }

    [Fact]
    public async Task A_tenant_whose_database_is_not_the_one_on_screen_is_dropped_when_the_link_names_no_db__a_gap_for_the_orchestrator()
    {
        using DocumentsComponentContext context = new();
        context.Services.AddSingleton<IStudioScopeCatalog>(new DatabasePerTenantCatalog());
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        context.Navigate($"marten/documents/customer/doc?store=default&tenant=globex&id={DocumentId}");
        context.Render<DocumentPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        StudioScope? scope = context.State.ActiveScope;
        scope.Should().NotBeNull();
        scope!.DatabaseId.Should().Be("db-a", "the page does not look the tenant up across the store's databases");
        scope.TenantId.Should().BeNull("db-a does not list globex, so the page falls back to all tenants and opens db-a: not what the link meant");
    }

    [Fact]
    public async Task The_same_gap_holds_for_a_stream_page()
    {
        using StudioComponentContext context = new();
        context.Services.AddSingleton<IStudioScopeCatalog>(new DatabasePerTenantCatalog());
        context.EventData.StreamStates[StreamId] = Stream("globex");

        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        context.Navigate($"marten/events/streams/s?store=default&tenant=globex&id={StreamId}");
        context.Render<StreamPage>();

        context.State.ActiveScope!.DatabaseId.Should().Be("db-a");
        context.State.ActiveScope.TenantId.Should().BeNull();
    }

    private static MartenStudio.Services.Documents.DocumentDetail Detail() => new()
    {
        Alias = "customer",
        Id = DocumentId,
        Json = """{"Name":"Customer 01"}""",
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        UpsertFunction = string.Empty,
        IdColumnType = DocumentIdColumnType.Uuid,
    };

    private static StreamState Stream(string? tenantId) =>
        new(
            StreamId,
            Exists: true,
            "Order",
            3,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            false,
            tenantId);

    /// <summary>A store with two databases, each listing only its own tenant: <c>db-a</c> has acme, <c>db-b</c> has globex.</summary>
    private sealed class DatabasePerTenantCatalog : IStudioScopeCatalog
    {
        private static readonly DatabaseListing[] Databases =
        [
            new("db-a", "db-a", "localhost"),
            new("db-b", "db-b", "localhost"),
        ];

        public Task<StoreListings> ListStoresAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoreListings([new StoreListing("default", "Default", StoreAvailability.Available)], []));

        public Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatabaseListing>>(Databases);

        public Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default)
        {
            string tenant = string.Equals(databaseId, "db-b", StringComparison.OrdinalIgnoreCase) ? "globex" : "acme";

            return Task.FromResult(new StoreScopeFacts(
                MartenStudioSource.Map(DatabaseCardinality.StaticMultiple),
                ShowTenantSelector: true,
                new TenantList([tenant], false, TenantListSource.Queried),
                null));
        }

        public void Invalidate()
        {
        }
    }
}
