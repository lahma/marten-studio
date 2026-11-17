using Bunit;

using DataStudio.Components.Shared;
using DataStudio.Services.Settings;
using DataStudio.Marten.Services;

using Microsoft.Extensions.DependencyInjection;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Tests.Components;

/// <summary>
/// 1.2-A11Y: Data Studio's sheet is the shared one plus the keys only its pages answer to - the document list and the document
/// editor - after the shared groups.
/// </summary>
public class ShortcutSheetMartenTests
{
    /// <summary>The shell's descriptor as <c>AddMarten()</c> leaves it: the database as the one module.</summary>
    private static StudioShellDescriptor ShellOf(StudioDescriptor descriptor)
    {
        MartenSettings settings = MartenSettings.Of(new MartenOptions());
        return StudioModuleComposition.Compose([new DatabaseStudioModule(descriptor, settings, new StudioCapabilityGuard(settings))]);
    }

    [Fact]
    public void The_Marten_sheet_ends_with_the_document_groups_after_the_shared_ones()
    {
        using BunitContext context = new();
        context.Services.AddSingleton(ShellOf(MartenDescriptor.Instance));

        IRenderedComponent<ShortcutSheet> sheet = context.Render<ShortcutSheet>();

        string[] titles = [.. sheet.FindAll(".ms-shortcuts-title").Select(static x => x.TextContent.Trim())];
        titles.Should().StartWith("Anywhere", "In a dialog");
        titles.TakeLast(3).Should().Equal("Document lists (a row has focus)", "LINQ query (a box has focus)", "Document editor");
        titles.Should().Contain("JSON viewer (a row has focus)", "the shared groups are still there");
    }

    /// <summary>
    /// The SQL editor, the row grids and the plan view are components of the Postgres module, which the Marten module is built on: a Marten host
    /// has all three, between the shell's groups and its own, and the sheet says so (the Postgres host's is held by <c>ShortcutSheetTests</c>).
    /// </summary>
    [Fact]
    public void The_Marten_sheet_lists_the_database_modules_three_groups_between_the_shells_and_its_own()
    {
        using BunitContext context = new();
        context.Services.AddSingleton(ShellOf(MartenDescriptor.Instance));

        string[] titles = [.. context.Render<ShortcutSheet>().FindAll(".ms-shortcuts-title").Select(static x => x.TextContent.Trim())];

        titles.Should().Equal(
            "Anywhere",
            "In a dialog",
            "Menus",
            "JSON viewer (a row has focus)",
            "JSON viewer search box",
            "SQL editor",
            "Table rows (a row has focus)",
            "Plan icicle (a frame has focus)",
            "Document lists (a row has focus)",
            "LINQ query (a box has focus)",
            "Document editor");
    }

    [Fact]
    public void The_document_editors_keys_are_listed_as_the_script_answers_them()
    {
        using BunitContext context = new();
        context.Services.AddSingleton(ShellOf(MartenDescriptor.Instance));

        AngleSharp.Dom.IElement editor = context.Render<ShortcutSheet>().FindAll(".ms-shortcuts-group")
            .Single(static x => x.QuerySelector(".ms-shortcuts-title")!.TextContent.Trim() == "Document editor");

        editor.QuerySelectorAll(".ms-shortcuts-row").Select(static x => System.Text.RegularExpressions.Regex.Replace(x.QuerySelector(".ms-shortcuts-keys")!.TextContent, @"\s|\+", string.Empty))
            .Should().Equal("CtrlEnter", "Esc", "Tab", "ShiftTab");
    }
}
