using MartenStudio.Services.Database;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The row filter: every form, raw values, quoting, the catalog's spelling, the refusal of a comparison
/// on a column with no btree opclass - and every problem as a value with a position.
/// </summary>
public class RowFilterGrammarTests
{
    private static readonly IReadOnlyList<DatabaseColumnInfo> Columns =
    [
        TableRowTestData.Column("status", "legacy.order_status", position: 1),
        TableRowTestData.Column("total", "numeric(12,2)", position: 2),
        TableRowTestData.Column("entity_key", "text", position: 3),
        TableRowTestData.Column("payload", "json", position: 4, sortable: false),
        TableRowTestData.Column("Name", "text", position: 5),
        TableRowTestData.Column("NAME", "text", position: 6),
        TableRowTestData.Column("bad\"col", "text", position: 7, quotable: false),
        TableRowTestData.Column("location", "point", position: 8, sortable: false),
    ];

    // The operator travels as its name: the enum is internal, and a public theory cannot take it.
    [Theory]
    [InlineData("total = 12.50", nameof(RowFilterOperator.Equal), "12.50")]
    [InlineData("total != 1", nameof(RowFilterOperator.NotEqual), "1")]
    [InlineData("total <> 1", nameof(RowFilterOperator.NotEqual), "1")]
    [InlineData("total < 1", nameof(RowFilterOperator.LessThan), "1")]
    [InlineData("total <= 1", nameof(RowFilterOperator.LessThanOrEqual), "1")]
    [InlineData("total > 1", nameof(RowFilterOperator.GreaterThan), "1")]
    [InlineData("total >= 1", nameof(RowFilterOperator.GreaterThanOrEqual), "1")]
    [InlineData("total>=1", nameof(RowFilterOperator.GreaterThanOrEqual), "1")]
    [InlineData("entity_key ~ Order", nameof(RowFilterOperator.Contains), "Order")]
    [InlineData("entity_key is:null", nameof(RowFilterOperator.IsNull), null)]
    [InlineData("entity_key IS:NOTNULL", nameof(RowFilterOperator.IsNotNull), null)]
    [InlineData("entity_key = 007", nameof(RowFilterOperator.Equal), "007")]
    [InlineData("status = approved", nameof(RowFilterOperator.Equal), "approved")]
    public void Every_form_parses_and_keeps_the_raw_value(string text, string op, string? value)
    {
        RowFilterParse parse = RowFilterGrammar.Parse(text, Columns);

        parse.Errors.Should().BeEmpty();
        RowFilterTerm term = parse.Terms.Should().ContainSingle().Which;
        term.Operator.Should().Be(Enum.Parse<RowFilterOperator>(op));
        term.Value.Should().Be(value);
    }

    [Fact]
    public void Terms_are_anded_in_the_order_typed_with_their_positions()
    {
        const string text = "status = approved  total >= 0100.00 entity_key is:null";

        RowFilterParse parse = RowFilterGrammar.Parse(text, Columns);

        parse.Errors.Should().BeEmpty();
        parse.Terms.Select(static x => x.Column).Should().Equal("status", "total", "entity_key");
        parse.Terms[1].Value.Should().Be("0100.00", "SearchValue would have made it 100");
        parse.Terms[1].Position.Should().Be(text.IndexOf("total", StringComparison.Ordinal));
        text.Substring(parse.Terms[2].Position, parse.Terms[2].Length).Should().Be("entity_key is:null");
    }

    [Theory]
    [InlineData("entity_key = \"two words\"", "two words")]
    [InlineData("entity_key = 'it''s'", "it's")]
    [InlineData("entity_key = \"say \\\"hi\\\"\"", "say \"hi\"")]
    [InlineData("entity_key = \"\"", "")]
    [InlineData("entity_key = \"null\"", "null")]
    [InlineData("entity_key = a=b", "a=b")]
    public void Quoted_values_are_how_to_say_anything(string text, string value)
    {
        RowFilterParse parse = RowFilterGrammar.Parse(text, Columns);

        parse.Errors.Should().BeEmpty();
        parse.Terms.Should().ContainSingle().Which.Value.Should().Be(value);
    }

    [Fact]
    public void A_bare_column_matches_case_insensitively_and_the_catalogs_spelling_is_kept()
    {
        RowFilterGrammar.Parse("STATUS = draft", Columns).Terms.Single().Column.Should().Be("status");
        RowFilterGrammar.Parse("\"Name\" = x", Columns).Terms.Single().Column.Should().Be("Name");
        RowFilterGrammar.Parse("NAME = x", Columns).Terms.Single().Column.Should().Be("NAME", "an exact match wins");
    }

    [Theory]
    [InlineData("nope = 1", 0, 4, "There is no column 'nope'.")]
    [InlineData("status = draft nope = 1", 15, 4, "There is no column 'nope'.")]
    [InlineData("name = x", 0, 4, "matches more than one column")]
    [InlineData("\"status \" = x", 0, 9, "There is no column")]
    [InlineData("status", 0, 6, "needs an operator")]
    [InlineData("status =", 7, 1, "needs a value")]
    [InlineData("entity_key = \"open", 13, 5, "never closed")]
    [InlineData("entity_key is:empty", 11, 8, "is not a state")]
    [InlineData("entity_key = null", 0, 17, "is:null")]
    [InlineData("payload = {}", 8, 1, "no ordering")]
    [InlineData("location < 1", 9, 1, "no ordering")]
    [InlineData("= 5", 0, 1, "does not start a filter term")]
    public void Every_problem_is_a_value_with_a_position(string text, int position, int length, string message)
    {
        RowFilterParse parse = RowFilterGrammar.Parse(text, Columns);

        // The first problem is the one asked about; "= 5" goes on to report the orphaned 5 as well.
        parse.Errors.Should().NotBeEmpty(text);
        SearchGrammarError error = parse.Errors[0];
        error.Message.Should().Contain(message);
        error.Position.Should().Be(position);
        error.Length.Should().Be(length);
    }

    [Fact]
    public void A_column_that_cannot_be_quoted_is_refused()
    {
        RowFilterParse parse = RowFilterGrammar.Parse("\"bad\"\"col\" = 1", Columns);

        parse.Terms.Should().BeEmpty();
        parse.Errors.Should().ContainSingle().Which.Message.Should().Contain("cannot be put into SQL safely");
    }

    [Fact]
    public void Substring_and_null_tests_work_on_a_column_with_no_ordering()
    {
        RowFilterParse parse = RowFilterGrammar.Parse("payload ~ DESADV location is:notnull", Columns);

        parse.Errors.Should().BeEmpty();
        parse.Terms.Select(static x => x.Operator).Should().Equal(RowFilterOperator.Contains, RowFilterOperator.IsNotNull);
    }

    [Fact]
    public void One_bad_term_does_not_silence_the_others()
    {
        RowFilterParse parse = RowFilterGrammar.Parse("status = draft nope = 1 total > 5", Columns);

        parse.Terms.Select(static x => x.Column).Should().Equal("status", "total");
        parse.Errors.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Nothing_typed_is_no_filter(string? text) =>
        RowFilterGrammar.Parse(text, Columns).Should().BeEquivalentTo(RowFilterParse.Empty);

    [Theory]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("~~~~")]
    [InlineData("a b c")]
    [InlineData("status ~")]
    [InlineData("status is:")]
    [InlineData("<>")]
    [InlineData("!")]
    [InlineData(":::")]
    [InlineData("status = 'x\\")]
    public void It_never_throws(string text)
    {
        RowFilterParse parse = RowFilterGrammar.Parse(text, Columns);

        parse.HasErrors.Should().BeTrue();
        parse.Errors.Should().OnlyContain(x => x.Position >= 0 && x.Position <= text.Length && x.Length >= 1);
    }

    [Theory]
    [InlineData("entity_key", nameof(RowFilterOperator.Equal), "plain")]
    [InlineData("entity_key", nameof(RowFilterOperator.Equal), "two words")]
    [InlineData("entity_key", nameof(RowFilterOperator.Equal), "")]
    [InlineData("entity_key", nameof(RowFilterOperator.Equal), "null")]
    [InlineData("entity_key", nameof(RowFilterOperator.Equal), "\"quoted\"")]
    [InlineData("entity_key", nameof(RowFilterOperator.Equal), "back\\slash")]
    [InlineData("entity_key", nameof(RowFilterOperator.Contains), "it's")]
    [InlineData("entity_key", nameof(RowFilterOperator.IsNull), null)]
    [InlineData("Name", nameof(RowFilterOperator.Equal), "007")]
    public void A_formatted_term_parses_back_to_itself(string column, string opName, string? value)
    {
        RowFilterOperator op = Enum.Parse<RowFilterOperator>(opName);
        string text = RowFilterGrammar.Format(column, op, value);

        RowFilterParse parse = RowFilterGrammar.Parse(text, Columns);

        parse.Errors.Should().BeEmpty(text);
        RowFilterTerm term = parse.Terms.Should().ContainSingle().Which;
        term.Column.Should().Be(column);
        term.Operator.Should().Be(op);
        term.Value.Should().Be(value);
    }

    [Fact]
    public void A_column_whose_name_needs_quoting_is_quoted_when_formatted()
    {
        IReadOnlyList<DatabaseColumnInfo> odd =[TableRowTestData.Column("order total"), TableRowTestData.Column("a=b")];

        foreach (string column in new[] { "order total", "a=b" })
        {
            string text = RowFilterGrammar.Format(column, RowFilterOperator.Equal, "1");
            RowFilterGrammar.Parse(text, odd).Terms.Should().ContainSingle().Which.Column.Should().Be(column, text);
        }
    }
}
