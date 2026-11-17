using Bunit;

using DataStudio.Postgres.Components.Shared;
using DataStudio.Marten.Services;
using DataStudio.Postgres.Services;

namespace DataStudio.Marten.Tests.Components;

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
            .And.Contain("MartenOptions.Capabilities.BrowseDatabase");
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

        text.Should().Contain("MartenOptions.ReadOnly").And.Contain("every capability");
        text.Should().Contain("RunSql").And.Contain("BrowseDatabase");
        text.Should().NotContain("every mutating operation off");
    }

    /// <summary>
    /// The read-only sentence is read whole in each of the three cases (REVIEW-V2-E, finding 2): the shell's switch turns every module's
    /// capabilities off, the module's turns off the Marten pages', and both is the shell's. A word fused to the next ("off:the") fails it.
    /// </summary>
    [Theory]
    [InlineData(true, false, "DataStudioOptions.ReadOnly", "every capability off: the mutating operations")]
    [InlineData(false, true, "MartenOptions.ReadOnly", "every capability of the Marten pages off: the mutating operations")]
    [InlineData(true, true, "DataStudioOptions.ReadOnly", "every capability off: the mutating operations")]
    public void The_read_only_sentence_reads_whole_in_each_of_the_three_cases(bool shell, bool module, string option, string turnsOff)
    {
        using var context = new StudioComponentContext();
        context.Options.Capabilities = MartenCapabilities.All();
        context.Options.ReadOnly = module;
        context.ShellOptions.ReadOnly = shell;

        var note = context.Render<CapabilityDisabled>(parameters => parameters
            .Add(x => x.Capability, nameof(StudioCapability.BrowseDatabase)));

        string text = System.Text.RegularExpressions.Regex.Replace(note.Find(".ms-capability-disabled").TextContent, @"\s+", " ");

        text.Should().Contain(option + " is true, which turns " + turnsOff + ", and the reads authorized like writes");
    }
}
