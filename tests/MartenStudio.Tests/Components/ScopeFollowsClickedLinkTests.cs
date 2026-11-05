using Bunit;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components;
using PostgresStudio.Components.Layout;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-SCOPE-1-FIX B1: the shell follows a link the visitor clicked, and nothing else. With NAV's interception a click is handled in the
/// circuit and the layout is kept, so a link to another store - the Overview's strip, the Stores page's "Open overview", a database
/// row's links - changed the address and left the store shown unchanged. Over <c>StudioRoutes</c>, with two stores, as the reviewer's
/// proof was.
/// </summary>
public class ScopeFollowsClickedLinkTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private sealed record Rig(StudioComponentContext Context, ClickableNavigationManager Navigation, IRenderedComponent<StudioRoutes> Routes, List<StudioScope?> Scopes);

    private static async Task<Rig> StartAsync(string relativeStart = "")
    {
        ClickableNavigationManager navigation = new("http://localhost/marten/" + relativeStart);

        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception, QuietNavigationInterception>();
            services.AddSingleton<IScrollToLocationHash, QuietScrollToLocationHash>();
        });

        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.StoreInfo.WithStore("default", "Default", databaseIdentity: "db-a").WithStore("other", "Other", databaseIdentity: "db-x");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");

        List<StudioScope?> scopes = [];
        context.State.ScopeChanged += () =>
        {
            scopes.Add(context.State.ActiveScope);
            return Task.CompletedTask;
        };

        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();

        // The layout has read the (empty) address and settled the first store.
        routes.WaitForAssertion(() => context.State.ActiveScope.Should().NotBeNull());
        context.State.ActiveScope!.StoreKey.Should().Be("default", "the premise");

        return new Rig(context, navigation, routes, scopes);
    }

    [Fact]
    public async Task The_overview_strips_link_to_another_store_changes_the_store_shown()
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;

        rig.Routes.WaitForAssertion(() => rig.Routes.Find(".ms-overview-store .ms-section-key").TextContent.Trim().Should().Be("default"));

        string href = rig.Routes.FindAll(".ms-stores-strip-link").Single(static x => x.TextContent.Trim() == "Other").GetAttribute("href")!;
        rig.Navigation.Click(href);

        rig.Routes.WaitForAssertion(() =>
        {
            context.State.ActiveScope!.StoreKey.Should().Be("other");
            rig.Routes.Find(".ms-overview-store .ms-section-key").TextContent.Trim().Should().Be("other", "the card is the new store's");
        });
    }

    [Fact]
    public async Task The_stores_pages_open_overview_link_shows_that_stores_overview()
    {
        Rig rig = await StartAsync("stores");
        await using StudioComponentContext context = rig.Context;

        rig.Routes.WaitForAssertion(() => rig.Routes.FindAll(".ms-stores-open-overview").Should().HaveCount(2));

        string href = rig.Routes.FindAll(".ms-stores-store")[1].QuerySelector(".ms-stores-open-overview")!.GetAttribute("href")!;
        rig.Navigation.Click(href);

        rig.Routes.WaitForAssertion(() =>
        {
            context.State.ActiveScope!.StoreKey.Should().Be("other");
            rig.Routes.Find(".ms-overview-store .ms-section-key").TextContent.Trim().Should().Be("other");
        });
    }

    [Fact]
    public async Task A_database_rows_health_link_opens_the_health_page_on_that_stores_database()
    {
        Rig rig = await StartAsync("stores");
        await using StudioComponentContext context = rig.Context;

        rig.Routes.WaitForAssertion(() => rig.Routes.FindAll(".ms-stores-store").Should().HaveCount(2));
        rig.Routes.WaitForAssertion(() => rig.Routes.FindAll(".ms-stores-store")[1].QuerySelectorAll("tbody tr").Should().ContainSingle());

        string href = rig.Routes.FindAll(".ms-stores-store")[1].QuerySelectorAll("tbody td:last-child a")
            .Single(static x => x.TextContent.Trim() == "Health").GetAttribute("href")!;
        rig.Navigation.Click(href);

        rig.Routes.WaitForAssertion(() =>
        {
            context.State.ActiveScope.Should().Be(new StudioScope("other", "db-x", null));
            rig.Routes.Find("h1").TextContent.Trim().Should().Be("Health");
        });
    }

    [Fact]
    public async Task A_programmatic_navigation_to_another_store_never_changes_the_scope()
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;
        int changes = rig.Scopes.Count;

        // A page's own push, the shell's replace and any other NavigateTo report that the browser did not intercept anything.
        await rig.Routes.InvokeAsync(() => rig.Navigation.NavigateTo("?store=other&db=db-x"));
        await rig.Routes.InvokeAsync(() => rig.Navigation.NavigateTo("?store=other", replace: true));
        await rig.Routes.InvokeAsync(() => { });

        context.State.ActiveScope!.StoreKey.Should().Be("default");
        rig.Scopes.Should().HaveCount(changes, "no ScopeChanged was raised");
    }

    // Back and Forward. The browser reports a popstate as an intercepted navigation (blazor.web.js onBrowserInitiatedPopState:
    // notifyLocationChanged(true)), so the shell cannot tell it from a click: HistoryMove models it that way by default, and as not
    // intercepted where navigation interception is off and the enhanced navigation reports it.

    /// <summary>(a) Back to an address naming another scope the visitor's listings carry moves the scope, as a click does.</summary>
    [Fact]
    public async Task Back_to_an_address_naming_another_scope_the_listings_carry_moves_the_scope_as_a_click_does()
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;
        rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=default"));
        string before = rig.Navigation.Uri;

        string href = rig.Routes.FindAll(".ms-stores-strip-link").Single(static x => x.TextContent.Trim() == "Other").GetAttribute("href")!;
        rig.Navigation.Click(href);
        rig.Routes.WaitForAssertion(() =>
        {
            context.State.ActiveScope!.StoreKey.Should().Be("other");
            rig.Navigation.Uri.Should().Contain("store=other");
        });
        string after = rig.Navigation.Uri;

        // Back: the browser returns to the address before the click, which names the first store.
        rig.Navigation.HistoryMove(before);

        rig.Routes.WaitForAssertion(() => context.State.ActiveScope!.StoreKey.Should().Be("default", "the address names a store the listings carry"));
        rig.Navigation.Uri.Should().Contain("store=default").And.NotContain("store=other");

        // And Forward again.
        rig.Navigation.HistoryMove(after);
        rig.Routes.WaitForAssertion(() => context.State.ActiveScope!.StoreKey.Should().Be("other"));
    }

    /// <summary>(b) Back to an address naming a scope the listings do not carry does not move the scope.</summary>
    [Fact]
    public async Task Back_to_an_address_naming_a_scope_the_listings_do_not_carry_does_not_move_the_scope()
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;
        int changes = rig.Scopes.Count;

        rig.Navigation.HistoryMove("?store=nowhere&db=db-q");
        await rig.Routes.InvokeAsync(() => { });

        context.State.ActiveScope.Should().Be(new StudioScope("default", "db-a", null), "ignored exactly as at first load");
        rig.Scopes.Should().HaveCount(changes);
    }

    /// <summary>
    /// With navigation interception off the enhanced navigation reports Back as not intercepted: the scope does not move and the address
    /// follows it again, as it does for any programmatic navigation.
    /// </summary>
    [Fact]
    public async Task With_interception_off_Back_to_another_scopes_address_leaves_the_scope_and_rewrites_the_address()
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;
        int changes = rig.Scopes.Count;

        rig.Navigation.HistoryMove("?store=other&db=db-x", intercepted: false);
        await rig.Routes.InvokeAsync(() => { });

        context.State.ActiveScope!.StoreKey.Should().Be("default");
        rig.Scopes.Should().HaveCount(changes);
        rig.Navigation.Uri.Should().Contain("store=default").And.NotContain("store=other", "the address follows the scope again");
    }

    /// <summary>
    /// (c) Back to an address naming only part of the scope on screen - or none of it - rewrites the address to the whole of it, and the
    /// scope does not move. Reported both ways: as the browser reports a popstate, and as the enhanced navigation does with interception off.
    /// </summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("", false)]
    [InlineData("?store=other", true)]
    [InlineData("?store=other", false)]
    [InlineData("?db=db-x", true)]
    [InlineData("?db=db-x", false)]
    public async Task Back_to_an_address_naming_part_of_the_scope_on_screen_rewrites_the_address_and_leaves_the_scope(string named, bool intercepted)
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;
        rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=default"));

        string href = rig.Routes.FindAll(".ms-stores-strip-link").Single(static x => x.TextContent.Trim() == "Other").GetAttribute("href")!;
        rig.Navigation.Click(href);
        rig.Routes.WaitForAssertion(() => context.State.ActiveScope!.StoreKey.Should().Be("other"));
        rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=other"));
        int changes = rig.Scopes.Count;
        int requests = rig.Navigation.Requests.Count;

        rig.Navigation.HistoryMove(named, intercepted);

        rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=other").And.Contain("db=db-x"));
        context.State.ActiveScope.Should().Be(new StudioScope("other", "db-x", null), "an address that names no other scope moves nothing");
        rig.Scopes.Should().HaveCount(changes);
        rig.Navigation.Requests.Skip(requests).Should().ContainSingle("one rewrite").Which.Options.ReplaceHistoryEntry.Should().BeTrue();
    }

    [Fact]
    public async Task A_clicked_link_with_the_scope_the_shell_is_on_or_none_changes_nothing()
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;
        int changes = rig.Scopes.Count;

        rig.Navigation.Click("health?store=default&db=db-a");
        rig.Navigation.Click("documents");
        await rig.Routes.InvokeAsync(() => { });

        context.State.ActiveScope.Should().Be(new StudioScope("default", "db-a", null));
        rig.Scopes.Should().HaveCount(changes, "the same scope, and an address that names none, ask nothing");
    }

    [Fact]
    public async Task A_clicked_link_naming_a_store_the_listings_do_not_carry_is_ignored()
    {
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;

        rig.Navigation.Click("?store=nowhere");
        await rig.Routes.InvokeAsync(() => { });

        context.State.ActiveScope!.StoreKey.Should().Be("default", "ignored exactly as at first load");
    }

    [Theory]
    [InlineData("default", "db-a", null, null, null, null, false)]
    [InlineData("default", "db-a", null, "default", "db-a", null, false)]
    [InlineData("DEFAULT", "DB-A", null, "default", "db-a", null, false)]
    [InlineData("default", "db-a", null, "other", null, null, true)]
    [InlineData("default", "db-a", null, null, "db-b", null, true)]
    [InlineData("default", "db-a", null, null, null, "acme", true)]
    [InlineData("default", "db-a", "acme", null, null, "ACME", true)]
    [InlineData("default", "db-a", "acme", null, null, "acme", false)]
    public void An_address_names_another_scope_only_for_a_part_it_names_that_differs(
        string store, string database, string? tenant, string? namedStore, string? namedDatabase, string? namedTenant, bool another) =>
        StudioLayout.NamesAnotherScope(new StudioScope(store, database, tenant), namedStore, namedDatabase, namedTenant).Should().Be(another);

    [Fact]
    public void With_no_scope_yet_any_named_part_is_another_scope()
    {
        StudioLayout.NamesAnotherScope(null, "default", null, null).Should().BeTrue();
        StudioLayout.NamesAnotherScope(null, null, null, null).Should().BeFalse();
    }
}
