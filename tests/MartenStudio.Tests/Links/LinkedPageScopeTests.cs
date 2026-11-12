using Bunit;

using MartenStudio.Internal.Sql;
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
/// In a single-database store (conjoined tables, the tenants the header lists for that database) the page keeps the tenant and the
/// database it was already on, so the link works. A store that keeps each tenant in a database of its own gets no link at all
/// (<see cref="MartenStudioRouteLinksTests" />): <see cref="StudioState.SetScopeAsync" /> takes the database from the address or the
/// visitor's current one and accepts the tenant only if that database's listing carries it, and nothing looks a tenant up across the
/// store's databases.
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
}
