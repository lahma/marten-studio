using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Services.Shapes;
using MartenStudio.Tests.Components;

using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Shapes;

/// <summary>Event types › Check shapes (D-SHAPE S10): one call per type, problems first, the rest folded, over a hand-written fake of the check.</summary>
public class EventShapePanelTests
{
    private static readonly DateTimeOffset LongAgo = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static ShapeCheckResult Fits(string name, int examined = 20, DateTimeOffset? at = null) =>
        new(name, ShapeCoverage.Complete, at ?? LongAgo, TimeSpan.Zero, examined, 0, 0, 0, 0, null, null, [], [Type(name, examined)], null, null, []);

    private static ShapeTypeResult Type(string name, int examined, params ShapeFailureGroup[] failures) =>
        new(name, null, examined, 0, 0, failures, [], 0, 0);

    private static ShapeCheckResult Broken(string name)
    {
        ShapeFailureGroup failure = new(
            ShapeFailureKind.Unreadable, "JsonException", "$.total", System.Text.Json.JsonValueKind.String, null, 3, new ShapeItemKey("stream-9", null, 1042, 4), 1042);

        return new ShapeCheckResult(
            name,
            ShapeCoverage.Complete,
            LongAgo,
            TimeSpan.Zero,
            20,
            0,
            0,
            0,
            0,
            null,
            ShapeCheckText.UpcasterRemedy,
            [ShapeCheckText.ArchivedLeftOut],
            [Type(name, 20, failure)],
            ShapeCheckText.RebuildSerialization(name, 1042, false, false),
            null,
            [new ShapeStatement("select 1", ["@type"])]);
    }

    private static Task<(StudioComponentContext Context, FakeEventShapeCheck Check)> RigAsync(params string[] names) => RigAsync(null, names);

    private static async Task<(StudioComponentContext Context, FakeEventShapeCheck Check)> RigAsync(ManualClock? clock, string[] names)
    {
        FakeEventShapeCheck check = new();

        // Registered before anything resolves a service: ReadyAsync does, and bUnit locks the provider then.
        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<IEventShapeCheck>(check);

            if (clock is not null)
            {
                services.AddSingleton<TimeProvider>(clock);
            }
        });

        context.WithStores("default");
        context.Catalog.WithStore("second", "Second", databaseIdentities: "localhost.marten");
        await context.ReadyAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList([.. names.Select(static x => new EventTypeInfo(x, "Sample." + x, true))], false);
        context.EventData.TypesWithCounts = new EventTypeList(
            [.. names.Select(static (x, i) => new EventTypeInfo(x, "Sample." + x, true, i == 1 ? 0 : 10 + i))], true);

        return (context, check);
    }

    private static IElement? Button(IRenderedComponent<EventTypes> page, string text) =>
        page.FindAll("button").FirstOrDefault(x => x.TextContent.Trim() == text);

    private static async Task ClickAsync(IRenderedComponent<EventTypes> page, string text) =>
        await Button(page, text)!.ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Nothing_runs_until_the_button_is_pressed_and_then_each_type_is_checked_once_in_the_order_the_page_lists_them()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced", "OrderShipped", "OrderPaid");
        using (context)
        {
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            check.Calls.Should().BeEmpty();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => check.Calls.Select(static x => x.Name).Should().Equal("OrderPlaced", "OrderShipped", "OrderPaid"));
            check.Calls.Should().OnlyContain(static x => !x.Force);
        }
    }

    [Fact]
    public async Task Types_with_problems_come_first_with_the_rebuild_sentence_on_top_and_the_rest_fold_into_a_count()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced", "OrderShipped", "OrderPaid");
        using (context)
        {
            check.Results["OrderShipped"] = Broken("OrderShipped");
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.FindAll(".ms-shape-event-type").Should().ContainSingle());
            IElement problem = page.Find(".ms-shape-event-type");

            problem.QuerySelector("h3")!.TextContent.Should().Contain("OrderShipped");
            problem.QuerySelector(".ms-shape-rebuild")!.TextContent.Should().Contain(
                "A rebuild of a projection that reads OrderShipped stops at sequence 1,042, the first of them, because RebuildErrors.SkipSerializationErrors is off.");
            problem.QuerySelector(".ms-shape-remedy")!.TextContent.Should().Contain("opts.Events.Upcast(...)");
            problem.TextContent.Should().Contain("3 of 20 sampled OrderShipped events cannot be read: $.total is stored as a string (JsonException).");

            // The rebuild sentence is above the table.
            problem.InnerHtml.IndexOf("ms-shape-rebuild", StringComparison.Ordinal).Should().BeLessThan(problem.InnerHtml.IndexOf("ms-shape-table", StringComparison.Ordinal));

            page.Find(".ms-shape-fits-fold summary").TextContent.Should().Be("2 types fit");
            page.Find(".ms-shape-summary").TextContent.Should().Be("Checked 3 event types: 1 with problems, 2 fit.");
        }
    }

    [Fact]
    public async Task The_example_links_to_the_stream_at_the_version_in_the_scope_the_check_ran_with_and_every_table_is_in_a_scroll_region()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderShipped");
        using (context)
        {
            check.Results["OrderShipped"] = Broken("OrderShipped");
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.FindAll("a.ms-id-text").Should().NotBeEmpty());
            string href = page.Find("a.ms-id-text").GetAttribute("href")!;

            href.Should().Contain("events/streams/s").And.Contain("id=stream-9").And.Contain("v=4").And.Contain("store=default");

            foreach (IElement table in page.FindAll(".ms-shape-table"))
            {
                table.Closest(".ms-table-scroll").Should().NotBeNull();
            }
        }
    }

    [Fact]
    public async Task A_type_the_page_counted_as_empty_is_not_sent_to_the_check()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced", "OrderShipped", "OrderPaid");
        using (context)
        {
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await page.Find(".ms-page-header button").ClickAsync(new MouseEventArgs());
            page.WaitForAssertion(() => page.Markup.Should().Contain("OrderShipped"));
            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => check.Calls.Select(static x => x.Name).Should().Equal("OrderPlaced", "OrderPaid"));
            page.WaitForAssertion(() => page.Find(".ms-shape-fits-fold").TextContent.Should().Contain("There are no events of this type in scope."));
        }
    }

    [Fact]
    public async Task Cancel_stops_after_the_types_done_and_says_how_far_it_got()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced", "OrderShipped", "OrderPaid");
        using (context)
        {
            TaskCompletionSource<ShapeCheckResult> gate = new();
            check.GateFor = "OrderShipped";
            check.Gate = gate;
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => Button(page, "Cancel").Should().NotBeNull());
            page.Find(".ms-shape-note").TextContent.Should().Be("Checking 2 of 3...");
            await ClickAsync(page, "Cancel");

            page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("cancelled after 1 of 3 event types"));
            check.Calls.Select(static x => x.Name).Should().Equal("OrderPlaced", "OrderShipped");
            Button(page, "Check again").Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_check_that_fails_is_shown_as_a_problem_with_its_sentence()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced");
        using (context)
        {
            check.Results["OrderPlaced"] = ShapeCheckResult.Failed("OrderPlaced", "The shape check could not be completed: busy.", LongAgo, TimeSpan.Zero, []);
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-event-type .ms-error-alert").TextContent.Should().Contain("could not be completed"));
        }
    }

    [Fact]
    public async Task Check_again_re_enables_itself_when_the_window_opens_and_the_timer_goes_with_the_panel()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync(clock, ["OrderPlaced"]);
        using (context)
        {
            check.Results["OrderPlaced"] = Fits("OrderPlaced", at: clock.GetUtcNow());
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => Button(page, "Check again")!.HasAttribute("disabled").Should().BeTrue());
            Button(page, "Check again")!.GetAttribute("title").Should().StartWith("Available at ");

            clock.Advance(ShapeCheckLimits.RecheckAfter + TimeSpan.FromSeconds(1));
            clock.FireAll();

            page.WaitForAssertion(() => Button(page, "Check again")!.HasAttribute("disabled").Should().BeFalse());

            await ClickAsync(page, "Close");
            clock.Timers.Should().OnlyContain(static x => x.Disposed);
        }
    }

    [Fact]
    public async Task A_scope_change_re_creates_the_open_panel_and_checks_again_for_the_new_scope()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced");
        using (context)
        {
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");
            page.WaitForAssertion(() => check.Scopes.Select(static x => x.StoreKey).Should().Equal("default"));

            await page.InvokeAsync(() => context.State.SetScopeAsync("second", null, null, Xunit.TestContext.Current.CancellationToken));

            page.WaitForAssertion(() => check.Scopes.Select(static x => x.StoreKey).Should().Equal("default", "second"));
        }
    }

    [Fact]
    public async Task With_the_index_a_click_that_runs_past_its_minute_stops_and_says_press_again_and_the_next_press_continues_without_forcing()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync(clock, ["OrderPlaced", "OrderShipped", "OrderPaid"]);
        using (context)
        {
            check.Indexed = true;
            check.OnCall = () => clock.Advance(TimeSpan.FromSeconds(40));
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Markup.Should().Contain("Checked 2 of 3 types; press again for the rest."));
            check.Calls.Select(static x => x.Name).Should().Equal("OrderPlaced", "OrderShipped");
            Button(page, "Check again")!.HasAttribute("disabled").Should().BeFalse("the press that continues is offered at once");

            check.OnCall = null;
            await ClickAsync(page, "Check again");

            page.WaitForAssertion(() => check.Calls.Select(static x => x.Name).Should().Equal("OrderPlaced", "OrderShipped", "OrderPlaced", "OrderShipped", "OrderPaid"));
            check.Calls.Skip(2).Should().OnlyContain(static x => !x.Force, "the types already reached are cached: the press continues, it does not force");
            page.Markup.Should().NotContain("press again for the rest");
        }
    }

    [Fact]
    public async Task Without_the_index_the_counts_come_first_empty_types_cost_no_call_and_the_minute_ends_with_the_remedy()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync(clock, ["A", "B", "C", "D"]);
        using (context)
        {
            check.Indexed = false;
            check.OnCall = () => clock.Advance(TimeSpan.FromSeconds(40));
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            // The page never counted, so the panel read the (cached) listing: B has no events, and is not sent to the check.
            page.WaitForAssertion(() => page.Markup.Should().Contain("Checked 3 of 4 types; the rest need the index on type to be checked quickly"));
            page.Find(".ms-shape-panel").TextContent.Should().Contain("opts.Events.EnableEventTypeIndex = true;");
            check.Calls.Select(static x => x.Name).Should().Equal("A", "C");
        }
    }

    [Fact]
    public async Task A_result_the_cache_handed_back_says_when_it_was_checked_and_how_long_ago_and_offers_Check_again_beside_it()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync(clock, ["OrderPlaced", "OrderShipped"]);
        using (context)
        {
            check.Results["OrderPlaced"] = Fits("OrderPlaced", at: clock.GetUtcNow() - TimeSpan.FromMinutes(3));
            check.Results["OrderShipped"] = Broken("OrderShipped") with { CheckedAt = clock.GetUtcNow() - TimeSpan.FromSeconds(40) };
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-fits-fold li").TextContent.Should().Contain("(3 min ago, from the last check)"));
            page.Find(".ms-shape-event-type h3").TextContent.Should().Contain("(40 s ago, from the last check)");

            IElement beside = page.Find(".ms-shape-summary-row .ms-shape-recheck");
            beside.TextContent.Trim().Should().Be("Check again");
            beside.HasAttribute("disabled").Should().BeFalse("both checks are older than the thirty seconds after which a press runs again");
        }
    }

    [Fact]
    public async Task A_result_made_by_this_press_carries_no_age_and_no_second_Check_again()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync(clock, ["OrderPlaced"]);
        using (context)
        {
            check.Results["OrderPlaced"] = Fits("OrderPlaced", at: clock.GetUtcNow());
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-fits-fold li").TextContent.Should().Contain("Checked all 20 events at "));
            page.Find(".ms-shape-fits-fold li").TextContent.Should().NotContain("from the last check");
            page.FindAll(".ms-shape-recheck").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task An_event_type_only_the_log_holds_is_checked_though_the_page_never_counted_and_the_store_has_the_index()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced", "OrderShipped");
        using (context)
        {
            check.Indexed = true;
            context.EventData.TypesWithCounts = new EventTypeList(
                [
                    new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true, 10),
                    new EventTypeInfo("OrderShipped", "Sample.OrderShipped", true, 11),
                    new EventTypeInfo("ghost_event", "Gone.Ghost, Gone", false, 3),
                ],
                true);
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => check.Calls.Select(static x => x.Name).Should().Equal("OrderPlaced", "OrderShipped", "ghost_event"));
            page.WaitForAssertion(() => page.Find(".ms-shape-summary").TextContent.Should().Be("Checked 3 event types: 0 with problems, 3 fit."));
        }
    }

    [Fact]
    public async Task When_the_log_cannot_be_listed_the_registered_types_are_checked_and_the_panel_says_the_others_were_not()
    {
        (StudioComponentContext context, FakeEventShapeCheck check) = await RigAsync("OrderPlaced");
        using (context)
        {
            context.EventData.TypesWithCounts = EventTypeList.Failed(new EventDataError("busy", null, true));
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => check.Calls.Select(static x => x.Name).Should().Equal("OrderPlaced"));
            page.WaitForAssertion(() => page.Find(".ms-shape-panel").TextContent.Should().Contain("the store does not register could not be listed, so they were not checked"));
        }
    }

    [Fact]
    public async Task Close_removes_the_panel()
    {
        (StudioComponentContext context, _) = await RigAsync("OrderPlaced");
        using (context)
        {
            IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

            await ClickAsync(page, "Check shapes");
            page.WaitForAssertion(() => page.FindAll(".ms-shape-panel").Should().ContainSingle());

            await ClickAsync(page, "Close");

            page.FindAll(".ms-shape-panel").Should().BeEmpty();
        }
    }

    /// <summary>A clock that moves when told, and whose timers fire when told.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public List<ManualTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;

        public void FireAll()
        {
            foreach (ManualTimer timer in Timers.Where(static x => !x.Disposed).ToList())
            {
                timer.Callback(timer.State);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A hand-written check: a result per name, one name that waits on a gate, and a record of what was asked.</summary>
    private sealed class FakeEventShapeCheck : IEventShapeCheck
    {
        public Dictionary<string, ShapeCheckResult> Results { get; } = new(StringComparer.Ordinal);

        public string? GateFor { get; set; }

        public TaskCompletionSource<ShapeCheckResult>? Gate { get; set; }

        public List<(string Name, bool Force)> Calls { get; } = [];

        public List<StudioScope> Scopes { get; } = [];

        /// <summary>Whether the store has the index on type.</summary>
        public bool Indexed { get; set; } = true;

        /// <summary>Run at every call, before it answers: a test moves its clock here.</summary>
        public Action? OnCall { get; set; }

        public Task<bool> HasTypeIndexAsync(StudioScope scope, CancellationToken cancellationToken) => Task.FromResult(Indexed);

        public Task<ShapeCheckResult> CheckAsync(StudioScope scope, string eventTypeName, bool force, CancellationToken cancellationToken)
        {
            Calls.Add((eventTypeName, force));
            Scopes.Add(scope);
            OnCall?.Invoke();

            if (Gate is { } gate && string.Equals(GateFor, eventTypeName, StringComparison.Ordinal))
            {
                return gate.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(Results.TryGetValue(eventTypeName, out ShapeCheckResult? result) ? result : Fits(eventTypeName));
        }
    }
}
