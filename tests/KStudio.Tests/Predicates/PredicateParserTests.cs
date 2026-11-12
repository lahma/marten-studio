using KStudio.Core.Predicates;

namespace KStudio.Tests.Predicates;

/// <summary>
/// The search grammar of decision 7.1: every rule, the precedence, every message with its position, and every limit. A message never
/// contains a literal - an error is a sink like any other (D40) - and a test below proves it over a corpus of broken texts.
/// </summary>
public class PredicateParserTests
{
    private static PredicateNode Tree(string text)
    {
        PredicateParseResult result = PredicateParser.Parse(text);
        result.Errors.Should().BeEmpty("'" + text + "' is valid");
        return result.Tree!;
    }

    private static string Error(string text)
    {
        PredicateParseResult result = PredicateParser.Parse(text);
        result.Errors.Should().ContainSingle("'" + text + "' has exactly one error (the parser stops at the first)");
        result.Tree.Should().BeNull();
        return result.Errors[0].Message;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void An_empty_query_is_valid_and_matches_every_record(string text)
    {
        PredicateParseResult result = PredicateParser.Parse(text);

        result.IsValid.Should().BeTrue();
        result.Tree.Should().BeNull();
        PredicateParser.Parse(null).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Keywords_are_case_insensitive_and_names_are_not()
    {
        PredicateNode tree = Tree("KEY = 'a' AND NOT Header.X-Id IS NOT NULL OR Any ~ 'z'");

        PredicateFormatter.Format(tree).Should().Be("key = 'a' and not header.X-Id is not null or any ~ 'z'");
    }

    [Fact]
    public void Precedence_is_not_over_and_over_or_and_a_chain_is_one_node()
    {
        PredicateNode tree = Tree("key = 'a' or key = 'b' and not key = 'c' and key = 'd'");

        OrNode or = tree.Should().BeOfType<OrNode>().Subject;
        or.Children.Should().HaveCount(2);
        or.Children[0].Should().BeOfType<ClauseNode>();
        AndNode and = or.Children[1].Should().BeOfType<AndNode>().Subject;
        and.Children.Should().HaveCount(3, "key = 'b' and not ... and key = 'd' is one node, not a nest");
        and.Children[1].Should().BeOfType<NotNode>();
    }

    [Fact]
    public void Parentheses_override_precedence_and_a_parenthesised_chain_of_the_same_operator_is_spliced()
    {
        PredicateNode grouped = Tree("(key = 'a' or key = 'b') and key = 'c'");

        grouped.Should().BeOfType<AndNode>().Subject.Children[0].Should().BeOfType<OrNode>();

        PredicateNode spliced = Tree("(key = 'a' or key = 'b') or key = 'c'");

        spliced.Should().BeOfType<OrNode>().Subject.Children.Should().HaveCount(3);
    }

    [Fact]
    public void Every_field_operator_and_literal_of_the_grammar_parses()
    {
        ClauseNode clause(string text) => Tree(text).Should().BeOfType<ClauseNode>().Subject;

        clause("key = 'a'").Field.Should().BeOfType<KeyField>();
        clause("key ^= 'a'").Operator.Should().Be(PredicateOperator.Prefix);
        clause("key ~ 'a'").Operator.Should().Be(PredicateOperator.Contains);
        clause("key in ('a', 'b', 'c')").Literals.Should().HaveCount(3);
        clause("key is null").Operator.Should().Be(PredicateOperator.IsNull);
        clause("key is not null").Operator.Should().Be(PredicateOperator.IsNotNull);
        clause("header.x-request-id = 'a'").Field.Should().BeOfType<HeaderField>().Which.Name.Should().Be("x-request-id");
        clause("header['a b'] = 'a'").Field.Should().BeOfType<HeaderField>().Which.Name.Should().Be("a b");
        clause("header.a.b_c-d = 'a'").Field.Should().BeOfType<HeaderField>().Which.Name.Should().Be("a.b_c-d");
        clause("ce.type = 'a'").Field.Should().BeOfType<CloudEventField>().Which.Attribute.Should().Be("type");
        clause("ce.time2 = 'a'").Field.Should().BeOfType<CloudEventField>();
        clause("any = 'a'").Field.Should().BeOfType<AnyField>();
        clause("value ~ 'a'").Field.Should().BeOfType<ValueField>("M3 fields are parsed now and refused by the compiler, so the grammar does not change");
        clause("$.a.b[0] = 'a'").Field.Should().BeOfType<JsonPathField>();
        clause("key < 'a'").Operator.Should().Be(PredicateOperator.Less);
        clause("key <= 'a'").Operator.Should().Be(PredicateOperator.LessOrEqual);
        clause("key > 'a'").Operator.Should().Be(PredicateOperator.Greater);
        clause("key >= 'a'").Operator.Should().Be(PredicateOperator.GreaterOrEqual);
        clause("key = 'it''s'").Literals[0].Should().BeOfType<StringLiteral>().Which.Value.Should().Be("it's");
        clause("key = \"say \"\"hi\"\"\"").Literals[0].Should().BeOfType<StringLiteral>().Which.Value.Should().Be("say \"hi\"");
        clause("key = -12.5e+3").Literals[0].Should().BeOfType<NumberLiteral>().Which.Value.Should().Be(-12500m);
        clause("key = null").Literals[0].Should().BeOfType<NullLiteral>();
        clause("key = TRUE").Literals[0].Should().BeOfType<BoolLiteral>().Which.Value.Should().BeTrue();
        clause("key = false").Literals[0].Should().BeOfType<BoolLiteral>().Which.Value.Should().BeFalse();
        clause("key = 'a\\nb'").Literals[0].Should().BeOfType<StringLiteral>().Which.Value.Should().Be("a\\nb", "there are no backslash escapes");
    }

    [Theory]
    [InlineData("key =", "Unexpected the end at position 6: expected a value.")]
    [InlineData("key 'a'", "Unexpected a string at position 5: expected an operator ('=', '^=', '~', '<', '<=', '>', '>=', 'in' or 'is').")]
    [InlineData("foo = 'a'", "Unknown field 'foo' at position 1. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("key = 'a' and foo = 'b'", "Unknown field 'foo' at position 15. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("ce.Type = 'a'", "Unknown field 'ce.Type' at position 1. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("header = 'a'", "Unknown field 'header' at position 1. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("key = 'abc", "The string starting at position 7 has no closing quote.")]
    [InlineData("(key = 'a'", "The '(' at position 1 has no matching ')'.")]
    [InlineData("key = 'a' and", "Unexpected the end at position 14: expected a field (key, header.<name>, ce.<attribute>, any, value or a $.json.path).")]
    [InlineData("key = 'a' )", "Unexpected ')' at position 11: expected 'and', 'or' or the end.")]
    [InlineData("key in 'a'", "Unexpected a string at position 8: expected '('.")]
    [InlineData("key in ('a' 'b')", "Unexpected a string at position 13: expected ',' or ')'.")]
    [InlineData("key is 'a'", "Unexpected a string at position 8: expected 'null'.")]
    [InlineData("and key = 'a'", "Unexpected 'and' at position 1: expected a field (key, header.<name>, ce.<attribute>, any, value or a $.json.path).")]
    [InlineData("key = <redacted>", "The link left out a redacted value at position 7: type it in.")]
    [InlineData("key in ('a', <redacted>)", "The link left out a redacted value at position 14: type it in.")]
    [InlineData("key = 'a' & key = 'b'", "Unexpected character at position 11.")]
    [InlineData("key = 99999999999999999999999999999", "The number at position 7 is out of range.")]
    [InlineData("header[''] = 'a'", "The header name at position 1 is empty.")]
    [InlineData("header[a] = 'a'", "Unexpected a name at position 8: expected a string.")]
    public void Every_message_names_its_position_and_says_what_was_expected(string text, string message)
    {
        Error(text).Should().Be(message);
    }

    [Fact]
    public void No_message_contains_a_literal()
    {
        string[] texts =
        [
            "key = 's3cr3t",
            "key 's3cr3t'",
            "(key = 's3cr3t'",
            "key = 's3cr3t' )",
            "key in ('s3cr3t' 's3cr3t')",
            "key is 's3cr3t'",
            "key = 's3cr3t' & key = 's3cr3t'",
            "key = 's3cr3t' and bogus = 's3cr3t'",
            "key in 's3cr3t'",
            "key = 99999999999999999999999999999 'S3CR3T'",
            "key = " + new string('x', 5) + "'s3cr3t'",
        ];

        foreach (string text in texts)
        {
            PredicateParseResult result = PredicateParser.Parse(text);

            if (!result.IsValid)
            {
                result.Errors[0].Message.Should().NotContainEquivalentOf("s3cr3t", "'" + text + "'");
            }
        }
    }

    [Fact]
    public void An_unknown_field_name_in_the_message_is_cut_at_64_characters()
    {
        string name = new('f', 100);

        Error(name + " = 'a'").Should().Contain($"Unknown field '{new string('f', 64)}' at position 1.").And.NotContain(new string('f', 65));
    }

    [Fact]
    public void The_text_limit_is_2000_characters()
    {
        string ok = "key = '" + new string('a', 990) + "'";
        PredicateParser.Parse(ok).IsValid.Should().BeTrue();

        string tooLong = "key = 'a'" + new string(' ', 2_000);
        Error(tooLong).Should().Be("The query is 2,009 characters long; the most is 2,000.");
    }

    [Fact]
    public void The_clause_limit_is_32()
    {
        string thirtyTwo = string.Join(" and ", Enumerable.Range(0, 32).Select(static i => $"key = '{i}'"));
        PredicateParser.Parse(thirtyTwo).IsValid.Should().BeTrue();

        string thirtyThree = string.Join(" and ", Enumerable.Range(0, 33).Select(static i => $"key = '{i}'"));
        Error(thirtyThree).Should().Be("The query has 33 conditions; the most is 32.");
    }

    [Fact]
    public void The_in_list_limit_is_100_values()
    {
        string hundred = "key in (" + string.Join(", ", Enumerable.Range(0, 100).Select(static i => $"'{i}'")) + ")";
        PredicateParser.Parse(hundred).IsValid.Should().BeTrue();

        string more = "key in (" + string.Join(", ", Enumerable.Range(0, 101).Select(static i => $"'{i}'")) + ")";
        Error(more).Should().Be("The list at position 9 has 101 values; the most is 100.");
    }

    [Fact]
    public void The_nesting_limit_is_16_levels_of_parentheses_and_not()
    {
        string sixteen = new string('(', 16) + "key = 'a'" + new string(')', 16);
        PredicateParser.Parse(sixteen).IsValid.Should().BeTrue();

        string seventeen = new string('(', 17) + "key = 'a'" + new string(')', 17);
        Error(seventeen).Should().Be("The query nests more than 16 levels at position 17.");

        string nots = string.Concat(Enumerable.Repeat("not ", 17)) + "key = 'a'";
        Error(nots).Should().Contain("nests more than 16 levels");
    }

    [Fact]
    public void The_literal_limit_is_1000_characters()
    {
        PredicateParser.Parse("key = '" + new string('a', 1_000) + "'").IsValid.Should().BeTrue();

        Error("key = '" + new string('a', 1_001) + "'").Should().Be("The value at position 7 is 1,001 characters long; the most is 1,000.");
    }

    [Fact]
    public void Positions_in_the_tree_are_counted_from_zero_and_cover_the_clause()
    {
        PredicateNode tree = Tree("key = 'a' and header.x ^= 'b'");

        AndNode and = tree.Should().BeOfType<AndNode>().Subject;
        and.Start.Should().Be(0);
        and.Length.Should().Be(29);
        and.Children[1].Start.Should().Be(14);
    }
}
