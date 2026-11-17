using Bunit;

using DataStudio.Components;
using DataStudio.Components.Layout;
using DataStudio.Components.Pages;
using DataStudio.Marten.Tests.Support;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Tests.Support;
using DataStudio.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>
/// D34, the database module's half, proved on the real <c>DatabaseScopeHost</c>, <c>DatabaseScopeGate</c> and <c>StudioState</c> beside a module this suite
/// defines (<see cref="SecondModule" />, which has a page of its own and no gate): the database's scope waits in memory on another module's page, its policy is
/// not asked there, and the way back asks the gate again before a database page's body is read. The module beside it is a test's, not Kafka: the shell between
/// two modules is what is held here, and the Kafka suite holds the same shell with a test-defined module of its own.
/// </summary>
/// <remarks>
/// Each test names the one-line change to the source that makes it fail, and was seen to fail with it (v2-G).
/// </remarks>
public sealed class DatabaseModuleBesideAnotherModuleTests
{
    private const string Policy = "host-source";

    private const string DatabasePage = "http://localhost/studio/postgres/database/connection";

    private const string SecondPage = "http://localhost/studio/" + SecondModule.PageAddress;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(PostgresComponentContext context, RecordingNavigationManager navigation, IRenderedComponent<StudioRoutes> routes)
        {
            Context = context;
            Navigation = navigation;
            Routes = routes;
        }

        public PostgresComponentContext Context { get; }

        public RecordingNavigationManager Navigation { get; }

        public IRenderedComponent<StudioRoutes> Routes { get; }

        public TestSourceAuthorizationService Policies => Context.AuthorizationService;

        public int Bodies => Context.Connection.Requests.Count;

        /// <summary>Waits until everything queued on the renderer's dispatcher, a render and its after-render among it, has run.</summary>
        public Task SettleAsync() => Routes.InvokeAsync(() => { });

        public void Click(string relative) => Navigation.Simulate(relative, intercepted: true);

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    /// <summary>The studio rendered through <c>StudioRoutes</c> at <paramref name="startAt" />, with the second module composed after the database's and a second data source listed.</summary>
    private static async Task<Rig> StartAsync(string startAt, bool policy = true, bool ready = true, Action<PostgresComponentContext>? beforeRender = null)
    {
        RecordingNavigationManager navigation = new(startAt);

        PostgresComponentContext context = new(services =>
        {
            services.AddSingleton<NavigationManager>(navigation);
            services.AddSingleton<INavigationInterception>(new RecordingNavigationInterception());
            services.AddSingleton<IScrollToLocationHash>(new RecordingScrollToLocationHash());
            SecondModule.AddTo(services);
        });

        context.ShellOptions.RefreshInterval = TimeSpan.FromMinutes(5);
        context.Options.AddDataSource("reporting", _ => context.DataSource);

        if (policy)
        {
            context.Options.SourceAuthorizationPolicy = Policy;
        }

        if (ready)
        {
            await context.ReadyAsync();
        }

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

    /// <summary>The control: the same address on a page of the database does move the scope, so the next test's "nothing moved" is a result.</summary>
    [Fact]
    public async Task An_address_naming_another_scope_on_a_database_page_moves_the_scope_and_a_click_does_too()
    {
        await using Rig rig = await StartAsync(DatabasePage + "?store=reporting&db=reporting");

        rig.Routes.WaitForAssertion(() => rig.Context.State.ActiveScope.Should().Be(new StudioScope("reporting", "reporting", null)));

        rig.Click("studio/postgres/database/connection?store=default&db=default");

        rig.Routes.WaitForAssertion(() => rig.Context.State.ActiveScope.Should().Be(new StudioScope("default", "default", null)));
    }

    /// <summary>
    /// Fails when <c>DatabaseScopeHost.OnInitializedAsync</c> reads the address's scope on a page that is not the database's (the <c>Active ? …</c> on the
    /// read): the scope moves to <c>reporting</c>.
    /// </summary>
    [Fact]
    public async Task An_address_naming_a_database_scope_on_the_other_modules_page_does_not_move_it_on_first_load()
    {
        await using Rig rig = await StartAsync(SecondPage + "?store=reporting&db=reporting");

        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        rig.Routes.WaitForAssertion(() => rig.Context.State.ActiveScope.Should().NotBeNull("the catalog was loaded while the second module's page was on screen"));
        await rig.SettleAsync();

        rig.Context.State.ActiveScope.Should().Be(new StudioScope("default", "default", null), "the database's scope waits in memory");
        rig.Navigation.Requests.Should().BeEmpty("the shell does not write the database's scope into an address it is not about");
        rig.Navigation.Uri.Should().Be(SecondPage + "?store=reporting&db=reporting");
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
        int asked = rig.Policies.Calls.Count;
        int requests = rig.Navigation.Requests.Count;

        rig.Click("studio/" + SecondModule.PageAddress + "?store=reporting&db=reporting");

        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        await rig.SettleAsync();
        await Task.Yield();
        await rig.SettleAsync();

        rig.Context.State.ActiveScope.Should().Be(new StudioScope("default", "default", null));
        rig.Policies.Calls.Should().HaveCount(asked, "no policy is asked about a page that is not about a data source");
        rig.Navigation.Requests.Should().HaveCount(requests, "the address is the other module's and is left as the visitor wrote it");
        rig.Navigation.Uri.Should().Be(SecondPage + "?store=reporting&db=reporting");
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

        rig.Click("studio/" + SecondModule.PageAddress);
        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        rig.Policies.Allow(static _ => false);
        await rig.SettleAsync();
        int asked = rig.Policies.Calls.Count;

        rig.Click("studio/postgres/database/connection");

        rig.Routes.WaitForAssertion(() => rig.Routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        rig.Policies.Calls.Count.Should().BeGreaterThan(asked, "the gate was asked again on the way back");
        rig.Bodies.Should().Be(1, "a scope refused while the visitor was away is never read on return");
        rig.Routes.WaitForAssertion(() => rig.Navigation.Uri.Should().Contain("store=default").And.Contain("db=default", "the address names the scope again"));
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
        rig.Click("studio/" + SecondModule.PageAddress);
        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        await rig.SettleAsync();

        rig.Policies.Allow(_ => admitted);
        rig.Policies.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Click("studio/postgres/database/connection");
        await rig.SettleAsync();

        rig.Routes.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the layout waits for the gate");
        rig.Bodies.Should().Be(1, "the body is not read before the policy has answered");

        rig.Policies.Gate.SetResult();

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

    /// <summary>
    /// Leaving the other module's page for the database's while the catalog is still loading: only the host's own last step gates the page. Fails when
    /// <c>OnParametersSet</c> asks the gate before the catalog has loaded (its <c>initialized</c> guard), which takes "nothing to be about" for a grant and
    /// draws the body at once.
    /// </summary>
    [Fact]
    public async Task A_click_to_a_database_page_during_the_catalog_load_is_gated_once_the_catalog_has_loaded()
    {
        // The catalog asks the policy about every source, so it is pending for as long as the gate is held; the rule is applied when asked.
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Rig rig = await StartAsync(SecondPage, ready: false, beforeRender: context => context.AuthorizationService.Gate = gate);
        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));

        rig.Click("studio/postgres/database/connection");
        await rig.SettleAsync();

        rig.Bodies.Should().Be(0, "the database's body is not read while the gate has no scope to ask about");
        rig.Routes.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the gate shows that it has not answered; it does not take 'no scope yet' for a grant");
        rig.Routes.FindAll(".ms-empty-title").Should().BeEmpty();

        gate.SetResult();

        rig.Routes.WaitForAssertion(() => rig.Bodies.Should().BePositive("the catalog loaded and the gate answered yes"));
        rig.Routes.FindAll(".ms-loading-spinner").Should().BeEmpty();
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
        await using Rig rig = await StartAsync(SecondPage, beforeRender: static context => context.AuthorizationService.Allow(static _ => false));
        await rig.SettleAsync();

        rig.Routes.WaitForAssertion(() => Heading(rig.Routes).Should().Be("Second page"));
        rig.Routes.FindAll(".ms-empty-title, .ms-loading-spinner").Should().BeEmpty("no gate stands in front of the other module's page");
        rig.Routes.FindAll(".ms-scope-selector").Should().BeEmpty("the database's scope selector belongs to the database's pages");
        int asked = rig.Policies.Calls.Count;

        rig.Click("studio/postgres/database/connection");

        rig.Routes.WaitForAssertion(() => rig.Routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        rig.Bodies.Should().Be(0, "the database's body is never drawn without its gate");
        rig.Routes.FindAll(".ms-scope-selector").Should().NotBeEmpty("a database page draws the database's selector");
        rig.Policies.Calls.Count.Should().BeGreaterThan(asked);
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
        await using PostgresComponentContext context = new(SecondModule.AddTo);
        await context.ReadyAsync();
        context.AuthenticationState.SignIn("ops");
        context.Preferences.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
        StudioActionLogService ring = context.Services.GetRequiredService<StudioActionLogService>();
        DateTimeOffset at = new(2026, 11, 14, 9, 30, 15, TimeSpan.Zero);

        ring.Record(new StudioActionLogEntry(at, "ops", new DatabaseAuditScope("default", "main", "acme").ToAuditScope(PostgresDescriptor.Key), "OpenRow", "public.orders", true, "done", null));
        ring.Record(new StudioActionLogEntry(at.AddMinutes(1), "viewer", new DatabaseAuditScope("default", "main", null).ToAuditScope(PostgresDescriptor.Key), "OpenAll", "public.orders", false, "nope", null));

        IRenderedComponent<Activity> page = context.Render<Activity>();

        page.FindAll("tbody tr").Select(static x => x.OuterHtml).Should().Equal(
            Row("2026-11-14 09:31:15 +00:00", "viewer", "default" + Separator + "main" + Separator + "all", "Store default, database main, tenant all", "OpenAll", "public.orders", false, "nope"),
            Row("2026-11-14 09:30:15 +00:00", "ops", "default" + Separator + "main" + Separator + "acme", "Store default, database main, tenant acme", "OpenRow", "public.orders", true, "done"));
    }

    /// <summary>The front page of two modules: one tile each, in registration order, each linking to its module's home. It redirects for one module and draws no tile.</summary>
    [Fact]
    public async Task The_front_page_has_one_tile_per_module_in_registration_order_and_does_not_redirect()
    {
        await using PostgresComponentContext context = new(SecondModule.AddTo);

        IRenderedComponent<Front> page = context.Render<Front>();

        page.FindAll(".ms-front-tile-name").Select(static x => x.TextContent.Trim()).Should().Equal("Postgres", SecondModule.Name);
        page.FindAll(".ms-front-tile").Select(static x => x.GetAttribute("href")).Should().Equal("postgres", SecondModule.PageAddress);
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
        await using PostgresComponentContext context = new(SecondModule.AddTo);

        IRenderedComponent<CapabilityBanner> chip = context.Render<CapabilityBanner>();

        ChipLines lines = ChipLines.Read(chip);
        lines.OwnLines.Should().BeEquivalentTo(
            new Dictionary<string, string>
            {
                ["Postgres"] = "PostgresCapabilities.All()",
                [SecondModule.Name] = SecondModule.AllOption,
            });
        lines.Chip.Should().NotContain("turns them all on");
        lines.ShellLine.Should().NotContain("All()").And.Contain("DataStudioOptions.ReadOnly");
    }
}
