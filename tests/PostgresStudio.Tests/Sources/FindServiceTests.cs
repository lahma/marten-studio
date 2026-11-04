using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging.Abstractions;
using PostgresStudio.Services;
using PostgresStudio.Services.Find;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// 1.2-SCOPE-3: the circuit's Find - every contributor whose kind the value fits, in order, inside a ten-second budget, one Find at a
/// time - over contributors a test writes outright. The clock is the test's own (<see cref="ManualTimeProvider" />), so the budget is
/// proven by moving it and never by waiting.
/// </summary>
public class FindServiceTests
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static FindService Service(
        TimeProvider? time = null,
        TestSourceAuthorizationService? authorization = null,
        PostgresStudioOptions? options = null,
        params IFindContributor[] contributors)
    {
        PostgresStudioSettings settings = new(Microsoft.Extensions.Options.Options.Create(options ?? new PostgresStudioOptions()));
        StudioAuthorization gate = new(settings, PostgresStudioDescriptor.Instance, authorization ?? new TestSourceAuthorizationService(), new TestAuthenticationStateProvider());

        return new FindService(contributors, gate, NullLogger<FindService>.Instance, time);
    }

    [Fact]
    public async Task Contributors_run_in_their_own_order_and_the_hits_follow_it()
    {
        FakeFindContributor third = new FakeFindContributor(30, "Third").Returns("thirds", null, FakeFindContributor.Hit(Scope, "Third", "3"));
        FakeFindContributor first = new FakeFindContributor(10, "First").Returns("firsts", null, FakeFindContributor.Hit(Scope, "First", "1"));
        FakeFindContributor second = new FakeFindContributor(20, "Second").Returns("seconds", null, FakeFindContributor.Hit(Scope, "Second", "2"));

        FindResult result = await Service(contributors: [third, first, second]).FindAsync(Scope, "abc", TestContext.Current.CancellationToken);

        result.Hits.Select(static x => x.Kind).Should().Equal("First", "Second", "Third");
        result.Searched.Should().Equal("firsts", "seconds", "thirds");
    }

    [Fact]
    public async Task Every_kind_the_value_fits_runs_and_none_wins_by_coming_first()
    {
        FakeFindContributor documents = new FakeFindContributor(10, "Documents").Returns("documents", null, FakeFindContributor.Hit(Scope, "Document", "abc"));
        FakeFindContributor streams = new FakeFindContributor(20, "Streams").Returns("streams", null, FakeFindContributor.Hit(Scope, "Stream", "abc"));

        FindResult result = await Service(contributors: [documents, streams]).FindAsync(Scope, "abc", TestContext.Current.CancellationToken);

        documents.Calls.Should().ContainSingle();
        streams.Calls.Should().ContainSingle();
        result.Hits.Should().HaveCount(2, "an aggregate and its stream share an id and both are worth offering");
    }

    [Fact]
    public async Task A_kind_the_value_does_not_fit_adds_no_phrase_and_no_hit()
    {
        FakeFindContributor fits = new FakeFindContributor(10, "Fits").Returns("fits");
        FakeFindContributor doesNot = new(20, "Does not");

        FindResult result = await Service(contributors: [fits, doesNot]).FindAsync(Scope, "abc", TestContext.Current.CancellationToken);

        result.Searched.Should().Equal("fits");
        result.Found.Should().BeFalse();
    }

    [Fact]
    public async Task What_was_typed_reaches_every_contributor_trimmed_and_in_the_scope_that_was_asked()
    {
        FakeFindContributor contributor = new(10, "Any");
        StudioScope other = new("other", string.Empty, null);

        await Service(contributors: [contributor]).FindAsync(other, "  abc  ", TestContext.Current.CancellationToken);

        contributor.Calls.Should().ContainSingle().Which.Should().Be((other, "abc"));
    }

    [Fact]
    public async Task An_empty_box_and_an_over_long_value_run_nothing()
    {
        FakeFindContributor contributor = new(10, "Any");
        FindService service = Service(contributors: [contributor]);

        (await service.FindAsync(Scope, "  ", TestContext.Current.CancellationToken)).Refusal.Should().BeNull();
        FindResult tooLong = await service.FindAsync(Scope, new string('x', 513), TestContext.Current.CancellationToken);

        tooLong.Refusal.Should().Be("Too long to be an id (512 characters at most).");
        tooLong.Found.Should().BeFalse();
        contributor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_scope_the_policy_refuses_reads_as_a_scope_with_nothing_in_it()
    {
        TestSourceAuthorizationService authorization = new();
        authorization.Allow(static _ => false);
        FakeFindContributor contributor = new FakeFindContributor(10, "Any").Returns("anything", null, FakeFindContributor.Hit(Scope, "Any", "1"));

        FindResult result = await Service(
                authorization: authorization,
                options: new PostgresStudioOptions { SourceAuthorizationPolicy = "Studio" },
                contributors: [contributor])
            .FindAsync(Scope, "abc", TestContext.Current.CancellationToken);

        result.Found.Should().BeFalse();
        result.Searched.Should().BeEmpty("a refused scope is not described");
        result.Notes.Should().BeEmpty();
        contributor.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_contributor_that_throws_is_a_note_and_the_others_still_run()
    {
        FakeFindContributor broken = new(10, "Broken") { Behavior = static (_, _, _) => throw new InvalidOperationException("password=hunter2") };
        FakeFindContributor fine = new FakeFindContributor(20, "Fine").Returns("fine", null, FakeFindContributor.Hit(Scope, "Fine", "1"));

        FindResult result = await Service(contributors: [broken, fine]).FindAsync(Scope, "abc", TestContext.Current.CancellationToken);

        result.Notes.Should().Equal("Broken: could not be searched.");
        result.Notes.Should().NotContain(static x => x.Contains("hunter2", StringComparison.Ordinal), "what an exception said never reaches the page");
        result.Hits.Should().ContainSingle();
    }

    [Fact]
    public async Task The_ten_second_budget_turns_the_kind_that_was_running_and_the_kinds_after_it_into_notes()
    {
        ManualTimeProvider time = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeFindContributor fast = new FakeFindContributor(10, "Fast").Returns("fast", null, FakeFindContributor.Hit(Scope, "Fast", "1"));
        FakeFindContributor slow = FakeFindContributor.Blocking(20, "Slow", started);
        FakeFindContributor unreached = new FakeFindContributor(30, "Unreached").Returns("unreached");

        Task<FindResult> running = Service(time, contributors: [fast, slow, unreached]).FindAsync(Scope, "abc", TestContext.Current.CancellationToken);
        await started.Task.Bounded();

        time.Advance(FindService.Budget);
        FindResult result = await running;

        result.Hits.Should().ContainSingle("what was found before the budget ran out is kept");
        result.Notes.Should().Equal("Slow: not searched, out of time.", "Unreached: not searched, out of time.");
        unreached.Calls.Should().BeEmpty();
        result.Searched.Should().Equal("fast");
    }

    [Fact]
    public async Task A_second_Find_cancels_the_first()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeFindContributor contributor = FakeFindContributor.Blocking(10, "Slow", started);
        FindService service = Service(contributors: [contributor]);

        Task<FindResult> first = service.FindAsync(Scope, "first", TestContext.Current.CancellationToken);
        await started.Task.Bounded();

        contributor.Behavior = static (_, _, _) => Task.FromResult(FindContribution.NotApplicable);
        FindResult second = await service.FindAsync(Scope, "second", TestContext.Current.CancellationToken);

        Func<Task> firstFind = () => first;
        await firstFind.Should().ThrowAsync<OperationCanceledException>();
        second.Value.Should().Be("second");
    }

    [Fact]
    public async Task The_caller_going_away_cancels_the_Find()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource caller = new();
        FindService service = Service(contributors: [FakeFindContributor.Blocking(10, "Slow", started)]);

        Task<FindResult> running = service.FindAsync(Scope, "abc", caller.Token);
        await started.Task.Bounded();
        await caller.CancelAsync();

        Func<Task> find = () => running;
        await find.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Cancel_stops_the_Find_in_flight()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FindService service = Service(contributors: [FakeFindContributor.Blocking(10, "Slow", started)]);

        Task<FindResult> running = service.FindAsync(Scope, "abc", TestContext.Current.CancellationToken);
        await started.Task.Bounded();
        service.Cancel();

        Func<Task> find = () => running;
        await find.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task At_most_twenty_hits_are_kept_and_the_rest_are_counted()
    {
        FindHit[] hits = [.. Enumerable.Range(1, 26).Select(i => FakeFindContributor.Hit(Scope, "Document", i.ToString(System.Globalization.CultureInfo.InvariantCulture)))];
        FakeFindContributor contributor = new FakeFindContributor(10, "Documents").Returns("documents", null, hits);

        FindResult result = await Service(contributors: [contributor]).FindAsync(Scope, "abc", TestContext.Current.CancellationToken);

        result.Hits.Should().HaveCount(FindService.MaxHits);
        result.HitsOmitted.Should().Be(6);
        result.Hits[0].Id.Should().Be("1", "the first hits are the ones kept");
    }

    [Fact]
    public async Task The_same_phrase_or_note_from_two_kinds_is_said_once()
    {
        const string note = "This store numbers and partitions events per tenant: select a tenant.";
        FakeFindContributor streams = new FakeFindContributor(10, "Streams").Returns(null, [note]);
        FakeFindContributor events = new FakeFindContributor(20, "Events").Returns(null, [note]);

        FindResult result = await Service(contributors: [streams, events]).FindAsync(Scope, "abc", TestContext.Current.CancellationToken);

        result.Notes.Should().ContainSingle().Which.Should().Be(note);
    }

    private sealed class ScreenBound(int order, string noun) : IFindContributor
    {
        public List<StudioScope> Calls { get; } = [];

        public int Order { get; } = order;

        public string Noun { get; } = noun;

        public bool SearchesOtherScopes => false;

        public Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
        {
            Calls.Add(scope);
            return Task.FromResult(new FindContribution([], "bound", []));
        }
    }

    [Fact]
    public async Task A_kind_whose_page_does_not_apply_its_scope_is_searched_on_screen_and_left_out_elsewhere_without_a_word()
    {
        ScreenBound bound = new(10, "Statements");
        FakeFindContributor anywhere = new FakeFindContributor(20, "Documents").Returns("documents");
        FindService service = Service(contributors: [bound, anywhere]);
        StudioScope other = new("other", "db", null);

        FindResult here = await service.FindAsync(Scope, "abc", TestContext.Current.CancellationToken);
        FindResult away = await service.FindAsync(other, "abc", elsewhere: true, TestContext.Current.CancellationToken);

        here.Searched.Should().Equal("bound", "documents");
        away.Searched.Should().Equal("documents");
        away.Notes.Should().BeEmpty();
        bound.Calls.Should().Equal(Scope);
        anywhere.Calls.Select(static x => x.Scope).Should().Equal(Scope, other);
    }

    [Fact]
    public void Find_is_available_only_when_a_kind_is_registered()
    {
        Service().IsAvailable.Should().BeFalse();
        Service(contributors: [new FakeFindContributor(10, "Any")]).IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void A_hit_is_keyed_by_kind_store_database_tenant_and_identity()
    {
        FindHit one = FakeFindContributor.Hit(Scope, "Document", "1");
        FindHit inTenant = one with { Scope = Scope with { TenantId = "acme" } };
        FindHit otherStore = one with { Scope = Scope with { StoreKey = "other" } };
        FindHit otherKind = one with { Kind = "Stream" };

        new[] { one.Key, inTenant.Key, otherStore.Key, otherKind.Key }.Should().OnlyHaveUniqueItems();
    }
}
