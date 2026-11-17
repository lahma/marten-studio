using Bunit;

using DataStudio.Components;
using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Marten.Tests.Components;

/// <summary>
/// v2 step 5, decision 2: a page of no module (the shell's own Activity, an address no page matches) draws no scope selector, no header search and no scope
/// gate, and the database module's circuit host is told it is not the page on screen, so the database's scope waits in memory. The rail stays whole. A page of
/// the Marten module, on the same composition, draws all of them.
/// </summary>
public class PagesOfNoModuleTests
{
    private static async Task<(StudioComponentContext Context, IRenderedComponent<StudioRoutes> Routes)> RenderAtAsync(string address)
    {
        ClickableNavigationManager navigation = new("http://localhost/studio/" + address);

        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception, QuietNavigationInterception>();
            services.AddSingleton<IScrollToLocationHash, QuietScrollToLocationHash>();
        });

        context.ShellOptions.RefreshInterval = TimeSpan.FromMinutes(5);
        context.StoreInfo.WithStore("default", "Default", databaseIdentity: "db-a").WithStore("other", "Other", databaseIdentity: "db-x");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");
        context.WithPolicy("default", "other");

        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();
        await routes.InvokeAsync(() => { });
        return (context, routes);
    }

    [Theory]
    [InlineData("activity")]
    [InlineData("no-such-page")]
    public async Task A_page_of_no_module_has_no_scope_selector_no_search_and_no_gate_and_the_rail_stays_whole(string address)
    {
        (StudioComponentContext context, IRenderedComponent<StudioRoutes> routes) = await RenderAtAsync(address);
        await using StudioComponentContext _ = context;

        routes.FindAll(".ms-scope-selector").Should().BeEmpty("a page of no module has no scope to pick");
        routes.FindAll(".ms-header-find").Should().BeEmpty("the database's search belongs to the database's pages");
        routes.FindAll(".ms-loading-spinner").Should().BeEmpty("no gate stands in front of it");
        routes.FindAll(".ms-sidebar a.ms-nav-link").Select(static x => x.GetAttribute("href")?.Split('?')[0]).Should().Contain(["marten", "marten/documents", "postgres/health", "activity"], "the rail stays whole");
        context.AuthorizationService.Calls.Should().BeEmpty("the database's scope policy is not asked about a page that is not about a store");
    }

    [Fact]
    public async Task A_page_of_the_marten_module_draws_its_scope_selector()
    {
        (StudioComponentContext context, IRenderedComponent<StudioRoutes> routes) = await RenderAtAsync("marten");
        await using StudioComponentContext _ = context;

        routes.WaitForAssertion(() =>
        {
            routes.FindAll(".ms-scope-selector").Should().ContainSingle();
        });
    }
}
