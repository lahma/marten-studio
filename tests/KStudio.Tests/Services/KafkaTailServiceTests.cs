using KStudio.Core.Clusters;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Scanning;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>
/// The live tail service's gates and audit (D-M5 decisions 3.8 and 3.9, hard rule 5): the capability, the profile, the bounds, both policies per topic and the grant
/// are asked before every plan and every segment; a request naming a refused topic is refused whole and names nothing; a tail session is one audit entry whatever the
/// number of its segments (9607 at its start, 9608 and the ring entry when it ends); and the visitor's typed text reaches neither.
/// </summary>
public class KafkaTailServiceTests
{
    private const string Sentinel = "S3NT1NEL-91ab4c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KafkaSearchRig Rig(Action<KafkaStudioOptions>? studio = null, bool payloads = false, KafkaProfile? profile = null) =>
        new(
            studio: o =>
            {
                o.Capabilities.LiveTail = true;
                studio?.Invoke(o);
            },
            payloads: payloads,
            profile: profile);

    private static void Seed(KafkaSearchRig rig, string topic = "orders", int count = 3)
    {
        rig.Topic(topic);

        for (int i = 0; i < count; i++)
        {
            rig.Produce(topic, 0, i, "k" + i, "v" + i, ("correlation-id", "c-" + i));
        }
    }

    /// <summary>Applies a segment's batches to its session until it ends, as the page's loop does; the returned task is the segment's end.</summary>
    private static Task Pump(KafkaTailSegment segment) =>
        Task.Run(
            async () =>
            {
                await foreach (KafkaScanBatch batch in segment.ReadBatchesAsync(Ct))
                {
                    segment.Session.Apply(batch);

                    if (batch.Final is { } final)
                    {
                        segment.Complete(final);
                    }
                }
            },
            Ct);

    private static async Task<(KafkaTailSegment Segment, Task Pump)> BeginAsync(KafkaTailService tail, KafkaTailInput input, KafkaTailSession? session = null)
    {
        KafkaTailPlanResult.Planned plan = (KafkaTailPlanResult.Planned) await tail.PlanAsync(input, session, Ct);
        KafkaTailStartResult.Running run = (KafkaTailStartResult.Running) await tail.StartAsync(session, plan.Value, Ct);
        return (run.Segment, Pump(run.Segment));
    }

    private static void UntilCaughtUp(KafkaTailSession session) =>
        ScanFixture.Until(() => session.LiveProgress is { } progress && progress.Partitions.Count > 0 && progress.Partitions.All(static x => x.CaughtUp), "every partition reported its end");

    [Fact]
    public async Task A_tail_is_refused_whole_when_its_capability_is_off_and_the_one_it_needs_is_named_first()
    {
        await using KafkaSearchRig rig = Rig(o => o.Capabilities.LiveTail = false);
        Seed(rig);
        int callsBefore = rig.BrokerCalls;
        KafkaTailService tail = rig.Tail();

        KafkaTailPlanResult plan = await tail.PlanAsync(rig.TailInput(["orders"]), null, Ct);

        plan.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Contain("KafkaStudioOptions.Capabilities.LiveTail");
        rig.BrokerCalls.Should().Be(callsBefore, "a refused tail reads nothing");
        rig.Ring.Should().ContainSingle(static x => !x.Succeeded && x.Action == "LiveTail" && x.Capability == "LiveTail");
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9602);

        rig.Studio.Options.Capabilities.LiveTail = true;
        rig.Studio.Options.Capabilities.SearchRecords = false;

        KafkaTailPlanResult withoutSearch = await tail.PlanAsync(rig.TailInput(["orders"]), null, Ct);

        withoutSearch.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords", "a prerequisite is named before the capability");
    }

    [Fact]
    public async Task A_Production_cluster_never_tails_even_when_the_capability_is_granted_and_the_refusal_names_the_setting()
    {
        await using KafkaSearchRig rig = Rig(profile: KafkaProfile.Production);
        Seed(rig);
        int callsBefore = rig.BrokerCalls;
        KafkaTailService tail = rig.Tail();

        tail.ForbiddenByProfile(rig.Entry).Should().BeTrue();
        KafkaTailPlanResult plan = await tail.PlanAsync(rig.TailInput(["orders"]), null, Ct);

        string text = plan.Should().BeOfType<KafkaTailPlanResult.Refused>().Subject.Text;
        text.Should().Be(KafkaStudioText.ProfileForbids(KafkaStudioText.LiveTailAction, rig.Entry));
        text.Should().Contain("KafkaClusterBuilder.Profile").And.Contain("even when they are granted");
        rig.BrokerCalls.Should().Be(callsBefore);

        StudioActionLogEntry entry = rig.Ring.Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be("LiveTail");
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9609).Which.Message.Should().Contain("live tail");
    }

    [Fact]
    public async Task A_request_naming_a_refused_topic_is_refused_whole_and_names_nothing()
    {
        await using KafkaSearchRig rig = Rig();
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret.v1");
        Seed(rig, "orders");
        Seed(rig, "secret.v1");
        int callsBefore = rig.BrokerCalls;

        KafkaTailPlanResult plan = await rig.Tail().PlanAsync(rig.TailInput(["orders", "secret.v1"]), null, Ct);

        plan.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.RequestRefused).And.NotContain("secret");
        rig.BrokerCalls.Should().Be(callsBefore, "a refused request is not narrowed to the allowed topics and read");
        rig.Ring.Should().ContainSingle().Which.Message.Should().Be(KafkaStudioText.RefusedEntry, "the entry says nothing about why");
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task The_cluster_policy_and_the_capability_policy_are_asked_per_topic_for_SearchRecords_and_LiveTail()
    {
        await using KafkaSearchRig rig = Rig(o => o.CapabilityAuthorizationPolicy = "kafka-tail");
        Seed(rig);

        KafkaTailPlanResult plan = await rig.Tail().PlanAsync(rig.TailInput(["orders"]), null, Ct);
        plan.Should().BeOfType<KafkaTailPlanResult.Planned>();

        var asked = rig.Studio.Authorization.KafkaCalls.Select(static x => (x.Policy, x.Resource.Topic, x.Resource.Capability)).ToArray();
        asked.Should().Contain(("kafka-topics", null, null), "the cluster policy is asked about the cluster");
        asked.Should().Contain(("kafka-topics", "orders", null), "and about the topic");
        asked.Should().Contain(("kafka-tail", "orders", "SearchRecords"));
        asked.Should().Contain(("kafka-tail", "orders", "LiveTail"));

        // A capability policy that refuses LiveTail on the topic refuses the whole tail.
        rig.Studio.Authorization.AllowKafka(static resource => resource.Capability != "LiveTail");
        KafkaTailPlanResult refused = await rig.Tail().PlanAsync(rig.TailInput(["orders"]), null, Ct);

        refused.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.RequestRefused);
    }

    [Fact]
    public async Task A_tail_that_reads_values_is_refused_whole_without_ReadPayloads()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);

        KafkaTailPlanResult plan = await rig.Tail().PlanAsync(rig.TailInput(["orders"], "value ~ 'v1'"), null, Ct);

        plan.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Contain("KafkaStudioOptions.Capabilities.ReadPayloads");
        rig.Ring.Should().ContainSingle(static x => !x.Succeeded);
    }

    [Fact]
    public async Task More_than_twenty_topics_and_a_text_too_long_are_refused_before_anything_is_read()
    {
        await using KafkaSearchRig rig = Rig();
        int callsBefore = rig.BrokerCalls;
        KafkaTailService tail = rig.Tail();

        KafkaTailPlanResult many = await tail.PlanAsync(rig.TailInput([.. Enumerable.Range(0, 21).Select(static i => "t" + i)]), null, Ct);
        KafkaTailPlanResult tooLong = await tail.PlanAsync(rig.TailInput(["orders"], new string('x', KafkaStudioText.MaxPredicateLength + 1)), null, Ct);

        many.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchTopicCount);
        tooLong.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.PredicateTooLong);
        rig.BrokerCalls.Should().Be(callsBefore);
        rig.Logs.Entries.Count(static x => x.EventId.Id == 9609).Should().Be(2);
    }

    [Fact]
    public async Task A_tail_session_is_one_audit_entry_whatever_the_number_of_its_segments()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment first, Task firstEnd) = await BeginAsync(tail, rig.TailInput(["orders"]));
        KafkaTailSession session = first.Session;

        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9607, "the start is logged when the first segment starts");
        rig.Ring.Should().BeEmpty("the ring entry is the session's, written when it ends");

        UntilCaughtUp(session);
        rig.Produce("orders", 0, 10, "k10", "v10");
        rig.Produce("orders", 0, 11, "k11", "v11");
        ScanFixture.Until(() => session.Ring.Count == 2, "two records arrived");
        first.Pause();
        await firstEnd.Bounded();

        session.LastStop.Should().Be(KafkaScanStopReason.Paused);
        session.Cursor!.Positions.Should().ContainSingle().Which.Next.Should().Be(5, "the cursor is exact: three seeded records, two arrived");
        session.Current.Should().BeNull("a pause frees the segment");

        // Resume: a new segment of the same session, from the cursor; nothing produced while paused is skipped.
        rig.Produce("orders", 0, 12, "k12", "v12");
        (KafkaTailSegment second, Task secondEnd) = await BeginAsync(tail, session.Input with { ResumeFrom = session.Cursor }, session);
        ScanFixture.Until(() => session.Ring.Count == 3, "the record produced while paused arrived");
        second.Stop();
        await secondEnd.Bounded();

        session.Segments.Should().Be(2);
        session.Ring.Select(static x => x.Offset).Should().Equal([5L, 4L, 3L], "newest first, every offset once");
        session.Records.Should().Be(3);
        rig.Ring.Should().BeEmpty("a pause and a resume write nothing");
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9608);

        session.End(KafkaScanStopReason.Cancelled);
        session.End(KafkaScanStopReason.Failed);
        await session.DisposeAsync();

        StudioActionLogEntry entry = rig.Ring.Should().ContainSingle("one entry per session, whatever its segments and however often it was told to end").Subject;
        entry.Action.Should().Be("LiveTail");
        entry.Capability.Should().Be("LiveTail");
        entry.Succeeded.Should().BeTrue();
        entry.Target.Should().Be("live tail of orders");
        entry.Message.Should().Contain("2 segments").And.Contain("3 records").And.Contain("3 matches").And.Contain("ended by Cancelled").And.Contain("predicate: ");
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9608).Should().ContainSingle().Which.Message.Should().Contain("in 2 segments").And.Contain("ended by Cancelled");
    }

    [Fact]
    public async Task The_typed_text_never_reaches_the_ring_or_the_log()
    {
        await using KafkaSearchRig rig = new(
            engine: o => o.Topic("orders", t => t.Redact(r => r.Header("customer-email").Searchable())),
            studio: o => o.Capabilities.LiveTail = true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k1", "v1", ("customer-email", Sentinel));
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment segment, Task end) = await BeginAsync(tail, rig.TailInput(["orders"], $"header.customer-email = '{Sentinel}'"));
        KafkaTailSession session = segment.Session;
        UntilCaughtUp(session);
        segment.Stop();
        await end.Bounded();
        session.AuditText.Should().Contain("<redacted>").And.NotContain(Sentinel);
        session.End(KafkaScanStopReason.Cancelled);

        // A text that does not parse is recorded by its length only.
        KafkaTailPlanResult.Planned invalid = (KafkaTailPlanResult.Planned) await tail.PlanAsync(rig.TailInput(["orders"], $"header.customer-email ~ '{Sentinel}'"), null, Ct);
        invalid.Value.Plan.IsValid.Should().BeFalse();

        string all = string.Join('\n', rig.Ring.Select(static x => string.Join('|', x.Target, x.Message, x.Action)))
            + string.Join('\n', rig.Logs.Entries.Select(static x => x.Message));
        all.Should().NotContain(Sentinel);
        rig.Logs.Entries.Single(static x => x.EventId.Id == 9609).Message.Should().Contain("a predicate that did not parse");
    }

    [Fact]
    public async Task A_resume_asks_every_gate_again_and_a_refusal_is_counted_in_the_sessions_entry()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment first, Task firstEnd) = await BeginAsync(tail, rig.TailInput(["orders"]));
        KafkaTailSession session = first.Session;
        UntilCaughtUp(session);
        first.Pause();
        await firstEnd.Bounded();

        // The capability was switched off while the tail was paused: the resume is refused where the operation is.
        rig.Studio.Options.Capabilities.LiveTail = false;
        KafkaTailPlanResult refused = await tail.PlanAsync(session.Input with { ResumeFrom = session.Cursor }, session, Ct);

        refused.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Contain("KafkaStudioOptions.Capabilities.LiveTail");
        session.RefusedResumes.Should().Be(1);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9602);

        rig.Studio.Options.Capabilities.LiveTail = true;
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "orders");
        KafkaTailPlanResult byPolicy = await tail.PlanAsync(session.Input with { ResumeFrom = session.Cursor }, session, Ct);

        byPolicy.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.RequestRefused);
        session.RefusedResumes.Should().Be(2);

        session.End(KafkaScanStopReason.Cancelled);
        rig.Ring.Should().Contain(static x => x.Message!.Contains("2 resumes refused", StringComparison.Ordinal), "the session's entry counts both");
    }

    [Fact]
    public async Task A_resume_the_engine_refuses_is_counted_and_writes_no_entry_or_9609_of_its_own()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment first, Task firstEnd) = await BeginAsync(tail, rig.TailInput(["orders"]));
        KafkaTailSession paused = first.Session;
        UntilCaughtUp(paused);
        first.Pause();
        await firstEnd.Bounded();

        // Another tail of the same visitor holds the one place a visitor has.
        (KafkaTailSegment other, Task otherEnd) = await BeginAsync(tail, rig.TailInput(["orders"]));
        KafkaTailSession busy = other.Session;
        int logged = rig.Logs.Entries.Count;

        KafkaTailPlanResult.Planned plan = (KafkaTailPlanResult.Planned) await tail.PlanAsync(paused.Input with { ResumeFrom = paused.Cursor }, paused, Ct);
        KafkaTailStartResult start = await tail.StartAsync(paused, plan.Value, Ct);

        start.Should().BeOfType<KafkaTailStartResult.Refused>().Which.Text.Should().Be(KafkaTailText.Refusal(KafkaScanRefusal.OwnerBusy, null));
        paused.RefusedResumes.Should().Be(1);
        rig.Logs.Entries.Skip(logged).Should().NotContain(static x => x.EventId.Id == 9609, "a refused resume is counted in the session's entry");
        rig.Ring.Should().BeEmpty();

        other.Stop();
        await otherEnd.Bounded();
        busy.End(KafkaScanStopReason.Cancelled);
        paused.End(KafkaScanStopReason.Cancelled);
        rig.Ring.Should().HaveCount(2).And.Contain(static x => x.Message!.Contains("1 resume refused: Another search or live tail of yours is running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_first_segment_the_engine_refuses_is_one_entry_and_9609_and_no_session()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment holder, Task holderEnd) = await BeginAsync(tail, rig.TailInput(["orders"]));
        UntilCaughtUp(holder.Session);
        int logged = rig.Logs.Entries.Count;
        int ringed = rig.Ring.Count;

        KafkaTailPlanResult.Planned plan = (KafkaTailPlanResult.Planned) await tail.PlanAsync(rig.TailInput(["orders"]), null, Ct);
        KafkaTailStartResult start = await tail.StartAsync(null, plan.Value, Ct);

        start.Should().BeOfType<KafkaTailStartResult.Refused>();
        rig.Logs.Entries.Skip(logged).Should().ContainSingle(static x => x.EventId.Id == 9609);
        rig.Logs.Entries.Skip(logged).Should().NotContain(static x => x.EventId.Id == 9607, "no tail started");
        rig.Ring.Skip(ringed).Should().ContainSingle(static x => !x.Succeeded && x.Action == "LiveTail");

        holder.Stop();
        await holderEnd.Bounded();
        await holder.Session.DisposeAsync();
    }

    [Fact]
    public async Task A_start_refused_for_the_allowance_names_the_allowance_that_is_spent_the_clusters_or_the_processs()
    {
        const long MiB = 1024 * 1024;
        const long GiB = 1024 * MiB;

        // The process allows 50 GiB an hour and a Test cluster caps its own at 2 GiB.
        await using KafkaSearchRig rig = new(engine: o => o.Limits.OwnerBytesPerHour = 50 * GiB, studio: o => o.Capabilities.LiveTail = true, profile: KafkaProfile.Test);
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        // A named visitor: an anonymous one is an owner made once per service instance, and the budget is the engine's per owner.
        rig.Studio.Visitor.SignIn("ann");
        string owner = (await new KafkaOwnerKey(rig.Studio.Visitor).OwnerAsync()).Key;
        KafkaTailPlanned planned = ((KafkaTailPlanResult.Planned) await tail.PlanAsync(rig.TailInput(["orders"]), null, Ct)).Value;
        KafkaClusterRegistry registry = rig.Engine.Provider.GetRequiredService<KafkaClusterRegistry>();

        rig.Engine.Provider.GetRequiredService<KafkaClusterCostControls>().For(registry.Get(rig.Entry.Key)).OwnerBudget!.Add(owner, 2 * GiB);
        KafkaTailStartResult cluster = await tail.StartAsync(null, planned, Ct);

        cluster.Should().BeOfType<KafkaTailStartResult.Refused>().Which.Text.Should().Be("You have read your hourly allowance on this cluster of 2 GiB. It frees up as the hour rolls on.");

        rig.Engine.Provider.GetRequiredService<OwnerByteBudget>().Add(owner, 50 * GiB);
        KafkaTailStartResult process = await tail.StartAsync(null, planned, Ct);

        process.Should().BeOfType<KafkaTailStartResult.Refused>().Which.Text.Should().Be("You have read your hourly allowance of 50 GiB. It frees up as the hour rolls on.");
        rig.Logs.Entries.Count(static x => x.EventId.Id == 9609).Should().Be(2);
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9607);
    }

    [Fact]
    public async Task A_resume_whose_grant_is_lower_than_the_first_segments_is_refused()
    {
        await using KafkaSearchRig rig = Rig(payloads: true);
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment first, Task firstEnd) = await BeginAsync(tail, rig.TailInput(["orders"]));
        KafkaTailSession session = first.Session;
        session.Grant.HasPayload.Should().BeTrue();
        UntilCaughtUp(session);
        first.Pause();
        await firstEnd.Bounded();

        rig.Studio.Options.Capabilities.ReadPayloads = false;
        KafkaTailPlanResult.Planned plan = (KafkaTailPlanResult.Planned) await tail.PlanAsync(session.Input with { ResumeFrom = session.Cursor }, session, Ct);
        KafkaTailStartResult start = await tail.StartAsync(session, plan.Value, Ct);

        start.Should().BeOfType<KafkaTailStartResult.Refused>().Which.Text.Should().Be(KafkaStudioText.TailGrantChanged);
        session.RefusedResumes.Should().Be(1);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Jumping_to_now_skips_exactly_what_was_produced_while_paused()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment first, Task firstEnd) = await BeginAsync(tail, rig.TailInput(["orders"]));
        KafkaTailSession session = first.Session;
        UntilCaughtUp(session);
        first.Pause();
        await firstEnd.Bounded();

        for (int i = 0; i < 4; i++)
        {
            rig.Produce("orders", 0, 20 + i, "p" + i, "v");
        }

        KafkaTailPlanResult.Planned fromEnd = (KafkaTailPlanResult.Planned) await tail.PlanAsync(session.Input, session, Ct);

        KafkaTailService.SkippedByJumping(session, fromEnd.Value.Plan).Should().Be(4);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_a_session_while_a_segment_runs_adds_what_it_read_and_writes_the_entry_once()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        KafkaTailService tail = rig.Tail();

        (KafkaTailSegment segment, Task end) = await BeginAsync(tail, rig.TailInput(["orders"]));
        KafkaTailSession session = segment.Session;
        UntilCaughtUp(session);
        rig.Produce("orders", 0, 30, "k30", "v30");
        ScanFixture.Until(() => session.Ring.Count == 1, "the record arrived");

        // The page is left mid-tail: the session stops the segment, waits for its result and writes its one entry after it.
        await session.DisposeAsync();
        await end.Bounded();

        StudioActionLogEntry entry = rig.Ring.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("1 segment over").And.Contain("1 record and").And.Contain("ended by CallerGone");
        session.IsEnded.Should().BeTrue();
    }
}
