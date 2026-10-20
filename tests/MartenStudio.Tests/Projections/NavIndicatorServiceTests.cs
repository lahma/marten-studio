using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Settings;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// What the sidebar's badges are, where they come from, and what they do when they cannot be read.
/// </summary>
/// <remarks>
/// The navigation is on every page, so this is the one service in the studio whose failure mode matters
/// more than its answer: a badge that could throw would be a decoration taking navigation down with it.
/// Everything here answers <see cref="NavIndicators.None" /> rather than raising, and the whole answer
/// goes through the snapshot cache so that N circuits are one pair of reads per interval (plan D10). The badges are
/// MartenStudio's contributor's (<see cref="MartenNavIndicatorContributor" />), read through the shared service, so
/// these are the contributor's numbers and the service's caching and authorization at once; where "zero" and "could
/// not tell" have to be told apart, which no badge does, the contributor's own reading is asked.
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

        DeadLetterBadge(indicators).Should().Be("7");
    }

    [Fact]
    public async Task A_count_that_could_not_be_read_is_null_rather_than_zero()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Events.DeadLetterCount = null;

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        DeadLetterBadge(indicators).Should().BeNull("nobody could tell draws no badge");
        (await harness.ReadingAsync()).DeadLetters.Should().BeNull("could not tell is not zero");
    }

    [Fact]
    public async Task A_shard_past_the_amber_threshold_raises_the_dot_and_names_itself()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.HighWaterMark = 50_000;
        harness.Projections.WithProjection("OrderSummary", sequence: 0);

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        ProjectionsDot(indicators).Should().NotBeNull();
        ProjectionsDot(indicators)!.Title.Should().Contain("OrderSummary:All").And.Contain("50,000 events behind");
    }

    [Fact]
    public async Task A_paused_shard_raises_the_dot_even_though_it_is_not_behind()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.WithProjection("OrderSummary", sequence: 1_000, pauseReason: "it threw");

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        ProjectionsDot(indicators).Should().NotBeNull();
        ProjectionsDot(indicators)!.Title.Should().Contain("paused");
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

        ProjectionsDot(indicators).Should().BeNull();
    }

    [Fact]
    public async Task A_projection_read_that_failed_draws_no_dot_and_does_not_throw()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Projections.Failure = new InvalidOperationException("the progression table is gone");
        harness.Events.DeadLetterCount = 4;

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        // GetSummaryAsync answers "cannot report" as a value, so the dead-letter count survives it.
        ProjectionsDot(indicators).Should().BeNull();
        DeadLetterBadge(indicators).Should().Be("4");
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

    // -----------------------------------------------------------------------------------------------
    // Hard rule 14: the sidebar is on every route, so it is the worst possible place to apply a
    // migration from.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A database with no event tables is never asked about projections.
    /// </summary>
    /// <remarks>
    /// <c>GetSummaryAsync</c> reaches <c>FetchHighestEventSequenceNumber</c> and
    /// <c>AllProjectionProgress</c>, and both open with <c>EnsureStorageExistsAsync(typeof(IEvent))</c> -
    /// a Weasel migration of the event store under the database's own <c>AutoCreate</c>. The badge runs on
    /// every route, during prerender and again on the interactive render, and then once per refresh
    /// interval, so an ungated one would mean a host got an event store because somebody opened
    /// <c>/marten/documents</c>. <c>NavIndicatorNoDdlLiveTests</c> is the same statement against a real
    /// Postgres.
    /// </remarks>
    [Fact]
    public async Task A_database_with_no_event_tables_is_never_asked_about_projections()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Events.Shape = new EventStoreShape { EventTablesExist = false };
        harness.Events.DeadLetterCount = 0;
        harness.Projections.WithProjection("OrderSummary", sequence: 0);
        harness.Projections.HighWaterMark = 50_000;

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        harness.Projections.Reads.Should().Be(0, "reading the progression would have created the event store");

        indicators.Badges.Should().BeEmpty();

        // The dead-letter half still answers: its own read is an information_schema question and a
        // database with no dead-letter table is a real zero.
        MartenNavReading reading = await harness.ReadingAsync();
        reading.DeadLetters.Should().Be(0);
        reading.ProjectionsNeedAttention.Should().BeFalse();
        reading.ProjectionsExplanation.Should().BeNull();
        harness.Projections.Reads.Should().Be(0, "the contributor's own reading is gated the same way");
    }

    /// <summary>
    /// A shape that could not be read is treated as a database with no event store.
    /// </summary>
    /// <remarks>
    /// The conservative way round, and deliberately: the only thing on the other side of that branch is a
    /// call that can write DDL, and "I could not tell" is not a reason to run one.
    /// </remarks>
    [Fact]
    public async Task An_event_store_that_could_not_be_described_is_not_asked_about_projections_either()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Events.Shape = EventStoreShape.Unavailable(new EventDataError("connection refused", null, true));
        harness.Projections.WithProjection("OrderSummary", sequence: 0);

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        harness.Projections.Reads.Should().Be(0);
        ProjectionsDot(indicators).Should().BeNull();
    }

    // -----------------------------------------------------------------------------------------------
    // D5: the store policy decides, and a cache must never be the thing that answers instead of it.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A scope the store policy refuses is refused before the cache is reached.
    /// </summary>
    /// <remarks>
    /// <see cref="StudioSnapshotCache" /> is a process-wide singleton keyed on the scope alone, and its
    /// own contract says why that is safe: every caller resolves - and therefore authorizes - its scope
    /// before it asks. A cache hit skips the factory, so a check made inside would be a check the second
    /// circuit never ran. Nothing is read, and nothing is cached either: a refusal must not leave an
    /// entry behind for the next visitor to hit.
    /// </remarks>
    [Fact]
    public async Task A_scope_the_store_policy_refuses_is_refused_before_the_cache_is_asked()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.WithPolicy();
        harness.Authorization.DenyEverything();
        harness.Events.DeadLetterCount = 7;
        harness.Projections.WithProjection("OrderSummary", sequence: 0);

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        indicators.Should().Be(NavIndicators.None);
        harness.Events.DeadLetterCountReads.Should().Be(0, "the refusal came before anything was read");
        harness.Projections.Reads.Should().Be(0);
        harness.Cache.Count.Should().Be(0, "a refusal must not leave an entry for the next visitor");
    }

    /// <summary>
    /// The same visitor, allowed: the policy is asked, and asked as a read rather than as a write.
    /// </summary>
    [Fact]
    public async Task An_allowed_scope_is_asked_as_a_read_and_then_answered()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.WithPolicy();
        harness.Authorization.AllowStores("default");
        harness.Events.DeadLetterCount = 2;

        NavIndicators indicators = await harness.Service.ReadAsync(Token);

        DeadLetterBadge(indicators).Should().Be("2");

        harness.Authorization.Calls.Should().Contain(x =>
            x.Policy == Harness.PolicyName
            && x.Resource.StoreName == "default"
            && x.Resource.Capability == null);
    }

    /// <summary>
    /// A cached answer is still only handed to somebody the policy has just passed.
    /// </summary>
    /// <remarks>
    /// The point of the previous test in reverse: the cache still saves the reads, but it never saves the
    /// question. Two calls, one pair of reads, two authorizations.
    /// </remarks>
    [Fact]
    public async Task A_cache_hit_still_costs_an_authorization()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.WithPolicy();
        harness.Authorization.AllowStores("default");

        await harness.Service.ReadAsync(Token);
        await harness.Service.ReadAsync(Token);

        harness.Events.DeadLetterCountReads.Should().Be(1, "the reads are cached");
        harness.Authorization.Calls.Should().HaveCount(2, "the question is not");
    }

    // -----------------------------------------------------------------------------------------------
    // PS-1d: the shared service draws what its contributors read, and nothing of its own beyond health.
    // -----------------------------------------------------------------------------------------------

    /// <summary>A studio with no contributor - a plain data source's - draws no badge beyond health, and asks nothing.</summary>
    [Fact]
    public async Task A_studio_with_no_contributor_draws_no_badge_and_reads_nothing()
    {
        Harness harness = await Harness.ReadyAsync();
        harness.Events.DeadLetterCount = 7;

        NavIndicators indicators = await harness.With().ReadAsync(Token);

        indicators.Badges.Should().BeEmpty();
        harness.Events.DeadLetterCountReads.Should().Be(0, "nothing but a contributor reads a badge's number");
    }

    /// <summary>Every contributor is asked, in registration order, and a key two of them answer is the first one's.</summary>
    [Fact]
    public async Task Every_contributor_is_asked_and_the_first_to_answer_a_key_wins()
    {
        Harness harness = await Harness.ReadyAsync();
        var first = new StubContributor(("one", "first"));
        var second = new StubContributor(("one", "second"), ("two", "second"));

        NavIndicators indicators = await harness.With(first, second).ReadAsync(Token);

        indicators.Badges["one"].Text.Should().Be("first");
        indicators.Badges["two"].Text.Should().Be("second");
        first.Reads.Should().Be(1);
        second.Reads.Should().Be(1);
    }

    /// <summary>A contributor that throws takes the whole read's badges with it, and caches nothing.</summary>
    [Fact]
    public async Task A_contributor_that_throws_costs_every_badge_of_that_read_and_is_not_cached()
    {
        Harness harness = await Harness.ReadyAsync();
        var fine = new StubContributor(("one", "fine"));
        var broken = new StubContributor { Failure = new InvalidOperationException("gone") };

        NavIndicators indicators = await harness.With(fine, broken).ReadAsync(Token);

        indicators.Should().Be(NavIndicators.None);
        harness.Cache.Count.Should().Be(0, "a failed read must not be what the next circuit is handed");
    }

    /// <summary>A contributor that answers what it is told to.</summary>
    private sealed class StubContributor(params (string Key, string Text)[] badges) : MartenStudio.Services.Sources.INavIndicatorContributor
    {
        public Exception? Failure { get; init; }

        public int Reads { get; private set; }

        public Task<IReadOnlyDictionary<string, StudioNavBadge>> ReadAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Reads++;

            if (Failure is not null)
            {
                return Task.FromException<IReadOnlyDictionary<string, StudioNavBadge>>(Failure);
            }

            return Task.FromResult<IReadOnlyDictionary<string, StudioNavBadge>>(
                badges.ToDictionary(static x => x.Key, static x => new StudioNavBadge(x.Text, "ms-nav-badge", x.Text), StringComparer.Ordinal));
        }
    }

    /// <summary>The dead-letter entry's badge text, or <see langword="null" /> for no badge.</summary>
    private static string? DeadLetterBadge(NavIndicators indicators) =>
        indicators.Badges.TryGetValue(MartenStudioDescriptor.DeadLettersBadge, out StudioNavBadge? badge) ? badge.Text : null;

    /// <summary>The projections entry's dot, or <see langword="null" /> for none.</summary>
    private static StudioNavBadge? ProjectionsDot(NavIndicators indicators) =>
        indicators.Badges.TryGetValue(MartenStudioDescriptor.ProjectionsBadge, out StudioNavBadge? badge) ? badge : null;

    /// <summary>The service under test, with its scope, MartenStudio's contributor over two fake data services, and a real cache.</summary>
    private sealed class Harness
    {
        /// <summary>The per-store policy name <see cref="WithPolicy" /> configures.</summary>
        public const string PolicyName = "MartenStoreOwner";

        private readonly MartenStudioOptions options = new();

        public Harness()
        {
            Catalog = new FakeStudioScopeCatalog();
            Events = new FakeEventDataService();
            Projections = new FakeProjectionDataService();
            Authorization = new TestStoreAuthorizationService();
            AuthenticationState = new TestAuthenticationStateProvider();
            Cache = new StudioSnapshotCache();

            AuthenticationState.SignIn("operator");

            State = new StudioState(Catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());

            Contributor = new MartenNavIndicatorContributor(Events, Projections);

            Service = With(Contributor);
        }

        /// <summary>The same service - same circuit, policy and cache - over other contributors.</summary>
        public NavIndicatorService With(params MartenStudio.Services.Sources.INavIndicatorContributor[] contributors) =>
            new(
                State,
                MartenSpine.Authorization(Options.Create(options), Authorization, AuthenticationState),
                contributors,
                Cache,
                NullLogger<NavIndicatorService>.Instance);

        public FakeStudioScopeCatalog Catalog { get; }

        public FakeEventDataService Events { get; }

        public FakeProjectionDataService Projections { get; }

        /// <summary>The policy engine, and the record of what it was asked.</summary>
        public TestStoreAuthorizationService Authorization { get; }

        /// <summary>Who the circuit belongs to.</summary>
        public TestAuthenticationStateProvider AuthenticationState { get; }

        /// <summary>The real cache, so a test can say what is in it.</summary>
        public StudioSnapshotCache Cache { get; }

        public StudioState State { get; }

        public NavIndicatorService Service { get; }

        /// <summary>MartenStudio's contributor, which the service asks for the two badges.</summary>
        public MartenNavIndicatorContributor Contributor { get; }

        /// <summary>The contributor's own numbers for the active scope, straight from the fakes - no cache, no policy.</summary>
        public Task<MartenNavReading> ReadingAsync() =>
            Contributor.ReadNumbersAsync(State.ActiveScope!, TestContext.Current.CancellationToken);

        /// <summary>Turns the per-store policy on, the way a host configures one.</summary>
        public Harness WithPolicy()
        {
            options.StoreAuthorizationPolicy = PolicyName;
            return this;
        }

        public static async Task<Harness> ReadyAsync()
        {
            var harness = new Harness();
            harness.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");
            await harness.State.EnsureInitializedAsync(TestContext.Current.CancellationToken);
            return harness;
        }
    }
}
