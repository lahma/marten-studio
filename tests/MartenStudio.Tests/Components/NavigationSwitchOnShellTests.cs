using Bunit;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>The tests that set the process-wide <c>PostgresStudio.DisableNavigationInterception</c> switch run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NavigationSwitchOnSerial
{
    /// <summary>The collection's name.</summary>
    public const string Name = "NavigationSwitchOn";
}

/// <summary>
/// 1.2-FINAL-FIX item 8: with <c>PostgresStudio.DisableNavigationInterception</c> on - which restores 1.1's navigation - the shell writes
/// nothing into the address on a location change that was not intercepted. Back and Forward are reported as not intercepted there, the
/// pages that follow their own address apply the scope it names as they did in 1.1, and a rewrite from the shell, racing a page's own
/// scope change under a policy that yields, bounced the address between two databases (a page fetch on every turn).
/// </summary>
[Collection(NavigationSwitchOnSerial.Name)]
public class NavigationSwitchOnShellTests
{
    private sealed record Rig(StudioComponentContext Context, ClickableNavigationManager Navigation, IRenderedComponent<StudioRoutes> Routes);

    private static async Task<Rig> StartAsync()
    {
        ClickableNavigationManager navigation = new("http://localhost/marten/");

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

        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => context.State.ActiveScope.Should().NotBeNull());
        routes.WaitForAssertion(() => navigation.Uri.Should().Contain("store=default"));

        return new Rig(context, navigation, routes);
    }

    private static async Task WithSwitchOnAsync(Func<Task> body)
    {
        AppContext.SetSwitch(StudioNavigationSwitch.Name, true);
        try
        {
            await body();
        }
        finally
        {
            AppContext.SetSwitch(StudioNavigationSwitch.Name, false);
        }
    }

    [Theory]
    [InlineData("?store=other&db=db-x")]
    [InlineData("?store=other")]
    [InlineData("")]
    public async Task A_history_move_that_is_not_intercepted_is_not_rewritten_by_the_shell_with_the_switch_on(string named) =>
        await WithSwitchOnAsync(async () =>
        {
            Rig rig = await StartAsync();
            await using StudioComponentContext context = rig.Context;
            int requests = rig.Navigation.Requests.Count;

            rig.Navigation.HistoryMove(named, intercepted: false);
            await rig.Routes.InvokeAsync(() => { });

            rig.Navigation.Requests.Skip(requests).Should().BeEmpty("the shell wrote nothing: the address is the pages' to follow");
            context.State.ActiveScope.Should().Be(new StudioScope("default", "db-a", null), "the shell never moved the scope for a navigation it was not told was a click");
        });

    [Fact]
    public async Task With_the_switch_off_the_same_history_move_is_rewritten_to_the_scope_on_screen()
    {
        AppContext.SetSwitch(StudioNavigationSwitch.Name, false);
        Rig rig = await StartAsync();
        await using StudioComponentContext context = rig.Context;

        rig.Navigation.HistoryMove("?store=other&db=db-x", intercepted: false);
        await rig.Routes.InvokeAsync(() => { });

        rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=default").And.NotContain("store=other"));
    }

    [Fact]
    public async Task A_scope_change_still_writes_the_scope_on_screen_into_the_address_with_the_switch_on() =>
        await WithSwitchOnAsync(async () =>
        {
            Rig rig = await StartAsync();
            await using StudioComponentContext context = rig.Context;

            await rig.Routes.InvokeAsync(() => context.State.SetScopeAsync("other", "db-x", null));

            rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=other").And.Contain("db=db-x"));
        });

    [Fact]
    public async Task A_clicked_link_to_another_store_still_moves_the_scope_with_the_switch_on() =>
        await WithSwitchOnAsync(async () =>
        {
            Rig rig = await StartAsync();
            await using StudioComponentContext context = rig.Context;

            rig.Navigation.Click("?store=other&db=db-x");

            rig.Routes.WaitForAssertion(() => context.State.ActiveScope!.StoreKey.Should().Be("other"));
        });
}
