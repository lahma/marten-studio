using AngleSharp.Dom;

using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Tests.Components;
using MartenStudio.Tests.Database;

using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// A finding card's on-demand action on the Health page (1.2-H-C), in bUnit over the fake action service: the button on an offering
/// card only, nothing run on render, the running state, the result in a labelled scroll region, and when a result is dropped.
/// </summary>
public class HealthPageActionTests
{
    private const string Stream = "5f8b3a52-1c6f-4f0e-8f2e-0b1a2c3d4e5f";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static async Task<StudioComponentContext> ReadyAsync(Action<StudioComponentContext>? configure = null)
    {
        StudioComponentContext context = new();
        configure?.Invoke(context);
        await context.ReadyAsync();
        context.HealthActions.OfferedFor = "E06";
        context.Health.WithFindings(
            FakeHealthService.Finding("E06", HealthSeverity.Info, "Some streams are long", HealthArea.Events),
            FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"));
        return context;
    }

    /// <summary>The same, over a store with two databases the visitor can move between.</summary>
    private static async Task<StudioComponentContext> ReadyTwoDatabasesAsync()
    {
        StudioComponentContext context = new();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a", "db-b");
        await context.State.EnsureInitializedAsync(Token);
        context.HealthActions.OfferedFor = "E06";
        context.Health.WithFindings(
            FakeHealthService.Finding("E06", HealthSeverity.Info, "Some streams are long", HealthArea.Events),
            FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"));
        return context;
    }

    private static HealthActionResult Done() => new()
    {
        Outcome = HealthActionOutcome.Done,
        RanAt = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.Zero),
        Took = TimeSpan.FromMilliseconds(1_234),
        Summary = "The 2 longest live streams in this database: 2 were started as Trip.",
        TableLabel = "The longest streams",
        Columns = ["Stream", "Events", "Started"],
        Rows =
        [
            new HealthActionRow(
            [
                new HealthActionCell(Stream)
                {
                    Link = new HealthLink("Open the stream", "events/streams/s", [new("id", Stream)]),
                    Mono = true,
                    Copy = true,
                },
                new HealthActionCell("4,200") { Number = true },
                new HealthActionCell("2026-11-01 08:00:00Z") { Time = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero) },
            ]),
        ],
        Notes = ["Archived streams are left out."],
        Remedies = [new HealthRemedy("opts.Projections.Snapshot<Trip>(SnapshotLifecycle.Inline);", "Keeps it as a document.")],
        Statements = [new HealthStatement("select s.\"id\" from \"events\".\"mt_streams\" as s", ["@limit"]) { Label = "the longest streams" }],
    };

    private static IRenderedComponent<HealthPage> Render(StudioComponentContext context)
    {
        IRenderedComponent<HealthPage> page = context.Render<HealthPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-health-finding").Should().HaveCount(2));
        return page;
    }

    private static async Task PressAsync(IRenderedComponent<HealthPage> page)
    {
        await page.InvokeAsync(() => page.Find("#finding-e06 .ms-health-action-run").Click());
    }

    // ---------------------------------------------------------------------------------------------------
    // The button
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_button_is_on_the_offering_card_only_with_what_pressing_it_does_and_nothing_runs_on_render()
    {
        await using StudioComponentContext context = await ReadyAsync();

        IRenderedComponent<HealthPage> page = Render(context);

        IElement button = page.Find("#finding-e06 .ms-health-action-run");
        button.TextContent.Trim().Should().Be("Find the test rows");
        button.HasAttribute("disabled").Should().BeFalse();
        page.Find("#finding-e06 .ms-health-action-note").TextContent.Should().Contain("audited").And.Contain("kept nowhere");
        page.FindAll("#finding-k01 .ms-health-action").Should().BeEmpty("no other card offers one");
        page.FindAll(".ms-health-action-result").Should().BeEmpty();
        context.HealthActions.Runs.Should().BeEmpty("a render, a poll and a prerender read nothing the visitor did not ask for");
    }

    [Fact]
    public async Task An_offer_whose_gate_is_Activity_is_left_out_where_the_report_says_the_visitor_does_not_pass_it()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Offer = context.HealthActions.Offer with { Gate = HealthGate.Activity };
        context.Health.Report = context.Health.Report with { StatementChecksExist = true, StatementChecksAllowed = false };
        int questions = context.HealthActions.OfferQuestions;

        IRenderedComponent<HealthPage> page = Render(context);

        page.FindAll("#finding-e06 .ms-health-action").Should().BeEmpty("a press would be refused and audited anyway");
        context.HealthActions.Runs.Should().BeEmpty();
        context.HealthActions.OfferQuestions.Should().BeGreaterThan(questions, "the offer was asked for; the report's own answer left it out");
    }

    [Fact]
    public async Task An_Activity_offer_is_drawn_where_the_report_says_the_visitor_passes_it_and_a_Database_offer_is_drawn_either_way()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Offer = context.HealthActions.Offer with { Gate = HealthGate.Activity };
        context.Health.Report = context.Health.Report with { StatementChecksExist = true, StatementChecksAllowed = true };

        Render(context).FindAll("#finding-e06 .ms-health-action-run").Should().ContainSingle();

        context.HealthActions.Offer = context.HealthActions.Offer with { Gate = HealthGate.Database };
        context.Health.Report = context.Health.Report with { StatementChecksExist = true, StatementChecksAllowed = false };

        Render(context).FindAll("#finding-e06 .ms-health-action-run").Should().ContainSingle("a Database offer asks nothing the report knows to be refused");
    }

    [Fact]
    public async Task A_muted_finding_offers_it_too()
    {
        await using StudioComponentContext context = await ReadyAsync();
        IRenderedComponent<HealthPage> page = Render(context);

        await page.InvokeAsync(() => page.Find("#finding-e06 .ms-health-mute").Click());

        page.WaitForAssertion(() => page.FindAll(".ms-health-muted .ms-health-action-run").Should().ContainSingle());
    }

    [Fact]
    public async Task A_prerender_draws_no_button_and_asks_no_one()
    {
        await using StudioComponentContext context = await ReadyAsync();

        string html = await DatabasePagePrerenderTests.StaticAsync<HealthPage>(context);

        html.Should().NotContain("ms-health-action-");
        context.HealthActions.Runs.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Running
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task While_it_runs_the_button_says_so_and_every_action_button_is_off()
    {
        await using StudioComponentContext context = await ReadyAsync();
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.HealthActions.Hold = hold;
        context.HealthActions.Result = Done();
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);

        page.WaitForAssertion(() =>
        {
            IElement running = page.Find("#finding-e06 .ms-health-action-run");
            running.TextContent.Trim().Should().Be("Reading…");
            running.HasAttribute("disabled").Should().BeTrue();
            running.GetAttribute("aria-busy").Should().Be("true");
        });

        hold.SetResult();
        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().ContainSingle());
        page.Find("#finding-e06 .ms-health-action-run").TextContent.Trim().Should().Be("Find the test rows");
        context.HealthActions.Runs.Should().ContainSingle().Which.Should().Match<(StudioScope Scope, string MuteKey, string ActionId)>(
            static x => x.ActionId == "test.action" && x.Scope.StoreKey == "default");
    }

    [Fact]
    public async Task A_result_is_drawn_in_a_labelled_scroll_region_with_the_stream_linked_under_the_scope()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Result = Done();
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);
        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().ContainSingle());

        IElement result = page.Find("#finding-e06 .ms-health-action-result");
        result.QuerySelector(".ms-health-action-read")!.TextContent.Should().Contain("2026-11-04 09:30:00").And.Contain("in 1,234 ms");
        result.QuerySelector(".ms-health-action-summary")!.TextContent.Should().Contain("The 2 longest live streams");

        IElement region = result.QuerySelector(".ms-table-scroll")!;
        region.GetAttribute("role").Should().Be("region");
        region.GetAttribute("aria-label").Should().Be("The longest streams");
        region.QuerySelector("table.ms-health-action-table")!.QuerySelectorAll("th").Select(static x => x.TextContent).Should().Equal("Stream", "Events", "Started");

        IElement link = region.QuerySelector("tbody td a")!;
        link.TextContent.Should().Be(Stream);
        link.GetAttribute("href").Should().StartWith("events/streams/s?id=" + Stream).And.Contain("store=default").And.Contain("db=");
        region.QuerySelector(".ms-copy-button")!.GetAttribute("aria-label").Should().Be("Copy stream");
        region.QuerySelectorAll("tbody td")[1].ClassList.Should().Contain("ms-table-cell-number");
        region.QuerySelectorAll("tbody td")[2].TextContent.Should().Contain("2026-11-01 08:00:00", "a time is drawn in the visitor's zone");

        result.QuerySelector(".ms-health-action-notes")!.TextContent.Should().Contain("Archived streams are left out.");
        result.QuerySelector(".ms-health-remedy-code")!.TextContent.Should().Be("opts.Projections.Snapshot<Trip>(SnapshotLifecycle.Inline);");
        result.QuerySelector(".ms-sql-disclosure summary")!.TextContent.Should().Contain("Show SQL for the longest streams");
    }

    [Fact]
    public async Task A_refusal_is_a_status_alert_with_no_Run_again()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Result = HealthActionResult.Refused("Pick a tenant in the header.", DateTimeOffset.UtcNow);
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);

        page.WaitForAssertion(() => page.FindAll(".ms-health-action-refused").Should().ContainSingle());
        IElement alert = page.Find(".ms-health-action-refused");
        alert.ClassList.Should().Contain("ms-alert-warning");
        alert.GetAttribute("role").Should().Be("status");
        alert.TextContent.Should().Contain("Pick a tenant in the header.");
        page.FindAll(".ms-health-action-again").Should().BeEmpty();
        page.FindAll(".ms-health-action-table").Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_timeout_or_a_failure_is_an_error_alert_with_Run_again(bool timedOut)
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Result = timedOut
            ? HealthActionResult.TimedOut("It ran past its statement_timeout.", DateTimeOffset.UtcNow)
            : HealthActionResult.Failed("The action failed (InvalidOperationException); the server log has the details.", DateTimeOffset.UtcNow);
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);

        page.WaitForAssertion(() => page.FindAll(".ms-health-action-problem").Should().ContainSingle());
        page.Find(".ms-health-action-problem").GetAttribute("role").Should().Be("alert");

        await page.InvokeAsync(() => page.Find(".ms-health-action-again").Click());

        page.WaitForAssertion(() => context.HealthActions.Runs.Should().HaveCount(2));
    }

    [Fact]
    public async Task A_service_that_throws_puts_the_failure_on_the_card()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Throw = new InvalidOperationException("broken");
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);

        page.WaitForAssertion(() => page.Find(".ms-health-action-problem").TextContent.Should().Contain("could not be run (InvalidOperationException)"));
        page.Find("#finding-e06 .ms-health-action-run").HasAttribute("disabled").Should().BeFalse("the tab is free again");
    }

    // ---------------------------------------------------------------------------------------------------
    // When a result goes
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Close_removes_the_result_and_the_next_press_runs_again()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Result = Done();
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);
        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().ContainSingle());

        await page.InvokeAsync(() => page.Find(".ms-health-action-close").Click());
        page.FindAll(".ms-health-action-result").Should().BeEmpty();

        await PressAsync(page);
        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().ContainSingle());
        context.HealthActions.Runs.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_poll_keeps_a_result_and_a_finding_that_is_gone_takes_it_away()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.HealthActions.Result = Done();
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);
        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().ContainSingle());

        int reads = context.Health.Requests.Count;
        await page.InvokeAsync(() => page.Find(".ms-page-header .ms-button").Click());
        page.WaitForAssertion(() => context.Health.Requests.Count.Should().BeGreaterThan(reads));
        page.FindAll(".ms-health-action-result").Should().ContainSingle("the same finding, read again: the result stays");

        context.Health.WithFindings(FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"));
        reads = context.Health.Requests.Count;
        await page.InvokeAsync(() => page.Find(".ms-page-header .ms-button").Click());
        page.WaitForAssertion(() => context.Health.Requests.Count.Should().BeGreaterThan(reads));

        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().BeEmpty());
    }

    [Fact]
    public async Task Another_database_shows_none_of_it()
    {
        await using StudioComponentContext context = await ReadyTwoDatabasesAsync();
        context.HealthActions.Result = Done();
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);
        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().ContainSingle());

        await page.InvokeAsync(() => context.State.SetScopeAsync("default", "db-b", tenantId: null, Token));

        page.WaitForAssertion(() => page.FindAll(".ms-health-action-result").Should().BeEmpty());
    }

    [Fact]
    public async Task A_result_that_lands_after_the_scope_moved_is_not_drawn()
    {
        await using StudioComponentContext context = await ReadyTwoDatabasesAsync();
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.HealthActions.Hold = hold;
        context.HealthActions.Result = Done();
        IRenderedComponent<HealthPage> page = Render(context);

        await PressAsync(page);
        page.WaitForAssertion(() => context.HealthActions.Runs.Should().ContainSingle());

        await page.InvokeAsync(() => context.State.SetScopeAsync("default", "db-b", tenantId: null, Token));
        page.WaitForAssertion(() => page.FindAll(".ms-health-finding").Should().HaveCount(2));
        hold.SetResult();

        page.WaitForAssertion(() => page.Find("#finding-e06 .ms-health-action-run").HasAttribute("disabled").Should().BeFalse());
        page.FindAll(".ms-health-action-result").Should().BeEmpty("it was read for the database the visitor left");
    }
}
