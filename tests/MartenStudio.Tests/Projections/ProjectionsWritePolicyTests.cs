using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services;
using MartenStudio.Tests.Components;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The projections screen's mutating controls against
/// <see cref="MartenStudioOptions.WriteAuthorizationPolicy" />, which is asked about the visitor rather
/// than about the process.
/// </summary>
/// <remarks>
/// <para>
/// Seven controls answer to three capabilities here, and a policy is free to allow one and refuse
/// another: pausing the daemon is not rebuilding a projection, and neither is moving the high water mark
/// by hand. So the page asks three questions rather than one, once per load and once per scope change,
/// and never memoises the answers — they are about this circuit's user, and a cache that outlived one is
/// how one person's answer gets shown to another.
/// </para>
/// <para>
/// Disabled and not hidden, and the refusal is said on the page as well as in the hover text: a tooltip
/// does not exist on a touch screen. None of it is the enforcement — <c>IProjectionDataService</c>
/// resolves the scope with the same capability and throws (AGENTS.md hard rule 5).
/// </para>
/// </remarks>
public class ProjectionsWritePolicyTests
{
    private const string WritePolicy = "MartenStudioWriter";

    /// <summary>The controls <c>ControlDaemon</c> answers for.</summary>
    private static string[] DaemonControls =>
        [".ms-daemon-pause", ".ms-daemon-resume", ".ms-daemon-restart-high-water", ".ms-shard-start", ".ms-shard-stop"];

    /// <summary>The controls <c>CorrectProgression</c> answers for.</summary>
    private static string[] CorrectionControls =>
        [".ms-daemon-advance-high-water", ".ms-daemon-correct-progression"];

    [Fact]
    public async Task Every_mutating_control_is_disabled_for_a_visitor_the_write_policy_refuses()
    {
        await using StudioComponentContext context = await ContextAsync(static resource => resource.Capability is null);

        var page = context.Render<Page>();

        foreach (string selector in DaemonControls)
        {
            IElement control = page.Find(selector);
            control.HasAttribute("disabled").Should().BeTrue($"'{selector}' is a write");
            control.GetAttribute("title").Should().Contain("may not control the daemon here");
        }

        foreach (string selector in CorrectionControls)
        {
            IElement control = page.Find(selector);
            control.HasAttribute("disabled").Should().BeTrue($"'{selector}' is a write");
            control.GetAttribute("title").Should().Contain("may not correct projection progress here");
        }

        IElement rebuild = page.Find(".ms-projection-rebuild");
        rebuild.HasAttribute("disabled").Should().BeTrue();
        rebuild.GetAttribute("title").Should().Contain("may not rebuild projections here");
    }

    [Fact]
    public async Task The_page_says_which_capabilities_this_account_was_refused()
    {
        await using StudioComponentContext context = await ContextAsync(static resource => resource.Capability is null);

        var page = context.Render<Page>();

        page.TextOfAll(".ms-write-refusal").Should().Equal(
            WritePolicyRefusal.For(StudioCapability.ControlDaemon),
            WritePolicyRefusal.For(StudioCapability.CorrectProgression),
            WritePolicyRefusal.For(StudioCapability.RebuildProjections));

        page.FindAll(".ms-capability-disabled").Should().BeEmpty(
            "every capability is on; it is the visitor who was refused");
    }

    /// <summary>
    /// A policy is free to allow one capability and refuse another, so the page says exactly which — and
    /// this is the test that would catch a page which collapsed the three questions into one.
    /// </summary>
    [Fact]
    public async Task A_policy_that_allows_the_daemon_and_refuses_rebuilds_disables_only_the_rebuild()
    {
        await using StudioComponentContext context = await ContextAsync(
            static resource => resource.Capability != nameof(StudioCapability.RebuildProjections));

        var page = context.Render<Page>();

        page.Find(".ms-daemon-pause").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-daemon-correct-progression").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-projection-rebuild").HasAttribute("disabled").Should().BeTrue();

        page.TextOfAll(".ms-write-refusal")
            .Should().Equal(WritePolicyRefusal.For(StudioCapability.RebuildProjections));
    }

    /// <summary>
    /// The same page for a visitor the policy allows. Without this the tests above would pass over a
    /// screen whose controls were disabled because no daemon is hosted here.
    /// </summary>
    [Fact]
    public async Task Every_control_is_live_for_a_visitor_the_same_policy_allows()
    {
        await using StudioComponentContext context = await ContextAsync(static _ => true);

        var page = context.Render<Page>();

        foreach (string selector in DaemonControls.Concat(CorrectionControls).Append(".ms-projection-rebuild"))
        {
            page.Find(selector).HasAttribute("disabled").Should().BeFalse($"'{selector}' should be offered");
        }

        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    /// <summary>
    /// A capability that is off names the option and says nothing about the account: that refusal is
    /// about the application, and the guard reaches it before a scope is ever resolved.
    /// </summary>
    [Fact]
    public async Task A_capability_that_is_off_names_the_option_rather_than_the_account()
    {
        await using var context = new StudioComponentContext();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.DenyEverything();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Markup.Should().Contain("MartenStudioOptions.Capabilities.ControlDaemon");
        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    /// <summary>
    /// A studio whose host enabled every capability, with a daemon hosted here and one projection, so
    /// every control on the page is offered for anything but the policy.
    /// </summary>
    private static async Task<StudioComponentContext> ContextAsync(Func<MartenStoreResource, bool> rule)
    {
        StudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.Allow(rule);

        // The studio's own pause is in effect, which is what makes the per-agent Start and Stop
        // meaningful: with the coordinator running they are disabled for a reason that has nothing to do
        // with the visitor, and the "allowed" half of every pair below would prove nothing.
        context.ProjectionData.WithProjection("DailySales").WithStudioPause();

        await context.ReadyAsync();
        return context;
    }
}
