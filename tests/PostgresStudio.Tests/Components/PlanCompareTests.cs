using Bunit;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 0.10-B (issue #28) as the visitor meets it: Keep as Before and the next plan becoming After, the Compare section, clearing Before;
/// the settings what-if; the generic plan against the custom one with P21; the What if? in place; and the statement timeout box.
/// </summary>
public class PlanCompareTests
{
    /// <summary>
    /// How long a wait on a condition may take: the paste and the render go through the circuit's dispatcher, and bUnit's
    /// one-second default failed once on a loaded machine. A ceiling, never a delay.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<PostgresStudioComponentContext> ReadyAsync()
    {
        PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();

        return await context.ReadyAsync();
    }

    private static PlanRunResult Ran(string pair, IReadOnlyList<PlanSettingRow>? offers = null, int placeholders = 0) => new()
    {
        PlanJson = PlanFixtures.GetPair(pair),
        Duration = TimeSpan.FromMilliseconds(3),
        Options = "verbose, format json",
        ServerMajor = 17,
        SettingOffers = offers ?? [],
        TimeoutSeconds = 30,
        Placeholders = placeholders,
    };

    private static readonly PlanSettingRow[] Offers =
    [
        new("work_mem", "4096", "kB", "integer", "64", "2147483647"),
        new("random_page_cost", "4", null, "real", "0", "1.79769e+308"),
        new("enable_seqscan", "on", null, "bool", null, null),
    ];

    private static async Task RunAsync(IRenderedComponent<PlanPage> page, string statement)
    {
        page.Find("#ms-plan-sql").Change(statement);
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
    }

    private static async Task<IRenderedComponent<PlanPage>> FirstAsync(PostgresStudioComponentContext context, string pair, string statement = "select * from orders", IReadOnlyList<PlanSettingRow>? offers = null)
    {
        context.SqlConsole.NextPlan = Ran(pair, offers);

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        await RunAsync(page, statement);
        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle(), Wait);

        return page;
    }

    // ---- Keep as Before ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_on_screen_can_be_kept_as_Before_and_the_next_plan_run_becomes_After_and_opens_Compare()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "seq-before");

        page.FindAll(".ms-plan-compare").Should().BeEmpty("one plan is no comparison");
        page.Find(".ms-plan-keep").Click();
        page.Find(".ms-plan-keep").HasAttribute("disabled").Should().BeTrue("the plan on screen is Before already");
        page.Find(".ms-plan-pin-label").TextContent.Should().Contain("Explain");
        page.FindAll(".ms-plan-compare").Should().BeEmpty("nothing arrived to compare it with");

        context.SqlConsole.NextPlan = Ran("seq-after");
        await RunAsync(page, "select * from orders where id = 1");

        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);
        page.Find(".ms-plan-compare-labels").TextContent.Should().Contain("Before:").And.Contain("After:");
        page.Find(".ms-plan-compare-totals").TextContent.Should().Contain("1,500.00 → 8.44");
        page.FindAll(".ms-plan-diff-changed").Should().ContainSingle().Which.TextContent.Should().Contain("Seq Scan → Index Scan");
        page.Find(".ms-plan-compare-table").ParentElement!.GetAttribute("role").Should().Be("region", "the diff is in a labelled scroll region (D25)");
    }

    [Fact]
    public async Task Clearing_Before_returns_to_the_single_plan_page_and_a_failed_run_keeps_Before()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "seq-before");

        page.Find(".ms-plan-keep").Click();

        context.SqlConsole.NextPlan = new PlanRunResult { Error = new SqlError("42P01", "relation does not exist", 0, null, null), ServerMajor = 17 };
        await RunAsync(page, "select * from nothing");

        page.WaitForAssertion(() => page.Find(".ms-query-error-message").TextContent.Should().Contain("42P01"), Wait);
        page.FindAll(".ms-plan-pin-label").Should().ContainSingle("a run that failed leaves Before where it was");
        page.FindAll(".ms-plan-compare").Should().BeEmpty();

        context.SqlConsole.NextPlan = Ran("seq-after");
        await RunAsync(page, "select * from orders where id = 1");
        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);

        page.Find(".ms-plan-clear-before").Click();

        page.FindAll(".ms-plan-compare, .ms-plan-pin-label, .ms-plan-clear-before").Should().BeEmpty();
        page.FindAll(".ms-plan-tree").Should().ContainSingle("the plan on screen stays");
    }

    /// <summary>
    /// UI-0.10: a run's own timeout that fired is the one named, not the host's <c>QueryTimeout</c>; without one, the host's
    /// option is named as before.
    /// </summary>
    [Fact]
    public async Task A_run_s_own_timeout_is_the_one_its_timeout_message_names()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "seq-before");

        page.Find("#ms-plan-timeout").Change("1");
        context.SqlConsole.NextPlan = new PlanRunResult
        {
            Error = new SqlError("57014", "canceling statement due to statement timeout", 0, null, null),
            ServerMajor = 17,
            TimeoutSeconds = 1,
        };
        await RunAsync(page, "select pg_sleep(3)");

        page.WaitForAssertion(() => page.Find(".ms-query-error-message").TextContent.Should().Contain("timed out after 1 s, the timeout set for this run"), Wait);
        page.Find(".ms-query-error-message").TextContent.Should().NotContain("QueryTimeout");

        page.Find("#ms-plan-timeout").Change(string.Empty);
        context.SqlConsole.NextPlan = new PlanRunResult
        {
            Error = new SqlError("57014", "canceling statement due to statement timeout", 0, null, null),
            ServerMajor = 17,
        };
        await RunAsync(page, "select pg_sleep(60)");

        page.WaitForAssertion(() => page.Find(".ms-query-error-message").TextContent.Should().Contain("QueryTimeout is what sets that"), Wait);
    }

    [Fact]
    public async Task A_pasted_plan_is_compared_with_nothing_read_from_the_database()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        await PasteAsync(context, page, PlanFixtures.GetPair("seq-before"));
        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle(), Wait);

        page.Find(".ms-plan-keep").Click();

        // The paste's own continuation may still hold the renderer's dispatcher when the click is queued behind it.
        page.WaitForAssertion(() => page.Find(".ms-plan-pin-label").TextContent.Should().Contain("the pasted plan"), Wait);

        await PasteAsync(context, page, PlanFixtures.GetPair("seq-after"));
        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);

        context.SqlConsole.Plans.Should().BeEmpty("a pasted plan reads nothing, compared or not");
    }

    [Fact]
    public async Task Compare_hides_the_unchanged_nodes_until_asked()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "wrap-before");

        page.Find(".ms-plan-keep").Click();
        context.SqlConsole.NextPlan = Ran("wrap-after");
        await RunAsync(page, "select * from orders order by total");
        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);

        page.FindAll(".ms-plan-compare-row").Should().OnlyContain(static x => !x.ClassList.Contains("ms-plan-diff-unchanged"));
        page.FindAll(".ms-plan-diff-added").Should().ContainSingle().Which.TextContent.Should().Contain("Limit").And.Contain("Only in After");

        page.Find(".ms-plan-compare-unchanged input").Change(true);

        page.FindAll(".ms-plan-compare-row").Count.Should().BeGreaterThan(1);
        page.FindAll("tr.ms-plan-diff-unchanged").Should().NotBeEmpty();
    }

    // ---- Settings ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_settings_panel_offers_only_the_names_the_server_has_and_plans_again_with_the_chosen_ones()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "seq-before", offers: Offers);

        context.SqlConsole.Plans[0].Request.OfferSettings.Should().BeTrue("the first run asks the server which settings it has");
        page.FindAll(".ms-plan-whatif-setting").Select(static x => x.QuerySelector("code")!.TextContent).Should().Equal("work_mem", "random_page_cost", "enable_seqscan");

        // A value the page itself can tell is wrong is not sent.
        page.FindAll(".ms-plan-whatif-setting-pick input")[0].Change(true);
        page.Find("#ms-plan-setting-work_mem").Change("1kB");
        await page.InvokeAsync(() => page.Find(".ms-plan-whatif-settings-run").Click());

        page.Find(".ms-plan-whatif-setting-error").TextContent.Should().Contain("At least 64");
        context.SqlConsole.Plans.Should().HaveCount(1, "nothing was sent");

        page.Find("#ms-plan-setting-work_mem").Change("64MB");
        context.SqlConsole.NextPlan = Ran("seq-after");
        await page.InvokeAsync(() => page.Find(".ms-plan-whatif-settings-run").Click());

        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);
        context.SqlConsole.Plans.Should().HaveCount(2, "the plan on screen is Before, so only the changed plan is asked for");
        PlanRunRequest sent = context.SqlConsole.Plans[1].Request;
        sent.SessionSettings.Should().Equal(new PlanSetting("work_mem", "64MB"));
        sent.OfferSettings.Should().BeFalse("the page already has the list");
        page.Find(".ms-plan-compare-labels").TextContent.Should().Contain("work_mem=64MB");
    }

    [Fact]
    public async Task A_settings_run_needs_a_setting_and_a_value_and_shows_nothing_for_a_pasted_plan()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "seq-before", offers: Offers);

        await page.InvokeAsync(() => page.Find(".ms-plan-whatif-settings-run").Click());
        page.Find(".ms-plan-whatif-settings-message").TextContent.Should().Contain("Pick at least one setting");
        context.SqlConsole.Plans.Should().HaveCount(1);

        await PasteAsync(context, page, PlanFixtures.GetPair("seq-after"));
        page.WaitForAssertion(() => page.FindAll(".ms-plan-whatif-settings").Should().BeEmpty("a pasted plan has no statement to plan again"), Wait);
    }

    // ---- Generic against custom ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Generic_and_custom_are_planned_by_one_action_the_generic_plan_is_Before_and_P21_judges_the_pair()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.QueuedPlans.Enqueue(Ran("generic-fires", placeholders: 1));
        context.SqlConsole.QueuedPlans.Enqueue(Ran("custom", placeholders: 1));

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select * from orders where customer_id = $1");
        page.Find("#ms-plan-param-1").Change("42");

        await page.InvokeAsync(() => page.Find(".ms-plan-compare-generic").Click());

        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);
        context.SqlConsole.Plans.Select(static x => x.Request.Mode).Should().Equal(PlanMode.Generic, PlanMode.Plan);
        context.SqlConsole.Plans[0].Request.Values.Should().BeEmpty("the generic plan takes no values");
        context.SqlConsole.Plans[1].Request.Values.Should().Equal("42");

        page.Find(".ms-plan-compare-labels").TextContent.Should().Contain("the generic plan").And.Contain("the custom plan");
        page.Find(".ms-plan-compare-insights").TextContent.Should().Contain("P21").And.Contain("force_custom_plan");
    }

    [Fact]
    public async Task A_generic_plan_that_is_not_ten_times_dearer_is_compared_without_P21()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.QueuedPlans.Enqueue(Ran("generic-quiet", placeholders: 1));
        context.SqlConsole.QueuedPlans.Enqueue(Ran("custom", placeholders: 1));

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select * from orders where customer_id = $1");
        page.Find("#ms-plan-param-1").Change("42");

        await page.InvokeAsync(() => page.Find(".ms-plan-compare-generic").Click());

        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);
        page.FindAll(".ms-plan-compare-insights").Should().BeEmpty();
    }

    [Fact]
    public async Task The_generic_comparison_is_offered_only_for_a_statement_with_placeholders()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.Find("#ms-plan-sql").Change("select 1");
        page.FindAll(".ms-plan-compare-generic").Should().BeEmpty();

        page.Find("#ms-plan-sql").Change("select $1::int");
        page.FindAll(".ms-plan-compare-generic").Should().ContainSingle();
    }

    // ---- What if? in place ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task What_if_in_place_plans_the_pages_own_statement_and_fills_Compare_without_drawing_the_two_plans_twice()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.WhatIf = static index => new WhatIfResult
        {
            Index = index,
            Availability = HypotheticalIndexState.Available,
            Before = PlanFixtures.Pair("seq-before"),
            After = PlanFixtures.Pair("seq-after"),
            IndexName = "<1>btree_orders_customer_id",
            Used = true,
        };

        IRenderedComponent<PlanPage> page = await FirstAsync(context, "generic-fires", "select * from orders where customer_id = 42");

        await page.InvokeAsync(() => page.Find(".ms-whatif-run").Click());

        page.WaitForAssertion(() => page.FindAll(".ms-plan-compare").Should().ContainSingle(), Wait);
        context.SqlConsole.PlanWhatIfs.Should().ContainSingle().Which.Request.Statement.Should().Be("select * from orders where customer_id = 42");
        context.SqlConsole.WhatIfs.Should().BeEmpty("the console's own what-if is not the page's");
        page.Find(".ms-whatif-in-place").TextContent.Should().Contain("Compare below");
        page.FindAll(".ms-whatif-compare").Should().BeEmpty("the plans are drawn once, in Compare");
        page.Find(".ms-plan-compare-labels").TextContent.Should().Contain("the plan now").And.Contain("with the hypothetical index");
    }

    [Fact]
    public async Task Without_hypopg_What_if_in_place_says_so_and_compares_nothing()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "generic-fires", "select * from orders where customer_id = 42");

        await page.InvokeAsync(() => page.Find(".ms-whatif-run").Click());

        page.WaitForAssertion(() => page.Find(".ms-whatif-reason").TextContent.Should().Contain("hypopg is not installed"), Wait);
        page.FindAll(".ms-plan-compare").Should().BeEmpty();
    }

    [Fact]
    public async Task A_late_What_if_answer_does_not_overwrite_the_Compare_of_a_plan_that_arrived_after_it_was_asked()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.WhatIf = static index => new WhatIfResult
        {
            Index = index,
            Availability = HypotheticalIndexState.Available,
            Before = PlanFixtures.Pair("seq-before"),
            After = PlanFixtures.Pair("seq-after"),
            IndexName = "<1>btree_orders_customer_id",
            Used = true,
        };

        IRenderedComponent<PlanPage> page = await FirstAsync(context, "generic-fires", "select * from orders where customer_id = 42");

        TaskCompletionSource gate = new();
        context.SqlConsole.WhatIfGate = gate;
        await page.InvokeAsync(() => page.Find(".ms-whatif-run").Click());
        context.SqlConsole.PlanWhatIfs.Should().ContainSingle("the what-if is asked and waiting");

        // Before it answers, the visitor keeps this plan as Before and runs another: that pair is the page's Compare now.
        page.Find(".ms-plan-keep").Click();
        context.SqlConsole.NextPlan = Ran("wrap-after");
        await RunAsync(page, "select * from orders order by total");
        page.WaitForAssertion(() => page.Find(".ms-plan-compare-labels").TextContent.Should().Contain("Explain"), Wait);
        string compare = page.Find(".ms-plan-compare").InnerHtml;

        // The late answer arrives, and what is drawn is not changed by it.
        await page.InvokeAsync(() => gate.SetResult());
        await page.InvokeAsync(() => { });
        await page.InvokeAsync(() => { });

        page.Find(".ms-plan-compare").InnerHtml.Should().Be(compare);
        page.Find(".ms-plan-compare-labels").TextContent.Should().NotContain("hypothetical index");
    }

    // ---- The database changes under a run ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_in_flight_when_the_database_changes_is_cancelled_and_nothing_of_it_is_left_under_the_new_one()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        // A first plan, kept as Before; then a second run whose service is slow, and which ignores the token a slow service may.
        IRenderedComponent<PlanPage> page = await FirstAsync(context, "seq-before", offers: Offers);
        page.Find(".ms-plan-keep").Click();

        TaskCompletionSource gate = new();
        context.SqlConsole.PlanGate = gate;
        context.SqlConsole.NextPlan = Ran("seq-after", Offers);
        page.Find("#ms-plan-sql").Change("select * from orders where id = 1");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        context.SqlConsole.Plans.Should().HaveCount(2, "the second run is in the service, waiting");

        // The scope moves under it.
        await page.InvokeAsync(() => context.State.RefreshScopeListingsAsync());
        page.FindAll(".ms-plan-tree, .ms-plan-pin, .ms-plan-whatif-settings, .ms-plan-compare").Should().BeEmpty("the plan, Before and what the server offered are the old database's");

        // The service answers anyway.
        await page.InvokeAsync(() => gate.SetResult());
        await page.InvokeAsync(() => { });
        await page.InvokeAsync(() => { });

        page.WaitForAssertion(() => page.Markup.Should().Contain("Cancelled"), Wait);
        page.FindAll(".ms-plan-tree, .ms-plan-pin, .ms-plan-whatif-settings, .ms-plan-compare, .ms-plan-keep").Should().BeEmpty("nothing of the run that was started for the old database is written back");
    }

    // ---- The statement timeout -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_timeout_box_travels_in_the_request_empty_is_the_hosts_and_text_that_is_not_seconds_runs_nothing()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.NextPlan = Ran("seq-before");

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-timeout").GetAttribute("placeholder").Should().Be("30", "the host's own is the placeholder");

        await RunAsync(page, "select 1");
        context.SqlConsole.Plans[0].Request.TimeoutSeconds.Should().BeNull();

        page.Find("#ms-plan-timeout").Change("5");
        await RunAsync(page, "select 1");
        context.SqlConsole.Plans[1].Request.TimeoutSeconds.Should().Be(5);

        page.Find("#ms-plan-timeout").Change("soon");
        await RunAsync(page, "select 1");
        page.WaitForAssertion(() => page.Find(".ms-query-error-message").TextContent.Should().Contain("whole seconds"), Wait);
        context.SqlConsole.Plans.Should().HaveCount(2);

        page.Find("#ms-plan-timeout").Change("0");
        await RunAsync(page, "select 1");
        context.SqlConsole.Plans.Should().HaveCount(2, "zero is not 'no timeout'");
    }

    private static async Task PasteAsync(PostgresStudioComponentContext context, IRenderedComponent<PlanPage> page, string text)
    {
        JSRuntimeInvocationHandler<int> sending = context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true);

        PlanPaste paste = page.FindComponent<PlanPaste>().Instance;
        page.Find(".ms-plan-paste-draw").Click();

        await page.InvokeAsync(() => paste.BeginPaste());

        for (int i = 0; i < text.Length; i += 1000)
        {
            int start = i;
            await page.InvokeAsync(() => paste.PasteChunk(text.Substring(start, Math.Min(1000, text.Length - start))));
        }

        sending.SetResult(text.Length);
    }
}
