using Bunit;

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
        using EventsComponentContext context = await NewContextAsync();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        string row = page.Find("tbody tr").TextContent;
        row.Should().Contain("OrderSummary").And.Contain("OrderSummary:All").And.Contain("#42")
            .And.Contain("System.DivideByZeroException");
    }

    [Fact]
    public async Task The_expansion_shows_the_exception_and_the_event_that_failed()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.Data.EventBySequence = FakeEventDataService.Event(42, type: "OrderShipped");

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        page.Find(".ms-dead-letter-exception").TextContent.Should().Contain("Attempted to divide by zero.");
        page.TextOfAll(".ms-event-chip").Should().Contain("OrderShipped");
    }

    [Fact]
    public async Task An_event_that_is_no_longer_there_says_so_rather_than_showing_nothing()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.Data.EventBySequence = null;

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
        using EventsComponentContext context = await NewContextAsync();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

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
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        await page.Find(".ms-dead-letter-actions button.ms-button-danger").ClickAsync(new());

        page.Find(".ms-confirm-dialog").TextContent.Should().Contain("projection is not restarted");

        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        context.Data.Discarded.Should().Equal(LetterId);
    }

    [Fact]
    public async Task Skipping_an_event_confirms_and_then_marks_the_sequence()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.Shape = context.Data.Shape with { HasIsSkipped = true };
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId, sequence: 77)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        await SkipButton(page).ClickAsync(new());
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        context.Data.Skipped.Should().Equal(77L);
    }

    /// <summary>
    /// A store without <c>mt_events.is_skipped</c> cannot express the idea at all, so the button is
    /// disabled and says why rather than failing at the click.
    /// </summary>
    [Fact]
    public async Task Skipping_is_disabled_when_the_store_does_not_record_skipped_events()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        AngleSharp.Dom.IElement skip = SkipButton(page);
        skip.HasAttribute("disabled").Should().BeTrue();
        skip.GetAttribute("title").Should().Contain("is_skipped");
    }

    /// <summary>
    /// The third action the design names needs a running daemon, which this page never reaches for. It
    /// ships disabled and says where it is going.
    /// </summary>
    [Fact]
    public async Task Rewinding_is_rendered_disabled_and_says_when_it_arrives()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        AngleSharp.Dom.IElement rewind = page.FindAll(".ms-dead-letter-actions button")
            .Single(x => x.TextContent.Contains("Rewind", StringComparison.Ordinal));

        rewind.HasAttribute("disabled").Should().BeTrue();
        rewind.GetAttribute("title").Should().Be("arrives with the projections page");
    }

    [Fact]
    public async Task A_refused_action_is_shown_on_the_page()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.Data.MutationFailure = new InvalidOperationException("the write policy said no");

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());
        await page.Find(".ms-dead-letter-actions button.ms-button-danger").ClickAsync(new());
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        page.Find(".ms-error-alert").TextContent.Should().Contain("the write policy said no");
    }

    [Fact]
    public async Task The_filters_in_the_url_reach_the_query()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Navigate("marten/events/dead-letters?projection=Orders&shard=Orders:All&exception=Divide&offset=50");

        context.Render<DeadLetters>();

        DeadLetterQuery query = context.Data.DeadLetterQueries[^1];
        query.ProjectionName.Should().Be("Orders");
        query.ShardName.Should().Be("Orders:All");
        query.ExceptionType.Should().Be("Divide");
        query.Offset.Should().Be(50);
    }

    [Fact]
    public async Task No_dead_letters_is_good_news_and_says_so()
    {
        using EventsComponentContext context = await NewContextAsync();

        context.Render<DeadLetters>().Find(".ms-empty-title").TextContent.Should().Be("No dead letters");
    }

    private static AngleSharp.Dom.IElement SkipButton(IRenderedComponent<DeadLetters> page) =>
        page.FindAll(".ms-dead-letter-actions button")
            .Single(x => x.TextContent.Contains("Skip event", StringComparison.Ordinal));

    private static async Task<EventsComponentContext> NewContextAsync()
    {
        var context = new EventsComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
