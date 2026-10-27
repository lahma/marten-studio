using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Events;

/// <summary>
/// M1's page: the stream stepped into one aggregate type - the step list, the selected step's event, the aggregate after
/// it and what it changed, where the fold threw, the dead letters that name an event - with the step in the query string
/// (D9), and a window that is read once and moved around in without reading again.
/// </summary>
public class StreamStepperTests
{
    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    private const string Aggregate = "X.StepTally";

    [Fact]
    public async Task Without_an_id_the_page_says_so_rather_than_reading_anything()
    {
        using StudioComponentContext context = await NewContextAsync();

        context.Navigate("marten/events/streams/step");
        IRenderedComponent<StreamStepper> page = context.Render<StreamStepper>();

        page.Find(".ms-empty-title").TextContent.Should().Be("No stream asked for");
        context.EventData.StepRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_an_aggregate_type_it_asks_for_one_and_steps_nothing()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, string.Empty);

        page.Find("#ms-stepper-type").Should().NotBeNull();
        page.Markup.Should().Contain("Pick the aggregate type");
        context.EventData.StepRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_step_asked_for_is_shown_with_its_event_the_aggregate_after_it_and_what_it_changed()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=2");

        context.EventData.StepRequests.Should().Equal([Aggregate + "@2"]);
        page.FindAll(".ms-stepper-steps-item").Should().HaveCount(3);
        page.Find(".ms-stepper-step-link-selected").TextContent.Should().Contain("v2").And.Contain("1 change");
        page.Find(".ms-stepper-step-link-selected").GetAttribute("aria-current").Should().Be("step");

        page.Find(".ms-stepper-step .ms-event-card-version").TextContent.Should().Be("v2");
        page.Markup.Should().Contain("The aggregate after this event");
        page.Find(".ms-stepper-diff").TextContent.Should().Contain("$.Balance").And.Contain("10").And.Contain("25");
        page.FindAll(".ms-stepper-failure").Should().BeEmpty();
    }

    [Fact]
    public async Task The_first_step_says_the_event_created_the_aggregate()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=1");

        page.Find(".ms-stepper-diff").TextContent.Should().Contain("This event created the aggregate");
    }

    /// <summary>
    /// The stepper stops on the event whose fold threw: its exception is on the page, the aggregate shown is the one
    /// before it, and there is no Next - Marten's aggregate at that version or any later one is the failure.
    /// </summary>
    [Fact]
    public async Task A_step_whose_fold_threw_shows_the_exception_and_the_aggregate_before_it_and_stops()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=3");

        page.Find(".ms-stepper-failure").TextContent.Should()
            .Contain("System.InvalidOperationException").And.Contain("Jammed: the third one").And.Contain("stops here");
        page.Markup.Should().Contain("The aggregate before this event");
        page.Find(".ms-stepper-state").TextContent.Should().Contain("25", "the state before the throw is the second step's");
        page.FindAll(".ms-stepper-diff").Should().BeEmpty("a step that did not fold changed nothing to show");

        page.FindAll(".ms-stepper-nav button").Single(x => x.TextContent.Trim() == "Next").HasAttribute("disabled").Should().BeTrue();
        page.FindAll(".ms-stepper-nav button").Single(x => x.TextContent.Trim() == "Last").HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>The step links to the dead letters that name its event; the list marks the step.</summary>
    [Fact]
    public async Task A_dead_letter_that_names_the_event_is_on_the_step_with_a_link_to_it()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=3");

        page.FindAll(".ms-stepper-steps-item")[2].QuerySelector(".ms-stepper-marker-dead").Should().NotBeNull();
        page.Find(".ms-stepper-dead-letter").TextContent.Should().Contain("StepTally").And.Contain("InvalidOperationException");
        page.Find(".ms-stepper-dead-letter a").GetAttribute("href").Should().Contain("events/dead-letters?seq=103");
    }

    /// <summary>D9: every move is a navigation, and a move inside the window reads nothing; outside it, the next window.</summary>
    [Fact]
    public async Task Moving_inside_the_window_reads_nothing_and_outside_it_reads_the_window_that_holds_the_step()
    {
        using StudioComponentContext context = await ArrangedAsync();
        StreamStepWindow open = Window();
        context.EventData.StepWindows[2] = open with { Steps = [.. open.Steps.Take(2)], StoppedAt = null, HasMore = true };

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=2");

        await page.FindAll(".ms-stepper-nav button").Single(x => x.TextContent.Trim() == "Previous").ClickAsync(new());
        context.CurrentUri.Should().Contain("v=1").And.Contain("agg=" + Aggregate);

        page.Render();
        page.Instance.Version.Should().Be(1);
        page.Find(".ms-stepper-step-link-selected").TextContent.Should().Contain("v1");
        context.EventData.StepRequests.Should().Equal([Aggregate + "@2"], "version 1 is in the window on screen");

        context.Navigate(Url("&agg=" + Aggregate + "&v=45"));
        page.Render();

        page.WaitForAssertion(() => context.EventData.StepRequests.Should().Equal([Aggregate + "@2", Aggregate + "@45"]));
    }

    /// <summary>A fold that stopped in the window answers every later step with the same stop, and reads nothing for it.</summary>
    [Fact]
    public async Task A_step_past_where_the_fold_stopped_is_the_stop_and_reads_nothing()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=2");

        context.Navigate(Url("&agg=" + Aggregate + "&v=40"));
        page.Render();

        context.EventData.StepRequests.Should().Equal([Aggregate + "@2"]);
        page.Find(".ms-stepper-step-link-selected").TextContent.Should().Contain("v3");
        page.Find(".ms-stepper-stopped").TextContent.Should().Contain("You asked for version 40").And.Contain("stops at version 3");
    }

    /// <summary>A dead letter's link names its projection; the page steps the aggregate that projection folds.</summary>
    [Fact]
    public async Task A_projection_name_picks_the_aggregate_it_folds()
    {
        using StudioComponentContext context = await ArrangedAsync();

        Render(context, "&projection=StepTally&v=2");

        context.EventData.StepRequests.Should().Equal([Aggregate + "@2"]);
    }

    [Fact]
    public async Task A_projection_that_is_not_an_aggregate_of_the_stream_is_said_and_steps_nothing()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, "&projection=ShipmentTracker&v=2");

        page.Markup.Should().Contain("The dead letter came from ShipmentTracker");
        context.EventData.StepRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Finding_the_next_change_asks_from_the_step_on_screen_and_goes_to_the_event_it_found()
    {
        using StudioComponentContext context = await ArrangedAsync();
        context.EventData.Search = new StepSearch { Path = "$.Balance", AfterVersion = 1, FoundVersion = 2 };

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=1");

        await page.Find("#ms-stepper-path").InputAsync(new() { Value = "Balance" });
        await page.FindAll(".ms-stepper-search button").Single().ClickAsync(new());

        context.EventData.SearchRequests.Should().Equal([Aggregate + "@1:Balance"]);
        context.CurrentUri.Should().Contain("v=2").And.Contain("path=Balance");
        page.Find(".ms-stepper-search-result").TextContent.Should().Contain("Version 2 changed $.Balance.");
    }

    [Fact]
    public async Task A_search_that_finds_nothing_says_how_far_it_looked()
    {
        using StudioComponentContext context = await ArrangedAsync();
        context.EventData.Search = new StepSearch { Path = "$.Owner", AfterVersion = 1, ScannedThrough = 3 };

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=1");

        await page.Find("#ms-stepper-path").InputAsync(new() { Value = "Owner" });
        await page.FindAll(".ms-stepper-search button").Single().ClickAsync(new());

        page.Find(".ms-stepper-search-result").TextContent.Should()
            .Contain("No event after version 1 changed $.Owner").And.Contain("version 3");
    }

    /// <summary>
    /// 0.6's UI sweep: the path bound on change, so "Find next change" stayed disabled until the field lost focus. It binds on
    /// input now - the button is enabled while the path is typed, and disabled again once it is cleared - and nothing is
    /// searched until it is clicked.
    /// </summary>
    [Fact]
    public async Task Find_next_change_is_enabled_while_the_path_is_typed_not_only_once_the_field_loses_focus()
    {
        using StudioComponentContext context = await ArrangedAsync();

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=1");
        AngleSharp.Dom.IElement Button() => page.FindAll(".ms-stepper-search button").Single();

        Button().HasAttribute("disabled").Should().BeTrue("there is no path to look for yet");

        await page.Find("#ms-stepper-path").InputAsync(new() { Value = "Bal" });
        page.WaitForAssertion(() => Button().HasAttribute("disabled").Should().BeFalse("a keystroke is enough; no change event has fired"));

        await page.Find("#ms-stepper-path").InputAsync(new() { Value = string.Empty });
        Button().HasAttribute("disabled").Should().BeTrue();

        context.EventData.SearchRequests.Should().BeEmpty("typing searches nothing");
    }

    [Theory]
    [InlineData(AggregateMissingReason.TenantRequired, "Pick a tenant")]
    [InlineData(AggregateMissingReason.StreamArchived, "archived")]
    [InlineData(AggregateMissingReason.StreamMissing, "no such stream")]
    internal async Task A_stream_that_gives_the_fold_nothing_says_why(AggregateMissingReason reason, string expected)
    {
        using StudioComponentContext context = await ArrangedAsync();
        context.EventData.StepWindows.Clear();
        context.EventData.Step = StreamStepWindow.NotFound("StepTally", 1, reason);

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate);

        page.Find(".ms-stepper-missing").TextContent.Should().Contain(expected);
    }

    [Fact]
    public async Task A_window_that_could_not_be_folded_in_time_is_a_value_with_a_retry()
    {
        using StudioComponentContext context = await ArrangedAsync();
        context.EventData.StepWindows.Clear();
        context.EventData.Step = StreamStepWindow.Failed(
            "StepTally", 1, new EventDataError("Folding this stream to the window took longer than the studio's query timeout", null, true));

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate);

        page.Find(".ms-error-alert").TextContent.Should().Contain("query timeout");
    }

    [Fact]
    public async Task A_projection_folded_from_the_start_says_so()
    {
        using StudioComponentContext context = await ArrangedAsync();
        context.EventData.StepWindows[2] = Window() with
        {
            Mode = AggregateStepMode.FromTheStart,
            ModeReason = "StepBatchProjection overrides DetermineAction, which Marten hands a whole batch of events at once",
        };

        IRenderedComponent<StreamStepper> page = Render(context, "&agg=" + Aggregate + "&v=2");

        page.Find(".ms-stepper-mode").TextContent.Should().Contain("overrides DetermineAction");
    }

    [Fact]
    public async Task The_time_travel_panel_links_to_the_stepper_at_its_version_and_type()
    {
        using StudioComponentContext context = await ArrangedAsync();

        context.Navigate("marten/events/streams/s?id=" + StreamId + "&agg=" + Aggregate + "&v=2");
        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();

        page.Find("a.ms-time-travel-step").GetAttribute("href").Should()
            .Contain("events/streams/step?id=" + StreamId).And.Contain("agg=" + Aggregate).And.Contain("v=2");
    }

    /// <summary>The window every test steps: three events, the third one's fold threw and a dead letter names it.</summary>
    private static StreamStepWindow Window() => new()
    {
        TypeName = "StepTally",
        RequestedVersion = 2,
        StateBeforeJson = null,
        Steps =
        [
            new AggregateStep(101, 1, "step_opened", "StepOpened", At(1), "*DEFAULT*", """{"Owner":"acme"}""")
            {
                StateJson = """{"Id":"11111111-1111-1111-1111-111111111111","Owner":"acme","Balance":10}""",
            },
            new AggregateStep(102, 2, "step_credited", "StepCredited", At(2), "*DEFAULT*", """{"Amount":15}""")
            {
                StateJson = """{"Id":"11111111-1111-1111-1111-111111111111","Owner":"acme","Balance":25}""",
                Changes = 1,
            },
            new AggregateStep(103, 3, "step_jammed", "StepJammed", At(3), "*DEFAULT*", """{"Why":"the third one"}""")
            {
                Failure = new StepFailure("System.InvalidOperationException", "Jammed: the third one"),
                DeadLetters =
                [
                    new StepDeadLetter(Guid.NewGuid(), "StepTally", "All", "InvalidOperationException", "Failure to apply event #103", "*DEFAULT*"),
                ],
            },
        ],
        StoppedAt = 3,
    };

    private static DateTimeOffset At(int second) => new(2026, 10, 27, 9, 0, second, TimeSpan.Zero);

    private static async Task<StudioComponentContext> ArrangedAsync()
    {
        StudioComponentContext context = await NewContextAsync();

        context.EventData.StreamStates[StreamId] = new StreamState(
            StreamId, Exists: true, "step_tally", 3, At(1), At(3), IsArchived: false, TenantId: null);
        context.EventData.Candidates.Add(new AggregateTypeCandidate(
            typeof(StepTally), "StepTally", Aggregate, AggregateCandidateSource.SingleStreamProjection) { ProjectionName = "StepTally" });

        foreach (long version in (long[]) [1, 2, 3])
        {
            context.EventData.StepWindows[version] = Window() with { RequestedVersion = version };
        }

        return context;
    }

    private static string Url(string extra) => "marten/events/streams/step?id=" + StreamId + extra;

    private static IRenderedComponent<StreamStepper> Render(StudioComponentContext context, string extra)
    {
        context.Navigate(Url(extra));
        return context.Render<StreamStepper>();
    }

    private static async Task<StudioComponentContext> NewContextAsync()
    {
        var context = new StudioComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
