using KStudio.Core.Clusters;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Scanning;

using static KStudio.Tests.Predicates.ValueHarness;
using static KStudio.Tests.Redaction.PayloadRedactionTests;

namespace KStudio.Tests.Redaction;

/// <summary>
/// The engine's sinks over a whole scan and a whole record read (decision 3.6 of D-PAYLOAD): no match, no plan text and no log line holds a
/// value a rule masks, and a rule that cannot be applied is logged once, by kind, never by content.
/// </summary>
public class PayloadRedactionScanTests
{
    private static async Task<string> SeedAsync(ScanFixture fixture, KafkaRedactionRules rules, int count = 5)
    {
        string topic = fixture.Topic("sentinel");
        fixture.Declare(topic, t => t.Rules = rules);

        for (int i = 0; i < count; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, Document);
        }

        await Task.CompletedTask;
        return topic;
    }

    private static void NoSentinelInLogs(ScanFixture fixture)
    {
        fixture.Logs.Entries.Should().NotBeEmpty("a scan logs at Trace what it did");
        fixture.Logs.Entries.Select(static x => x.Message).Should().NotContain(static x => x.Contains("SENTINEL", StringComparison.Ordinal) || x.Contains("555 0199", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scan_that_matches_by_another_field_returns_masked_values_and_logs_nothing_of_them()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = await SeedAsync(fixture, SentinelRules());

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) =
            await fixture.RunAsync(ScanFixture.Request([topic], "$.customer.name = 'Ann'", grant: Payload));

        matches.Should().HaveCount(5);
        matches.Should().OnlyContain(static x => x.ValueText!.Contains("••• (redacted by $.customer.email)") && x.ValueText.Contains("pattern phone"));
        NoSentinel(matches);
        NoSentinel(result.UndecidableSentences.Append("sentinel guard"));
        NoSentinel(plan.Verdicts.Select(static x => x.Description).Append(plan.AuditText).Append("guard"));
        NoSentinelInLogs(fixture);
    }

    [Fact]
    public async Task A_searchable_exact_match_finds_the_record_and_its_plan_text_holds_redacted_not_the_literal()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = await SeedAsync(fixture, SentinelRules());

        (KafkaScanPlan plan, KafkaScanResult _, IReadOnlyList<KafkaMatch> matches) =
            await fixture.RunAsync(ScanFixture.Request([topic], $"$.customer.email = '{Email}'", grant: Payload));

        matches.Should().HaveCount(5, "the exact value is found");
        plan.AuditText.Should().Be("$.customer.email = <redacted>");
        plan.Verdicts.Single().Description.Should().Contain("compared exactly, and never shown");
        NoSentinel(matches);
        NoSentinelInLogs(fixture);
    }

    [Fact]
    public async Task A_refused_clause_on_a_masked_path_keeps_its_literal_out_of_the_plan()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = await SeedAsync(fixture, SentinelRules());

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], $"$.customer.email ~ '{Email}'", grant: Payload));

        plan.IsValid.Should().BeFalse();
        plan.AuditText.Should().Be("$.customer.email ~ <redacted>");
        plan.Errors.Should().ContainSingle().Which.Message.Should().Be("$.customer.email is redacted by $.customer.email (position 1): only = and in can search it.");
        NoSentinel(plan.Errors.Select(static x => x.Message).Append(plan.AuditText).Append("guard"));
    }

    [Fact]
    public async Task A_record_read_returns_the_masked_value_whole_and_logs_nothing_of_it()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = await SeedAsync(fixture, SentinelRules());

        KafkaRecordRead read = await fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 0, 2, 2, Payload, KafkaIsolation.Committed, 1024 * 1024),
            fixture.Owner,
            TestContext.Current.CancellationToken);

        read.Status.Should().Be(KafkaRecordReadStatus.Found);
        read.Record!.ValueText.Should().Contain("••• (redacted by $.customer.email)");
        NoSentinel(read);
        NoSentinelInLogs(fixture);
    }

    [Fact]
    public async Task A_value_a_rule_cannot_be_applied_to_is_withheld_in_every_match_and_logged_once_by_kind()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("withheld");
        fixture.Declare(topic, static t => t.Redact(static r => r.JsonPath("$.note")));

        for (int i = 0; i < 20; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, $"call {Phone} now");
        }

        (_, KafkaScanResult _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], "key ^= 'k'", grant: Payload));

        matches.Should().HaveCount(20);
        matches.Should().OnlyContain(static x => x.ValueText == null && x.ValueWithheldBy == "$.note");
        NoSentinelExcept(matches, Phone);

        string[] logged = [.. fixture.Logs.Entries.Where(static x => x.EventId.Id == 9675).Select(static x => x.Message)];

        logged.Should().ContainSingle("the same rule on the same topic is logged once per window");
        logged[0].Should().Be($"KStudio could not apply a JsonPath redaction rule to a value of {topic} on {EngineHarness.ClusterKey}: NotApplicable, none");
        fixture.Logs.Entries.Where(static x => x.EventId.Id == 9675).Should().OnlyContain(static x => x.Level == Microsoft.Extensions.Logging.LogLevel.Debug);
        fixture.Logs.Entries.Select(static x => x.Message).Should().NotContain(static x => x.Contains("555 0199", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_decoder_never_sees_a_masked_header_in_a_scan()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("decoder-headers");
        List<string> seen = [];
        fixture.Declare(topic, t =>
        {
            t.Redact(static r => r.Header("x-email"));
            t.Value(new HeaderEchoDecoder(seen));
        });
        fixture.Produce(topic, 0, 1, "k", "payload", ("x-email", "SENTINEL-mail"), ("authorization", "SENTINEL-token"), ("x-tenant", "acme"));

        (_, KafkaScanResult _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], "key = 'k'", grant: Payload));

        matches.Should().ContainSingle();
        matches[0].ValueText.Should().Be("x-tenant");
        seen.Should().Equal("x-tenant");
        NoSentinel(matches);
    }

    private sealed class HeaderEchoDecoder(List<string> seen) : IKafkaRecordDecoder
    {
        public string Name => "HeaderEcho";

        public KafkaDecodeResult Decode(KafkaDecodeContext context)
        {
            string names = string.Join(",", context.Headers.Select(static x => x.Name));
            seen.Add(names);
            return KafkaDecodeResult.Text(names, "Echo");
        }
    }
}
