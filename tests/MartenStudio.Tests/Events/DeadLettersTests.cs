using Bunit;

using JasperFx.Events.Projections;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The dead-letter screen: the list, the expansion that shows the exception and the offending event, and
/// the two actions this release ships - both gated, both confirmed.
/// </summary>
public class DeadLettersTests
{
    private static readonly Guid LetterId = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task The_rows_carry_the_projection_the_shard_the_sequence_and_the_exception_type()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        string row = page.Find("tbody tr").TextContent;
        row.Should().Contain("OrderSummary").And.Contain("OrderSummary:All").And.Contain("#42")
            .And.Contain("System.DivideByZeroException");
    }

    [Fact]
    public async Task The_expansion_shows_the_exception_and_the_event_that_failed()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.EventData.EventBySequence = FakeEventDataService.Event(42, type: "OrderShipped");

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        page.Find(".ms-dead-letter-exception").TextContent.Should().Contain("Attempted to divide by zero.");
        page.TextOfAll(".ms-event-chip").Should().Contain("OrderShipped");
    }

    [Fact]
    public async Task An_event_that_is_no_longer_there_says_so_rather_than_showing_nothing()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.EventData.EventBySequence = null;

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        page.Find(".ms-dead-letter-panel").TextContent.Should().Contain("this tenant cannot see it");
    }

    /// <summary>
    /// The capability is off by default (D4), so the page names the exact option a host would set - and
    /// renders no action at all.
    /// </summary>
    [Fact]
    public async Task Without_the_capability_the_actions_are_absent_and_the_option_is_named()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        page.Find(".ms-capability-disabled").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.ManageDeadLetters");

        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        page.TextOfAll(".ms-dead-letter-actions button").Should().NotContain("Discard");
        page.Find(".ms-dead-letter-actions").TextContent.Should().Contain("ManageDeadLetters");
    }

    [Fact]
    public async Task Discarding_confirms_first_and_then_calls_the_service()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        await page.Find(".ms-dead-letter-actions button.ms-button-danger").ClickAsync(new());

        page.Find(".ms-confirm-dialog").TextContent.Should().Contain("projection is not restarted");

        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        context.EventData.Discarded.Should().Equal(LetterId);
    }

    [Fact]
    public async Task Skipping_an_event_confirms_and_then_marks_the_sequence()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.Shape = context.EventData.Shape with { HasIsSkipped = true };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId, sequence: 77)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        await SkipButton(page).ClickAsync(new());
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        context.EventData.Skipped.Should().Equal(77L);
    }

    /// <summary>
    /// A store without <c>mt_events.is_skipped</c> cannot express the idea at all, so the button is
    /// disabled and says why rather than failing at the click.
    /// </summary>
    [Fact]
    public async Task Skipping_is_disabled_when_the_store_does_not_record_skipped_events()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        AngleSharp.Dom.IElement skip = SkipButton(page);
        skip.HasAttribute("disabled").Should().BeTrue();
        skip.GetAttribute("title").Should().Contain("is_skipped");
    }

    // -----------------------------------------------------------------------------------------------
    // Rewind subscription to this event - the third action the design names, and the only one that needs
    // a running daemon.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rewinding_is_offered_when_a_daemon_is_hosted_here_and_the_projection_is_async()
    {
        using StudioComponentContext context = await RewindableAsync();

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement rewind = RewindButton(page);

        rewind.HasAttribute("disabled").Should().BeFalse();
        rewind.GetAttribute("title").Should().Contain("re-applies this event");
    }

    /// <summary>
    /// A daemon running in another process is a supported deployment, not a fault: the button says so
    /// with the coordinator's own explanation rather than pretending the action does not exist.
    /// </summary>
    [Fact]
    public async Task Rewinding_is_disabled_and_explains_itself_when_the_daemon_runs_somewhere_else()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.ProjectionData.WithNoDaemonHere();

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement rewind = RewindButton(page);

        rewind.HasAttribute("disabled").Should().BeTrue();
        rewind.GetAttribute("title").Should().Contain("AddAsyncDaemon");
    }

    /// <summary>
    /// A dead letter is a document and outlives the code that wrote it, so a record can name a projection
    /// this store no longer has - and there is then no shard for the daemon to restart.
    /// </summary>
    [Fact]
    public async Task Rewinding_is_disabled_for_a_projection_this_store_no_longer_registers()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement rewind = RewindButton(page);

        rewind.HasAttribute("disabled").Should().BeTrue();
        rewind.GetAttribute("title").Should().Contain("no longer registers");
    }

    [Fact]
    public async Task Rewinding_is_disabled_for_a_projection_the_daemon_does_not_run()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.ProjectionData.WithProjection("OrderSummary", ProjectionLifecycle.Inline);
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement rewind = RewindButton(page);

        rewind.HasAttribute("disabled").Should().BeTrue();
        rewind.GetAttribute("title").Should().Contain("runs Inline");
    }

    [Fact]
    public async Task Rewinding_is_absent_without_the_capability()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.ProjectionData.WithProjection("OrderSummary");
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        page.TextOfAll(".ms-dead-letter-actions button").Should().NotContain(x => x.Contains("Rewind", StringComparison.Ordinal));
    }

    /// <summary>
    /// The dialog has to say what a rewind costs before it is confirmed: how many events are re-applied
    /// and from where, what the progression is set to, which shards restart and which tables are written.
    /// </summary>
    [Fact]
    public async Task Rewinding_states_the_replay_the_floor_the_shards_and_the_tables()
    {
        using StudioComponentContext context = await RewindableAsync();

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);
        await RewindButton(page).ClickAsync(new());

        string dialog = page.Find(".ms-confirm-dialog").TextContent;

        // The dead letter is #42 and the high-water mark is 1,000, so the replay is 959 events.
        dialog.Should().Contain("959 events").And.Contain("#42").And.Contain("#1,000");

        // One below the dead letter's own sequence, because the progression row records what the shard
        // has already done - the off-by-one that decides whether the poison event is re-applied at all.
        dialog.Should().Contain("#41");

        dialog.Should().Contain("OrderSummary:All").And.Contain("mt_doc_ordersummary");
        dialog.Should().Contain("deletes this projection's dead letters");

        // Type-the-name, because a rewind replays every event after a point through a projection and the
        // row it starts from is one of forty that look alike.
        page.Find(".ms-confirm-input").Should().NotBeNull();
        page.Find(".ms-confirm-label").TextContent.Should().Contain("OrderSummary");
    }

    [Fact]
    public async Task Rewinding_calls_the_service_with_the_dead_letters_own_sequence()
    {
        using StudioComponentContext context = await RewindableAsync();

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);
        await RewindButton(page).ClickAsync(new());

        // The confirm button stays disabled until the projection's own name is typed out.
        page.Find(".ms-confirm-actions button.ms-button-danger").HasAttribute("disabled").Should().BeTrue();

        await page.Find(".ms-confirm-input").InputAsync(new() { Value = "OrderSummary" });
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        // The page hands over the sequence, not the floor: the off-by-one belongs in the service, where
        // it is written down once and audited.
        context.EventData.Rewinds.Should().Equal("OrderSummary/42");
    }

    [Fact]
    public async Task A_refused_rewind_is_shown_on_the_page()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.EventData.MutationFailure = new InvalidOperationException("no async daemon is hosted in this process");

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);
        await RewindButton(page).ClickAsync(new());
        await page.Find(".ms-confirm-input").InputAsync(new() { Value = "OrderSummary" });
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        page.Find(".ms-error-alert").TextContent.Should().Contain("no async daemon is hosted in this process");
    }

    [Fact]
    public async Task A_refused_action_is_shown_on_the_page()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.EventData.MutationFailure = new InvalidOperationException("the write policy said no");

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());
        await page.Find(".ms-dead-letter-actions button.ms-button-danger").ClickAsync(new());
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        page.Find(".ms-error-alert").TextContent.Should().Contain("the write policy said no");
    }

    [Fact]
    public async Task The_filters_in_the_url_reach_the_query()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.Navigate("marten/events/dead-letters?projection=Orders&shard=Orders:All&exception=Divide&offset=50");

        context.Render<DeadLetters>();

        DeadLetterQuery query = context.EventData.DeadLetterQueries[^1];
        query.ProjectionName.Should().Be("Orders");
        query.ShardName.Should().Be("Orders:All");
        query.ExceptionType.Should().Be("Divide");
        query.Offset.Should().Be(50);
    }

    [Fact]
    public async Task No_dead_letters_is_good_news_and_says_so()
    {
        using StudioComponentContext context = await NewContextAsync();

        context.Render<DeadLetters>().Find(".ms-empty-title").TextContent.Should().Be("No dead letters");
    }

    private static AngleSharp.Dom.IElement SkipButton(IRenderedComponent<DeadLetters> page) =>
        page.FindAll(".ms-dead-letter-actions button")
            .Single(x => x.TextContent.Contains("Skip event", StringComparison.Ordinal));

    private static AngleSharp.Dom.IElement RewindButton(IRenderedComponent<DeadLetters> page) =>
        page.FindAll(".ms-dead-letter-actions button")
            .Single(x => x.TextContent.Contains("Rewind", StringComparison.Ordinal));

    private static async Task<IRenderedComponent<DeadLetters>> ExpandAsync(StudioComponentContext context)
    {
        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());
        return page;
    }

    /// <summary>
    /// A context in which the rewind is genuinely available: the capability is on, a daemon is hosted
    /// here, and the projection the dead letter names is registered and async.
    /// </summary>
    private static async Task<StudioComponentContext> RewindableAsync()
    {
        StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.ProjectionData.WithProjection("OrderSummary");
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        return context;
    }

    private static async Task<StudioComponentContext> NewContextAsync()
    {
        var context = new StudioComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
