using JasperFx.Events;

using MartenStudio.Services.Events;
using MartenStudio.Services.Find;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Tests.Events;

/// <summary>
/// 1.2-SCOPE-3: Find's streams, natural keys and events. The reads are the Streams page's and the Feed's own, so what these prove is
/// what the contributors add: which values they ask about, that a store which numbers events per tenant is not searched without one,
/// that a tenant the policy refuses is dropped and never counted, and where a hit opens.
/// </summary>
public class EventFindContributorTests
{
    private const string StreamId = "2f6c0e0a-1b2c-4d3e-8f90-123456789abc";

    private static readonly StudioScope Asked = new("default", string.Empty, null);
    private static readonly StudioScope Matched = new("default", "primary", null);

    /// <summary>The event-side seam, said outright: the context and which tenants the policy admits.</summary>
    private sealed class FakeScopes(FindEventContext? context, Func<string?, bool>? admits = null) : IFindEventScopes
    {
        public int Contexts { get; private set; }

        public Task<FindEventContext?> ContextAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
        {
            Contexts++;
            return Task.FromResult(context);
        }

        public ValueTask<List<T>> AdmitAsync<T>(StudioScope scope, IReadOnlyList<T> rows, Func<T, string?> tenantOf, CancellationToken cancellationToken) =>
            ValueTask.FromResult<List<T>>([.. rows.Where(x => admits is null || admits(tenantOf(x)))]);
    }

    private static EventStoreShape Shape(StreamIdentity identity = StreamIdentity.AsGuid, bool perTenant = false, params string[] naturalKeys) => new()
    {
        StreamIdentity = identity,
        Schema = "studio_events",
        EventTablesExist = true,
        PerTenantSequences = perTenant,
        NaturalKeys = naturalKeys,
    };

    private static FindEventContext Context(EventStoreShape shape, StudioScope? scope = null) => new(scope ?? Matched, shape);

    private static StreamFindContributor Streams(FakeEventDataService events, FakeScopes scopes) =>
        new(events, scopes, Microsoft.Extensions.Options.Options.Create(new MartenStudioOptions()));

    private static EventSequenceFindContributor Sequences(FakeEventDataService events, FakeScopes scopes) =>
        new(events, scopes, Microsoft.Extensions.Options.Options.Create(new MartenStudioOptions()));

    [Fact]
    public async Task A_stream_id_asks_the_streams_read_for_that_id_with_archived_streams_and_room_for_several_tenants()
    {
        FakeEventDataService events = new() { StreamsForId = [FakeEventDataService.Stream(StreamId, "Order", version: 5)] };

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape()))).FindAsync(Asked, FindQuery.Parse(StreamId), CancellationToken.None);

        StreamListRequest request = events.StreamRequests.Should().ContainSingle().Subject;
        request.StreamId.Should().Be(StreamId);
        request.IncludeArchived.Should().BeTrue();
        request.PageSize.Should().Be(21);
        events.StreamRequestScopes.Should().ContainSingle().Which.Should().Be(Asked, "the read resolves the scope itself, under the gates of the Streams page");

        FindHit hit = found.Hits.Should().ContainSingle().Subject;
        hit.Kind.Should().Be("Stream");
        hit.Title.Should().Be("Stream " + StreamId);
        hit.Detail.Should().Be("Order, 5 events");
        hit.Url.Should().Contain("events/streams/s").And.Contain("id=" + StreamId).And.Contain("db=primary");
        found.Searched.Should().Be("streams");
    }

    [Fact]
    public async Task An_archived_stream_is_marked_and_a_stream_in_a_tenant_links_to_that_tenant()
    {
        FakeEventDataService events = new() { StreamsForId = [FakeEventDataService.Stream(StreamId, archived: true, tenantId: "acme")] };

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape()))).FindAsync(Asked, FindQuery.Parse(StreamId), CancellationToken.None);

        FindHit hit = found.Hits.Single();
        hit.Mark.Should().Be("archived");
        hit.TenantChip.Should().Be("tenant acme");
        hit.Url.Should().Contain("tenant=acme");
        hit.Scope.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task A_value_that_is_not_a_guid_is_not_asked_of_a_store_whose_streams_are()
    {
        FakeEventDataService events = new();

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape()))).FindAsync(Asked, FindQuery.Parse("ORD-1"), CancellationToken.None);

        events.StreamRequests.Should().BeEmpty();
        found.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Any_value_is_asked_of_a_store_whose_streams_are_strings()
    {
        FakeEventDataService events = new() { StreamsForId = [FakeEventDataService.Stream("ORD-1")] };

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape(StreamIdentity.AsString)))).FindAsync(Asked, FindQuery.Parse("ORD-1"), CancellationToken.None);

        events.StreamRequests.Should().ContainSingle();
        found.Hits.Should().ContainSingle();
    }

    [Fact]
    public async Task A_tenant_the_policy_refuses_is_dropped_before_anything_is_counted()
    {
        FakeEventDataService events = new()
        {
            StreamsForId =
            [
                FakeEventDataService.Stream(StreamId, tenantId: "acme"),
                FakeEventDataService.Stream(StreamId, tenantId: "secret"),
                FakeEventDataService.Stream(StreamId, tenantId: "zeta"),
            ],
        };

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape()), tenant => tenant != "secret"))
            .FindAsync(Asked, FindQuery.Parse(StreamId), CancellationToken.None);

        found.Hits.Select(static x => x.Scope.TenantId).Should().Equal("acme", "zeta");
        found.Hits.Select(static x => x.Detail).Should().OnlyContain(static x => !x!.Contains("more tenant", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Five_tenants_are_named_and_the_rest_are_counted_on_the_last()
    {
        FakeEventDataService events = new() { StreamsForId = [.. Enumerable.Range(1, 8).Select(i => FakeEventDataService.Stream(StreamId, tenantId: "t" + i))] };

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape()))).FindAsync(Asked, FindQuery.Parse(StreamId), CancellationToken.None);

        found.Hits.Should().HaveCount(5);
        found.Hits[4].Detail.Should().EndWith("and 3 more tenants you may see");
        found.Hits[3].Detail.Should().NotContain("more tenant");
    }

    [Fact]
    public async Task A_natural_key_is_looked_up_and_each_match_links_to_its_stream()
    {
        FakeEventDataService events = new()
        {
            NaturalKeys = new NaturalKeyLookup(
                [new NaturalKeyMatch("Order.Number", FakeEventDataService.Stream(StreamId, "Order"))],
                ["Order.Number"]),
        };

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape(naturalKeys: "Order.Number"))))
            .FindAsync(Asked, FindQuery.Parse("ORD-2026-0001"), CancellationToken.None);

        events.NaturalKeyLookups.Should().Equal("ORD-2026-0001");
        FindHit hit = found.Hits.Should().ContainSingle().Subject;
        hit.Kind.Should().Be("Natural key");
        hit.Title.Should().Be("Order.Number: stream " + StreamId);
        hit.Url.Should().Contain("events/streams/s").And.Contain("id=" + StreamId);
        found.Searched.Should().Be("natural keys (Order.Number)");
    }

    [Fact]
    public async Task A_store_with_no_natural_key_looks_nothing_up()
    {
        FakeEventDataService events = new();

        await Streams(events, new FakeScopes(Context(Shape()))).FindAsync(Asked, FindQuery.Parse("ORD-1"), CancellationToken.None);

        events.NaturalKeyLookups.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stream_and_a_natural_key_that_name_the_same_stream_are_two_hits_with_two_keys()
    {
        FakeEventDataService events = new()
        {
            StreamsForId = [FakeEventDataService.Stream(StreamId)],
            NaturalKeys = new NaturalKeyLookup([new NaturalKeyMatch("Order.Number", FakeEventDataService.Stream(StreamId))], ["Order.Number"]),
        };

        FindContribution found = await Streams(events, new FakeScopes(Context(Shape(naturalKeys: "Order.Number"))))
            .FindAsync(Asked, FindQuery.Parse(StreamId), CancellationToken.None);

        found.Hits.Should().HaveCount(2);
        found.Hits.Select(static x => x.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_store_that_numbers_events_per_tenant_is_not_searched_for_streams_natural_keys_or_events_until_a_tenant_is_chosen()
    {
        FakeEventDataService events = new();
        FakeScopes scopes = new(Context(Shape(perTenant: true, naturalKeys: "Order.Number")));

        FindContribution streams = await Streams(events, scopes).FindAsync(Asked, FindQuery.Parse(StreamId), CancellationToken.None);
        FindContribution sequence = await Sequences(events, scopes).FindAsync(Asked, FindQuery.Parse("#12"), CancellationToken.None);

        const string note = "This store numbers and partitions events per tenant: select a tenant to search streams, natural keys and events.";
        streams.Notes.Should().Equal(note);
        sequence.Notes.Should().Equal(note);
        streams.Searched.Should().BeNull();
        events.StreamRequests.Should().BeEmpty();
        events.NaturalKeyLookups.Should().BeEmpty();
        events.EventBySequenceScopes.Should().BeEmpty();
    }

    [Fact]
    public async Task With_a_tenant_chosen_a_store_that_numbers_events_per_tenant_is_searched_in_that_tenant()
    {
        StudioScope inTenant = Asked with { TenantId = "acme" };
        FakeEventDataService events = new() { EventBySequence = FakeEventDataService.Event(12, version: 3) };
        FakeScopes scopes = new(Context(Shape(perTenant: true), Matched with { TenantId = "acme" }));

        FindContribution found = await Sequences(events, scopes).FindAsync(inTenant, FindQuery.Parse("#12"), CancellationToken.None);

        found.Notes.Should().BeEmpty();
        found.Hits.Should().ContainSingle();
        events.EventBySequenceScopes.Should().ContainSingle().Which.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task A_sequence_is_read_in_the_scope_that_was_asked_so_the_tenant_predicate_applies()
    {
        StudioScope inTenant = Asked with { TenantId = "acme" };
        FakeEventDataService events = new() { EventBySequence = FakeEventDataService.Event(12, version: 3, streamId: StreamId) };

        FindContribution found = await Sequences(events, new FakeScopes(Context(Shape(), Matched with { TenantId = "acme" })))
            .FindAsync(inTenant, FindQuery.Parse("#12"), CancellationToken.None);

        events.EventBySequenceScopes.Should().ContainSingle().Which.Should().Be(inTenant);
        FindHit hit = found.Hits.Should().ContainSingle().Subject;
        hit.Kind.Should().Be("Event");
        hit.Title.Should().Be("Event #12");
        hit.Detail.Should().Be("OrderPlaced, version 3 of stream " + StreamId);
        hit.Url.Should().Contain("events/streams/s").And.Contain("id=" + StreamId).And.Contain("v=3").And.Contain("tenant=acme");
        found.Searched.Should().Be("event sequences");
    }

    [Theory]
    [InlineData("#12", 12)]
    [InlineData("12", 12)]
    public async Task A_hash_or_a_plain_number_is_a_sequence(string typed, long expected)
    {
        FakeEventDataService events = new() { EventBySequence = FakeEventDataService.Event(expected) };

        FindContribution found = await Sequences(events, new FakeScopes(Context(Shape()))).FindAsync(Asked, FindQuery.Parse(typed), CancellationToken.None);

        found.Hits.Single().Title.Should().Be("Event #12");
    }

    [Theory]
    [InlineData("ORD-1")]
    [InlineData("0")]
    [InlineData("#abc")]
    [InlineData("")]
    public async Task Anything_else_is_not_a_sequence_and_reads_nothing(string typed)
    {
        FakeEventDataService events = new();
        FakeScopes scopes = new(Context(Shape()));

        FindContribution found = await Sequences(events, scopes).FindAsync(Asked, FindQuery.Parse(typed), CancellationToken.None);

        found.Should().Be(FindContribution.NotApplicable);
        events.EventBySequenceScopes.Should().BeEmpty();
        scopes.Contexts.Should().Be(0, "not even the event store's shape is read for a value that cannot be a sequence");
    }

    [Fact]
    public async Task An_event_in_a_tenant_the_policy_refuses_reads_as_no_event()
    {
        FakeEventDataService events = new() { EventBySequence = FakeEventDataService.Event(12) with { TenantId = "secret" } };

        FindContribution found = await Sequences(events, new FakeScopes(Context(Shape()), tenant => tenant != "secret"))
            .FindAsync(Asked, FindQuery.Parse("#12"), CancellationToken.None);

        found.Hits.Should().BeEmpty();
        found.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sequence_nothing_holds_is_no_hit_and_still_says_it_was_searched()
    {
        FakeEventDataService events = new();

        FindContribution found = await Sequences(events, new FakeScopes(Context(Shape()))).FindAsync(Asked, FindQuery.Parse("#12"), CancellationToken.None);

        found.Hits.Should().BeEmpty();
        found.Searched.Should().Be("event sequences");
    }

    [Fact]
    public async Task A_scope_with_no_event_store_is_not_this_kinds_to_search()
    {
        FakeEventDataService events = new();
        FakeScopes scopes = new(null);

        (await Streams(events, scopes).FindAsync(Asked, FindQuery.Parse(StreamId), CancellationToken.None)).Should().Be(FindContribution.NotApplicable);
        (await Sequences(events, scopes).FindAsync(Asked, FindQuery.Parse("#12"), CancellationToken.None)).Should().Be(FindContribution.NotApplicable);

        events.StreamRequests.Should().BeEmpty();
        events.EventBySequenceScopes.Should().BeEmpty();
    }

    [Fact]
    public void A_stream_link_at_an_event_names_the_stream_the_version_and_the_scope()
    {
        string url = EventLinks.ToStreamAt(new MartenStudioOptions(), new StudioScope("default", "primary", "acme"), StreamId, 7);

        url.Should().Contain("events/streams/s").And.Contain("id=" + StreamId).And.Contain("v=7")
            .And.Contain("store=default").And.Contain("db=primary").And.Contain("tenant=acme");
    }
}
