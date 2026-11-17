using DataStudio.Postgres.Internal.Sql;
using DataStudio.Services.Json;

namespace DataStudio.Postgres.Tests.Sources;

/// <summary>
/// The shell's <c>JsonLiteralText</c> is a copy of two functions of the engine's <c>SqlLiteralText</c> (v2 step 3): the JSON viewer's copy menu
/// needs "may this name be printed as itself" and "this value as a string literal", and the shell references no database code. A copy is only
/// safe while it answers what the original answers, so this holds the two together - the same answer for every character, and for a corpus
/// of values that includes each shape the original's own tests pin.
/// </summary>
public class JsonLiteralTextParityTests
{
    public static TheoryData<string> Values =>
    [
        string.Empty,
        "city",
        "O'Brien",
        "''",
        "a\\b",
        "trailing\\",
        "\\",
        "a'b\\c'd",
        "has\0nul",
        "tab\there",
        "line\nbreak",
        "carriage\rreturn",
        "\u202Eright-to-left override",
        "zero\u200Bwidth space",
        "line\u2028separator",
        "paragraph\u2029separator",
        "emoji \U0001F600 and combining e\u0301",
        "{\"a\":[1,2]}",
        "  padded  ",
    ];

    [Theory]
    [MemberData(nameof(Values))]
    public void A_value_gets_the_literal_the_engine_writes(string value)
    {
        JsonLiteralText.Literal(value).Should().Be(SqlLiteralText.Literal(value));
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void A_value_is_printable_when_the_engine_says_so(string value)
    {
        JsonLiteralText.IsPrintableText(value).Should().Be(SqlLiteralText.IsPrintableText(value));
    }

    [Fact]
    public void A_null_is_the_SQL_null_and_is_never_printable_on_both_sides()
    {
        JsonLiteralText.Literal(null).Should().Be(SqlLiteralText.Literal(null)).And.Be("null");
        JsonLiteralText.IsPrintableText(null).Should().Be(SqlLiteralText.IsPrintableText(null)).And.BeFalse();
    }

    /// <summary>Every UTF-16 code unit, one at a time: the Unicode categories the rule refuses are the same on both sides, not only for the corpus.</summary>
    [Fact]
    public void Every_character_is_judged_as_the_engine_judges_it()
    {
        int refused = 0;

        for (int unit = 0; unit <= char.MaxValue; unit++)
        {
            string text = "a" + (char) unit + "b";
            bool printable = SqlLiteralText.IsPrintableText(text);

            JsonLiteralText.IsPrintableText(text).Should().Be(printable, "U+{0:X4} is judged alike", unit);
            JsonLiteralText.Literal(text).Should().Be(SqlLiteralText.Literal(text), "U+{0:X4} is quoted alike", unit);

            if (!printable)
            {
                refused++;
            }
        }

        refused.Should().BeGreaterThan(60, "the control, format and separator characters are refused, so the scan is not comparing two empty answers");
    }
}
