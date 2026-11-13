using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using PostgresStudio.Components;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-NAV (D-NAV N1, N2, N3, N12): <c>StudioRoutes</c> asks for navigation interception once, in its first interactive render; an
/// intercepted address no studio page matches is loaded by the browser, and nothing else is; a change of path or fragment scrolls to
/// the fragment as the built-in Router does.
/// </summary>
public sealed class StudioRoutesTests
{
    private const string Start = "http://localhost/postgres/database/connection";

    private sealed record Rig(
        PostgresStudioComponentContext Context,
        RecordingNavigationManager Navigation,
        RecordingNavigationInterception Interception,
        RecordingScrollToLocationHash Scroll);

    private static async Task<Rig> NewRigAsync(string startAt = Start)
    {
        var navigation = new RecordingNavigationManager(startAt);
        var interception = new RecordingNavigationInterception();
        var scroll = new RecordingScrollToLocationHash();

        PostgresStudioComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception>(interception);
            services.AddSingleton<IScrollToLocationHash>(scroll);
        });

        await context.ReadyAsync();
        return new Rig(context, navigation, interception, scroll);
    }

    /// <summary>Waits until everything queued on the renderer's dispatcher, a render and its after-render among it, has run.</summary>
    private static Task SettleAsync(IRenderedComponent<StudioRoutes> routes) => routes.InvokeAsync(() => { });

    [Fact]
    public async Task Interception_is_asked_exactly_once_after_the_first_render_and_not_again()
    {
        Rig rig = await NewRigAsync();
        await using PostgresStudioComponentContext context = rig.Context;

        var routes = context.Render<StudioRoutes>();

        routes.WaitForAssertion(() => rig.Interception.Calls.Should().Be(1));

        rig.Navigation.Simulate("postgres/health", intercepted: true);
        rig.Navigation.Simulate("postgres/database/connection?x=1", intercepted: true);
        routes.Render();
        await SettleAsync(routes);

        rig.Interception.Calls.Should().Be(1, "later renders and location changes never ask again");
    }

    [Fact]
    public async Task An_intercepted_address_no_page_matches_is_force_loaded_once_and_the_page_on_screen_stays()
    {
        Rig rig = await NewRigAsync();
        await using PostgresStudioComponentContext context = rig.Context;
        var routes = context.Render<StudioRoutes>();
        routes.Find("h1").TextContent.Should().Be("Connection", "the premise");

        rig.Navigation.Simulate("postgres/no-such-page", intercepted: true);
        await SettleAsync(routes);

        (string Uri, NavigationOptions Options) request = rig.Navigation.ForceLoads.Should().ContainSingle().Subject;
        request.Uri.Should().Be("http://localhost/postgres/no-such-page");
        request.Options.ForceLoad.Should().BeTrue();
        routes.Find("h1").TextContent.Should().Be("Connection", "the page on screen stays until the browser has loaded the address");
        routes.Markup.Should().NotContain("The requested page was not found.");
    }

    /// <summary>
    /// 1.2-UX-FIX-FU (U8): the shell makes the address follow the scope after a location change, but never for an address no studio page
    /// matches - a file, an endpoint - which is the browser's to load as named. Without the route guard in <c>StudioLayout</c> the shell
    /// replaced it with the same address plus the scope, a request of its own beside the force-load.
    /// </summary>
    [Fact]
    public async Task An_intercepted_address_no_page_matches_is_not_given_the_scope_by_the_shell()
    {
        Rig rig = await NewRigAsync();
        await using PostgresStudioComponentContext context = rig.Context;
        var routes = context.Render<StudioRoutes>();
        routes.Find("h1").TextContent.Should().Be("Connection", "the premise");
        await SettleAsync(routes);
        int before = rig.Navigation.Requests.Count;

        rig.Navigation.Simulate("postgres/no-such-page", intercepted: true);
        await SettleAsync(routes);

        (string Uri, NavigationOptions Options) only = rig.Navigation.Requests.Skip(before).Should().ContainSingle("only the force-load, and no rewrite of the address").Subject;
        only.Options.ForceLoad.Should().BeTrue();
        only.Uri.Should().Be("http://localhost/postgres/no-such-page");
    }

    /// <summary>
    /// 1.2-UX-FIX-FU3 (F5): the same, when the address also names a scope the listings do not carry. The scope does not move, and the
    /// shell's rewrite - which would put the scope on screen into the address - is held to studio pages here too.
    /// </summary>
    [Fact]
    public async Task An_intercepted_non_page_address_naming_a_refused_scope_records_only_the_force_load()
    {
        Rig rig = await NewRigAsync();
        await using PostgresStudioComponentContext context = rig.Context;
        var routes = context.Render<StudioRoutes>();
        routes.Find("h1").TextContent.Should().Be("Connection", "the premise");
        await SettleAsync(routes);
        int before = rig.Navigation.Requests.Count;

        rig.Navigation.Simulate("postgres/no-such-page?store=nowhere&db=db-q", intercepted: true);
        await SettleAsync(routes);
        await Task.Yield();
        await SettleAsync(routes);

        (string Uri, NavigationOptions Options) only = rig.Navigation.Requests.Skip(before).Should().ContainSingle("only the force-load, and no rewrite").Subject;
        only.Options.ForceLoad.Should().BeTrue();
    }

    [Fact]
    public async Task An_address_no_page_matches_that_the_browser_did_not_intercept_shows_not_found_and_loads_nothing()
    {
        Rig rig = await NewRigAsync();
        await using PostgresStudioComponentContext context = rig.Context;
        var routes = context.Render<StudioRoutes>();

        rig.Navigation.Simulate("postgres/no-such-page", intercepted: false);

        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Should().Be("Not found"));
        rig.Navigation.ForceLoads.Should().BeEmpty("a studio navigation to a non-page is a bug and is shown, not loaded");
    }

    [Fact]
    public async Task An_intercepted_address_that_is_a_page_shows_that_page_and_loads_nothing()
    {
        Rig rig = await NewRigAsync();
        await using PostgresStudioComponentContext context = rig.Context;
        var routes = context.Render<StudioRoutes>();

        rig.Navigation.Simulate("postgres/health", intercepted: true);

        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Should().NotBe("Connection"));
        routes.Markup.Should().NotContain("The requested page was not found.");
        rig.Navigation.ForceLoads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_document_that_starts_on_an_unmatched_address_shows_not_found_and_never_force_loads()
    {
        Rig rig = await NewRigAsync("http://localhost/postgres/no-such-page");
        await using PostgresStudioComponentContext context = rig.Context;

        var routes = context.Render<StudioRoutes>();
        await SettleAsync(routes);

        routes.Find("h1").TextContent.Should().Be("Not found", "the first address is read without an event, so a load cannot loop");
        rig.Navigation.ForceLoads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lost_circuit_or_a_second_renderer_holding_interception_costs_nothing()
    {
        foreach (Exception failure in new Exception[] { new JSDisconnectedException("the circuit closed"), new JSException("another renderer has it") })
        {
            Rig rig = await NewRigAsync();
            await using PostgresStudioComponentContext context = rig.Context;
            rig.Interception.Failure = failure;

            var routes = context.Render<StudioRoutes>();

            routes.WaitForAssertion(() => rig.Interception.Calls.Should().Be(1));
            await SettleAsync(routes);
            routes.Find("h1").TextContent.Should().Be("Connection", failure.GetType().Name + ": the page renders");
            await AssertEnhancedNavigationBehaviourAsync(rig, routes);
        }
    }

    /// <summary>
    /// With interception off - an ask that failed - the enhanced navigation is in charge: it reports a link
    /// click as intercepted and scrolls to a fragment itself, so the studio's force-load (which would abort its fetch) and its
    /// own fragment scroll (the page would scroll twice) must not run.
    /// </summary>
    private static async Task AssertEnhancedNavigationBehaviourAsync(Rig rig, IRenderedComponent<StudioRoutes> routes)
    {
        rig.Navigation.Simulate("postgres/no-such-page", intercepted: true);
        await SettleAsync(routes);

        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Should().Be("Not found"));
        rig.Navigation.ForceLoads.Should().BeEmpty("enhanced navigation reports a click as intercepted, and a force-load would abort its fetch");

        rig.Navigation.Simulate("postgres/database/connection#conn-marten", intercepted: true);
        await SettleAsync(routes);

        rig.Scroll.Refreshed.Should().BeEmpty("enhanced navigation scrolls to the fragment itself");
    }

    [Fact]
    public async Task The_fragment_is_scrolled_to_when_the_path_or_the_fragment_changes_and_never_for_a_query_only_change()
    {
        Rig rig = await NewRigAsync("http://localhost/postgres/health");
        await using PostgresStudioComponentContext context = rig.Context;
        var routes = context.Render<StudioRoutes>();
        await SettleAsync(routes);
        rig.Scroll.Refreshed.Should().BeEmpty("the first render does nothing: the browser scrolled the document itself");

        // A path change to an address with a fragment.
        rig.Navigation.Simulate("postgres/database/connection#conn-row-host", intercepted: true);
        routes.WaitForAssertion(() => rig.Scroll.Refreshed.Should().Equal("http://localhost/postgres/database/connection#conn-row-host"));

        // A fragment change on the same path.
        rig.Navigation.Simulate("postgres/database/connection#conn-marten", intercepted: true);
        routes.WaitForAssertion(() => rig.Scroll.Refreshed.Should().HaveCount(2));

        // A query-only change: a push keeps the fragment, and must not pull the visitor back to it.
        rig.Navigation.Simulate("postgres/database/connection?x=2#conn-marten", intercepted: true);
        await SettleAsync(routes);
        rig.Scroll.Refreshed.Should().HaveCount(2);

        // An address with no fragment.
        rig.Navigation.Simulate("postgres/health", intercepted: true);
        await SettleAsync(routes);
        rig.Scroll.Refreshed.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_scroll_that_fails_because_the_circuit_closed_costs_nothing()
    {
        Rig rig = await NewRigAsync("http://localhost/postgres/health");
        await using PostgresStudioComponentContext context = rig.Context;
        rig.Scroll.Failure = new JSDisconnectedException("the circuit closed");
        var routes = context.Render<StudioRoutes>();

        rig.Navigation.Simulate("postgres/database/connection#conn-marten", intercepted: true);

        routes.WaitForAssertion(() => rig.Scroll.Refreshed.Should().ContainSingle());
        await SettleAsync(routes);
        routes.Find("h1").TextContent.Should().Be("Connection");
    }
}
