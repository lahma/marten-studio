namespace KStudio.Sample.Wolverine;

/// <summary>The command the sample's Wolverine application listens for on <c>checkout.v1</c>.</summary>
/// <param name="OrderId">The order's id, <c>chk-0001</c> and on; it is also the Kafka key (the envelope's <c>PartitionKey</c>).</param>
/// <param name="Customer">Who placed the order.</param>
/// <param name="Total">The order's total.</param>
public sealed record PlaceCheckout(string OrderId, string Customer, decimal Total);

/// <summary>The event the handler returns, which Wolverine cascades to <c>checkout.confirmed.v1</c> in the command's tenant.</summary>
/// <param name="OrderId">The order that was stored.</param>
/// <param name="Total">Its total.</param>
public sealed record CheckoutConfirmed(string OrderId, decimal Total);
