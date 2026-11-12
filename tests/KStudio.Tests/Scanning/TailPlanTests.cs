using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Scanning;

/// <summary>
/// Planning a live tail (D-M5 decisions 3.1 and 3.2): where each partition is read from, what a resumed tail does with a cursor, the plan's
/// shape, the profile's cap, and the refusals of <c>StartTail</c>.
/// </summary>
public class TailPlanTests
{
    private static void Seed(ScanFixture fixture, string topic, int partition, int count, int fromSecond = 0)
    {
        for (int i = 0; i < count; i++)
        {
            fixture.Produce(topic, partition, fromSecond + i, "k" + i, "v" + i);
        }
    }

    /// <summary>Five committed records and a transaction left open over three more: the last stable offset is 5 and the high watermark 8.</summary>
    private static void SeedOpenTransaction(ScanFixture fixture, string topic)
    {
        Seed(fixture, topic, 0, 5);
        long transaction = fixture.Cluster.BeginTransaction();

        for (int i = 5; i < 8; i++)
        {
            fixture.Cluster.Produce(topic, 0, [1], [1], [], ScanFixture.T0.ToUnixTimeMilliseconds() + (i * 1000L), transaction);
        }
    }

    [Fact]
    public async Task A_tail_starts_at_the_last_stable_offset_under_committed_and_at_the_high_watermark_under_uncommitted()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("lso");
        SeedOpenTransaction(fixture, topic);

        KafkaScanPlan committed = await fixture.PlanTailAsync(TailRig.Request([topic]));
        KafkaScanPlan uncommitted = await fixture.PlanTailAsync(TailRig.Request([topic], isolation: KafkaIsolation.IncludeUncommitted));

        PartitionRange c = committed.Ranges.Should().ContainSingle().Subject;
        c.Start.Should().Be(5, "a transaction open now holds records below the high watermark that commit later; starting at the high watermark would skip them");
        c.End.Should().Be(KafkaScanCursor.OpenEnd, "a tail's range has no end");
        c.HighWatermark.Should().Be(8);
        c.Lso.Should().Be(5);

        PartitionRange u = uncommitted.Ranges.Should().ContainSingle().Subject;
        u.Start.Should().Be(8, "uncommitted records are read, so the high watermark is the end of what can be read");
        u.End.Should().Be(KafkaScanCursor.OpenEnd);
        u.Lso.Should().BeNull();
    }

    [Fact]
    public async Task A_plan_is_a_tail_plan_with_no_pruning_no_confirmation_and_the_tails_own_estimate()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("shape", partitions: 3);

        for (int p = 0; p < 3; p++)
        {
            Seed(fixture, topic, p, 4);
        }

        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request([topic], "key = \"k1\""));

        plan.IsTail.Should().BeTrue();
        plan.Tail.Should().NotBeNull();
        plan.IsValid.Should().BeTrue();
        plan.Pruning.Should().BeEmpty("a key pruning needs a verified sample of a window, and a tail has none");
        plan.Estimate.Should().Be(new KafkaScanEstimate(0, null, null, 3, false, "A live tail reads what arrives."));
        plan.Estimate.NeedsConfirmation.Should().BeFalse();
        plan.Ranges.Should().HaveCount(3).And.OnlyContain(static x => x.Start == 4 && x.End == KafkaScanCursor.OpenEnd);
        plan.Ranges.SelectMany(static x => x.Notes).Should().BeEmpty();
        plan.Gaps.Should().BeEmpty();
        plan.Request.Isolation.Should().Be(KafkaIsolation.Committed);
        plan.Request.PredicateText.Should().Be("key = \"k1\"");
    }

    [Fact]
    public async Task The_search_is_compiled_exactly_as_a_scans_is_the_same_audit_text_verdicts_and_values_rule()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("same");
        Seed(fixture, topic, 0, 3);
        const string text = "header.x-user = \"anna\" and key ~ \"k\"";

        KafkaScanPlan scan = await fixture.PlanAsync(ScanFixture.Request([topic], text));
        KafkaScanPlan tail = await fixture.PlanTailAsync(TailRig.Request([topic], text));

        tail.AuditText.Should().Be(scan.AuditText);
        tail.Verdicts.Should().BeEquivalentTo(scan.Verdicts);
        tail.ReadsValues.Should().Be(scan.ReadsValues);
        tail.Topics.Keys.Should().Equal(scan.Topics.Keys);
    }

    [Fact]
    public async Task A_search_with_errors_is_a_plan_with_errors_that_cannot_start_and_a_scan_plan_cannot_be_started_as_a_tail_nor_the_other_way()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("invalid");
        Seed(fixture, topic, 0, 3);

        KafkaScanPlan invalid = await fixture.PlanTailAsync(TailRig.Request([topic], "key = "));
        invalid.IsValid.Should().BeFalse();
        invalid.IsTail.Should().BeTrue();
        fixture.StartTail(invalid).Refusal.Should().Be(KafkaScanRefusal.PredicateInvalid);

        KafkaScanPlan scan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        KafkaScanPlan tail = await fixture.PlanTailAsync(TailRig.Request([topic]));

        Action startScanAsTail = () => fixture.StartTail(scan);
        Action startTailAsScan = () => fixture.Start(tail);
        startScanAsTail.Should().Throw<ArgumentException>().WithMessage("*Start*");
        startTailAsScan.Should().Throw<ArgumentException>().WithMessage("*StartTail*");
    }

    [Fact]
    public async Task A_resumed_tail_starts_where_the_paused_one_stopped_and_a_partition_added_since_starts_at_its_end_with_a_note()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("resume", partitions: 2);
        Seed(fixture, topic, 0, 10);
        Seed(fixture, topic, 1, 6);

        // The paused tail knew partition 0 only: it stopped at offset 3 of 10; partition 1 came after.
        KafkaScanCursor cursor = new([new KafkaCursorPosition(topic, 0, 3, KafkaScanCursor.OpenEnd)]);

        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request([topic], resume: cursor));

        plan.Ranges.Single(static x => x.Partition == 0).Start.Should().Be(3, "nothing produced while paused is skipped");
        PartitionRange added = plan.Ranges.Single(static x => x.Partition == 1);
        added.Start.Should().Be(6, "a partition the cursor does not name is read from its end");
        added.Notes.Should().ContainSingle().Which.Should().Be(new WindowNote.PartitionAddedToTail(topic, 1));
        added.Notes[0].Text.Should().Be($"Partition 1 of {topic} was added after the tail started; it is read from its end.");
        plan.Gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cursor_below_the_log_start_is_moved_to_it_and_the_records_in_between_are_a_gap()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("gap");
        Seed(fixture, topic, 0, 10);
        fixture.Cluster.DeleteRecords(topic, 0, 4);

        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request([topic], resume: new KafkaScanCursor([new KafkaCursorPosition(topic, 0, 2, KafkaScanCursor.OpenEnd)])));

        plan.Ranges.Should().ContainSingle().Which.Start.Should().Be(4, "the log starts at 4; reading from 2 would be a reset the policy refuses");
        plan.Gaps.Should().ContainSingle().Which.Should().Be(new KafkaScanGap(topic, 0, 2, 3, 2, LogStart: 4));

        // The run reports it: the gap is in the result of the segment.
        KafkaScanStart started = fixture.StartTail(plan);
        await using KafkaScanHandle handle = started.Handle!;
        handle.UntilCaughtUp();
        handle.Stop();
        KafkaScanResult result = await handle.Result.Bounded();
        result.Gaps.Should().ContainSingle().Which.Lost.Should().Be(2);
    }

    [Fact]
    public async Task A_cursor_position_at_the_log_start_or_inside_the_log_is_read_from_exactly_there()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("inside");
        Seed(fixture, topic, 0, 10);
        fixture.Cluster.DeleteRecords(topic, 0, 4);

        KafkaScanPlan atStart = await fixture.PlanTailAsync(TailRig.Request([topic], resume: new KafkaScanCursor([new KafkaCursorPosition(topic, 0, 4, KafkaScanCursor.OpenEnd)])));
        KafkaScanPlan inside = await fixture.PlanTailAsync(TailRig.Request([topic], resume: new KafkaScanCursor([new KafkaCursorPosition(topic, 0, 7, KafkaScanCursor.OpenEnd)])));

        atStart.Ranges.Single().Start.Should().Be(4);
        atStart.Gaps.Should().BeEmpty();
        inside.Ranges.Single().Start.Should().Be(7);
        inside.Gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cursor_past_the_end_of_a_recreated_or_truncated_partition_starts_at_its_end_and_says_so()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("shorter");
        Seed(fixture, topic, 0, 10);

        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request([topic], resume: new KafkaScanCursor([new KafkaCursorPosition(topic, 0, 25, KafkaScanCursor.OpenEnd)])));

        PartitionRange range = plan.Ranges.Single();
        range.Start.Should().Be(10, "the high watermark: the partition is shorter than where the tail paused");
        range.Notes.Should().ContainSingle().Which.Should().Be(new WindowNote.TailPositionPastEnd(topic, 0, 10));
        range.Notes[0].Text.Should().Be($"Partition 0 of {topic} is shorter than where the tail paused (recreated or truncated); it is read from offset 10.");
        plan.Gaps.Should().BeEmpty("nothing can be said about records that are not in this partition");

        // At the end exactly is not past it: a tail that had read everything resumes there, with no note.
        KafkaScanPlan atEnd = await fixture.PlanTailAsync(TailRig.Request([topic], resume: new KafkaScanCursor([new KafkaCursorPosition(topic, 0, 10, KafkaScanCursor.OpenEnd)])));
        atEnd.Ranges.Single().Start.Should().Be(10);
        atEnd.Ranges.Single().Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tails_notes_carry_no_window_words_only_uncommitted_records_and_an_unreadable_configuration()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string compacted = fixture.Topic("compacted", config: new Dictionary<string, string> { ["cleanup.policy"] = "compact" });
        string plain = fixture.Topic("plain");
        Seed(fixture, compacted, 0, 3);
        Seed(fixture, plain, 0, 3);
        fixture.Cluster.Deny(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, plain);

        KafkaScanPlan committed = await fixture.PlanTailAsync(TailRig.Request([compacted, plain]));
        KafkaScanPlan uncommitted = await fixture.PlanTailAsync(TailRig.Request([compacted, plain], isolation: KafkaIsolation.IncludeUncommitted));

        // The resolver said these of a window: a compacted topic, a partition list fixed at a time, a margin past an end. A tail has none of them.
        committed.Notes.OfType<WindowNote.CompactedTopic>().Should().BeEmpty();
        committed.Notes.OfType<WindowNote.PartitionsFixed>().Should().BeEmpty();
        committed.Notes.OfType<WindowNote.CreateTimeMargin>().Should().BeEmpty();
        committed.Notes.OfType<WindowNote.IncludesUncommitted>().Should().BeEmpty();
        committed.Notes.OfType<WindowNote.ConfigNotReadable>().Should().ContainSingle().Which.Topic.Should().Be(plain);
        committed.Notes.Should().AllBeOfType<WindowNote.ConfigNotReadable>();

        uncommitted.Notes.OfType<WindowNote.IncludesUncommitted>().Should().ContainSingle();
        uncommitted.Notes.OfType<WindowNote.CompactedTopic>().Should().BeEmpty();
        uncommitted.Notes.OfType<WindowNote.PartitionsFixed>().Should().BeEmpty();
        uncommitted.Notes.Where(static x => x is not (WindowNote.ConfigNotReadable or WindowNote.IncludesUncommitted)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_topic_that_does_not_exist_is_reported_by_the_plan_and_a_tail_of_nothing_ends_not_authorized_without_a_consumer()
    {
        await using ScanFixture fixture = ScanFixture.Create();

        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request(["no.such.topic"]));

        plan.Ranges.Should().BeEmpty();
        plan.Window!.Topics.Should().ContainSingle().Which.Refusal.Should().NotBeNull();

        KafkaScanStart started = fixture.StartTail(plan);
        await using KafkaScanHandle handle = started.Handle!;
        (await handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.NotAuthorized);
        fixture.Readers.Should().BeEmpty("there was nothing to follow, so no consumer was opened");
    }

    [Fact]
    public async Task A_plan_asks_for_no_grant_below_the_envelope_and_no_topic_less_tail()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("args");

        Func<Task> noGrant = () => fixture.PlanTailAsync(TailRig.Request([topic], grant: KafkaReadGrant.None));
        Func<Task> noTopics = () => fixture.PlanTailAsync(TailRig.Request([]));

        await noGrant.Should().ThrowAsync<ArgumentException>();
        await noTopics.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(KafkaProfile.Development, true)]
    [InlineData(KafkaProfile.Test, true)]
    [InlineData(KafkaProfile.Production, false)]
    public async Task StartTail_refuses_on_a_production_cluster_only_and_takes_no_slot_when_it_refuses(KafkaProfile? profile, bool starts)
    {
        await using ScanFixture fixture = ScanFixture.Create(cluster: profile is { } p ? c => c.Profile(p) : null);
        string topic = fixture.Topic("profile");
        Seed(fixture, topic, 0, 3);

        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request([topic]));
        KafkaScanStart started = fixture.StartTail(plan);

        if (starts)
        {
            started.Refusal.Should().BeNull();
            await using KafkaScanHandle handle = started.Handle!;
            handle.Stop();
            await handle.Result.Bounded();
        }
        else
        {
            started.Refusal.Should().Be(KafkaScanRefusal.ProfileForbids);
            started.Handle.Should().BeNull();
            fixture.Readers.Should().BeEmpty("a refused tail opened no consumer");

            // The slot was never taken: a scan of the same cluster by the same owner starts.
            KafkaScanPlan scan = await fixture.PlanAsync(ScanFixture.Request([topic]));
            KafkaScanStart scanStarted = fixture.Start(scan, confirmed: true);
            scanStarted.Refusal.Should().BeNull();
            await using KafkaScanHandle scanHandle = scanStarted.Handle!;
            await scanHandle.Result.Bounded();
        }
    }

    [Fact]
    public async Task The_profile_caps_a_tails_limits()
    {
        static void Raised(KafkaEngineOptions options)
        {
            options.Limits.MaxTailMatches = 5_000;
            options.Limits.TailPauseAfter = TimeSpan.FromMinutes(25);
            options.Limits.MaxMatches = 8_000;
        }

        await using ScanFixture fixture = ScanFixture.Create(Raised, cluster: static c => c.Profile(KafkaProfile.Test));
        string topic = fixture.Topic("cap");
        Seed(fixture, topic, 0, 3);

        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request([topic]));

        plan.Limits.MaxTailMatches.Should().Be(200);
        plan.Limits.TailPauseAfter.Should().Be(TimeSpan.FromMinutes(5));
        plan.Limits.TailCapacity.Should().Be(200);
    }

    [Fact]
    public async Task A_tail_of_a_cluster_whose_own_allowance_is_spent_is_refused_before_it_takes_a_slot()
    {
        static void Raised(KafkaEngineOptions options)
        {
            options.Limits.OwnerBytesPerHour = 50L * 1024 * 1024 * 1024;
            options.Limits.BytesPerSecond = 100L * 1024 * 1024;
        }

        await using ScanFixture fixture = ScanFixture.Create(Raised, cluster: static c => c.Profile(KafkaProfile.Test));
        string topic = fixture.Topic("allowance");
        Seed(fixture, topic, 0, 3);

        // Spend the cluster's own allowance (Test: 2 GiB an hour) for this owner, as the cost controls hold it.
        KafkaClusterCost cost = fixture.Provider.GetRequiredService<KafkaClusterCostControls>().For(fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey));
        cost.OwnerBudget!.Add(fixture.Owner.Key, 3L * 1024 * 1024 * 1024);

        KafkaScanStart started = fixture.StartTail(await fixture.PlanTailAsync(TailRig.Request([topic])));

        started.Refusal.Should().Be(KafkaScanRefusal.OwnerBudgetSpent);
        fixture.Readers.Should().BeEmpty();
    }
}
