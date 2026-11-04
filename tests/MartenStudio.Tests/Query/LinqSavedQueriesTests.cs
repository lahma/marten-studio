using Bunit;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Services.Query;

using QueryPage = MartenStudio.Components.Pages.Query.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// 1.2-POLISH: LINQ mode's recent and saved queries. The drawer is the one the other two modes use, keyed by mode so that a LINQ
/// entry - a predicate, an ordering and a target - never reaches the SQL console's editor or the Marten clause box, and neither of
/// those reaches LINQ's. The lists live in the visitor's own browser (<c>martenStudio.prefs</c>), never on the server.
/// </summary>
public class LinqSavedQueriesTests
{
    private const string Store = "default";

    private static StudioComponentContext CreateContext(FakeLinqQueryService linq)
    {
        var context = new StudioComponentContext();
        context.WithStores(Store);
        context.Options.Capabilities.RunLinqQueries = true;

        // The fallback provider, as the Query page's other suites do: the collection is sealed by StudioComponentContext's constructor.
        context.Services.AddFallbackServiceProvider(new Services(new FakeQueryService(), linq));

        return context;
    }

    private sealed class Services(FakeQueryService queries, FakeLinqQueryService linq) : IServiceProvider
    {
        public object? GetService(Type requested) =>
            requested == typeof(IQueryService) || requested == typeof(PostgresStudio.Services.Sources.ISqlConsoleService) ? queries
            : requested == typeof(ILinqQueryService) ? linq
            : null;
    }

    private static string RecentKey(QueryMode mode) => SavedQueryStore.RecentKey(Store, mode);

    private static string SavedKey(QueryMode mode) => SavedQueryStore.SavedKey(Store, mode);

    /// <summary>What the page last wrote under <paramref name="key"/>, or <see langword="null" />.</summary>
    private static IReadOnlyList<SavedQuery>? Written(StudioComponentContext context, string key)
    {
        var writes = context.JSInterop.Invocations["martenStudio.prefs.set"].Where(x => Equals(x.Arguments[0], key)).ToList();

        return writes.Count == 0 ? null : SavedQueryStore.Deserialize((string?)writes[^1].Arguments[1]);
    }

    [Fact]
    public void A_LINQ_run_over_a_document_type_is_kept_under_the_LINQ_key_with_its_ordering()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-where").Should().ContainSingle());

        page.Find("#ms-query-linq-where").Change("Total > 5");
        page.Find("#ms-query-linq-orderby").Change("Total desc");
        page.Find(".ms-query-run").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-query-drawer-recent .ms-query-drawer-pick").Should().ContainSingle());

        IReadOnlyList<SavedQuery> kept = Written(context, RecentKey(QueryMode.Linq))!;
        kept.Should().ContainSingle().Which.Should().Be(new SavedQuery("Total > 5", QueryMode.Linq, "order", "Total > 5", "Total desc"));

        Written(context, RecentKey(QueryMode.Sql)).Should().BeNull("a LINQ entry never lands in the SQL console's list");
        Written(context, RecentKey(QueryMode.Marten)).Should().BeNull("nor in the Marten clause box's");
    }

    [Fact]
    public void A_LINQ_run_over_the_event_log_is_kept_with_its_target_and_its_archived_selector()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-type").Should().ContainSingle());

        page.Find("#ms-query-linq-type").Change(LinqQueryPanel.EventsToken);
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-archived").Should().ContainSingle());
        page.Find("#ms-query-linq-archived").Change(nameof(ArchivedFilter.Include));
        page.Find("#ms-query-linq-where").Change("Version > 1");
        page.Find(".ms-query-run").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-query-drawer-recent .ms-query-drawer-pick").Should().ContainSingle());

        SavedQuery kept = Written(context, RecentKey(QueryMode.Linq))!.Should().ContainSingle().Subject;
        kept.Alias.Should().Be(LinqQueryPanel.EventsToken, "the event log is filed under the target picker's own token, which no document alias can be");
        kept.Archived.Should().Be("include");
        kept.Text.Should().Be("Version > 1");
    }

    [Fact]
    public void Picking_a_recent_entry_puts_the_target_the_predicate_and_the_ordering_back_and_runs_nothing()
    {
        var linq = new FakeLinqQueryService().WithType("order").WithType("customer", "Customer");
        using StudioComponentContext context = CreateContext(linq);

        context.JSInterop.Setup<string?>("martenStudio.prefs.get", RecentKey(QueryMode.Linq)).SetResult(SavedQueryStore.Serialize(
            [new SavedQuery("Name == \"Ada\"", QueryMode.Linq, "customer", "Name == \"Ada\"", "Name")]));

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll(".ms-query-drawer-recent .ms-query-drawer-pick").Should().ContainSingle());

        page.Find(".ms-query-drawer-recent .ms-query-drawer-pick").Click();

        page.WaitForAssertion(() => page.Find("#ms-query-linq-where").GetAttribute("value").Should().Be("Name == \"Ada\""));
        page.Find("#ms-query-linq-orderby").GetAttribute("value").Should().Be("Name");
        page.Find("#ms-query-linq-type").GetAttribute("value").Should().Be("customer");
        linq.Requests.Should().BeEmpty("picking fills the boxes; the visitor runs it");
    }

    [Fact]
    public void Picking_an_event_log_entry_selects_the_event_log_and_its_archived_selector()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        context.JSInterop.Setup<string?>("martenStudio.prefs.get", RecentKey(QueryMode.Linq)).SetResult(SavedQueryStore.Serialize(
            [new SavedQuery("Version > 1", QueryMode.Linq, LinqQueryPanel.EventsToken, "Version > 1", "Sequence", "only")]));

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll(".ms-query-drawer-recent .ms-query-drawer-pick").Should().ContainSingle());

        page.Find(".ms-query-drawer-recent .ms-query-drawer-pick").Click();

        page.WaitForAssertion(() => page.Find("#ms-query-linq-type").GetAttribute("value").Should().Be(LinqQueryPanel.EventsToken));
        page.Find("#ms-query-linq-archived").GetAttribute("value").Should().Be(nameof(ArchivedFilter.Only));
        page.Find("#ms-query-linq-where").GetAttribute("value").Should().Be("Version > 1");
        page.Find("#ms-query-linq-orderby").GetAttribute("value").Should().Be("Sequence");
    }

    [Fact]
    public void An_entry_for_a_document_type_the_store_does_not_register_is_refused_in_the_status_line()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        context.JSInterop.Setup<string?>("martenStudio.prefs.get", RecentKey(QueryMode.Linq)).SetResult(SavedQueryStore.Serialize(
            [new SavedQuery("x", QueryMode.Linq, "gone", "Total > 1", null)]));

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll(".ms-query-drawer-recent .ms-query-drawer-pick").Should().ContainSingle());

        page.Find(".ms-query-drawer-recent .ms-query-drawer-pick").Click();

        page.WaitForAssertion(() => page.Markup.Should().Contain("not one this store registers"));
        page.Find("#ms-query-linq-where").GetAttribute("value").Should().BeNullOrEmpty("nothing was applied");
        page.Find("#ms-query-linq-type").GetAttribute("value").Should().Be("order");
    }

    [Fact]
    public void Save_in_the_LINQ_editor_asks_for_a_name_and_keeps_the_query_under_the_LINQ_saved_key()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-where").Should().ContainSingle());

        page.Find("#ms-query-linq-where").Change("Total > 100");
        page.Find("#ms-query-linq-orderby").Change("Total desc");
        page.FindAll("button").Single(static x => x.TextContent.Trim().StartsWith("Save", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => page.FindAll("#ms-query-save-name").Should().ContainSingle());
        page.Find("#ms-query-save-name").Change("Big orders");
        page.Find(".ms-query-drawer-save .ms-btn-primary").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-query-drawer-saved .ms-query-drawer-pick").Should().ContainSingle());

        Written(context, SavedKey(QueryMode.Linq)).Should().ContainSingle().Which
            .Should().Be(new SavedQuery("Big orders", QueryMode.Linq, "order", "Total > 100", "Total desc"));
        Written(context, SavedKey(QueryMode.Sql)).Should().BeNull();
        Written(context, SavedKey(QueryMode.Marten)).Should().BeNull();
    }

    [Fact]
    public void The_SQL_console_does_not_list_what_LINQ_mode_kept()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        context.JSInterop.Setup<string?>("martenStudio.prefs.get", RecentKey(QueryMode.Linq)).SetResult(SavedQueryStore.Serialize(
            [new SavedQuery("Total > 5", QueryMode.Linq, "order", "Total > 5", null)]));
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", SavedKey(QueryMode.Linq)).SetResult(SavedQueryStore.Serialize(
            [new SavedQuery("Mine", QueryMode.Linq, "order", "Total > 7", null)]));

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll(".ms-query-drawer-pick").Should().HaveCount(2));

        page.Find("#ms-query-mode-sql").Click();

        page.WaitForAssertion(() => context.JSInterop.Invocations["martenStudio.prefs.get"]
            .Select(static x => (string?)x.Arguments[0]).Should().Contain(RecentKey(QueryMode.Sql)));
        page.FindAll(".ms-query-drawer-pick").Should().BeEmpty("the SQL console's lists are its own");

        page.Find("#ms-query-mode-marten").Click();
        page.WaitForAssertion(() => context.JSInterop.Invocations["martenStudio.prefs.get"]
            .Select(static x => (string?)x.Arguments[0]).Should().Contain(RecentKey(QueryMode.Marten)));
        page.FindAll(".ms-query-drawer-pick").Should().BeEmpty("and so are the Marten clause box's");
    }

    [Fact]
    public void Importing_into_the_LINQ_drawer_takes_LINQ_entries_only_and_the_other_drawers_take_none_of_them()
    {
        using StudioComponentContext context = new();

        string export = SavedQueryStore.Serialize(
        [
            new SavedQuery("A LINQ one", QueryMode.Linq, "order", "Total > 5", "Total desc"),
            new SavedQuery("A SQL one", QueryMode.Sql, null, "select 1", null),
        ]);

        IRenderedComponent<SavedQueryDrawer> linq = context.Render<SavedQueryDrawer>(parameters =>
            parameters.Add(x => x.StoreKey, Store).Add(x => x.Mode, "Linq"));

        linq.FindAll("button").Single(static x => x.TextContent.Trim() == "Import").Click();
        linq.Find("#ms-query-import").Change(export);
        linq.FindAll("button").Last(static x => x.TextContent.Trim() == "Import").Click();

        linq.WaitForAssertion(() => linq.FindAll(".ms-query-drawer-saved .ms-query-drawer-pick").Should().ContainSingle()
            .Which.TextContent.Trim().Should().Be("A LINQ one"));

        IRenderedComponent<SavedQueryDrawer> sql = context.Render<SavedQueryDrawer>(parameters =>
            parameters.Add(x => x.StoreKey, Store).Add(x => x.Mode, "Sql"));

        sql.FindAll("button").Single(static x => x.TextContent.Trim() == "Import").Click();
        sql.Find("#ms-query-import").Change(SavedQueryStore.Serialize([new SavedQuery("A LINQ one", QueryMode.Linq, "order", "Total > 5", null)]));
        sql.FindAll("button").Last(static x => x.TextContent.Trim() == "Import").Click();

        sql.Find(".ms-query-drawer-message").TextContent.Should().Contain("not a list of queries");
        sql.FindAll(".ms-query-drawer-saved .ms-query-drawer-pick").Should().BeEmpty("a LINQ entry is not offered to the SQL console");
    }
}
