using Bunit;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Services;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

using QueryPage = MartenStudio.Components.Pages.Query.Query;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// LINQ mode on the Query page, through a fake service: the disabled states name what is missing, refusals land under
/// the character they are about, and a result renders its SQL, its advice and its rows.
/// </summary>
public class LinqQueryPanelTests
{
    private static StudioComponentContext CreateContext(FakeLinqQueryService linq, bool runLinq = true)
    {
        var context = new StudioComponentContext();
        context.WithStores("default");
        context.Options.Capabilities.RunLinqQueries = runLinq;

        // A fallback provider rather than a registration, as the Query page's own tests do: StudioComponentContext
        // seals bUnit's collection in its constructor, and it is shared with every other page suite.
        context.Services.AddFallbackServiceProvider(new Services(new FakeQueryService(), linq));

        return context;
    }

    private sealed class Services(FakeQueryService queries, FakeLinqQueryService linq) : IServiceProvider
    {
        public object? GetService(Type requested) =>
            requested == typeof(IQueryService) ? queries
            : requested == typeof(ILinqQueryService) ? linq
            : null;
    }

    /// <summary>
    /// 1.1-FINAL-POLISH C1, reworded for 1.2: editing the predicate and then pressing Ctrl+Enter from the Order-by box writes one
    /// address, with both values. The edit alone keeps its draft and writes nothing; the Run writes the address once, after both
    /// boxes are bound. (In 1.1 a second push began a second enhanced navigation while the first was in flight and the browser
    /// aborted the first, net::ERR_ABORTED; since 1.2 a push is a client-side history write and cannot abort another.)
    /// </summary>
    [Fact]
    public async Task Editing_the_predicate_and_pressing_Ctrl_Enter_from_Order_by_writes_one_address_with_both_values()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-where").Should().ContainSingle());

        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        List<string> locations = [];
        navigation.LocationChanged += (_, args) => locations.Add(args.Location);

        page.Find("#ms-query-linq-where").Change("Total > 5");
        locations.Should().BeEmpty("an edited box keeps its draft and does not touch the address");

        await page.FindComponent<LinqQueryPanel>().InvokeAsync(() => page.FindComponent<LinqQueryPanel>().Instance.RunFromOrderByAsync("Total desc"));

        locations.Should().ContainSingle("one address per Run, written after both boxes are bound");
        locations[0].Should().Contain("predicate=Total%20%3E%205").And.Contain("orderby=Total%20desc");
        linq.Requests.Should().ContainSingle().Which.OrderBy.Should().Be("Total desc");
    }

    /// <summary>
    /// 1.2-NAV N6a: a push writes the scope from the state, not from whatever the address held. The rail's links used to carry no
    /// <c>?store=&amp;db=</c>, and a push built from such an address wrote one without it and every later push copied the gap forward.
    /// </summary>
    [Fact]
    public async Task A_push_writes_the_scope_even_when_the_address_had_none()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);
        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        context.CurrentUri.Should().NotContain("store=", "the premise: the address names no scope");

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-where").Should().ContainSingle());

        await page.FindComponent<LinqQueryPanel>().InvokeAsync(() => page.FindComponent<LinqQueryPanel>().Instance.RunFromOrderByAsync("Total desc"));

        context.CurrentUri.Should().Contain("orderby=Total%20desc")
            .And.Contain("store=" + context.State.ActiveScope!.StoreKey)
            .And.Contain("db=" + Uri.EscapeDataString(context.State.ActiveScope!.DatabaseId!));
    }

    /// <summary>
    /// 1.2-NAV: a page that has been disposed writes no address. A Run that reaches the page after the visitor has left it (the panel
    /// outlives the await) must not put this page's mode and predicate onto the address of the page that replaced it.
    /// </summary>
    [Fact]
    public async Task A_page_that_is_disposed_writes_no_address()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        using StudioComponentContext context = CreateContext(linq);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-where").Should().ContainSingle());

        NavigationManager navigation = context.Services.GetRequiredService<NavigationManager>();
        List<string> locations = [];
        navigation.LocationChanged += (_, args) => locations.Add(args.Location);

        LinqQueryPanel panel = page.FindComponent<LinqQueryPanel>().Instance;
        await context.DisposeComponentsAsync();

        await panel.RunFromOrderByAsync("Total desc");

        locations.Should().BeEmpty("a page that is gone pushes nothing onto the address of the page that replaced it");
    }

    [Fact]
    public async Task A_draft_survives_leaving_LINQ_mode_without_a_navigation()
    {
        using StudioComponentContext context = CreateContext(new FakeLinqQueryService().WithType("order"));

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-where").Should().ContainSingle());
        page.Find("#ms-query-linq-where").Change("Total > 5");
        await page.Find("#ms-query-linq-orderby").ChangeAsync(new ChangeEventArgs { Value = "Total desc" });

        page.Find("#ms-query-mode-marten").Click();
        page.Find("#ms-query-mode-linq").Click();
        page.WaitForAssertion(() => page.FindAll("#ms-query-linq-where").Should().ContainSingle());

        page.Find("#ms-query-linq-where").GetAttribute("value").Should().Be("Total > 5");
        page.Find("#ms-query-linq-orderby").GetAttribute("value").Should().Be("Total desc");
    }

    [Fact]
    public void The_Query_page_has_a_third_mode_and_switching_to_it_shows_the_panel()
    {
        using StudioComponentContext context = CreateContext(new FakeLinqQueryService().WithType("order"));

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();

        page.Find("#ms-query-mode-linq").GetAttribute("aria-selected").Should().Be("true");
        page.Find(".ms-query-panel-linq").Should().NotBeNull();
        page.FindAll(".ms-query-side .ms-query-drawer").Should().ContainSingle("LINQ mode has a saved-and-recent drawer of its own since 1.2");
    }

    [Fact]
    public void A_capability_that_is_off_names_RunLinqQueries()
    {
        var linq = new FakeLinqQueryService
        {
            Status = new LinqModeStatus(LinqAvailability.CapabilityOff, "Set MartenStudioOptions.Capabilities.RunLinqQueries to true.", []),
        };

        using StudioComponentContext context = CreateContext(linq, runLinq: false);

        var panel = context.Render<LinqQueryPanel>();

        panel.Find(".ms-capability-disabled").TextContent.Should().Contain("RunLinqQueries");
        panel.FindAll("#ms-query-linq-where").Should().BeEmpty();
    }

    [Fact]
    public void A_JasperFx_the_studio_does_not_recognise_says_so()
    {
        var linq = new FakeLinqQueryService
        {
            Status = new LinqModeStatus(LinqAvailability.Unrecognised,
                "LINQ mode is not available: the JasperFx DynamicQuery or System.Linq.Dynamic.Core this host loaded (JasperFx 2.98.0) is not one the studio recognises.", []),
        };

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();

        panel.Find(".ms-query-linq-unavailable").TextContent.Should().Contain("not one the studio recognises").And.Contain("JasperFx 2.98.0");
        panel.FindAll("#ms-query-linq-where").Should().BeEmpty();
    }

    [Fact]
    public void A_refusal_is_shown_under_the_character_it_is_about()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Result = LinqQueryResult.Refused(
            "order",
            new LinqRefusal(LinqStage.Screen, LinqClause.Where, "The predicate names 'System', which is not a member.", 8),
            0,
            50);

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find("#ms-query-linq-where").Change("Name == System.IO.File.ReadAllText(\"x\")");
        panel.Find(".ms-query-run").Click();

        panel.Find(".ms-query-error-message").TextContent.Should().Contain("System");
        panel.Find(".ms-query-caret").TextContent.Should().Contain("        ^", "the caret sits under the S of System");
        linq.Requests.Should().ContainSingle().Which.Where.Should().Be("Name == System.IO.File.ReadAllText(\"x\")");
    }

    [Fact]
    public void An_ordering_refusal_lands_under_the_ordering_box()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Result = LinqQueryResult.Refused(
            "order", new LinqRefusal(LinqStage.Screen, LinqClause.OrderBy, "The ordering names 'Nope'.", 0), 0, 50);

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find("#ms-query-linq-orderby").Change("Nope");
        panel.Find(".ms-query-run").Click();

        panel.Find(".ms-query-linq-order-error").TextContent.Should().Contain("Nope");
        linq.Requests[0].OrderBy.Should().Be("Nope");
    }

    [Fact]
    public void A_result_renders_its_rows_its_sql_and_the_index_advice_with_the_line_that_declares_it()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Result = new LinqQueryResult(
            "order",
            [
                new LinqQueryRow("42", """{"Name":"Ada"}""", "acme"),
                new LinqQueryRow("42", """{"Name":"Ada"}""", "globex"),
                new LinqQueryRow("43", null, NotInlined: "Not shown: it is larger than MartenStudioOptions.MaxInlineDocumentBytes."),
            ],
            HasMore: true,
            Offset: 0,
            PageSize: 3,
            Sql: "select d.id, d.data from public.mt_doc_order as d where d.data ->> 'Name' = :p0",
            Parameters: [new LinqParameterInfo(":p0", "Varchar")],
            Duration: TimeSpan.FromMilliseconds(3),
            Advice: new LinqIndexAdvice(
                IndexVerdictLevel.Red,
                [new LinqIndexVerdict("Customer.Name", new IndexVerdict(IndexVerdictLevel.Red,
                    "Customer.Name is only in the JSON, so every document is read.",
                    "options.Schema.For<Order>().Duplicate(x => x.Customer.Name);"))],
                [new LinqPlanScan("Seq Scan", "mt_doc_order", null)],
                "Postgres plans to read the whole table, as the advice says it must."),
            Scope: new MartenQueryScope(null, true, DeletedFilter.Exclude, false));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find(".ms-query-run").Click();

        panel.FindAll(".ms-query-linq-rows tbody tr").Should().HaveCount(3, "two tenants' rows with one id render side by side - keyed by position");
        panel.Find(".ms-query-sql").TextContent.Should().Contain("mt_doc_order");
        panel.Find(".ms-query-sql-note").TextContent.Should().Contain(":p0 (Varchar)").And.NotContain("Ada");
        panel.Find(".ms-query-linq-suggestion").TextContent.Should().Contain("Duplicate(x => x.Customer.Name)");
        panel.Find(".ms-query-linq-scans").TextContent.Should().Contain("Seq Scan on mt_doc_order");
        panel.Find(".ms-query-linq-not-inlined").TextContent.Should().Contain("MaxInlineDocumentBytes");
        panel.FindAll(".ms-query-scope-chip").Select(static x => x.TextContent.Trim()).Should().Contain("all tenants");
        panel.Find(".ms-query-linq-range").TextContent.Should().Contain("more follow");
    }

    [Fact]
    public async Task The_advices_Marten_index_is_offered_under_the_plan_and_a_what_if_asks_with_the_query()
    {
        PostgresStudio.Services.Query.HypotheticalIndex marten = new()
        {
            Key = "marten:Customer.Name",
            Definition = "CREATE INDEX mt_doc_order_idx_customer_name ON public.mt_doc_order USING btree ((data -> 'Customer' ->> 'Name'));",
            Caption = "The index Marten would create for options.Schema.For<Order>().Duplicate(x => x.Customer.Name);",
            Origin = PostgresStudio.Services.Query.HypotheticalIndexOrigin.Marten,
            Table = "mt_doc_order",
        };

        var linq = new FakeLinqQueryService().WithType("order");
        linq.Result = new LinqQueryResult(
            "order", [], HasMore: false, Offset: 0, PageSize: 3,
            Sql: "select d.id, d.data from public.mt_doc_order as d where d.data -> 'Customer' ->> 'Name' = :p0",
            Parameters: [new LinqParameterInfo(":p0", "Varchar")],
            Duration: TimeSpan.FromMilliseconds(3),
            Plan: new ExplainResult(null, "[{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"mt_doc_order\",\"Alias\":\"d\",\"Total Cost\":10}}]", null, null),
            Advice: new LinqIndexAdvice(IndexVerdictLevel.Red, [], []) { WhatIf = [marten] });

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.InvokeAsync(() => panel.Find(".ms-query-run").Click());
        panel.Find("#ms-query-tab-plan").Click();

        panel.Find(".ms-whatif-caption").TextContent.Should().Be(marten.Caption);
        panel.Find(".ms-whatif-definition").TextContent.Should().Be(marten.Definition);
        panel.Find(".ms-whatif-note").TextContent.Should().Contain("as Marten writes it");

        await panel.InvokeAsync(() => panel.Find(".ms-whatif-run").Click());

        panel.WaitForAssertion(() => panel.Find(".ms-whatif-reason").TextContent.Should().StartWith("hypopg is not installed"));
        linq.WhatIfs.Should().ContainSingle().Which.Should().Match<(LinqQueryRequest Request, PostgresStudio.Services.Query.HypotheticalIndex Index)>(
            x => x.Request.Alias == "order" && x.Request.Offset == 0 && ReferenceEquals(x.Index, marten));
    }

    /// <summary>
    /// Both clicks are awaited. bUnit's <c>Click()</c> discards the dispatch task, and the dispatch runs inline only
    /// while the renderer's dispatcher is idle - so a click that lands while the dispatcher is busy is queued, and
    /// <c>Click()</c> returns before its handler has run. The panel's own "Running… N ms" ticker makes it busy: the
    /// first run starts a 200 ms <see cref="PeriodicTimer" /> whose one tick, after the run has ended, re-renders the
    /// panel on the dispatcher. On a loaded machine the second click can land inside that render, and the request it
    /// makes then arrives after the assertion - the failure seen once in a full fast run.
    /// </summary>
    [Fact]
    public async Task Next_asks_for_the_following_page_and_keeps_the_first_pages_plan()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Result = new LinqQueryResult("order", [new LinqQueryRow("1", "{}")], true, 0, 1, "select 1", [], TimeSpan.Zero,
            Plan: new ExplainResult(null, "[{\"Plan\":{}}]", null, null));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        linq.Result = linq.Result with { Offset = 1, Plan = null };
        await RenderRace.ClickCurrentAsync(() => panel.FindAll(".ms-query-linq-pager button")[1]);

        linq.Requests.Should().HaveCount(2);
        linq.Requests[1].Offset.Should().Be(1);
        linq.Requests[1].IncludePlan.Should().BeFalse("the plan is the query's, fetched with its first page");
    }

    [Fact]
    public void A_refusal_the_service_throws_is_rendered_and_names_the_capability()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Failure = Support.MartenSpine.Denied(StudioCapability.RunLinqQueries, CapabilityDenialReason.Disabled);

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find(".ms-query-run").Click();

        panel.Find(".ms-query-linq-error").TextContent.Should().Contain("RunLinqQueries");
    }

    /// <summary>An exception that escaped the service is shown by its type, never by its words - the service's own rule.</summary>
    [Fact]
    public void An_unexpected_exception_is_shown_by_its_type_and_never_by_its_message()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Failure = new InvalidOperationException("Host=db.internal.example;Password=hunter2 refused the type LinqSecretType");

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find(".ms-query-run").Click();

        string shown = panel.Find(".ms-query-linq-error").TextContent;

        shown.Should().Contain(nameof(InvalidOperationException));
        shown.Should().NotContain("hunter2").And.NotContain("db.internal.example").And.NotContain("LinqSecretType");
        panel.Markup.Should().NotContain("hunter2");
    }
}
