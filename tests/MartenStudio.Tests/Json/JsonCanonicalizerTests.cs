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
    [InlineData("1.500", "1.5")]
    [InlineData("-0.0", "0")]
    [InlineData("0.000", "0")]
    [InlineData("12345678901234567890123", "12345678901234567890123")]
    public void Numbers_are_reduced_to_their_value(string raw, string expected)
    {
        JsonCanonicalizer.NormalizeNumber(raw).Should().Be(expected);
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
