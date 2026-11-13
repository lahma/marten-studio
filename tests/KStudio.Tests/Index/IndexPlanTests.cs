using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Index;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Contract;
using KStudio.Tests.Scanning;
using KStudio.Tests.Support;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Index;

/// <summary>
/// The host's record index in a plan (AGENTS.md D48, D-M5 decision 5), over the fake broker: when it is asked and when it never is, what is dropped
/// unseen, that a hint only narrows and a match is only ever what the scan found, that a failing, slow or unconstructible index costs only the
/// narrowing, and that a continuation stays inside the ranges and does not ask again.
/// </summary>
public class IndexPlanTests
{
    private const string Sentinel = "S3NT1NEL-7c41d0";
    private const int PerPartition = 20;

    /// <summary>
    /// Two partitions of twenty records each. The key <c>needle</c> is at partition 0 offsets 5 and 15 and at partition 1 offsets 12 and 18; every other
    /// record has its own key.
    /// </summary>
    private static ScanFixture Seeded(FakeRecordIndex index, out string topic, Action<KafkaEngineOptions>? configure = null)
    {
        ScanFixture fixture = ScanFixture.Create(options =>
        {
            options.UseRecordIndex(_ => index, nameof(FakeRecordIndex));
            configure?.Invoke(options);
        });

        topic = fixture.Topic("orders", 2);
        Fill(fixture, topic);
        return fixture;
    }

    private static void Fill(ScanFixture fixture, string topic)
    {
        for (int offset = 0; offset < PerPartition; offset++)
        {
            fixture.Produce(topic, 0, offset * 2, offset is 5 or 15 ? "needle" : $"k-0-{offset}", "v");
            fixture.Produce(topic, 1, (offset * 2) + 1, offset is 12 or 18 ? "needle" : $"k-1-{offset}", "v");
        }
    }

    private static KafkaScanRequest Needle(string topic, string predicate = "key = 'needle'", KafkaScanLowering? lower = null, KafkaScanCursor? cursor = null) =>
        ScanFixture.Request([topic], predicate, cursor: cursor, lower: lower);

    private static string[] Positions(IEnumerable<KafkaMatch> matches) => [.. matches.Select(static x => $"{x.Partition}/{x.Offset}").Order(StringComparer.Ordinal)];

    private static CapturedLogEntry[] IndexLogs(ScanFixture fixture) => [.. fixture.Logs.Entries.Where(static x => x.EventId.Id == 9679)];

    [Fact]
    public async Task A_key_equality_asks_the_index_once_with_the_cluster_the_topics_the_window_and_the_literal()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);

        await fixture.PlanAsync(Needle(topic));

        KafkaIndexQuery query = index.Queries.Should().ContainSingle().Subject;
        query.Cluster.Should().Be(EngineHarness.ClusterKey);
        query.TenantId.Should().BeNull();
        query.Topics.Should().Equal(topic);
        query.From.Should().Be(ScanFixture.T0);
        query.To.Should().Be(ScanFixture.T0.AddSeconds(1_000));
        query.Field.Should().Be(KafkaIndexField.Key);
        query.FieldName.Should().BeNull();
        query.CorrelationKind.Should().BeNull();
        query.Value.Should().Be("needle");
    }

    [Fact]
    public async Task The_index_cannot_widen_the_topics_a_hint_is_admitted_for_by_writing_to_the_list_it_was_given()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        string secret = fixture.Topic("secret", 2);
        Fill(fixture, secret);
        bool writable = true;

        index.Answer = (query, _) =>
        {
            writable = query.Topics is string[] or List<string> || !((ICollection<string>) query.Topics).IsReadOnly;
            return ValueTask.FromResult(KafkaIndexAnswer.Hints([new KafkaIndexHint(secret, 0, 5, 6)]));
        };

        KafkaScanPlan plan = await fixture.PlanAsync(Needle(topic));

        writable.Should().BeFalse("the topics the index holds are a read-only copy");
        plan.Index!.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere, "a hint about a topic the search did not name is dropped");
        plan.Ranges.Should().OnlyContain(x => x.Topic == topic);
    }

    [Fact]
    public async Task A_correlation_header_is_asked_by_name_with_its_kind()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);

        await fixture.PlanAsync(Needle(topic, "header.correlation-id = 'c-1'"));

        KafkaIndexQuery query = index.Queries.Should().ContainSingle().Subject;
        query.Field.Should().Be(KafkaIndexField.Header);
        query.FieldName.Should().Be("correlation-id");
        query.CorrelationKind.Should().NotBeNullOrEmpty("correlation-id is a default correlation field of every topic");
        query.Value.Should().Be("c-1");
    }

    [Fact]
    public async Task A_right_hint_narrows_the_plan_to_its_range_and_the_estimate_is_of_the_narrowed_ranges()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        index.Hints(new KafkaIndexHint(topic, 1, 12, 13));

        KafkaScanPlan plan = await fixture.PlanAsync(Needle(topic));

        plan.Ranges.Where(static x => !x.IsEmpty).Should().ContainSingle().Which.Should().Match<KStudio.Core.Windows.PartitionRange>(static x => x.Partition == 1 && x.Start == 12 && x.End == 13);
        plan.Ranges.Single(static x => x.Partition == 0).IsEmpty.Should().BeTrue();
        plan.Estimate.Offsets.Should().Be(1, "the estimate is of what is read, not of the window");
        plan.Estimate.Partitions.Should().Be(1);
        plan.Index.Should().NotBeNull();
        plan.Index!.IsNarrowed.Should().BeTrue();
        plan.Index.OffsetsInWindow.Should().Be(PerPartition * 2);
    }

    [Fact]
    public async Task A_right_hint_finds_the_match_by_reading_and_judging_the_record()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        index.Hints(new KafkaIndexHint(topic, 1, 12, 13));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Needle(topic));

        Positions(matches).Should().Equal("1/12");
        result.Records.Should().Be(1, "only the offset the hint named was read");
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.Index.Should().BeSameAs(plan.Index);
        result.IndexAuditText.Should().Be($"; narrowed by the host's record index to 1 range of {topic} (1 offset)");
    }

    [Fact]
    public async Task A_wrong_hint_finds_nothing_and_never_something_false_and_the_negative_is_said_of_the_range_read()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);

        // The needles are at 0/5, 0/15, 1/12 and 1/18; this range holds none of them.
        index.Hints(new KafkaIndexHint(topic, 1, 3, 6));

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Needle(topic));

        matches.Should().BeEmpty("a hint is where to look, never what was found");
        result.Records.Should().Be(3);
        result.Index!.NoMatch(result.Records).Should().Be("No match in the 3 records read from the 1 range the host's record index named. The rest of the window was not read.");
    }

    [Fact]
    public async Task A_hint_that_names_a_record_with_another_key_does_not_make_it_a_match()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        index.Hints(new KafkaIndexHint(topic, 0, 0, 3));

        (_, _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Needle(topic));

        matches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hull_of_several_hints_still_judges_every_record_in_it()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);

        // Two hints on partition 0 become one range [4, 8): the record between them is read as well, and a needle in the gap would be found.
        index.Hints(new KafkaIndexHint(topic, 0, 4, 5), new KafkaIndexHint(topic, 0, 7, 8));

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Needle(topic));

        result.Records.Should().Be(4);
        Positions(matches).Should().Equal("0/5");
    }

    [Theory]
    [InlineData("key in ('needle', 'other')")]
    [InlineData("key ^= 'need'")]
    [InlineData("key ~ 'need'")]
    [InlineData("any = 'needle'")]
    [InlineData("key = 'needle' or key = 'other'")]
    [InlineData("not key = 'needle'")]
    [InlineData("header.tenant-id is null")]
    [InlineData("")]
    public async Task A_search_the_index_cannot_be_asked_about_never_asks_it(string predicate)
    {
        FakeRecordIndex index = new();
        index.Hints(new KafkaIndexHint("any", 0, 0, 1));
        await using ScanFixture fixture = Seeded(index, out string topic);

        KafkaScanPlan plan = await fixture.PlanAsync(Needle(topic, predicate));

        index.Queries.Should().BeEmpty();
        plan.Index.Should().BeNull();
    }

    [Theory]
    [InlineData("value = 'needle'")]
    [InlineData("$.customer.id = 'needle'")]
    public async Task A_value_or_a_json_path_is_never_asked_about(string predicate)
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], predicate, grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload));

        plan.IsValid.Should().BeTrue();
        index.Queries.Should().BeEmpty();
        plan.Index.Should().BeNull();
    }

    [Fact]
    public async Task A_redacted_field_is_never_asked_about_even_when_it_is_searchable()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        fixture.Declare(topic, t => t.Redact(r => r.Header("tenant-id").Searchable()));

        KafkaScanPlan plan = await fixture.PlanAsync(Needle(topic, $"header.tenant-id = '{Sentinel}'"));

        plan.IsValid.Should().BeTrue();
        index.Queries.Should().BeEmpty("a Searchable() rule's literal never leaves the engine");
        plan.Index.Should().BeNull();
    }

    [Fact]
    public async Task A_key_masked_on_any_one_topic_is_not_asked_and_the_next_clause_is()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        string masked = fixture.Topic("masked");
        fixture.Declare(masked, t => t.Redact(r => r.Key().Searchable()));

        await fixture.PlanAsync(ScanFixture.Request([topic, masked], $"key = '{Sentinel}'"));
        index.Queries.Should().BeEmpty("one of the topics masks the key");

        await fixture.PlanAsync(ScanFixture.Request([topic, masked], $"key = '{Sentinel}' and header.correlation-id = 'c-1'"));
        index.Queries.Should().ContainSingle().Which.FieldName.Should().Be("correlation-id");
        index.Queries[0].Value.Should().Be("c-1");
    }

    [Fact]
    public async Task A_continuation_a_search_for_each_topic_a_tail_and_the_whole_window_never_ask()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        KafkaScanCursor cursor = new([new KafkaCursorPosition(topic, 0, 2, 10)]);

        KafkaScanPlan continued = await fixture.PlanAsync(Needle(topic, cursor: cursor));
        KafkaScanPlan perTopic = await fixture.PlanAsync(
            ScanFixture.Request([topic], grant: KafkaReadGrant.Envelope) with { PerTopic = [new KafkaTopicQuery(new KafkaTopicName(topic), "key = 'needle'", KafkaReadGrant.Envelope)] });
        KafkaScanPlan tail = await fixture.PlanTailAsync(TailRig.Request([topic], "key = 'needle'"));
        KafkaScanPlan whole = await fixture.PlanAsync(Needle(topic) with { IgnoreIndex = true });

        index.Queries.Should().BeEmpty();
        continued.Index.Should().BeNull();
        perTopic.Index.Should().BeNull();
        tail.Index.Should().BeNull();
        whole.Index.Should().BeNull();
        whole.Ranges.Sum(static x => x.End - x.Start).Should().Be(PerPartition * 2, "the whole window is read");
    }

    [Fact]
    public async Task A_hint_about_another_topic_is_never_in_the_plan_the_result_or_the_audit_text()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        string secret = fixture.Topic("secret", 2);
        Fill(fixture, secret);
        index.Hints(new KafkaIndexHint(secret, 0, 5, 6), new KafkaIndexHint(topic, 1, 12, 13));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Needle(topic));

        index.Queries.Single().Topics.Should().Equal(topic);
        plan.Index!.Ranges.Should().OnlyContain(x => x.Topic == topic);
        plan.Ranges.Should().OnlyContain(x => x.Topic == topic);
        matches.Should().OnlyContain(x => x.Topic == topic);
        string[] texts = [plan.Index.Note, plan.Index.Caveat!, plan.Index.NoMatch(0), plan.Index.AuditSuffix, result.IndexAuditText, .. result.Index!.Ranges.Select(static x => x.Topic)];
        texts.Should().OnlyContain(x => !x.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hints_that_name_only_another_topic_leave_the_whole_window_read_with_the_note()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);
        string secret = fixture.Topic("secret", 2);
        Fill(fixture, secret);
        index.Hints(new KafkaIndexHint(secret, 0, 5, 6));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Needle(topic));

        plan.Index!.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere);
        plan.Index.Note.Should().Be("The host's record index pointed nowhere in this window; the whole window is read.");
        Positions(matches).Should().Equal("0/15", "0/5", "1/12", "1/18");
        result.Records.Should().Be(PerPartition * 2);
        result.Index.Should().BeNull("a scan that read its whole window has nothing a negative must not claim");
        result.IndexAuditText.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failing_index_reads_the_whole_window_and_logs_event_9679_once_with_its_type_and_no_content(bool throwsSynchronously)
    {
        FakeRecordIndex index = new()
        {
            Answer = throwsSynchronously
                ? (_, _) => throw new InvalidOperationException("EXC-" + Sentinel)
                : (_, _) => ValueTask.FromException<KafkaIndexAnswer>(new InvalidOperationException("EXC-" + Sentinel)),
        };

        await using ScanFixture fixture = Seeded(index, out string topic);

        (KafkaScanPlan plan, KafkaScanResult result, _) = await fixture.RunAsync(Needle(topic, $"key = '{Sentinel}'"));
        await fixture.PlanAsync(Needle(topic, $"key = '{Sentinel}'"));

        plan.Index!.Outcome.Should().Be(KafkaIndexOutcome.DidNotAnswer);
        plan.Index.Note.Should().Be("The host's record index did not answer; the whole window is read.");
        result.Records.Should().Be(PerPartition * 2, "the whole window was read");
        CapturedLogEntry entry = IndexLogs(fixture).Should().ContainSingle("once per cluster per ten minutes").Subject;
        entry.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Debug);
        entry.Message.Should().Be($"KStudio's record index {nameof(FakeRecordIndex)} did not answer for cluster {EngineHarness.ClusterKey}: InvalidOperationException");
        fixture.Logs.Entries.Should().OnlyContain(x => !x.Message.Contains(Sentinel, StringComparison.Ordinal), "neither the literal nor the exception's message is ever logged");
    }

    [Fact]
    public async Task The_failure_is_logged_again_after_ten_minutes()
    {
        FakeRecordIndex index = new() { Answer = static (_, _) => throw new InvalidOperationException("x") };
        await using ScanFixture fixture = Seeded(index, out string topic);

        await fixture.PlanAsync(Needle(topic));
        await fixture.PlanAsync(Needle(topic));
        IndexLogs(fixture).Should().HaveCount(1);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        await fixture.PlanAsync(Needle(topic));

        IndexLogs(fixture).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_index_that_never_answers_is_given_the_request_timeout_and_then_the_whole_window_is_read()
    {
        FakeRecordIndex index = new()
        {
            // Ignores its token and never completes: the engine must not wait for it.
            Answer = static (_, _) => new ValueTask<KafkaIndexAnswer>(new TaskCompletionSource<KafkaIndexAnswer>().Task),
        };

        await using ScanFixture fixture = Seeded(index, out string topic);

        Task<KafkaScanPlan> planning = fixture.PlanAsync(Needle(topic));
        ScanFixture.Until(() => index.Queries.Count == 1, "the index to be asked");
        fixture.Clock.Advance(fixture.Options.RequestTimeout + TimeSpan.FromSeconds(1));

        KafkaScanPlan plan = await planning.Bounded();

        plan.Index!.Outcome.Should().Be(KafkaIndexOutcome.DidNotAnswer);
        plan.Ranges.Sum(static x => x.End - x.Start).Should().Be(PerPartition * 2);
        IndexLogs(fixture).Should().ContainSingle().Which.Message.Should().EndWith(": Timeout");
    }

    [Fact]
    public async Task Abandoning_the_planning_is_not_an_index_that_did_not_answer()
    {
        FakeRecordIndex index = new() { Answer = static (_, _) => new ValueTask<KafkaIndexAnswer>(new TaskCompletionSource<KafkaIndexAnswer>().Task) };
        await using ScanFixture fixture = Seeded(index, out string topic);
        using CancellationTokenSource abandon = new();

        Task<KafkaScanPlan> planning = fixture.Scanner.PlanAsync(Needle(topic), fixture.Owner, abandon.Token);
        ScanFixture.Until(() => index.Queries.Count == 1, "the index to be asked");
        await abandon.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planning.Bounded());
        IndexLogs(fixture).Should().BeEmpty();
    }

    [Fact]
    public async Task The_index_gets_the_request_timeout_as_its_token()
    {
        FakeRecordIndex index = new()
        {
            Answer = static async (_, token) =>
            {
                // Honours its token: the timeout cancels it rather than the engine walking away from it.
                await new TaskCompletionSource().Task.WaitAsync(token);
                return KafkaIndexAnswer.None;
            },
        };

        await using ScanFixture fixture = Seeded(index, out string topic);

        Task<KafkaScanPlan> planning = fixture.PlanAsync(Needle(topic));
        ScanFixture.Until(() => index.Queries.Count == 1, "the index to be asked");
        fixture.Clock.Advance(fixture.Options.RequestTimeout + TimeSpan.FromSeconds(1));

        KafkaScanPlan plan = await planning.Bounded();

        plan.Index!.Outcome.Should().Be(KafkaIndexOutcome.DidNotAnswer);
        IndexLogs(fixture).Should().ContainSingle().Which.Message.Should().EndWith(": Timeout");
    }

    [Fact]
    public async Task A_continuation_of_a_narrowed_scan_reads_the_rest_of_the_same_ranges_and_does_not_ask_again()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);

        // Partition 0 is read up to offset 10 (its needle at 15 is outside), partition 1 in full: three needles are inside.
        index.Hints(new KafkaIndexHint(topic, 0, 0, 10), new KafkaIndexHint(topic, 1, 0, 20));
        (KafkaScanPlan first, KafkaScanResult stopped, IReadOnlyList<KafkaMatch> firstMatches) = await fixture.RunAsync(Needle(topic, lower: new KafkaScanLowering { MaxMatches = 1 }));

        stopped.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        stopped.Cursor.Index.Should().BeSameAs(first.Index, "the cursor carries the narrowing, as it carries its pruning");

        (KafkaScanPlan second, KafkaScanResult rest, IReadOnlyList<KafkaMatch> restMatches) = await fixture.RunAsync(Needle(topic, cursor: stopped.Cursor));

        index.Queries.Should().ContainSingle("a continuation never asks again");
        second.Index.Should().BeSameAs(first.Index, "its negatives are still about those ranges");
        rest.Index.Should().BeSameAs(first.Index);
        Positions([.. firstMatches, .. restMatches]).Should().Equal("0/5", "1/12", "1/18");
        second.Ranges.Should().OnlyContain(x => x.End <= (x.Partition == 0 ? 10 : 20), "the continuation stays inside the narrowed ranges");
    }

    [Fact]
    public async Task A_continued_runs_negative_is_said_only_of_the_records_it_read_and_never_of_the_ranges_the_first_run_found_a_match_in()
    {
        FakeRecordIndex index = new();
        await using ScanFixture fixture = Seeded(index, out string topic);

        // The one range, offsets 4 to 6 of partition 0, holds the needle at 0/5. The first run stops at it; the rest of the range holds none.
        index.Hints(new KafkaIndexHint(topic, 0, 4, 7));
        (KafkaScanPlan first, KafkaScanResult stopped, IReadOnlyList<KafkaMatch> firstMatches) = await fixture.RunAsync(Needle(topic, lower: new KafkaScanLowering { MaxMatches = 1 }));
        (_, KafkaScanResult rest, IReadOnlyList<KafkaMatch> restMatches) = await fixture.RunAsync(Needle(topic, cursor: stopped.Cursor));

        Positions(firstMatches).Should().Equal("0/5");
        restMatches.Should().BeEmpty();
        rest.Records.Should().Be(1);
        string stop = KafkaSearchText.Stop(rest, first.Limits);
        stop.Should().Be("No match in the 1 record read from the 1 range the host's record index named. The rest of the window was not read.");
        stop.Should().NotContain("offsets", "it does not claim the ranges as a whole had no match: the first run found one there");
    }

    [Fact]
    public async Task An_index_that_blocks_its_thread_before_returning_is_bounded_by_the_request_timeout()
    {
        using ManualResetEventSlim release = new(false);
        FakeRecordIndex index = new()
        {
            Answer = (_, _) =>
            {
                release.Wait(TestBounds.Gate, CancellationToken.None);
                return ValueTask.FromResult(KafkaIndexAnswer.None);
            },
        };

        await using ScanFixture fixture = Seeded(index, out string topic);

        try
        {
            Task<KafkaScanPlan> planning = fixture.PlanAsync(Needle(topic));
            ScanFixture.Until(() => index.Queries.Count == 1, "the index to be asked");
            fixture.Clock.Advance(fixture.Options.RequestTimeout + TimeSpan.FromSeconds(1));

            KafkaScanPlan plan = await planning.Bounded();

            plan.Index!.Outcome.Should().Be(KafkaIndexOutcome.DidNotAnswer);
            IndexLogs(fixture).Should().ContainSingle().Which.Message.Should().EndWith(": Timeout");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_host_that_registered_no_index_plans_exactly_as_before()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("orders", 2);
        Fill(fixture, topic);

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Needle(topic));

        KafkaRecordIndexResolver resolver = fixture.Provider.GetRequiredService<KafkaRecordIndexResolver>();
        resolver.IsRegistered.Should().BeFalse();
        resolver.Name.Should().BeNull();
        plan.Index.Should().BeNull();
        result.Index.Should().BeNull();
        Positions(matches).Should().Equal("0/15", "0/5", "1/12", "1/18");
        result.Records.Should().Be(PerPartition * 2);
    }

    [Fact]
    public async Task RecordIndex_creates_the_host_type_from_the_container_once_and_names_it()
    {
        DependencyIndex.Created = 0;
        await using ScanFixture fixture = ScanFixture.Create(static options => options.RecordIndex<DependencyIndex>());
        string topic = fixture.Topic("orders", 2);
        Fill(fixture, topic);

        KafkaScanPlan first = await fixture.PlanAsync(Needle(topic));
        KafkaScanPlan second = await fixture.PlanAsync(Needle(topic));

        DependencyIndex.Created.Should().Be(1, "one index per engine, made on first use");
        DependencyIndex.Time.Should().BeSameAs(fixture.Clock, "its constructor is resolved from the container");
        first.Index!.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere);
        second.Index!.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere);
        fixture.Provider.GetRequiredService<KafkaRecordIndexResolver>().Name.Should().Be(nameof(DependencyIndex));
    }

    [Fact]
    public async Task An_index_whose_constructor_throws_is_an_index_that_did_not_answer_and_is_tried_again_on_the_next_plan()
    {
        FlakyIndex.Attempts = 0;
        await using ScanFixture fixture = ScanFixture.Create(static options => options.RecordIndex<FlakyIndex>());
        string topic = fixture.Topic("orders", 2);
        Fill(fixture, topic);

        KafkaScanPlan first = await fixture.PlanAsync(Needle(topic));
        KafkaScanPlan second = await fixture.PlanAsync(Needle(topic));

        first.Index!.Outcome.Should().Be(KafkaIndexOutcome.DidNotAnswer);
        second.Index!.IsNarrowed.Should().BeTrue("a constructor that throws is never cached as the answer");
        FlakyIndex.Attempts.Should().Be(2);
        IndexLogs(fixture).Should().ContainSingle().Which.Message.Should().EndWith(": InvalidOperationException");
    }

    [Fact]
    public async Task A_second_RecordIndex_call_replaces_the_first()
    {
        DependencyIndex.Created = 0;
        FlakyIndex.Attempts = 0;
        await using ScanFixture fixture = ScanFixture.Create(static options => options.RecordIndex<FlakyIndex>().RecordIndex<DependencyIndex>());
        string topic = fixture.Topic("orders", 2);
        Fill(fixture, topic);

        await fixture.PlanAsync(Needle(topic));

        FlakyIndex.Attempts.Should().Be(0);
        DependencyIndex.Created.Should().Be(1);
        fixture.Provider.GetRequiredService<KafkaRecordIndexResolver>().Name.Should().Be(nameof(DependencyIndex));
    }

    /// <summary>Takes a service from the container, to prove the registration activates it the way a decoder is.</summary>
    internal sealed class DependencyIndex : IKafkaRecordIndex
    {
        public DependencyIndex(TimeProvider time)
        {
            Created++;
            Time = time;
        }

        public static int Created { get; set; }

        public static TimeProvider? Time { get; private set; }

        public ValueTask<KafkaIndexAnswer> FindAsync(KafkaIndexQuery query, CancellationToken cancellationToken) => ValueTask.FromResult(KafkaIndexAnswer.None);
    }

    /// <summary>Its constructor throws the first time and never again.</summary>
    internal sealed class FlakyIndex : IKafkaRecordIndex
    {
        public FlakyIndex()
        {
            if (++Attempts == 1)
            {
                throw new InvalidOperationException("first attempt " + Sentinel);
            }
        }

        public static int Attempts { get; set; }

        public ValueTask<KafkaIndexAnswer> FindAsync(KafkaIndexQuery query, CancellationToken cancellationToken) =>
            ValueTask.FromResult(KafkaIndexAnswer.Hints([new KafkaIndexHint(query.Topics[0], 1, 12, 13)]));
    }
}
