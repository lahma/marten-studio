using KStudio.Core.Scanning;

namespace KStudio.Tests.Scanning;

/// <summary>The cursor's text form (D-M5 decision 3.3): a range with no end, a live tail's, is written <c>-</c> and read back as the open end.</summary>
public class KafkaScanCursorTests
{
    [Fact]
    public void A_cursor_with_ends_round_trips_as_it_always_did()
    {
        KafkaScanCursor cursor = new([new KafkaCursorPosition("orders.v1", 0, 10, 100), new KafkaCursorPosition("orders.v1", 3, 0, 5)]);

        cursor.TryFormat(out string text).Should().BeTrue();
        text.Should().Be("v1;orders.v1:0:10:100,orders.v1:3:0:5");
        KafkaScanCursor.TryParse(text, out KafkaScanCursor parsed).Should().BeTrue();
        parsed.Positions.Should().BeEquivalentTo(cursor.Positions);
    }

    [Fact]
    public void An_open_end_is_written_as_a_dash_and_round_trips()
    {
        KafkaScanCursor cursor = new([new KafkaCursorPosition("heartbeats.v1", 0, 1_204, KafkaScanCursor.OpenEnd), new KafkaCursorPosition("heartbeats.v1", 1, 7, KafkaScanCursor.OpenEnd)]);

        cursor.TryFormat(out string text).Should().BeTrue();
        text.Should().Be("v1;heartbeats.v1:0:1204:-,heartbeats.v1:1:7:-");
        text.Should().NotContain("9223372036854775807", "the open end is not written as a number");

        KafkaScanCursor.TryParse(text, out KafkaScanCursor parsed).Should().BeTrue();
        parsed.Positions.Should().BeEquivalentTo(cursor.Positions);
        parsed.Positions.Should().OnlyContain(static x => x.End == KafkaScanCursor.OpenEnd);
    }

    [Theory]
    [InlineData("v1;t:0:5:-x")]
    [InlineData("v1;t:0:5:--")]
    [InlineData("v1;t:0:-:5")]
    [InlineData("v1;t:0:-1:-")]
    [InlineData("v1;t:0:5:")]
    [InlineData("v1;t:0:5:-,t:0:6:-")]
    public void A_dash_is_accepted_only_as_the_whole_end_of_a_position(string text)
    {
        KafkaScanCursor.TryParse(text, out _).Should().BeFalse();
    }
}
