using AngleSharp.Dom;

using Bunit;

using JasperFx.Events.Projections;

using MartenStudio.Services;
using MartenStudio.Tests.Components;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The projections screen: the daemon card's three states, the lag colours, the gating, and the rebuild
/// dialog that will not confirm until the projection's name has been typed.
/// </summary>
public class ProjectionsPageTests
{
    [Fact]
    public async Task A_hosted_running_daemon_is_said_so_and_its_controls_are_offered()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Hosted and running");
        page.Find(".ms-daemon-card").ClassList.Should().Contain("ms-daemon-success");
        page.Find(".ms-daemon-pause").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_daemon_hosted_here_but_stopped_is_drawn_differently_from_one_that_is_running()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithStoppedDaemon().WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Hosted, stopped");
        page.Find(".ms-daemon-card").ClassList.Should().Contain("ms-daemon-error");
    }

    /// <summary>
    /// A daemon somewhere else is a supported deployment, not a fault: the progress still renders, the
    /// controls do not, and the card says why in plain words (AGENTS.md hard rule 11).
    /// </summary>
    [Fact]
    public async Task A_daemon_hosted_elsewhere_still_renders_progress_and_explains_the_missing_controls()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithNoDaemonHere().WithProjection("DailySales", sequence: 900);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Not hosted in this process");
        page.Find(".ms-daemon-explanation").TextContent.Should().Contain("AddAsyncDaemon");
        page.Find(".ms-daemon-unavailable").TextContent.Should().Contain("read from the database");

        // The row is still there, with its numbers.
        page.Find("[data-shard='DailySales:All'] .ms-shard-lag").TextContent.Trim().Should().Be("100");

        // And every control that needs a daemon is disabled rather than missing.
        page.Find(".ms-daemon-pause").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-projection-rebuild").HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// Async projections, no daemon here, and nothing has ever advanced: that combination, and only that
    /// combination, is worth a banner.
    /// </summary>
    [Fact]
    public async Task Nothing_running_the_projections_anywhere_raises_the_amber_banner()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithNoDaemonHere().WithProjection("DailySales", sequence: 0, hasProgressRow: false);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-no-daemon-banner").Should().ContainSingle();
    }

    /// <summary>
    /// A database with no event tables is why everything below reads "never started", and it is not the
    /// same problem as a daemon nobody is hosting — so it is said in its own words and the daemon banner
    /// stands down. Marten creates those tables on the first append; the studio never does (hard rule 14).
    /// </summary>
    [Fact]
    public async Task A_database_with_no_event_store_says_so_instead_of_blaming_the_daemon()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.HasEventStore = false;
        context.ProjectionData.WithNoDaemonHere().WithProjection("DailySales", sequence: 0, hasProgressRow: false);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-empty-title").TextContent.Should().Contain("No event store in this database");
        page.FindAll(".ms-no-daemon-banner").Should().BeEmpty(
            "the daemon is not the reason, and pointing an operator at it would send them the wrong way");
    }

    [Fact]
    public async Task A_daemon_that_has_advanced_somewhere_else_raises_no_banner()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithNoDaemonHere().WithProjection("DailySales", sequence: 900);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-no-daemon-banner").Should().BeEmpty();
    }

    [Theory]
    [InlineData(1_000, "ms-lag-ok")]
    [InlineData(2_500, "ms-lag-warning")]
    [InlineData(200_000, "ms-lag-critical")]
    public async Task Lag_wears_the_colour_of_its_severity(long highWaterMark, string expectedClass)
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.HighWaterMark = highWaterMark;
        context.ProjectionData.WithProjection("DailySales", sequence: 1_000);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All'] .ms-shard-lag").ClassList.Should().Contain(expectedClass);
    }

    [Fact]
    public async Task An_inline_projection_has_a_row_and_no_shard_progress()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("OrderSummary", ProjectionLifecycle.Inline);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-projection='OrderSummary'] .ms-lifecycle").TextContent.Trim().Should().Be("Inline");
        page.Find("[data-projection='OrderSummary'] .ms-projection-note").TextContent
            .Should().Contain("does not run this");
        page.FindAll(".ms-shard-row").Should().BeEmpty();
    }

    /// <summary>
    /// A shard that has never started is not a shard sitting at zero, and the page must not draw them the
    /// same way (plan section 4.8).
    /// </summary>
    [Fact]
    public async Task A_shard_that_has_never_started_says_so_rather_than_showing_a_zero()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("DailySales", sequence: 0, hasProgressRow: false, agentStatus: null);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All']").TextContent.Should().Contain("never started");
    }

    [Fact]
    public async Task A_paused_shard_shows_its_reason_and_a_skipped_count()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection(
            "ShipmentTracker", agentStatus: "Paused", pauseReason: "Too many errors", skipped: 3);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-shard='ShipmentTracker:All'] .ms-shard-pause").TextContent.Should().Contain("Too many errors");
        page.Find("[data-shard='ShipmentTracker:All'] .ms-shard-skipped").TextContent.Should().Contain("3");
    }

    // --------------------------------------------------------------------------------------------
    // Gating
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// Under <c>ReadOnly</c> the controls are not rendered at all: there is no capability a host could
    /// turn on to bring them back without turning the master switch off first.
    /// </summary>
    [Fact]
    public async Task ReadOnly_hides_every_control()
    {
        await using var context = new StudioComponentContext().WithReadOnly();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-daemon-pause").Should().BeEmpty();
        page.FindAll(".ms-daemon-resume").Should().BeEmpty();
        page.FindAll(".ms-projection-rebuild").Should().BeEmpty();
        page.FindAll(".ms-shard-start").Should().BeEmpty();
    }

    /// <summary>
    /// With the master switch off but the capability not granted, the control is rendered disabled and
    /// names the exact property a host would set (D4).
    /// </summary>
    [Fact]
    public async Task A_capability_that_is_off_disables_its_controls_and_names_the_option()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        IElement pause = page.Find(".ms-daemon-pause");
        pause.HasAttribute("disabled").Should().BeTrue();
        pause.GetAttribute("title").Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");

        page.Markup.Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");
        page.FindAll(".ms-capability-disabled").Should().HaveCount(2, "ControlDaemon and CorrectProgression are both off");
    }

    /// <summary>
    /// With the capabilities granted the notices go, and the per-agent controls come back - but only
    /// once the coordinator has been paused, because with it running they would undo themselves.
    /// </summary>
    [Fact]
    public async Task Granting_the_capabilities_removes_the_disabled_notices()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithStudioPause();
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-capability-disabled").Should().BeEmpty();
        page.Find(".ms-daemon-pause").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-shard-start").HasAttribute("disabled").Should().BeFalse();
    }

    // --------------------------------------------------------------------------------------------
    // Actions
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// With the coordinator paused the per-agent controls act on the daemon, and they name the shard.
    /// </summary>
    [Fact]
    public async Task Starting_and_stopping_a_shard_reaches_the_service_by_shard_name()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithStudioPause();
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-shard-stop").Click();
        page.WaitForAssertion(() => context.ProjectionData.Calls.Should().Equal("StopAgent:DailySales:All"));

        page.Find(".ms-shard-start").Click();

        page.WaitForAssertion(() => context.ProjectionData.Calls
            .Should().Equal("StopAgent:DailySales:All", "StartAgent:DailySales:All"));
    }

    /// <summary>A service refusal is what the visitor sees, not what the log sees.</summary>
    [Fact]
    public async Task A_refused_action_is_reported_on_the_page()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.ProjectionData.ActionFailure =
            new StudioCapabilityDeniedException(StudioCapability.ControlDaemon, CapabilityDenialReason.Disabled);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-restart-high-water").Click();

        page.WaitForAssertion(() =>
        {
            context.Toasts.Messages.Should().ContainSingle();
            context.Toasts.Messages[0].Message.Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");
        });
    }

    [Fact]
    public async Task A_running_operation_is_shown_with_its_handle()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithRunningOperation("abc123", "DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-operation='abc123']").TextContent.Should().Contain("Rebuild DailySales");
    }

    // --------------------------------------------------------------------------------------------
    // The rebuild dialog
    // --------------------------------------------------------------------------------------------

    /// <summary>The confirm button of whichever dialog is open. There is never more than one.</summary>
    private const string ConfirmButton = ".ms-confirm-actions .ms-button-danger";

    /// <summary>Its Cancel button.</summary>
    private const string CancelButton = ".ms-confirm-actions .ms-button:not(.ms-button-danger)";

    /// <summary>
    /// Why every click below is followed by a <c>WaitFor…</c> rather than a <c>Find</c>.
    /// </summary>
    /// <remarks>
    /// Opening the rebuild dialog awaits <c>DescribeRebuildAsync</c> before <c>rebuild</c> is set, and a
    /// bUnit <c>Click()</c> returns as soon as the handler yields - so the dialog is not in the markup
    /// yet on the next line. In Debug on a warm machine the continuation usually runs first and the bare
    /// <c>Find</c> passed; on GitHub Actions in Release it did not, and
    /// <c>The_rebuild_dialog_states_the_scope_and_will_not_confirm_until_the_name_is_typed</c> failed
    /// with <c>No elements were found that matches the selector '.ms-confirm-dialog'</c>. The rule for
    /// this file: after any action whose handler awaits anything, wait on the assertion.
    /// </remarks>
    private const string ConfirmDialog = ".ms-confirm-dialog";

    [Fact]
    public async Task The_rebuild_dialog_states_the_scope_and_will_not_confirm_until_the_name_is_typed()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.HighWaterMark = 4_321;
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-projection-rebuild").Click();

        IElement dialog = page.WaitForElement(ConfirmDialog);
        dialog.TextContent.Should().Contain("4,321");
        dialog.TextContent.Should().Contain("mt_doc_dailysales");

        // Nothing typed: the button is there and refuses.
        page.Find(ConfirmButton).HasAttribute("disabled").Should().BeTrue();

        page.Find(ConfirmButton).Click();
        context.ProjectionData.Calls.Should().NotContain("Rebuild:DailySales");

        // The wrong name does not do either.
        page.Find(".ms-confirm-input").Input("dailysales");
        page.WaitForAssertion(() => page.Find(ConfirmButton).HasAttribute("disabled").Should().BeTrue());

        // The right one does.
        page.Find(".ms-confirm-input").Input("DailySales");
        page.WaitForAssertion(() => page.Find(ConfirmButton).HasAttribute("disabled").Should().BeFalse());

        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => context.ProjectionData.Calls.Should().Contain("Rebuild:DailySales"));
    }

    /// <summary>
    /// B1: Marten tears the projection's tables down before the per-shard timeout starts applying to the
    /// replay, so the budget is part of what a person is consenting to and the dialog has to say it.
    /// </summary>
    [Fact]
    public async Task The_rebuild_dialog_states_the_per_shard_timeout_and_that_the_tables_are_emptied_first()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.ShardTimeout = TimeSpan.FromHours(2);
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-projection-rebuild").Click();

        page.WaitForElement(".ms-rebuild-timeout").TextContent.Should().Contain("2 h");

        string note = page.Find(".ms-rebuild-timeout-note").TextContent;
        note.Should().Contain("emptied first");
        note.Should().Contain("MartenStudioOptions.RebuildShardTimeout");
    }

    /// <summary>
    /// F7: the typed confirmation is the shared dialog's, so there is one implementation of the rule
    /// rather than one per screen that needs it.
    /// </summary>
    [Fact]
    public async Task The_rebuild_dialog_uses_the_shared_confirm_dialog()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-projection-rebuild").Click();

        page.WaitForAssertion(() => page.FindAll(ConfirmDialog).Should().ContainSingle());
        page.FindAll(".ms-confirm-input").Should().ContainSingle();
        page.FindAll(".ms-rebuild-input").Should().BeEmpty("the private type-to-confirm box is gone");
        page.Find(".ms-rebuild-scope").TextContent.Should().Contain("DailySales:All");
    }

    [Fact]
    public async Task Cancelling_the_rebuild_dialog_starts_nothing()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-projection-rebuild").Click();
        page.WaitForElement(ConfirmDialog);

        page.Find(CancelButton).Click();

        page.WaitForAssertion(() => page.FindAll(ConfirmDialog).Should().BeEmpty());
        context.ProjectionData.Calls.Should().NotContain("Rebuild:DailySales");
    }

    // --------------------------------------------------------------------------------------------
    // One rebuild per projection, and stopping one
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// B3: two rebuilds of one projection would race over the same tables, so the button goes away while
    /// one is running - on every circuit, because the tracker the page reads is the singleton.
    /// </summary>
    [Fact]
    public async Task A_projection_that_is_already_being_rebuilt_cannot_be_rebuilt_again()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData
            .WithProjection("DailySales")
            .WithProjection("ShipmentTracker")
            .WithRunningOperation("abc123", "DailySales");

        await context.ReadyAsync();

        var page = context.Render<Page>();

        IElement daily = page.Find("[data-projection='DailySales'] .ms-projection-rebuild");
        daily.HasAttribute("disabled").Should().BeTrue();
        daily.GetAttribute("title").Should().Contain("already running");

        page.Find("[data-projection='ShipmentTracker'] .ms-projection-rebuild")
            .HasAttribute("disabled").Should().BeFalse("only the projection being rebuilt is held back");
    }

    /// <summary>
    /// The service answers a second rebuild with the running one rather than starting anything, and the
    /// page says so rather than claiming it started something.
    /// </summary>
    [Fact]
    public async Task A_rebuild_that_was_already_running_is_reported_as_such()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.ProjectionData.RebuildStarts = false;
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-projection-rebuild").Click();
        page.WaitForElement(ConfirmDialog);

        page.Find(".ms-confirm-input").Input("DailySales");
        page.WaitForAssertion(() => page.Find(ConfirmButton).HasAttribute("disabled").Should().BeFalse());

        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => context.Toasts.Messages.Select(x => x.Message)
            .Should().Contain(x => x.Contains("already being rebuilt", StringComparison.Ordinal)));
    }

    /// <summary>
    /// F8: cancelling a replay leaves a partial projection, so it is offered behind the same capability
    /// that started it and behind a confirmation that says what it costs.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_running_operation_asks_first_and_then_reaches_the_service()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithRunningOperation("abc123", "DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-operation='abc123'] .ms-operation-cancel").Click();

        page.WaitForElement(ConfirmDialog).TextContent.Should().Contain("leaves the projection partial");
        context.ProjectionData.Calls.Should().NotContain("CancelOperation:abc123");

        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => context.ProjectionData.Calls.Should().Contain("CancelOperation:abc123"));
    }

    [Fact]
    public async Task Without_the_rebuild_capability_a_running_operation_offers_no_cancel()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("DailySales").WithRunningOperation("abc123", "DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll("[data-operation='abc123'] .ms-operation-cancel").Should().BeEmpty();
    }

    // --------------------------------------------------------------------------------------------
    // Unregistered shards
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// F2: a progression row nothing claims is the reason a stale table is still there. It gets its own
    /// section rather than being invented into a projection row or dropped on the floor.
    /// </summary>
    [Fact]
    public async Task Progression_rows_no_projection_claims_are_rendered_under_their_own_heading()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData
            .WithProjection("DailySales")
            .WithUnregisteredShard("RetiredProjection:All", sequence: 17);

        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-unregistered-shards").TextContent.Should().Contain("mt_event_progression");
        page.Find("[data-unregistered-shard='RetiredProjection:All']").TextContent.Should().Contain("17");

        // And it is not smuggled into the projections table as a shard of something that is registered.
        page.FindAll("[data-shard='RetiredProjection:All']").Should().BeEmpty();
    }

    [Fact]
    public async Task With_nothing_unregistered_the_section_is_absent()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-unregistered-shards").Should().BeEmpty();
    }

    // --------------------------------------------------------------------------------------------
    // Live state
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// The page takes a tracker lease when it opens and gives it back when it goes: a studio nobody has
    /// open observes nothing.
    /// </summary>
    [Fact]
    public async Task The_page_leases_the_tracker_while_it_is_open_and_releases_it_on_dispose()
    {
        var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();
        page.Should().NotBeNull();

        context.ProjectionData.Subscriptions.Should().Be(1);
        context.ProjectionData.Releases.Should().Be(0);

        await context.DisposeAsync();

        context.ProjectionData.Releases.Should().Be(1);
    }

    [Fact]
    public async Task A_live_shard_is_marked_as_coming_from_the_tracker()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("DailySales", isLive: true);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All'] .ms-shard-live").TextContent.Trim().Should().Be("live");
    }

    // --------------------------------------------------------------------------------------------
    // Failure
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_that_fails_is_reported_on_the_page_with_a_retry()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.Failure = new InvalidOperationException("the database went away");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("the database went away");
    }
}
