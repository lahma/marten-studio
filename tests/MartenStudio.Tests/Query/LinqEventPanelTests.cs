using Bunit;

using JasperFx.Events;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;

using Microsoft.AspNetCore.Components.Web;

using QueryPage = MartenStudio.Components.Pages.Query.Query;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The Query page's LINQ tab over the event log (1.1), through a fake service: the picker, the toolbar, the two notes that
/// say what is and is not offered, the Feed's own card for each row, and a deep link that fills the boxes and never runs.
/// </summary>
public class LinqEventPanelTests
{
    private static readonly LinqEventTargetInfo Info = new(
        Conjoined: true,
        PerTenantSequences: false,
        StreamIdentity.AsGuid,
        ["Sequence", "Timestamp", "EventTypeName", "StreamId", "Version", "TenantId", "CorrelationId", "DotNetTypeName", "Id"],
        [
            new LinqWithheldMember("IsArchived", "Use the Archived selector: Marten drops its own archive filter when a predicate names IsArchived."),
            new LinqWithheldMember("CausationId", "Not recorded by this store: StoreOptions.Events.MetadataConfig.CausationIdEnabled is false."),
            new LinqWithheldMember("Data, Headers, Tags, EventType", "The event body and headers are not queryable here: see docs/linq-mode.md#events."),
        ],
        "Sequence desc",
        "public");

    private static FakeLinqQueryService WithEvents(FakeLinqQueryService linq, LinqEventTargetInfo? info = null)
    {
        linq.Status = linq.Status with { Events = info ?? Info, MayRun = true, MayExplain = true };

        return linq;
    }

    private static StudioComponentContext CreateContext(FakeLinqQueryService linq)
    {
        var context = new StudioComponentContext();
        context.WithStores("default");
        context.Options.Capabilities.RunLinqQueries = true;
        context.Services.AddFallbackServiceProvider(new Services(new FakeQueryService(), linq));

        return context;
    }

    private sealed class Services(FakeQueryService queries, FakeLinqQueryService linq) : IServiceProvider
    {
        public object? GetService(Type requested) =>
            requested == typeof(IQueryService) ? queries
            : requested == typeof(ILinqQueryService) ? linq
            : null;
    }

    private static LinqEventRow Row(long sequence, string stream = "aaaaaaaa-0000-0000-0000-000000000001", string? tenant = "acme", string type = "order_placed") =>
        new(sequence, Guid.NewGuid().ToString(), stream, 1, type, "App.Events.OrderPlaced, App", DateTimeOffset.UtcNow, tenant, IsArchived: false,
            CorrelationId: "c-1", CausationId: null, UserName: "ada", HasBinary: false, Json: """{"Total":5}""", NotInlined: null);

    private static LinqQueryResult Result(LinqEventScope scope, params LinqEventRow[] rows) =>
        new(LinqQueryService.EventsAlias, [], false, 0, 50, "select data from public.mt_events as d", [], TimeSpan.FromMilliseconds(2))
        {
            EventRows = rows,
            EventScope = scope,
        };

    // ------------------------------------------------------------------------------------------------
    // The picker
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_picker_has_a_documents_group_and_an_event_store_group_and_defaults_to_the_first_document_type()
    {
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService().WithType("order")));

        var panel = context.Render<LinqQueryPanel>();

        panel.Find("label[for=ms-query-linq-type]").TextContent.Should().Be("Query");
        panel.FindAll("#ms-query-linq-type optgroup").Select(static x => x.GetAttribute("label")).Should().Equal("Documents", "Event store");
        panel.FindAll("#ms-query-linq-type optgroup")[1].QuerySelector("option")!.TextContent.Should().Contain("Events (mt_events)");
        panel.Find("#ms-query-linq-type").GetAttribute("value").Should().Be("order");
        panel.FindAll("#ms-query-linq-archived").Should().BeEmpty("the archived selector belongs to the events");
    }

    [Fact]
    public void With_no_document_type_the_events_are_the_default_and_the_only_choice()
    {
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService()));

        var panel = context.Render<LinqQueryPanel>();

        panel.FindAll("#ms-query-linq-type optgroup").Select(static x => x.GetAttribute("label")).Should().Equal("Event store");
        panel.FindAll(".ms-empty-title").Should().BeEmpty("the old 'No document type to query' state no longer blocks the page");
        panel.Find("#ms-query-linq-archived").Should().NotBeNull();
        panel.Find("#ms-query-linq-where").GetAttribute("placeholder").Should().Be(LinqQueryPanel.EventsWherePlaceholder);
    }

    [Fact]
    public void Picking_the_events_shows_the_archived_selector_and_not_the_deleted_one()
    {
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService().WithType("order", softDeleted: true)));

        var panel = context.Render<LinqQueryPanel>();
        panel.FindAll("#ms-query-linq-deleted").Should().ContainSingle();

        panel.Find("#ms-query-linq-type").Change(LinqQueryPanel.EventsToken);

        panel.FindAll("#ms-query-linq-deleted").Should().BeEmpty();
        panel.FindAll("#ms-query-linq-archived option").Select(static x => x.TextContent.Trim())
            .Should().Equal("Live events only", "Live and archived", "Archived only");
        panel.Find("#ms-query-linq-orderby").GetAttribute("placeholder").Should().Be("Sequence desc (the default)");
    }

    [Fact]
    public void The_ordering_placeholder_names_the_default_that_applies()
    {
        LinqEventTargetInfo partitioned = Info with { PerTenantSequences = true, DefaultOrdering = "Timestamp desc" };
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService(), partitioned));

        var panel = context.Render<LinqQueryPanel>();

        panel.Find("#ms-query-linq-orderby").GetAttribute("placeholder").Should().Be("Timestamp desc (the default)");
    }

    // ------------------------------------------------------------------------------------------------
    // The notes
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_members_note_lists_what_is_offered_and_the_not_offered_note_gives_each_reason()
    {
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService()));

        var panel = context.Render<LinqQueryPanel>();

        panel.Find(".ms-query-linq-event-members").TextContent.Should().Contain("Members of an event: Sequence, Timestamp, EventTypeName, StreamId");

        string withheld = panel.Find(".ms-query-linq-withheld").TextContent;
        withheld.Should().Contain("Not offered here, and why");
        withheld.Should().Contain("Use the Archived selector").And.Contain("CausationIdEnabled is false").And.Contain("docs/linq-mode.md#events");
        panel.FindAll(".ms-query-linq-withheld-list li").Should().HaveCount(3);
        panel.FindAll(".ms-query-linq-withheld-list li code").Select(static x => x.TextContent)
            .Should().Equal("IsArchived", "CausationId", "Data, Headers, Tags, EventType");
    }

    [Fact]
    public void The_not_offered_list_is_open_by_default_so_an_admin_never_has_to_look_for_it()
    {
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService()));

        var panel = context.Render<LinqQueryPanel>();

        panel.Find(".ms-query-linq-withheld").HasAttribute("open").Should().BeTrue();
    }

    [Fact]
    public async Task The_not_offered_list_stays_open_and_the_refusal_carries_its_reason()
    {
        LinqWithheldMember archived = Info.Withheld[0];
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = LinqQueryResult.Refused(
            LinqQueryService.EventsAlias,
            new LinqRefusal(LinqStage.Screen, LinqClause.Where,
                "The predicate names 'IsArchived', which is not a member LINQ mode can query here. " + archived.Reason, 6),
            offset: 0,
            pageSize: 50);

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find("#ms-query-linq-type").Change(LinqQueryPanel.EventsToken);
        panel.Find("#ms-query-linq-where").Change("not IsArchived");
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        panel.Markup.Should().Contain(archived.Reason);
        panel.Find(".ms-query-linq-withheld").HasAttribute("open").Should().BeTrue();
    }

    [Fact]
    public void The_Order_by_box_asks_the_browser_for_Ctrl_Enter_as_the_predicate_editor_does()
    {
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService()));

        context.Render<LinqQueryPanel>();

        context.JSInterop.Invocations["martenStudio.query.enhanceRunField"].Should().ContainSingle(
            "the Order-by box is a plain input, so the browser recognises Ctrl+Enter there and sends its live text");

        context.JSInterop.Invocations["martenStudio.query.enhanceRunField"].Single().Arguments.Should().HaveCount(3)
            .And.Subject.Last().Should().Be(nameof(LinqQueryPanel.RunFromOrderByAsync),
                "the shared helper is told which [JSInvokable] to call; it does not know the panel's name");
    }

    [Fact]
    public async Task Ctrl_Enter_in_the_Order_by_box_runs_the_query_with_the_text_the_box_holds()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = Result(new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false), Row(7));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find("#ms-query-linq-type").Change(LinqQueryPanel.EventsToken);
        panel.Find("#ms-query-linq-where").Change("Version > 0");

        await panel.InvokeAsync(() => panel.Instance.RunFromOrderByAsync("Timestamp desc"));

        LinqQueryRequest request = linq.Requests.Should().ContainSingle().Subject;
        request.OrderBy.Should().Be("Timestamp desc", "the chord carries the live text, because `change` has not fired yet");
        request.Where.Should().Be("Version > 0");
        panel.FindAll(".ms-query-linq-events .ms-event-card").Should().HaveCount(1);
    }

    [Fact]
    public void The_mode_note_names_the_Marten_source_and_the_limits()
    {
        using StudioComponentContext context = CreateContext(WithEvents(new FakeLinqQueryService()));

        var panel = context.Render<LinqQueryPanel>();

        string note = panel.Find(".ms-query-linq-event-note").TextContent;
        note.Should().Contain("IReadOnlyEventStore.QueryRawEvents()").And.Contain("not its JSON body").And.Contain("Newest first unless you order it")
            .And.Contain("10,000");
        panel.FindAll(".ms-query-linq-note:not(.ms-query-linq-event-note)").Should().BeEmpty("the documents' note is not shown for the events");
    }

    // ------------------------------------------------------------------------------------------------
    // Running
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_over_the_events_asks_for_the_events_target_with_the_archived_choice()
    {
        var linq = WithEvents(new FakeLinqQueryService().WithType("order"));
        linq.Result = Result(new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Only, WholeDatabase: false, PerTenantSequences: false));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        panel.Find("#ms-query-linq-type").Change(LinqQueryPanel.EventsToken);
        panel.Find("#ms-query-linq-archived").Change(nameof(ArchivedFilter.Only));
        panel.Find("#ms-query-linq-where").Change("EventTypeName == \"order_placed\"");
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        LinqQueryRequest request = linq.Requests.Should().ContainSingle().Subject;
        request.Target.Should().Be(LinqQueryTarget.Events);
        request.Alias.Should().Be(LinqQueryService.EventsAlias);
        request.Archived.Should().Be(ArchivedFilter.Only);
        request.Where.Should().Be("EventTypeName == \"order_placed\"");
    }

    [Fact]
    public async Task Rows_are_the_feeds_event_cards_with_a_stream_link_carrying_the_rows_tenant_across_tenants()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = Result(
            new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: true),
            Row(7, tenant: "acme"),
            Row(7, stream: "bbbbbbbb-0000-0000-0000-000000000002", tenant: "globex"));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        panel.FindAll(".ms-query-linq-events .ms-event-card").Should().HaveCount(2, "two rows that share a sequence render side by side - keyed by position");
        panel.FindAll(".ms-query-linq-events .ms-event-card-seq").Select(static x => x.TextContent.Trim()).Should().Equal("#7", "#7");

        string[] hrefs = [.. panel.FindAll(".ms-query-linq-events .ms-event-card-stream a").Select(static x => x.GetAttribute("href")!)];
        hrefs.Should().HaveCount(2);
        hrefs[0].Should().Contain("tenant=acme").And.Contain("id=aaaaaaaa");
        hrefs[1].Should().Contain("tenant=globex").And.Contain("id=bbbbbbbb");

        panel.FindAll(".ms-query-linq-events .ms-event-meta-chip").Select(static x => x.TextContent).Should().Contain(static x => x.Contains("ada", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_narrowed_run_keeps_the_scope_tenant_in_the_stream_link()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = Result(new LinqEventScope("acme", CrossTenant: false, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false), Row(1, tenant: "acme"));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        panel.Find(".ms-query-linq-events .ms-event-card-stream a").GetAttribute("href")!.Should().NotContain("tenant=",
            "a narrowed run carries the active scope, not the row's tenant (only a cross-tenant read does)");
        panel.FindAll(".ms-query-scope-chip").Select(static x => x.TextContent.Trim()).Should().Contain("tenant acme").And.Contain("live only");
    }

    [Fact]
    public async Task An_events_result_has_no_index_advice_and_no_what_if()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = Result(new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false), Row(1));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        panel.FindAll(".ms-query-linq-advice").Should().BeEmpty();
        panel.FindAll(".ms-whatif-run").Should().BeEmpty();
        panel.Find(".ms-query-sql-note").TextContent.Should().Contain("The tenant and archive terms are Marten's.");
    }

    [Fact]
    public async Task The_messages_say_what_the_tenant_and_archive_terms_meant()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = Result(
            new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Include, WholeDatabase: false, PerTenantSequences: true), Row(1));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        await panel.Find("#ms-query-tab-messages").ClickAsync(new MouseEventArgs());

        panel.FindAll(".ms-query-message").Select(static x => x.TextContent).Should().Equal(
            "No tenant is selected: Marten read every tenant's events (AnyTenant()).",
            "Archived events are included (Marten's MaybeArchived()); each one carries an 'archived' flag.",
            "Sequences are counted per tenant here, so across tenants the default order is by timestamp.");
    }

    [Fact]
    public async Task A_store_whose_events_are_not_divided_by_tenant_says_the_whole_log_was_read()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = Result(new LinqEventScope(null, CrossTenant: false, ArchivedFilter.Exclude, WholeDatabase: true, PerTenantSequences: false), Row(1));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        await panel.Find("#ms-query-tab-messages").ClickAsync(new MouseEventArgs());

        panel.Find(".ms-query-messages").TextContent.Should().Contain("not divided by tenant").And.Contain("whole database's event log");
        panel.FindAll(".ms-query-scope-chip").Select(static x => x.TextContent.Trim()).Should().Contain("whole database");
    }

    [Fact]
    public async Task An_empty_page_says_no_event_matched()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = Result(new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        panel.Find(".ms-query-empty .ms-empty-title").TextContent.Should().Be("No event matched");
    }

    [Fact]
    public async Task A_body_that_was_not_inlined_says_why_beside_its_card()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        LinqEventRow big = Row(1) with { Json = null, NotInlined = "Not shown: it is larger than MartenStudioOptions.MaxInlineDocumentBytes (1 KiB). Open its stream page." };
        linq.Result = Result(new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false), big);

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        panel.Find(".ms-query-linq-not-inlined").TextContent.Should().Contain("Open its stream page");
    }

    [Fact]
    public async Task The_no_event_tables_sentence_is_shown_as_the_error()
    {
        var linq = WithEvents(new FakeLinqQueryService());
        linq.Result = new LinqQueryResult(LinqQueryService.EventsAlias, [], false, 0, 50, "select data from public.mt_events as d", [], TimeSpan.Zero,
            Error: new LinqQueryError("42P01", LinqQueryService.NoEventTablesMessage("public")))
        {
            EventScope = new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false),
        };

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>();
        await panel.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        panel.Find(".ms-query-linq-error").TextContent.Should().Contain("This database has no event store tables yet (public.mt_events)")
            .And.Contain("LINQ mode never creates them");
    }

    // ------------------------------------------------------------------------------------------------
    // The deep link
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_deep_link_to_the_events_fills_the_boxes_and_never_runs()
    {
        var linq = WithEvents(new FakeLinqQueryService().WithType("order"));

        using StudioComponentContext context = CreateContext(linq);

        var panel = context.Render<LinqQueryPanel>(parameters => parameters
            .Add(static x => x.Events, true)
            .Add(static x => x.Archived, "include")
            .Add(static x => x.Where, "Version > 3")
            .Add(static x => x.OrderBy, "Timestamp desc"));

        panel.Find("#ms-query-linq-type").GetAttribute("value").Should().Be(LinqQueryPanel.EventsToken);
        panel.Find("#ms-query-linq-archived").GetAttribute("value").Should().Be(nameof(ArchivedFilter.Include));
        panel.Find("#ms-query-linq-where").GetAttribute("value").Should().Be("Version > 3");
        panel.Find("#ms-query-linq-orderby").GetAttribute("value").Should().Be("Timestamp desc");
        linq.Requests.Should().BeEmpty("a link only fills the boxes (D9)");
    }

    [Fact]
    public void The_Query_page_reads_the_target_and_archived_parameters_and_never_runs()
    {
        var linq = WithEvents(new FakeLinqQueryService().WithType("order"));

        using StudioComponentContext context = CreateContext(linq);

        context.Navigate("marten/query?mode=linq&target=events&archived=only&predicate=" + Uri.EscapeDataString("EventTypeName == \"a\""));

        var page = context.Render<QueryPage>();

        page.Find("#ms-query-linq-type").GetAttribute("value").Should().Be(LinqQueryPanel.EventsToken);
        page.Find("#ms-query-linq-archived").GetAttribute("value").Should().Be(nameof(ArchivedFilter.Only));
        page.Find("#ms-query-linq-where").GetAttribute("value").Should().Be("EventTypeName == \"a\"");
        linq.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Editing a box and clicking Run is one address, not two: the box's <c>change</c> keeps its draft and writes nothing, and the Run
    /// writes the address once (one address per Run). In 1.1 the two writes were enhanced navigations, a fetch of the page each, and
    /// the second aborted the first; since 1.2 a write is a client-side history update that cannot abort another.
    /// </summary>
    [Fact]
    public async Task Editing_the_predicate_and_clicking_Run_rewrites_the_address_once_not_twice()
    {
        var linq = WithEvents(new FakeLinqQueryService().WithType("order"));
        linq.Result = Result(new LinqEventScope(null, CrossTenant: true, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false), Row(1));

        using StudioComponentContext context = CreateContext(linq);
        context.Navigate("marten/query?mode=linq&target=events");

        var page = context.Render<QueryPage>();

        var navigation = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>(context.Services);
        List<string> pushed = [];
        navigation.LocationChanged += (_, args) => pushed.Add(args.Location);

        page.Find("#ms-query-linq-where").Change("Version > 0");
        await page.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        linq.Requests.Should().ContainSingle();
        pushed.Should().ContainSingle("one address per Run: a second push would have been the same address again")
            .Which.Should().Contain("predicate=Version%20%3E%200");
    }

    // ------------------------------------------------------------------------------------------------
    // The Feed's way in
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_feeds_filters_become_a_predicate()
    {
        DateTimeOffset from = new(2026, 11, 1, 10, 30, 15, TimeSpan.FromHours(2));

        EventLinks.FeedPredicate(StreamIdentity.AsGuid, "aaaaaaaa-0000-0000-0000-000000000001", ["order_placed"], from)
            .Should().Be("StreamId == \"aaaaaaaa-0000-0000-0000-000000000001\" and EventTypeName == \"order_placed\" and Timestamp >= \"2026-11-01T08:30:15Z\"");
        EventLinks.FeedPredicate(StreamIdentity.AsString, "order/1", ["a", "b"], null)
            .Should().Be("StreamKey == \"order/1\" and EventTypeName in (\"a\", \"b\")");
        EventLinks.FeedPredicate(StreamIdentity.AsGuid, null, [], null).Should().BeNull();
        EventLinks.FeedPredicate(StreamIdentity.AsGuid, " ", [], null).Should().BeNull();
    }

    [Fact]
    public void A_clause_the_screen_would_refuse_is_left_out()
    {
        EventLinks.FeedPredicate(StreamIdentity.AsString, "say \"hi\"", ["a\\b", "ok"], null).Should().Be("EventTypeName == \"ok\"");
        EventLinks.FeedPredicate(StreamIdentity.AsString, new string('x', 300), [], null).Should().BeNull();
    }

    [Fact]
    public void The_link_to_the_events_query_carries_the_target_the_predicate_the_archived_choice_and_the_scope()
    {
        var options = new MartenStudioOptions();
        var scope = new StudioScope("default", "main", "acme");

        string href = EventLinks.ToLinqQuery(options, scope, "Version > 3", ArchivedFilter.Include);

        href.Should().Contain("query?mode=linq").And.Contain("target=events").And.Contain("predicate=Version%20%3E%203")
            .And.Contain("archived=include").And.Contain("store=default").And.Contain("db=main").And.Contain("tenant=acme");
        EventLinks.ToLinqQuery(options, null, null).Should().NotContain("predicate=").And.NotContain("archived=");
    }

    [Theory]
    [InlineData(null, "Exclude")]
    [InlineData("include", "Include")]
    [InlineData("ONLY", "Only")]
    [InlineData("nonsense", "Exclude")]
    public void The_archived_token_reads_back(string? token, string expected)
    {
        EventLinks.ParseArchived(token).Should().Be(Enum.Parse<ArchivedFilter>(expected));
        EventLinks.ArchivedToken(EventLinks.ParseArchived(token)).Should().Be(expected == "Exclude" ? null : expected.ToLowerInvariant());
    }
}
