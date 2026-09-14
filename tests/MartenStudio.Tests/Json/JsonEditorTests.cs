using AngleSharp.Dom;
using Bunit;
using MartenStudio.Components.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

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
        LiveValue(context, """{"a":2}""");

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, text => saved = text)));

        view.Find("textarea").Change("""{"a":2}""");
        Button(view, "Save").Click();

        saved.Should().Be("""{"a":2}""");
    }

    [Fact]
    public void The_button_path_refuses_rather_than_saving_text_it_could_not_read_back()
    {
        using var context = new JsonTestContext();
        var saves = 0;

        // No readValue set up, so the call answers null the way a missing helper does. What the server
        // holds may be one round trip behind what is on screen, and a save that quietly reverts a
        // character somebody typed is worse than a save that did not happen.
        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, _ => saves++)));

        view.Find("textarea").Change("""{"a":2}""");
        Button(view, "Save").Click();

        saves.Should().Be(0);
        view.Find(".ms-editor-error-message").TextContent.Should().Contain("could not be read");
    }

    [Fact]
    public async Task Ctrl_enter_saves_the_text_the_browser_has_without_a_round_trip_per_keystroke()
    {
        using var context = new JsonTestContext();
        string? saved = null;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, text => saved = text)));

        // This is what the lib module's keydown handler does: it carries textarea.value across, so the
        // server never needs the keystroke itself and never needs to read the value back either.
        await view.InvokeAsync(() => view.Instance.OnEditorSaveAsync("""{"a":2}"""));

        saved.Should().Be("""{"a":2}""");
        context.JSInterop.Invocations.Identifiers.Should().NotContain("martenStudio.json.readValue");
    }

    [Fact]
    public async Task Ctrl_enter_on_invalid_json_refuses_the_same_way_the_button_does()
    {
        using var context = new JsonTestContext();
        var saves = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, _ => saves++)));

        await view.InvokeAsync(() => view.Instance.OnEditorSaveAsync("{ nope"));

        saves.Should().Be(0);
        view.Find(".ms-editor-error-message").TextContent.Should().StartWith("Line 1, column 3:");
    }

    [Fact]
    public void The_textarea_handles_no_keystroke_on_the_server()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonEditor>(p => p.Add(c => c.Json, """{"a":1}"""));

        // A keydown handler on the textarea is a circuit round trip and a full re-render - gutter
        // included - for every key pressed in the document. There is deliberately none to raise.
        var keystroke = () => view.Find("textarea").KeyDown(new KeyboardEventArgs { Key = "Enter", CtrlKey = true });

        keystroke.Should().Throw<MissingEventHandlerException>(
            "the shortcuts are recognised in the browser, so no keystroke crosses the circuit");
    }

    [Fact]
    public void Saving_invalid_json_refuses_and_explains()
    {
        using var context = new JsonTestContext();
        var saves = 0;
        LiveValue(context, "{ nope");

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnSave, EventCallback.Factory.Create<string>(this, _ => saves++)));

        view.Find("textarea").Change("{ nope");
        Button(view, "Save").Click();

        saves.Should().Be(0);
        view.FindAll(".ms-editor-error").Should().ContainSingle();
        view.Find(".ms-editor-error-message").TextContent.Should().StartWith("Line 1, column 3:");
    }

    [Fact]
    public async Task Escape_on_a_clean_editor_cancels_straight_away()
    {
        using var context = new JsonTestContext();
        var cancelled = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        await view.InvokeAsync(() => view.Instance.OnEditorCancelAsync("""{"a":1}"""));

        cancelled.Should().Be(1);
        view.FindAll(".ms-editor-confirm").Should().BeEmpty();
    }

    [Fact]
    public async Task Escape_over_typing_the_change_event_has_not_reported_yet_still_asks_first()
    {
        using var context = new JsonTestContext();
        var cancelled = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        // No `change` has fired: the user typed and pressed Esc without leaving the textarea. The value
        // comes across with the shortcut, so the editor knows it is dirty and asks before discarding it.
        await view.InvokeAsync(() => view.Instance.OnEditorCancelAsync("""{"a":2}"""));

        cancelled.Should().Be(0);
        view.Find(".ms-editor-confirm").TextContent.Should().Contain("Discard your changes?");
    }

    [Fact]
    public async Task Escape_on_a_dirty_editor_asks_first_and_the_confirm_is_the_editors_own()
    {
        using var context = new JsonTestContext();
        var cancelled = 0;

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.OnCancel, EventCallback.Factory.Create(this, () => cancelled++)));

        view.Find("textarea").Change("""{"a":2}""");
        await view.InvokeAsync(() => view.Instance.OnEditorCancelAsync("""{"a":2}"""));

        cancelled.Should().Be(0);
        view.Find(".ms-editor-confirm").TextContent.Should().Contain("Discard your changes?");

        Button(view, "Keep editing").Click();
        view.FindAll(".ms-editor-confirm").Should().BeEmpty();
        cancelled.Should().Be(0);

        await view.InvokeAsync(() => view.Instance.OnEditorCancelAsync("""{"a":2}"""));
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
    public void The_gutter_stops_at_its_clamp_and_says_it_has()
    {
        using var context = new JsonTestContext();
        var json = string.Join("\n", Enumerable.Range(0, 500).Select(i => "// " + i));

        var view = context.Render<JsonEditor>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.Rows, 4)
            .Add(c => c.MaxLines, 20));

        // One element per line, re-rendered whenever the component is: 3,000 lines used to mean 3,000
        // diffed divs for every status message the editor showed.
        var lines = view.FindAll(".ms-editor-gutter-line");
        lines.Should().HaveCount(21, "twenty numbers and the row that says there are more");
        lines[^1].TextContent.Should().Be("…");
        lines[^1].ClassList.Should().Contain("ms-editor-gutter-more");
    }

    [Fact]
    public void The_editor_hands_the_browser_a_reference_it_can_call_the_shortcuts_back_on()
    {
        using var context = new JsonTestContext();

        context.Render<JsonEditor>(p => p.Add(c => c.Json, """{"a":1}"""));

        var call = context.JSInterop.Invocations["martenStudio.json.enhanceTextarea"].Should().ContainSingle().Subject;
        call.Arguments.Should().HaveCount(3, "the textarea, the gutter, and the reference Ctrl+Enter calls back on");
        call.Arguments[2].Should().BeOfType<DotNetObjectReference<JsonEditor>>();
    }

    /// <summary>
    /// Answers <c>martenStudio.json.readValue</c> with <paramref name="text"/>, the way the browser
    /// answers it with the textarea's live value.
    /// </summary>
    private static void LiveValue(JsonTestContext context, string text) =>
        context.JSInterop.Setup<string?>("martenStudio.json.readValue", _ => true).SetResult(text);

    private static IElement Button(IRenderedComponent<JsonEditor> view, string text) =>
        view.FindAll("button").First(e => e.TextContent.Trim() == text);
}
