using Bunit;

using JasperFx.Events.Projections;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The dead-letter screen: the list, the expansion that shows the exception and the offending event, and
/// the two actions this release ships - both gated, both confirmed.
/// </summary>
public class DeadLettersTests
{
    private static readonly Guid LetterId = new("22222222-2222-2222-2222-222222222222");

    // ------------------------------------------------------------------------------------------------
    // CL1 item 4 (and its review): a host that hides DeadLetterEvent
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reached by its address while the host hides the type - the nav entry is gone - the page says the one sentence,
    /// never "no collection called", and reads, filters and offers nothing. A delegate that throws hides it too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_page_reached_by_its_address_while_the_type_is_hidden_says_so_and_reads_nothing(bool throws)
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.Options.IsDocumentTypeVisible = Hiding(throws);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        page.Find(".ms-dead-letters-hidden").TextContent.Trim()
            .Should().Be("Dead letters are hidden by MartenStudioOptions.IsDocumentTypeVisible.");
        page.Markup.Should().NotContain("no collection called").And.NotContain("deadletterevent");
        page.FindAll("tbody tr").Should().BeEmpty();
        page.FindAll("#ms-dl-projection").Should().BeEmpty("no filter for a list that is not read");
        context.EventData.DeadLetterQueries.Should().BeEmpty("the page asks the service for nothing");
    }

    /// <summary>The service's own answer for a hidden type is drawn the same way, whatever the page asked.</summary>
    [Fact]
    public async Task The_services_hidden_answer_is_drawn_as_the_same_sentence()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = DeadLetterPage.Hidden;

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        page.WaitForAssertion(() => page.Find(".ms-dead-letters-hidden").TextContent.Trim()
            .Should().Be(DeadLetterVisibility.HiddenMessage), TimeSpan.FromSeconds(5));
        page.FindAll(".ms-error-alert").Should().BeEmpty("the host's configuration is not a failure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_navigation_has_no_dead_letters_entry_while_the_type_is_hidden(bool throws)
    {
        using StudioComponentContext context = await NewContextAsync();
        context.Options.IsDocumentTypeVisible = Hiding(throws);

        var nav = context.Render<PostgresStudio.Components.Layout.NavMenu>();

        List<string?> links = [.. nav.FindAll("a.ms-nav-link").Select(static x => x.GetAttribute("href"))];
        links.Should().NotContain("events/dead-letters", "a hidden document type is absent (D27)");
        links.Should().Contain("events/feed", "the rest of the Events section is still there");
    }

    /// <summary>The positive control: a host that shows the type, or sets no delegate, keeps the entry.</summary>
    [Fact]
    public async Task The_navigation_keeps_the_dead_letters_entry_while_the_type_is_shown()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.Options.IsDocumentTypeVisible = static type => type != typeof(Uri);

        var nav = context.Render<PostgresStudio.Components.Layout.NavMenu>();

        nav.FindAll("a.ms-nav-link").Select(static x => x.GetAttribute("href")).Should().Contain("events/dead-letters");
    }

    private static Func<Type, bool> Hiding(bool throws) => throws
        ? static type => type == typeof(JasperFx.Events.Daemon.DeadLetterEvent) ? throw new InvalidOperationException("broken") : true
        : static type => type != typeof(JasperFx.Events.Daemon.DeadLetterEvent);

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

    /// <summary>
    /// One event, and its toolbar used to be taller than its body. The controls are behind the card's
    /// own "Event actions", where the reader who wants Raw or Download can still reach them without the
    /// screen's real actions - Discard, Skip, Rewind - competing with seven of the viewer's.
    /// </summary>
    [Fact]
    public async Task The_offending_event_is_shown_compact_with_its_controls_one_press_away()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);
        context.EventData.EventBySequence = FakeEventDataService.Event(42, type: "OrderShipped");

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());

        page.FindAll(".ms-event-card").Should().ContainSingle();
        page.FindAll(".ms-json-toolbar").Should().BeEmpty();
        page.Find(".ms-event-card-body .ms-json-tree").TextContent.Should().Contain("total");

        await page.Find(".ms-event-card-more").ClickAsync(new());

        page.FindAll(".ms-json-toolbar").Should().ContainSingle();
        page.Find(".ms-event-card-more").GetAttribute("aria-expanded").Should().Be("true");
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

    /// <summary>
    /// DB-0-fix-3, R1: Marten's rewind rewrites every shard's progression row of the projection and deletes
    /// its dead letters at or above the floor for every tenant, and the daemon re-runs every tenant's
    /// events. The dialog says so before the name is typed, rather than letting a list filtered to one
    /// tenant suggest the rewind is too.
    /// </summary>
    [Fact]
    public async Task Rewinding_says_it_reaches_every_tenant_in_the_database()
    {
        using StudioComponentContext context = await RewindableAsync();

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);
        await RewindButton(page).ClickAsync(new());

        string dialog = page.Find(".ms-confirm-dialog").TextContent;

        dialog.Should().Contain("every agent of 'OrderSummary' in this database");
        dialog.Should().Contain("for every tenant in this database, not only the one on screen");
        dialog.Should().Contain("every tenant's, not only the ones listed here");
        page.FindAll(".ms-rewind-tenant-reach").Should().BeEmpty("no tenant is selected, so there is none to say it does not narrow");
    }

    /// <summary>
    /// With a tenant selected, the dialog says the tenant does not narrow the rewind and why the visitor's
    /// account has to be allowed the database as a whole - which is what the service now asks
    /// (<c>DatabaseReachAuthorization.RequireWholeDatabaseAsync</c>).
    /// </summary>
    [Fact]
    public async Task Rewinding_with_a_tenant_selected_says_the_tenant_does_not_narrow_it()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.Catalog.WithTenants("default", new TenantList(["acme"], IsTruncated: false, TenantListSource.Configured));

        // The context settled its scope before the tenants existed; re-read, then pick acme.
        await context.State.RefreshScopeListingsAsync(Xunit.TestContext.Current.CancellationToken);
        await context.State.SetScopeAsync("default", "localhost.marten", "acme", Xunit.TestContext.Current.CancellationToken);
        context.State.ActiveScope!.TenantId.Should().Be("acme");

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);
        await RewindButton(page).ClickAsync(new());

        // Markup text keeps its line breaks and indentation; what a person reads is single-spaced.
        string reach = string.Join(' ', page.Find(".ms-rewind-tenant-reach").TextContent
            .Split((char[]) [' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

        reach.Should().Contain("Selecting acme does not narrow this rewind");
        reach.Should().Contain("for every tenant in this database");
        reach.Should().Contain("allowed to manage dead letters for this database as a whole, not only for acme");
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

    // -----------------------------------------------------------------------------------------------
    // Hard rule 14: this screen must not be able to create an event store by being opened.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A database with no event tables is never asked about projections.
    /// </summary>
    /// <remarks>
    /// The projections read is what feeds the Rewind button, and it reaches
    /// <c>AllProjectionProgress</c> and <c>FetchHighestEventSequenceNumber</c> - both of which open with
    /// <c>EnsureStorageExistsAsync(typeof(IEvent))</c> and apply the event store's migration under the
    /// database's own <c>AutoCreate</c> before reading a row. The shape is already in hand from
    /// <c>DescribeAsync</c>, which is an <c>information_schema</c> question, and a database with no event
    /// store has no dead letters to rewind either. <c>NavIndicatorNoDdlLiveTests</c> is the same statement
    /// against a real Postgres.
    /// </remarks>
    [Fact]
    public async Task A_database_with_no_event_tables_is_never_asked_about_projections()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.EventData.Shape = new EventStoreShape { EventTablesExist = false };
        context.ProjectionData.WithProjection("OrderSummary");

        context.Render<DeadLetters>();

        context.ProjectionData.Reads.Should().Be(0, "reading the progression would have created the event store");
    }

    /// <summary>
    /// And the dead-letter list is not read either, for the same reason.
    /// </summary>
    /// <remarks>
    /// <c>ListDeadLettersAsync</c> is a Marten session query over <c>DeadLetterEvent</c>, and a Marten
    /// query opens with <c>EnsureStorageExistsAsync</c> for its document type - which on a database that
    /// has never run the daemon creates <c>mt_doc_deadletterevent</c> and Marten's whole helper-function
    /// set. A database with no event tables has never recorded a dead letter, so the list would have been
    /// empty at the cost of a migration. <c>NavIndicatorNoDdlLiveTests</c> proves both halves live.
    /// </remarks>
    [Fact]
    public async Task A_database_with_no_event_tables_is_never_asked_for_the_list_either()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.Shape = new EventStoreShape { EventTablesExist = false };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();

        context.EventData.DeadLetterQueries.Should().BeEmpty("the query would have created the document table");

        page.Find(".ms-empty-title").TextContent.Should().Be("This store has no event storage yet");
    }

    /// <summary>
    /// And with the tables there both reads still happen, so the gate above is a gate rather than a
    /// removal.
    /// </summary>
    [Fact]
    public async Task A_database_that_has_event_tables_is_still_asked()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.ProjectionData.WithProjection("OrderSummary");

        context.Render<DeadLetters>();

        context.ProjectionData.Reads.Should().Be(1);
        context.EventData.DeadLetterQueries.Should().ContainSingle();
    }

    /// <summary>
    /// Seven columns, one of them an exception type, inside a 334px phone column: the list measured
    /// 737px and was clipped at the fourth. It scrolls now.
    /// </summary>
    [Fact]
    public async Task The_list_is_in_a_scroll_region_rather_than_being_clipped()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId)], false);

        context.Render<DeadLetters>().ShouldPutEveryTableInALabelledScrollRegion();
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
    /// <summary>
    /// SEC1-fix5 (item 3 of fix4): under per-tenant sequences with "All tenants", #N names one event in each tenant, so the
    /// offending event is looked up in the dead letter's own tenant - and its stream is linked there, so a stream id two
    /// tenants share opens directly rather than the stream page's "choose a tenant".
    /// </summary>
    [Fact]
    public async Task Under_per_tenant_sequences_with_all_tenants_the_event_is_read_and_linked_in_the_dead_letters_tenant()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.EventData.Shape = context.EventData.Shape with { PerTenantSequences = true, EventsConjoined = true, HasTenantId = true };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId) with { TenantId = "acme" }], false);
        context.EventData.EventBySequence = FakeEventDataService.Event(42, streamId: "order-42") with { TenantId = "acme" };

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        context.EventData.EventBySequenceScopes.Should().ContainSingle().Which.TenantId.Should().Be("acme");
        page.FindAll("a").Select(static x => x.GetAttribute("href")).Should()
            .Contain(static x => x!.Contains("id=order-42", StringComparison.Ordinal) && x.Contains("tenant=acme", StringComparison.Ordinal));
    }

    /// <summary>The first control: with a tenant selected, the lookup keeps the scope as selected.</summary>
    [Fact]
    public async Task With_a_tenant_selected_the_event_is_read_in_the_selected_scope()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.Catalog.WithTenants("default", new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured));
        await context.State.RefreshScopeListingsAsync(Xunit.TestContext.Current.CancellationToken);
        await context.State.SetScopeAsync("default", "localhost.marten", "globex", Xunit.TestContext.Current.CancellationToken);
        context.EventData.Shape = context.EventData.Shape with { PerTenantSequences = true, EventsConjoined = true, HasTenantId = true };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId) with { TenantId = "acme" }], false);

        await ExpandAsync(context);

        context.EventData.EventBySequenceScopes.Should().ContainSingle().Which.Should().Be(context.State.ActiveScope);
    }

    /// <summary>The second control: with one sequence for the store, "All tenants" reads the store's #N as it always did.</summary>
    [Fact]
    public async Task Without_per_tenant_sequences_the_event_is_read_across_tenants()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.EventData.Shape = context.EventData.Shape with { PerTenantSequences = false, EventsConjoined = true, HasTenantId = true };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId) with { TenantId = "acme" }], false);

        await ExpandAsync(context);

        context.EventData.EventBySequenceScopes.Should().ContainSingle().Which.TenantId.Should().BeNull();
    }

    /// <summary>
    /// SEC1-fix4: on a store that numbers events per tenant, below Marten 9.51 Skip and Rewind reach every tenant's event
    /// of a number, so both are disabled with the service's own sentences - the shape saying the Marten has neither
    /// per-tenant form, whatever Marten this test runs at.
    /// </summary>
    [Fact]
    public async Task Under_per_tenant_sequences_skip_and_rewind_are_disabled_with_the_services_sentences()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.EventData.Shape = context.EventData.Shape with
        {
            HasIsSkipped = true, PerTenantSequences = true, EventsConjoined = true, TenantSkipping = false, TenantRewind = false,
        };

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement skip = SkipButton(page);
        skip.HasAttribute("disabled").Should().BeTrue();
        skip.GetAttribute("title").Should().Be(StreamCompactionRules.SkipNeedsOneSequence);

        AngleSharp.Dom.IElement rewind = RewindButton(page);
        rewind.HasAttribute("disabled").Should().BeTrue();
        rewind.GetAttribute("title").Should().Be(StreamCompactionRules.RewindNeedsOneSequence);
    }

    /// <summary>
    /// L1, from Marten 9.51: the skip is one tenant's, so it is offered, says so, and is asked in the dead letter's own
    /// tenant when "All tenants" is selected - the service skips that tenant's #N and no other.
    /// </summary>
    [Fact]
    public async Task Where_Marten_skips_one_tenants_event_skip_is_offered_and_asked_in_the_dead_letters_tenant()
    {
        using StudioComponentContext context = await RewindableAsync();
        context.EventData.Shape = context.EventData.Shape with
        {
            HasIsSkipped = true, PerTenantSequences = true, EventsConjoined = true, HasTenantId = true, TenantSkipping = true,
        };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId, sequence: 77) with { TenantId = "acme" }], false);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement skip = SkipButton(page);
        skip.HasAttribute("disabled").Should().BeFalse();
        skip.GetAttribute("title").Should().Contain("tenant acme's event").And.Contain("no other tenant's event");

        await skip.ClickAsync(new());
        page.Find(".ms-confirm-dialog").TextContent.Should().Contain("Tenant acme's event #77").And.Contain("no other tenant's event");
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        context.EventData.Skipped.Should().Equal(77L);
        context.EventData.SkipScopes.Should().ContainSingle().Which.TenantId.Should().Be("acme",
            "with All tenants selected, the write is asked in the dead letter's own tenant");
    }

    /// <summary>
    /// L2, from Marten 9.51 with JasperFx 2.91: a rewind is one tenant's, offered for a tenant whose agent the daemon here
    /// runs, described as that tenant's alone, and asked in that tenant.
    /// </summary>
    [Fact]
    public async Task Where_the_daemon_here_runs_the_tenants_agent_rewind_is_offered_for_that_tenant_alone()
    {
        using StudioComponentContext context = await PerTenantRewindableAsync(agents: ["OrderSummary:All:acme", "OrderSummary:All:globex"]);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement rewind = RewindButton(page);
        rewind.HasAttribute("disabled").Should().BeFalse();
        rewind.GetAttribute("title").Should().Contain("tenant acme's agents");

        await rewind.ClickAsync(new());

        string dialog = page.Find(".ms-confirm-dialog").TextContent;
        dialog.Should().Contain("tenant acme's agents of 'OrderSummary'").And.Contain("in no other");
        page.FindAll(".ms-rewind-one-tenant").Should().ContainSingle();
        page.FindAll(".ms-rewind-tenant-reach").Should().BeEmpty("the per-tenant rewind does not reach the database as a whole");
        dialog.Should().Contain("every event of tenant acme from #42 onwards");

        await page.Find(".ms-confirm-input").InputAsync(new() { Value = "OrderSummary" });
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        context.EventData.Rewinds.Should().Equal("OrderSummary/42");
        context.EventData.RewindScopes.Should().ContainSingle().Which.TenantId.Should().Be("acme");
    }

    /// <summary>
    /// The condition the research named: the per-tenant rewind starts the tenant's agents in the daemon it is asked of, so
    /// where the daemon here runs no agent of that projection for that tenant - HotCold's cold node, a distributed host - it
    /// is not offered, and says why.
    /// </summary>
    [Fact]
    public async Task Where_the_daemon_here_does_not_run_the_tenants_agent_rewind_is_disabled_and_says_why()
    {
        using StudioComponentContext context = await PerTenantRewindableAsync(agents: ["OrderSummary:All:globex", "Other:All:acme"]);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement rewind = RewindButton(page);
        rewind.HasAttribute("disabled").Should().BeTrue();
        rewind.GetAttribute("title").Should().Be(StreamCompactionRules.RewindNeedsTenantAgentHere("OrderSummary", "acme"));
    }

    /// <summary>
    /// A tenant id with a <c>:</c> in it: its agents' identity does not read back as its own, so the per-tenant rewind would
    /// stop them and then fail. Disabled, with the service's sentence - not "the daemon does not run it".
    /// </summary>
    [Fact]
    public async Task A_tenant_whose_agents_cannot_be_addressed_is_not_offered_a_rewind_and_says_why()
    {
        using StudioComponentContext context = await PerTenantRewindableAsync(agents: ["OrderSummary:All:acme"]);
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId) with { TenantId = "a:b" }], false);

        IRenderedComponent<DeadLetters> page = await ExpandAsync(context);

        AngleSharp.Dom.IElement rewind = RewindButton(page);
        rewind.HasAttribute("disabled").Should().BeTrue();
        rewind.GetAttribute("title").Should().Be(StreamCompactionRules.RewindTenantNotAddressable("a:b"));
    }

    [Theory]
    [InlineData("OrderSummary:All:acme", "OrderSummary", "acme", true)]
    [InlineData("ordersummary:All:acme", "OrderSummary", "acme", true)]
    [InlineData("OrderSummary:V2:All:acme", "OrderSummary", "acme", true)]
    [InlineData("OrderSummary:All:Acme", "OrderSummary", "acme", false)]
    [InlineData("OrderSummary:All", "OrderSummary", "acme", false)]
    [InlineData("OrderSummary:V2:All", "OrderSummary", "All", false)]
    [InlineData("OrderSummary:acme", "OrderSummary", "acme", false)]
    [InlineData("OrderSummary:All:acme", "OrderSummary", null, false)]
    public void A_tenant_agent_is_read_from_the_shard_identitys_grammar(string identity, string projection, string? tenant, bool runs)
    {
        TenantRewind.RunsAgent([identity], projection, tenant).Should().Be(runs);
    }

    private static async Task<StudioComponentContext> PerTenantRewindableAsync(string[] agents)
    {
        StudioComponentContext context = await RewindableAsync();
        context.EventData.Shape = context.EventData.Shape with
        {
            PerTenantSequences = true, EventsConjoined = true, HasTenantId = true, TenantRewind = true,
        };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(LetterId) with { TenantId = "acme" }], false);
        context.ProjectionData.Daemon = context.ProjectionData.Daemon with
        {
            Agents = [.. agents.Select(static x => new MartenStudio.Services.Projections.DaemonAgentInfo(x, "Running", 10, 10))],
        };

        return context;
    }

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
