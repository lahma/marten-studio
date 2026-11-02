using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.0-HARD: an error's caret is never off-screen at a narrow width - a long line is cut to a window around the caret, marked
/// with an ellipsis where it was cut, and the caret stays under the character it points at.
/// </summary>
public class QueryCaretTests
{
    [Fact]
    public void A_short_line_is_shown_whole_with_the_caret_under_its_character()
    {
        (string source, string line) = QueryCaret.Window("select * fron t", 9);

        source.Should().Be("select * fron t");
        line.Should().Be("         ^");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(150)]
    [InlineData(299)]
    public void A_long_line_is_cut_around_the_caret_which_stays_under_its_character(int column)
    {
        string text = string.Concat(Enumerable.Range(0, 300).Select(static i => (char) ('a' + (i % 26))));

        (string source, string line) = QueryCaret.Window(text, column);

        source.Length.Should().BeLessThanOrEqualTo(QueryCaret.Width + (2 * QueryCaret.Cut.Length));
        int caret = line.IndexOf('^', StringComparison.Ordinal);
        caret.Should().BeLessThan(source.Length);
        source[caret].Should().Be(text[column], "the caret points at the same character it did in the whole line");
        source.StartsWith(QueryCaret.Cut, StringComparison.Ordinal).Should().Be(column > QueryCaret.Width / 2);
    }

    [Fact]
    public void A_column_at_or_past_the_end_of_the_line_points_just_after_its_last_character()
    {
        (string source, string line) = QueryCaret.Window("abc", 3);
        source.Should().Be("abc");
        line.Should().Be("   ^", "an error at end of input points after the last character");

        (_, string past) = QueryCaret.Window("abc", 10);
        past.Should().Be("   ^");

        (string longSource, string longLine) = QueryCaret.Window(new string('x', 300), 300);
        longLine.IndexOf('^', StringComparison.Ordinal).Should().Be(longSource.Length, "a cut line's caret is still just after its end");
    }
}
