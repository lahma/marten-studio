using JasperFx.Events;

using Marten;
using Marten.Events.Projections;

namespace MartenStudio.SampleDomain.Events;

/// <summary>
/// One row per shipped order, written by <see cref="ShipmentTracker" />.
/// </summary>
public sealed class Shipment
{
    /// <summary>The order stream this shipment belongs to.</summary>
    public Guid Id { get; set; }

    public string Carrier { get; set; } = string.Empty;

    public DateTimeOffset ShippedAt { get; set; }
}

/// <summary>
/// The demo's deliberately fragile projection: an <b>asynchronous</b> event projection that throws on an
/// item whose SKU is <c>POISON</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is what puts a real <c>DeadLetterEvent</c> row in the demo database. A dead letter written by
/// hand would be a row with no history behind it; this one was produced by the daemon the way every dead
/// letter in a real system is - a projection threw, the shard retried, and the event was skipped and
/// recorded. <c>SampleStore.Configure</c> sets <c>Projections.Errors.SkipApplyErrors</c> so that the
/// shard carries on afterwards instead of pausing, which is what makes the demo repeatable.
/// </para>
/// <para>
/// It is an <c>EventProjection</c> rather than an aggregation so that the projections screen has all
/// three shapes to show: single-stream inline, multi-stream async, and "do anything" async.
/// </para>
/// </remarks>
internal sealed partial class ShipmentTracker : EventProjection
{
    public ShipmentTracker()
    {
        Name = "ShipmentTracker";
    }

    public static void Project(IEvent<OrderShipped> shipped, IDocumentOperations operations)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(operations);

        operations.Store(new Shipment
        {
            Id = shipped.StreamId,
            Carrier = shipped.Data.Carrier,
            ShippedAt = shipped.Data.OccurredAt
        });
    }

    public static void Project(IEvent<ItemAdded> added, IDocumentOperations operations)
    {
        ArgumentNullException.ThrowIfNull(added);

        if (string.Equals(added.Data.Sku, ItemAdded.PoisonSku, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ShipmentTracker cannot ship SKU '{ItemAdded.PoisonSku}'. This projection throws on purpose so that the " +
                "demo database has a dead letter that the daemon really produced.");
        }
    }
}
