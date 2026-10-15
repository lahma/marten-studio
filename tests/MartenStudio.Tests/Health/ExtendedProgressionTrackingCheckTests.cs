using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Subscriptions;

using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>K02's judgement, over stated facts, and what it reads to decide whether the daemon has work.</summary>
public class ExtendedProgressionTrackingCheckTests
{
    private static readonly HealthEvaluation Evaluation = HealthEvaluation.At(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

    private static HealthVerdict Evaluate(bool? hasAsyncWork, bool enabled, int? shards = 2) =>
        new ExtendedProgressionTrackingCheck().Evaluate(new AsyncTrackingFacts(hasAsyncWork, shards, enabled), Evaluation);

    [Fact]
    public void Async_work_with_the_tracking_off_is_one_info_finding()
    {
        HealthFinding finding = Evaluate(hasAsyncWork: true, enabled: false).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("K02");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(new HealthEvidence("Events.EnableExtendedProgressionTracking", "false"));
        finding.Evidence.Should().Contain(new HealthEvidence("Async projections and subscriptions", "2 shards"));
        finding.Remedy!.Code.Should().Be("opts.Events.EnableExtendedProgressionTracking = true;");
    }

    /// <summary>
    /// H1-fix item 4: K02 claims only what the flag makes Marten write. It does not make anything write the two
    /// behind-threshold columns (marten#5173), and the heartbeat is written at state transitions only unless a
    /// compatibility interval is set (jasperfx#622) - so the finding promises neither lag thresholds nor telling
    /// an idle shard from a stopped one, and does not suggest the interval.
    /// </summary>
    [Fact]
    public void The_finding_promises_only_what_the_flag_makes_Marten_write()
    {
        HealthFinding finding = Evaluate(hasAsyncWork: true, enabled: false).Findings.Single();
        string said = finding.Title + " " + finding.WhyItMatters + " " + finding.Remedy!.Code + " " + finding.Remedy.Explanation;

        said.Should().NotContainAny(["threshold", "alive", "idle", "liveness", "HeartbeatInterval"]);
        said.Should().Contain("status").And.Contain("paused").And.Contain("node").And.Contain("failure");
    }

    [Fact]
    public void A_shard_count_the_options_could_not_give_is_said_as_at_least_one()
    {
        Evaluate(hasAsyncWork: true, enabled: false, shards: null).Findings.Single().Evidence
            .Should().Contain(new HealthEvidence("Async projections and subscriptions", "at least one"));
    }

    [Fact]
    public void The_tracking_on_is_quiet()
    {
        Evaluate(hasAsyncWork: true, enabled: true).Findings.Should().BeEmpty();
    }

    /// <summary>No shard, nothing to track: an Info that told such a store to turn it on would be crying wolf.</summary>
    [Fact]
    public void A_store_with_nothing_for_the_daemon_to_run_is_quiet()
    {
        HealthVerdict verdict = Evaluate(hasAsyncWork: false, enabled: false, shards: 0);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("no async projection");
    }

    /// <summary>H1-fix item 4: a store whose projections could not be read is not assumed to have async work.</summary>
    [Fact]
    public void A_store_whose_projections_could_not_be_read_is_quiet_and_says_so()
    {
        HealthVerdict verdict = Evaluate(hasAsyncWork: null, enabled: false, shards: null);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be(AsyncTrackingFacts.UnreadableNote);
    }

    /// <summary>A subscription is async work: <c>Events.Subscribe(...)</c> lives apart from the projection list.</summary>
    [Fact]
    public void A_subscription_alone_is_async_work_and_no_registration_at_all_is_none()
    {
        using DocumentStore subscribed = DocumentStore.For(options =>
        {
            options.Connection(HealthHarness.RefusedConnectionString);
            options.Events.Subscribe(new NoOpSubscription());
        });

        using DocumentStore bare = DocumentStore.For(options => options.Connection(HealthHarness.RefusedConnectionString));

        AsyncTrackingFacts.AsyncWorkOf(subscribed).Should().BeTrue();
        AsyncTrackingFacts.AsyncWorkOf(bare).Should().BeFalse();
    }

    private sealed class NoOpSubscription : SubscriptionBase
    {
        public override Task<IChangeListener> ProcessEventsAsync(
            EventRange page,
            ISubscriptionController controller,
            IDocumentOperations operations,
            CancellationToken cancellationToken) =>
            Task.FromResult(NullChangeListener.Instance);
    }
}
