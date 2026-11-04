using Bunit;

using MartenStudio.Services;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Shared;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-A11Y: Marten Studio's sheet is the shared one plus the keys only its pages answer to - the document list and the document
/// editor - after the shared groups.
/// </summary>
public class ShortcutSheetMartenTests
{
    [Fact]
    public void The_Marten_sheet_ends_with_the_document_groups_after_the_shared_ones()
    {
        using BunitContext context = new();
        context.Services.AddSingleton(MartenStudioDescriptor.Instance);

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
        context.Services.AddSingleton(MartenStudioDescriptor.Instance);

        AngleSharp.Dom.IElement editor = context.Render<ShortcutSheet>().FindAll(".ms-shortcuts-group")
            .Single(static x => x.QuerySelector(".ms-shortcuts-title")!.TextContent.Trim() == "Document editor");

        editor.QuerySelectorAll(".ms-shortcuts-row").Select(static x => System.Text.RegularExpressions.Regex.Replace(x.QuerySelector(".ms-shortcuts-keys")!.TextContent, @"\s|\+", string.Empty))
            .Should().Equal("CtrlEnter", "Esc", "Tab", "ShiftTab");
    }
}
