using Bunit;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Services;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;

using QueryPage = MartenStudio.Components.Pages.Query.Query;

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

    [Fact]
    public void The_Query_page_has_a_third_mode_and_switching_to_it_shows_the_panel()
    {
        using StudioComponentContext context = CreateContext(new FakeLinqQueryService().WithType("order"));

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-linq").Click();

        page.Find("#ms-query-mode-linq").GetAttribute("aria-selected").Should().Be("true");
        page.Find(".ms-query-panel-linq").Should().NotBeNull();
        page.FindAll(".ms-query-side").Should().BeEmpty("saved queries do not cover LINQ mode yet, so the drawer is not offered");
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
    public void An_older_Marten_says_which_Marten_the_mode_needs()
    {
        var linq = new FakeLinqQueryService
        {
            Status = new LinqModeStatus(LinqAvailability.MartenTooOld,
                "LINQ mode needs Marten 9.48+; this host runs Marten 9.39.0 (JasperFx 2.74.0).", []),
        };

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();

        panel.Find(".ms-query-linq-unavailable").TextContent.Should().Contain("Marten 9.48+").And.Contain("this host runs Marten 9.39.0");
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
    public void Next_asks_for_the_following_page_and_keeps_the_first_pages_plan()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Result = new LinqQueryResult("order", [new LinqQueryRow("1", "{}")], true, 0, 1, "select 1", [], TimeSpan.Zero,
            Plan: new ExplainResult(null, "[{\"Plan\":{}}]", null, null));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find(".ms-query-run").Click();

        linq.Result = linq.Result with { Offset = 1, Plan = null };
        panel.FindAll(".ms-query-linq-pager button")[1].Click();

        linq.Requests.Should().HaveCount(2);
        linq.Requests[1].Offset.Should().Be(1);
        linq.Requests[1].IncludePlan.Should().BeFalse("the plan is the query's, fetched with its first page");
    }

    [Fact]
    public void A_refusal_the_service_throws_is_rendered_and_names_the_capability()
    {
        var linq = new FakeLinqQueryService().WithType("order");
        linq.Failure = new StudioCapabilityDeniedException(StudioCapability.RunLinqQueries, CapabilityDenialReason.Disabled);

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
