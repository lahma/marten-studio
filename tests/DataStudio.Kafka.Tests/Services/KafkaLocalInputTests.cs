using DataStudio.Kafka.Services;

namespace DataStudio.Kafka.Tests.Services;

/// <summary>xvfv: a date-and-time box is kept as the browser holds it, to the minute, whatever the <c>change</c> event adds.</summary>
public class KafkaLocalInputTests
{
    [Theory]
    [InlineData("2026-11-08T11:00:00", "2026-11-08T11:00")]
    [InlineData("2026-11-08T11:00:59.123", "2026-11-08T11:00")]
    [InlineData("2026-11-08T11:00", "2026-11-08T11:00")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("not a date and time", "not a date and time")]
    [InlineData("2026-11-08 11:00:00", "2026-11-08 11:00:00")]
    public void The_seconds_a_change_event_adds_are_dropped_and_nothing_else_is(string? given, string expected) =>
        KafkaLocalInput.Minutes(given).Should().Be(expected);
}
