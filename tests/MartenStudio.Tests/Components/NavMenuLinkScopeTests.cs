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
/// 1.2-HEADER item 4 (NAV-A review F3): the rail follows a scope change at once. <c>StudioState.NotifyAsync</c> awaits its handlers one
/// after another and the shell's is first, so the rail's own used to run a policy read and a round trip after the scope moved - and in
/// that time a rail link carried the old scope, which a click then carried back (the shell follows an intercepted link's scope). The
/// rail redraws from <see cref="StudioState.LinkScopeChanged">a synchronous event</see>, from the scope being applied.
/// </summary>
public class NavMenuLinkScopeTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static string[] RailHrefs(IRenderedComponent<NavMenu> nav) =>
        [.. nav.FindAll("a.ms-nav-link").Select(static x => x.GetAttribute("href") ?? string.Empty)];

    /// <summary>
    /// The picker moves the scope to a store whose facts take a while to read: until they are read the active scope is the old one, and
    /// every rail link already carries the one being applied.
    /// </summary>
    [Fact]
    public async Task While_a_scope_change_reads_the_catalog_the_rail_links_carry_the_scope_being_applied()
    {
        await using StudioComponentContext context = new();
        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();
        RailHrefs(nav).Should().OnlyContain(static x => x.Contains("store=default"), "the premise: the rail starts on the first store");

        context.Catalog.DescribeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task change = nav.InvokeAsync(() => context.State.SetScopeAsync("other", null, null, Token));

        nav.WaitForAssertion(() =>
        {
            context.State.ActiveScope!.StoreKey.Should().Be("default", "the premise: the change is still reading, so the scope has not moved");
            RailHrefs(nav).Should().OnlyContain(static x => x.Contains("store=other"), "a click now must carry the scope being applied");
        });

        context.Catalog.DescribeGate.SetResult();
        await change;

        context.State.ActiveScope!.StoreKey.Should().Be("other");
        RailHrefs(nav).Should().OnlyContain(static x => x.Contains("store=other") && x.Contains("db=db-x"));
    }

    /// <summary>
    /// The scope has changed and the handlers are being awaited one after another: the first (the shell's, which asks the policy) has not
    /// returned, so the rail's own handler has not even started. The links are the new scope's all the same.
    /// </summary>
    [Fact]
    public async Task A_slow_first_scope_handler_does_not_leave_the_rail_links_on_the_old_scope()
    {
        await using StudioComponentContext context = new();
        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");
        await context.State.EnsureInitializedAsync(Token);

        // Subscribed before the rail, so it is called first - as the layout's is.
        TaskCompletionSource firstHandlerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.State.ScopeChanged += () => firstHandlerGate.Task;

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();
        RailHrefs(nav).Should().OnlyContain(static x => x.Contains("store=default"));

        Task change = nav.InvokeAsync(() => context.State.SetScopeAsync("other", null, null, Token));

        nav.WaitForAssertion(() =>
        {
            context.State.ActiveScope!.StoreKey.Should().Be("other", "the scope has moved");
            RailHrefs(nav).Should().OnlyContain(
                static x => x.Contains("store=other") && x.Contains("db=db-x"),
                "the rail is redrawn from the synchronous event, not after the first handler's policy read");
        });

        change.IsCompleted.Should().BeFalse("the premise: the first handler is still holding the notification");

        firstHandlerGate.SetResult();
        await change;
    }

    /// <summary>
    /// A change that threw reads nothing and applies nothing: the links go back to the active scope rather than keep the one that failed.
    /// </summary>
    [Fact]
    public async Task A_scope_change_that_failed_leaves_the_links_on_the_active_scope()
    {
        await using StudioComponentContext context = new();
        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();

        context.Catalog.DescribeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task change = nav.InvokeAsync(() => context.State.SetScopeAsync("other", null, null, Token));
        nav.WaitForAssertion(() => RailHrefs(nav).Should().OnlyContain(static x => x.Contains("store=other")));

        context.Catalog.DescribeGate.SetException(new InvalidOperationException("the catalog failed"));
        Func<Task> act = () => change;
        await act.Should().ThrowAsync<InvalidOperationException>();

        nav.WaitForAssertion(() => RailHrefs(nav).Should().OnlyContain(static x => x.Contains("store=default")));
        context.State.ActiveScope!.StoreKey.Should().Be("default");
    }

    /// <summary>A page re-validating the scope it was opened with moves nothing, so it redraws nothing.</summary>
    [Fact]
    public async Task Setting_the_scope_the_studio_is_already_on_redraws_no_links()
    {
        await using StudioComponentContext context = new();
        context.Options.RefreshInterval = TimeSpan.FromMinutes(5);
        context.Catalog.WithStore("default", "Default", databaseIdentities: "db-a");
        await context.State.EnsureInitializedAsync(Token);

        int raised = 0;
        context.State.LinkScopeChanged += () => raised++;

        await context.State.SetScopeAsync("default", "db-a", null, Token);

        raised.Should().Be(0);
    }

    // -----------------------------------------------------------------------------------------------
    // N5a's window: the first load applies the scope a pasted address names
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The address names the second store, and the listings are slow. The rail is drawn while they are read, with the address's scope in
    /// its links; a click inside that window lands on the address's store, not the one the studio opens on.
    /// </summary>
    [Fact]
    public async Task During_the_first_load_the_rail_carries_the_scope_the_pasted_address_names_and_a_click_keeps_it()
    {
        ClickableNavigationManager navigation = new("http://localhost/marten/?store=other&db=db-x");

        await using StudioComponentContext context = new(services =>
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

        // The listings are being read: the first load is held half way, with no active scope at all.
        context.Catalog.DescribeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();

        routes.WaitForAssertion(() =>
        {
            context.State.ActiveScope.Should().BeNull("the premise: the listings have not been read");
            string[] hrefs = [.. routes.FindAll(".ms-sidebar a.ms-nav-link").Select(static x => x.GetAttribute("href") ?? string.Empty)];
            hrefs.Should().NotBeEmpty();
            hrefs.Should().OnlyContain(static x => x.Contains("store=other") && x.Contains("db=db-x"), "the address's scope, not nothing and not the first store's");
        });

        // A click on a rail link inside the window.
        string stores = routes.FindAll(".ms-sidebar a.ms-nav-link").First(static x => (x.GetAttribute("href") ?? string.Empty).StartsWith("stores", StringComparison.Ordinal)).GetAttribute("href")!;
        navigation.Click(stores);

        context.Catalog.DescribeGate.SetResult();

        routes.WaitForAssertion(() => context.State.ActiveScope.Should().Be(new StudioScope("other", "db-x", null)));
        routes.WaitForAssertion(() => context.CurrentUri.Should().Contain("store=other").And.Contain("stores"));

        // And once settled the rail is the active scope's, with nothing left over from the window.
        routes.WaitForAssertion(() => routes.FindAll(".ms-sidebar a.ms-nav-link").Select(static x => x.GetAttribute("href") ?? string.Empty)
            .Should().OnlyContain(static x => x.Contains("store=other") && x.Contains("db=db-x")));
    }

    /// <summary>An address that names no scope puts nothing in the window: the links are the active scope's, as ever.</summary>
    [Fact]
    public async Task An_address_naming_no_scope_leaves_the_rail_to_the_active_scope()
    {
        await using StudioComponentContext context = new();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "db-a");
        await context.State.EnsureInitializedAsync(Token);

        context.State.BeginApplying(null, null, null);

        context.State.LinkScope.Should().Be(context.State.ActiveScope);
    }
}
