using System.Globalization;

using Bunit;

using JasperFx.Events;

using MartenStudio.Components.Pages;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Events;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The Overview page: real facts about the stores this application registered, and a region that breaks
/// on its own when one of them cannot be reached.
/// </summary>
public class OverviewTests
{
    [Fact]
    public void The_tiles_carry_the_facts_the_service_reported()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(documentTypes: 7, postgresVersion: "17.2");

        var page = context.Render<Overview>();

        page.StatCardValue("Databases").Should().Be("1");
        page.StatCardValue("Document types").Should().Be("7");
        page.StatCardValue("Event types").Should().Be("4");
        page.StatCardValue("PostgreSQL").Should().Be("17.2");
        page.StatCardClasses("PostgreSQL").Should().Contain("ms-stat-card-success");
    }

    [Fact]
    public void The_event_store_configuration_is_rendered_as_facts_a_person_can_act_on()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore();

        var page = context.Render<Overview>();

        page.KeyValue("Stream identity").Should().Be("Guid");
        page.KeyValue("Append mode").Should().Be("Quick");
        page.KeyValue("Event tenancy").Should().Be("Single");
        page.KeyValue("Event schema").Should().Be("studio_sample_events");
        page.KeyValue("Tenancy").Should().Be("Single");
    }

    [Fact]
    public void Each_database_is_named_by_Martens_identity_and_never_by_a_connection_string()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(databaseIdentity: "localhost.marten");

        var page = context.Render<Overview>();

        page.Find(".ms-database-title").TextContent.Trim().Should().Be("localhost.marten");
        page.KeyValue("Database").Should().Be("marten");
        page.KeyValue("Server").Should().Be("localhost");
        page.KeyValue("Schemas").Should().Be("studio_sample, studio_sample_events");
        page.Markup.Should().NotContain("Password", "no connection string is ever rendered");
    }

    /// <summary>
    /// A version nobody could read is drawn differently from one that is zero or missing: "cannot report"
    /// is a value (plan section 4.8).
    /// </summary>
    [Fact]
    public void A_Postgres_version_that_could_not_be_read_says_unknown_in_amber()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(postgresVersion: null);

        var page = context.Render<Overview>();

        page.StatCardValue("PostgreSQL").Should().Be("unknown");
        page.StatCardClasses("PostgreSQL").Should().Contain("ms-stat-card-warning");
    }

    /// <summary>A database that cannot be reached breaks its own region and nothing else.</summary>
    [Fact]
    public void A_database_that_cannot_be_reached_shows_an_alert_in_its_own_region()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo
            .WithStore(key: "default", displayName: "Default")
            .WithStore(key: "IInvoicingStore", displayName: "Invoicing Store", databaseError: "28P01: password authentication failed");

        var page = context.Render<Overview>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("28P01");
        page.FindAll("section").Should().HaveCount(2, "the healthy store is still drawn");
        page.StatCardValue("Databases").Should().Be("1");
    }

    [Fact]
    public void A_store_that_will_not_build_is_an_alert_rather_than_a_missing_section()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithUnavailableStore("IInvoicingStore", "Invoicing Store", "no connection string was configured");

        var page = context.Render<Overview>();

        page.Find("h2").TextContent.Should().Contain("Invoicing Store");
        page.Find(".ms-error-alert").TextContent.Should().Contain("no connection string was configured");
    }

    [Fact]
    public void A_failure_to_load_is_shown_with_a_retry_that_asks_again()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.Failure = new InvalidOperationException("the store could not be reached");

        var page = context.Render<Overview>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("the store could not be reached");
        context.StoreInfo.Loads.Should().Be(1);

        context.StoreInfo.Failure = null;
        context.StoreInfo.WithStore();
        page.Find(".ms-error-alert button").Click();

        // bUnit's Click dispatches through the renderer's dispatcher and discards the task, so the retry's
        // reload has not necessarily started - let alone finished - when this line runs. Asserting on the
        // next line is what failed the Release run of cbf5112 with "expected 2, found 1".
        page.WaitForAssertion(() =>
        {
            context.StoreInfo.Loads.Should().Be(2);
            page.FindAll(".ms-error-alert").Should().BeEmpty();
            page.StatCardValue("Databases").Should().Be("1");
        });
    }

    /// <summary>
    /// No store, or none the visitor may see, is an empty state that names what a host would do about it
    /// - not a blank page.
    /// </summary>
    [Fact]
    public void No_store_at_all_is_an_empty_state_that_says_what_to_do()
    {
        using var context = new StudioComponentContext();

        var page = context.Render<Overview>();

        page.Find(".ms-empty-title").TextContent.Should().Be("No Marten store to show");
        page.Find(".ms-empty-description").TextContent.Should().Contain("AddMarten");
        page.Find(".ms-empty-description").TextContent.Should().Contain("StoreAuthorizationPolicy");
    }

    [Fact]
    public void The_registration_key_is_shown_beside_the_display_name()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(key: "IInvoicingStore", displayName: "Invoicing Store");

        var page = context.Render<Overview>();

        page.Find(".ms-section-key").TextContent.Trim().Should().Be("IInvoicingStore");
    }

    // -----------------------------------------------------------------------------------------------
    // The scope's own tiles: the six numbers about the database the selector is pointed at.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tiles_report_the_streams_the_events_the_high_water_mark_and_the_dead_letters()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.EventData.DeadLetterCount = 3;
        context.ProjectionData.WithProjection("OrderSummary", sequence: 400);

        var page = context.Render<Overview>();

        // D8's tilde: these are reltuples estimates, and a dashboard that ran count(*) per tile per
        // refresh would be a denial of service against the database it is meant to explain.
        page.StatCardValue("Streams").Should().Be("~1,200");
        page.StatCardValue("Events").Should().Be("~9,400");
        page.StatCardValue("High-water mark").Should().Be("#1,000");
        page.StatCardValue("Max projection lag").Should().Be("600");
        page.StatCardValue("Dead letters").Should().Be("3");
        page.StatCardClasses("Dead letters").Should().Contain("ms-stat-card-danger");
    }

    [Fact]
    public async Task Every_tile_links_to_the_page_that_explains_it()
    {
        using StudioComponentContext context = await ScopedAsync();

        var page = context.Render<Overview>();

        List<string> hrefs = [.. page.FindAll(".ms-overview-tiles a").Select(x => x.GetAttribute("href") ?? string.Empty)];

        hrefs.Should().HaveCount(6);
        hrefs.Should().Contain(x => x.StartsWith("events/streams?", StringComparison.Ordinal));
        hrefs.Should().Contain(x => x.StartsWith("events/feed?", StringComparison.Ordinal));
        hrefs.Should().Contain(x => x.StartsWith("events/dead-letters?", StringComparison.Ordinal));
        hrefs.Should().Contain(x => x.StartsWith("projections?", StringComparison.Ordinal));

        // The scope rides on every one of them, so a pasted link opens the same database (D9).
        hrefs.Should().OnlyContain(x => x.Contains("store=default", StringComparison.Ordinal));
    }

    /// <summary>
    /// A daemon running somewhere else is a supported deployment (hard rule 11), so the tile says so as a
    /// value rather than colouring it as a fault.
    /// </summary>
    [Fact]
    public async Task A_daemon_that_is_not_hosted_here_is_a_value_and_not_an_alarm()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.ProjectionData.WithNoDaemonHere();

        var page = context.Render<Overview>();

        page.StatCardValue("Daemon").Should().Be("not hosted in this process");
        page.StatCardClasses("Daemon").Should().Contain("ms-stat-card-info");
    }

    [Fact]
    public async Task A_paused_shard_is_named_on_the_daemon_tile()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.ProjectionData.WithProjection("OrderSummary", pauseReason: "it threw");

        var page = context.Render<Overview>();

        page.StatCardValue("Daemon").Should().Be("paused (1)");
        page.StatCardClasses("Daemon").Should().Contain("ms-stat-card-warning");
    }

    /// <summary>
    /// A projection read that failed takes its own three tiles to "unknown" and leaves the event counts,
    /// the dead-letter count and the store sections alone (plan section 4.8).
    /// </summary>
    [Fact]
    public async Task A_region_that_could_not_be_read_says_unknown_and_blanks_only_itself()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.ProjectionData.Failure = new InvalidOperationException("the progression table is gone");
        context.EventData.DeadLetterCount = 0;

        var page = context.Render<Overview>();

        page.StatCardValue("High-water mark").Should().Be("unknown");
        page.StatCardValue("Max projection lag").Should().Be("unknown");
        page.StatCardValue("Daemon").Should().Be("unknown");

        page.StatCardValue("Streams").Should().Be("~1,200");
        page.StatCardValue("Dead letters").Should().Be("0");
        page.StatCardValue("Databases").Should().Be("1", "the store sections load on their own");

        page.Find(".ms-overview-projections .ms-error-alert").TextContent.Should().Contain("progression table is gone");
    }

    /// <summary>
    /// The counts gate everything below them, so their failure is the one that has to be legible: the
    /// tiles say "unknown" and an alert says why, and the store sections below still draw.
    /// </summary>
    [Fact]
    public async Task An_unreadable_event_table_says_why_and_leaves_the_store_sections_alone()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.EventData.Counts = EventStoreCounts.Failed(
            new EventDataError("relation \"mt_events\" does not exist", "42P01", false));

        var page = context.Render<Overview>();

        page.StatCardValue("Streams").Should().Be("unknown");
        page.StatCardValue("Events").Should().Be("unknown");
        page.Find(".ms-overview .ms-error-alert").TextContent.Should().Contain("[42P01]");
        page.StatCardValue("Databases").Should().Be("1");
    }

    [Fact]
    public async Task A_dead_letter_count_that_could_not_be_read_is_unknown_rather_than_zero()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.EventData.DeadLetterCount = null;

        var page = context.Render<Overview>();

        page.StatCardValue("Dead letters").Should().Be("unknown");
        page.StatCardClasses("Dead letters").Should().Contain("ms-stat-card-warning");
    }

    // -----------------------------------------------------------------------------------------------
    // Projection health
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_health_table_is_sorted_by_lag_and_coloured_by_it()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.ProjectionData.HighWaterMark = 2_000;
        context.ProjectionData
            .WithProjection("Quiet", sequence: 1_999)
            .WithProjection("Behind", sequence: 1_500)
            .WithProjection("Stuck", sequence: 0);

        var page = context.Render<Overview>();

        page.TextOfAll(".ms-overview-health tbody tr td:first-child")
            .Should().Equal("Stuck:All", "Behind:All", "Quiet:All");

        // Amber above a thousand events behind, which is the threshold ProjectionLagThresholds names and
        // the projections screen colours by - the Overview must not invent a second one.
        page.FindAll(".ms-overview-health tbody tr")[0].ClassList.Should().Contain("ms-row-warning");
        page.FindAll(".ms-overview-health tbody tr")[2].ClassList.Should().NotContain("ms-row-warning");
    }

    [Fact]
    public async Task A_store_with_no_async_projection_says_so_rather_than_drawing_an_empty_table()
    {
        using StudioComponentContext context = await ScopedAsync();

        var page = context.Render<Overview>();

        page.Find(".ms-overview-projections .ms-empty-description").TextContent
            .Should().Contain("registers no async projection");
    }

    // -----------------------------------------------------------------------------------------------
    // Recent activity
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_latest_events_are_listed_newest_first_with_links_to_the_stream_and_the_type()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.EventData.Feed = new EventPage(
            [FakeEventDataService.Event(9, streamId: "order-9"), FakeEventDataService.Event(8, streamId: "order-8")],
            8,
            false);

        var page = context.Render<Overview>();

        page.TextOfAll(".ms-overview-events .ms-overview-list-seq").Should().Equal("#9", "#8");

        page.FindAll(".ms-overview-events a").Select(x => x.GetAttribute("href")).Should()
            .Contain(x => x!.StartsWith("events/streams/s?id=order-9", StringComparison.Ordinal))
            .And.Contain(x => x!.StartsWith("events/feed?types=OrderPlaced", StringComparison.Ordinal));

        // Fifteen, the way the plan asks - and the request says so rather than the page trimming later.
        context.EventData.FeedRequests[^1].PageSize.Should().Be(15);
    }

    [Fact]
    public async Task The_audit_ring_is_shown_beside_the_events_and_is_filtered_the_way_Activity_filters_it()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.ActionLog.Record("Archive stream", "order-1", succeeded: true);
        context.ActionLog.Record("Discard dead letter", "letter-1", succeeded: false, "it was gone");

        var page = context.Render<Overview>();

        page.TextOfAll(".ms-overview-activity .ms-overview-list-name")
            .Should().Equal("Discard dead letter", "Archive stream");

        page.FindAll(".ms-overview-activity .ms-state-error").Should().ContainSingle();
    }

    // -----------------------------------------------------------------------------------------------
    // The streams panel, named from the store's append mode.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>mt_streams.timestamp</c> keeps its <c>default now()</c> from stream creation under
    /// <c>EventAppendMode.Rich</c> - the version bump never touches it - so a panel ordered by that column
    /// is "newest", not "busiest". Calling it "recently active" over a Rich store would tell an operator
    /// that a quiet stream is busy.
    /// </summary>
    [Fact]
    public async Task A_Rich_store_calls_the_panel_newest_streams()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.EventData.Recent = new RecentStreams(
            [FakeEventDataService.Stream("order-1")], EventAppendMode.Rich, null);

        var page = context.Render<Overview>();

        page.Find(".ms-overview-streams .ms-section-title").TextContent.Trim().Should().Be("Newest streams");
        page.Find(".ms-overview-streams-note").TextContent.Should().Contain("never updates that column");
    }

    [Fact]
    public async Task A_Quick_store_calls_the_same_panel_recently_active()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.EventData.Recent = new RecentStreams(
            [FakeEventDataService.Stream("order-1")], EventAppendMode.Quick, null);

        var page = context.Render<Overview>();

        page.Find(".ms-overview-streams .ms-section-title").TextContent.Trim().Should().Be("Recently active streams");
        page.Find(".ms-overview-streams-note").TextContent.Should().Contain("updates on every append");
    }

    // -----------------------------------------------------------------------------------------------
    // Hard rule 14: the landing page must not be able to create an event store by being opened.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Every Marten call that answers for projection progress or the high-water mark opens with
    /// <c>EnsureStorageExistsAsync(typeof(IEvent))</c>, which applies the event store's migration under
    /// the database's own <c>AutoCreate</c>. So the Overview asks the cheap <c>information_schema</c>
    /// question first and only goes on when there is already something to read.
    /// </summary>
    [Fact]
    public async Task A_database_with_no_event_tables_is_never_asked_about_projections()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.EventData.Counts = EventStoreCounts.NoEventStorage;

        var page = context.Render<Overview>();

        context.ProjectionData.Reads.Should().Be(0);
        context.EventData.FeedRequests.Should().BeEmpty();

        page.Find(".ms-overview-no-events .ms-empty-title").TextContent
            .Should().Be("This database has no event storage yet");

        page.FindAll(".ms-overview-projections").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // The audit ring: five hundred entries, fifteen rows.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The ring is filtered only as far as the panel draws.
    /// </summary>
    /// <remarks>
    /// <c>GetLatest()</c> hands back the whole ring, and the per-store policy decides entry by entry - one
    /// <c>IAuthorizationService.AuthorizeAsync</c> each. Filtering all of it to render fifteen rows would
    /// be five hundred policy evaluations per refresh interval per circuit. The ring is newest first, so
    /// the fifteenth entry the visitor may see is the last one worth asking about.
    /// </remarks>
    [Fact]
    public async Task The_activity_ring_is_only_authorized_as_far_as_the_fifteen_rows_it_draws()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.WithPolicy("default");

        for (int index = 0; index < 40; index++)
        {
            context.ActionLog.Record("Archive stream", "order-" + index.ToString(CultureInfo.InvariantCulture), succeeded: true);
        }

        var page = context.Render<Overview>();

        page.TextOfAll(".ms-overview-activity .ms-overview-list-name").Should().HaveCount(15);
        context.AuthorizationService.Calls.Should().HaveCount(15, "the sweep stops at the fifteenth row it can draw");
    }

    /// <summary>
    /// With no per-store policy configured the ring costs nothing at all.
    /// </summary>
    /// <remarks>
    /// The fast path <see cref="MartenStudio.Services.StudioAuthorization.FilterAsync{T}" /> takes, and
    /// the reason an application that never set the option pays nothing for this panel.
    /// </remarks>
    [Fact]
    public async Task Without_a_store_policy_the_ring_asks_nothing()
    {
        using StudioComponentContext context = await ScopedAsync();

        for (int index = 0; index < 40; index++)
        {
            context.ActionLog.Record("Archive stream", "order-" + index.ToString(CultureInfo.InvariantCulture), succeeded: true);
        }

        var page = context.Render<Overview>();

        page.TextOfAll(".ms-overview-activity .ms-overview-list-name").Should().HaveCount(15);
        context.AuthorizationService.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// An entry aimed at a store the visitor may not see is not drawn, and the sweep still stops at
    /// fifteen rows rather than at fifteen entries.
    /// </summary>
    [Fact]
    public async Task Entries_for_a_store_the_visitor_may_not_see_are_skipped_and_not_counted()
    {
        using StudioComponentContext context = await ScopedAsync();
        context.WithPolicy("default");

        // The ring is newest first, so this records oldest first: ten that are never reached, then the
        // fifteen the panel draws, then twenty the policy refuses and the sweep has to read past.
        for (int index = 0; index < 10; index++)
        {
            context.ActionLog.Record("Archive stream", "older-" + index.ToString(CultureInfo.InvariantCulture), succeeded: true);
        }

        for (int index = 0; index < 15; index++)
        {
            context.ActionLog.Record("Archive stream", "mine-" + index.ToString(CultureInfo.InvariantCulture), succeeded: true);
        }

        for (int index = 0; index < 20; index++)
        {
            context.ActionLog.Record(
                "Archive stream",
                "theirs-" + index.ToString(CultureInfo.InvariantCulture),
                succeeded: true,
                message: null,
                capability: null,
                scope: new StudioScope("IInvoicingStore", "localhost.invoicing", null));
        }

        var page = context.Render<Overview>();

        List<string> targets = [.. page.TextOfAll(".ms-overview-activity .ms-overview-list-stream")];

        targets.Should().HaveCount(15);
        targets.Should().NotContain(x => x.Contains("theirs", StringComparison.Ordinal), "the policy refused them");
        targets.Should().NotContain(x => x.Contains("older", StringComparison.Ordinal), "the newest fifteen filled the panel");
        context.AuthorizationService.Calls.Should().HaveCount(35,
            "twenty refusals, then the fifteen it could draw, and then it stopped");
    }

    // -----------------------------------------------------------------------------------------------
    // The scope race: two runs of LoadScopeRegionsAsync, and the one that finishes last is not the one
    // that was asked last.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A read that was in flight when the selector moved does not land.
    /// </summary>
    /// <remarks>
    /// <c>LoadScopeRegionsAsync</c> is started by the poll, by the refresh button and by the scope-changed
    /// handler, and none of them waits for the others. Without a stamp the slower run wins, which is how a
    /// page ends up showing one database's numbers under another database's name - and on a
    /// multi-tenant store, one tenant's numbers under another tenant's. The gate here decides which run
    /// finishes last so that the test is about the rule rather than about the scheduler.
    /// </remarks>
    [Fact]
    public async Task A_read_that_was_in_flight_when_the_scope_moved_does_not_land()
    {
        using StudioComponentContext context = await TwoStoreContextAsync();

        var page = context.Render<Overview>();
        page.StatCardValue("Streams").Should().Be("~1,200");

        // The next counts read is held open. The run it belongs to is the one for the other store.
        TaskCompletionSource held = new();
        context.EventData.CountsGates.Enqueue(held);

        Task movedAway = context.State.SetScopeAsync("other", null, null, Xunit.TestContext.Current.CancellationToken);
        movedAway.IsCompleted.Should().BeFalse("the other store's counts read is being held open");

        // What the store the visitor came back to reports, which is a different number.
        context.EventData.Counts = new EventStoreCounts(
            EventStoreCount.Estimate(77), EventStoreCount.Estimate(88), TablesExist: true, Error: null);

        await context.State.SetScopeAsync("default", null, null, Xunit.TestContext.Current.CancellationToken);

        page.StatCardValue("Streams").Should().Be("~77", "the run for the scope the selector is on landed");

        // Now the abandoned run finishes, holding the first store's numbers.
        held.SetResult();
        await movedAway;

        page.StatCardValue("Streams").Should().Be("~77", "the abandoned run must not put the old scope's numbers back");
        context.EventData.FeedRequests.Should().HaveCount(2,
            "the abandoned run stopped where it noticed rather than reading five more regions");
    }

    /// <summary>A context with a settled scope and a store for the sections below the tiles.</summary>
    private static async Task<StudioComponentContext> ScopedAsync()
    {
        var context = new StudioComponentContext();
        context.StoreInfo.WithStore();
        await context.ReadyAsync();
        return context;
    }

    /// <summary>
    /// A context whose selector has somewhere else to go, settled on the first store.
    /// </summary>
    /// <remarks>
    /// The catalog is filled by hand rather than through <c>ReadyAsync</c>, which would add the default
    /// store a second time: <c>SetScopeAsync</c> validates against the listing the circuit was given, so
    /// both stores have to be in it before the state is initialized.
    /// </remarks>
    private static async Task<StudioComponentContext> TwoStoreContextAsync()
    {
        var context = new StudioComponentContext();
        context.StoreInfo.WithStore();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");
        context.Catalog.WithStore("other", "Other", databaseIdentities: "localhost.other");
        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        return context;
    }
}
