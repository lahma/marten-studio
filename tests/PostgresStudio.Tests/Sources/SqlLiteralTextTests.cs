using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// The database browser's older copy texts - Copy SELECT, Copy WHERE, the row statements (W4-polish): every name they
/// print is one that reads on screen as the statement it is, and every value a literal that means the same whatever the
/// session's <c>standard_conforming_strings</c> says.
/// </summary>
public class SqlLiteralTextTests
{
    // ---- Values ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("'", "''''")]
    [InlineData("", "''")]
    [InlineData("tab\there", "'tab\there'")]
    public void A_value_without_a_backslash_is_the_plain_standard_literal_as_before(string value, string literal)
    {
        SqlLiteralText.Literal(value).Should().Be(literal, "ordinary output is unchanged");
    }

    [Theory]
    [InlineData("a\\b", "E'a\\\\b'")]
    [InlineData("\\", "E'\\\\'")]
    [InlineData("\\'", "E'\\\\'''")]
    [InlineData("it\\'s", "E'it\\\\''s'")]
    [InlineData("trailing\\", "E'trailing\\\\'")]
    [InlineData("a\\\\b", "E'a\\\\\\\\b'")]
    [InlineData("C:\\temp\\new", "E'C:\\\\temp\\\\new'")]
    public void A_value_with_a_backslash_is_an_escape_string_with_quotes_and_backslashes_doubled(string value, string literal)
    {
        SqlLiteralText.Literal(value).Should().Be(literal,
            "an E'' string reads a doubled backslash as one under either standard_conforming_strings");
    }

    [Theory]
    [InlineData("nul\0inside")]
    [InlineData("\0")]
    [InlineData("back\\slash\0and nul")]
    public void A_value_with_a_NUL_has_no_literal_and_no_clause(string value)
    {
        SqlLiteralText.Literal(value).Should().BeNull("no Postgres text value contains a NUL");
        SqlLiteralText.WhereClause([new("id", value)]).Should().BeNull();
        SqlLiteralText.RowStatement("public", "orders", [new("id", "1"), new("note", value)]).Should().BeNull();
    }

    [Fact]
    public void A_NULL_is_still_compared_with_is_null()
    {
        SqlLiteralText.Literal(null).Should().Be("null");
        SqlLiteralText.WhereClause([new("a", "x\\"), new("b", null)]).Should().Be("where \"a\" = E'x\\\\'\n  and \"b\" is null");
    }

    // ---- Names ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Names that quote safely but do not read as what they are - a line break, a control or format character, a Unicode
    /// line or paragraph separator - and the two the quoting refuses, built at run time so that this file holds none of them.
    /// </summary>
    public static TheoryData<string> UnprintableNames() =>
    [
        "line\nbreak",
        "carriage\rreturn",
        "tab\tname",
        "bell" + (char) 7,
        "rlo" + char.ConvertFromUtf32(0x202E) + "txt",
        "zero" + char.ConvertFromUtf32(0x200B) + "width",
        "line" + char.ConvertFromUtf32(0x2028) + "separator",
        "paragraph" + char.ConvertFromUtf32(0x2029) + "separator",
        "we\"ird",
        "nul\0name",
        "",
        "   ",
    ];

    [Theory]
    [MemberData(nameof(UnprintableNames))]
    public void A_name_that_does_not_print_as_itself_gets_no_copy_text(string name)
    {
        SqlLiteralText.SelectStatement("public", name).Should().BeNull();
        SqlLiteralText.SelectStatement(name, "orders").Should().BeNull();
        SqlLiteralText.WhereClause([new("id", "1"), new(name, "x")]).Should().BeNull();
        SqlLiteralText.RowStatement("public", name, [new("id", "1")]).Should().BeNull();
        SqlLiteralText.RowStatement(name, "orders", [new("id", "1")]).Should().BeNull();
        SqlLiteralText.RowStatement("public", "orders", [new(name, "1")]).Should().BeNull();
        MigrationSqlText.IsPrintableName(name).Should().BeFalse("the copy texts hold a name to the remedies' rule");
    }

    [Theory]
    [MemberData(nameof(UnprintableNames))]
    public void Printable_text_is_the_names_rule_less_what_only_an_identifier_needs(string name)
    {
        bool identifierOnly = name.Contains('"', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(name);

        SqlLiteralText.IsPrintableText(name).Should().Be(identifierOnly,
            "a quote and a blank are refused only as a name; everything else here would not read as itself anywhere");
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("a\"b")]
    [InlineData("back\\slash")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("commandes_été")]
    public void Printable_text_admits_what_reads_as_itself(string text)
    {
        SqlLiteralText.IsPrintableText(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("Mixed Case")]
    [InlineData("it's")]
    [InlineData("back\\slash")]
    [InlineData("commandes_été")]
    [InlineData("x); drop table y; --")]
    public void Any_other_name_is_printed_quoted_as_it_is(string name)
    {
        SqlLiteralText.SelectStatement("public", name).Should().Be("select * from \"public\".\"" + name + "\" limit 100");
        SqlLiteralText.RowStatement("public", name, [new(name, "1")])
            .Should().Be("select * from \"public\".\"" + name + "\"\nwhere \"" + name + "\" = '1'",
                "a backslash in a quoted identifier is an ordinary character under either setting");
    }
}
