using Marten;

using MartenStudio.Services.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The two facts P5-fix-2 turns on: which progression rows are Marten's own bookkeeping, and when a
/// per-agent control can mean what it says.
/// </summary>
/// <remarks>
/// No database and no daemon. The predicate is a pure function over a string, the daemon card's control
/// state is a pure function over a DTO, and the store's polling interval is readable off a store that
/// <c>DocumentStore.For</c> builds without ever opening a connection.
/// </remarks>
public class DaemonControlTests
{
    // --------------------------------------------------------------------------------------------
    // Bookkeeping rows
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// Every name Marten writes into <c>mt_event_progression</c> that is not a shard.
    /// </summary>
    /// <remarks>
    /// <c>HighWaterMark</c> and <c>HighWaterMark:{tenant}</c> come from <c>ShardName.Identity</c>, which
    /// hard-codes both; <c>HighWaterAllocationFence</c> and <c>HighWaterStuckGap</c> are
    /// <c>HighWaterStatisticsDetector</c>'s two <c>ProgressionName</c> constants. The stuck-gap row
    /// trails the mark by the detection gap <em>by design</em>, so a table sorted by lag descending put
    /// a projection that does not exist at the top of the operations screen.
    /// </remarks>
    [Theory]
    [InlineData("HighWaterMark")]
    [InlineData("HighWaterMark:acme")]
    [InlineData("HighWaterMark:acme_corp")]
    [InlineData("HighWaterAllocationFence")]
    [InlineData("HighWaterStuckGap")]
    [InlineData("SomethingMartenAddsLater")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_progression_name_without_a_shard_key_is_bookkeeping(string progressionName) =>
        ProjectionDataService.IsBookkeepingRow(progressionName).Should().BeTrue();

    /// <summary>
    /// And every shard identity <c>ShardName</c> can compose is not.
    /// </summary>
    /// <remarks>
    /// All three grammars, because the general rule is "a real shard carries a colon" and a version
    /// marker or a tenant slot must not accidentally look like bookkeeping - nor must a projection
    /// somebody called <c>HighWaterMarkAudit</c>, whose shard is <c>HighWaterMarkAudit:All</c> and which
    /// the prefix rule would eat if it were not anchored on the colon.
    /// </remarks>
    [Theory]
    [InlineData("DailySales:All")]
    [InlineData("DailySales:All:acme")]
    [InlineData("DailySales:V2:All")]
    [InlineData("DailySales:V2:All:acme")]
    [InlineData("Name:V2:All")]
    [InlineData("Name:All:tenant")]
    [InlineData("HighWaterMarkAudit:All")]
    public void A_real_shard_identity_is_never_bookkeeping(string shardName) =>
        ProjectionDataService.IsBookkeepingRow(shardName).Should().BeFalse();

    // --------------------------------------------------------------------------------------------
    // The daemon card's control state
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void A_running_coordinator_refuses_per_agent_control_and_quotes_the_stores_own_interval()
    {
        DaemonStatus status = Hosted() with { LeadershipPollingMilliseconds = 1_500 };

        status.IsPausedByStudio.Should().BeFalse();
        status.CanControlAgents.Should().BeFalse();
        status.LeadershipPollingTime.Should().Be(TimeSpan.FromMilliseconds(1_500));

        status.AgentControlRefusal.Should()
            .Be("The projection coordinator restarts agents every 1.5 s (LeadershipPollingTime); pause the daemon first.");
    }

    [Fact]
    public void A_studio_pause_allows_per_agent_control_and_stops_explaining_why_it_would_not()
    {
        DaemonStatus status = Hosted() with
        {
            IsRunning = false,
            PausedByStudio = new StudioDaemonPause("default", "admin", DateTimeOffset.UnixEpoch, "localhost.marten", null)
        };

        status.IsPausedByStudio.Should().BeTrue();
        status.CanControlAgents.Should().BeTrue();
        status.AgentControlRefusal.Should().BeNull();
    }

    /// <summary>
    /// With no daemon here the card already says so, and one disabled button must not carry two reasons.
    /// </summary>
    [Fact]
    public void A_daemon_that_is_somewhere_else_has_no_coordinator_reason_of_its_own()
    {
        DaemonStatus status = new(
            DaemonHostingState.NotHostedInThisProcess, false, "Disabled", [], false, null,
            DaemonAccessor.NotRegisteredExplanation);

        status.CanControlAgents.Should().BeFalse();
        status.AgentControlRefusal.Should().BeNull();
        status.IsPausedByStudio.Should().BeFalse();
    }

    /// <summary>The default is JasperFx's own, so a status nobody enriched still quotes a real number.</summary>
    [Fact]
    public void The_polling_interval_defaults_to_the_documented_five_seconds()
    {
        Hosted().LeadershipPollingTime.Should().Be(TimeSpan.FromSeconds(5));
        Hosted().Databases.Should().BeEmpty("a status that named none names none, rather than throwing");
    }

    // --------------------------------------------------------------------------------------------
    // The pause register
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void A_pause_is_remembered_per_store_and_forgotten_on_resume()
    {
        DaemonControlState state = new();

        state.Find("default").Should().BeNull();

        state.RecordPause(new StudioDaemonPause("default", "admin", DateTimeOffset.UnixEpoch, "localhost.marten", null));
        state.RecordPause(new StudioDaemonPause("other", "someone", DateTimeOffset.UnixEpoch, "other.marten", "acme"));

        state.Find("DEFAULT")!.User.Should().Be("admin", "a store key is matched the way every other one is");
        state.Find("other")!.TenantId.Should().Be("acme");
        state.Count.Should().Be(2);

        state.ClearPause("default").Should().BeTrue();
        state.ClearPause("default").Should().BeFalse();
        state.Find("default").Should().BeNull();
        state.Find("other").Should().NotBeNull("one store's resume is not another store's");
    }

    [Fact]
    public void Pausing_twice_records_the_second_person()
    {
        DaemonControlState state = new();

        state.RecordPause(new StudioDaemonPause("default", "first", DateTimeOffset.UnixEpoch, "localhost.marten", null));
        state.RecordPause(new StudioDaemonPause("default", "second", DateTimeOffset.UnixEpoch, "localhost.marten", null));

        state.Find("default")!.User.Should().Be("second");
        state.Count.Should().Be(1);
    }

    [Fact]
    public void Nothing_is_remembered_for_a_store_key_that_is_not_one()
    {
        DaemonControlState state = new();

        state.Find(null).Should().BeNull();
        state.Find(string.Empty).Should().BeNull();
        state.ClearPause(null).Should().BeFalse();
    }

    // --------------------------------------------------------------------------------------------
    // Reading the interval off a real store
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// The route to <c>LeadershipPollingTime</c> is not the one the plan named, so it is proved here
    /// against a real Marten store rather than assumed.
    /// </summary>
    /// <remarks>
    /// <c>DocumentStore.For</c> never opens a connection. <c>IDocumentStore.Options</c> is
    /// <c>IReadOnlyStoreOptions</c>, which has no <c>Projections</c> member at all, and
    /// <c>IReadOnlyDaemonSettings</c> does not surface the interval either - what works is that
    /// <c>Events.Daemon</c> <em>is</em> the store's <c>ProjectionOptions</c>, which derives from
    /// <c>DaemonSettings</c>. <c>MartenApiSurfaceTest</c> pins that identity.
    /// </remarks>
    [Fact]
    public void The_stores_configured_polling_interval_is_what_the_hint_quotes()
    {
        using IDocumentStore configured = DocumentStore.For(options =>
        {
            options.Connection(NeverConnected);
            options.Projections.LeadershipPollingTime = 1_000;
        });

        ProjectionDataService.LeadershipPollingMillisecondsOf(configured).Should().Be(1_000);
        ProjectionDataService.LeadershipPollingTimeOf(configured).Should().Be(TimeSpan.FromSeconds(1));

        using IDocumentStore untouched = DocumentStore.For(options => options.Connection(NeverConnected));

        ProjectionDataService.LeadershipPollingMillisecondsOf(untouched)
            .Should().Be(DaemonDefaults.LeadershipPollingMilliseconds);
    }

    private const string NeverConnected =
        "Host=marten-studio-daemon-control-test.invalid;Database=none;Username=none;Password=none";

    private static DaemonStatus Hosted() => new(
        DaemonHostingState.Hosted, true, "Solo", [], false, DateTimeOffset.UnixEpoch,
        "The async daemon is hosted in this process.");
}
