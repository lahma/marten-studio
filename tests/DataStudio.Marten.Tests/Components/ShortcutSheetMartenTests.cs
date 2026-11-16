using Bunit;

using DataStudio.Components.Shared;
using DataStudio.Services.Settings;
using DataStudio.Marten.Services;

using Microsoft.Extensions.DependencyInjection;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Tests.Components;

/// <summary>
/// 1.2-A11Y: Marten Studio's sheet is the shared one plus the keys only its pages answer to - the document list and the document
/// editor - after the shared groups.
/// </summary>
public class ShortcutSheetMartenTests
{
    /// <summary>The shell's descriptor as <c>AddMartenStudio()</c> leaves it: the database as the one module.</summary>
    private static StudioShellDescriptor ShellOf(StudioDescriptor descriptor)
    {
        MartenStudioSettings settings = MartenStudioSettings.Of(new MartenStudioOptions());
        return StudioModuleComposition.Compose(descriptor.ShellIdentity, [new DatabaseStudioModule(descriptor, settings, new StudioCapabilityGuard(settings))]);
    }

    [Fact]
    public void The_Marten_sheet_ends_with_the_document_groups_after_the_shared_ones()
    {
        using BunitContext context = new();
        context.Services.AddSingleton(ShellOf(MartenStudioDescriptor.Instance));

        IRenderedComponent<ShortcutSheet> sheet = context.Render<ShortcutSheet>();

        string[] titles = [.. sheet.FindAll(".ms-shortcuts-title").Select(static x => x.TextContent.Trim())];
        titles.Should().StartWith("Anywhere", "In a dialog");
        titles.TakeLast(3).Should().Equal("Document lists (a row has focus)", "LINQ query (a box has focus)", "Document editor");
        titles.Should().Contain("JSON viewer (a row has focus)", "the shared groups are still there");
    }

    [Fact]
    public void The_document_editors_keys_are_listed_as_the_script_answers_them()
    {
        using BunitContext context = new();
        context.Services.AddSingleton(ShellOf(MartenStudioDescriptor.Instance));

        AngleSharp.Dom.IElement editor = context.Render<ShortcutSheet>().FindAll(".ms-shortcuts-group")
            .Single(static x => x.QuerySelector(".ms-shortcuts-title")!.TextContent.Trim() == "Document editor");

        editor.QuerySelectorAll(".ms-shortcuts-row").Select(static x => System.Text.RegularExpressions.Regex.Replace(x.QuerySelector(".ms-shortcuts-keys")!.TextContent, @"\s|\+", string.Empty))
            .Should().Equal("CtrlEnter", "Esc", "Tab", "ShiftTab");
    }
}
