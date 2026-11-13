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
        return result.Errors[0].Shown;
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
    [InlineData("key =", "Position 6: expected a value after '=', found the end.")]
    [InlineData("key 'a'", "Position 5: expected an operator ('=', '^=', '~', '<', '<=', '>', '>=', 'in' or 'is'), found a string.")]
    [InlineData("foo = 'a'", "Position 1: unknown field 'foo'. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("key = 'a' and foo = 'b'", "Position 15: unknown field 'foo'. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("ce.Type = 'a'", "Position 1: unknown field 'ce.Type'. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("header = 'a'", "Position 1: unknown field 'header'. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("key = 'abc", "Position 7: this string has no closing quote.")]
    [InlineData("(key = 'a'", "Position 1: this '(' has no matching ')'.")]
    [InlineData("key = 'a' and", "Position 14: expected a field (key, header.<name>, ce.<attribute>, any, value or a $.json.path), found the end.")]
    [InlineData("key = 'a' )", "Position 11: expected 'and', 'or' or the end, found ')'.")]
    [InlineData("key in 'a'", "Position 8: expected '(', found a string.")]
    [InlineData("key in ('a' 'b')", "Position 13: expected ',' or ')', found a string.")]
    [InlineData("key is 'a'", "Position 8: expected 'null', found a string.")]
    [InlineData("and key = 'a'", "Position 1: expected a field (key, header.<name>, ce.<attribute>, any, value or a $.json.path), found 'and'.")]
    [InlineData("key = <redacted>", "Position 7: the link left out a redacted value here: type it in.")]
    [InlineData("key in ('a', <redacted>)", "Position 14: the link left out a redacted value here: type it in.")]
    [InlineData("key = 'a' & key = 'b'", "Position 11: unexpected character.")]
    [InlineData("key = 99999999999999999999999999999", "Position 7: this number is out of range.")]
    [InlineData("header[''] = 'a'", "Position 1: the header name is empty.")]
    [InlineData("header[a] = 'a'", "Position 8: expected a string, found a name.")]
    public void Every_message_names_its_position_and_says_what_was_expected(string text, string message)
    {
        Error(text).Should().Be(message);
    }

    [Theory]
    [InlineData("key == 5 &&", "Position 10: unexpected character.")]
    [InlineData("value ~", "Position 8: expected a value after '~', found the end.")]
    [InlineData("foo = 'x'", "Position 1: unknown field 'foo'. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("key ==", "Position 6: expected a value after '=', found '='.")]
    [InlineData("key in (", "Position 9: expected a value after '(', found the end.")]
    [InlineData("key in ('a',", "Position 13: expected a value after ',', found the end.")]
    public void The_four_inputs_of_issue_75_give_one_position_each_in_one_shape(string text, string message)
    {
        string shown = Error(text);

        shown.Should().Be(message);
        shown.Split("osition", StringSplitOptions.None).Should().HaveCount(2, "the position is said once, by the page's prefix");
    }

    [Fact]
    public void Every_error_of_every_input_above_has_the_one_shape_and_a_constant_audit_phrase()
    {
        string[] texts =
        [
            "key =", "key 'a'", "foo = 'a'", "key = 'abc", "(key = 'a'", "key = 'a' and", "key = 'a' )", "key in 'a'", "key is 'a'", "key = <redacted>",
            "key = 'a' & key = 'b'", "key = 99999999999999999999999999999", "header[''] = 'a'", "header[a] = 'a'", "value ~", "key == 5 &&", "$.a. = 1",
            "key = '" + new string('a', 1_001) + "'",
        ];

        foreach (string text in texts)
        {
            PredicateError error = PredicateParser.Parse(text).Errors.Should().ContainSingle(text).Which;

            error.Shown.Should().MatchRegex(@"^Position \d+: [a-z]", "'" + text + "' is drawn as 'Position n: message', lower case after the colon");
            error.Message.Should().NotContainEquivalentOf("position", "'" + text + "'");
            error.Message.Should().NotMatchRegex("(?i)unexpected the", "'" + text + "' reads as grammar, not as a stack trace");
            error.AuditPhrase.Should().NotBeNullOrWhiteSpace().And.NotContain("'").And.NotContain("$", "a phrase is a constant: nothing the visitor typed");
        }
    }

    [Theory]
    [InlineData("value ~", "expected a value")]
    [InlineData("key = 'abc", "unclosed string")]
    [InlineData("foo = 'a'", "unknown field")]
    [InlineData("key = 'a' & key = 'b'", "unexpected character")]
    [InlineData("key 'a'", "unexpected token")]
    public void The_audit_phrase_is_a_constant_of_the_kind_of_error(string text, string phrase)
    {
        PredicateParser.Parse(text).Errors[0].AuditPhrase.Should().Be(phrase);
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

        Error(name + " = 'a'").Should().Contain($"Position 1: unknown field '{new string('f', 64)}'.").And.NotContain(new string('f', 65));
    }

    [Fact]
    public void The_text_limit_is_2000_characters()
    {
        string ok = "key = '" + new string('a', 990) + "'";
        PredicateParser.Parse(ok).IsValid.Should().BeTrue();

        string tooLong = "key = 'a'" + new string(' ', 2_000);
        Error(tooLong).Should().Be("Position 1: the search text is 2,009 characters long; the most is 2,000.");
    }

    [Fact]
    public void The_clause_limit_is_32()
    {
        string thirtyTwo = string.Join(" and ", Enumerable.Range(0, 32).Select(static i => $"key = '{i}'"));
        PredicateParser.Parse(thirtyTwo).IsValid.Should().BeTrue();

        string thirtyThree = string.Join(" and ", Enumerable.Range(0, 33).Select(static i => $"key = '{i}'"));
        Error(thirtyThree).Should().Be("Position 1: the query has 33 conditions; the most is 32.");
    }

    [Fact]
    public void The_in_list_limit_is_100_values()
    {
        string hundred = "key in (" + string.Join(", ", Enumerable.Range(0, 100).Select(static i => $"'{i}'")) + ")";
        PredicateParser.Parse(hundred).IsValid.Should().BeTrue();

        string more = "key in (" + string.Join(", ", Enumerable.Range(0, 101).Select(static i => $"'{i}'")) + ")";
        Error(more).Should().Be("Position 9: the list has 101 values; the most is 100.");
    }

    [Fact]
    public void The_nesting_limit_is_16_levels_of_parentheses_and_not()
    {
        string sixteen = new string('(', 16) + "key = 'a'" + new string(')', 16);
        PredicateParser.Parse(sixteen).IsValid.Should().BeTrue();

        string seventeen = new string('(', 17) + "key = 'a'" + new string(')', 17);
        Error(seventeen).Should().Be("Position 17: the query nests more than 16 levels.");

        string nots = string.Concat(Enumerable.Repeat("not ", 17)) + "key = 'a'";
        Error(nots).Should().Contain("nests more than 16 levels");
    }

    [Fact]
    public void The_literal_limit_is_1000_characters()
    {
        PredicateParser.Parse("key = '" + new string('a', 1_000) + "'").IsValid.Should().BeTrue();

        Error("key = '" + new string('a', 1_001) + "'").Should().Be("Position 7: this value is 1,001 characters long; the most is 1,000.");
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
