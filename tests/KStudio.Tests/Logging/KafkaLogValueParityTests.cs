using KStudio.Core.Logging;

using PostgresStudio.Services;

namespace KStudio.Tests.Logging;

/// <summary>
/// <c>KafkaLogValue</c> is a verbatim port of the diagnostics engine's <c>StudioLogValue.Cap</c> and <c>CapText</c> (F9: the engine must not
/// reference the diagnostics assembly), and this holds the two equal on one table of inputs: line breaks, bidirectional overrides, a backslash
/// and an <c>n</c>, a surrogate pair, a name at and past its bound, and null.
/// </summary>
public class KafkaLogValueParityTests
{
    private static string Chars(params int[] codes) => string.Concat(codes.Select(static x => ((char) x).ToString()));

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    public static IEnumerable<object?[]> Inputs() => Values().Select(static x => new object?[] { x });

    private static string?[] Values() =>
    [
        null,
        string.Empty,
        "plain",
        "orders.v1",
        "a\nb",
        "a\r\nb",
        "a\\nb",
        "tab\there",
        Chars(0x202E) + "rtl override" + Chars(0x202C),
        Chars(0x2028) + " line sep " + Chars(0x2029) + " para sep",
        "nul" + Chars(0) + "char",
        Chars(0x200B) + "zero width",
        "emoji " + char.ConvertFromUtf32(0x1F600) + " pair",
        new string('x', 255),
        new string('x', 256),
        new string('x', 257),
        new string('x', 300),
        new string('x', 3999),
        new string('x', 4000),
        new string('x', 4001),
        new string('y', 5000),
        new string('\n', 100),
        new string('\\', 200),
        Repeat(char.ConvertFromUtf32(0x1F600), 200),
        "name" + new string('\n', 300),
    ];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void Cap_equals_the_original(string? value)
    {
        KafkaLogValue.Cap(value).Should().Be(StudioLogValue.Cap(value));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void CapText_equals_the_original(string? value)
    {
        KafkaLogValue.CapText(value).Should().Be(StudioLogValue.CapText(value));
    }

    [Fact]
    public void The_bounds_are_the_originals()
    {
        KafkaLogValue.MaxLength.Should().Be(StudioLogValue.MaxLength);
        KafkaLogValue.MaxTextLength.Should().Be(StudioLogValue.MaxTextLength);
    }

    [Fact]
    public void A_line_break_is_escaped_never_dropped_and_a_backslash_is_doubled()
    {
        KafkaLogValue.Cap("a\nb").Should().Be("a\\nb");
        KafkaLogValue.Cap("a\\nb").Should().Be("a\\\\nb");
        KafkaLogValue.Cap(new string('x', 300)).Should().EndWith("… (300 characters)");
    }
}
