namespace MartenStudio.SampleDomain.Events;

/// <summary>
/// What every demo order event has in common: when it happened.
/// </summary>
/// <remarks>
/// The interface is not decoration. <c>DailySalesProjection</c> groups events into one document per
/// calendar day, and a multi-stream projection can only slice on something the event body carries -
/// grouping on an interface every event implements is Marten's own answer to that, and it is why
/// <see cref="OccurredAt" /> is on the events rather than read from <c>IEvent.Timestamp</c>.
/// </remarks>
public interface IOrderEvent
{
    /// <summary>When the thing this event records happened, as the writer saw it.</summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>An order was placed; the first event of every demo order stream.</summary>
public sealed record OrderPlaced(Guid OrderId, string Customer, DateTimeOffset OccurredAt) : IOrderEvent;

/// <summary>A line was added to an order.</summary>
public sealed record ItemAdded(string Sku, int Quantity, decimal Price, DateTimeOffset OccurredAt) : IOrderEvent
{
    /// <summary>
    /// The SKU <c>ShipmentTracker</c> throws on, which is how the demo database ends up with a real dead
    /// letter rather than a hand-written row.
    /// </summary>
    public const string PoisonSku = "POISON";
}

/// <summary>The order left the warehouse.</summary>
public sealed record OrderShipped(string Carrier, DateTimeOffset OccurredAt) : IOrderEvent;

/// <summary>The order was cancelled before it shipped.</summary>
public sealed record OrderCancelled(string Reason, DateTimeOffset OccurredAt) : IOrderEvent;
