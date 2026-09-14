using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The one place an identifier can become SQL text. Hard rule 4 says every schema, table and column name
/// goes through here and no value ever does, so this is the smallest and most load-bearing test in the
/// suite.
/// </summary>
public class SqlIdentifierTests
{
    [Theory]
    [InlineData("id", "\"id\"")]
    [InlineData("mt_doc_customer", "\"mt_doc_customer\"")]
    [InlineData("Mixed Case", "\"Mixed Case\"")]
    [InlineData("order", "\"order\"")]
    public void An_identifier_comes_back_quoted(string identifier, string expected) =>
        SqlIdentifier.Quote(identifier).Should().Be(expected);

    [Fact]
    public void A_qualified_name_quotes_both_halves() =>
        SqlIdentifier.Qualify("public", "mt_doc_customer").Should().Be("\"public\".\"mt_doc_customer\"");

    [Theory]
    [InlineData("a\"b")]
    [InlineData("\"")]
    [InlineData("x\";drop table y--")]
    public void A_name_holding_a_double_quote_is_refused(string identifier)
    {
        // Doubling the quote instead would "work" and would be the wrong answer: a name that needs
        // escaping did not come from Marten's configuration, and the studio has no business inventing one.
        var act = () => SqlIdentifier.Quote(identifier);

        act.Should().Throw<ArgumentException>().WithMessage("*is not a valid identifier*");
    }

    [Fact]
    public void A_name_holding_a_null_character_is_refused()
    {
        var act = () => SqlIdentifier.Quote("a\0b");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_refused(string identifier)
    {
        var act = () => SqlIdentifier.Quote(identifier);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_null_name_is_refused()
    {
        var act = () => SqlIdentifier.Quote(null!);

        act.Should().Throw<ArgumentException>();
    }
}
