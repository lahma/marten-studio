using AngleSharp.Dom;
using Bunit;
using MartenStudio.Components.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MartenStudio.Tests.Json;

public class JsonEditorTests
{
    [Fact]
    public void The_textarea_is_bound_on_change_and_never_spell_checked()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonEditor>(p => p.Add(c => c.Json, """{"a":1}"""));

        var textArea = view.Find("textarea");
        textArea.GetAttribute("spellcheck").Should().Be("false");
        textArea.GetAttribute("autocapitalize").Should().Be("off");
        textArea.GetAttribute("value").Should().Be("""{"a":1}""");
        view.Instance.Text.Should().Be("""{"a":1}""");
    }

    [Fact]
    public void Validating_bad_json_names_the_line_and_column_and_draws_a_caret()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonEditor>(p => p.Add(c => c.Json, "{\n  \"a\": ,\n}"));

        Button(view, "Validate").Click();

        var error = view.Find(".ms-editor-error-message").TextContent;
        error.Should().StartWith("Line 2, column 8:");
        view.Find(".ms-editor-caret").TextContent.Should().Be("  \"a\": ,\n       ^");
    }

    [Fact]
    public void Validating_good_json_says_so()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonEditor>(p => p.Add(c => c.Json, """{"a":1}"""));

        Button(view, "Validate").Click();

        view.Find(".ms-editor-ok").TextContent.Should().Be("Valid JSON.");
        view.FindAll(".ms-editor-error").Should().BeEmpty();
    }

    [Fact]
    public void Formatting_re_indents_in_place()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonEditor>(p => p.Add(c => c.Json, """{"a":1,"b":[2]}"""));

        Button(view, "Format").Click();

        view.Instance.Text.Should().Be("{\n  \"a\": 1,\n  \"b\": [\n    2\n  ]\n}");
        view.Find(".ms-editor-ok").TextContent.Should().Be("Formatted.");
    }

    [Fact]
    public void Formatting_something_that_is_not_json_reports_instead_of_replacing_the_text()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonEditor>(p => p.Add(c => c.Json, "{ nope"));

        Button(view, "Format").Click();

        view.Instance.Text.Should().Be("{ nope");
        view.Find(".ms-editor-error-message").TextContent.Should().StartWith("Line 1, column 3:");
    }

    [Fact]
    public void A_change_marks_the_editor_dirty()
    {
        using var context = new JsonTestContext();
        var dirtyStates = new List<bool>();

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnDirtyChanged, EventCallback.Factory.Create<bool>(this, dirtyStates.Add)));

        view.Instance.IsDirty.Should().BeFalse();

        view.Find("textarea").Change("""{"a":2}""");

        view.Instance.IsDirty.Should().BeTrue();
        dirtyStates.Should().Equal(true);
        view.Find(".ms-editor-dirty").TextContent.Should().Be("unsaved changes");
    }

    [Fact]
    public void Saving_hands_over_the_edited_text()
    {
        using var context = new JsonTestContext();
        string? saved = null;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, text => saved = text)));

        view.Find("textarea").Change("""{"a":2}""");
        Button(view, "Save").Click();

        saved.Should().Be("""{"a":2}""");
    }

    [Fact]
    public void Ctrl_enter_saves()
    {
        using var context = new JsonTestContext();
        string? saved = null;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, text => saved = text)));

        view.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

        saved.Should().Be("""{"a":1}""");
    }

    [Fact]
    public void Saving_invalid_json_refuses_and_explains()
    {
        using var context = new JsonTestContext();
        var saves = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, _ => saves++)));

        view.Find("textarea").Change("{ nope");
        Button(view, "Save").Click();

        saves.Should().Be(0);
        view.FindAll(".ms-editor-error").Should().ContainSingle();
    }

    [Fact]
    public void Escape_on_a_clean_editor_cancels_straight_away()
    {
        using var context = new JsonTestContext();
        var cancelled = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        view.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cancelled.Should().Be(1);
        view.FindAll(".ms-editor-confirm").Should().BeEmpty();
    }

    [Fact]
    public void Escape_on_a_dirty_editor_asks_first_and_the_confirm_is_the_editors_own()
    {
        using var context = new JsonTestContext();
        var cancelled = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        view.Find("textarea").Change("""{"a":2}""");
        view.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cancelled.Should().Be(0);
        view.Find(".ms-editor-confirm").TextContent.Should().Contain("Discard your changes?");

        Button(view, "Keep editing").Click();
        view.FindAll(".ms-editor-confirm").Should().BeEmpty();
        cancelled.Should().Be(0);

        view.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Button(view, "Discard").Click();

        cancelled.Should().Be(1);
        view.Instance.Text.Should().Be("""{"a":1}""");
        view.Instance.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void The_confirm_can_be_turned_off_for_a_page_that_guards_navigation_itself()
    {
        using var context = new JsonTestContext();
        var cancelled = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.ConfirmDiscard, false)
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        view.Find("textarea").Change("""{"a":2}""");
        Button(view, "Cancel").Click();

        cancelled.Should().Be(1);
    }

    [Fact]
    public void The_gutter_numbers_every_line()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, "{\n  \"a\": 1\n}")
            .Add(c => c.Rows, 3));

        view.FindAll(".ms-editor-gutter-line").Should().HaveCount(3);
        view.FindAll(".ms-editor-gutter-line")[2].TextContent.Should().Be("3");
    }

    [Fact]
    public void The_editor_asks_the_browser_for_soft_tabs_and_a_synced_gutter_but_does_not_need_them()
    {
        using var context = new JsonTestContext();

        context.Render<JsonEditor>(p => p.Add(c => c.Json, """{"a":1}"""));

        context.JSInterop.Invocations["martenStudio.json.enhanceTextarea"].Should().ContainSingle();
    }

    private static IElement Button(IRenderedComponent<JsonEditor> view, string text) =>
        view.FindAll("button").First(e => e.TextContent.Trim() == text);
}
