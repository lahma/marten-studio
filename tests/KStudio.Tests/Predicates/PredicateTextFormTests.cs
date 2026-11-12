using System.Text;

using KStudio.Core.Predicates;

namespace KStudio.Tests.Predicates;

/// <summary>
/// The canonical text of decision 7.3: lower-case keywords, single quotes with doubled quotes, parentheses only where precedence needs
/// them, and the guarantee that parsing the text of a tree gives the same tree - by example, over a corpus, and over seeded random texts.
/// </summary>
public class PredicateTextFormTests
{
    private static PredicateNode? Parse(string text)
    {
        PredicateParseResult result = PredicateParser.Parse(text);
        result.Errors.Should().BeEmpty("'" + text + "' is valid");
        return result.Tree;
    }

    [Theory]
    [InlineData("KEY=\"a\"  AND  header.X IN ('b','c')", "key = 'a' and header.X in ('b', 'c')")]
    [InlineData("header['a b'] = 'x'", "header['a b'] = 'x'")]
    [InlineData("header['abc'] = 'x'", "header.abc = 'x'")]
    [InlineData("header['it''s'] = 'x'", "header['it''s'] = 'x'")]
    [InlineData("(key='a' OR key='b') AND NOT key='c'", "(key = 'a' or key = 'b') and not key = 'c'")]
    [InlineData("key='a' or key='b' and key='c'", "key = 'a' or key = 'b' and key = 'c'")]
    [InlineData("(key='a' and key='b') or key='c'", "key = 'a' and key = 'b' or key = 'c'")]
    [InlineData("not (key='a' and key='b')", "not (key = 'a' and key = 'b')")]
    [InlineData("not not key is null", "not not key is null")]
    [InlineData("((key = 'a'))", "key = 'a'")]
    [InlineData("key = 'it''s'", "key = 'it''s'")]
    [InlineData("key = \"say \"\"hi\"\"\"", "key = 'say \"hi\"'")]
    [InlineData("key = 1.50", "key = 1.50")]
    [InlineData("key = TRUE", "key = true")]
    [InlineData("ANY ~ 'x'", "any ~ 'x'")]
    [InlineData("CE.type IS NOT NULL", "ce.type is not null")]
    [InlineData("", "")]
    public void The_canonical_text_is_written_the_same_way_whatever_was_typed(string typed, string canonical)
    {
        PredicateFormatter.Format(Parse(typed)).Should().Be(canonical);
    }

    [Theory]
    [InlineData("'")]
    [InlineData("\\")]
    [InlineData("")]
    [InlineData("é")]
    [InlineData("😀")]
    [InlineData("it's \"quoted\"")]
    public void QuoteText_round_trips_through_the_parser(string value)
    {
        string quoted = PredicateFormatter.QuoteText(value);

        PredicateParseResult parsed = PredicateParser.Parse("key = " + quoted);

        parsed.Errors.Should().BeEmpty();
        PredicateFormatter.Format(parsed.Tree).Should().Be("key = " + quoted);
        ((StringLiteral) ((ClauseNode) parsed.Tree!).Literals.Single()).Value.Should().Be(value);
    }

    [Fact]
    public void Parsing_the_canonical_text_gives_the_same_tree_for_a_corpus()
    {
        string[] corpus =
        [
            "key = 'a'",
            "key = 'a' and key = 'b' or key = 'c'",
            "(key = 'a' or key = 'b') and (key = 'c' or key = 'd')",
            "not (key = 'a' or header.x ^= 'b') and ce.type ~ 'order'",
            "((key = 'a' or key = 'b') or key = 'c') or key = 'd'",
            "((key = 'a' and key = 'b') and key = 'c') and key = 'd'",
            "header['weird name'] in ('x', 'y''z') and any ~ 'q'",
            "key is not null and not header.a is null",
            "NOT NOT (NOT key = 'a')",
            "key in ('a') or ce.subject ^= 'orders/'",
        ];

        foreach (string text in corpus)
        {
            PredicateNode? first = Parse(text);
            string canonical = PredicateFormatter.Format(first);
            PredicateNode? second = Parse(canonical);

            PredicateTree.StructurallyEqual(first, second).Should().BeTrue($"'{text}' -> '{canonical}'");
            PredicateFormatter.Format(second).Should().Be(canonical, "the canonical text is a fixed point");
        }
    }

    [Fact]
    public void Parsing_the_canonical_text_gives_the_same_tree_for_seeded_random_texts()
    {
        // No package: a small generator over the grammar, seeded so that a failure names the text that reproduces it.
        for (int seed = 0; seed < 300; seed++)
        {
            Random random = new(seed);
            string text = Generate(random, depth: 0);

            PredicateParseResult first = PredicateParser.Parse(text);

            if (!first.IsValid)
            {
                // The generator can exceed a limit (clauses, nesting); such a text has no tree to round-trip.
                continue;
            }

            string canonical = PredicateFormatter.Format(first.Tree);
            PredicateNode? second = Parse(canonical);

            PredicateTree.StructurallyEqual(first.Tree, second).Should().BeTrue($"seed {seed}: '{text}' -> '{canonical}'");
        }
    }

    [Fact]
    public void A_literal_on_a_redacted_field_is_written_as_the_placeholder_in_the_text_for_a_sink()
    {
        PredicateNode? tree = Parse("header.authorization = 'Bearer s3cr3t' and key in ('a', 'b') and header.x-tenant ^= 'acme'");

        string audit = PredicateFormatter.Format(
            tree,
            static clause => clause.Field is HeaderField { Name: "authorization" } || clause.Field is KeyField);

        audit.Should().Be("header.authorization = <redacted> and key in (<redacted>, <redacted>) and header.x-tenant ^= 'acme'");
        audit.Should().NotContain("s3cr3t");
    }

    [Fact]
    public void The_text_for_a_link_cannot_be_parsed_back_until_the_person_types_the_value_in()
    {
        PredicateNode? tree = Parse("header.authorization = 'x'");
        string link = PredicateFormatter.Format(tree, static _ => true);

        PredicateParseResult back = PredicateParser.Parse(link);

        back.IsValid.Should().BeFalse();
        back.Errors[0].Message.Should().Be("The link left out a redacted value at position 24: type it in.");
    }

    private static readonly string[] Fields = ["key", "header.a", "header.x-request-id", "header['a b']", "ce.type", "ce.subject", "any"];
    private static readonly string[] Operators = ["=", "^=", "~"];
    private static readonly string[] Values = ["'a'", "'it''s'", "\"q\"", "'x y'", "'1'", "''"];

    private static string Generate(Random random, int depth)
    {
        int choice = random.Next(depth >= 3 ? 1 : 6);

        switch (choice)
        {
            case 1:
                return $"({Generate(random, depth + 1)} and {Generate(random, depth + 1)})";
            case 2:
                return $"({Generate(random, depth + 1)} or {Generate(random, depth + 1)})";
            case 3:
                return $"not {Generate(random, depth + 1)}";
            case 4:
                return $"{Generate(random, depth + 1)} and {Generate(random, depth + 1)} or {Generate(random, depth + 1)}";
            case 5:
                return Generate(random, depth + 1);
            default:
                string field = Fields[random.Next(Fields.Length)];

                if (random.Next(5) == 0)
                {
                    return $"{field} is {(random.Next(2) == 0 ? "not " : string.Empty)}null";
                }

                if (random.Next(4) == 0)
                {
                    StringBuilder list = new();
                    int count = random.Next(1, 4);

                    for (int i = 0; i < count; i++)
                    {
                        list.Append(i > 0 ? ", " : string.Empty).Append(Values[random.Next(Values.Length)]);
                    }

                    return $"{field} in ({list})";
                }

                return $"{field} {Operators[random.Next(Operators.Length)]} {Values[random.Next(Values.Length)]}";
        }
    }
}
