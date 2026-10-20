using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using StreamState = MartenStudio.Services.Events.StreamState;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// SEC1 M1 and L2 on Stream detail: nothing drawn for one stream, or under one scope, survives into the next.
/// </summary>
/// <remarks>
/// <para>
/// <b>M1.</b> The page kept the previous stream's state while the next id loaded, so its header, its Archive
/// button and its time-travel panel stayed drawn - and every handler on them acts on <c>Id</c>, which was already
/// the new one. A version step dispatched in that window replayed the new id under the old stream's panel. The
/// service now proves a stream is the scope's before it replays or archives it, which is the enforcement; the page
/// draws nothing of the old stream once the id has moved, which is what keeps the window from existing at all.
/// </para>
/// <para>
/// <b>L2.</b> A scope change left the archive and compact dialogs open, the archive dialog with the id already
/// typed: on a conjoined store the next tenant's stream of the same id was one click away, under a header that had
/// changed. Both close, and a dialog opened again starts from an empty box.
/// </para>
/// </remarks>
public class StreamDetailStreamChangeTests
{
    private const string First = "11111111-aaaa-4111-8111-111111111111";
    private const string Second = "22222222-bbbb-4222-8222-222222222222";

    private const string ConfirmButton = ".ms-confirm-actions button.ms-button-danger";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task Changing_the_id_clears_the_time_travel_panel_before_the_next_stream_is_read()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.StreamStates[First] = Stream(First);
        context.EventData.StreamStates[Second] = Stream(Second);
        context.EventData.Candidates.Add(new AggregateTypeCandidate(
            typeof(TimeTravelOrder), "TimeTravelOrder", "X.TimeTravelOrder", AggregateCandidateSource.SingleStreamProjection));

        context.Navigate(Url(First));
        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();

        page.FindAll(".ms-time-travel").Should().ContainSingle("the premise: the first stream's panel is drawn");
        page.Markup.Should().Contain(First);

        // The next stream's read is held, so the page is caught exactly between the id moving and the answer.
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.EventData.StreamGates[Second] = held;

        // A link to another stream: the address moves, and the router hands the same page its new query parameters.
        context.Navigate(Url(Second));
        page.Render();

        page.Instance.Id.Should().Be(Second, "the premise: the page has the next id");
        page.WaitForAssertion(() => page.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the page waits for the next stream"));
        page.FindAll(".ms-time-travel").Should().BeEmpty(
            "the first stream's panel would replay the second id - nothing drawn for the old stream may carry the new id's handlers");
        page.FindAll(".ms-stream-header").Should().BeEmpty("nor its header");
        page.Markup.Should().NotContain(First, "nothing of the first stream is on screen while the second loads");

        held.SetResult();

        page.WaitForAssertion(() => page.FindAll(".ms-time-travel").Should().ContainSingle("the second stream's own panel, once it is read"));
        page.Find(".ms-stream-header").TextContent.Should().Contain(Second);
    }

    [Fact]
    public async Task A_scope_change_closes_the_archive_dialog_and_what_was_typed_in_it_is_gone()
    {
        using StudioComponentContext context = await TenantedContextAsync();
        context.WithAllCapabilities();
        context.EventData.StreamStates[First] = Stream(First);

        IRenderedComponent<StreamDetail> page = Render(context, First);

        await page.Find("button.ms-stream-archive").ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = First });
        page.Find(ConfirmButton).HasAttribute("disabled").Should().BeFalse("the premise: what was typed would confirm");

        await page.InvokeAsync(() => context.State.SetScopeAsync("default", "localhost.marten", "globex", Token));

        page.WaitForAssertion(() => page.FindAll("button.ms-stream-archive").Should().ContainSingle("globex's stream of that id is on screen"));
        page.FindAll(".ms-confirm-dialog").Should().BeEmpty("a dialog opened under acme's header confirms nothing about globex's stream");
        context.EventData.Archived.Should().BeEmpty();

        await page.Find("button.ms-stream-archive").ClickAsync(new());

        page.Find(".ms-confirm-dialog input").GetAttribute("value").Should().BeNullOrEmpty("opened again, the box starts empty");
        page.Find(ConfirmButton).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task A_scope_change_closes_the_compact_dialog_and_it_does_not_reopen_over_the_next_scopes_stream()
    {
        using StudioComponentContext context = await TenantedContextAsync();
        context.WithAllCapabilities();
        context.EventData.StreamStates[First] = Stream(First);
        context.EventData.CompactionPlans[First] = FakeEventDataService.Offered(First);

        IRenderedComponent<StreamDetail> page = Render(context, First);

        await page.Find(".ms-stream-compact-button").ClickAsync(new());
        page.FindAll(".ms-confirm-dialog").Should().ContainSingle("the premise: the compact dialog is open");

        await page.InvokeAsync(() => context.State.SetScopeAsync("default", "localhost.marten", "globex", Token));

        // The next scope's stream of the same id is offered compaction as well, so a dialog the page still thought was
        // open would be drawn again, over a stream nobody asked to compact.
        page.WaitForAssertion(() => page.FindAll(".ms-stream-compact-button").Should().ContainSingle());
        page.FindAll(".ms-confirm-dialog").Should().BeEmpty("closed by the scope change, and not reopened by the next plan");
        context.EventData.Compactions.Should().BeEmpty();
    }

    /// <summary>
    /// SEC1-fix F3: a replay that answers after the page has moved to another stream is the previous stream's aggregate,
    /// and is not drawn under the next stream's header.
    /// </summary>
    [Fact]
    public async Task A_replay_that_answers_after_the_stream_changed_is_dropped()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.StreamStates[First] = Stream(First);
        context.EventData.StreamStates[Second] = Stream(Second);
        context.EventData.Candidates.Add(new AggregateTypeCandidate(
            typeof(TimeTravelOrder), "TimeTravelOrder", "X.TimeTravelOrder", AggregateCandidateSource.SingleStreamProjection));
        context.EventData.Snapshot = new AggregateSnapshot("TimeTravelOrder", 2, "{\"first\":1}", true);

        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.EventData.ReplayGate = held;

        context.Navigate(Url(First) + "&agg=X.TimeTravelOrder&v=2");
        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();

        // Not awaited: the replay waits on the gate, and the click's task with it.
        Task replaying = page.Find(".ms-time-travel button.ms-button-primary").ClickAsync(new());

        context.Navigate(Url(Second) + "&agg=X.TimeTravelOrder&v=2");
        page.Render();
        page.WaitForAssertion(() => page.Find(".ms-stream-header").TextContent.Should().Contain(Second));

        held.SetResult();
        await replaying;

        page.WaitForAssertion(() => page.FindAll(".ms-time-travel button.ms-button-primary").Should().NotBeEmpty());
        page.FindAll(".ms-time-travel-json").Should().BeEmpty("the first stream's aggregate is not drawn under the second stream's header");
    }

    /// <summary>
    /// SEC1-fix F3: Load more during a reload of the same stream. The flag that offered it was the previous load's, so the
    /// first window was fetched twice and two rows shared one <c>@key</c>, which ends the circuit. The reload offers no
    /// Load more until its own first window says there is more.
    /// </summary>
    [Fact]
    public async Task Load_more_is_not_offered_while_the_same_stream_reloads()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.StreamStates[First] = Stream(First);

        EventPage window = new([FakeEventDataService.Event(1, version: 1), FakeEventDataService.Event(2, version: 2)], 2, true);
        context.EventData.StreamWindows.Enqueue(window);
        context.EventData.StreamWindows.Enqueue(window);
        context.EventData.StreamWindows.Enqueue(window);

        IRenderedComponent<StreamDetail> page = Render(context, First);
        page.FindAll(".ms-timeline-more button").Should().ContainSingle("the premise: the first window says there is more");

        // An archive reloads the same stream; its read is held, so the page is caught mid-reload.
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.EventData.StreamGates[First] = held;

        await page.Find("button.ms-stream-archive").ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = First });
        Task archiving = page.Find(ConfirmButton).ClickAsync(new());

        page.WaitForAssertion(() => page.FindAll(".ms-event-card").Should().BeEmpty("the reload cleared the timeline"));
        page.FindAll(".ms-timeline-more button").Should().BeEmpty("nothing is known yet about this load's windows");

        held.SetResult();
        await archiving;

        page.WaitForAssertion(() => page.FindAll(".ms-event-card").Should().HaveCount(2, "the first window once, not twice"));
    }

    /// <summary>
    /// SEC1-fix F5: on a conjoined event store with "All tenants" selected, Archive is disabled with the sentence Compact
    /// uses, because the service refuses it - Marten would archive the default tenant's stream of the id.
    /// </summary>
    [Fact]
    public async Task Archive_on_a_conjoined_event_store_waits_for_a_tenant_as_Compact_does()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.Shape = context.EventData.Shape with { EventsConjoined = true, HasTenantId = true };
        context.EventData.StreamStates[First] = Stream(First) with { TenantId = "acme" };

        IRenderedComponent<StreamDetail> page = Render(context, First);

        page.Find("button.ms-stream-archive").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-stream-archive-tenant").TextContent
            .Should().Be(StreamCompactionRules.WriteNeedsTenant("an archive", "acme"));
        StreamCompactionRules.TenantRequired("acme").Should().Be(StreamCompactionRules.WriteNeedsTenant("a compaction", "acme"),
            "one sentence for both writes");
    }

    /// <summary>
    /// SEC1-fix2: under per-tenant event sequences, "All tenants" can list two tenants' events with one <c>seq_id</c>.
    /// Keyed by sequence alone the two rows were siblings under one <c>@key</c>, which ends the circuit; they are keyed
    /// by tenant and sequence, on Stream detail and on the feed, and render twice as two cards, re-render included.
    /// </summary>
    [Fact]
    public async Task Two_tenants_events_with_one_sequence_are_two_rows_on_the_timeline_and_the_feed()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.StreamStates[First] = Stream(First);

        EventRow acme = FakeEventDataService.Event(5, version: 1, streamId: First) with { TenantId = "acme" };
        EventRow globex = FakeEventDataService.Event(5, version: 1, streamId: First) with { TenantId = "globex" };
        context.EventData.StreamWindows.Enqueue(new EventPage([acme, globex], 5, false));
        context.EventData.Feed = new EventPage([acme, globex], 5, false);

        IRenderedComponent<StreamDetail> page = Render(context, First);
        page.FindAll(".ms-event-card").Should().HaveCount(2);
        page.Invoking(static x => x.Render()).Should().NotThrow("a second render diffs the keys, which is where a duplicate throws");

        IRenderedComponent<Feed> feed = context.Render<Feed>();
        feed.FindAll(".ms-event-card").Should().HaveCount(2);
        feed.Invoking(static x => x.Render()).Should().NotThrow();
    }

    private static StreamState Stream(string id) =>
        new(
            id,
            Exists: true,
            "Order",
            3,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            IsArchived: false,
            TenantId: null);

    private static string Url(string id) => "marten/events/streams/s?id=" + id;

    private static IRenderedComponent<StreamDetail> Render(StudioComponentContext context, string id)
    {
        context.Navigate(Url(id));
        return context.Render<StreamDetail>();
    }

    private static async Task<StudioComponentContext> NewContextAsync()
    {
        var context = new StudioComponentContext();
        await context.ReadyAsync();
        return context;
    }

    /// <summary>A context whose database has two tenants, scoped to <c>acme</c>.</summary>
    private static async Task<StudioComponentContext> TenantedContextAsync()
    {
        StudioComponentContext context = await NewContextAsync();
        context.Catalog.WithTenants("default", new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured));

        // The context settled its scope before the tenants existed; re-read, then pick acme.
        await context.State.RefreshScopeListingsAsync(Token);
        await context.State.SetScopeAsync("default", "localhost.marten", "acme", Token);
        context.State.ActiveScope!.TenantId.Should().Be("acme", "the premise");

        return context;
    }
}
