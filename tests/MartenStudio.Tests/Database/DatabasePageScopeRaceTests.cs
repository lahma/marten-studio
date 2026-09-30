using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Services;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Database;

/// <summary>
/// POLISH P1: a database-browser page reads nothing until the scope its address names is the studio's, and
/// then reads once. POLISH P7: a stored column set is part of the Rows tab's first read, not a second one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The race.</b> Applying a <c>?store=&amp;db=&amp;tenant=</c> that differs from the active scope is
/// <see cref="StudioState.SetScopeAsync" />, which lists databases and describes tenants - a query on a real
/// catalog - and the framework renders the page, and calls its first <c>OnAfterRenderAsync</c>, while that is
/// awaited. The pages read there, so an in-circuit Back or Forward to an entry of another database read - and
/// audited - against the previous scope first, and again once the scope arrived: a stray "not found" or 9203,
/// and a Rows read that could land on the previous database. <see cref="FakeStudioScopeCatalog.DescribeGate" />
/// holds the scope change half way, the way a slow catalog does, and the test releases it.
/// </para>
/// <para>
/// <b>The second read.</b> On Blazor Server a navigation is a round trip: <see cref="NavigationManager.Uri" />
/// changes when the browser calls back, not when the circuit asks. The Rows tab wrote a stored column set into
/// the address and read the address straight back - unchanged - so its first read went out without the columns
/// and the callback's re-render read the page again with them. <see cref="DeferredNavigationManager" /> is a
/// navigation manager that behaves the way Blazor Server's does.
/// </para>
/// </remarks>
public class DatabasePageScopeRaceTests
{
    private const string Browser = "marten/database?schema=quartz&store=default&db=db-b";

    private const string Row =
        "marten/database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QuartzScheduler&key.trigger_name=trigger-00" +
        "&key.trigger_group=DEFAULT&store=default&db=db-b";

    // ----------------------------------------------------------------------------------------------
    // P1 - a fresh page, whose first interactive render happens while the scope is applied
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_object_page_reads_nothing_until_the_addresss_database_is_applied_and_then_once()
    {
        await using StudioComponentContext context = await TwoDatabasesAsync();
        TaskCompletionSource gate = Hold(context);

        context.Navigate("marten/database/object?schema=quartz&name=qrtz_triggers&tab=columns&store=default&db=db-b");
        var page = context.Render<ObjectDetail>();

        page.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the circuit rendered while the scope was being applied");
        context.DatabaseObjects.Reads.Should().Be(0, "nothing is read against the previous database while the address's is applied");

        gate.SetResult();

        page.WaitForAssertion(() => page.FindAll(".ms-db-object-header").Should().ContainSingle());
        context.DatabaseObjects.ObjectsAsked.Should().ContainSingle("the object is read once, for the address's database");
        context.DatabaseObjects.Scopes.Should().OnlyContain(static x => x.DatabaseId == "db-b");
    }

    [Fact]
    public async Task The_browser_lists_nothing_until_the_addresss_database_is_applied_and_then_once()
    {
        await using StudioComponentContext context = await TwoDatabasesAsync();
        TaskCompletionSource gate = Hold(context);

        context.Navigate(Browser);
        var page = context.Render<DatabaseObjects>();

        context.DatabaseObjects.Reads.Should().Be(0);

        gate.SetResult();

        page.WaitForAssertion(() => page.FindAll(".ms-loading-spinner").Should().BeEmpty());
        context.DatabaseObjects.Queries.Should().ContainSingle("the list is read once");
        context.DatabaseObjects.Scopes.Should().NotBeEmpty().And.OnlyContain(static x => x.DatabaseId == "db-b");
    }

    [Fact]
    public async Task Row_detail_opens_nothing_until_the_addresss_database_is_applied_and_then_once()
    {
        await using StudioComponentContext context = await TwoDatabasesAsync();
        TaskCompletionSource gate = Hold(context);

        context.Navigate(Row);
        var page = context.Render<RowDetail>();

        page.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the key is the address's, and the page waits for the scope with it");
        context.TableRows.Reads.Should().Be(0, "opening a row is audited, and it is not opened against the previous database");

        gate.SetResult();

        page.WaitForAssertion(() => page.FindAll(".ms-row-detail-header").Should().NotBeEmpty());
        context.TableRows.RowsAsked.Should().ContainSingle();
        context.TableRows.Scopes.Should().OnlyContain(static x => x.DatabaseId == "db-b");
    }

    // ----------------------------------------------------------------------------------------------
    // P1 - an open object page, and a Back or Forward to the same table in another database
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The object page is on screen with its Rows tab, and the visitor goes Back to an entry of the same table
    /// in another database, with a filter. The page re-renders while it applies the database - and the tab used
    /// to read then: the object on screen, under the new address's filter, against the previous database.
    /// </summary>
    [Fact]
    public async Task Back_to_another_databases_rows_reads_nothing_against_the_previous_one()
    {
        await using StudioComponentContext context = await TwoDatabasesAsync();

        context.Navigate(RowUiData.TabUrl(FakeTableRows.Schema, FakeTableRows.Table) + "&store=default&db=db-a");
        var page = context.Render<ObjectDetail>();
        page.WaitForAssertion(() => context.TableRows.Requests.Should().ContainSingle());

        context.TableRows.Scopes.Should().OnlyContain(static x => x.DatabaseId == "db-a", "the premise");

        TaskCompletionSource gate = Hold(context);
        context.Navigate(
            RowUiData.TabUrl(FakeTableRows.Schema, FakeTableRows.Table) + "&store=default&db=db-b&q="
            + Uri.EscapeDataString("trigger_group = DEFAULT"));

        context.TableRows.Requests.Should().ContainSingle("nothing is read while the address's database is being applied");

        gate.SetResult();

        page.WaitForAssertion(() => context.TableRows.Requests.Should().HaveCount(2));
        page.WaitForAssertion(() => page.FindAll(".ms-row-grid").Should().NotBeEmpty());

        context.TableRows.Requests.Should().HaveCount(2, "exactly one read after the database arrived");
        context.TableRows.Scopes[1].DatabaseId.Should().Be("db-b");
        context.TableRows.Requests[1].Request.Filter.Should().Be("trigger_group = DEFAULT");
        context.DatabaseObjects.Scopes.Should().Contain(static x => x.DatabaseId == "db-b", "the object was read again for the new database");
    }

    // ----------------------------------------------------------------------------------------------
    // P7 - a stored column set is one read, even while the address bar catches up
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stored_column_set_is_part_of_the_first_read_and_the_address_catching_up_reads_nothing()
    {
        var navigation = new DeferredNavigationManager();
        await using StudioComponentContext context = new(services => services.AddSingleton<NavigationManager>(navigation));
        await context.ReadyAsync();

        context.JSInterop
            .Setup<string?>("martenStudio.prefs.get", "ms_db_cols_default_" + FakeTableRows.Schema + "." + FakeTableRows.Table)
            .SetResult("sched_name,trigger_name");

        context.Navigate(RowUiData.TabUrl(FakeTableRows.Schema, FakeTableRows.Table));
        navigation.Defer = true;

        var tab = context.Render<TableRowsTab>(parameters => parameters
            .Add(x => x.Schema, FakeTableRows.Schema)
            .Add(x => x.Name, FakeTableRows.Table)
            .Add(x => x.Detail, (object?) FakeDatabaseObjects.Detail(FakeTableRows.Schema, FakeTableRows.Table)));

        tab.WaitForAssertion(() => context.TableRows.Requests.Should().ContainSingle());
        context.TableRows.Requests[0].Request.Columns.Should().Equal(["sched_name", "trigger_name"], "the stored set is part of the first read");

        navigation.Pending.Should().ContainSingle("the address bar is asked to say so, with a replacing navigation")
            .Which.Should().Contain("cols=");
        navigation.Uri.Should().NotContain("cols=", "the premise: the browser has not called back yet");

        // The object page re-renders before the browser has confirmed the replacement...
        tab.Render();

        // ... and then the browser confirms it, and the page re-renders again.
        navigation.Confirm();
        tab.Render();

        navigation.Uri.Should().Contain("cols=");
        context.TableRows.Requests.Should().ContainSingle("a stored column set costs no second read");
    }

    // ----------------------------------------------------------------------------------------------

    /// <summary>The default store with two databases, settled on the first - the scope a pasted link moves off.</summary>
    private static async Task<StudioComponentContext> TwoDatabasesAsync()
    {
        StudioComponentContext context = new();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a", "db-b");

        await context.State.EnsureInitializedAsync();
        context.State.ActiveScope!.DatabaseId.Should().Be("db-a", "the premise: the circuit starts on the first database");

        return context;
    }

    /// <summary>Holds the next scope change half way: <see cref="StudioState.SetScopeAsync" /> describes the new database.</summary>
    private static TaskCompletionSource Hold(StudioComponentContext context)
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Catalog.DescribeGate = gate;
        return gate;
    }

    /// <summary>
    /// A navigation manager that behaves the way Blazor Server's does: while <see cref="Defer" /> is on, a
    /// navigation is only asked for, and <see cref="NavigationManager.Uri" /> changes when the "browser" calls
    /// back - <see cref="Confirm" />.
    /// </summary>
    private sealed class DeferredNavigationManager : NavigationManager
    {
        public DeferredNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        /// <summary>Whether a navigation waits for <see cref="Confirm" />.</summary>
        public bool Defer { get; set; }

        /// <summary>The navigations asked for and not yet confirmed.</summary>
        public List<string> Pending { get; } = [];

        /// <summary>The browser calls back: every pending navigation lands, in order.</summary>
        public void Confirm()
        {
            foreach (string uri in Pending)
            {
                Uri = uri;
                NotifyLocationChanged(isInterceptedLink: false);
            }

            Pending.Clear();
        }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            string absolute = ToAbsoluteUri(uri).ToString();

            if (Defer)
            {
                Pending.Add(absolute);
                return;
            }

            Uri = absolute;
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }
}
