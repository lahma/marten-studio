using Bunit;

using MartenStudio.Components.Shared;
using MartenStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>
/// What a page renders where a control would be when the capability behind it is off.
/// </summary>
public class CapabilityDisabledTests
{
    [Fact]
    public void An_individual_switch_that_is_off_names_its_own_option()
    {
        using var context = new StudioComponentContext();

        var note = context.Render<CapabilityDisabled>(parameters => parameters
            .Add(x => x.Capability, nameof(StudioCapability.BrowseDatabase))
            .Add(x => x.Action, "Browsing the database"));

        note.Find(".ms-capability-disabled").TextContent.Should()
            .Contain("Browsing the database is not enabled")
            .And.Contain("MartenStudioOptions.Capabilities.BrowseDatabase");
    }

    /// <summary>
    /// <c>ReadOnly</c> turns off the console and the database browser as well as every mutation, so the
    /// sentence may no longer say it turns off only mutating operations.
    /// </summary>
    [Fact]
    public void ReadOnly_is_described_as_turning_every_capability_off_reads_beyond_the_store_included()
    {
        using var context = new StudioComponentContext().WithReadOnly();

        var note = context.Render<CapabilityDisabled>(parameters => parameters
            .Add(x => x.Capability, nameof(StudioCapability.BrowseDatabase)));

        string text = note.Find(".ms-capability-disabled").TextContent;

        text.Should().Contain("MartenStudioOptions.ReadOnly").And.Contain("every capability");
        text.Should().Contain("RunSql").And.Contain("BrowseDatabase");
        text.Should().NotContain("every mutating operation off");
    }
}
