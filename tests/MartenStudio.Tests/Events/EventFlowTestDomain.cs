using JasperFx.Events;

using Marten;
using Marten.Events.Aggregation;
using Marten.Events.Projections;

namespace MartenStudio.Tests.Events;

// 1.2, M6: the small event-sourced domain the event flow's graph tests read. Shaped like the sample domain's
// (MartenStudio.SampleDomain, which the fast suite does not reference): an interface every order event implements, an inline
// single-stream projection, a multi-stream projection grouped on the interface, and an event projection that stores a document
// from a Project method without declaring it.

/// <summary>What the order events implement; the multi-stream projection groups on it.</summary>
public interface IFlowOrderEvent;

/// <summary>An order was placed.</summary>
/// <param name="OrderId">The order.</param>
public sealed record FlowPlaced(Guid OrderId) : IFlowOrderEvent;

/// <summary>An item was added.</summary>
/// <param name="Sku">The item.</param>
public sealed record FlowItemAdded(string Sku) : IFlowOrderEvent;

/// <summary>The order shipped.</summary>
/// <param name="Carrier">Who carried it.</param>
public sealed record FlowShipped(string Carrier) : IFlowOrderEvent;

/// <summary>The order was cancelled.</summary>
/// <param name="Reason">Why.</param>
public sealed record FlowCancelled(string Reason) : IFlowOrderEvent;

/// <summary>The order was voided: an event that is not an <see cref="IFlowOrderEvent" />.</summary>
/// <param name="Reason">Why.</param>
public sealed record FlowVoided(string Reason);

/// <summary>A ledger was opened; no projection reads it.</summary>
public sealed record FlowLedgerOpened;

/// <summary>A ledger was credited; no projection reads it.</summary>
public sealed record FlowLedgerCredited;

/// <summary>The single-stream projection's document.</summary>
public sealed class FlowOrder
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>The state.</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>The multi-stream projection's document.</summary>
public sealed class FlowDay
{
    /// <summary>The day.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>How many.</summary>
    public int Orders { get; set; }
}

/// <summary>The event projection's document, stored without being declared.</summary>
public sealed class FlowShipment
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A document type that is also a hierarchy root.</summary>
public class FlowVehicle
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A subclass of <see cref="FlowVehicle" />.</summary>
public sealed class FlowCar : FlowVehicle;

/// <summary>An aggregate folded live.</summary>
public sealed class FlowLive
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>The fold.</summary>
    public void Apply(FlowPlaced placed) => Id = placed.OrderId;
}

/// <summary>An aggregate the host will hide.</summary>
public sealed class FlowSecret
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

}

/// <summary>A strongly typed id, the tag type of <see cref="FlowTagged" />.</summary>
/// <param name="Value">The value.</param>
public readonly record struct FlowTagId(Guid Value);

/// <summary>An aggregate keyed by a strongly typed id.</summary>
public sealed class FlowTagged
{
    /// <summary>The id.</summary>
    public FlowTagId Id { get; set; }

    /// <summary>The fold.</summary>
    public void Apply(FlowPlaced placed) => Id = new FlowTagId(placed.OrderId);
}

/// <summary>The inline single-stream projection: places, adds, ships, cancels and voids (<c>ShouldDelete</c>).</summary>
public sealed partial class FlowOrderProjection : SingleStreamProjection<FlowOrder, Guid>
{
    /// <summary>Named.</summary>
    public FlowOrderProjection() => Name = "Orders";

    /// <summary>Starts one.</summary>
    public static FlowOrder Create(FlowPlaced placed) => new() { Id = placed.OrderId, Status = "Placed" };

    /// <summary>Adds.</summary>
    public static void Apply(FlowItemAdded added, FlowOrder view) => view.Status = "Items";

    /// <summary>Ships.</summary>
    public static void Apply(FlowShipped shipped, FlowOrder view) => view.Status = "Shipped";

    /// <summary>Cancels.</summary>
    public static void Apply(FlowCancelled cancelled, FlowOrder view) => view.Status = "Cancelled";

    /// <summary>Deletes.</summary>
    public static bool ShouldDelete(FlowVoided voided) => voided is not null;
}

/// <summary>The multi-stream projection: groups on <see cref="IFlowOrderEvent" />, applies three of its events.</summary>
public sealed partial class FlowDayProjection : MultiStreamProjection<FlowDay, string>
{
    /// <summary>Named.</summary>
    public FlowDayProjection()
    {
        Name = "Daily";
        Identity<IFlowOrderEvent>(static _ => "today");
    }

    /// <summary>Counts.</summary>
    public static void Apply(FlowPlaced placed, FlowDay view) => view.Orders++;

    /// <summary>Counts.</summary>
    public static void Apply(FlowItemAdded added, FlowDay view) => view.Orders++;

    /// <summary>Counts.</summary>
    public static void Apply(FlowCancelled cancelled, FlowDay view) => view.Orders--;
}

/// <summary>An event projection that stores a document from a <c>Project</c> method without declaring it.</summary>
internal sealed partial class FlowTrackerProjection : EventProjection
{
    /// <summary>Named.</summary>
    public FlowTrackerProjection() => Name = "Tracker";

    /// <summary>Stores a shipment.</summary>
    public static void Project(IEvent<FlowShipped> shipped, IDocumentOperations operations) =>
        operations.Store(new FlowShipment { Id = shipped.StreamId });

    /// <summary>Reads another event type.</summary>
    public static void Project(IEvent<FlowItemAdded> added, IDocumentOperations operations)
    {
    }
}

/// <summary>An event projection whose <c>PublishedTypes()</c> throws.</summary>
internal sealed partial class FlowThrowingProjection : EventProjection
{
    /// <summary>Named.</summary>
    public FlowThrowingProjection() => Name = "Throwing";

    /// <summary>Reads an event type.</summary>
    public static void Project(IEvent<FlowPlaced> placed, IDocumentOperations operations)
    {
    }

    /// <summary>Whether <see cref="PublishedTypes" /> throws: registration asks it too, so a test arms it afterwards.</summary>
    public bool Armed { get; set; }

    /// <inheritdoc />
    public override IEnumerable<Type> PublishedTypes() =>
        Armed ? throw new InvalidOperationException("no types for you") : base.PublishedTypes();
}

/// <summary>An event projection that declares the document it writes, and a subclass of a hierarchy.</summary>
internal sealed partial class FlowDeclaringProjection : EventProjection
{
    /// <summary>Named; declares the hierarchy's subclass.</summary>
    public FlowDeclaringProjection()
    {
        Name = "Declaring";
        Options.DeleteViewTypeOnTeardown<FlowCar>();
    }

    /// <summary>Reads an event type.</summary>
    public static void Project(IEvent<FlowPlaced> placed, IDocumentOperations operations)
    {
    }
}

/// <summary>A hand-written projection that is not a <c>ProjectionBase</c>: it declares nothing.</summary>
public sealed class FlowBareProjection : IProjection
{
    /// <inheritdoc />
    public Task ApplyAsync(IDocumentOperations operations, IReadOnlyList<IEvent> events, CancellationToken cancellation) =>
        Task.CompletedTask;
}

/// <summary>A catch-all aggregation: <c>Evolve</c> takes the whole event.</summary>
public sealed partial class FlowCatchAllProjection : SingleStreamProjection<FlowOrder, Guid>
{
    /// <summary>Named.</summary>
    public FlowCatchAllProjection() => Name = "CatchAll";

    /// <inheritdoc />
    public override FlowOrder? Evolve(FlowOrder? snapshot, Guid id, IEvent e) => snapshot;
}

/// <summary>A projection of the hidden <see cref="FlowSecret" />, under a name that does not give it away.</summary>
public sealed partial class FlowSecretProjection : SingleStreamProjection<FlowSecret, Guid>
{
    /// <summary>Named.</summary>
    public FlowSecretProjection() => Name = "Opaque";

    /// <summary>Starts one.</summary>
    public static FlowSecret Create(FlowPlaced placed) => new() { Id = placed.OrderId };
}

/// <summary>A live projection of the hidden <see cref="FlowSecretLive" />, under a name that does not give it away.</summary>
public sealed partial class FlowSecretLiveProjection : SingleStreamProjection<FlowSecretLive, Guid>
{
    /// <summary>Named.</summary>
    public FlowSecretLiveProjection() => Name = "OpaqueLive";

    /// <summary>Starts one.</summary>
    public static FlowSecretLive Create(FlowPlaced placed) => new() { Id = placed.OrderId };
}
