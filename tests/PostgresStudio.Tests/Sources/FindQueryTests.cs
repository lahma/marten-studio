using PostgresStudio.Services.Find;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// 1.2-SCOPE-3: what Find makes of a typed value is a table of shapes (D-SCOPE F3), and the table is the whole contract: a parser with
/// no clock, no service and no database.
/// </summary>
public class FindQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void Nothing_typed_is_empty(string? typed)
    {
        FindQuery query = FindQuery.Parse(typed);

        query.IsEmpty.Should().BeTrue();
        query.CouldBeAnId.Should().BeFalse();
    }

    [Fact]
    public void The_value_is_trimmed()
    {
        FindQuery.Parse("  ORD-2026-0001 \n").Value.Should().Be("ORD-2026-0001");
    }

    [Fact]
    public void Five_hundred_and_twelve_characters_are_taken_and_one_more_is_refused_before_anything_runs()
    {
        FindQuery.Parse(new string('a', FindQuery.MaxLength)).Refusal.Should().BeNull();

        FindQuery tooLong = FindQuery.Parse(new string('a', FindQuery.MaxLength + 1));

        tooLong.Refusal.Should().Be("Too long to be an id (512 characters at most).");
        tooLong.CouldBeAnId.Should().BeFalse();
        tooLong.Identifier.Should().BeNull("a refused value is not read any further");
        tooLong.Value.Length.Should().BeLessThanOrEqualTo(FindQuery.MaxLength);
    }

    [Fact]
    public void A_value_with_a_hash_is_an_event_sequence_and_nothing_else()
    {
        FindQuery query = FindQuery.Parse("#12");

        query.SequenceOnly.Should().BeTrue();
        query.Sequence.Should().Be(12);
        query.CouldBeAnId.Should().BeFalse("a document, stream, table or statement is never looked for under a hash");
        query.Identifier.Should().BeNull();
        query.QueryId.Should().BeNull();
    }

    [Theory]
    [InlineData("#")]
    [InlineData("#0")]
    [InlineData("#abc")]
    [InlineData("#-4")]
    [InlineData("# 12")]
    [InlineData("#99999999999999999999")]
    public void A_hash_that_is_not_a_positive_number_is_a_sequence_search_that_cannot_match(string typed)
    {
        FindQuery query = FindQuery.Parse(typed);

        query.SequenceOnly.Should().BeTrue();
        query.Sequence.Should().BeNull();
    }

    [Fact]
    public void A_plain_positive_number_is_tried_as_a_sequence_and_as_every_id_besides()
    {
        FindQuery query = FindQuery.Parse("12");

        query.Sequence.Should().Be(12);
        query.SequenceOnly.Should().BeFalse();
        query.CouldBeAnId.Should().BeTrue();
        query.QueryId.Should().BeNull("a small number is far more likely something else's id than a statement's");
        query.Identifier.Should().BeNull("an identifier starts with a letter or an underscore");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-12")]
    [InlineData("1.5")]
    [InlineData("12abc")]
    public void Other_numbers_are_not_sequences(string typed)
    {
        FindQuery.Parse(typed).Sequence.Should().BeNull();
    }

    [Theory]
    [InlineData("orders", null, "orders")]
    [InlineData("_t", null, "_t")]
    [InlineData("public.orders", "public", "orders")]
    [InlineData("Mt_Doc_Order", null, "Mt_Doc_Order")]
    [InlineData("a$b", null, "a$b")]
    public void An_identifier_is_a_table_or_view_name_with_an_optional_schema(string typed, string? schema, string name)
    {
        FindQuery.Parse(typed).Identifier.Should().Be(new FindIdentifier(schema, name));
    }

    [Theory]
    [InlineData("1orders")]
    [InlineData("a.b.c")]
    [InlineData("a b")]
    [InlineData("orders;")]
    [InlineData("\"orders\"")]
    [InlineData(".orders")]
    [InlineData("orders.")]
    [InlineData("2f6c0e0a-1b2c-4d3e-8f90-123456789abc")]
    public void Anything_else_is_not_an_identifier(string typed)
    {
        FindQuery.Parse(typed).Identifier.Should().BeNull();
    }

    [Fact]
    public void An_identifier_part_may_be_sixty_three_characters_and_not_sixty_four()
    {
        FindQuery.Parse(new string('a', 63)).Identifier.Should().NotBeNull();
        FindQuery.Parse(new string('a', 64)).Identifier.Should().BeNull();
        FindQuery.Parse(new string('a', 63) + "." + new string('b', 63)).Identifier.Should().NotBeNull();
        FindQuery.Parse(new string('a', 63) + "." + new string('b', 64)).Identifier.Should().BeNull();
    }

    [Theory]
    [InlineData("-1", -1)]
    [InlineData("-8472916038475629105", -8472916038475629105)]
    [InlineData("100000000000", 100_000_000_000)]
    [InlineData("123456789012345", 123_456_789_012_345)]
    public void A_negative_number_or_one_of_twelve_digits_could_be_a_statements_queryid(string typed, long expected)
    {
        FindQuery.Parse(typed).QueryId.Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("12345")]
    [InlineData("99999999999")]
    [InlineData("99999999999999999999")]
    [InlineData("12.5")]
    [InlineData("abc")]
    public void A_small_positive_number_or_one_that_does_not_fit_is_not(string typed)
    {
        FindQuery.Parse(typed).QueryId.Should().BeNull();
    }

    [Fact]
    public void A_guid_is_an_id_and_no_other_kind()
    {
        FindQuery query = FindQuery.Parse("2f6c0e0a-1b2c-4d3e-8f90-123456789abc");

        query.CouldBeAnId.Should().BeTrue();
        query.Sequence.Should().BeNull();
        query.Identifier.Should().BeNull();
        query.QueryId.Should().BeNull();
    }

    [Fact]
    public void A_twelve_digit_number_is_every_kind_it_fits()
    {
        FindQuery query = FindQuery.Parse("123456789012");

        query.Sequence.Should().Be(123_456_789_012);
        query.QueryId.Should().Be(123_456_789_012);
        query.CouldBeAnId.Should().BeTrue("every kind whose shape it fits runs; it is not first-hit-wins");
    }
}
