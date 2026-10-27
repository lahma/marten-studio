using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// X4's bound parser over the text <c>pg_get_expr(relpartbound, 0)</c> really returns - every string below captured from
/// <c>postgres:14.24-alpine</c> and <c>postgres:18.6-alpine</c> on 2026-10-27, identical on both - for each key type it reads:
/// <c>date</c>, <c>timestamp</c>, <c>timestamptz</c>, <c>smallint</c>, <c>integer</c> and <c>bigint</c>. Anything else is not read.
/// </summary>
public class PartitionBoundTextTests
{
    private const string Iso = "ISO, MDY";

    private static double Epoch(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000d;

    // ---- Range bounds, by key type, as Postgres writes them under ISO and UTC -------------------------------------------

    [Fact]
    public void A_timestamptz_range_is_read_as_the_instants_it_names()
    {
        RangeBound bound = PartitionBoundText.Range(
            "FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00')", "timestamp with time zone", Iso)!;

        bound.Lower.Should().Be(RangeEnd.Of(Epoch(2026, 10, 1)));
        bound.Upper.Should().Be(RangeEnd.Of(Epoch(2026, 11, 1)));

        PartitionBoundText.Range("FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-11-01 00:00:00.5+00')", "timestamp with time zone", Iso)!
            .Upper.Value.Should().Be(Epoch(2026, 11, 1) + 0.5, "a fraction is kept to the microsecond");
        PartitionBoundText.Range("FOR VALUES FROM (MINVALUE) TO ('2019-12-31 18:30:00+00')", "timestamp with time zone", Iso)!
            .Should().Be(new RangeBound(RangeEnd.Min, RangeEnd.Of(Epoch(2019, 12, 31, 18, 30))));
        PartitionBoundText.Range("FOR VALUES FROM ('2030-01-01 00:00:00+00') TO (MAXVALUE)", "timestamp with time zone", Iso)!
            .Upper.Should().Be(RangeEnd.Max);
    }

    [Theory]
    [InlineData("FOR VALUES FROM ('2026-10-01 03:00:00+03') TO ('2026-11-01 02:00:00+02')", "Europe/Helsinki: summer, then winter")]
    [InlineData("FOR VALUES FROM ('2026-10-01 05:30:00+05:30') TO ('2026-11-01 05:30:00+05:30')", "Asia/Kolkata: a half-hour offset")]
    [InlineData("FOR VALUES FROM ('2026-09-30 21:30:00-02:30') TO ('2026-10-31 21:30:00-02:30')", "America/St_Johns: a negative half hour")]
    public void A_timestamptz_bound_written_in_another_zone_is_the_same_instant(string text, string zone)
    {
        // The same partition, [2026-10-01 00:00+00, 2026-11-01 00:00+00), read by sessions in three zones.
        PartitionBoundText.Range(text, "timestamp with time zone", Iso)
            .Should().Be(new RangeBound(RangeEnd.Of(Epoch(2026, 10, 1)), RangeEnd.Of(Epoch(2026, 11, 1))), zone);
    }

    [Fact]
    public void An_offset_with_seconds_is_read_too()
    {
        // Europe/Amsterdam's local mean time before 1937, as a timestamptz prints it: +00:17:30.
        PartitionBoundText.End("'1890-01-01 00:17:30+00:17:30'", "timestamp with time zone", Iso).Should().Be(RangeEnd.Of(Epoch(1890, 1, 1)));
    }

    [Fact]
    public void A_timestamp_range_is_read_as_utc_and_infinity_is_unbounded()
    {
        RangeBound bound = PartitionBoundText.Range(
            "FOR VALUES FROM ('2026-10-01 00:00:00') TO ('2026-11-01 06:30:15.123456')", "timestamp without time zone", Iso)!;
        bound.Lower.Should().Be(RangeEnd.Of(Epoch(2026, 10, 1)));
        bound.Upper.Kind.Should().Be(RangeEndKind.Value);
        bound.Upper.Value.Should().BeApproximately(Epoch(2026, 11, 1, 6, 30, 15) + 0.123456, 1e-6);

        PartitionBoundText.Range("FOR VALUES FROM ('2026-11-01 06:30:15.123456') TO ('infinity')", "timestamp without time zone", Iso)!
            .Upper.Should().Be(RangeEnd.Max, "'infinity' never runs out, as MAXVALUE never does");
    }

    [Fact]
    public void A_date_before_christ_is_not_read()
    {
        RangeBound bound = PartitionBoundText.Range(
            "FOR VALUES FROM ('0044-03-15 00:00:00 BC') TO ('2000-01-01 00:00:00')", "timestamp without time zone", Iso)!;

        bound.Lower.Should().Be(RangeEnd.Unread);
        bound.Upper.Should().Be(RangeEnd.Of(Epoch(2000, 1, 1)));
        bound.IsRead.Should().BeFalse();
    }

    [Fact]
    public void A_date_range_is_read_as_midnights_utc()
    {
        PartitionBoundText.Range("FOR VALUES FROM ('2026-10-01') TO ('2026-11-01')", "date", Iso)
            .Should().Be(new RangeBound(RangeEnd.Of(Epoch(2026, 10, 1)), RangeEnd.Of(Epoch(2026, 11, 1))));
        PartitionBoundText.Range("FOR VALUES FROM ('2026-11-01') TO ('infinity')", "date", Iso)!.Upper.Should().Be(RangeEnd.Max);
    }

    [Fact]
    public void An_integer_bound_is_read_bare_or_quoted_as_postgres_writes_each()
    {
        // A non-negative integer is written bare, a negative one quoted; smallint and bigint are always quoted.
        PartitionBoundText.Range("FOR VALUES FROM ('-1000') TO (0)", "integer", Iso)
            .Should().Be(new RangeBound(RangeEnd.Of(-1000), RangeEnd.Of(0)));
        PartitionBoundText.Range("FOR VALUES FROM (0) TO (1000)", "integer", Iso)
            .Should().Be(new RangeBound(RangeEnd.Of(0), RangeEnd.Of(1000)));
        PartitionBoundText.Range("FOR VALUES FROM (1000) TO (MAXVALUE)", "integer", Iso)!.Upper.Should().Be(RangeEnd.Max);
        PartitionBoundText.Range("FOR VALUES FROM ('1') TO ('32767')", "smallint", Iso)
            .Should().Be(new RangeBound(RangeEnd.Of(1), RangeEnd.Of(32767)));
        PartitionBoundText.Range("FOR VALUES FROM (MINVALUE) TO ('-9223372036854775807')", "bigint", Iso)
            .Should().Be(new RangeBound(RangeEnd.Min, RangeEnd.Of(-9223372036854775807)));
        PartitionBoundText.Range("FOR VALUES FROM ('-9223372036854775807') TO ('9223372036854775807')", "bigint", "SQL, DMY")!
            .Upper.Should().Be(RangeEnd.Of(9223372036854775807), "an integer does not depend on the DateStyle");
    }

    // ---- What is not read ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("FOR VALUES FROM ('01/10/2026 00:00:00 UTC') TO ('01/11/2026 00:00:00 UTC')", "timestamp with time zone", "SQL, DMY")]
    [InlineData("FOR VALUES FROM ('Thu Oct 01 00:00:00 2026 UTC') TO ('Sun Nov 01 00:00:00 2026 UTC')", "timestamp with time zone", "Postgres, MDY")]
    [InlineData("FOR VALUES FROM ('01.10.2026 00:00:00 UTC') TO ('01.11.2026 00:00:00 UTC')", "timestamp with time zone", "German, DMY")]
    [InlineData("FOR VALUES FROM ('01/10/2026') TO ('01/11/2026')", "date", "SQL, DMY")]
    [InlineData("FOR VALUES FROM ('10-01-2026') TO ('11-01-2026')", "date", "Postgres, MDY")]
    [InlineData("FOR VALUES FROM ('Thu Oct 01 00:00:00 2026') TO ('Sun Nov 01 06:30:15.123456 2026')", "timestamp without time zone", "Postgres, MDY")]
    public void A_time_bound_in_a_DateStyle_other_than_ISO_is_not_read(string text, string keyType, string dateStyle)
    {
        RangeBound bound = PartitionBoundText.Range(text, keyType, dateStyle)!;

        bound.Should().Be(new RangeBound(RangeEnd.Unread, RangeEnd.Unread), "a zone abbreviation is not an instant, so only ISO is read");
        PartitionBoundText.IsIsoDateStyle(dateStyle).Should().BeFalse();
    }

    [Fact]
    public void ISO_with_either_field_order_is_ISO()
    {
        PartitionBoundText.IsIsoDateStyle("ISO, MDY").Should().BeTrue();
        PartitionBoundText.IsIsoDateStyle("ISO, DMY").Should().BeTrue();
        PartitionBoundText.IsIsoDateStyle(null).Should().BeTrue("Postgres' default");

        // ISO, DMY writes exactly what ISO, MDY does (measured).
        PartitionBoundText.Range("FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00')", "timestamp with time zone", "ISO, DMY")!
            .IsRead.Should().BeTrue();
    }

    [Theory]
    [InlineData("DEFAULT")]
    [InlineData("FOR VALUES IN ('acme')")]
    [InlineData("FOR VALUES WITH (modulus 2, remainder 0)")]
    [InlineData("FOR VALUES FROM (0, 0) TO (10, MAXVALUE)")]
    [InlineData("FOR VALUES FROM (0) TO (1000")]
    [InlineData("")]
    public void Anything_but_a_single_column_range_is_no_range(string text)
    {
        PartitionBoundText.Range(text, "integer", Iso).Should().BeNull();
    }

    [Fact]
    public void Another_key_type_is_not_read()
    {
        PartitionBoundText.Range("FOR VALUES FROM ('a') TO ('m')", "text", Iso).Should().Be(new RangeBound(RangeEnd.Unread, RangeEnd.Unread));
        PartitionBoundText.End("'1.5'", "numeric", Iso).Should().Be(RangeEnd.Unread);
        PartitionBoundText.End("'12345678901234567890'", "bigint", Iso).Should().Be(RangeEnd.Unread, "past bigint");
        PartitionBoundText.End("'10000-01-01'", "date", Iso).Should().Be(RangeEnd.Unread, "past what the studio's clock holds");
    }

    // ---- List bounds ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_list_bound_of_one_value_is_that_value_unquoted()
    {
        PartitionBoundText.SingleListValue("FOR VALUES IN ('acme')").Should().Be("acme");
        PartitionBoundText.SingleListValue("FOR VALUES IN ('*DEFAULT*')").Should().Be("*DEFAULT*", "Marten's default tenant");
        PartitionBoundText.SingleListValue("FOR VALUES IN ('tenant \"x\"')").Should().Be("tenant \"x\"");
        PartitionBoundText.SingleListValue("FOR VALUES IN ('o''brien')").Should().Be("o'brien");
    }

    [Theory]
    [InlineData("FOR VALUES IN ('o''brien', 'two words')")]
    [InlineData("FOR VALUES IN (NULL)")]
    [InlineData("DEFAULT")]
    [InlineData("FOR VALUES FROM (0) TO (1000)")]
    [InlineData("FOR VALUES IN ('unclosed)")]
    public void A_list_bound_of_several_values_or_none_is_no_one_value(string text)
    {
        PartitionBoundText.SingleListValue(text).Should().BeNull();
    }

    // ---- The roll-up --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_range_rolls_up_to_its_newest_bound_and_where_its_rows_stop()
    {
        string[] bounds =
        [
            "FOR VALUES FROM (0) TO (1000)",
            "FOR VALUES FROM (1000) TO (2000)",
            "FOR VALUES FROM (2000) TO (3000)",
            "FOR VALUES FROM (3000) TO (4000)",
        ];

        RangeSummary summary = PartitionBoundText.Summarise(bounds, [500, 900, 0, 0], "integer", Iso);

        summary.Should().Be(new RangeSummary(4, 4000, 3000, false, 2000, 2, 0));

        PartitionBoundText.Summarise(bounds, [500, 900, 0, 1], "integer", Iso).Should().Be(new RangeSummary(4, 4000, 3000, false, 4000, 0, 0),
            "one row in the newest partition is where the rows stop");
        PartitionBoundText.Summarise(bounds, [0, 0, 0, 0], "integer", Iso).PopulatedUpper.Should().BeNull("no partition holds rows");
        PartitionBoundText.Summarise(bounds, [], "integer", Iso).PopulatedUpper.Should().BeNull("no rows were read");
    }

    [Fact]
    public void A_maxvalue_partition_or_an_unread_bound_is_said_and_never_the_newest()
    {
        RangeSummary unbounded = PartitionBoundText.Summarise(
            ["FOR VALUES FROM (0) TO (1000)", "FOR VALUES FROM (1000) TO (MAXVALUE)"], [10, 10], "integer", Iso);
        unbounded.UnboundedAbove.Should().BeTrue();
        unbounded.NewestUpper.Should().Be(1000);

        RangeSummary styled = PartitionBoundText.Summarise(
            ["FOR VALUES FROM ('01/10/2026') TO ('01/11/2026')"], [10], "date", "SQL, DMY");
        styled.Should().Be(new RangeSummary(0, null, null, false, null, 0, 1));

        PartitionBoundText.Summarise(["FOR VALUES FROM (MINVALUE) TO ('2026-11-01')"], [0], "date", Iso)
            .Should().Be(new RangeSummary(1, Epoch(2026, 11, 1), null, false, null, 0, 0), "a MINVALUE lower end has no width");
    }
}
