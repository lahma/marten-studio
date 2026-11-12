using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Redaction;

namespace KStudio.Tests.Redaction;

/// <summary>
/// The payload rules a host declares (decision 3.1 of D-PAYLOAD): <c>JsonPath</c> and <c>Pattern</c> on the public builder, the names a rule
/// is shown by, and every refusal of the options validation word for word.
/// </summary>
public class PayloadRuleValidationTests
{
    private static IReadOnlyList<string> Validate(Action<KafkaRedactionBuilder> rules)
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", new ClientConfig { BootstrapServers = "broker:9092" });
        options.Topic("orders.v1", topic => topic.Redact(rules));
        return KafkaEngineOptionsValidation.Validate(options);
    }

    [Fact]
    public void The_builder_declares_a_json_path_rule_named_by_its_canonical_path_and_a_pattern_named_by_its_name_or_its_place()
    {
        KafkaTopicBuilder topic = new();

        topic.Redact(static r =>
        {
            r.JsonPath("$['customer'].Email").Searchable();
            r.JsonPath("$.items[*].x");
            r.Pattern("aa", "first");
            r.Pattern("bb");
            r.Pattern("cc", "third");
            r.Pattern("dd");
        });

        topic.Rules!.Declared.Select(static x => x.Name).Should().Equal(
            "$.customer.Email", "$.items[*].x", "pattern first", "pattern 2", "pattern third", "pattern 4");
        topic.Rules.Declared[0].Searchable.Should().BeTrue();
        topic.Rules.Declared[1].Searchable.Should().BeFalse();
    }

    [Fact]
    public void The_redactor_keeps_the_payload_rules_in_order_with_their_paths_and_compiled_patterns()
    {
        KafkaTopicBuilder topic = new();
        topic.Redact(static r =>
        {
            r.Header("x-email");
            r.JsonPath("$.a.b").Searchable();
            r.Pattern("zz", "z");
        });

        KafkaRedactor redactor = new(topic.Rules);

        redactor.HasPayloadRules.Should().BeTrue();
        redactor.PayloadRules.Select(static x => x.Name).Should().Equal("$.a.b", "pattern z");
        redactor.PayloadRules[0].Path!.Text.Should().Be("$.a.b");
        redactor.PayloadRules[0].Searchable.Should().BeTrue();
        redactor.PayloadRules[1].Pattern!.IsMatch("xxzzxx").Should().BeTrue();
        redactor.PayloadRules[1].Searchable.Should().BeFalse("a pattern is never searchable");
        KafkaRedactor.DenyListOnly.HasPayloadRules.Should().BeFalse();
        new KafkaRedactor(new KafkaTopicBuilder().Redact(static r => r.Header("h")).Rules).HasPayloadRules.Should().BeFalse();
    }

    [Fact]
    public void The_snapshot_shares_the_compiled_pattern_and_a_later_declaration_does_not_reach_it()
    {
        KafkaTopicBuilder topic = new();
        topic.Redact(static r => r.Pattern("aa", "first"));

        KafkaTopicBuilder snapshot = topic.Snapshot();
        topic.Redact(static r => r.Pattern("bb", "later"));

        snapshot.Rules!.Declared.Should().ContainSingle();
        new KafkaRedactor(snapshot.Rules).PayloadRules[0].Pattern.Should().BeSameAs(new KafkaRedactor(topic.Rules).PayloadRules[0].Pattern);
    }

    [Fact]
    public void Valid_payload_rules_are_accepted()
    {
        Validate(static r =>
        {
            r.JsonPath("$.customer.email").Searchable();
            r.JsonPath("$.items[*].secret");
            r.JsonPath("$['a-b']");
            r.Pattern(@"\b[0-9]{16}\b", "card");
            r.Pattern(@"[A-Z]{2}[0-9]{6}", "passport-nr_2");
            r.Pattern("abc");
        }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("$.a.", "$.a.")]
    [InlineData("a.b", "a.b")]
    [InlineData("$..a", "$..a")]
    [InlineData("$.a[x]", "$.a[x]")]
    public void A_path_the_search_cannot_read_is_refused_with_the_path_shown(string path, string shown)
    {
        Validate(r => r.JsonPath(path)).Should().ContainSingle().Which.Should().Be(
            $"KStudio: topic 'orders.v1': the redaction path '{shown}' is not a JSON path the search can read ($.name, $.name[0], $.name[*]).");
    }

    [Fact]
    public void A_path_declared_twice_ignoring_case_is_refused()
    {
        Validate(static r =>
        {
            r.JsonPath("$.Customer.email");
            r.JsonPath("$.customer.EMAIL");
        }).Should().ContainSingle().Which.Should().Be(
            "KStudio: topic 'orders.v1' declares a redaction rule on '$.customer.EMAIL' twice (member names are compared ignoring case).");

        Validate(static r =>
        {
            r.JsonPath("$.a['b']");
            r.JsonPath("$.a.b");
        }).Should().ContainSingle("two spellings of one path are one path");
    }

    [Fact]
    public void More_than_32_paths_or_8_patterns_are_refused()
    {
        Validate(static r =>
        {
            for (int i = 0; i < 33; i++)
            {
                r.JsonPath("$.f" + i);
            }
        }).Should().ContainSingle().Which.Should().Be("KStudio: topic 'orders.v1' declares 33 JSON-path redaction rules; the most is 32.");

        Validate(static r =>
        {
            for (int i = 0; i < 9; i++)
            {
                r.Pattern("p" + i);
            }
        }).Should().ContainSingle().Which.Should().Be("KStudio: topic 'orders.v1' declares 9 redaction patterns; the most is 8.");

        Validate(static r =>
        {
            for (int i = 0; i < 32; i++)
            {
                r.JsonPath("$.f" + i);
            }

            for (int i = 0; i < 8; i++)
            {
                r.Pattern("p" + i);
            }
        }).Should().BeEmpty("32 and 8 are the limits, not over them");
    }

    [Fact]
    public void A_pattern_is_checked_for_length_syntax_backtracking_and_emptiness()
    {
        Validate(static r => r.Pattern(new string('a', 501), "long")).Should().ContainSingle().Which.Should().Be(
            "KStudio: topic 'orders.v1': pattern long is 501 characters long; the most is 500.");
        Validate(static r => r.Pattern(new string('a', 500), "limit")).Should().BeEmpty();

        Validate(static r => r.Pattern("(unclosed", "bad")).Should().ContainSingle().Which.Should().Be(
            "KStudio: topic 'orders.v1': pattern bad is not a regular expression .NET can read.");

        Validate(static r => r.Pattern(@"(a)\1", "backref")).Should().ContainSingle().Which.Should().Be(
            "KStudio: topic 'orders.v1': pattern backref uses a construct that cannot run without backtracking (backreferences, lookarounds, atomic groups and conditionals are refused).");
        Validate(static r => r.Pattern("(?=a)b", "look")).Should().ContainSingle().Which.Should().Contain("pattern look uses a construct that cannot run without backtracking");
        Validate(static r => r.Pattern("(?<=a)b", "behind")).Should().ContainSingle().Which.Should().Contain("pattern behind uses a construct that cannot run without backtracking");
        Validate(static r => r.Pattern("(?>a+)b", "atomic")).Should().ContainSingle().Which.Should().Contain("pattern atomic uses a construct that cannot run without backtracking");

        Validate(static r => r.Pattern("a*", "empty")).Should().ContainSingle().Which.Should().Be(
            "KStudio: topic 'orders.v1': pattern empty matches an empty text, so it would mask nothing and run on every value.");
        Validate(static r => r.Pattern("^$", "anchors")).Should().ContainSingle().Which.Should().Contain("pattern anchors matches an empty text");
        Validate(static r => r.Pattern("(a|)", "alt")).Should().ContainSingle().Which.Should().Contain("pattern alt matches an empty text");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("dot.name")]
    [InlineData("é")]
    public void A_pattern_name_is_one_to_64_letters_digits_hyphens_and_underscores(string name)
    {
        Validate(r => r.Pattern("aa", name)).Should().ContainSingle().Which.Should().Be(
            "KStudio: topic 'orders.v1': a pattern name is 1 to 64 letters, digits, '-' or '_'.");
    }

    [Fact]
    public void A_pattern_name_of_64_characters_is_accepted_and_one_of_65_is_not()
    {
        Validate(static r => r.Pattern("aa", new string('n', 64))).Should().BeEmpty();
        Validate(static r => r.Pattern("aa", new string('n', 65))).Should().ContainSingle();
    }

    [Fact]
    public void A_null_path_pattern_or_blank_one_is_refused_at_the_call()
    {
        KafkaTopicBuilder topic = new();

        Action noPath = () => topic.Redact(static r => r.JsonPath(" "));
        Action noPattern = () => topic.Redact(static r => r.Pattern(string.Empty));

        noPath.Should().Throw<ArgumentException>();
        noPattern.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Patterns_compile_once_for_the_process_and_a_failed_one_is_not_cached()
    {
        PayloadPatterns.TryCompile("shared-pattern-xyz", out System.Text.RegularExpressions.Regex? first).Should().Be(PayloadPatternProblem.None);
        PayloadPatterns.TryCompile("shared-pattern-xyz", out System.Text.RegularExpressions.Regex? second).Should().Be(PayloadPatternProblem.None);

        second.Should().BeSameAs(first);
        PayloadPatterns.TryCompile("(unclosed", out System.Text.RegularExpressions.Regex? none).Should().Be(PayloadPatternProblem.NotARegularExpression);
        none.Should().BeNull();
    }
}
