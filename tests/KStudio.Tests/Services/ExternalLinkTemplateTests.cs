using KStudio.Internal;

namespace KStudio.Tests.Services;

/// <summary>D44, decision 9.2 and 9.3 of D-CORR: a host's external link templates carry no credential, an error never repeats a template, a value is escaped wherever it stands, and a value that is absent means no link.</summary>
public class ExternalLinkTemplateTests
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 8, 12, 30, 15, 250, TimeSpan.Zero);

    private static KafkaExternalLinkRegistration Link(string label, string template) => new(label, template);

    private static IReadOnlyList<string> Problems(params KafkaExternalLinkRegistration[] links) => ExternalLinkTemplate.Validate(links);

    [Fact]
    public void A_plain_template_is_valid()
    {
        Problems(Link("Traces", "https://tempo.example.com/trace/{traceId}")).Should().BeEmpty();
        Problems(Link("Logs", "http://logs.example.com/search?from={fromMs}&to={toMs}&q={correlationId}")).Should().BeEmpty();
        Problems().Should().BeEmpty("no link is a valid configuration");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789012345678901234567890123456789X")]
    public void A_label_is_one_to_forty_characters(string label)
    {
        Problems(Link(label, "https://tempo.example.com/trace/{traceId}")).Should().ContainSingle().Which.Should().Contain("the label must be 1 to 40 characters");
    }

    [Fact]
    public void A_label_of_forty_characters_is_valid()
    {
        Problems(Link(new string('a', 40), "https://tempo.example.com/")).Should().BeEmpty();
    }

    [Fact]
    public void A_label_is_unique_ignoring_case()
    {
        Problems(Link("Traces", "https://a.example.com/"), Link("traces", "https://b.example.com/"))
            .Should().ContainSingle().Which.Should().Contain("External link 'traces'").And.Contain("used twice");
    }

    [Fact]
    public void At_most_eight_links()
    {
        KafkaExternalLinkRegistration[] nine = [.. Enumerable.Range(0, 9).Select(static i => Link("L" + i, "https://x.example.com/" + i))];

        Problems(nine[..8]).Should().BeEmpty();
        Problems(nine).Should().ContainSingle().Which.Should().Contain("at most 8");
    }

    [Fact]
    public void A_template_is_at_most_two_thousand_characters()
    {
        string longest = "https://tempo.example.com/" + new string('a', ExternalLinkTemplate.MaxTemplateLength - "https://tempo.example.com/".Length);

        longest.Length.Should().Be(2_000);
        Problems(Link("Traces", longest)).Should().BeEmpty();
        Problems(Link("Traces", longest + "a")).Should().ContainSingle().Which.Should().Contain("longer than 2,000");
    }

    [Theory]
    [InlineData("tempo.example.com/trace/{traceId}")]
    [InlineData("/trace/{traceId}")]
    [InlineData("ftp://tempo.example.com/{traceId}")]
    [InlineData("javascript:alert({traceId})")]
    [InlineData("{traceId}")]
    public void A_template_must_be_an_absolute_http_or_https_address_once_filled(string template)
    {
        Problems(Link("Traces", template)).Should().ContainSingle().Which.Should().Contain("not an absolute http or https address");
    }

    [Theory]
    [InlineData("https://tempo.example.com/{trace}")]
    [InlineData("https://tempo.example.com/{TraceId}")]
    [InlineData("https://tempo.example.com/{spanId}")]
    [InlineData("https://tempo.example.com/{}")]
    public void An_unknown_placeholder_is_refused(string template)
    {
        Problems(Link("Traces", template)).Should().ContainSingle().Which.Should().Contain("placeholder KStudio does not know");
    }

    [Theory]
    [InlineData("https://tempo.example.com/{traceId")]
    [InlineData("https://tempo.example.com/traceId}")]
    [InlineData("https://tempo.example.com/{{traceId}}")]
    public void An_unbalanced_brace_is_refused(string template)
    {
        Problems(Link("Traces", template)).Should().ContainSingle().Which.Should().Contain("brace");
    }

    [Theory]
    [InlineData("https://user:secret@tempo.example.com/trace/{traceId}")]
    [InlineData("https://user@tempo.example.com/trace/{traceId}")]
    public void User_info_is_a_credential(string template)
    {
        Problems(Link("Traces", template)).Should().ContainSingle().Which.Should().Contain("user info");
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("token")]
    [InlineData("api_key")]
    [InlineData("apikey")]
    [InlineData("x-api-key")]
    [InlineData("password")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("secret")]
    [InlineData("client_secret")]
    [InlineData("signature")]
    [InlineData("sig")]
    [InlineData("auth")]
    [InlineData("TOKEN")]
    [InlineData("Api_Key")]
    public void A_credential_parameter_is_refused_whatever_its_case(string name)
    {
        Problems(Link("Traces", $"https://tempo.example.com/trace/{{traceId}}?{name}=abc")).Should().ContainSingle().Which.Should().Contain("names a credential");
        Problems(Link("Traces", $"https://tempo.example.com/trace/{{traceId}}?a=1&{name}=abc")).Should().ContainSingle();
        Problems(Link("Traces", $"https://tempo.example.com/trace/{{traceId}}?{name}={{correlationId}}")).Should().ContainSingle();
    }

    [Theory]
    [InlineData("https://kibana.example.com/app#/view?token=abc")]
    [InlineData("https://kibana.example.com/app#%2Fview%3Ftoken=abc")]
    [InlineData("https://logs.example.com/?a=1;token=abc")]
    [InlineData("https://logs.example.com/?a=1%26token=abc")]
    public void A_credential_parameter_is_seen_in_a_fragment_that_is_a_query_and_after_a_semicolon(string template)
    {
        Problems(Link("Traces", template)).Should().ContainSingle().Which.Should().Contain("names a credential");
    }

    [Theory]
    [InlineData("https://kibana.example.com/app#/view?tokenizer=x")]
    [InlineData("https://kibana.example.com/app#/view?author=x&keyword=y")]
    public void A_fragment_with_a_harmless_parameter_is_allowed(string template)
    {
        Problems(Link("Traces", template)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("access_key")]
    [InlineData("private_token")]
    [InlineData("X-Amz-Signature")]
    [InlineData("X-Amz-Security-Token")]
    [InlineData("X-Amz-Credential")]
    [InlineData("code")]
    [InlineData("key")]
    [InlineData("db.password")]
    public void A_name_that_ends_like_a_credential_is_refused_whatever_it_holds(string name)
    {
        Problems(Link("Traces", $"https://tempo.example.com/trace/{{traceId}}?{name}=abc")).Should().ContainSingle().Which.Should().Contain("names a credential");
    }

    [Theory]
    [InlineData("tokenizer")]
    [InlineData("author")]
    [InlineData("keyword")]
    [InlineData("codec")]
    public void A_name_that_only_contains_a_credential_word_is_allowed(string name)
    {
        Problems(Link("Traces", $"https://tempo.example.com/trace/{{traceId}}?{name}=abc")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://{correlationId}/x")]
    [InlineData("https://logs.example.com{correlationId}/")]
    [InlineData("https://logs.{traceId}.example.com/x")]
    public void A_placeholder_in_the_host_is_refused_with_the_sentence_and_never_the_template(string template)
    {
        string text = string.Join(" ", Problems(Link("Traces", template)));

        text.Should().Contain("A link template puts a placeholder in its host; placeholders go in the path, the query or the fragment.").And.NotContain("example.com");
    }

    [Theory]
    [InlineData("https://h/{traceId}")]
    [InlineData("https://logs.example.com?q={correlationId}")]
    [InlineData("https://logs.example.com#{correlationId}")]
    public void A_placeholder_after_the_host_is_allowed(string template)
    {
        Problems(Link("Traces", template)).Should().BeEmpty();
    }

    [Fact]
    public void A_parameter_that_only_contains_a_credential_word_is_allowed()
    {
        Problems(Link("Traces", "https://tempo.example.com/trace/{traceId}?tokenizer=simple&author=me")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://user:S3NT1NEL-pass@tempo.example.com/{traceId}")]
    [InlineData("https://tempo.example.com/{traceId}?api_key=S3NT1NEL-pass")]
    [InlineData("https://tempo.example.com/{nope}?S3NT1NEL-pass=1")]
    [InlineData("ftp://tempo.example.com/S3NT1NEL-pass")]
    public void An_error_names_the_label_and_the_rule_and_never_the_template(string template)
    {
        string text = string.Join(" ", Problems(Link("Traces", template)));

        text.Should().Contain("Traces").And.NotContain("S3NT1NEL").And.NotContain("tempo.example.com");
    }

    [Fact]
    public void An_error_bounds_a_long_label()
    {
        string label = new string('x', 500);

        string text = string.Join(" ", Problems(Link(label, "https://a.example.com/")));

        text.Length.Should().BeLessThan(300);
    }

    [Fact]
    public void Every_value_is_escaped_wherever_the_placeholder_stands()
    {
        KafkaExternalLinkValues values = new("4bf92f3577b34da6a3ce929d0e0e4736", "a/b c?d#e&f=g%h", Sent);

        KafkaExternalLink? link = ExternalLinkTemplate.Fill(Link("Logs", "https://logs.example.com/{correlationId}/x?q={correlationId}&t={traceId}#{correlationId}"), values);

        link.Should().NotBeNull();
        link!.Href.Should().Be("https://logs.example.com/a%2Fb%20c%3Fd%23e%26f%3Dg%25h/x?q=a%2Fb%20c%3Fd%23e%26f%3Dg%25h&t=4bf92f3577b34da6a3ce929d0e0e4736#a%2Fb%20c%3Fd%23e%26f%3Dg%25h");
    }

    [Fact]
    public void A_missing_value_means_no_link_never_a_link_with_a_hole()
    {
        KafkaExternalLinkRegistration both = Link("Both", "https://x.example.com/{traceId}/{correlationId}");

        ExternalLinkTemplate.Fill(both, new KafkaExternalLinkValues(null, "c-1", Sent)).Should().BeNull();
        ExternalLinkTemplate.Fill(both, new KafkaExternalLinkValues("t", null, Sent)).Should().BeNull();
        ExternalLinkTemplate.Fill(both, new KafkaExternalLinkValues("t", string.Empty, Sent)).Should().BeNull();
        ExternalLinkTemplate.Fill(both, new KafkaExternalLinkValues("t", "c-1", Sent)).Should().NotBeNull();
    }

    [Fact]
    public void A_template_that_names_only_the_time_needs_no_header()
    {
        ExternalLinkTemplate.Fill(Link("Window", "https://x.example.com/?from={from}&to={to}"), new KafkaExternalLinkValues(null, null, Sent)).Should().NotBeNull();
    }

    [Fact]
    public void The_window_is_the_timestamp_fifteen_minutes_either_side_in_both_forms()
    {
        KafkaExternalLinkValues values = new(null, null, Sent);

        KafkaExternalLink iso = ExternalLinkTemplate.Fill(Link("Iso", "https://x.example.com/{from}/{to}"), values)!;
        KafkaExternalLink millis = ExternalLinkTemplate.Fill(Link("Ms", "https://x.example.com/{fromMs}/{toMs}"), values)!;

        iso.Href.Should().Be("https://x.example.com/2026-11-08T12%3A15%3A15.250Z/2026-11-08T12%3A45%3A15.250Z");
        millis.Href.Should().Be($"https://x.example.com/{Sent.AddMinutes(-15).ToUnixTimeMilliseconds()}/{Sent.AddMinutes(15).ToUnixTimeMilliseconds()}");
    }

    [Fact]
    public void A_timestamp_in_another_offset_is_written_in_utc()
    {
        DateTimeOffset local = new(2026, 11, 8, 14, 30, 15, 250, TimeSpan.FromHours(2));

        ExternalLinkTemplate.Fill(Link("Iso", "https://x.example.com/{from}"), new KafkaExternalLinkValues(null, null, local))!.Href
            .Should().Be("https://x.example.com/2026-11-08T12%3A15%3A15.250Z");
    }
}
