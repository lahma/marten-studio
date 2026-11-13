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

    private sealed record Shell(StudioComponentContext Context, ClickableNavigationManager Navigation, IRenderedComponent<StudioRoutes> Routes) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    /// <summary>
    /// The shell over the document page, opened on an address that names tenant initech and no database - whose tenant the catalog cannot
    /// place (<paramref name="unplaced" />) or has no declaration for - and settled: the page's own words are on screen, which is drawn only
    /// after the shell's first rewrite of the address has run.
    /// </summary>
    private static Shell StartShell(bool unplaced)
    {
        ClickableNavigationManager navigation = new("http://localhost/marten/documents/customer/doc?id=" + Id + "&tenant=initech");

        StudioComponentContext context = new(services =>
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
        routes.WaitForAssertion(() => routes.Markup.Should().Contain(unplaced ? "No document with id" : "Customer 01"));

        return new Shell(context, navigation, routes);
    }

    [Fact]
    public async Task The_shell_keeps_the_address_whose_tenant_was_not_placed()
    {
        await using Shell shell = StartShell(unplaced: true);
        await shell.Routes.InvokeAsync(() => { });

        shell.Context.State.IsTenantUnplaced(null, null, "initech").Should().BeTrue("the premise");
        shell.Navigation.Uri.Should().Contain("tenant=initech", "the address keeps naming the tenant the studio could not place");
        shell.Navigation.Uri.Should().NotContain("db=");
    }

    [Fact]
    public async Task Any_other_address_is_rewritten_to_the_scope_on_screen_as_before()
    {
        await using Shell shell = StartShell(unplaced: false);
        await shell.Routes.InvokeAsync(() => { });

        shell.Navigation.Uri.Should().NotContain("tenant=initech");
    }

    /// <summary>
    /// 1.2.0's behaviours stay while an unplaced address exists elsewhere in the circuit's history: the placement belongs to its address, and
    /// Back to an address that names no database is rewritten to the scope on screen exactly as it was.
    /// </summary>
    [Fact]
    public async Task Back_to_an_address_naming_no_database_is_rewritten_after_an_unplaced_address_was_left()
    {
        await using Shell shell = StartShell(unplaced: true);

        // A rail link: the scope on screen, which the circuit is already on. Nothing applies it, so the placement stays in the state.
        shell.Navigation.Click("?store=default&db=db-a");
        await shell.Routes.InvokeAsync(() => { });
        shell.Context.State.IsTenantUnplaced(null, null, "initech").Should().BeTrue("the premise: the unplaced address is still remembered");

        shell.Navigation.HistoryMove("?store=default");

        shell.Routes.WaitForAssertion(() => shell.Navigation.Uri.Should().Contain("db=db-a", "an address that names no database is rewritten to the whole scope"));
    }

    /// <summary>The same for a link to a store the listings do not carry, which applies nothing and is rewritten to the scope on screen.</summary>
    [Fact]
    public async Task A_link_to_a_refused_store_is_rewritten_after_an_unplaced_address_was_left()
    {
        await using Shell shell = StartShell(unplaced: true);
        shell.Context.State.IsTenantUnplaced(null, null, "initech").Should().BeTrue("the premise");

        shell.Navigation.Click("?store=nowhere&db=db-q");

        shell.Routes.WaitForAssertion(() => shell.Navigation.Uri.Should().Contain("store=default").And.NotContain("nowhere"));
    }

    /// <summary>
    /// A page that asks while the placement is still being worked out is told the address is unplaced, so it reads nothing and never shows the
    /// other database's record; it is told again, even when the scope did not move, when the answer is in.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("db-a")]
    [InlineData("db-b")]
    public async Task A_page_that_asks_during_the_race_never_shows_the_other_databases_record(string? placedIn)
    {
        using DocumentsComponentContext context = TwoDatabases();
        context.Catalog.WithPlacement("default", "initech", placedIn);
        await context.State.EnsureInitializedAsync(Token);
        context.Catalog.PlaceGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Navigate($"marten/documents/customer/doc?id={Id}&tenant=initech");

        IRenderedComponent<DetailPage> page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        context.State.IsTenantUnplaced(null, null, "initech").Should().BeTrue("a placement still being worked out is unplaced");
        page.Markup.Should().NotContain("Customer 01", "the id in the database on screen is not the tenant's, and nothing has been placed yet");

        // The answer arrives; the scope moves for db-b only, and the page is told either way.
        context.Catalog.PlaceGate.SetResult();

        if (placedIn is null)
        {
            page.WaitForAssertion(() => page.Find(".ms-empty-description").TextContent.Should().Contain("No document with id"));
            page.Markup.Should().NotContain("Customer 01");
            context.State.IsTenantUnplaced(null, null, "initech").Should().BeTrue();
        }
        else
        {
            page.WaitForAssertion(() => page.Markup.Should().Contain("Customer 01"));
            context.State.ActiveScope!.DatabaseId.Should().Be(placedIn);
            context.State.IsTenantUnplaced(null, null, "initech").Should().BeFalse();
        }
    }

    /// <summary>A scope that did not move still notifies the pages once an address's placement has been settled, and not again for the same address.</summary>
    [Fact]
    public async Task The_scope_listeners_are_told_when_a_placement_settles_even_if_the_scope_did_not_move_and_not_again()
    {
        using DocumentsComponentContext context = TwoDatabases();
        context.Catalog.WithPlacement("default", "initech", "db-a");
        await context.State.EnsureInitializedAsync(Token);
        int notified = 0;
        context.State.ScopeChanged += () =>
        {
            notified++;
            return Task.CompletedTask;
        };

        await context.State.SetScopeAsync("default", null, "initech", Token);
        notified.Should().Be(1, "placed in the database already on screen: the scope did not move, the placement did");

        await context.State.SetScopeAsync("default", null, "initech", Token);
        notified.Should().Be(1, "the same address, remembered: nothing changed");
    }
}
