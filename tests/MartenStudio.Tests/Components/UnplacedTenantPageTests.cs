using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Tests.Documents;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components;
using PostgresStudio.Services;

using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using StreamDetail = MartenStudio.Components.Pages.Events.StreamDetail;
using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Components;

/// <summary>
/// ZW38: a link that names a tenant the studio could not place (undeclared, declared twice, or declared in a database the visitor may not see)
/// leaves the scope on the usual database with no tenant, and the pages a link opens must then say what they say of an id that is not there -
/// without reading it, and without the shell rewriting the address to the scope on screen, which would drop the tenant and let the page read
/// the id in the database on screen.
/// </summary>
public class UnplacedTenantPageTests
{
    private const string Id = "8f1d5a6e-0000-0000-0000-000000000001";

    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static DetailModel Detail() => new()
    {
        Alias = "customer",
        Id = Id,
        Json = """{"Name":"Customer 01"}""",
        TextBytes = 42,
        StoredBytes = 30,
        Columns = [new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity)],
        Duplicated = [],
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        UpsertFunction = "\"studio_sample\".\"mt_upsert_customer\"",
        ExpectedDotNetType = "Sample.Customer",
        IsRegistered = true,
        IdColumnType = DocumentIdColumnType.Uuid,
    };

    private static DocumentsComponentContext TwoDatabases()
    {
        DocumentsComponentContext context = new();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a", "db-b");
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.EventData.StreamStates[StreamId] = new StreamState(StreamId, Exists: true, "order_summary", 3, new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero), false, null);
        return context;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("db-b")]
    public async Task A_document_link_for_an_unplaced_tenant_says_it_is_not_there_and_never_shows_the_document_on_screen(string? placedIn)
    {
        using DocumentsComponentContext context = TwoDatabases();
        context.Catalog.WithPlacement("default", "initech", placedIn);
        await context.State.EnsureInitializedAsync(Token);
        context.Navigate($"marten/documents/customer/doc?id={Id}&tenant=initech");

        IRenderedComponent<DetailPage> page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        if (placedIn is null)
        {
            page.WaitForAssertion(() => page.Find(".ms-empty-description").TextContent.Should().Be("No document with id '" + Id + "' is in 'customer' for tenant 'initech'."));
            page.Markup.Should().NotContain("Customer 01", "the id in the database on screen is not the tenant's");
            context.State.ActiveScope!.DatabaseId.Should().Be("db-a");
        }
        else
        {
            page.WaitForAssertion(() => page.Markup.Should().Contain("Customer 01"));
            context.State.ActiveScope.Should().Be(new StudioScope("default", "db-b", null), "placed: the tenant's own database");
        }
    }

    [Fact]
    public async Task A_stream_link_for_an_unplaced_tenant_says_it_is_not_there_and_never_reads_the_stream()
    {
        using DocumentsComponentContext context = TwoDatabases();
        context.Catalog.WithPlacement("default", "initech", null);
        await context.State.EnsureInitializedAsync(Token);
        context.Navigate($"marten/events/streams/s?id={StreamId}&tenant=initech");

        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();

        page.WaitForAssertion(() => page.Find(".ms-empty-title").TextContent.Should().Be("No such stream"));
        page.FindAll(".ms-stream-header").Should().BeEmpty("the stream in the database on screen is not the tenant's");
    }

    [Fact]
    public async Task A_stream_link_for_a_placed_tenant_reads_the_stream()
    {
        using DocumentsComponentContext context = TwoDatabases();
        context.Catalog.WithPlacement("default", "initech", "db-b");
        await context.State.EnsureInitializedAsync(Token);
        context.Navigate($"marten/events/streams/s?id={StreamId}&tenant=initech");

        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();

        page.WaitForAssertion(() => page.FindAll(".ms-stream-header").Should().ContainSingle());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task The_shell_keeps_an_address_whose_tenant_was_not_placed_and_rewrites_any_other(bool unplaced, bool keepsTenant)
    {
        ClickableNavigationManager navigation = new("http://localhost/marten/documents/customer/doc?id=" + Id + "&tenant=initech");

        await using StudioComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception, QuietNavigationInterception>();
            services.AddSingleton<IScrollToLocationHash, QuietScrollToLocationHash>();
            services.AddSingleton<IDocumentDataService>(new FakeDocumentDataService { Detail = DocumentDetailResult.Ok(Detail()) });
            services.AddSingleton<IDocumentWriteService>(new FakeDocumentWriteService());
            services.AddSingleton(new DocumentBrowserState());
        });

        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.StoreInfo.WithStore("default", "Default", databaseIdentity: "db-a");
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a", "db-b");

        if (unplaced)
        {
            context.Catalog.WithPlacement("default", "initech", null);
        }

        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => context.State.ActiveScope.Should().NotBeNull());
        await routes.InvokeAsync(() => { });

        routes.WaitForAssertion(() =>
        {
            if (keepsTenant)
            {
                navigation.Uri.Should().Contain("tenant=initech", "the address keeps naming the tenant the studio could not place");
                context.State.HasUnplacedTenant.Should().BeTrue();
            }
            else
            {
                navigation.Uri.Should().NotContain("tenant=initech", "any other address follows the scope on screen, as before");
            }
        });
    }
}
