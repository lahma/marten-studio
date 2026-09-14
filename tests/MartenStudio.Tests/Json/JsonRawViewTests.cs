using Bunit;
using MartenStudio.Components.Json;

namespace MartenStudio.Tests.Json;

public class JsonRawViewTests
{
    [Fact]
    public void Every_line_of_the_pretty_printed_document_gets_a_row_and_a_number()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonRawView>(p => p.Add(c => c.Json, """{"a":1,"b":[2]}"""));

        var lines = view.FindAll(".ms-code-line");
        lines.Should().HaveCount(6);
        lines[0].QuerySelector(".ms-code-gutter")!.TextContent.Should().Be("1");
        lines[5].QuerySelector(".ms-code-gutter")!.TextContent.Should().Be("6");
    }

    [Fact]
    public void Concatenating_a_rows_spans_gives_the_line_back_including_its_indent()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonRawView>(p => p.Add(c => c.Json, """{"a":1}"""));

        view.FindAll(".ms-code-line")[1].QuerySelector(".ms-code-text")!.TextContent
            .Should().Be("  \"a\": 1");
    }

    [Fact]
    public void Keys_values_and_punctuation_get_their_own_classes()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonRawView>(p => p.Add(c => c.Json, """{"a":"x","b":1,"c":true,"d":null}"""));

        view.FindAll(".ms-code-key").Should().HaveCount(4);
        view.FindAll(".ms-code-string").Should().ContainSingle();
        view.FindAll(".ms-code-number").Should().ContainSingle();
        view.FindAll(".ms-code-bool").Should().ContainSingle();
        view.FindAll(".ms-code-null").Should().ContainSingle();
    }

    [Fact]
    public void The_gutter_can_be_turned_off_and_wrapping_on()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonRawView>(p => p
            .Add(c => c.Json, """{"a":1}""")
            .Add(c => c.ShowLineNumbers, false)
            .Add(c => c.Wrap, true));

        view.FindAll(".ms-code-gutter").Should().BeEmpty();
        view.Find(".ms-code").ClassList.Should().Contain("ms-code-wrap");
    }

    [Fact]
    public void The_note_about_jsonb_is_there_because_the_document_on_screen_is_not_the_bytes_that_were_written()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonRawView>(p => p.Add(c => c.Json, """{"a":1}"""));

        view.Find(".ms-json-footnote").TextContent.Should().Contain("jsonb does not preserve member order");
    }

    [Fact]
    public void A_clamped_document_says_how_much_of_it_is_showing()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonRawView>(p => p
            .Add(c => c.Json, """{"a":1,"b":2,"c":3}""")
            .Add(c => c.MaxLines, 2));

        view.FindAll(".ms-code-line").Should().HaveCount(2);
        view.Find(".ms-json-notice").TextContent.Should().Contain("first 2 of 5 lines");
    }

    [Fact]
    public void A_raised_line_limit_re_renders_the_document_rather_than_keeping_the_old_clamp()
    {
        using var context = new JsonTestContext();
        const string Json = """{"a":1,"b":2,"c":3}""";

        var view = context.Render<JsonRawView>(p => p
            .Add(c => c.Json, Json)
            .Add(c => c.MaxLines, 2));

        view.FindAll(".ms-code-line").Should().HaveCount(2);

        // Same document, a different limit. Keying the rendered document on the text alone left the old
        // clamp on screen under a notice that said it was showing fewer lines than it had been asked for.
        view.Render(p => p
            .Add(c => c.Json, Json)
            .Add(c => c.MaxLines, 10));

        view.FindAll(".ms-code-line").Should().HaveCount(5);
        view.FindAll(".ms-json-notice").Should().BeEmpty();
    }

    [Fact]
    public void Content_that_will_not_parse_is_still_shown()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonRawView>(p => p.Add(c => c.Json, "not json"));

        view.FindAll(".ms-code-line").Should().ContainSingle();
        view.Find(".ms-json-notice").TextContent.Should().Contain("not valid JSON");
    }
}
