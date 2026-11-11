using System.Reflection;
using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Logging;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The review follow-ups of the scan engine (REVIEW-ENG-B, findings 1, 3, 4, 5, 8, 9 and 10): what the audit text keeps, the one budget a
/// kept match spends, the bounds of a lowered limit and the slots a failed start must give back, the synchronous dispose, the cursor of a
/// partition that finished, and the small items.
/// </summary>
public class ScanFollowUpTests
{
    // Finding 1: the audit text hides a literal a redaction rule refused.

    [Fact]
    public async Task The_audit_text_hides_a_literal_on_a_deny_listed_header_although_every_topic_refused_the_clause()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("audit");

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], "header.authorization = 'Bearer eyJhbGciOiJIUzI1NiJ9' and key = 'order-7'"));

        plan.IsValid.Should().BeFalse("the deny-listed header cannot be searched");
        plan.Topics.Should().BeEmpty("no topic compiled, so no topic context asked its redactor");
        plan.AuditText.Should().Be("header.authorization = <redacted> and key = 'order-7'");
        plan.AuditText.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
        plan.Errors.Should().OnlyContain(static x => !x.Message.Contains("eyJhbGci", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_audit_text_hides_a_literal_when_only_one_of_two_topics_refused_it()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string redacting = fixture.Topic("audit-a");
        string plain = fixture.Topic("audit-b");
        KafkaRedactionRules rules = new();
        rules.Header("x-token");
        fixture.Declare(redacting, builder => builder.Rules = rules);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([redacting, plain], "header.x-token = 'abc-123'"));

        plan.Errors.Should().ContainSingle("the topic with the rule refused the clause");
        plan.Topics.Keys.Should().Equal(plain);
        plan.AuditText.Should().Be("header.x-token = <redacted>", "the topic that did compile has no rule, but the audit is asked of every topic's rules");
    }

    // Finding 3: one budget for everything a kept match holds.

    private static TopicScanContext Topic(KafkaRedactionRules? rules = null, IKafkaRecordDecoder? decoder = null)
    {
        KafkaTopicBuilder builder = new() { Rules = rules };
        KafkaTopicDescriptor descriptor = new("t", builder);
        PredicateCompilation compiled = PredicateCompiler.Compile(
            null,
            new PredicateCompileContext("t", descriptor.KeyFormat, descriptor.KeyFormatDeclared, new KafkaRedactor(rules), KafkaReadGrant.Envelope, false));

        return new TopicScanContext("t", descriptor, new KafkaRedactor(rules), compiled.Predicate!, decoder);
    }

    private static KafkaMatch Match(WireRecord record, KafkaReadGrant grant = KafkaReadGrant.Envelope | KafkaReadGrant.Payload, int? matchBytes = null, TopicScanContext? topic = null)
    {
        KafkaScanLimits limits = new();

        if (matchBytes is { } cap)
        {
            limits.MaxMatchBytes = cap;
        }

        return KafkaMatchFactory.Create(record, topic ?? Topic(), grant, limits, new DecodeCounters());
    }

    private static WireRecord Record(byte[]? key, byte[]? value, params WireHeader[] headers) =>
        new("t", 0, 7, key, value, headers, 1_700_000_000_000, WireTimestampType.CreateTime, 0);

    /// <summary>Everything a match keeps, in UTF-8 bytes: the sum the budget bounds.</summary>
    private static long Held(KafkaMatch match) =>
        Utf8(match.Key.Text)
        + match.Headers.Sum(static x => Utf8(x.Name) + Utf8(x.Value.Text))
        + match.CloudEventAttributes.Sum(static x => Utf8(x.Name) + Utf8(x.Value.Text))
        + Utf8(match.ValueText)
        + Utf8(match.ValuePreview);

    private static long Utf8(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);

    [Fact]
    public void A_record_of_64_headers_of_4_KiB_and_a_96_KiB_value_keeps_at_most_the_match_budget_cut_in_order()
    {
        WireHeader[] headers = [.. Enumerable.Range(0, 64).Select(static i => new WireHeader("h" + i, Encoding.UTF8.GetBytes(new string('h', 4096))))];
        KafkaMatch match = Match(Record(Encoding.UTF8.GetBytes("key-1"), Encoding.UTF8.GetBytes(new string('v', 96 * 1024)), headers));

        Held(match).Should().BeLessThanOrEqualTo(64 * 1024);
        match.Key.Text.Should().Be("key-1", "the key is spent first");
        match.Headers.Should().NotBeEmpty("headers are spent second");
        match.Headers[0].Value.Text.Should().HaveLength(4096);
        match.Headers.Count.Should().BeLessThan(64);
        (match.Headers.Count + match.HeadersOmitted).Should().Be(64, "every header that is not kept is counted");
        (match.ValueText ?? string.Empty).Length.Should().BeLessThan(96 * 1024);
        match.ValueTruncated.Should().BeTrue();
        match.ValueLength.Should().Be(96 * 1024, "the length of what was cut stays known");
    }

    [Fact]
    public void A_match_spends_its_budget_in_utf8_bytes_not_in_characters()
    {
        string accented = new('é', 4000);
        WireHeader[] headers = [.. Enumerable.Range(0, 20).Select(i => new WireHeader("é" + i, Encoding.UTF8.GetBytes(accented)))];
        KafkaMatch match = Match(Record(Encoding.UTF8.GetBytes(new string('é', 500)), Encoding.UTF8.GetBytes(new string('€', 30_000)), headers));

        Held(match).Should().BeLessThanOrEqualTo(64 * 1024);
        match.Headers.Count.Should().BeLessThan(20, "each header costs about 4 KiB, the 64 KiB runs out before the 20th (each is cut at 4 KiB)");
    }

    [Fact]
    public void A_lowered_match_cap_bounds_the_whole_match()
    {
        WireHeader[] headers = [.. Enumerable.Range(0, 10).Select(static i => new WireHeader("header-" + i, Encoding.UTF8.GetBytes(new string('x', 600))))];
        KafkaMatch match = Match(Record(Encoding.UTF8.GetBytes(new string('k', 900)), Encoding.UTF8.GetBytes(new string('v', 50_000)), headers), matchBytes: 1024);

        Held(match).Should().BeLessThanOrEqualTo(1024);
        match.Key.Text.Should().HaveLength(900);
        match.ValueText.Should().BeNullOrEmpty("the key and the first header spent the budget before the value");
    }

    [Fact]
    public void The_value_gets_what_the_key_headers_and_attributes_leave()
    {
        KafkaMatch match = Match(Record(Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes(new string('v', 200_000)), new WireHeader("h", Encoding.UTF8.GetBytes("1234567890"))));

        Held(match).Should().BeLessThanOrEqualTo(64 * 1024);
        (match.ValueText!.Length).Should().Be((64 * 1024) - 1 - 1 - 10, "key, header name and header value were spent first, and the value took the rest");
        match.ValueTruncated.Should().BeTrue();
    }

    [Fact]
    public void A_cloud_event_keeps_at_most_64_attributes_with_names_cut_at_256_characters_and_counts_the_rest()
    {
        StringBuilder envelope = new("{\"specversion\":\"1.0\"");

        for (int i = 0; i < 100; i++)
        {
            envelope.Append(",\"a").Append(i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).Append("\":\"v\"");
        }

        envelope.Append(",\"").Append(new string('n', 1000)).Append("\":\"long-name\"}");
        KafkaMatch match = Match(Record(null, Encoding.UTF8.GetBytes(envelope.ToString())), topic: Topic(decoder: KafkaDecoders.CloudEvents));

        match.CloudEventAttributes.Should().HaveCount(64);
        match.CloudEventAttributesOmitted.Should().Be(102 - 64, "specversion, 100 numbered members and the one with the long name make 102");
        match.CloudEventAttributes.Should().OnlyContain(static x => x.Name.Length <= 256);
        Held(match).Should().BeLessThanOrEqualTo(64 * 1024);

        KafkaMatch longName = Match(Record(null, Encoding.UTF8.GetBytes("{\"specversion\":\"1.0\",\"" + new string('n', 1000) + "\":\"x\"}")), topic: Topic(decoder: KafkaDecoders.CloudEvents));
        longName.CloudEventAttributes.Should().Contain(static x => x.Name.Length == 256);
    }

    [Fact]
    public void Attributes_are_cut_by_the_budget_after_the_headers_and_the_count_of_what_did_not_fit_is_kept()
    {
        StringBuilder envelope = new("{\"specversion\":\"1.0\"");

        for (int i = 0; i < 40; i++)
        {
            envelope.Append(",\"a").Append(i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append("\":\"").Append(new string('x', 1000)).Append('"');
        }

        envelope.Append('}');
        WireHeader[] headers = [new WireHeader("h", Encoding.UTF8.GetBytes(new string('h', 4096)))];
        KafkaMatch match = Match(Record(null, Encoding.UTF8.GetBytes(envelope.ToString()), headers), matchBytes: 8 * 1024, topic: Topic(decoder: KafkaDecoders.CloudEvents));

        Held(match).Should().BeLessThanOrEqualTo(8 * 1024);
        match.CloudEventAttributes.Count.Should().BeLessThan(41);
        (match.CloudEventAttributes.Count + match.CloudEventAttributesOmitted).Should().Be(41);
    }

    // Finding 4: lowered limits are checked against the bounds, and a failed start gives its slots back.

    [Theory]
    [InlineData("MaxScanDuration")]
    [InlineData("MaxMatches")]
    [InlineData("MaxMatchBytes")]
    [InlineData("MaxBytesPerScan")]
    [InlineData("MaxRecordsPerScan")]
    [InlineData("ConfirmAboveBytes")]
    [InlineData("ConfirmAboveRecords")]
    public void A_lowered_limit_below_the_lowest_the_engine_accepts_is_refused(string property)
    {
        KafkaScanLowering lowering = property switch
        {
            "MaxScanDuration" => new KafkaScanLowering { MaxScanDuration = TimeSpan.FromSeconds(-5) },
            "MaxMatches" => new KafkaScanLowering { MaxMatches = 0 },
            "MaxMatchBytes" => new KafkaScanLowering { MaxMatchBytes = 100 },
            "MaxBytesPerScan" => new KafkaScanLowering { MaxBytesPerScan = 1000 },
            "MaxRecordsPerScan" => new KafkaScanLowering { MaxRecordsPerScan = 10 },
            "ConfirmAboveBytes" => new KafkaScanLowering { ConfirmAboveBytes = -1 },
            _ => new KafkaScanLowering { ConfirmAboveRecords = -1 },
        };

        Action apply = () => lowering.Apply(new KafkaScanLimits());

        apply.Should().Throw<ArgumentException>().Which.Message.Should().Contain(property).And.Contain("lowest");
    }

    [Fact]
    public void A_lowered_limit_at_its_lowest_bound_is_accepted()
    {
        KafkaScanLimits applied = new KafkaScanLowering
        {
            MaxScanDuration = TimeSpan.FromSeconds(5),
            MaxMatches = 1,
            MaxMatchBytes = 1024,
            MaxBytesPerScan = 1024 * 1024,
            MaxRecordsPerScan = 1_000,
            ConfirmAboveBytes = 0,
            ConfirmAboveRecords = 0,
        }.Apply(new KafkaScanLimits());

        (applied.MaxScanDuration, applied.MaxMatches, applied.MaxMatchBytes).Should().Be((TimeSpan.FromSeconds(5), 1, 1024));
    }

    [Fact]
    public async Task A_start_that_fails_after_the_slots_were_taken_gives_both_back()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("lease");
        fixture.Produce(topic, 0, 1, "k", "v");

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        KafkaScanLimiter limiter = fixture.Provider.GetRequiredService<KafkaScanLimiter>();

        // A plan built by hand with a limit the lowering would have refused: the run cannot be made, after the slots were taken.
        KafkaScanLimits bad = new() { MaxScanDuration = TimeSpan.FromSeconds(-5) };
        KafkaScanPlan broken = new(plan.Request, plan.Window, plan.Ranges, plan.Topics, plan.Errors, plan.Verdicts, plan.Estimate, bad, plan.AuditText);

        Action start = () => fixture.Start(broken, confirmed: true);

        start.Should().Throw<ArgumentOutOfRangeException>();
        limiter.Running.Should().Be(0, "the slots were released");

        KafkaScanStart again = fixture.Start(plan, confirmed: true);
        again.Refusal.Should().BeNull("neither the process nor the owner is busy any more");
        await using KafkaScanHandle handle = again.Handle!;
        (await handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Completed);
    }

    // Finding 5: a container disposed synchronously stops its scans.

    [Fact]
    public async Task Disposing_the_provider_synchronously_stops_a_running_scan_with_shutdown()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan reads");

        // On a thread of its own and bounded: a host that disposes its provider synchronously blocks its own thread, never the test's.
        await Task.Run(scan.Fixture.Provider.Dispose, TestContext.Current.CancellationToken).Bounded();

        (await scan.Handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Shutdown);
        scan.Reader.CloseCount.Should().Be(1);
        scan.Fixture.Logs.Entries.Where(static x => x.EventId.Id == 9662).Should().ContainSingle();
    }

    // Finding 8: a partition that finished is not in the cursor.

    [Fact]
    public async Task A_partition_that_ended_at_the_end_of_its_range_is_not_offered_again_even_when_the_last_offsets_were_control_records()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();

        // The range is [0, 100); records 0 to 4 arrive, and the offsets 5 to 99 are markers and aborted batches: the end of the partition is 100.
        for (int i = 0; i < 5; i++)
        {
            scan.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(scan.Topic, 0, i, i)));
        }

        scan.Reader.Enqueue(new WireReadOutcome.PartitionEnd(new WirePartition(scan.Topic, 0), 100));
        KafkaScanResult result = await scan.Handle.Result.Bounded();

        KafkaPartitionResult partition = result.Partitions.Single();
        (partition.State, partition.Next).Should().Be((PartitionEndState.EndOfWindow, 100L));
        result.Cursor.IsEmpty.Should().BeTrue("a continuation of a finished scan would read nothing");
        await scan.DisposeAsync();
    }

    [Fact]
    public async Task A_partition_that_ended_below_its_range_end_continues_from_where_the_log_ended()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();

        // Records 0 to 7 arrive; the log ends at 10 (offsets 8 and 9 are markers) although the range runs to 100.
        for (int i = 0; i < 8; i++)
        {
            scan.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(scan.Topic, 0, i, i)));
        }

        scan.Reader.Enqueue(new WireReadOutcome.PartitionEnd(new WirePartition(scan.Topic, 0), 10));
        KafkaScanResult result = await scan.Handle.Result.Bounded();

        KafkaPartitionResult partition = result.Partitions.Single();
        (partition.State, partition.Next).Should().Be((PartitionEndState.EndOfLog, 10L), "the markers are behind the cursor, not in front of it");
        result.Cursor.Positions.Should().ContainSingle().Which.Should().Be(new KafkaCursorPosition(scan.Topic, 0, 10, 100));
        await scan.DisposeAsync();
    }

    [Fact]
    public async Task A_window_end_found_by_a_record_past_the_range_finishes_the_partition_at_its_end()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();

        for (int i = 0; i < 3; i++)
        {
            scan.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(scan.Topic, 0, i, i)));
        }

        // A record at or past the end of the range: the partition is done, whatever lay between the last record and the end.
        scan.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(scan.Topic, 0, 100, 100)));
        KafkaScanResult result = await scan.Handle.Result.Bounded();

        result.Partitions.Single().State.Should().Be(PartitionEndState.EndOfWindow);
        result.Cursor.IsEmpty.Should().BeTrue();
        await scan.DisposeAsync();
    }

    // Finding 9: no engine log event carries an exception.

    [Fact]
    public void No_engine_log_event_takes_an_exception_because_its_message_can_hold_what_the_broker_or_a_host_decoder_said()
    {
        MethodInfo[] events = [.. typeof(KStudioLog).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static x => x.GetCustomAttributes().Any(static a => a.GetType().Name == "LoggerMessageAttribute"))];

        events.Should().NotBeEmpty();
        events.Where(static x => x.GetParameters().Any(static p => typeof(Exception).IsAssignableFrom(p.ParameterType)))
            .Select(static x => x.Name).Should().BeEmpty();
    }

    // Finding 10.

    [Fact]
    public async Task A_plan_whose_every_topic_was_refused_reports_not_authorized_and_not_completed()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string hidden = fixture.Topic("hidden");
        fixture.Cluster.Deny(KafkaAclOperation.Describe, KafkaResourceType.Topic, hidden);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([hidden]));
        KafkaScanStart started = fixture.Start(plan, confirmed: true);
        await using KafkaScanHandle handle = started.Handle!;
        KafkaScanResult result = await handle.Result.Bounded();

        result.StopReason.Should().Be(KafkaScanStopReason.NotAuthorized);
        result.TopicRefusals.Should().ContainSingle(x => x.Topic == hidden);
        result.Partitions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_with_one_readable_empty_topic_beside_a_refused_one_still_completes()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string hidden = fixture.Topic("hidden");
        string empty = fixture.Topic("empty");
        fixture.Cluster.Deny(KafkaAclOperation.Describe, KafkaResourceType.Topic, hidden);

        (_, KafkaScanResult result, _) = await fixture.RunAsync(ScanFixture.Request([hidden, empty]));

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
    }

    public sealed class FlakyDecoder : IKafkaRecordDecoder
    {
        public static int Constructed { get; set; }

        private static int failures = 1;

        public FlakyDecoder()
        {
            Constructed++;

            if (Interlocked.Decrement(ref failures) >= 0)
            {
                throw new InvalidOperationException("the host's decoder could not be built");
            }
        }

        public string Name => "flaky";

        public KafkaDecodeResult Decode(KafkaDecodeContext context) => KafkaDecodeResult.NotApplicable;
    }

    [Fact]
    public void A_decoder_whose_factory_throws_is_not_cached_as_the_answer()
    {
        FlakyDecoder.Constructed = 0;
        KafkaTopicBuilder builder = new();
        builder.Value<FlakyDecoder>();
        KafkaTopicDescriptor descriptor = new("t", builder);
        KafkaDecoderResolver resolver = new(new ServiceCollection().BuildServiceProvider());

        Action first = () => resolver.For(descriptor);

        first.Should().Throw<InvalidOperationException>();
        resolver.For(descriptor).Should().BeOfType<FlakyDecoder>("the next scan tries the factory again");
        resolver.For(descriptor).Should().BeSameAs(resolver.For(descriptor), "once built it is a singleton per descriptor");
        FlakyDecoder.Constructed.Should().Be(2);
    }

    public sealed class CountingDecoder : IKafkaRecordDecoder
    {
        public static int Constructed { get; set; }

        public CountingDecoder() => Constructed++;

        public string Name => "counting";

        public KafkaDecodeResult Decode(KafkaDecodeContext context) => KafkaDecodeResult.NotApplicable;
    }

    [Fact]
    public async Task A_host_decoder_is_not_even_created_for_a_visitor_who_may_not_read_payloads()
    {
        CountingDecoder.Constructed = 0;
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("decoder");
        fixture.Declare(topic, static builder => builder.Value<CountingDecoder>());
        fixture.Produce(topic, 0, 1, "k", "v");

        await fixture.PlanAsync(ScanFixture.Request([topic], grant: KafkaReadGrant.Envelope));
        CountingDecoder.Constructed.Should().Be(0, "under the envelope grant no host decoder runs, so none is made");

        await fixture.PlanAsync(ScanFixture.Request([topic], grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload));
        CountingDecoder.Constructed.Should().Be(1);
    }

    [Fact]
    public async Task A_built_in_decoder_still_applies_under_the_envelope_grant()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("builtin");
        fixture.Declare(topic, static builder => builder.Value(KafkaDecoders.CloudEvents));
        fixture.Produce(topic, 0, 1, "k", """{"specversion":"1.0","type":"order.created"}""");

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], "ce.type = 'order.created'"));

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        matches.Should().ContainSingle("the topic declared structured CloudEvents, and that is not a host decoder");
    }

    [Fact]
    public async Task The_owner_budget_forgets_an_owner_who_has_been_idle_for_more_than_an_hour()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        OwnerByteBudget budget = fixture.Provider.GetRequiredService<OwnerByteBudget>();

        budget.Add("visitor-1", 100);
        budget.Add("visitor-2", 100);
        budget.TrackedOwners.Should().Be(2);

        fixture.Clock.Advance(TimeSpan.FromMinutes(61));
        budget.Add("visitor-3", 100);

        budget.TrackedOwners.Should().Be(1, "the two idle owners were dropped, the one who just read stays");
        budget.Remaining("visitor-1").Should().Be(budget.BytesPerHour);
        budget.Remaining("visitor-3").Should().Be(budget.BytesPerHour - 100);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_sampling_consumer_is_small_and_the_scans_own_consumer_keeps_the_configured_sizes()
    {
        List<WireReaderOptions> opened = [];
        ScanFixture fixture = ScanFixture.Create(wrap: wire => new ScriptedWire(wire, options =>
        {
            lock (opened)
            {
                opened.Add(options);
            }

            return wire.OpenReader(options);
        }));
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("sampler");

        for (int i = 0; i < 20; i++)
        {
            fixture.Produce(topic, 0, i, "k", "v");
        }

        await fixture.RunAsync(ScanFixture.Request([topic]));

        opened.Should().HaveCount(2, "one consumer samples record sizes and one scans");
        opened[0].Limits.QueueKilobytes.Should().Be(1024);
        opened[0].Limits.FetchBytes.Should().Be(1024 * 1024);
        opened[1].Limits.QueueKilobytes.Should().Be(new KafkaScanLimits().QueueKilobytes);
        opened[1].Limits.FetchBytes.Should().Be(new KafkaScanLimits().FetchBytes);
        fixture.Options.Limits.QueueKilobytes.Should().Be(new KafkaScanLimits().QueueKilobytes, "the host's limits were not changed for the sampler");
    }
}
