using Marten.Events.Aggregation;

namespace MartenStudio.SampleDomain.Events;

/// <summary>
/// One row per order stream: what the order is, now.
/// </summary>
public sealed class OrderSummary
{
    /// <summary>The stream id, which Marten assigns from the stream this view aggregates.</summary>
    public Guid Id { get; set; }

    public string Customer { get; set; } = string.Empty;

    public int ItemCount { get; set; }

    public decimal Total { get; set; }

    /// <summary><c>Placed</c>, <c>Shipped</c> or <c>Cancelled</c>.</summary>
    public string Status { get; set; } = "Placed";

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ShippedAt { get; set; }

    public string? CancelledReason { get; set; }
}

/// <summary>
/// <see cref="OrderSummary" />, aggregated from one stream, <b>inline</b>.
/// </summary>
/// <remarks>
/// Inline on purpose: the projections screen has to show that a lifecycle is a property of the
/// registration rather than of the projection type, and an inline projection is one the async daemon
/// never runs - so it appears in the table with no shard progress at all, which is exactly the state the
/// page has to render honestly.
/// </remarks>
public sealed partial class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    public OrderSummaryProjection()
    {
        Name = "OrderSummary";
    }

    public static OrderSummary Create(OrderPlaced placed) => new()
    {
        Customer = placed.Customer,
        PlacedAt = placed.OccurredAt,
        Status = "Placed"
    };

    public static void Apply(ItemAdded added, OrderSummary view)
    {
        view.ItemCount += added.Quantity;
        view.Total += added.Quantity * added.Price;
    }

    public static void Apply(OrderShipped shipped, OrderSummary view)
    {
        view.Status = "Shipped";
        view.ShippedAt = shipped.OccurredAt;
    }

    public static void Apply(OrderCancelled cancelled, OrderSummary view)
    {
        view.Status = "Cancelled";
        view.CancelledReason = cancelled.Reason;
    }

    /// <summary>
    /// A voided order is not a summary at all: the aggregate deletes itself, so a stream that ends with the event folds to
    /// nothing (Marten 9.57 leaves such a stream alone when it is compacted).
    /// </summary>
    public static bool ShouldDelete(OrderVoided voided) => voided is not null;
}
