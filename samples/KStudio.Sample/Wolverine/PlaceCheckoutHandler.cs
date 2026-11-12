using Marten;

using Wolverine;

namespace KStudio.Sample.Wolverine;

/// <summary>
/// The Wolverine handler of <see cref="PlaceCheckout" />: it stores a <see cref="CheckoutOrder" /> in the sample's Marten store, in the message's
/// tenant, and returns the <see cref="CheckoutConfirmed" /> (with the order id as its key) Wolverine publishes to <c>checkout.confirmed.v1</c>. It is application code: KStudio and
/// Marten Studio only ever read what it wrote.
/// </summary>
public static class PlaceCheckoutHandler
{
    /// <summary>Stores the order and confirms it.</summary>
    /// <param name="command">The command.</param>
    /// <param name="envelope">Its envelope: the tenant travels in it.</param>
    /// <param name="store">The sample's document store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static async Task<DeliveryMessage<CheckoutConfirmed>> Handle(PlaceCheckout command, Envelope envelope, IDocumentStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(store);

        // The tenant of the message: a conjoined type needs one, and a message with none is the sample's mistake, not a default tenant's.
        string tenantId = envelope.TenantId ?? throw new InvalidOperationException($"The command {command.OrderId} carries no tenant id.");

        await using IDocumentSession session = store.LightweightSession(tenantId);
        session.Store(new CheckoutOrder { Id = command.OrderId, Customer = command.Customer, Total = command.Total, PlacedAt = DateTimeOffset.UtcNow });
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The confirmation's Kafka key is the order id too: Wolverine keys a record by its PartitionKey, and the topics' correlation field is the key.
        return new CheckoutConfirmed(command.OrderId, command.Total).WithDeliveryOptions(new DeliveryOptions { PartitionKey = command.OrderId });
    }
}
