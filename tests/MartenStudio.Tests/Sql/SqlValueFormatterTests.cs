using System.Globalization;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// Cell formatting for the SQL console. Two things are being defended: a circuit that must not be asked to
/// carry a 40 MB <c>bytea</c> value, and a timestamp that must mean the same thing to every reader.
/// </summary>
public class SqlValueFormatterTests
{
    private readonly SqlValueFormatter formatter = new();

    [Fact]
    public void Null_is_a_kind_of_its_own_and_not_an_empty_string()
    {
        formatter.Format(null).Kind.ToString().Should().Be("Null");
        formatter.Format(DBNull.Value).Kind.ToString().Should().Be("Null");
        formatter.Format(DBNull.Value).Text.Should().Be("NULL");

        formatter.Format(string.Empty).Kind.ToString().Should().Be("Text");
        formatter.Format(string.Empty).Text.Should().BeEmpty();
    }

    [Fact]
    public void Binary_is_hex_with_a_byte_count_and_never_the_whole_payload()
    {
        var cell = formatter.Format(new byte[] { 0xde, 0xad, 0xbe, 0xef });

        cell.Kind.ToString().Should().Be("Binary");
        cell.Text.Should().Be("\\xdeadbeef (4 bytes)");
        cell.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public void A_large_binary_value_is_cut_and_says_how_big_it_really_is()
    {
        var cell = formatter.Format(new byte[4096]);

        cell.IsTruncated.Should().BeTrue();
        cell.FullLength.Should().Be(4096);
        cell.Text.Should().EndWith("(4096 bytes)");
        cell.Text.Length.Should().BeLessThan(100);
    }

    [Fact]
    public void Json_is_its_own_kind_and_is_capped_at_four_kibibytes()
    {
        var json = "{\"a\":\"" + new string('x', 10_000) + "\"}";

        var cell = formatter.Format(json, "jsonb");

        cell.Kind.ToString().Should().Be("Json");
        cell.IsTruncated.Should().BeTrue();
        cell.Text.Length.Should().Be((4 * 1024) + 1, "the cut text plus the ellipsis");
        cell.FullLength.Should().Be(json.Length);
    }

    [Fact]
    public void A_text_column_holding_json_shaped_text_is_still_text()
    {
        formatter.Format("{\"a\":1}", "text").Kind.ToString().Should().Be("Text");
        formatter.Format("{\"a\":1}", "json").Kind.ToString().Should().Be("Json");
    }

    [Fact]
    public void Every_cell_is_capped_and_the_cap_is_configurable()
    {
        var tight = new SqlValueFormatter(new SqlValueFormatterOptions { MaxCellLength = 10 });

        var cell = tight.Format(new string('y', 50));

        cell.Text.Should().Be(new string('y', 10) + "…");
        cell.IsTruncated.Should().BeTrue();
        cell.FullLength.Should().Be(50);
    }

    [Fact]
    public void A_timestamp_carries_its_kind_so_a_reader_can_tell_what_it_means()
    {
        formatter.Format(new DateTime(2026, 9, 14, 10, 30, 0, DateTimeKind.Utc)).Text
            .Should().Be("2026-09-14T10:30:00.0000000Z");

        formatter.Format(new DateTime(2026, 9, 14, 10, 30, 0, DateTimeKind.Unspecified)).Text
            .Should().Be("2026-09-14T10:30:00.0000000", "a `timestamp` column has no zone and must not gain one");

        formatter.Format(new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.FromHours(3))).Text
            .Should().Be("2026-09-14T10:30:00.0000000+03:00");

        formatter.Format(new DateOnly(2026, 9, 14)).Text.Should().Be("2026-09-14");
        formatter.Format(new TimeOnly(10, 30)).Text.Should().Be("10:30:00.0000000");
        formatter.Format(TimeSpan.FromMinutes(90)).Text.Should().Be("01:30:00");
    }

    [Fact]
    public void Numbers_and_booleans_are_invariant_and_look_like_Postgres()
    {
        formatter.Format(1234.5m).Text.Should().Be("1234.5");
        formatter.Format(1234.5d).Text.Should().Be("1234.5");
        formatter.Format(42).Text.Should().Be("42");
        formatter.Format(42).Kind.ToString().Should().Be("Number");

        formatter.Format(true).Text.Should().Be("true", "psql prints t/true, never .NET's True");
        formatter.Format(false).Text.Should().Be("false");
    }

    [Fact]
    public void Numbers_do_not_change_with_the_thread_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fi-FI");

            formatter.Format(1234.5m).Text.Should().Be("1234.5", "a comma here would be a different number");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_uuid_is_its_own_kind()
    {
        var id = Guid.NewGuid();

        formatter.Format(id).Text.Should().Be(id.ToString("D"));
        formatter.Format(id).Kind.ToString().Should().Be("Uuid");
    }

    [Fact]
    public void An_array_is_rendered_the_way_Postgres_writes_one()
    {
        string[] letters = ["a", "b", "c"];

        var cell = formatter.Format(letters);

        cell.Kind.ToString().Should().Be("Array");
        cell.Text.Should().Be("{a,b,c}");
    }

    [Fact]
    public void An_array_of_numbers_and_nulls_keeps_the_nulls_visible()
    {
        formatter.Format(new object?[] { 1, null, 3 }).Text.Should().Be("{1,NULL,3}");
    }

    [Fact]
    public void A_long_array_is_cut_by_element_count()
    {
        var tight = new SqlValueFormatter(new SqlValueFormatterOptions { MaxArrayElements = 3 });

        var cell = tight.Format(Enumerable.Range(1, 100).ToArray());

        cell.Text.Should().Be("{1,2,3,…}");
        cell.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public void An_unknown_type_falls_back_to_its_invariant_string()
    {
        formatter.Format(new Uri("https://example.com/x")).Text.Should().Be("https://example.com/x");
    }
}
