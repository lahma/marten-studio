using System.Text;
using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Json;

public class JsonPrettyPrinterTests
{
    [Fact]
    public void Pretty_printing_is_two_space_indented_and_stable()
    {
        var pretty = JsonPrettyPrinter.PrettyPrint("""{"b":1,"a":[1,2],"c":{"d":null},"e":true}""");

        pretty.Should().Be(
            """
            {
              "b": 1,
              "a": [
                1,
                2
              ],
              "c": {
                "d": null
              },
              "e": true
            }
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Member_order_is_left_alone_because_the_viewer_is_not_the_place_to_hide_what_jsonb_did()
    {
        JsonPrettyPrinter.PrettyPrint("""{"z":1,"a":2}""").Should().StartWith("{\n  \"z\"");
    }

    [Fact]
    public void A_member_name_and_a_string_value_are_different_tokens()
    {
        var lines = JsonPrettyPrinter.Tokenize(JsonPrettyPrinter.PrettyPrint("""{"name":"Ada"}"""));

        var valueLine = lines[1];
        valueLine.Tokens.Should().Contain(t => t.Kind == JsonTokenKind.Key && t.Text == "\"name\"");
        valueLine.Tokens.Should().Contain(t => t.Kind == JsonTokenKind.String && t.Text == "\"Ada\"");
    }

    [Fact]
    public void Every_scalar_kind_gets_its_own_token_kind()
    {
        var lines = JsonPrettyPrinter.Tokenize(JsonPrettyPrinter.PrettyPrint("""{"n":-1.5e3,"t":true,"f":false,"z":null}"""));
        var kinds = lines.SelectMany(l => l.Tokens).Select(t => t.Kind).ToArray();

        kinds.Should().Contain(JsonTokenKind.Number);
        kinds.Should().Contain(JsonTokenKind.Boolean);
        kinds.Should().Contain(JsonTokenKind.Null);
        kinds.Should().Contain(JsonTokenKind.Punctuation);
    }

    [Fact]
    public void Concatenating_a_lines_tokens_reproduces_the_line_exactly()
    {
        var pretty = JsonPrettyPrinter.PrettyPrint("""{"a":{"b":[1,"two",null]}}""");
        var lines = JsonPrettyPrinter.Tokenize(pretty);

        var rebuilt = string.Join("\n", lines.Select(l => string.Concat(l.Tokens.Select(t => t.Text))));

        rebuilt.Should().Be(pretty, "the gutter is a separate element, so the token spans have to carry the indent");
    }

    [Fact]
    public void Rendering_counts_the_lines_of_the_pretty_printed_form()
    {
        var document = JsonPrettyPrinter.Render("""{"a":1,"b":2}""", maxBytes: 4 * 1024 * 1024);

        document.TotalLines.Should().Be(4);
        document.Lines.Should().HaveCount(4);
        document.Truncated.Should().BeFalse();
        document.Notice.Should().BeNull();
        document.Lines[0].Number.Should().Be(1);
        document.Lines[3].Number.Should().Be(4);
    }

    [Fact]
    public void Too_many_lines_are_clamped_and_the_clamp_is_announced()
    {
        var document = JsonPrettyPrinter.Render("""{"a":1,"b":2,"c":3}""", maxBytes: 4 * 1024 * 1024, maxLines: 3);

        document.Lines.Should().HaveCount(3);
        document.TotalLines.Should().Be(5);
        document.Truncated.Should().BeTrue();
        document.Notice.Should().Contain("first 3 of 5 lines");
    }

    [Fact]
    public void A_document_over_the_raw_limit_shows_its_head_and_says_how_much_is_missing()
    {
        var big = "{\"blob\":\"" + new string('x', 200) + "\"}";

        var document = JsonPrettyPrinter.Render(big, maxBytes: 64, maxLines: 100, headLength: 50);

        document.Truncated.Should().BeTrue();
        document.Notice.Should().Be("Showing the first 50 B of a 211 B document.");
        document.Text.Should().HaveLength(50);
        Encoding.UTF8.GetByteCount(document.Text).Should().BeLessThanOrEqualTo(JsonPrettyPrinter.TruncatedRawLength);
    }

    [Fact]
    public void The_head_is_cut_between_characters_and_never_through_one()
    {
        // A string is UTF-16, so every emoji here is two chars. Cutting between them leaves a lone
        // surrogate: not a character, unrepresentable in UTF-8, and rendered as a replacement glyph at
        // the exact place a reader is trying to work out what the document says.
        var big = "{\"blob\":\"" + string.Concat(Enumerable.Repeat("\U0001F600", 60)) + "\"}";

        var document = JsonPrettyPrinter.Render(big, maxBytes: 16, maxLines: 100, headLength: 20);

        document.Text.Should().HaveLength(19, "the twentieth char was half of a character");

        // A lone surrogate cannot be encoded, so it comes back from UTF-8 as U+FFFD. Surviving the round
        // trip unchanged is the same thing as being well-formed.
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(document.Text)).Should().Be(document.Text);
    }

    [Fact]
    public void A_document_over_the_limit_but_shorter_than_the_head_says_so_rather_than_claiming_a_truncation()
    {
        var document = JsonPrettyPrinter.Render("""{"a":1}""", maxBytes: 4);

        document.Notice.Should().Be("This document is 7 B, over the 4 B limit, so it is shown unformatted.");
    }

    [Fact]
    public void Content_that_is_not_json_still_renders_as_lines_with_a_notice()
    {
        var document = JsonPrettyPrinter.Render("not json at all\nsecond line", maxBytes: 4 * 1024 * 1024);

        document.Lines.Should().HaveCount(2);
        document.Notice.Should().StartWith("This is not valid JSON");
    }

    [Fact]
    public void Nothing_renders_as_nothing()
    {
        JsonPrettyPrinter.Render(null, maxBytes: 1024).Should().BeSameAs(JsonRawDocument.Empty);
        JsonPrettyPrinter.Render(string.Empty, maxBytes: 1024).Should().BeSameAs(JsonRawDocument.Empty);
    }
}
