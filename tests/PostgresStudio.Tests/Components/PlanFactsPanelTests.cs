using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The Facts panel beside a plan (0.10-A, issue #28) as the visitor meets it: each relation's facts, the withheld ones named only
/// <c>‹withheld›</c>, the values' own gate, every data table in a labelled scroll region (D25), the node table's Facts column and
/// the selected node's detail, the four insights that read them - and the page, which reads them once per run, never for a pasted plan.
/// </summary>
public class PlanFactsPanelTests
{
    /// <summary>
    /// How long a wait on a condition may take: the paste and the render go through the circuit's dispatcher, and bUnit's
    /// one-second default failed once on a loaded machine. A ceiling, never a delay.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task<PostgresStudioComponentContext> ContextAsync() => await new PostgresStudioComponentContext().WithAllCapabilities().ReadyAsync();

    private static PlanRelationFacts Orders() => new("public", "orders", PlanRelationState.Read)
    {
        Kind = "r",
        EstimatedRows = 1_000,
        Pages = 40,
        AllVisiblePages = 20,
        LastAnalyze = Now.AddDays(-30),
        ModifiedSinceAnalyze = 200_000,
        DeadTuples = 12,
        Indexes = [new PlanIndexFact("orders_pkey", ["id"], true, true, true, false, false, "btree", 16_384, 77, Now.AddHours(-2))],
        ExtendedStatistics = [new PlanExtendedStatistic("orders_stats", ["status", "tier"], ["d", "f"])],
        Columns = [new PlanColumnFact("status", "text", true, 0.02, 4, 0.31), new PlanColumnFact("tier", "text", false, null, null, null)],
        Values = PlanValuesState.NeedsBrowse,
    };

    private static PlanFacts Facts(params PlanRelationFacts[] relations) => new(Now, relations, [new PlanSettingFact("work_mem", "4096", "kB", "default", FromPlan: false)]);

    private static string Normal(string text) => string.Join(' ', text.Split((char[]) [' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static IRenderedComponent<PlanFactsPanel> Panel(PostgresStudioComponentContext context, PlanFacts? facts) =>
        context.Render<PlanFactsPanel>(parameters => parameters.Add(x => x.Facts, facts));

    [Fact]
    public async Task A_relation_shows_its_estimate_its_analyze_state_its_indexes_its_extended_statistics_and_its_columns()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, Facts(Orders()));

        panel.Find(".ms-plan-dbfact-summary").TextContent.Should().Be("public.orders: ~1,000 rows, analyzed 30 days ago, 200,000 changed since (20000%)");
        string text = panel.Find(".ms-plan-dbfact-list").TextContent;
        text.Should().Contain("table").And.Contain("40").And.Contain("20 (50%)").And.Contain("2026-10-02 12:00 UTC").And.Contain("200,000").And.Contain("12");

        panel.FindAll(".ms-plan-dbfact-table").Should().HaveCount(2, "the indexes and the columns");
        panel.FindAll(".ms-plan-dbfact-table").Select(static x => x.TextContent).First().Should().Contain("orders_pkey").And.Contain("(id)").And.Contain("btree").And.Contain("primary key");
        panel.Find(".ms-plan-dbfact-extended").TextContent.Should().Contain("orders_stats").And.Contain("status, tier").And.Contain("n-distinct, dependencies");

        IElement columns = panel.FindAll(".ms-plan-dbfact-table")[1];
        columns.TextContent.Should().Contain("status").And.Contain("0.31").And.Contain("not analyzed: no statistics yet");
    }

    [Fact]
    public async Task Values_need_BrowseDatabase_and_are_not_shown_without_it_and_a_refusal_says_why()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanFactsPanel> without = Panel(context, Facts(Orders()));
        without.Find(".ms-plan-dbfact-values-note").TextContent.Should().Contain("need BrowseDatabase");
        without.Markup.Should().NotContain("Most common values</th>");

        IRenderedComponent<PlanFactsPanel> refused = Panel(context, Facts(Orders() with { Values = PlanValuesState.Refused, ValuesNote = "Marten's document tables are never read raw." }));
        refused.Find(".ms-plan-dbfact-values-note").TextContent.Should().Contain("Marten's document tables are never read raw.");
    }

    [Fact]
    public async Task Values_that_were_read_are_shown_and_encoded()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        PlanRelationFacts orders = Orders() with
        {
            Values = PlanValuesState.Read,
            Columns = [new PlanColumnFact("status", "text", true, 0.02, 4, 0.31) { Values = new PlanColumnValues("{open,<b>closed</b>}", "{0.6,0.4}", "{a,z}") }],
        };

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, Facts(orders));

        panel.Markup.Should().Contain("Most common values</th>").And.Contain("Histogram bounds</th>");
        panel.Find(".ms-plan-dbfact-values").TextContent.Should().Be("{open,<b>closed</b>}");
        panel.FindAll(".ms-plan-dbfact-values b").Should().BeEmpty("data from the database is text, never markup");
    }

    [Fact]
    public async Task A_withheld_relation_is_named_withheld_and_nothing_about_it_is_shown_or_counted()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, Facts(new PlanRelationFacts("hr", "salaries", PlanRelationState.Withheld)));

        string markup = panel.Markup;
        panel.Find(".ms-plan-dbfact-summary").TextContent.Should().StartWith("‹withheld›");
        markup.Should().NotContain("hr.salaries").And.NotContain("salaries");
        panel.FindAll(".ms-plan-dbfact-relation .ms-plan-dbfact-list, .ms-plan-dbfact-table, .ms-plan-dbfact-extended").Should().BeEmpty("a withheld relation has no facts");
    }

    [Fact]
    public async Task Relations_not_read_and_scans_without_a_schema_are_said_and_a_failure_is_a_notice()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, Facts(Orders()) with { NotRead = 3, Unqualified = 2, Failure = "The facts could not be read (SQLSTATE 57014)." });

        Normal(panel.Find(".ms-plan-dbfact-not-read").TextContent).Should().Contain("3 more relations were not read").And.Contain("at most 50");
        Normal(panel.Find(".ms-plan-dbfact-unqualified").TextContent).Should().Contain("2 scans name their relations without a schema").And.Contain("VERBOSE");
        panel.Find(".ms-plan-dbfacts-failure").TextContent.Should().Contain("SQLSTATE 57014");

        IRenderedComponent<PlanFactsPanel> none = Panel(context, Facts() with { Unqualified = 1 });
        none.Find(".ms-plan-dbfacts-none").TextContent.Should().Contain("Plan it again with VERBOSE");
    }

    [Fact]
    public async Task The_plans_own_settings_come_first_and_say_where_each_value_is_from()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        PlanFacts facts = new(Now, [], [new PlanSettingFact("random_page_cost", "1.1", null, null, FromPlan: true), new PlanSettingFact("work_mem", "4096", "kB", "default", FromPlan: false)]);

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, facts);

        IReadOnlyList<IElement> rows = panel.FindAll(".ms-plan-dbfact-settings .ms-plan-detail");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("random_page_cost").And.Contain("printed by the plan");
        rows[1].TextContent.Should().Contain("work_mem").And.Contain("4096 kB").And.Contain("set by default");
    }

    [Fact]
    public async Task Every_table_the_panel_draws_is_in_a_labelled_scroll_region()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, Facts(Orders()));

        panel.FindAll("table").Should().NotBeEmpty();
        panel.FindAll("table").Should().OnlyContain(static x => x.ParentElement!.ClassList.Contains("ms-table-scroll") && x.ParentElement.GetAttribute("role") == "region"
            && x.ParentElement.GetAttribute("aria-label")!.Contains("public.orders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_facts_the_panel_draws_nothing()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        Panel(context, null).Markup.Trim().Should().BeEmpty();
    }

    // ---- In the view ---------------------------------------------------------------------------------------------------------

    private static IRenderedComponent<PlanView> View(PostgresStudioComponentContext context, string json, PlanFacts? facts, bool analyzer = true) =>
        context.Render<PlanView>(parameters => parameters.Add(x => x.Json, json).Add(x => x.Analyzer, analyzer).Add(x => x.PlanFactsValue, facts));

    [Fact]
    public async Task The_view_judges_P15_with_the_facts_and_draws_the_panel_and_lists_what_it_cannot_judge_without_them()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        string json = PlanFixtures.Get("p15-fires");

        IRenderedComponent<PlanView> with = View(context, json, Facts(Orders()));

        with.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Contain("P15");
        with.FindAll(".ms-plan-dbfacts").Should().ContainSingle();
        with.FindAll(".ms-plan-not-judged-facts").Should().BeEmpty();

        IRenderedComponent<PlanView> without = View(context, json, null);

        without.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().NotContain("P15");
        without.FindAll(".ms-plan-dbfacts").Should().BeEmpty();
        without.Find(".ms-plan-not-judged-facts").TextContent.Should().Contain("P15 Stale statistics behind a misestimate").And.Contain("P18 No partition pruning").And.Contain("only for a plan this page ran");
    }

    [Fact]
    public async Task The_console_style_view_without_the_analyzer_shows_no_facts_and_names_no_facts_insight_as_unjudged()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = View(context, PlanFixtures.Get("p15-fires"), Facts(Orders()), analyzer: false);

        view.FindAll(".ms-plan-dbfacts, .ms-plan-not-judged-facts").Should().BeEmpty("only the Plan page reads facts");
        view.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().NotContain("P15");
    }

    [Fact]
    public async Task Facts_arriving_for_a_plan_already_drawn_judge_it_again_and_keep_the_selection()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        string json = PlanFixtures.Get("p15-fires");
        IRenderedComponent<PlanView> view = View(context, json, null);

        view.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().NotContain("P15");

        view.Render(parameters => parameters.Add(x => x.Json, json).Add(x => x.Analyzer, true).Add(x => x.PlanFactsValue, Facts(Orders())));

        view.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Contain("P15");
    }

    [Fact]
    public async Task The_node_table_has_a_Facts_column_with_the_scans_relation_and_the_selected_nodes_detail_lists_its_columns()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        PlanRelationFacts addresses = new("public", "addresses", PlanRelationState.Read)
        {
            EstimatedRows = 80_000,
            LastAnalyze = Now.AddDays(-2),
            Columns = [new PlanColumnFact("city", "text", true, 0, 1200, 0.05), new PlanColumnFact("postcode", "text", true, 0, -0.5, 0.01)],
            Values = PlanValuesState.NeedsBrowse,
        };

        IRenderedComponent<PlanView> view = View(context, PlanFixtures.Get("p16-fires"), Facts(addresses));
        view.Find(".ms-plan-view-table").Click();

        view.Find(".ms-plan-table-facts-head").TextContent.Should().Be("Facts");
        view.Find(".ms-plan-table-facts").TextContent.Should().Be("public.addresses: ~80,000 rows, analyzed 2 days ago");

        view.Find(".ms-plan-pick").Click();

        string detail = view.Find(".ms-plan-node-facts").TextContent;
        detail.Should().Contain("city (filter)").And.Contain("distinct 1,200").And.Contain("postcode (filter)").And.Contain("distinct 50% of the rows");
    }

    [Fact]
    public async Task A_withheld_scan_shows_the_withheld_label_in_the_table_and_no_columns_in_the_detail()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = View(context, PlanFixtures.Get("p16-fires"), Facts(new PlanRelationFacts("public", "addresses", PlanRelationState.Withheld)));
        view.Find(".ms-plan-view-table").Click();

        string cell = view.Find(".ms-plan-table-facts").TextContent;
        cell.Should().StartWith("‹withheld›").And.NotContain("addresses");

        view.Find(".ms-plan-pick").Click();
        view.FindAll(".ms-plan-node-facts dt").Select(static x => x.TextContent).Should().NotContain(static x => x.Contains("city", StringComparison.Ordinal));
    }

    // ---- On the page ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_reads_the_facts_once_for_the_plan_it_drew_and_draws_the_panel()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        context.PlanFacts.Answer = _ => Facts(Orders());
        context.SqlConsole.NextPlan = new PlanRunResult
        {
            PlanJson = PlanFixtures.Get("p15-fires"),
            Duration = TimeSpan.FromMilliseconds(12),
            Options = "analyze, buffers, verbose, format json",
            ServerMajor = 17,
        };

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select * from orders where status = 'open'");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        page.WaitForAssertion(() => page.FindAll(".ms-plan-dbfacts").Should().ContainSingle(), Wait);

        context.PlanFacts.Reads.Should().ContainSingle().Which.Plan.Nodes.Should().ContainSingle(static x => x.RelationName == "orders");
        context.PlanFacts.Reads[0].Scope.TenantId.Should().BeNull();
        page.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Contain("P15");
    }

    [Fact]
    public async Task A_pasted_plan_reads_no_facts()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        string text = PlanFixtures.Get("p15-fires");
        JSRuntimeInvocationHandler<int> sending = context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true);
        PlanPaste paste = page.FindComponent<PlanPaste>().Instance;
        page.Find(".ms-plan-paste-draw").Click();
        await page.InvokeAsync(() => paste.BeginPaste());
        await page.InvokeAsync(() => paste.PasteChunk(text));
        sending.SetResult(text.Length);

        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle(), Wait);

        context.PlanFacts.Reads.Should().BeEmpty("facts are read only for plans this page ran");
        page.FindAll(".ms-plan-dbfacts").Should().BeEmpty();
        page.Find(".ms-plan-not-judged-facts").TextContent.Should().Contain("P15");
    }

    [Fact]
    public async Task A_facts_read_that_the_gate_refuses_keeps_the_plan_and_says_why()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        context.PlanFacts.Throws = new StudioNotAuthorizedException(new StudioScope("default", "default", null));
        context.SqlConsole.NextPlan = new PlanRunResult
        {
            PlanJson = PlanFixtures.Get("p15-fires"),
            Duration = TimeSpan.FromMilliseconds(12),
            Options = "analyze, buffers, format json",
            ServerMajor = 17,
        };

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select * from orders");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle(), Wait);

        page.Find(".ms-plan-dbfacts-failure").TextContent.Should().NotBeEmpty();
        page.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().NotContain("P15");
    }

    // ---- A second render: Blazor throws on a duplicate sibling key, and a circuit that throws is over ------------------------

    [Fact]
    public async Task Two_withheld_relations_render_twice_without_a_duplicate_key()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        // Every withheld record is called the same and holds no name: only its position tells two apart.
        PlanFacts Withheld() => Facts(new PlanRelationFacts(string.Empty, string.Empty, PlanRelationState.Withheld), new PlanRelationFacts(string.Empty, string.Empty, PlanRelationState.Withheld), Orders());

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, Withheld());
        panel.FindAll(".ms-plan-dbfact-relation").Should().HaveCount(3);

        panel.Render(parameters => parameters.Add(x => x.Facts, Withheld()));
        panel.Render(parameters => parameters.Add(x => x.Facts, Withheld()));

        panel.FindAll(".ms-plan-dbfact-relation").Should().HaveCount(3);
        panel.FindAll(".ms-plan-dbfact-summary").Select(static x => x.TextContent).Should().StartWith(["‹withheld›: its schema is not one you may see, so nothing is read about it.", "‹withheld›: its schema is not one you may see, so nothing is read about it."]);
    }

    [Fact]
    public async Task Statistics_objects_of_one_name_in_two_schemas_render_twice_without_a_duplicate_key()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        PlanFacts Both() => Facts(Orders() with
        {
            ExtendedStatistics =
            [
                new PlanExtendedStatistic("orders_stats", ["status", "tier"], ["d"]) { Schema = "public" },
                new PlanExtendedStatistic("orders_stats", ["status", "tier"], ["d"]) { Schema = "audit" },
                new PlanExtendedStatistic(PlanFacts.Withheld, ["status"], ["m"]) { Schema = PlanFacts.Withheld },
                new PlanExtendedStatistic(PlanFacts.Withheld, ["tier"], ["m"]) { Schema = PlanFacts.Withheld },
            ],
        });

        IRenderedComponent<PlanFactsPanel> panel = Panel(context, Both());
        panel.Render(parameters => parameters.Add(x => x.Facts, Both()));
        panel.Render(parameters => parameters.Add(x => x.Facts, Both()));

        panel.FindAll(".ms-plan-dbfact-extended li").Should().HaveCount(4);
    }

    [Fact]
    public async Task A_column_a_node_uses_twice_is_listed_once_and_the_view_renders_twice()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        string json = PlanFixtures.Get("p16-fires").Replace(
            "((city = 'Oslo'::text) AND (postcode = '0150'::text))",
            "((city > 'a'::text) AND ((city)::text = 'Oslo'::text))",
            StringComparison.Ordinal);
        PlanRelationFacts addresses = new("public", "addresses", PlanRelationState.Read)
        {
            EstimatedRows = 80_000,
            Columns = [new PlanColumnFact("city", "text", true, 0, 20, 0.05)],
        };

        IRenderedComponent<PlanView> view = View(context, json, Facts(addresses));
        view.Find(".ms-plan-view-table").Click();
        view.Find(".ms-plan-pick").Click();

        view.FindAll(".ms-plan-node-facts dt").Select(static x => x.TextContent).Should().Equal(["Relation", "city (filter)"], "the column is one entry for its text, however often the text names it");

        view.Render(parameters => parameters.Add(x => x.Json, json).Add(x => x.Analyzer, true).Add(x => x.PlanFactsValue, Facts(addresses)));
        view.Render(parameters => parameters.Add(x => x.Json, json).Add(x => x.Analyzer, true).Add(x => x.PlanFactsValue, Facts(addresses)));

        view.FindAll(".ms-plan-node-facts dt").Should().HaveCount(2);
    }

    [Fact]
    public void A_withheld_record_is_still_found_by_the_names_the_plan_printed_though_it_holds_neither()
    {
        PlanFacts facts = new(Now, [new PlanRelationFacts(string.Empty, string.Empty, PlanRelationState.Withheld)], [])
        {
            Requested = new Dictionary<PlanRelationRef, int> { [new PlanRelationRef("secret", "salaries")] = 0 },
        };

        facts.Find("secret", "salaries").Should().NotBeNull().And.Match<PlanRelationFacts>(static x => x.State == PlanRelationState.Withheld);
        facts.Find("secret", "elsewhere").Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(facts).Should().NotContain("salaries").And.NotContain("secret");
    }
}
