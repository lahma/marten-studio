using System.Collections.Immutable;
using System.Text;

using MartenStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// The server-side SQL colouring behind the Schema screen's migration preview, function definitions and
/// DDL script.
/// </summary>
/// <remarks>
/// The assertions are golden token strings rather than "the markup contains a span", because the failure
/// this catches is a <em>mis</em>-classified run, not a missing one. A lexer that called the body of a
/// dollar-quoted function one long string literal would still render spans, still look plausible in a
/// screenshot, and still be useless.
/// </remarks>
public class SqlTokenizerTests
{
    [Fact]
    public void Keywords_identifiers_and_punctuation_get_their_own_kinds()
    {
        Golden("""create table "studio"."mt_doc_customer" (id uuid);""").Should().Be(
            "K:create|P: |K:table|P: |I:\"studio\"|O:.|I:\"mt_doc_customer\"|P: |O:(|P:id|P: |K:uuid|O:)|O:;");
    }

    [Fact]
    public void A_string_literal_with_a_doubled_quote_stays_one_literal()
    {
        // '' is an escaped quote in Postgres, not the end of the literal. A lexer that got this wrong
        // would colour the rest of the file as code and the code as a string, alternately.
        Golden("select 'it''s fine' as note").Should().Be(
            "K:select|P: |S:'it''s fine'|P: |K:as|P: |P:note");
    }

    [Fact]
    public void A_line_comment_runs_to_the_end_of_its_line_and_no_further()
    {
        Lines("-- drop everything\nselect 1").Should().HaveCount(2);

        Golden("-- drop everything\nselect 1").Should().Be(
            "C:-- drop everything|K:select|P: |N:1");
    }

    [Fact]
    public void A_block_comment_spans_lines_and_the_gutter_still_counts_them()
    {
        SqlDocument document = SqlTokenizer.Tokenize("/* one\n   two */ select 1");

        document.Lines.Should().HaveCount(2);
        document.Lines[0].Tokens[0].Kind.Should().Be(SqlTokenKind.Comment);
        document.Lines[1].Tokens[0].Kind.Should().Be(SqlTokenKind.Comment);
        document.Lines[1].Number.Should().Be(2);
    }

    /// <summary>
    /// The one that matters: every function body Marten installs is dollar-quoted.
    /// </summary>
    [Fact]
    public void A_dollar_quoted_body_is_one_string_however_many_quotes_are_inside_it()
    {
        const string Sql = """
            CREATE FUNCTION studio.mt_upsert_customer() RETURNS void LANGUAGE plpgsql AS $function$
            BEGIN
              INSERT INTO studio.mt_doc_customer ("data") VALUES ('{}');
            END;
            $function$;
            """;

        SqlDocument document = SqlTokenizer.Tokenize(Sql);

        // The body is one token that spans lines, so no keyword inside it is coloured as one.
        Flatten(document).Should().NotContain("K:BEGIN");
        Flatten(document).Should().NotContain("K:INSERT");
        Flatten(document).Should().Contain("S:$function$");

        // And the statement around it is still coloured.
        Flatten(document).Should().Contain("K:CREATE");
        Flatten(document).Should().Contain("K:FUNCTION");
    }

    [Fact]
    public void An_unterminated_dollar_quote_ends_at_the_end_of_the_text_rather_than_looping()
    {
        SqlDocument document = SqlTokenizer.Tokenize("select $tag$ never closed");

        document.Lines.Should().HaveCount(1);
        document.Lines[0].Tokens[^1].Kind.Should().Be(SqlTokenKind.String);
    }

    [Fact]
    public void A_lone_dollar_is_an_operator_rather_than_the_start_of_a_literal()
    {
        Golden("where x = $1").Should().Be("K:where|P: |P:x|P: |O:=|P: |O:$|N:1");
    }

    [Fact]
    public void Numbers_are_their_own_kind_and_a_word_with_digits_is_not_a_number()
    {
        Golden("limit 100 offset 2.5 and col1").Should().Be(
            "K:limit|P: |N:100|P: |K:offset|P: |N:2.5|P: |K:and|P: |P:col1");
    }

    [Fact]
    public void Keywords_are_matched_without_regard_to_case()
    {
        Golden("SeLeCt").Should().Be("K:SeLeCt");
    }

    [Fact]
    public void Concatenating_every_token_reproduces_each_line_exactly()
    {
        const string Sql = """
            alter table studio.mt_doc_order
                add column customer_id uuid; -- a trailing comment
            """;

        SqlDocument document = SqlTokenizer.Tokenize(Sql);

        string[] expected = Sql.Split('\n');
        document.Lines.Should().HaveCount(expected.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            var line = new StringBuilder();
            foreach (SqlToken token in document.Lines[i].Tokens)
            {
                line.Append(token.Text);
            }

            line.ToString().Should().Be(expected[i].TrimEnd('\r'),
                "a colouring lexer that changes the text is not colouring it");
        }
    }

    [Fact]
    public void Windows_line_endings_do_not_produce_blank_lines()
    {
        SqlDocument document = SqlTokenizer.Tokenize("select 1\r\nselect 2\r\n");

        document.Lines.Should().HaveCount(2);
        document.Lines[1].Tokens[0].Text.Should().Be("select");
    }

    [Fact]
    public void A_long_script_is_clamped_and_says_so()
    {
        string sql = string.Join('\n', Enumerable.Range(0, 50).Select(static i => $"select {i};"));

        SqlDocument document = SqlTokenizer.Tokenize(sql, maxLines: 10);

        document.Lines.Should().HaveCount(10);
        document.Truncated.Should().BeTrue();
        document.TotalLines.Should().Be(50);
    }

    [Fact]
    public void Nothing_in_is_nothing_out()
    {
        SqlTokenizer.Tokenize(null).Should().BeSameAs(SqlDocument.Empty);
        SqlTokenizer.Tokenize(string.Empty).Should().BeSameAs(SqlDocument.Empty);
    }

    /// <summary>Every token of every line, as <c>Kind:text</c> joined by <c>|</c>.</summary>
    private static string Golden(string sql) => Flatten(SqlTokenizer.Tokenize(sql));

    private static string Flatten(SqlDocument document)
    {
        var builder = new StringBuilder();

        foreach (SqlLine line in document.Lines)
        {
            foreach (SqlToken token in line.Tokens)
            {
                if (builder.Length > 0)
                {
                    builder.Append('|');
                }

                builder.Append(Abbreviate(token.Kind)).Append(':').Append(token.Text);
            }
        }

        return builder.ToString();
    }

    private static ImmutableArray<SqlLine> Lines(string sql) => SqlTokenizer.Tokenize(sql).Lines;

    private static char Abbreviate(SqlTokenKind kind) => kind switch
    {
        SqlTokenKind.Keyword => 'K',
        SqlTokenKind.Identifier => 'I',
        SqlTokenKind.String => 'S',
        SqlTokenKind.Comment => 'C',
        SqlTokenKind.Number => 'N',
        SqlTokenKind.Operator => 'O',
        _ => 'P',
    };
}
