using JasperFx.Events;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Services.Events;

using SampleEvents = MartenStudio.SampleDomain.Events;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// M1 over the sample domain's own projection: the sample's <c>OrderSummaryProjection</c> - <c>Create</c> and three
/// <c>Apply</c> methods, its dispatcher generated in the sample's assembly - stepped one event at a time agrees with Marten's
/// own fold of the stream up to every version. No database: this suite references the sample domain and the fast one does
/// not, so the in-memory half lives here, and runs without Docker.
/// </summary>
public class SampleDomainSteppingTests
{
    /// <summary>Nothing listens here; nothing in these tests opens a connection.</summary>
    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=nowhere;Username=nobody;Password=nothing;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_sample_order_summary_stepped_event_by_event_is_Martens_own_fold_at_every_version()
    {
        await using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(Unreachable);
            options.DatabaseSchemaName = "sample_stepping";
            options.Projections.Add(new SampleEvents.OrderSummaryProjection(), ProjectionLifecycle.Inline);
        });
        await using IQuerySession session = store.QuerySession();

        Guid stream = Guid.NewGuid();
        DateTimeOffset at = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

        IEvent[] events =
        [
            Event(stream, 1, new SampleEvents.OrderPlaced(stream, "acme", at)),
            Event(stream, 2, new SampleEvents.ItemAdded("sku-1", 2, 9.5m, at.AddMinutes(1))),
            Event(stream, 3, new SampleEvents.ItemAdded("sku-2", 1, 20m, at.AddMinutes(2))),
            Event(stream, 4, new SampleEvents.OrderShipped("dhl", at.AddMinutes(3))),
            Event(stream, 5, new SampleEvents.OrderCancelled("too late", at.AddMinutes(4))),
        ];

        IAggregateFolder folder = AggregateStreamInvoker.FolderFor(typeof(SampleEvents.OrderSummary));
        var walk = new AggregateWalk(folder, session, store.Options.Serializer(), stream);

        walk.Mode.Should().Be(AggregateStepMode.EventByEvent);

        for (int version = 1; version <= events.Length; version++)
        {
            (await walk.FeedAsync(events[version - 1], Token)).Kind.Should().Be(WalkOutcomeKind.Folded);

            object? whole = await AggregateStreamInvoker.AggregateAtVersionAsync(
                session, typeof(SampleEvents.OrderSummary), stream, events[..version], 0, Token);

            walk.StateJson().Should().Be(store.Options.Serializer().ToJson(whole!), "version {0}", version);
        }

        walk.StateJson().Should().Contain("\"Status\":\"Cancelled\"").And.Contain("\"ItemCount\":3");
    }

    private static Event<T> Event<T>(Guid stream, long version, T data) where T : notnull =>
        new(data)
        {
            Id = Guid.NewGuid(),
            StreamId = stream,
            Version = version,
            Sequence = version,
            Timestamp = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero).AddSeconds(version),
        };
}
