using JasperFx.Events;

using Marten;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Events;

/// <summary>
/// DL: the aggregate replay's fold, over events it is handed - Marten's own aggregator and Marten's own identity
/// setting, with no database and no <c>session.Events</c> call anywhere in it.
/// </summary>
/// <remarks>
/// The replay used to be <c>session.Events.AggregateStreamAsync&lt;T&gt;</c>, whose <c>FetchStreamAsync</c> ensures the
/// event store's storage first (AGENTS.md hard rule 14). The read is the studio's own SQL now; these pin that what is
/// left - <c>QueryEventStore.AggregateStreamAsync</c> after its fetch, read at Marten <c>V9.39.0</c> and <c>V9.50.2</c> -
/// still behaves as Marten's did. <c>DocumentStore.For</c> opens no connection, and nothing here runs a statement: the
/// unreachable connection string would fail any that did.
/// </remarks>
public class AggregateFoldTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_fold_is_Martens_own_aggregator_over_the_events_it_is_handed()
    {
        await using IQuerySession session = Session(StreamIdentity.AsGuid);
        Guid stream = Guid.NewGuid();

        object? aggregate = await AggregateStreamInvoker.AggregateAtVersionAsync(
            session, typeof(TimeTravelOrder), stream, [Placed(stream, 1, 10), Placed(stream, 2, 25)], 0, Token);

        TimeTravelOrder order = aggregate.Should().BeOfType<TimeTravelOrder>().Subject;
        order.Total.Should().Be(25, "Apply ran for each event, in order");
        order.Id.Should().Be(stream, "Marten sets a live aggregate's identity from the stream, and so does the replay");
    }

    [Fact]
    public async Task A_string_keyed_stream_sets_the_key_as_the_identity()
    {
        await using IQuerySession session = Session(StreamIdentity.AsString);

        object? aggregate = await AggregateStreamInvoker.AggregateAtVersionAsync(
            session, typeof(FoldKeyed), "order-7", [Keyed("order-7", 1), Keyed("order-7", 2), Keyed("order-7", 3)], 0, Token);

        FoldKeyed keyed = aggregate.Should().BeOfType<FoldKeyed>().Subject;
        keyed.Count.Should().Be(3);
        keyed.LastTotal.Should().Be(3, "the events were folded in the order they were handed over");
        keyed.Id.Should().Be("order-7");
    }

    [Fact]
    public async Task No_events_is_no_aggregate()
    {
        await using IQuerySession session = Session(StreamIdentity.AsGuid);

        object? aggregate = await AggregateStreamInvoker.AggregateAtVersionAsync(
            session, typeof(TimeTravelOrder), Guid.NewGuid(), [], 0, Token);

        aggregate.Should().BeNull("Marten returns its starting state - none - for a stream with no events");
    }

    /// <summary>
    /// Marten answers a version the stream never reached with nothing rather than with the last state, and the caller
    /// turns that into "this stream never reached that version".
    /// </summary>
    [Fact]
    public async Task A_version_past_the_last_event_is_no_aggregate()
    {
        await using IQuerySession session = Session(StreamIdentity.AsGuid);
        Guid stream = Guid.NewGuid();

        object? aggregate = await AggregateStreamInvoker.AggregateAtVersionAsync(
            session, typeof(TimeTravelOrder), stream, [Placed(stream, 1, 10), Placed(stream, 2, 25)], 5, Token);

        aggregate.Should().BeNull();
    }

    private static IQuerySession Session(StreamIdentity identity) =>
        DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            options.Events.StreamIdentity = identity;
            options.Events.AddEventType<TimeTravelOrderPlaced>();
        }).QuerySession();

    private static Event<TimeTravelOrderPlaced> Placed(Guid stream, long version, int total) =>
        new Event<TimeTravelOrderPlaced>(new TimeTravelOrderPlaced(total))
        {
            Id = Guid.NewGuid(),
            StreamId = stream,
            Version = version,
            Sequence = version,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static Event<TimeTravelOrderPlaced> Keyed(string key, long version) =>
        new Event<TimeTravelOrderPlaced>(new TimeTravelOrderPlaced((int) version))
        {
            Id = Guid.NewGuid(),
            StreamKey = key,
            Version = version,
            Sequence = version,
            Timestamp = DateTimeOffset.UtcNow,
        };
}

/// <summary>A self-aggregating aggregate keyed by a stream key, that never sets its own id.</summary>
public class FoldKeyed
{
    public string Id { get; set; } = string.Empty;

    public int Count { get; set; }

    public int LastTotal { get; set; }

    public void Apply(TimeTravelOrderPlaced placed)
    {
        Count++;
        LastTotal = placed.Total;
    }
}
