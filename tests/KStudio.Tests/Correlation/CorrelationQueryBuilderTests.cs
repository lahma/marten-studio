using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;

namespace KStudio.Tests.Correlation;

/// <summary>The search one topic of a related-records scan runs (D-CORR 1.5): which fields, how a clause reads, what is left out and why, and that every text parses and compiles.</summary>
public class CorrelationQueryBuilderTests
{
    private static CorrelationKeySet Keys(params (string Kind, string Value)[] values) =>
        new([.. values.Select(static x => new CorrelationValue(x.Kind, x.Value, CorrelationValueForm.Text, "test"))], []);

    private static CorrelationTopicQuery Build(CorrelationKeySet keys, KafkaTopicBuilder? topic = null, bool payload = false, string name = "orders", string? seedStore = null)
    {
        KafkaTopicBuilder builder = topic ?? CorrelationRig.Declare();
        KafkaTopicDescriptor descriptor = new(name, builder);
        return CorrelationQueryBuilder.Build(keys, descriptor, new KafkaRedactor(builder.Rules), payload, seedStore);
    }

    [Fact]
    public void A_seed_searches_only_the_fields_of_its_store_and_the_fields_that_name_none()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c =>
        {
            c.Key().Document("Order", "a");
            c.Header("x-b").Document("Order", "b");
            c.Header("x-any").Document("Order");
        }));
        CorrelationKeySet seed = CorrelationKeySet.Seed("document:Order", "o-1");

        Build(seed, builder, seedStore: "a").PredicateText.Should().Be("key = 'o-1' or header.x-any = 'o-1'", "store b's field is another store's");
        Build(seed, builder, seedStore: "A").PredicateText.Should().Be("key = 'o-1' or header.x-any = 'o-1'", "a store is compared ignoring case");
        Build(seed, builder, seedStore: "b").PredicateText.Should().Be("header.x-b = 'o-1' or header.x-any = 'o-1'");
        Build(seed, builder).PredicateText.Should().Be("key = 'o-1' or header.x-b = 'o-1' or header.x-any = 'o-1'", "a link that names no store searches every field of the kind");
    }

    [Fact]
    public void A_seed_for_a_store_no_field_names_gets_no_query_on_that_topic()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.Key().Document("Order", "a")));

        Build(CorrelationKeySet.Seed("document:Order", "o-1"), builder, seedStore: "b").HasQuery.Should().BeFalse();
    }

    /// <summary>Compiles the text on the topic, as the scan engine does.</summary>
    private static void ShouldCompile(CorrelationTopicQuery query, KafkaTopicBuilder builder, KafkaReadGrant grant, string name = "orders")
    {
        query.PredicateText.Should().NotBeNull();
        PredicateParseResult parsed = PredicateParser.Parse(query.PredicateText);
        parsed.IsValid.Should().BeTrue(string.Join("; ", parsed.Errors.Select(static x => x.Message)));
        KafkaTopicDescriptor descriptor = new(name, builder);
        PredicateCompilation compiled = PredicateCompiler.Compile(
            parsed.Tree,
            new PredicateCompileContext(name, descriptor.KeyFormat, descriptor.KeyFormatDeclared, new KafkaRedactor(builder.Rules), grant, false));
        compiled.Errors.Should().BeEmpty(string.Join("; ", compiled.Errors.Select(static x => x.Message)));
    }

    [Fact]
    public void One_value_gives_equals_and_several_give_in()
    {
        CorrelationTopicQuery one = Build(Keys(("correlation", "c-1")));
        one.PredicateText.Should().Be("header.correlation-id = 'c-1'");

        CorrelationTopicQuery several = Build(Keys(("message", "m-1"), ("message", "m-2")));
        several.PredicateText.Should().Be("header.id in ('m-1', 'm-2') or header.conversation-id in ('m-1', 'm-2') or header.causation-id in ('m-1', 'm-2') or ce.id in ('m-1', 'm-2')");
    }

    [Fact]
    public void The_fields_are_the_topics_own_and_a_kind_with_no_value_is_not_searched()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.Header("x-order").As("order")));

        Build(Keys(("order", "o-1")), builder).PredicateText.Should().Be("header.x-order = 'o-1'");
        Build(Keys(("order", "o-1")), CorrelationRig.Declare()).HasQuery.Should().BeFalse("a topic without a field of the kind has nothing to search");
    }

    [Fact]
    public void A_trace_is_a_prefix_search_on_each_of_the_three_headers()
    {
        CorrelationTopicQuery query = Build(Keys(("trace", "4bf92f3577b34da6a3ce929d0e0e4736")));

        query.PredicateText.Should().Be(
            "header.traceparent ^= '00-4bf92f3577b34da6a3ce929d0e0e4736' or header.ce_traceparent ^= '00-4bf92f3577b34da6a3ce929d0e0e4736' or header.parent-id ^= '00-4bf92f3577b34da6a3ce929d0e0e4736'");
        ShouldCompile(query, CorrelationRig.Declare(), KafkaReadGrant.Envelope);
    }

    [Fact]
    public void A_trace_header_that_is_redacted_is_left_out_even_when_searchable()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("traceparent").Searchable()));
        CorrelationTopicQuery query = Build(Keys(("trace", "4bf92f3577b34da6a3ce929d0e0e4736")), builder);

        query.PredicateText.Should().NotContain("header.traceparent");
        query.PredicateText.Should().Contain("header.ce_traceparent");
        query.Notes.Should().Contain("`header.traceparent` is redacted on this topic and is not searched.");
        ShouldCompile(query, builder, KafkaReadGrant.Envelope);
    }

    [Fact]
    public void A_field_redacted_without_searchable_is_left_out_with_its_reason_and_the_rest_still_runs()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("correlation-id")));
        CorrelationTopicQuery query = Build(Keys(("correlation", "c-1"), ("message", "m-1")), builder);

        query.PredicateText.Should().NotContain("correlation-id");
        query.PredicateText.Should().Contain("header.id = 'm-1'");
        query.Notes.Should().Equal("`header.correlation-id` is redacted on this topic and is not searched.");
        ShouldCompile(query, builder, KafkaReadGrant.Envelope);
    }

    [Fact]
    public void A_field_redacted_with_searchable_is_searched_by_equals()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("correlation-id").Searchable()));
        CorrelationTopicQuery query = Build(Keys(("correlation", "c-1")), builder);

        query.PredicateText.Should().Be("header.correlation-id = 'c-1'");
        query.Notes.Should().BeEmpty();
        ShouldCompile(query, builder, KafkaReadGrant.Envelope);
    }

    [Fact]
    public void A_deny_listed_header_is_never_searched()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.Header("Authorization").As("auth")));
        CorrelationTopicQuery query = Build(Keys(("auth", "Bearer x")), builder);

        query.HasQuery.Should().BeFalse();
        query.Notes.Should().Equal("`header.Authorization` is never searched.");
    }

    [Fact]
    public void A_json_path_is_searched_only_with_the_payload_grant()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c =>
        {
            c.WithoutDefaults();
            c.JsonPath("$.order.id").As("order");
            c.Header("x-order").As("order");
        }));

        CorrelationTopicQuery without = Build(Keys(("order", "o-1")), builder, payload: false);
        without.PredicateText.Should().Be("header.x-order = 'o-1'");
        without.ReadsValues.Should().BeFalse();
        without.Notes.Should().Equal("`$.order.id` needs `ReadPayloads`.");

        CorrelationTopicQuery with = Build(Keys(("order", "o-1")), builder, payload: true);
        with.PredicateText.Should().Be("$.order.id = 'o-1' or header.x-order = 'o-1'");
        with.ReadsValues.Should().BeTrue();
        ShouldCompile(with, builder, KafkaReadGrant.Envelope | KafkaReadGrant.Payload);
    }

    [Fact]
    public void A_json_path_that_only_the_payload_reads_leaves_no_query_without_the_grant()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.WithoutDefaults().JsonPath("$.order.id").As("order")));
        CorrelationTopicQuery query = Build(Keys(("order", "o-1")), builder, payload: false);

        query.HasQuery.Should().BeFalse();
        query.PredicateText.Should().BeNull();
        query.Notes.Should().ContainSingle().Which.Should().Contain("ReadPayloads");
    }

    [Fact]
    public void A_json_path_is_searched_as_text_and_as_a_number_or_a_boolean_where_the_value_reads_as_one()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.WithoutDefaults().JsonPath("$.n").As("order")));

        Build(Keys(("order", "42")), builder, payload: true).PredicateText.Should().Be("$.n in ('42', 42)");
        Build(Keys(("order", "-1.5e3")), builder, payload: true).PredicateText.Should().Be("$.n in ('-1.5e3', -1.5e3)");
        Build(Keys(("order", "007")), builder, payload: true).PredicateText.Should().Be("$.n = '007'", "007 is a text id, and the number 7 is not it");
        Build(Keys(("order", "true")), builder, payload: true).PredicateText.Should().Be("$.n in ('true', true)");
        Build(Keys(("order", "abc")), builder, payload: true).PredicateText.Should().Be("$.n = 'abc'");

        // A header takes the text only.
        Build(Keys(("correlation", "42"))).PredicateText.Should().Be("header.correlation-id = '42'");
    }

    [Fact]
    public void A_path_redacted_without_searchable_is_left_out()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.WithoutDefaults().JsonPath("$.order.id").As("order")).Redact(r => r.JsonPath("$.order.id")));
        CorrelationTopicQuery query = Build(Keys(("order", "o-1")), builder, payload: true);

        query.HasQuery.Should().BeFalse();
        query.Notes.Should().Equal("`$.order.id` is redacted on this topic and is not searched.");
    }

    [Fact]
    public void A_key_is_written_as_text_and_the_topics_key_format_encodes_it_at_compile_time()
    {
        KafkaTopicBuilder json = CorrelationRig.Declare(t => t.Key(KafkaKeyFormat.JsonString).Correlation(c => c.WithoutDefaults().Key().As("order")));
        CorrelationTopicQuery query = Build(Keys(("order", "ord-1")), json);

        query.PredicateText.Should().Be("key = 'ord-1'");
        ShouldCompile(query, json, KafkaReadGrant.Envelope);

        KafkaTopicBuilder number = CorrelationRig.Declare(t => t.Key(KafkaKeyFormat.Int32BigEndian).Correlation(c => c.WithoutDefaults().Key().As("order")));
        Build(Keys(("order", "17")), number).PredicateText.Should().Be("key = '17'");
        ShouldCompile(Build(Keys(("order", "17")), number), number, KafkaReadGrant.Envelope);
    }

    [Fact]
    public void A_key_the_topics_format_cannot_hold_is_left_out_rather_than_failing_the_scan()
    {
        KafkaTopicBuilder number = CorrelationRig.Declare(t => t.Key(KafkaKeyFormat.Int32BigEndian).Correlation(c => c.WithoutDefaults().Key().As("order")));
        CorrelationTopicQuery query = Build(Keys(("order", "ord-1"), ("order", "17")), number);

        query.PredicateText.Should().Be("key = '17'", "the value the format can hold is searched");
        ShouldCompile(query, number, KafkaReadGrant.Envelope);

        query.Notes.Should().Equal("key: 1 value cannot be held in this topic's key format and was not searched.");

        CorrelationTopicQuery none = Build(Keys(("order", "ord-1"), ("order", "ord-2")), number);
        none.HasQuery.Should().BeFalse();
        none.Notes.Should().Equal("key: 2 values cannot be held in this topic's key format and were not searched.");
    }

    [Fact]
    public void A_redacted_key_is_left_out_unless_searchable()
    {
        KafkaTopicBuilder hidden = CorrelationRig.Declare(t => t.Correlation(c => c.WithoutDefaults().Key().As("order")).Redact(r => r.Key()));
        Build(Keys(("order", "o-1")), hidden).HasQuery.Should().BeFalse();

        KafkaTopicBuilder searchable = CorrelationRig.Declare(t => t.Correlation(c => c.WithoutDefaults().Key().As("order")).Redact(r => r.Key().Searchable()));
        Build(Keys(("order", "o-1")), searchable).PredicateText.Should().Be("key = 'o-1'");
    }

    // The bounds of the parser: 32 clauses, 2,000 characters.

    [Fact]
    public void Over_thirty_two_clauses_the_trace_is_dropped_first_and_the_drop_is_said()
    {
        // Twelve trace values on three headers are 36 clauses, and the message and correlation clauses make 5 more.
        CorrelationKeySet keys = Keys([.. Enumerable.Range(0, 12).Select(static x => ("trace", x.ToString("x32", CultureInfo.InvariantCulture))), ("correlation", "c-1"), ("message", "m-1")]);

        CorrelationTopicQuery query = Build(keys);

        query.PredicateText.Should().NotContain("^=");
        query.PredicateText.Should().Contain("header.correlation-id = 'c-1'").And.Contain("header.id = 'm-1'");
        query.Notes.Should().Equal("`trace` was not searched on this topic: the search would be too long.");
        PredicateParser.Parse(query.PredicateText).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Over_two_thousand_characters_whole_kinds_are_dropped_in_the_documented_order()
    {
        string Long(char c) => new(c, 1_000);
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c =>
        {
            c.Header("x-host");
            c.Header("x-doc").Document("Order");
        }));
        CorrelationKeySet keys = Keys(
            ("trace", "4bf92f3577b34da6a3ce929d0e0e4736"),
            ("x-host", Long('h')),
            ("document:Order", Long('d')),
            ("saga", Long('s')),
            ("message", Long('m')),
            ("correlation", Long('c')));

        CorrelationTopicQuery query = Build(keys, builder);

        // Dropped in order until the text fits: the trace first, then the host's kinds, the document, the saga and the message. The correlation fits.
        query.Notes.Select(static x => x[..x.IndexOf(' ', StringComparison.Ordinal)]).Should().Equal("`trace`", "`x-host`", "`document:Order`", "`saga`", "`message`");
        query.PredicateText.Should().Be("header.correlation-id = '" + Long('c') + "'");
        query.PredicateText!.Length.Should().BeLessThanOrEqualTo(PredicateParser.MaxTextLength);
        PredicateParser.Parse(query.PredicateText).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_search_too_long_for_any_one_kind_leaves_nothing_and_says_so()
    {
        CorrelationTopicQuery query = Build(Keys([.. Enumerable.Range(0, 3).Select(static x => ("correlation", new string((char) ('a' + x), 1_000)))]));

        query.HasQuery.Should().BeFalse();
        query.Notes.Should().Equal("`correlation` was not searched on this topic: the search would be too long.");
    }

    // Every text parses and compiles.

    [Fact]
    public void Every_text_it_writes_parses_and_compiles_across_awkward_values()
    {
        Random random = new(20261112);
        string[] alphabet = ["a", "Z", "0", "'", "''", "\\", "\"", " ", "é", "日本", "😀", "-", ".", "_", "\t", "\r\n", "%", "$", "(", ")", ",", "="];
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Key(KafkaKeyFormat.JsonString).Correlation(c =>
        {
            c.Header("x-order").As("order");
            c.CloudEvent("subject").As("order");
            c.Key().As("order");
            c.JsonPath("$.o.id").As("order");
        }));

        for (int round = 0; round < 200; round++)
        {
            CorrelationKeySet keys = Keys([.. Enumerable.Range(0, random.Next(1, 4)).Select(_ => ("order", string.Concat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => alphabet[random.Next(alphabet.Length)]))))]);

            CorrelationTopicQuery query = Build(keys, builder, payload: true);

            if (query.HasQuery)
            {
                ShouldCompile(query, builder, KafkaReadGrant.Envelope | KafkaReadGrant.Payload);
            }
        }
    }

    [Fact]
    public void Every_default_kind_parses_and_compiles()
    {
        CorrelationTopicQuery query = Build(Keys(("correlation", "it's"), ("message", "a\\b"), ("saga", "日本"), ("trace", "4bf92f3577b34da6a3ce929d0e0e4736")));

        ShouldCompile(query, CorrelationRig.Declare(), KafkaReadGrant.Envelope);
        query.PredicateText.Should().Contain("'it''s'");
    }

    [Fact]
    public void The_notes_name_no_value()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("correlation-id")));
        CorrelationTopicQuery query = Build(Keys(("correlation", "secret-value-123")), builder);

        query.Notes.Should().NotBeEmpty();
        query.Notes.Should().OnlyContain(static x => !x.Contains("secret-value-123", StringComparison.Ordinal));
    }
}
