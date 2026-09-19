using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The event card itself: compact by default, with the viewer's toolbar one press away.
/// </summary>
/// <remarks>
/// A feed is fifty of these. The card used to embed the whole JSON viewer - Tree/Raw, Expand all,
/// Collapse all, the depth stepper, Copy JSON and Download - once per event, which put seven controls
/// on every card and made a 14 919px page out of fifty events. What a reader needs from the list is the
/// body copied and, on the one card they are investigating, the rest; everything else belongs behind
/// "Event actions".
/// </remarks>
public class EventCardTests
{
    private const string Body = """{"total":10}""";

    /// <summary>What <c>JsonPrettyPrinter</c> makes of <see cref="Body" />, LF and two-space indented.</summary>
    private const string PrettyBody = "{\n  \"total\": 10\n}";

    [Fact]
    public async Task A_card_shows_its_body_without_a_toolbar_and_without_a_search_bar()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(context);

        card.FindAll(".ms-json-toolbar").Should().BeEmpty("fifty toolbars is not a page anybody can scan");
        card.FindAll(".ms-json-search").Should().BeEmpty();

        // The body itself is still there, expanded one level, which is the whole point of the card.
        card.FindAll("[role=treeitem]").Should().NotBeEmpty();
        card.Find(".ms-json-tree").TextContent.Should().Contain("total");
    }

    [Fact]
    public async Task More_reveals_the_viewers_own_toolbar_and_says_so_on_the_button()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(context);

        IElement more = card.Find(".ms-event-card-more");
        more.GetAttribute("aria-label").Should().Be("Event actions");
        more.GetAttribute("aria-expanded").Should().Be("false");

        await more.ClickAsync(new());

        card.Find(".ms-event-card-more").GetAttribute("aria-expanded").Should().Be("true");
        card.FindAll(".ms-json-toolbar").Should().ContainSingle();

        // Every control the card used to carry on its own is in there, and only there.
        string toolbar = card.Find(".ms-json-toolbar").TextContent;
        toolbar.Should().Contain("Tree").And.Contain("Raw").And.Contain("Expand all")
            .And.Contain("Collapse all").And.Contain("Copy JSON").And.Contain("Download");

        await card.Find(".ms-event-card-more").ClickAsync(new());

        card.Find(".ms-event-card-more").GetAttribute("aria-expanded").Should().Be("false");
        card.FindAll(".ms-json-toolbar").Should().BeEmpty();
    }

    /// <summary>
    /// The tree carries the border and the corners the toolbar was carrying, so a card without one is
    /// not a box with its lid off.
    /// </summary>
    [Fact]
    public async Task The_body_is_a_closed_box_when_the_toolbar_is_not_there()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(context);

        card.Find(".ms-json").ClassList.Should().Contain("ms-event-json-bare");

        await card.Find(".ms-event-card-more").ClickAsync(new());

        card.Find(".ms-json").ClassList.Should().NotContain("ms-event-json-bare");
    }

    [Fact]
    public async Task Copy_puts_the_event_body_on_the_clipboard_through_the_shared_helper()
    {
        using StudioComponentContext context = await NewContextAsync();

        // A browser that took the text answers true; the loose runtime answers default(bool), which is
        // the refusal case the next test is about.
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        IRenderedComponent<EventCard> card = Render(context);

        IElement copy = card.Find(".ms-event-card-copy");
        copy.GetAttribute("aria-label").Should().Be("Copy event JSON");

        await copy.ClickAsync(new());

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be(PrettyBody, "the card and the toolbar must copy the same bytes");

        // The flash is on the button, and the accessible name never moves.
        card.Find(".ms-event-card-copy").ClassList.Should().Contain("ms-event-card-action-copied");
        card.Find(".ms-event-card-copy").GetAttribute("aria-label").Should().Be("Copy event JSON");
        card.Find(".ms-event-card-copy").GetAttribute("title").Should().Be("Copied");
    }

    /// <summary>
    /// A browser that refused the clipboard is not a copy that happened; the button must not claim one.
    /// </summary>
    [Fact]
    public async Task A_refused_clipboard_does_not_flash_copied()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(false);

        IRenderedComponent<EventCard> card = Render(context);

        await card.Find(".ms-event-card-copy").ClickAsync(new());

        card.Find(".ms-event-card-copy").ClassList.Should().NotContain("ms-event-card-action-copied");
    }

    /// <summary>
    /// A closed tab throws <c>JSDisconnectedException</c> out of the clipboard call, and it derives from
    /// <see cref="Exception" /> rather than from <c>JSException</c>: the exception filter is what keeps
    /// that from taking the render with it.
    /// </summary>
    [Fact]
    public async Task A_circuit_that_is_already_gone_does_not_take_the_card_with_it()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.JSInterop.Disconnect<bool>("martenStudio.clipboard.copyText");

        IRenderedComponent<EventCard> card = Render(context);

        Func<Task> copy = async () => await card.Find(".ms-event-card-copy").ClickAsync(new());

        await copy.Should().NotThrowAsync();
        card.Find(".ms-event-card-copy").ClassList.Should().NotContain("ms-event-card-action-copied");
    }

    /// <summary>
    /// In a stream's timeline the card itself is the selector, so neither button may also select it -
    /// the type chip already stops the bubble for the same reason.
    /// </summary>
    [Fact]
    public async Task Neither_button_selects_the_card_it_sits_on()
    {
        using StudioComponentContext context = await NewContextAsync();
        List<long> selected = [];

        IRenderedComponent<EventCard> card = Render(
            context,
            parameters => parameters.Add(
                c => c.OnSelect,
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create<long>(this, selected.Add)));

        await card.Find(".ms-event-card-copy").ClickAsync(new());
        await card.Find(".ms-event-card-more").ClickAsync(new());

        selected.Should().BeEmpty();

        // The card itself still selects, or the test above proves nothing.
        await card.Find(".ms-event-card").ClickAsync(new());
        selected.Should().Equal(3);
    }

    /// <summary>An event with no JSON has nothing to copy and nothing to open; both buttons say so.</summary>
    [Fact]
    public async Task A_binary_event_offers_neither_action()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(
            context,
            parameters => parameters
                .Add(c => c.HasBinary, true)
                .Add(c => c.BinaryLength, 4_096L),
            json: null);

        card.Find(".ms-event-card-copy").HasAttribute("disabled").Should().BeTrue();
        card.Find(".ms-event-card-more").HasAttribute("disabled").Should().BeTrue();
        card.Find(".ms-event-card-copy").GetAttribute("aria-label").Should().Be("Copy event JSON");
        card.Find(".ms-event-binary").TextContent.Should().Contain("binary payload, 4,096 bytes");
    }

    // ------------------------------------------------------------------------------------------------
    // Metadata chips
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_dotnet_type_chip_shows_the_short_name_and_keeps_the_whole_of_it_in_the_title()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(
            context,
            parameters => parameters.Add(
                c => c.Metadata,
                Meta(("mt_dotnet_type", "MartenStudio.SampleDomain.Events.OrderShipped, MartenStudio.SampleDomain"))));

        IElement value = card.Find(".ms-event-meta-chip .ms-event-meta-value");

        value.TextContent.Trim().Should().Be("OrderShipped");
        value.GetAttribute("title").Should()
            .Be("MartenStudio.SampleDomain.Events.OrderShipped, MartenStudio.SampleDomain");
    }

    /// <summary>
    /// Marten writes <c>$"{eventType.FullName}, {assembly}"</c>, and a closed generic's
    /// <see cref="Type.FullName" /> carries every argument assembly-qualified inside <c>[[ ]]</c>.
    /// Taking the text after the last dot of that answers <c>DailySales</c> for a
    /// <c>Compacted&lt;DailySales&gt;</c>, which names the wrong type.
    /// </summary>
    [Theory]
    [InlineData("MartenStudio.SampleDomain.Events.OrderShipped, MartenStudio.SampleDomain", "OrderShipped")]
    [InlineData("OrderShipped", "OrderShipped")]
    [InlineData("MartenStudio.SampleDomain.Events.Outer+Nested, MartenStudio.SampleDomain", "Nested")]
    [InlineData(
        "JasperFx.Events.Daemon.Compacted`1[[MartenStudio.SampleDomain.Projections.DailySales, MartenStudio.SampleDomain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], JasperFx.Events",
        "Compacted<DailySales>")]
    [InlineData(
        "X.Pair`2[[A.First, A],[B.Second, B]], X",
        "Pair<First, Second>")]
    public async Task A_dotnet_type_name_shortens_to_something_a_reader_recognises(string stored, string expected)
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(
            context,
            parameters => parameters.Add(c => c.Metadata, Meta(("mt_dotnet_type", stored))));

        card.Find(".ms-event-meta-chip .ms-event-meta-value").TextContent.Trim().Should().Be(expected);
    }

    /// <summary>Only that one column is rewritten; every other chip still reads as the column holds it.</summary>
    [Fact]
    public async Task Every_other_chip_shows_the_column_exactly_as_it_is_stored()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(
            context,
            parameters => parameters.Add(
                c => c.Metadata,
                Meta(
                    ("causation_id", "SampleDataSeeder.SeedEventsAsync"),
                    ("headers", """{"source": "marten-studio-sample"}"""))));

        card.TextOfAll(".ms-event-meta-chip .ms-event-meta-value").Should()
            .Equal("SampleDataSeeder.SeedEventsAsync", """{"source": "marten-studio-sample"}""");
    }

    // ------------------------------------------------------------------------------------------------
    // The identity line
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The stream used to own a row of its own, which cost a line on every card in the feed. It is part
    /// of "which event is this", so it rides on the identity line with everything else.
    /// </summary>
    [Fact]
    public async Task The_stream_rides_on_the_identity_line_rather_than_a_row_of_its_own()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(
            context,
            parameters => parameters
                .Add(c => c.ShowStream, true)
                .Add(c => c.StreamId, "order-17")
                .Add(c => c.StreamHref, "events/streams/s?id=order-17"));

        card.FindAll(".ms-event-card-header").Should().ContainSingle();

        IElement header = card.Find(".ms-event-card-header");
        header.QuerySelector(".ms-event-card-stream a")!.GetAttribute("href").Should()
            .Be("events/streams/s?id=order-17");
        header.QuerySelector(".ms-event-card-seq")!.TextContent.Should().Be("#7");
    }

    [Fact]
    public async Task A_timeline_card_names_no_stream_at_all()
    {
        using StudioComponentContext context = await NewContextAsync();

        IRenderedComponent<EventCard> card = Render(
            context,
            parameters => parameters.Add(c => c.ShowStream, false).Add(c => c.StreamId, "order-17"));

        card.FindAll(".ms-event-card-stream").Should().BeEmpty();
    }

    private static Dictionary<string, string?> Meta(params (string Key, string? Value)[] entries)
    {
        Dictionary<string, string?> map = new(StringComparer.Ordinal);
        foreach ((string key, string? value) in entries)
        {
            map[key] = value;
        }

        return map;
    }

    private static IRenderedComponent<EventCard> Render(
        StudioComponentContext context,
        Action<ComponentParameterCollectionBuilder<EventCard>>? extra = null,
        string? json = Body) =>
        context.Render<EventCard>(parameters =>
        {
            parameters
                .Add(c => c.Sequence, 7)
                .Add(c => c.Version, 3)
                .Add(c => c.EventType, "item_added")
                .Add(c => c.DotNetType, "MartenStudio.SampleDomain.Events.ItemAdded")
                .Add(c => c.Timestamp, new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero))
                .Add(c => c.Json, json);

            extra?.Invoke(parameters);
        });

    private static async Task<StudioComponentContext> NewContextAsync()
    {
        var context = new StudioComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
