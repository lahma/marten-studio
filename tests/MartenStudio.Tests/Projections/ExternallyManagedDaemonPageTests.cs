using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// A store whose async projections an external system runs, as the projections screen and the Overview
/// draw it: "External", a sentence saying whose controls these are, and no controls.
/// </summary>
/// <remarks>
/// <para>
/// Hidden rather than disabled, unlike the not-hosted case: a disabled control says "set something and
/// this works", and for an externally managed store nothing a host could set would make pausing or
/// rebuilding from here right. The progression corrections stay, because they are writes against the
/// progression table and need no daemon at all.
/// </para>
/// <para>
/// None of this is the enforcement. <see cref="DaemonLogHygieneTests" /> proves the service refuses every
/// daemon control for such a store without asking its coordinator anything (AGENTS.md hard rule 5).
/// </para>
/// </remarks>
public class ExternallyManagedDaemonPageTests
{
    [Fact]
    public async Task The_card_says_External_and_explains_whose_controls_these_are()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithExternallyManagedDaemon().WithProjection("DailySales", sequence: 900);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().StartWith("External");
        page.Find(".ms-daemon-hosting").TextContent.Trim().Should().Be("External");
        page.Find(".ms-daemon-card").ClassList.Should().Contain("ms-daemon-info", "it is a value, not an error");
        page.Find(".ms-daemon-mode").TextContent.Should().Contain("ExternallyManaged");
        page.Find(".ms-daemon-explanation").TextContent.Trim().Should().Be(DaemonAccessor.ExternallyManagedExplanation);

        // The progress is still the page: read from the database either way.
        page.Find("[data-shard='DailySales:All'] .ms-shard-lag").TextContent.Trim().Should().Be("100");
    }

    [Fact]
    public async Task The_daemon_controls_are_not_offered_and_the_progression_corrections_are()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithExternallyManagedDaemon().WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        foreach (string selector in new[]
                 {
                     ".ms-daemon-pause", ".ms-daemon-resume", ".ms-daemon-restart-high-water",
                     ".ms-shard-start", ".ms-shard-stop", ".ms-projection-rebuild",
                 })
        {
            page.FindAll(selector).Should().BeEmpty($"'{selector}' belongs to the system that runs these projections");
        }

        page.FindAll(".ms-daemon-unavailable").Should().BeEmpty("the card has already said it, in the right words");

        page.Find(".ms-daemon-advance-high-water").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-daemon-correct-progression").HasAttribute("disabled").Should().BeFalse();
    }

    /// <summary>
    /// A capability that is off is not advertised for controls that are not on the page: telling a host
    /// to set <c>ControlDaemon</c> for a store whose daemon is somebody else's would be advice that does
    /// nothing.
    /// </summary>
    [Fact]
    public async Task No_capability_notice_is_shown_for_controls_that_are_not_offered()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithExternallyManagedDaemon().WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Markup.Should().NotContain("Pausing and resuming the projection daemon");
        page.Markup.Should().Contain("Advancing the high water mark and correcting progression");
    }

    [Fact]
    public async Task Nothing_advanced_under_an_external_system_is_said_without_blaming_this_process()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithExternallyManagedDaemon().WithProjection("DailySales", sequence: 0, hasProgressRow: false);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        string banner = page.Find(".ms-no-daemon-banner").TextContent;
        banner.Should().Contain("run by an external system");
        banner.Should().NotContain("no daemon is hosted in this process");
    }

    [Fact]
    public async Task The_Overview_daemon_tile_says_external_as_a_value_and_not_an_alarm()
    {
        await using var context = new StudioComponentContext();
        context.StoreInfo.WithStore();
        context.ProjectionData.WithExternallyManagedDaemon();
        await context.ReadyAsync();

        var page = context.Render<Overview>();

        page.StatCardValue("Daemon").Should().Be("external");
        page.StatCardClasses("Daemon").Should().Contain("ms-stat-card-info");
    }

    /// <summary>
    /// The not-hosted card, for a store with nothing to run, states that and nothing else.
    /// </summary>
    [Fact]
    public async Task A_store_with_no_async_projections_is_told_so_rather_than_how_to_host_a_daemon()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithNoDaemonHere(DaemonAccessor.NoAsyncProjectionsExplanation);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Not hosted in this process");
        page.Find(".ms-daemon-explanation").TextContent.Trim()
            .Should().Be("This store has no asynchronous projections, so there is no daemon to host.");
    }
}
