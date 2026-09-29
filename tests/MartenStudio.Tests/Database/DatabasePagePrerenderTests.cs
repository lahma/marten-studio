using Bunit;

using Marten;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Database;

/// <summary>
/// SEC-fix F5 and F7: a database-browser page reads - and so audits - nothing while it is prerendered, nor in
/// any other static render of it, and a page view whose gate is closed writes no refusal at all unless it asked
/// for a specific gated object, and then exactly one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two renderers.</b> The routes prerender: the framework renders each page statically first, in a DI
/// scope of its own, and then again in the circuit. <see cref="HtmlRenderer" /> is that static rendering - the
/// framework's own, which never calls <c>OnAfterRender</c> - so what a page reads there is what it read while
/// being prerendered. bUnit is the circuit. A read in both was two ring entries and, for a refused link, two
/// Warnings; the static re-render the layout's scope navigation costs made it three on the sample (measured,
/// fifteen 9203s for five page views by one viewer).
/// </para>
/// <para>
/// <b>Why the real services.</b> Which call audits is the service's business, so the closed-gate tests route the
/// pages' reads to a real studio container - capability, policies, the audit ring and a capturing logger - over a
/// store that never connects. Every refusal it can write happens before a connection would be opened, so the
/// counts are exact; a store-schema page's catalog read fails on the unreachable host, which is a value on the
/// page and no refusal.
/// </para>
/// </remarks>
public class DatabasePagePrerenderTests
{
    private const string Spinner = "ms-loading-spinner";

    // ----------------------------------------------------------------------------------------------
    // F5 - nothing is read in a static render, and the circuit reads once
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_rows_tab_reads_nothing_while_prerendered_and_once_in_the_circuit()
    {
        await using StudioComponentContext context = await RowUiData.ContextAsync();
        context.Navigate(RowUiData.TabUrl(FakeTableRows.Schema, FakeTableRows.Table));

        string html = await StaticAsync<TableRowsTab>(context, new Dictionary<string, object?>
        {
            [nameof(TableRowsTab.Schema)] = FakeTableRows.Schema,
            [nameof(TableRowsTab.Name)] = FakeTableRows.Table,
            [nameof(TableRowsTab.Detail)] = FakeDatabaseObjects.Detail(FakeTableRows.Schema, FakeTableRows.Table),
        });

        html.Should().Contain(Spinner, "the prerendered tab is the loading state");
        context.TableRows.Reads.Should().Be(0, "a read here would be audited in the prerender's scope and again in the circuit's");

        var tab = RowUiData.RenderTab(context);

        tab.WaitForAssertion(() => tab.FindAll("tbody tr").Should().NotBeEmpty());
        context.TableRows.Requests.Should().ContainSingle("the circuit's first render reads the page, once");
    }

    [Fact]
    public async Task Row_detail_reads_nothing_while_prerendered_and_once_in_the_circuit()
    {
        await using StudioComponentContext context = await RowUiData.ContextAsync();
        context.Navigate("marten/database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QuartzScheduler&key.trigger_name=trigger-00&key.trigger_group=DEFAULT");

        string html = await StaticAsync<RowDetail>(context);

        html.Should().Contain(Spinner, "a pasted row link is prerendered as the loading state - not as 'no row asked for'");
        context.TableRows.Reads.Should().Be(0);

        var page = context.Render<RowDetail>();

        page.WaitForAssertion(() => page.FindAll(".ms-row-detail-header").Should().NotBeEmpty());
        context.TableRows.RowsAsked.Should().ContainSingle("opening a row is audited, and it was opened once");
    }

    [Fact]
    public async Task The_object_page_and_the_browser_read_nothing_while_prerendered()
    {
        using StudioComponentContext context = DatabasePageData.Context();
        await context.ReadyAsync();

        context.Navigate("marten/database/object?schema=quartz&name=qrtz_triggers&tab=columns");
        (await StaticAsync<ObjectDetail>(context)).Should().Contain(Spinner);

        context.Navigate("marten/database?schema=quartz");
        (await StaticAsync<DatabaseObjects>(context)).Should().Contain(Spinner);

        context.DatabaseObjects.Reads.Should().Be(0, "neither page asks the service anything until the circuit renders it");

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "columns");

        page.FindAll(".ms-db-object-header").Should().ContainSingle();
        context.DatabaseObjects.ObjectsAsked.Should().ContainSingle("the circuit reads the object once");
    }

    // ----------------------------------------------------------------------------------------------
    // F7 - a closed gate writes nothing for page use, and one refusal for one explicit request
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A viewer - store policy yes, write policy no - on the browser and on a store-schema table's page: every
    /// read the pages make to learn what they may show is structure, which needs no capability, and nothing is
    /// audited or logged as refused, in the prerender or in the circuit.
    /// </summary>
    [Fact]
    public async Task A_closed_gate_page_view_writes_no_refusal()
    {
        await using RealStudio real = RealStudio.Viewer();
        using StudioComponentContext context = real.Context();
        await context.ReadyAsync();

        context.Navigate("marten/database");
        await real.PrerenderAsync<DatabaseObjects>(context);
        real.Circuit();
        var browser = context.Render<DatabaseObjects>();
        browser.WaitForAssertion(() => browser.FindAll(".ms-db-grid, .ms-empty, .ms-error-alert, .ms-db-refusal").Should().NotBeEmpty());

        context.Navigate("marten/database/object?schema=public&name=host_settings");
        await real.PrerenderAsync<ObjectDetail>(context);
        real.Circuit();
        var detail = context.Render<ObjectDetail>();
        detail.WaitForAssertion(() => detail.FindAll(".ms-db-object-header, .ms-empty, .ms-error-alert").Should().NotBeEmpty());

        real.Refusals.Should().BeEmpty("using a page whose gate is closed is not a refusal of anything");
        real.Ring.GetLatest().Should().BeEmpty();
        real.Asked.Should().BePositive("anti-vacuity: the pages did read through the real service");
    }

    /// <summary>
    /// A pasted link to an object in a schema the gate refuses is a real refusal: one ring entry and one 9203
    /// for the page view - prerender and circuit together - where it was three.
    /// </summary>
    [Fact]
    public async Task A_link_to_a_withheld_object_is_refused_once_per_page_view()
    {
        await using RealStudio real = RealStudio.Viewer();
        using StudioComponentContext context = real.Context();
        await context.ReadyAsync();

        context.Navigate("marten/database/object?schema=quartz&name=qrtz_triggers&tab=rows");
        await real.PrerenderAsync<ObjectDetail>(context);
        real.Circuit();
        var page = context.Render<ObjectDetail>();
        page.WaitForAssertion(() => page.FindAll(".ms-empty").Should().NotBeEmpty());

        real.Refusals.Should().ContainSingle().Which.EventId.Id.Should().Be(9203);
        real.Ring.GetLatest().Should().ContainSingle().Which.Target.Should().Be("quartz.qrtz_triggers");
    }

    /// <summary>
    /// The browser at <c>?schema=</c> a schema the gate refuses: the grid's list is refused once, and the rail's
    /// bands - the same schema asked again - are not asked at all, where they were a second refusal of the
    /// same page view.
    /// </summary>
    [Fact]
    public async Task A_link_to_a_withheld_schema_is_refused_once_and_the_rail_does_not_ask_again()
    {
        await using RealStudio real = RealStudio.Viewer(listsOnly: true);
        using StudioComponentContext context = real.Context();
        await context.ReadyAsync();

        context.Navigate("marten/database?schema=quartz&kind=views");
        await real.PrerenderAsync<DatabaseObjects>(context);
        real.Circuit();
        var page = context.Render<DatabaseObjects>();
        page.WaitForAssertion(() => real.Asked.Should().BePositive());

        real.Refusals.Should().ContainSingle().Which.EventId.Id.Should().Be(9203);
        real.Ring.GetLatest().Should().ContainSingle();
    }

    /// <summary>The same under <c>ReadOnly</c>, whose refusal is the capability's: one 9202 for one page view.</summary>
    [Fact]
    public async Task Under_ReadOnly_a_link_to_a_browsable_object_is_refused_once_per_page_view()
    {
        await using RealStudio real = RealStudio.Create(
            static options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.ReadOnly = true;
                options.BrowsableSchemas.Add("quartz");
            },
            static _ => true);

        using StudioComponentContext context = real.Context();
        await context.ReadyAsync();

        context.Navigate("marten/database/object?schema=quartz&name=qrtz_triggers");
        await real.PrerenderAsync<ObjectDetail>(context);
        real.Circuit();
        var page = context.Render<ObjectDetail>();
        page.WaitForAssertion(() => page.FindAll(".ms-empty").Should().NotBeEmpty());

        real.Refusals.Should().ContainSingle().Which.EventId.Id.Should().Be(9202);
    }

    /// <summary>A component rendered the way the framework prerenders it: statically, with no circuit.</summary>
    internal static async Task<string> StaticAsync<TComponent>(
        StudioComponentContext context,
        IDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        ILoggerFactory loggers = context.Services.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(context.Services, loggers);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent output = await renderer.RenderComponentAsync<TComponent>(
                parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters));

            return output.ToHtmlString();
        });
    }

    /// <summary>
    /// A real studio container over a store that never connects, whose object service a bUnit context's pages
    /// call - through a scope of their own for each render, as a prerender and a circuit each have one.
    /// </summary>
    private sealed class RealStudio : IAsyncDisposable
    {
        private const string DummyConnectionString =
            "Host=marten-studio-sec-fix-prerender.invalid;Database=none;Username=none;Password=none";

        private readonly ServiceProvider provider;
        private readonly CapturingLoggerProvider logs;
        private readonly bool listsOnly;
        private IServiceScope scope;
        private int asked;

        private RealStudio(ServiceProvider provider, CapturingLoggerProvider logs, bool listsOnly)
        {
            this.provider = provider;
            this.logs = logs;
            this.listsOnly = listsOnly;
            scope = provider.CreateScope();
        }

        /// <summary>Every refusal the real studio logged: events 9202 and 9203.</summary>
        public IReadOnlyList<CapturedLogEntry> Refusals =>
            [.. logs.Entries.Where(static x => x.EventId.Id is 9202 or 9203)];

        /// <summary>The real studio's audit ring.</summary>
        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        /// <summary>How many reads the pages handed to the real service.</summary>
        public int Asked => Volatile.Read(ref asked);

        /// <summary>
        /// A <c>viewer</c>: the store policy says yes, the write policy - which <c>BrowseDatabase</c> asks - says
        /// no; the capability is on and <c>quartz</c> is browsable, so only the account keeps the gate shut.
        /// </summary>
        /// <param name="listsOnly">
        /// Whether only lists reach the real service, and the overview says the browser is open - what a page
        /// needs to go on and ask the lists at all, over a store with no database behind it.
        /// </param>
        public static RealStudio Viewer(bool listsOnly = false) => Create(
            static options =>
            {
                options.StoreAuthorizationPolicy = "MartenStoreOwner";
                options.WriteAuthorizationPolicy = "MartenStudioWrite";
                options.Capabilities.BrowseDatabase = true;
                options.BrowsableSchemas.Add("quartz");
            },
            static resource => resource.Capability is null,
            listsOnly);

        public static RealStudio Create(
            Action<MartenStudioOptions> configure,
            Func<MartenStoreResource, bool> rule,
            bool listsOnly = false)
        {
            var logs = new CapturingLoggerProvider();
            var users = new TestAuthenticationStateProvider();
            users.SignIn("viewer");

            var policies = new TestStoreAuthorizationService();
            policies.Allow(rule);

            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(logs);
            });
            services.AddMarten(static options => options.Connection(DummyConnectionString));
            services.AddMartenStudio(configure);
            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            return new RealStudio(services.BuildServiceProvider(), logs, listsOnly);
        }

        /// <summary>A bUnit context whose object service is this real one.</summary>
        public StudioComponentContext Context() =>
            new(services => services.AddSingleton<IDatabaseObjectService>(new Routed(this)));

        /// <summary>Renders <typeparamref name="TComponent" /> statically, in a DI scope of its own.</summary>
        public async Task PrerenderAsync<TComponent>(StudioComponentContext context)
            where TComponent : IComponent
        {
            Circuit();
            await StaticAsync<TComponent>(context);
        }

        /// <summary>A new DI scope - the next render's, as the circuit's is not the prerender's.</summary>
        public void Circuit()
        {
            scope.Dispose();
            scope = provider.CreateScope();
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }

        private IDatabaseObjectService Service => scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>();

        private sealed class Routed(RealStudio real) : IDatabaseObjectService
        {
            private readonly FakeDatabaseObjectService open = new();

            public Task<DatabaseBrowserOverview> GetOverviewAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
                real.listsOnly ? open.GetOverviewAsync(scope, cancellationToken) : Real().GetOverviewAsync(scope, cancellationToken);

            public Task<DatabaseObjectList> ListAsync(StudioScope scope, DatabaseObjectQuery query, CancellationToken cancellationToken = default) =>
                Real().ListAsync(scope, query, cancellationToken);

            public Task<DatabaseObjectDetail> GetObjectAsync(StudioScope scope, string schema, string name, CancellationToken cancellationToken = default) =>
                Real().GetObjectAsync(scope, schema, name, cancellationToken);

            public Task<DatabaseObjectDefinition> GetDefinitionAsync(StudioScope scope, DatabaseObjectRef reference, CancellationToken cancellationToken = default) =>
                Real().GetDefinitionAsync(scope, reference, cancellationToken);

            public Task<DatabaseSequenceValue> GetSequenceValueAsync(StudioScope scope, string schema, string name, CancellationToken cancellationToken = default) =>
                Real().GetSequenceValueAsync(scope, schema, name, cancellationToken);

            private IDatabaseObjectService Real()
            {
                Interlocked.Increment(ref real.asked);
                return real.Service;
            }
        }
    }
}
