using Bunit;

using DataStudio.Components;
using DataStudio.Components.Layout;
using DataStudio.Components.Pages;
using DataStudio.Marten.Services;
using DataStudio.Marten.Tests.Support;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Database;
using DataStudio.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Marten.Tests.Components;

/// <summary>
/// D34, the database module's half, proved on the real <c>DatabaseScopeHost</c>, <c>DatabaseScopeGate</c> and <c>StudioState</c> of the Marten module beside
/// a module this suite defines (<see cref="SecondModule" />, which has a page of its own and no gate): the database's scope waits in memory on another
/// module's page, its policy is not asked there, and the way back asks the gate again before a database page's body is read. The Postgres suite holds the
/// same over its own module, and the one case that needs a catalog load that can be held (a click during the load) is its alone: the guard it proves is in the
/// one <c>DatabaseScopeHost</c> both modules register, and nothing module-specific reaches it.
/// </summary>
/// <remarks>
/// Each test names the one-line change to the source that makes it fail, and was seen to fail with it (v2-G).
/// </remarks>
public sealed class DatabaseModuleBesideAnotherModuleTests
{
    private const string DatabasePage = "postgres/database/connection";

    private const string SecondPage = SecondModule.PageAddress;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(StudioComponentContext context, ClickableNavigationManager navigation, IRenderedComponent<StudioRoutes> routes)
        {
            Context = context;
            Navigation = navigation;
            Routes = routes;
        }

        public StudioComponentContext Context { get; }

        public ClickableNavigationManager Navigation { get; }

        public IRenderedComponent<StudioRoutes> Routes { get; }

        public int Bodies => Context.Connection.Requests.Count;

        /// <summary>Waits until everything queued on the renderer's dispatcher, a render and its after-render among it, has run.</summary>
        public Task SettleAsync() => Routes.InvokeAsync(() => { });

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    /// <summary>The studio rendered through <c>StudioRoutes</c> at <paramref name="relativeStart" />, with the second module composed after the database's and two stores listed.</summary>
    private static async Task<Rig> StartAsync(string relativeStart, Action<StudioComponentContext>? beforeRender = null)
    {
        ClickableNavigationManager navigation = new("http://localhost/studio/" + relativeStart);

        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception, QuietNavigationInterception>();
            services.AddSingleton<IScrollToLocationHash, QuietScrollToLocationHash>();
            services.AddSingleton(new SessionPoolObserver("DataStudio.Marten.Tests.Beside." + Guid.NewGuid().ToString("N")));
            SecondModule.AddTo(services);
        });

        context.ShellOptions.RefreshInterval = TimeSpan.FromMinutes(5);
        context.StoreInfo.WithStore("default", "Default", databaseIdentity: "db-a").WithStore("other", "Other", databaseIdentity: "db-x");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");
        context.WithPolicy("default", "other");
        beforeRender?.Invoke(context);

        IRenderedComponent<StudioRoutes> routes = context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        return new Rig(context, navigation, routes);
    }

    private static string Heading(IRenderedComponent<StudioRoutes> routes)
    {
        var headings = routes.FindAll("h1");
        return headings.Count == 0 ? string.Empty : headings[0].TextContent.Trim();
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------
    // The database's scope waits in memory on another module's page.

    /// <summary>The control: the same address on a page of the database does move the scope, so the next tests' "nothing moved" is a result.</summary>
    [Fact]
    public async Task An_address_naming_another_scope_on_a_database_page_moves_the_scope_and_a_click_does_too()
    {
        await using Rig rig = await StartAsync(DatabasePage + "?store=other&db=db-x");

        rig.Routes.WaitForAssertion(() => rig.Context.State.ActiveScope.Should().Be(new StudioScope("other", "db-x", null)));

        rig.Navigation.Click(DatabasePage + "?store=default&db=db-a");

        rig.Routes.WaitForAssertion(() => rig.Context.State.ActiveScope.Should().Be(new StudioScope("default", "db-a", null)));
    }

    /// <summary>
    /// Fails when <c>DatabaseScopeHost.OnInitializedAsync</c> reads the address's scope on a page that is not the database's (the <c>Active ? …</c> on the
    /// read): the scope moves to <c>other</c>.
    /// </summary>
    [Fact]
    public async Task An_address_naming_a_database_scope_on_the_other_modules_page_does_not_move_it_on_first_load()
    {
        await using Rig rig = await StartAsync(SecondPage + "?store=other&db=db-x");

        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        rig.Routes.WaitForAssertion(() => rig.Context.State.ActiveScope.Should().NotBeNull("the catalog was loaded while the second module's page was on screen"));
        await rig.SettleAsync();

        rig.Context.State.ActiveScope.Should().Be(new StudioScope("default", "db-a", null), "the database's scope waits in memory");
        rig.Navigation.Requests.Should().BeEmpty("the shell does not write the database's scope into an address it is not about");
        rig.Navigation.Uri.Should().Be("http://localhost/studio/" + SecondPage + "?store=other&db=db-x");
    }

    /// <summary>
    /// Fails when <c>DatabaseScopeHost.OnLocationChanged</c> does not return for a page that is not the database's (the <c>IsMine</c> guard): the clicked
    /// address's scope is applied and the policy is asked.
    /// </summary>
    [Fact]
    public async Task A_click_from_a_database_page_to_the_other_modules_page_naming_another_scope_moves_nothing_and_asks_no_policy()
    {
        await using Rig rig = await StartAsync(DatabasePage);
        rig.Routes.WaitForAssertion(() => rig.Bodies.Should().BePositive("the premise: the database page is on screen"));
        await rig.SettleAsync();
        int asked = rig.Context.AuthorizationService.Calls.Count;
        int requests = rig.Navigation.Requests.Count;

        rig.Navigation.Click(SecondPage + "?store=other&db=db-x");

        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        await rig.SettleAsync();
        await Task.Yield();
        await rig.SettleAsync();

        rig.Context.State.ActiveScope.Should().Be(new StudioScope("default", "db-a", null));
        rig.Context.AuthorizationService.Calls.Should().HaveCount(asked, "no policy is asked about a page that is not about a store");
        rig.Navigation.Requests.Should().HaveCount(requests, "the address is the other module's and is left as the visitor wrote it");
        rig.Navigation.Uri.Should().Be("http://localhost/studio/" + SecondPage + "?store=other&db=db-x");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------
    // The way back asks the gate again.

    /// <summary>
    /// Fails when <c>DatabaseScopeHost.OnParametersSet</c> does not drop the gate to <c>Unknown</c> and ask again on the way back: the answer given
    /// before the visitor left is kept, and the page's body is read for a scope the policy now refuses.
    /// </summary>
    [Fact]
    public async Task Coming_back_to_a_database_page_asks_the_gate_again_and_a_scope_refused_while_away_is_refused_on_return()
    {
        await using Rig rig = await StartAsync(DatabasePage);
        rig.Routes.WaitForAssertion(() => rig.Bodies.Should().Be(1, "the premise: the gate admitted the visitor and the body ran"));

        rig.Navigation.Click(SecondPage);
        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        rig.Context.AuthorizationService.DenyEverything();
        await rig.SettleAsync();
        int asked = rig.Context.AuthorizationService.Calls.Count;

        rig.Navigation.Click(DatabasePage);

        rig.Routes.WaitForAssertion(() => rig.Routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        rig.Context.AuthorizationService.Calls.Count.Should().BeGreaterThan(asked, "the gate was asked again on the way back");
        rig.Bodies.Should().Be(1, "a scope refused while the visitor was away is never read on return");
        rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=default").And.Contain("db=db-a", "the address names the scope again"));
    }

    /// <summary>
    /// The same return with the policy held: the body waits for the answer (the spinner stands in its place) and runs only when the answer is yes. Fails
    /// when the host leaves the gate at its old answer on return.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Coming_back_to_a_database_page_the_body_waits_for_the_policys_answer(bool admitted)
    {
        await using Rig rig = await StartAsync(DatabasePage);
        rig.Routes.WaitForAssertion(() => rig.Bodies.Should().Be(1));
        rig.Navigation.Click(SecondPage);
        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        await rig.SettleAsync();

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Context.AuthorizationService.Gate = gate;
        rig.Navigation.Click(DatabasePage);
        await rig.SettleAsync();

        rig.Routes.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the layout waits for the gate");
        rig.Bodies.Should().Be(1, "the body is not read before the policy has answered");

        if (!admitted)
        {
            rig.Context.AuthorizationService.DenyEverything();
        }

        gate.SetResult();

        if (admitted)
        {
            rig.Routes.WaitForAssertion(() => rig.Bodies.Should().Be(2, "the policy admitted the visitor, so the body runs"));
        }
        else
        {
            rig.Routes.WaitForAssertion(() => rig.Routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
            rig.Bodies.Should().Be(1, "a refused scope is never read, however briefly");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------
    // The decision is the page's assembly's.

    /// <summary>
    /// A page of each, under a policy that refuses the database's scope: the other module's page is drawn with no gate and no scope selector, and a database
    /// page is never drawn without its gate. Fails when the layout keys the gate on anything but the matched page's assembly (a gate for every page, or for none).
    /// </summary>
    [Fact]
    public async Task The_other_modules_page_is_drawn_without_the_databases_gate_and_a_database_page_never_is()
    {
        await using Rig rig = await StartAsync(SecondPage, static context => context.AuthorizationService.DenyEverything());
        await rig.SettleAsync();

        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        rig.Routes.FindAll(".ms-empty-title, .ms-loading-spinner").Should().BeEmpty("no gate stands in front of the other module's page");
        rig.Routes.FindAll(".ms-scope-selector").Should().BeEmpty("the database's scope selector belongs to the database's pages");
        int asked = rig.Context.AuthorizationService.Calls.Count;

        rig.Navigation.Click(DatabasePage);

        rig.Routes.WaitForAssertion(() => rig.Routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        rig.Bodies.Should().Be(0, "the database's body is never drawn without its gate");
        rig.Routes.FindAll(".ms-scope-selector").Should().NotBeEmpty("a database page draws the database's selector");
        rig.Context.AuthorizationService.Calls.Count.Should().BeGreaterThan(asked);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------
    // The shell's own pages with two modules composed.

    private static readonly string Gap = "\n" + new string(' ', 28);

    private static string Row(string when, string user, string scope, string title, string action, string target, bool succeeded, string message) =>
        "<tr class=\"ms-table-row\"><td class=\"ms-table-cell ms-table-cell-mono ms-activity-when\">" + when + "</td>"
        + Gap + "<td class=\"ms-table-cell\">" + user + "</td>"
        + Gap + "<td class=\"ms-table-cell ms-table-cell-mono ms-activity-scope\" title=\"" + title + "\">" + scope + "</td>"
        + Gap + "<td class=\"ms-table-cell\">" + action + "</td>"
        + Gap + "<td class=\"ms-table-cell ms-table-cell-mono ms-activity-target\">" + target + "</td>"
        + Gap + "<td class=\"ms-table-cell\"><span class=\"ms-state-indicator\"><span class=\"ms-state-dot " + (succeeded ? "ms-state-success" : "ms-state-error") + "\" aria-hidden=\"true\"></span>"
        + "\n" + new string(' ', 36) + "<span class=\"ms-state-label\">" + (succeeded ? "succeeded" : "failed") + "</span></span></td>"
        + Gap + "<td class=\"ms-table-cell\">-</td>"
        + Gap + "<td class=\"ms-table-cell ms-activity-message\">" + message + "</td></tr>";

    private static string Separator => "<span class=\"ms-activity-scope-separator\"> · </span>";

    /// <summary>The Activity page draws a database entry's scope from what the database module says, with the separator spans it always had.</summary>
    [Fact]
    public async Task A_database_entry_renders_the_row_the_page_always_drew_beside_another_module()
    {
        await using StudioComponentContext context = new(SecondModule.AddTo);
        await context.ReadyAsync();
        context.AuthenticationState.SignIn("ops");
        context.Preferences.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
        StudioActionLogService ring = context.Services.GetRequiredService<StudioActionLogService>();
        DateTimeOffset at = new(2026, 11, 14, 9, 30, 15, TimeSpan.Zero);

        ring.Record(new StudioActionLogEntry(at, "ops", new DatabaseAuditScope("default", "main", "acme").ToAuditScope(MartenDescriptor.Key), "OpenRow", "public.orders", true, "done", null));
        ring.Record(new StudioActionLogEntry(at.AddMinutes(1), "viewer", new DatabaseAuditScope("default", "main", null).ToAuditScope(MartenDescriptor.Key), "OpenAll", "public.orders", false, "nope", null));

        IRenderedComponent<Activity> page = context.Render<Activity>();

        page.FindAll("tbody tr").Select(static x => x.OuterHtml).Should().Equal(
            Row("2026-11-14 09:31:15 +00:00", "viewer", "default" + Separator + "main" + Separator + "all", "Store default, database main, tenant all", "OpenAll", "public.orders", false, "nope"),
            Row("2026-11-14 09:30:15 +00:00", "ops", "default" + Separator + "main" + Separator + "acme", "Store default, database main, tenant acme", "OpenRow", "public.orders", true, "done"));
    }

    /// <summary>The front page of two modules: one tile each, in registration order, each linking to its module's home. It redirects for one module and draws no tile.</summary>
    [Fact]
    public async Task The_front_page_has_one_tile_per_module_in_registration_order_and_does_not_redirect()
    {
        await using StudioComponentContext context = new(SecondModule.AddTo);

        IRenderedComponent<Front> page = context.Render<Front>();

        page.FindAll(".ms-front-tile-name").Select(static x => x.TextContent.Trim()).Should().Equal("Marten", SecondModule.Name);
        page.FindAll(".ms-front-tile").Select(static x => x.GetAttribute("href")).Should().Equal("marten", SecondModule.PageAddress);
        page.FindAll(".ms-front-tile-summary").Select(static x => x.TextContent.Trim()).Last().Should().Be(SecondModule.SummaryText);
        page.FindAll(".ms-front-tile-summary").Select(static x => x.TextContent.Trim()).First().Should().NotBeEmpty().And.NotBe(SecondModule.SummaryText);
        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Be(context.Services.GetRequiredService<NavigationManager>().BaseUri, "two modules have something to choose, so nothing redirects");
    }

    /// <summary>
    /// The chip with the database module and a module that has an opt-in of its own, both off: each module's group carries its own line, and no line
    /// that sums the chip up names one module's call. Fails when the footnote names the first module's <c>All()</c> for every group (the 1.x line).
    /// </summary>
    [Fact]
    public async Task Each_modules_group_carries_its_own_turn_everything_on_line_and_the_chip_names_no_module_wide_call()
    {
        await using StudioComponentContext context = new(SecondModule.AddTo);

        IRenderedComponent<CapabilityBanner> chip = context.Render<CapabilityBanner>();

        ChipLines lines = ChipLines.Read(chip);
        lines.OwnLines.Should().BeEquivalentTo(
            new Dictionary<string, string>
            {
                ["Marten"] = "MartenCapabilities.All()",
                [SecondModule.Name] = SecondModule.AllOption,
            });
        lines.Chip.Should().NotContain("turns them all on");
        lines.ShellLine.Should().NotContain("All()").And.Contain("DataStudioOptions.ReadOnly");
    }
}
