using System.Text;

namespace KStudio.Tests.Clusters;

/// <summary>Every key format's byte forms, prefixes and problems (decision 7.4 / F8): a literal compiles to bytes, and a problem never repeats it.</summary>
public class KeyFormatTests
{
    private const string Literal = "secret-literal-123";

    private static byte[][] Forms(KafkaKeyFormat format, string literal)
    {
        format.TryEncode(literal, out IReadOnlyList<byte[]> forms, out string? problem).Should().BeTrue(problem);
        return [.. forms];
    }

    [Fact]
    public void Utf8_string_is_the_utf8_bytes_and_supports_a_prefix()
    {
        KafkaKeyFormat.Utf8String.SupportsPrefix.Should().BeTrue();
        Forms(KafkaKeyFormat.Utf8String, "héllo").Should().ContainSingle().Which.Should().Equal(Encoding.UTF8.GetBytes("héllo"));
        KafkaKeyFormat.Utf8String.TryEncodePrefix("ab", out IReadOnlyList<byte[]> prefix, out _).Should().BeTrue();
        prefix.Should().ContainSingle().Which.Should().Equal("ab"u8.ToArray());
    }

    [Fact]
    public void Utf8_string_formats_valid_text_and_refuses_invalid_bytes()
    {
        KafkaKeyFormat.Utf8String.TryFormat("héllo"u8, out string? text).Should().BeTrue();
        text.Should().Be("héllo");
        KafkaKeyFormat.Utf8String.TryFormat([0xFF, 0xFE], out string? bad).Should().BeFalse();
        bad.Should().BeNull();
    }

    [Fact]
    public void A_json_string_returns_every_byte_form_a_producer_could_have_written()
    {
        byte[][] forms = Forms(KafkaKeyFormat.JsonString, "é");

        forms.Should().HaveCount(2, "minimal escaping and System.Text.Json's default escaping differ for a non-ASCII character (F8)");
        forms.Select(Encoding.UTF8.GetString).Should().BeEquivalentTo("\"é\"", "\"\\u00E9\"");
        Forms(KafkaKeyFormat.JsonString, "abc").Should().ContainSingle("the two forms are equal for plain ASCII").Which.Should().Equal("\"abc\""u8.ToArray());
    }

    [Fact]
    public void A_json_string_escapes_quotes_backslashes_and_controls_minimally()
    {
        byte[][] forms = Forms(KafkaKeyFormat.JsonString, "a\"b\\c\n\u0001");

        forms.Select(Encoding.UTF8.GetString).Should().Contain("\"a\\\"b\\\\c\\n\\u0001\"");
    }

    [Fact]
    public void A_json_string_prefix_has_no_closing_quote_and_formats_by_unescaping()
    {
        KafkaKeyFormat.JsonString.SupportsPrefix.Should().BeTrue();
        KafkaKeyFormat.JsonString.TryEncodePrefix("ab", out IReadOnlyList<byte[]> prefix, out _).Should().BeTrue();
        prefix.Should().ContainSingle().Which.Should().Equal("\"ab"u8.ToArray());

        KafkaKeyFormat.JsonString.TryFormat("\"a\\\"b\""u8, out string? text).Should().BeTrue();
        text.Should().Be("a\"b");
        KafkaKeyFormat.JsonString.TryFormat("plain"u8, out string? plain).Should().BeTrue();
        plain.Should().Be("plain");
    }

    [Fact]
    public void A_64_bit_integer_is_eight_big_endian_bytes()
    {
        Forms(KafkaKeyFormat.Int64BigEndian, "258").Should().ContainSingle().Which.Should().Equal(0, 0, 0, 0, 0, 0, 1, 2);
        Forms(KafkaKeyFormat.Int64BigEndian, "-1").Should().ContainSingle().Which.Should().Equal(Enumerable.Repeat((byte) 0xFF, 8));
        KafkaKeyFormat.Int64BigEndian.TryFormat([0, 0, 0, 0, 0, 0, 1, 2], out string? text).Should().BeTrue();
        text.Should().Be("258");
        KafkaKeyFormat.Int64BigEndian.TryFormat([0, 0], out _).Should().BeFalse();
        KafkaKeyFormat.Int64BigEndian.SupportsPrefix.Should().BeFalse();
    }

    [Fact]
    public void A_32_bit_integer_is_four_big_endian_bytes_and_refuses_what_does_not_fit()
    {
        Forms(KafkaKeyFormat.Int32BigEndian, "258").Should().ContainSingle().Which.Should().Equal(0, 0, 1, 2);
        KafkaKeyFormat.Int32BigEndian.TryEncode("4294967296", out _, out string? problem).Should().BeFalse();
        problem.Should().NotContain("4294967296");
    }

    [Fact]
    public void Guid_formats_differ_in_byte_order()
    {
        const string guid = "00112233-4455-6677-8899-aabbccddeeff";

        Forms(KafkaKeyFormat.GuidRfc4122, guid).Should().ContainSingle().Which.Should().Equal(0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF);
        Forms(KafkaKeyFormat.GuidDotNet, guid).Should().ContainSingle().Which.Should().Equal(0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF);

        KafkaKeyFormat.GuidRfc4122.TryFormat(Forms(KafkaKeyFormat.GuidRfc4122, guid)[0], out string? text).Should().BeTrue();
        text.Should().Be(guid);
        KafkaKeyFormat.GuidDotNet.TryFormat(Forms(KafkaKeyFormat.GuidDotNet, guid)[0], out string? dotNet).Should().BeTrue();
        dotNet.Should().Be(guid);
        KafkaKeyFormat.GuidRfc4122.TryFormat([1], out _).Should().BeFalse();
    }

    [Fact]
    public void Bytes_is_hexadecimal_with_an_optional_prefix_marker_and_supports_a_prefix()
    {
        Forms(KafkaKeyFormat.Bytes, "0xDEADbeef").Should().ContainSingle().Which.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
        Forms(KafkaKeyFormat.Bytes, "00ff").Should().ContainSingle().Which.Should().Equal(0x00, 0xFF);
        KafkaKeyFormat.Bytes.SupportsPrefix.Should().BeTrue();
        KafkaKeyFormat.Bytes.TryEncodePrefix("ab", out IReadOnlyList<byte[]> prefix, out _).Should().BeTrue();
        prefix.Should().ContainSingle().Which.Should().Equal(0xAB);
        KafkaKeyFormat.Bytes.TryFormat([0xDE, 0xAD], out string? text).Should().BeTrue();
        text.Should().Be("0xdead");
    }

    [Theory]
    [InlineData("Int64BigEndian")]
    [InlineData("Int32BigEndian")]
    [InlineData("GuidRfc4122")]
    [InlineData("GuidDotNet")]
    [InlineData("Bytes")]
    public void A_problem_never_contains_the_literal(string name)
    {
        KafkaKeyFormat format = name switch
        {
            "Int64BigEndian" => KafkaKeyFormat.Int64BigEndian,
            "Int32BigEndian" => KafkaKeyFormat.Int32BigEndian,
            "GuidRfc4122" => KafkaKeyFormat.GuidRfc4122,
            "GuidDotNet" => KafkaKeyFormat.GuidDotNet,
            _ => KafkaKeyFormat.Bytes,
        };

        format.TryEncode(Literal, out IReadOnlyList<byte[]> forms, out string? problem).Should().BeFalse();
        forms.Should().BeEmpty();
        problem.Should().NotBeNullOrWhiteSpace().And.NotContain(Literal);
    }

    [Fact]
    public void A_format_without_prefix_support_says_why_without_the_literal()
    {
        KafkaKeyFormat.Int64BigEndian.TryEncodePrefix(Literal, out IReadOnlyList<byte[]> forms, out string? problem).Should().BeFalse();
        forms.Should().BeEmpty();
        problem.Should().Be("a prefix needs a text key format, and this topic's keys are Int64BigEndian");
    }

    [Fact]
    public void Odd_hex_is_refused()
    {
        KafkaKeyFormat.Bytes.TryEncode("abc", out _, out string? problem).Should().BeFalse();
        problem.Should().NotContain("abc");
    }

    [Fact]
    public void A_custom_format_can_be_derived()
    {
        new Custom().Name.Should().Be("custom");
    }

    private sealed class Custom() : KafkaKeyFormat("custom")
    {
        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            forms = [];
            problem = null;
            return true;
        }

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text)
        {
            text = null;
            return false;
        }
    }
}
