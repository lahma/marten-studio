using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Json;

public class JsonCanonicalizerTests
{
    [Fact]
    public void Object_members_are_sorted_at_every_level()
    {
        var canonical = JsonCanonicalizer.Canonicalize("""{"z":1,"a":{"y":2,"b":3}}""");

        JsonCanonicalizer.ToCanonicalString(canonical).Should().Be("""{"a":{"b":3,"y":2},"z":1}""");
    }

    [Fact]
    public void Array_order_is_never_touched_because_it_is_data()
    {
        var canonical = JsonCanonicalizer.Canonicalize("""[3,1,2]""");

        JsonCanonicalizer.ToCanonicalString(canonical).Should().Be("[3,1,2]");
    }

    [Theory]
    [InlineData("1.0", "1")]
    [InlineData("1", "1")]
    [InlineData("1e2", "100")]
    [InlineData("100", "100")]
    [InlineData("+1", "1")]
    [InlineData("007", "7")]
    [InlineData("1.500", "1.5")]
    [InlineData("-0.0", "0")]
    [InlineData("0.000", "0")]
    [InlineData("0e9", "0")]
    [InlineData("-1.5e1", "-15")]
    [InlineData("12345678901234567890123", "12345678901234567890123")]
    public void Numbers_are_reduced_to_their_value(string raw, string expected)
    {
        JsonCanonicalizer.NormalizeNumber(raw).Should().Be(expected);
    }

    [Theory]
    // Every one of these is a number Postgres will happily store in a jsonb column - it keeps them as
    // numeric - and every one of them is outside what decimal or double can hold without rounding. The
    // canonical form is textual for exactly this reason: a digit lost here is a diff that reports no
    // change about a document that changed.
    [InlineData("123456789012345678901234567890123", "123456789012345678901234567890123")]
    [InlineData("0.1000000000000000000000000000005", "0.1000000000000000000000000000005")]
    [InlineData("1e-30", "0.000000000000000000000000000001")]
    [InlineData("1e400", "1E+400")]
    [InlineData("-1e400", "-1E+400")]
    [InlineData("1.25e400", "1.25E+400")]
    [InlineData("1e-400", "1E-400")]
    public void A_number_no_clr_type_can_hold_keeps_every_digit_it_came_with(string raw, string expected)
    {
        JsonCanonicalizer.NormalizeNumber(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("1e400", "1e400")]
    [InlineData("1e400", "10e399")]
    [InlineData("123456789012345678901234567890123", "1.23456789012345678901234567890123e32")]
    [InlineData("1e-30", "0.000000000000000000000000000001")]
    public void Two_spellings_of_one_unholdable_number_are_still_one_number(string left, string right)
    {
        JsonCanonicalizer.NormalizeNumber(left).Should().Be(JsonCanonicalizer.NormalizeNumber(right));
    }

    [Theory]
    [InlineData("""{"n":1e400}""", """{"n":1e401}""")]
    [InlineData("""{"n":0.1000000000000000000000000000005}""", """{"n":0.1}""")]
    [InlineData("""{"n":1e-30}""", """{"n":0}""")]
    [InlineData(
        """{"n":123456789012345678901234567890123}""",
        """{"n":123456789012345678901000000000000}""")]
    public void Numbers_that_differ_only_past_the_precision_of_a_clr_type_still_differ(string left, string right)
    {
        JsonCanonicalizer.ValueEquals(
            JsonCanonicalizer.Canonicalize(left),
            JsonCanonicalizer.Canonicalize(right)).Should().BeFalse();
    }

    [Fact]
    public void A_number_outside_double_canonicalizes_to_something_that_is_still_json()
    {
        // The old implementation fell back to double.ToString("R"), which writes "Infinity" - not a JSON
        // number, so parsing the canonical form back threw and the whole document failed to canonicalize.
        var canonical = JsonCanonicalizer.Canonicalize("""{"n":1e400,"m":-1e400}""");

        JsonCanonicalizer.ToCanonicalString(canonical).Should().Be("""{"m":-1E+400,"n":1E+400}""");
        JsonCanonicalizer.TryCanonicalize("""{"n":1e400}""", out var node, out var error).Should().BeTrue();
        node.Should().NotBeNull();
        error.Should().BeNull();
    }

    [Fact]
    public void A_number_that_only_changed_its_spelling_is_not_a_change()
    {
        var before = JsonCanonicalizer.Canonicalize("""{"n":1.0}""");
        var after = JsonCanonicalizer.Canonicalize("""{"n":1}""");

        JsonCanonicalizer.ValueEquals(before, after).Should().BeTrue();
    }

    [Fact]
    public void A_member_that_only_moved_is_not_a_change()
    {
        var before = JsonCanonicalizer.Canonicalize("""{"a":1,"b":2}""");
        var after = JsonCanonicalizer.Canonicalize("""{"b":2,"a":1}""");

        JsonCanonicalizer.ValueEquals(before, after).Should().BeTrue();
    }

    [Fact]
    public void A_string_and_a_number_that_look_alike_are_still_different()
    {
        var before = JsonCanonicalizer.Canonicalize("""{"n":1}""");
        var after = JsonCanonicalizer.Canonicalize("""{"n":"1"}""");

        JsonCanonicalizer.ValueEquals(before, after).Should().BeFalse();
    }

    [Fact]
    public void Null_canonicalizes_to_the_null_literal()
    {
        JsonCanonicalizer.ToCanonicalString(JsonCanonicalizer.Canonicalize("null")).Should().Be("null");
    }

    [Fact]
    public void Trying_to_canonicalize_rubbish_reports_rather_than_throws()
    {
        JsonCanonicalizer.TryCanonicalize("{ nope", out var node, out var error).Should().BeFalse();
        node.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();

        JsonCanonicalizer.TryCanonicalize("   ", out var empty, out var noError).Should().BeTrue();
        empty.Should().BeNull();
        noError.Should().BeNull();
    }

    [Fact]
    public void A_repeated_member_keeps_the_last_value_the_way_a_deserializer_would()
    {
        var canonical = JsonCanonicalizer.Canonicalize("""{"a":1,"a":2}""");

        JsonCanonicalizer.ToCanonicalString(canonical).Should().Be("""{"a":2}""");
    }
}
