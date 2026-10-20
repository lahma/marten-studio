using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;
using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Events;

/// <summary>
/// F4 on the feed - the <c>tag:</c> filter, its verdict and the tag chips - and F5 on the Streams box.
/// </summary>
public class FeedTagsAndNaturalKeyPageTests
{
    private static EventTagTypeList CustomerTags(IndexVerdictLevel level = IndexVerdictLevel.Green) =>
        new(
            EventTagStorage.TagTables,
            [
                new EventTagTypeInfo(
                    "CustomerId", "customer", "String", null, "mt_event_tag_customer", true,
                    EventStoreCount.Estimate(100), null, level,
                    level == IndexVerdictLevel.Green ? "Index-backed: pk_mt_event_tag_customer leads with value." : "No index leads with value."),
            ],
            VersionTableExists: false,
            EventStoreCount.Unknown);

    // ------------------------------------------------------------------------------------------------
    // The feed
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_store_without_tag_types_has_no_tag_box()
    {
        using StudioComponentContext context = await ContextAsync();

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed");

        page.FindAll("#ms-feed-tag").Should().BeEmpty();
    }

    [Fact]
    public async Task The_tag_in_the_url_reaches_the_service_parsed_and_its_verdict_is_shown()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { TagStorage = EventTagStorage.TagTables };
            data.TagTypes = CustomerTags();
        });

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed?tag=customer%3Dacme-1");

        page.WaitForAssertion(() => context.EventData.FeedRequests.Should().NotBeEmpty());

        EventTagFilter sent = context.EventData.FeedRequests[^1].Tag!;
        sent.TypeName.Should().Be("customer");
        sent.Value.Should().Be("acme-1");

        page.Find("#ms-feed-tag").GetAttribute("value").Should().Be("customer=acme-1");
        page.Find(".ms-event-tag-filter .ms-verdict-badge").TextContent.Should().Be("indexed");
        page.Find(".ms-event-tag-filter .ms-verdict-badge").ClassList.Should().Contain("ms-verdict-badge-green");
    }

    [Fact]
    public async Task A_red_verdict_says_why_beside_the_box()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { TagStorage = EventTagStorage.TagTables };
            data.TagTypes = CustomerTags(IndexVerdictLevel.Red);
        });

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed?tag=customer%3Dacme-1");

        page.WaitForAssertion(() =>
            page.Find(".ms-event-tag-filter-reason").TextContent.Should().Contain("No index leads with value"));
        page.Find(".ms-event-tag-filter .ms-verdict-badge").TextContent.Should().Be("full scan");
    }

    /// <summary>Text that is not type=value is an answer, not "no filter" - the whole feed would be a lie.</summary>
    [Fact]
    public async Task A_malformed_tag_filter_says_how_to_write_it_and_reads_nothing()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { TagStorage = EventTagStorage.TagTables };
            data.TagTypes = CustomerTags();
        });

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed?tag=acme-1");

        page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("tag:<type>=<value>"));
        context.EventData.FeedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Typing_a_tag_puts_it_in_the_url_without_its_prefix()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { TagStorage = EventTagStorage.TagTables };
            data.TagTypes = CustomerTags();
        });

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed");

        await page.Find("#ms-feed-tag").ChangeAsync(new() { Value = "tag:customer=acme-1" });

        context.CurrentUri.Should().Contain("tag=customer%3Dacme-1");
    }

    [Fact]
    public async Task A_feed_events_tags_are_chips_that_link_to_the_feed_filtered_by_them()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { TagStorage = EventTagStorage.TagTables };
            data.TagTypes = CustomerTags();
            data.Feed = new EventPage([FakeEventDataService.Event(7) with { Tags = [new EventTag("customer", "acme-1")] }], 7, false)
            {
                TagLoad = EventTagLoad.Loaded,
            };
        });

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed");

        page.WaitForAssertion(() => page.FindAll(".ms-event-tag-chip").Should().ContainSingle());
        page.Find(".ms-event-tag-chip a").GetAttribute("href").Should().Contain("tag=customer%3Dacme-1");
    }

    [Fact]
    public async Task Deferred_tags_are_read_for_the_page_only_when_asked()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { TagStorage = EventTagStorage.TagTables };
            data.TagTypes = CustomerTags();
            data.Feed = new EventPage([FakeEventDataService.Event(8), FakeEventDataService.Event(7)], 7, false)
            {
                TagLoad = EventTagLoad.Deferred(EventTagText.Deferred("mt_event_tag_customer", 10_000)),
            };
            data.TagRead = new EventTagRead([new EventTagRow(7, null, new EventTag("customer", "acme-1"))]);
        });

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed");

        page.WaitForAssertion(() => page.Find(".ms-event-tags-note").TextContent.Should().Contain("ExactCountThreshold"));
        context.EventData.TagReads.Should().BeEmpty();

        await page.Find(".ms-event-tags-load").ClickAsync(new());

        page.WaitForAssertion(() => page.FindAll(".ms-event-tag-chip").Should().ContainSingle());
        context.EventData.TagReads.Should().ContainSingle().Which.Should().Equal(8L, 7L);
    }

    /// <summary>
    /// EV-fix 1: follow mode never reads a deferred delta's tags on its own. That read is a scan of every large
    /// tag table, and a tick runs every RefreshInterval; the visitor paid it once by pressing the button, not
    /// once per tick. The new events arrive bare and the note comes back with its button.
    /// </summary>
    [Fact]
    public async Task Follow_mode_shows_a_deferred_deltas_events_untagged_and_offers_the_load_again()
    {
        EventTagLoad deferred = EventTagLoad.Deferred(EventTagText.Deferred("mt_event_tag_customer", 10_000));

        using StudioComponentContext context = await ContextAsync(data =>
        {
            data.Shape = data.Shape with { TagStorage = EventTagStorage.TagTables };
            data.TagTypes = CustomerTags();
            data.Feed = new EventPage([FakeEventDataService.Event(10)], 10, false) { TagLoad = deferred };
            data.TagRead = new EventTagRead([new EventTagRow(10, null, new EventTag("customer", "acme-1"))]);

            // The last answer repeats, so with one value queued the page sees nothing new until the test says so.
            data.HighestSequences.Enqueue(10);
        });

        context.Options.RefreshInterval = TimeSpan.FromMilliseconds(20);

        IRenderedComponent<Feed> page = Render(context, "marten/events/feed?follow=1");

        await page.Find(".ms-event-tags-load").ClickAsync(new());

        page.WaitForAssertion(() => page.FindAll(".ms-event-tags-note").Should().BeEmpty());
        context.EventData.TagReads.Should().ContainSingle();

        // Only now does anything new arrive: one delta, whose tags the service deferred.
        context.EventData.FeedDeltas.Enqueue(new EventPage([FakeEventDataService.Event(12), FakeEventDataService.Event(11)], 11, false)
        {
            TagLoad = deferred,
        });
        context.EventData.HighestSequences.Enqueue(12);

        page.WaitForAssertion(
            () => page.Find(".ms-follow-pill").TextContent.Trim().Should().Be("2 new events"),
            TimeSpan.FromSeconds(10));

        page.Find(".ms-event-tags-note").TextContent.Should().Contain("Load them");
        page.FindAll(".ms-event-tags-load").Should().ContainSingle("the visitor is offered the read again, not given it");
        context.EventData.TagReads.Should().ContainSingle("no tick reads a deferred delta's tags by itself");
    }

    // ------------------------------------------------------------------------------------------------
    // The Streams box
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task On_a_store_with_a_natural_key_the_box_says_so_and_the_value_is_looked_up()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { NaturalKeys = ["Ticket.Number"] };
            data.NaturalKeys = new NaturalKeyLookup(
                [new NaturalKeyMatch("Ticket.Number", FakeEventDataService.Stream("22222222-2222-2222-2222-222222222222", type: "ticket"))],
                ["Ticket.Number"]);
        });

        IRenderedComponent<Streams> page = Render<Streams>(context, "marten/events/streams?id=T-100");

        page.WaitForAssertion(() => page.FindAll(".ms-natural-key-matches .ms-table-row").Should().ContainSingle());

        page.Find("label[for=ms-streams-id]").TextContent.Should().Be("Stream id or natural key");
        page.Find("#ms-streams-id").GetAttribute("placeholder").Should().Contain("Ticket.Number");
        page.Find(".ms-natural-key-matches").TextContent.Should().Contain("Ticket.Number").And.Contain("22222222-2222-2222-2222-222222222222");
        context.EventData.NaturalKeyLookups.Should().Equal("T-100");
    }

    [Fact]
    public async Task A_store_without_natural_keys_is_never_asked_to_look_one_up()
    {
        using StudioComponentContext context = await ContextAsync();

        IRenderedComponent<Streams> page = Render<Streams>(context, "marten/events/streams?id=T-100");

        page.WaitForAssertion(() => context.EventData.StreamRequests.Should().NotBeEmpty());
        context.EventData.NaturalKeyLookups.Should().BeEmpty();
        page.Find("label[for=ms-streams-id]").TextContent.Should().Be("Stream id");
    }

    [Fact]
    public async Task A_value_found_by_neither_says_which_keys_were_tried()
    {
        using StudioComponentContext context = await ContextAsync(static data =>
        {
            data.Shape = data.Shape with { NaturalKeys = ["Ticket.Number"] };
            data.NaturalKeys = new NaturalKeyLookup([], ["Ticket.Number"]);
        });

        IRenderedComponent<Streams> page = Render<Streams>(context, "marten/events/streams?id=T-404");

        page.WaitForAssertion(() =>
            page.Find(".ms-natural-key-none").TextContent.Should().Contain("T-404").And.Contain("Ticket.Number"));
    }

    // ------------------------------------------------------------------------------------------------

    private static IRenderedComponent<Feed> Render(StudioComponentContext context, string uri) => Render<Feed>(context, uri);

    private static IRenderedComponent<TPage> Render<TPage>(StudioComponentContext context, string uri)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        context.Navigate(uri);
        return context.Render<TPage>();
    }

    private static async Task<StudioComponentContext> ContextAsync(Action<FakeEventDataService>? data = null)
    {
        StudioComponentContext context = new();
        data?.Invoke(context.EventData);
        await context.ReadyAsync();
        return context;
    }
}
