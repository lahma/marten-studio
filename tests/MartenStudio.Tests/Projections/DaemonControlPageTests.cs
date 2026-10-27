using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using Page = MartenStudio.Components.Pages.Projections.Projections;
using PostgresStudio.Services.Live;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The projections screen after P5-fix-2: Pause and Resume where Start all and Stop all used to be, and
/// per-agent controls that say why they are disabled rather than pretending to work.
/// </summary>
/// <remarks>
/// On <see cref="StudioComponentContext" />'s <c>configure</c> hook rather than a context class of its
/// own - every page area needs exactly the same thing, which is why the hook exists. The projections
/// fake is supplied here and everything else is the studio's real service.
/// </remarks>
public class DaemonControlPageTests
{
    /// <summary>The confirm button of whichever dialog is open. There is never more than one.</summary>
    private const string ConfirmButton = ".ms-confirm-actions .ms-button-danger";

    [Fact]
    public async Task The_card_offers_pause_and_resume_rather_than_start_all_and_stop_all()
    {
        FakeProjectionDataService data = new FakeProjectionDataService().WithProjection("DailySales");

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-daemon-pause").TextContent.Trim().Should().Be("Pause daemon");
        page.Find(".ms-daemon-resume").TextContent.Trim().Should().Be("Resume daemon");

        // The two controls that lied are gone, not merely relabelled.
        page.FindAll(".ms-daemon-start-all").Should().BeEmpty();
        page.FindAll(".ms-daemon-stop-all").Should().BeEmpty();
    }

    /// <summary>
    /// The coordinator has no public paused flag, so the card reports the pause this process issued and
    /// says who issued it.
    /// </summary>
    [Fact]
    public async Task A_pause_this_studio_issued_is_named_on_the_card_with_who_and_when()
    {
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithStudioPause("alice", DateTimeOffset.UnixEpoch);

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Hosted, paused by Marten Studio");
        page.Find(".ms-daemon-card").ClassList.Should().Contain("ms-daemon-paused");

        string pausedBy = page.Find(".ms-daemon-paused-by").TextContent;
        pausedBy.Should().Contain("Paused by Marten Studio");
        pausedBy.Should().Contain("alice");
        pausedBy.Should().Contain("1970", "the time is formatted in the visitor's selected zone");

        page.Find(".ms-daemon-pause-effect").TextContent
            .Should().Contain("every database this process hosts");
    }

    /// <summary>
    /// A daemon that is hosted and running, with no studio pause, draws the way it always did - the
    /// paused-by line is only ever about a pause this process made.
    /// </summary>
    [Fact]
    public async Task A_running_daemon_says_nothing_about_a_pause_nobody_made()
    {
        FakeProjectionDataService data = new FakeProjectionDataService().WithProjection("DailySales");

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim().Should().Be("Hosted and running");
        page.FindAll(".ms-daemon-paused-by").Should().BeEmpty();
    }

    /// <summary>
    /// Disabled and not hidden (the same choice as every other capability-gated control): the option has
    /// to stay discoverable, and the reason has to be readable without hovering.
    /// </summary>
    [Fact]
    public async Task Per_agent_controls_are_disabled_with_the_reason_while_the_coordinator_runs()
    {
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithLeadershipPollingTime(1_000, agentPauseMilliseconds: 1_000);

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement start = page.Find(".ms-shard-start");
        IElement stop = page.Find(".ms-shard-stop");

        start.HasAttribute("disabled").Should().BeTrue();
        stop.HasAttribute("disabled").Should().BeTrue();

        const string reason =
            "The projection coordinator restarts agents as often as every 1 s (LeadershipPollingTime 1 s, " +
            "or AgentPauseTime 1 s while any shard is paused); pause the daemon first.";

        start.GetAttribute("title").Should().Be(reason);
        stop.GetAttribute("title").Should().Be(reason);

        // And on the page, not only in hover text: the hint names the thing to do about it.
        page.Find(".ms-shard-control-hint").TextContent.Trim().Should().Be(reason);
    }

    [Fact]
    public async Task Per_agent_controls_are_enabled_while_the_studio_pause_is_in_effect()
    {
        // An agent started by hand during the pause and since paused by an error: one Start would start and Stop would stop.
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithStudioPause()
            .WithAgentHere("DailySales:All", "Paused");

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-shard-start").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-shard-stop").HasAttribute("disabled").Should().BeFalse();
        page.FindAll(".ms-shard-control-hint").Should().BeEmpty();

        page.Find(".ms-shard-stop").Click();

        page.WaitForAssertion(() => data.Calls.Should().Equal("StopAgent:DailySales:All"));
    }

    /// <summary>
    /// M7: after the studio's pause no agent runs here, so Stop would stop nothing - disabled, and saying so - while Start,
    /// which would start one, is offered. With the agent running, it is the other way round.
    /// </summary>
    [Fact]
    public async Task While_paused_a_Stop_with_no_agent_here_and_a_Start_of_a_running_one_say_they_would_do_nothing()
    {
        FakeProjectionDataService paused = new FakeProjectionDataService().WithProjection("DailySales").WithStudioPause();

        await using (StudioComponentContext context = await ReadyAsync(paused))
        {
            IRenderedComponent<Page> page = context.Render<Page>();

            page.Find(".ms-shard-start").HasAttribute("disabled").Should().BeFalse();
            page.Find(".ms-shard-stop").HasAttribute("disabled").Should().BeTrue();
            page.Find(".ms-shard-stop").GetAttribute("title").Should().Be(DaemonTopologyWording.NothingToStop("DailySales:All", hotCold: false));
        }

        FakeProjectionDataService started = new FakeProjectionDataService().WithProjection("DailySales").WithStudioPause().WithAgentHere("DailySales:All");

        await using (StudioComponentContext context = await ReadyAsync(started))
        {
            IRenderedComponent<Page> page = context.Render<Page>();

            page.Find(".ms-shard-start").HasAttribute("disabled").Should().BeTrue();
            page.Find(".ms-shard-start").GetAttribute("title").Should().Be(DaemonTopologyWording.AlreadyRunning("DailySales:All"));
            page.Find(".ms-shard-stop").HasAttribute("disabled").Should().BeFalse();
        }
    }

    /// <summary>
    /// M7: under HotCold a node that does not lead a shard says so on the row, and refuses to start it beside the node that
    /// does - with the studio's pause in effect, which is when Start is offered at all.
    /// </summary>
    [Fact]
    public async Task Under_HotCold_a_shard_another_node_leads_is_marked_and_its_Start_is_refused()
    {
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithProjection("ShipmentTracker")
            .WithStudioPause()
            .WithCoordination(
                DaemonCoordination.LeadershipLoop,
                DaemonDistribution.PerShard,
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["DailySales:All"] = false, ["ShipmentTracker:All"] = true });

        data.Daemon = data.Daemon with { Mode = "HotCold" };

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All'] .ms-shard-node").TextContent.Should().Be("another node");
        page.Find("[data-shard='ShipmentTracker:All'] .ms-shard-node").TextContent.Should().Be("this node");

        page.Find("[data-shard='DailySales:All'] .ms-shard-start").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-shard='DailySales:All'] .ms-shard-start").GetAttribute("title").Should().Be(DaemonTopologyWording.LedElsewhere("DailySales:All"));
        page.Find("[data-shard='ShipmentTracker:All'] .ms-shard-start").HasAttribute("disabled").Should().BeFalse();

        page.Find(".ms-daemon-topology").TextContent.Should().Contain("HotCold").And.Contain("holds the lock for 1 of this database's 2 shards");
    }

    /// <summary>
    /// M7: <c>MartenDaemonModeIsSolo()</c>'s coordinator has no leadership loop, so a Start or a Stop means what it says
    /// without a pause - and the card says why.
    /// </summary>
    [Fact]
    public async Task With_no_leadership_loop_Start_and_Stop_are_offered_without_a_pause()
    {
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithAgentHere("DailySales:All")
            .WithCoordination(DaemonCoordination.Explicit);

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.FindAll(".ms-shard-control-hint").Should().BeEmpty("there is no loop to pause first");
        page.Find(".ms-shard-stop").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-shard-start").HasAttribute("disabled").Should().BeTrue("its agent is running already");
        page.Find(".ms-daemon-topology").TextContent.Should().StartWith("MartenDaemonModeIsSolo(): a coordinator with no leadership loop.");
    }

    /// <summary>
    /// A pause the host lifted itself takes the controls away again, and the card says which of the two
    /// facts is the true one.
    /// </summary>
    /// <remarks>
    /// The studio's record of its own pause is all <c>IsPausedByStudio</c> can mean - the coordinator has
    /// no public paused flag - but the daemon's <c>IsRunning</c> is live, and a running daemon means the
    /// leadership loop is back. Enabling per-agent controls on the record alone would offer a stop that
    /// the loop undoes within a poll, which is exactly what the pause requirement exists to prevent.
    /// </remarks>
    [Fact]
    public async Task A_studio_pause_the_host_lifted_disables_the_per_agent_controls_and_says_so()
    {
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithStudioPauseThatWasLifted();

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-daemon-headline").TextContent.Trim()
            .Should().Be("Hosted and running - the studio's pause is no longer in effect");

        page.Find(".ms-daemon-paused-by").Should().NotBeNull("the studio really did pause it");
        page.Find(".ms-daemon-pause-lifted").TextContent
            .Should().Contain("this daemon reports that it is running");
        page.FindAll(".ms-daemon-pause-effect").Should().BeEmpty(
            "the sentence about what a pause reaches is about a pause that is in effect");

        page.Find(".ms-shard-start").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-shard-stop").HasAttribute("disabled").Should().BeTrue();

        page.Find(".ms-shard-control-hint").TextContent.Trim()
            .Should().StartWith("Marten Studio's pause is still on the record");
    }

    /// <summary>
    /// Pausing is bigger than the page it is on, so the dialog says so and names every database it
    /// reaches rather than the one the scope selector is showing.
    /// </summary>
    [Fact]
    public async Task Pausing_asks_first_and_the_dialog_states_the_process_wide_effect()
    {
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithDatabases("localhost.marten", "reporting.marten");

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-daemon-pause").Click();

        IElement dialog = page.WaitForElement(".ms-confirm-dialog");

        dialog.TextContent.Should().Contain("Pause the projection daemon?");
        dialog.TextContent.Should().Contain("every database this process hosts");
        dialog.TextContent.Should().Contain("localhost.marten, reporting.marten");

        data.Calls.Should().BeEmpty("nothing happens until it is confirmed");

        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => data.Calls.Should().Equal("PauseDaemon"));
    }

    [Fact]
    public async Task Resuming_asks_first_and_then_reaches_the_service()
    {
        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithStudioPause();

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-daemon-resume").Click();

        IElement dialog = page.WaitForElement(".ms-confirm-dialog");
        dialog.TextContent.Should().Contain("Resume the projection daemon?");

        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => data.Calls.Should().Equal("ResumeDaemon"));
    }

    /// <summary>
    /// A refusal that comes back from the service anyway - a stale render, a second circuit - is a hint,
    /// never a red toast. Nothing failed, and nothing was changed.
    /// </summary>
    [Fact]
    public async Task A_refused_agent_control_is_a_hint_on_the_page_and_not_an_error_toast()
    {
        const string reason =
            "The projection coordinator restarts agents every 5 s (LeadershipPollingTime); pause the daemon first.";

        FakeProjectionDataService data = new FakeProjectionDataService()
            .WithProjection("DailySales")
            .WithStudioPause();

        data.AgentControl = DaemonControlResult.Refused(reason);

        await using StudioComponentContext context = await ReadyAsync(data);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find(".ms-shard-start").Click();

        page.WaitForAssertion(() => page.Find(".ms-agent-refusal").TextContent.Trim().Should().Be(reason));

        context.Toasts.Messages.Should().BeEmpty("a refusal changed nothing and failed at nothing");
    }

    /// <summary>
    /// Under <c>ReadOnly</c> there is no capability a host could turn on to bring these back without
    /// turning the master switch off first, so they are not rendered at all.
    /// </summary>
    [Fact]
    public async Task ReadOnly_renders_neither_pause_nor_resume_nor_the_per_agent_controls()
    {
        FakeProjectionDataService data = new FakeProjectionDataService().WithProjection("DailySales");

        await using StudioComponentContext context = await ReadyAsync(data, readOnly: true);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.FindAll(".ms-daemon-pause").Should().BeEmpty();
        page.FindAll(".ms-daemon-resume").Should().BeEmpty();
        page.FindAll(".ms-shard-start").Should().BeEmpty();
    }

    /// <summary>
    /// With the capability off, both controls are disabled and name the exact property a host would set
    /// (D4) - and the capability, not the coordinator, is the reason that is shown.
    /// </summary>
    [Fact]
    public async Task Without_ControlDaemon_the_controls_name_the_option_rather_than_the_coordinator()
    {
        FakeProjectionDataService data = new FakeProjectionDataService().WithProjection("DailySales");

        await using StudioComponentContext context = await ReadyAsync(data, allCapabilities: false);

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement pause = page.Find(".ms-daemon-pause");

        pause.HasAttribute("disabled").Should().BeTrue();
        pause.GetAttribute("title").Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");

        page.Find(".ms-shard-start").GetAttribute("title")
            .Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");

        // The coordinator hint is about a control the visitor could otherwise use; with the capability
        // off it would be a second, misleading reason.
        page.FindAll(".ms-shard-control-hint").Should().BeEmpty();
        page.Markup.Should().Contain("Pausing and resuming the projection daemon");
    }

    /// <summary>
    /// One context, wired the way every page area wires one: the studio's real services plus this area's
    /// hand-written fake, and the scope settled before anything renders.
    /// </summary>
    private static async Task<StudioComponentContext> ReadyAsync(
        FakeProjectionDataService data,
        bool allCapabilities = true,
        bool readOnly = false)
    {
        StudioComponentContext context = new(services =>
        {
            services.AddSingleton<IProjectionDataService>(data);

            // The page builds its own polling loop from this, so the circuit's container never tracks one.
            services.AddScoped<StudioLiveUpdatesFactory>();
        });

        context.WithStores("default");

        if (allCapabilities || readOnly)
        {
            context.WithAllCapabilities();
        }

        context.Options.ReadOnly = readOnly;

        await context.State.EnsureInitializedAsync();

        return context;
    }
}
