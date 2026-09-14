using MartenStudio.Services;
using MartenStudio.Services.Live;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// What the sidebar's badges are, where they come from, and what they do when they cannot be read.
/// </summary>
/// <remarks>
/// The navigation is on every page, so this is the one service in the studio whose failure mode matters
/// more than its answer: a badge that could throw would be a decoration taking navigation down with it.
/// Everything here answers <see cref="NavIndicators.None" /> rather than raising, and the whole answer
/// goes through the snapshot cache so that N circuits are one pair of reads per interval (plan D10).
/// </remarks>
public class NavIndicatorServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nothing_is_drawn_before_a_scope_has_been_settled()
    {
        Harness harness = new();

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.Should().Be(NavIndicators.None);
        harness.Events.DeadLetterCountReads.Should().Be(0, "the services were never asked");
        harness.Projections.Reads.Should().Be(0);
    }

    [Fact]
    public async Task The_dead_letter_count_comes_from_the_events_service()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Events.DeadLetterCount = 7;

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.DeadLetters.Should().Be(7);
        indicators.ShowDeadLetterCount.Should().BeTrue();
    }

    [Fact]
    public async Task A_count_that_could_not_be_read_is_null_rather_than_zero()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Events.DeadLetterCount = null;

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.DeadLetters.Should().BeNull();
        indicators.ShowDeadLetterCount.Should().BeFalse();
    }

    [Fact]
    public async Task A_shard_past_the_amber_threshold_raises_the_dot_and_names_itself()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.HighWaterMark = 50_000;
        harness.Projections.WithProjection("OrderSummary", sequence: 0);

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.ProjectionsNeedAttention.Should().BeTrue();
        indicators.ProjectionsExplanation.Should().Contain("OrderSummary:All").And.Contain("50,000 events behind");
    }

    [Fact]
    public async Task A_paused_shard_raises_the_dot_even_though_it_is_not_behind()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.WithProjection("OrderSummary", sequence: 1_000, pauseReason: "it threw");

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.ProjectionsNeedAttention.Should().BeTrue();
        indicators.ProjectionsExplanation.Should().Contain("paused");
    }

    /// <summary>
    /// A host that runs its daemon in another process is a supported deployment (hard rule 11). Marking
    /// it in the sidebar would be telling every visitor to fix something that is not broken.
    /// </summary>
    [Fact]
    public async Task A_daemon_that_is_not_hosted_here_is_not_by_itself_worth_a_dot()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.WithNoDaemonHere();
        harness.Projections.WithProjection("OrderSummary", sequence: 1_000);

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.ProjectionsNeedAttention.Should().BeFalse();
    }

    [Fact]
    public async Task A_projection_read_that_failed_draws_no_dot_and_does_not_throw()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.Failure = new InvalidOperationException("the progression table is gone");
        harness.Events.DeadLetterCount = 4;

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        // GetSummaryAsync answers "cannot report" as a value, so the dead-letter count survives it.
        indicators.ProjectionsNeedAttention.Should().BeFalse();
        indicators.DeadLetters.Should().Be(4);
    }

    /// <summary>
    /// The real events service answers "could not tell" as a value, so this is the belt above the braces:
    /// one that threw anyway must still leave a sidebar that renders.
    /// </summary>
    [Fact]
    public async Task An_events_service_that_throws_outright_costs_the_badges_and_nothing_else()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Events.CountFailure = new InvalidOperationException("the connection was refused");

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.Should().Be(NavIndicators.None);
    }

    /// <summary>
    /// The sidebar is on every page and every circuit polls it, so the whole answer is cached under one
    /// key: five tabs must cost one pair of reads per interval rather than ten.
    /// </summary>
    [Fact]
    public async Task The_answer_is_cached_so_that_many_circuits_cost_one_pair_of_reads()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.WithProjection("OrderSummary");

        await harness.Service.ReadAsync(Token);
        await harness.Service.ReadAsync(Token);
        await harness.Service.ReadAsync(Token);

        harness.Projections.Reads.Should().Be(1);
        harness.Events.DeadLetterCountReads.Should().Be(1);
    }

    /// <summary>The service under test, with its scope, its two data services and a real cache.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            Catalog = new FakeStudioScopeCatalog();
            Events = new FakeEventDataService();
            Projections = new FakeProjectionDataService();

            State = new StudioState(Catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());

            Service = new NavIndicatorService(
                State,
                Events,
                Projections,
                new StudioSnapshotCache(),
                NullLogger<NavIndicatorService>.Instance);
        }

        public FakeStudioScopeCatalog Catalog { get; }

        public FakeEventDataService Events { get; }

        public FakeProjectionDataService Projections { get; }

        public StudioState State { get; }

        public NavIndicatorService Service { get; }

        public static async Task<Harness> ReadyAsync()
        {
            var harness = new Harness();
            harness.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");
            await harness.State.EnsureInitializedAsync(TestContext.Current.CancellationToken);
            return harness;
        }
    }
}
