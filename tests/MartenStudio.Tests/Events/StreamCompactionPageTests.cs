using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using StreamState = MartenStudio.Services.Events.StreamState;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// F3 on Stream detail: where the Compact section appears, the capability and the write policy beside it,
/// what the dialog says, and that what was typed - not the page's own copy of the id - reaches the service.
/// </summary>
/// <remarks>
/// None of this is the enforcement. <c>EventDataService.CompactStreamAsync</c> refuses on the capability,
/// the write policy and the typed confirmation regardless of what was drawn (hard rule 5), which
/// <c>CapabilityGatingMatrixTests</c> and <c>StreamCompactionLiveTests</c> are about.
/// </remarks>
public class StreamCompactionPageTests
{
    private const string WritePolicy = "MartenStudioWriter";
    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    private const string CompactButton = ".ms-stream-compact-button";
    private const string ConfirmButton = ".ms-confirm-actions button.ms-button-danger";

    [Fact]
    public async Task A_stream_the_plan_does_not_offer_has_no_compact_section_at_all()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();

        IRenderedComponent<StreamDetail> page = Render(context);

        page.FindAll(".ms-stream-compact").Should().BeEmpty(
            "the fake's default plan is an untyped stream, and compaction means nothing for one");
    }

    [Fact]
    public async Task An_offered_stream_shows_the_section_with_a_live_button()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId, version: 3);

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Find(".ms-stream-compact .ms-section-title").TextContent.Should().Be("Compact");
        page.Find(".ms-stream-compact").TextContent.Should().Contain("Compacted<OrderSummary>");
        page.Find(CompactButton).HasAttribute("disabled").Should().BeFalse();
        page.FindAll(".ms-stream-compact .ms-write-refusal").Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_capability_off_the_section_names_the_option_rather_than_offering_a_button()
    {
        using StudioComponentContext context = new();
        await context.ReadyAsync();
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId);

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Find(".ms-stream-compact .ms-capability-disabled").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.CompactStreams");
        page.FindAll(CompactButton).Should().BeEmpty();
    }

    /// <summary>Disabled and not hidden, the same rule Archive keeps: the studio can, the account may not.</summary>
    [Fact]
    public async Task A_visitor_the_write_policy_refuses_sees_the_button_disabled_and_why()
    {
        using StudioComponentContext context = await ContextAsync(static resource => resource.Capability is null);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId);

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Find(CompactButton).HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-stream-compact .ms-write-refusal").TextContent
            .Should().Be(WritePolicyRefusal.For(StudioCapability.CompactStreams));
        page.FindAll(".ms-stream-compact .ms-capability-disabled").Should().BeEmpty();
    }

    [Fact]
    public async Task On_a_conjoined_store_with_no_tenant_picked_the_button_waits_for_the_tenant()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream(tenantId: "acme");
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId, tenantId: "acme", needsTenant: true);

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Find(CompactButton).HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-stream-compact-tenant").TextContent.Should().Contain("tenant acme");
    }

    /// <summary>
    /// What the dialog says is <see cref="StreamCompactionRules.WhatHappens" />, sentence for sentence - and
    /// the button stays dead until the stream's own id has been typed out.
    /// </summary>
    [Fact]
    public async Task The_dialog_says_what_compaction_does_and_waits_for_the_id()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        CompactionPlan plan = FakeEventDataService.Offered(StreamId, version: 3, tagTables: true);
        context.EventData.CompactionPlans[StreamId] = plan;

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find(CompactButton).ClickAsync(new());

        IElement dialog = page.Find(".ms-confirm-dialog");
        dialog.GetAttribute("aria-label").Should().Be("Compact this stream?");

        page.FindAll(".ms-compact-fact").Select(static x => x.TextContent)
            .Should().Equal(StreamCompactionRules.WhatHappens(plan));

        page.Find(ConfirmButton).HasAttribute("disabled").Should().BeTrue();

        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = "not-the-id" });
        page.Find(ConfirmButton).HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// The service checks the typed text again, and that check only means something if the text it gets came
    /// out of the input box - so what reaches the fake is what was typed, trimmed, not the page's copy.
    /// </summary>
    [Fact]
    public async Task What_was_typed_travels_to_the_service()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId);

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find(CompactButton).ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = "  " + StreamId + " " });
        await page.Find(ConfirmButton).ClickAsync(new());

        page.WaitForAssertion(() => context.EventData.Compactions.Should().Equal(StreamId + "/" + StreamId + "/3"));
    }

    /// <summary>
    /// What runs is bounded by what the dialog described: the plan's version travels with the typed id, so an
    /// event appended while the dialog was open is not folded into something nobody read.
    /// </summary>
    [Fact]
    public async Task The_version_the_dialog_described_travels_to_the_service()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId, version: 7);

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find(CompactButton).ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });
        await page.Find(ConfirmButton).ClickAsync(new());

        page.WaitForAssertion(() => context.EventData.Compactions.Should().Equal(StreamId + "/" + StreamId + "/7"));
    }

    /// <summary>The plan is made from the page's own read of the stream, not a second read of the same row.</summary>
    [Fact]
    public async Task The_plan_is_made_from_the_stream_the_page_already_read()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        StreamState stream = Stream();
        context.EventData.StreamStates[StreamId] = stream;

        Render(context);

        context.EventData.CompactionDescriptions.Should().ContainSingle().Which.Should().BeSameAs(stream);
    }

    [Fact]
    public async Task A_refused_compaction_is_shown_on_the_page_and_not_only_in_a_toast()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId);
        context.EventData.MutationFailure = new InvalidOperationException(StreamCompactionRules.TaggedStreamRefused("mt_event_tag_customer"));

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find(CompactButton).ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });
        await page.Find(ConfirmButton).ClickAsync(new());

        page.WaitForAssertion(() =>
            page.Find(".ms-stream-compact .ms-error-alert").TextContent.Should().Contain("mt_event_tag_customer"));
    }

    /// <summary>
    /// B1. A failure after the confirmation can come after a save, so the page reads the stream again rather
    /// than go on showing events that may be gone - and the message stays on screen even when the reloaded
    /// stream is no longer offered for compaction, which is exactly what a compaction that did happen leaves.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_confirmation_reloads_the_stream_and_keeps_its_message()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId);
        context.EventData.MutationFailure = new InvalidOperationException("Nothing was compacted: mt_streams.compacted_version is still 0.");

        IRenderedComponent<StreamDetail> page = Render(context);
        context.EventData.CompactionDescriptions.Should().ContainSingle();

        await page.Find(CompactButton).ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });
        context.EventData.CompactionPlans.Remove(StreamId);
        await page.Find(ConfirmButton).ClickAsync(new());

        page.WaitForAssertion(() => context.EventData.CompactionDescriptions.Should().HaveCount(2, "the page read the stream again"));
        page.Find(".ms-stream-compact .ms-error-alert").TextContent.Should().Contain("Nothing was compacted");
        page.FindAll(CompactButton).Should().BeEmpty("the reloaded stream is not offered, and its result is still shown");
    }

    /// <summary>
    /// What happened is the service's read-back, not "compacted" - on a skipping store it can be a compaction
    /// through an earlier version than the dialog's, and the page says which.
    /// </summary>
    [Fact]
    public async Task A_compaction_shows_what_the_service_read_back()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId, version: 5);
        context.EventData.CompactionOutcome =
            "compacted through version 4 into one Compacted<Order> event, not through version 5: version 5 is a skipped event";

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find(CompactButton).ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });
        context.EventData.CompactionPlans.Remove(StreamId);
        await page.Find(ConfirmButton).ClickAsync(new());

        page.WaitForAssertion(() =>
            page.Find(".ms-stream-compact-outcome").TextContent.Should().Be(
                "Stream " + StreamId + " is compacted through version 4 into one Compacted<Order> event, not through version 5: "
                + "version 5 is a skipped event."));
        context.EventData.CompactionDescriptions.Should().HaveCount(2);
        page.FindAll(".ms-stream-compact .ms-error-alert").Should().BeEmpty();
    }

    /// <summary>
    /// A save whose result could not be read back is neither a success nor a refusal: a warning that says
    /// "saved, not confirmed", never the error alert, and the page reloads.
    /// </summary>
    [Fact]
    public async Task A_compaction_that_could_not_be_confirmed_is_a_warning_and_not_an_error()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId);
        context.EventData.MutationFailure = new StreamCompactionUnconfirmedException(
            StreamCompactionRules.Unconfirmed("the stream could not be read back - connection refused"));

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find(CompactButton).ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });
        await page.Find(ConfirmButton).ClickAsync(new());

        page.WaitForAssertion(() =>
            page.Find(".ms-stream-compact-unconfirmed").TextContent.Should().StartWith("Compaction was saved, not confirmed"));
        page.Find(".ms-stream-compact-unconfirmed").ClassList.Should().Contain("ms-alert-warning");
        page.FindAll(".ms-stream-compact .ms-error-alert").Should().BeEmpty();
        context.EventData.CompactionDescriptions.Should().HaveCount(2);
    }

    // ------------------------------------------------------------------------------------------------
    // Tags on the timeline
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_timeline_event_shows_its_tags_each_linking_to_the_feed_filtered_by_it()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.StreamWindows.Enqueue(new EventPage(
            [FakeEventDataService.Event(1) with { Tags = [new EventTag("customer", "acme-1")] }], 1, false)
        {
            TagLoad = EventTagLoad.Loaded,
        });

        IRenderedComponent<StreamDetail> page = Render(context);

        IElement chip = page.Find(".ms-event-tag-chip");
        chip.QuerySelector(".ms-event-meta-key")!.TextContent.Should().Be("customer");
        chip.QuerySelector("a")!.GetAttribute("href").Should().Contain("events/feed").And.Contain("tag=customer%3Dacme-1");
        page.FindAll(".ms-event-tags-note").Should().BeEmpty();
    }

    [Fact]
    public async Task Deferred_tags_are_offered_and_loaded_on_request()
    {
        using StudioComponentContext context = await ContextAsync(static _ => true);
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.StreamWindows.Enqueue(new EventPage([FakeEventDataService.Event(1)], 1, false)
        {
            TagLoad = EventTagLoad.Deferred(EventTagText.Deferred("mt_event_tag_customer", 10_000)),
        });
        context.EventData.TagRead = new EventTagRead([new EventTagRow(1, null, new EventTag("customer", "acme-1"))]);

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Find(".ms-event-tags-note").TextContent.Should().Contain("mt_event_tag_customer");
        page.FindAll(".ms-event-tag-chip").Should().BeEmpty("nothing is read until the visitor asks");

        await page.Find(".ms-event-tags-load").ClickAsync(new());

        page.WaitForAssertion(() => page.FindAll(".ms-event-tag-chip").Should().ContainSingle());
        context.EventData.TagReads.Should().ContainSingle().Which.Should().Equal(1L);
        page.FindAll(".ms-event-tags-note").Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------

    private static StreamState Stream(string? tenantId = null) =>
        new(
            StreamId,
            Exists: true,
            "order_summary",
            3,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            false,
            tenantId);

    private static IRenderedComponent<StreamDetail> Render(StudioComponentContext context)
    {
        context.Navigate("marten/events/streams/s?id=" + StreamId);
        return context.Render<StreamDetail>();
    }

    private static async Task<StudioComponentContext> ContextAsync(Func<MartenStoreResource, bool> rule)
    {
        StudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.Allow(rule);

        await context.ReadyAsync();
        return context;
    }
}
