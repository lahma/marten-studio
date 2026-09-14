using AngleSharp.Dom;

using Bunit;

using JasperFx.Events.Projections;

using MartenStudio.Services;

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
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Hosted and running");
        page.Find(".ms-daemon-card").ClassList.Should().Contain("ms-daemon-success");
        page.Find(".ms-daemon-start-all").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_daemon_hosted_here_but_stopped_is_drawn_differently_from_one_that_is_running()
    {
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
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
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.WithNoDaemonHere().WithProjection("DailySales", sequence: 900);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Not hosted in this process");
        page.Find(".ms-daemon-explanation").TextContent.Should().Contain("AddAsyncDaemon");
        page.Find(".ms-daemon-unavailable").TextContent.Should().Contain("read from the database");

        // The row is still there, with its numbers.
        page.Find("[data-shard='DailySales:All'] .ms-shard-lag").TextContent.Trim().Should().Be("100");

        // And every control that needs a daemon is disabled rather than missing.
        page.Find(".ms-daemon-start-all").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-projection-rebuild").HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// Async projections, no daemon here, and nothing has ever advanced: that combination, and only that
    /// combination, is worth a banner.
    /// </summary>
    [Fact]
    public async Task Nothing_running_the_projections_anywhere_raises_the_amber_banner()
    {
        await using var context = new ProjectionsTestContext();
        context.ProjectionData.WithNoDaemonHere().WithProjection("DailySales", sequence: 0, hasProgressRow: false);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-no-daemon-banner").Should().ContainSingle();
    }

    [Fact]
    public async Task A_daemon_that_has_advanced_somewhere_else_raises_no_banner()
    {
        await using var context = new ProjectionsTestContext();
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
        await using var context = new ProjectionsTestContext();
        context.ProjectionData.HighWaterMark = highWaterMark;
        context.ProjectionData.WithProjection("DailySales", sequence: 1_000);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All'] .ms-shard-lag").ClassList.Should().Contain(expectedClass);
    }

    [Fact]
    public async Task An_inline_projection_has_a_row_and_no_shard_progress()
    {
        await using var context = new ProjectionsTestContext();
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
        await using var context = new ProjectionsTestContext();
        context.ProjectionData.WithProjection("DailySales", sequence: 0, hasProgressRow: false, agentStatus: null);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All']").TextContent.Should().Contain("never started");
    }

    [Fact]
    public async Task A_paused_shard_shows_its_reason_and_a_skipped_count()
    {
        await using var context = new ProjectionsTestContext();
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
        await using var context = new ProjectionsTestContext().WithReadOnly();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-daemon-start-all").Should().BeEmpty();
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
        await using var context = new ProjectionsTestContext();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        IElement startAll = page.Find(".ms-daemon-start-all");
        startAll.HasAttribute("disabled").Should().BeTrue();
        startAll.GetAttribute("title").Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");

        page.Markup.Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");
        page.FindAll(".ms-capability-disabled").Should().HaveCount(2, "ControlDaemon and CorrectProgression are both off");
    }

    [Fact]
    public async Task Granting_the_capabilities_removes_the_disabled_notices()
    {
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-capability-disabled").Should().BeEmpty();
        page.Find(".ms-shard-start").HasAttribute("disabled").Should().BeFalse();
    }

    // --------------------------------------------------------------------------------------------
    // Actions
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Starting_and_stopping_a_shard_reaches_the_service_by_shard_name()
    {
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-shard-stop").Click();
        page.Find(".ms-shard-start").Click();

        context.ProjectionData.Calls.Should().Equal("StopAgent:DailySales:All", "StartAgent:DailySales:All");
    }

    /// <summary>A service refusal is what the visitor sees, not what the log sees.</summary>
    [Fact]
    public async Task A_refused_action_is_reported_on_the_page()
    {
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.ProjectionData.ActionFailure =
            new StudioCapabilityDeniedException(StudioCapability.ControlDaemon, CapabilityDenialReason.Disabled);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-start-all").Click();

        context.Toasts.Messages.Should().ContainSingle();
        context.Toasts.Messages[0].Message.Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");
    }

    [Fact]
    public async Task A_running_operation_is_shown_with_its_handle()
    {
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithRunningOperation("abc123", "DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find("[data-operation='abc123']").TextContent.Should().Contain("Rebuild DailySales");
    }

    // --------------------------------------------------------------------------------------------
    // The rebuild dialog
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_rebuild_dialog_states_the_scope_and_will_not_confirm_until_the_name_is_typed()
    {
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.HighWaterMark = 4_321;
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-projection-rebuild").Click();

        page.Find(".ms-rebuild-dialog").TextContent.Should().Contain("4,321");
        page.Find(".ms-rebuild-dialog").TextContent.Should().Contain("mt_doc_dailysales");

        // Nothing typed: the button is there and refuses.
        page.Find(".ms-rebuild-confirm").HasAttribute("disabled").Should().BeTrue();

        page.Find(".ms-rebuild-confirm").Click();
        context.ProjectionData.Calls.Should().NotContain("Rebuild:DailySales");

        // The wrong name does not do either.
        page.Find(".ms-rebuild-input").Input("dailysales");
        page.Find(".ms-rebuild-confirm").HasAttribute("disabled").Should().BeTrue();

        // The right one does.
        page.Find(".ms-rebuild-input").Input("DailySales");
        page.Find(".ms-rebuild-confirm").HasAttribute("disabled").Should().BeFalse();

        page.Find(".ms-rebuild-confirm").Click();

        context.ProjectionData.Calls.Should().Contain("Rebuild:DailySales");
    }

    [Fact]
    public async Task Cancelling_the_rebuild_dialog_starts_nothing()
    {
        await using var context = new ProjectionsTestContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-projection-rebuild").Click();
        page.Find(".ms-rebuild-cancel").Click();

        page.FindAll(".ms-rebuild-dialog").Should().BeEmpty();
        context.ProjectionData.Calls.Should().NotContain("Rebuild:DailySales");
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
        var context = new ProjectionsTestContext();
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
        await using var context = new ProjectionsTestContext();
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
        await using var context = new ProjectionsTestContext();
        context.ProjectionData.Failure = new InvalidOperationException("the database went away");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("the database went away");
    }
}
