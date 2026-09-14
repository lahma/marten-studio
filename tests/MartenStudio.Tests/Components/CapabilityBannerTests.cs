using Bunit;

using MartenStudio.Components.Layout;
using MartenStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The chip in the header: what the studio is allowed to do to this process's Marten stores, said out
/// loud rather than discovered by clicking something that does nothing.
/// </summary>
public class CapabilityBannerTests
{
    [Fact]
    public void A_fresh_studio_reads_as_none_of_nine()
    {
        using var context = new StudioComponentContext();

        var banner = context.Render<CapabilityBanner>();

        banner.Find(".ms-chip").TextContent.Trim().Should().Be("0 / 9 capabilities");
    }

    [Fact]
    public void All_reads_as_nine_of_nine()
    {
        using var context = new StudioComponentContext().WithAllCapabilities();

        var banner = context.Render<CapabilityBanner>();

        banner.Find(".ms-chip").TextContent.Trim().Should().Be("9 / 9 capabilities");
    }

    /// <summary>
    /// The master switch is its own chip rather than "0 / 9": a read-only studio is a deliberate state,
    /// not nine switches that happen to be off.
    /// </summary>
    [Fact]
    public void ReadOnly_is_an_amber_chip_of_its_own_and_there_is_no_popover()
    {
        using var context = new StudioComponentContext().WithAllCapabilities();
        context.Options.ReadOnly = true;

        var banner = context.Render<CapabilityBanner>();

        var chip = banner.Find(".ms-chip");
        chip.TextContent.Trim().Should().Be("Read-only");
        chip.ClassList.Should().Contain("ms-chip-warning");
        chip.GetAttribute("title").Should().Contain("MartenStudioOptions.ReadOnly");

        banner.FindAll("#ms-capability-popover").Should().BeEmpty();
    }

    /// <summary>
    /// Every row names the exact property a host would have to set (D4). "Not permitted" with nothing to
    /// act on is the failure mode of every feature-flagged admin UI.
    /// </summary>
    [Fact]
    public void The_popover_lists_every_capability_with_its_state_and_its_option_name()
    {
        using var context = new StudioComponentContext();
        context.Options.Capabilities.EditDocuments = true;

        var banner = context.Render<CapabilityBanner>();

        banner.FindAll(".ms-capability").Should().HaveCount(9);
        banner.TextOfAll(".ms-capability-option").Should().Equal(
            StudioCapabilityGuard.All.Select(x => "MartenStudioOptions.Capabilities." + x));

        var edit = banner.FindAll(".ms-capability").Single(x =>
            x.QuerySelector(".ms-capability-name")!.TextContent.Trim() == nameof(StudioCapability.EditDocuments));

        edit.ClassList.Should().Contain("ms-capability-on");
        edit.QuerySelector(".ms-capability-state")!.TextContent.Trim().Should().Be("on");

        banner.FindAll(".ms-capability-off").Should().HaveCount(8);
    }

    /// <summary>The popover is the platform's, not a menu library's (AGENTS.md hard rule 3).</summary>
    [Fact]
    public void The_popover_is_the_native_one()
    {
        using var context = new StudioComponentContext();

        var banner = context.Render<CapabilityBanner>();

        banner.Find(".ms-chip").GetAttribute("popovertarget").Should().Be("ms-capability-popover");
        banner.Find("#ms-capability-popover").GetAttribute("popover").Should().Be("auto");
    }
}
