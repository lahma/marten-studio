using AngleSharp.Dom;

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
    public void A_fresh_studio_reads_as_none_of_ten()
    {
        using var context = new StudioComponentContext();

        var banner = context.Render<CapabilityBanner>();

        banner.Find(".ms-chip").TextContent.Trim().Should().Be("0 / 10 capabilities");
    }

    [Fact]
    public void All_reads_as_ten_of_ten()
    {
        using var context = new StudioComponentContext().WithAllCapabilities();

        var banner = context.Render<CapabilityBanner>();

        banner.Find(".ms-chip").TextContent.Trim().Should().Be("10 / 10 capabilities");
    }

    /// <summary>
    /// The master switch is its own chip rather than "0 / 10": a read-only studio is a deliberate state,
    /// not ten switches that happen to be off.
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
    /// <summary>
    /// UX-6, U9: a viewer the write policy refuses everything saw "10 / 10 capabilities" and read it as
    /// their own. The chip describes the studio's configuration, and the popover says so before it lists it -
    /// without trying to compute what this visitor may do, which the policies answer per call and scope.
    /// </summary>
    [Fact]
    public void The_popover_says_it_describes_the_studio_and_not_the_visitor()
    {
        using var context = new StudioComponentContext();
        context.Options.Capabilities = MartenStudioCapabilities.All();

        var banner = context.Render<CapabilityBanner>();

        IElement lead = banner.Find("#ms-capability-popover > p");
        lead.ClassList.Should().Contain("ms-popover-lead", "it comes first, before either list");
        lead.TextContent.Trim().Should().Be("What this studio allows. Your account's policies decide what you may do.");
        banner.Find(".ms-chip").TextContent.Trim().Should().Be("10 / 10 capabilities", "the count itself is unchanged");
    }

    [Fact]
    public void The_popover_lists_every_capability_with_its_state_and_its_option_name()
    {
        using var context = new StudioComponentContext();
        context.Options.Capabilities.EditDocuments = true;

        var banner = context.Render<CapabilityBanner>();

        banner.FindAll(".ms-capability").Should().HaveCount(10);
        banner.TextOfAll(".ms-capability-option").Should().Equal(
            StudioCapabilityGuard.ReadsBeyondTheStore.Concat(StudioCapabilityGuard.Mutating)
                .Select(x => "MartenStudioOptions.Capabilities." + x));

        var edit = banner.FindAll(".ms-capability").Single(x =>
            x.QuerySelector(".ms-capability-name")!.TextContent.Trim() == nameof(StudioCapability.EditDocuments));

        edit.ClassList.Should().Contain("ms-capability-on");
        edit.QuerySelector(".ms-capability-state")!.TextContent.Trim().Should().Be("on");

        banner.FindAll(".ms-capability-off").Should().HaveCount(9);
    }

    /// <summary>
    /// Two groups, because two kinds of switch: the console and the database browser change nothing, and
    /// a list headed "Mutating operations" that held them would describe a narrower switch than the host set.
    /// </summary>
    [Fact]
    public void The_popover_groups_the_reads_beyond_the_store_apart_from_the_mutating_operations()
    {
        using var context = new StudioComponentContext();

        var banner = context.Render<CapabilityBanner>();

        banner.TextOfAll(".ms-popover-title").Should().Equal("Reads beyond the store", "Mutating operations");

        var groups = banner.FindAll(".ms-capability-list");
        groups.Should().HaveCount(2);

        groups[0].QuerySelectorAll(".ms-capability-name").Select(static x => x.TextContent.Trim())
            .Should().Equal(nameof(StudioCapability.RunSql), nameof(StudioCapability.BrowseDatabase));

        groups[1].QuerySelectorAll(".ms-capability-name").Select(static x => x.TextContent.Trim())
            .Should().HaveCount(8).And.NotContain(nameof(StudioCapability.RunSql))
            .And.NotContain(nameof(StudioCapability.BrowseDatabase));
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

    /// <summary>
    /// UX-7: an option's name may wrap after each of its dots - and only there, before the stylesheet's last
    /// resort - so a popover narrower than the name shows it whole on two lines rather than cutting it off.
    /// The break is a <c>&lt;wbr&gt;</c>, which adds no character: the name reads and copies as written.
    /// </summary>
    [Fact]
    public void An_option_name_may_wrap_after_its_dots_and_still_reads_whole()
    {
        using var context = new StudioComponentContext();

        var banner = context.Render<CapabilityBanner>();

        banner.Find("#ms-capability-popover").ClassList.Should().Contain("ms-capability-popover");

        IElement option = banner.FindAll(".ms-capability-option").Single(static x =>
            x.TextContent.EndsWith(nameof(StudioCapability.ApplySchemaChanges), StringComparison.Ordinal));

        option.TextContent.Should().Be("MartenStudioOptions.Capabilities.ApplySchemaChanges");
        option.InnerHtml.Should().Be("MartenStudioOptions.<wbr>Capabilities.<wbr>ApplySchemaChanges");
    }
}
