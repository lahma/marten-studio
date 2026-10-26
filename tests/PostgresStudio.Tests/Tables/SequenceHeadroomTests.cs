using PostgresStudio.Services.Database;

namespace PostgresStudio.Tests.Tables;

/// <summary>The database browser's on-demand sequence headroom: <c>int4</c> and <c>bigint</c> columns, cycling and descending sequences, and the rate two reads make.</summary>
public class SequenceHeadroomTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static SequenceHeadroomFacts Bigint(long? columnMaximum, string? columnTypes = null, DateTimeOffset? readAt = null) =>
        new("bigint", 1, 1, 1, long.MaxValue, false, columnTypes, columnMaximum, readAt ?? At);

    [Fact]
    public void An_int4_column_on_a_bigint_sequence_is_judged_against_int4()
    {
        SequenceHeadroomReading reading = SequenceHeadroom.Of(1_073_741_824, Bigint(int.MaxValue, "integer"));

        reading.LimitIsColumn.Should().BeTrue();
        reading.Limit.Should().Be(int.MaxValue);
        SequenceHeadroom.Percent(reading.Used).Should().Be("50.0%");
        reading.Remaining.Should().Be(int.MaxValue - 1_073_741_824d);
        reading.PerSecond.Should().BeNull("one read makes no rate");
    }

    [Fact]
    public void A_bigint_column_on_a_bigint_sequence_is_judged_against_the_sequences_own_range()
    {
        SequenceHeadroomReading reading = SequenceHeadroom.Of(1_000_000, Bigint(long.MaxValue, "bigint"));

        reading.LimitIsColumn.Should().BeFalse();
        reading.Limit.Should().Be(long.MaxValue);
        SequenceHeadroom.Percent(reading.Used).Should().Be("0.0%");
    }

    [Fact]
    public void Two_reads_make_a_rate_and_a_time_left()
    {
        SequenceHeadroomFacts later = Bigint(int.MaxValue, "integer", At.AddSeconds(100));
        SequenceHeadroomReading reading = SequenceHeadroom.Of(2_000_000_100, later, (2_000_000_000, At));

        reading.PerSecond.Should().Be(1);
        reading.Left.Should().Be(TimeSpan.FromSeconds(int.MaxValue - 2_000_000_100d));
        SequenceHeadroom.Describe(reading.Left!.Value).Should().Be("about 4 years", "147,483,547 values at one a second");

        SequenceHeadroom.Of(2_000_000_000, later, (2_000_000_000, At)).Left.Should().BeNull("nothing taken, nothing to project");
        SequenceHeadroom.Of(2_000_000_000, later, (2_000_000_000, At)).PerSecond.Should().Be(0);
    }

    [Fact]
    public void A_descending_sequence_runs_towards_its_minimum()
    {
        SequenceHeadroomFacts descending = new("integer", -1, -1, int.MinValue, -1, false, "integer", int.MaxValue, At);
        SequenceHeadroomReading reading = SequenceHeadroom.Of(-1_073_741_825, descending);

        reading.Limit.Should().Be(int.MinValue);
        SequenceHeadroom.Percent(reading.Used).Should().Be("50.0%");

        SequenceHeadroomReading rate = SequenceHeadroom.Of(-1_073_741_925, descending with { ReadAt = At.AddSeconds(10) }, (-1_073_741_825, At));
        rate.PerSecond.Should().Be(10);
    }

    [Fact]
    public void A_cycling_sequence_reads_its_share_and_one_never_called_has_used_nothing()
    {
        SequenceHeadroomFacts cycling = new("smallint", 1, 1, 1, short.MaxValue, true, "smallint", short.MaxValue, At);
        SequenceHeadroom.Percent(SequenceHeadroom.Of(16_384, cycling).Used).Should().Be("50.0%");

        SequenceHeadroom.Of(null, Bigint(int.MaxValue, "integer")).Used.Should().Be(0);
    }

    [Theory]
    [InlineData(800, "about 2 years")]
    [InlineData(40, "about 40 days")]
    [InlineData(1, "about 24 hours")]
    [InlineData(0.05, "under two hours")]
    public void How_long_is_left_is_said_as_a_person_says_it(double days, string said) =>
        SequenceHeadroom.Describe(TimeSpan.FromDays(days)).Should().Be(said);

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "work_mem=64MB" }, false)]
    [InlineData(new[] { "search_path=\"\"" }, true)]
    [InlineData(new[] { "search_path=pg_catalog, pg_temp" }, true)]
    [InlineData(new[] { "SEARCH_PATH=public" }, true)]
    public void A_search_path_set_to_anything_pins_it_and_an_empty_one_is_the_tightest(string[] config, bool pinned) =>
        RoutineSecurity.PinsSearchPath(config).Should().Be(pinned, "B0 §9: f_bad and f_other_setting flagged, f_empty and f_good pinned");
}
