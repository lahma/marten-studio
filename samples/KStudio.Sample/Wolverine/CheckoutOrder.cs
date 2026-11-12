namespace KStudio.Sample.Wolverine;

/// <summary>
/// The Marten document <see cref="PlaceCheckoutHandler" /> stores for each command, in the command's tenant. Its id is the Kafka key of the
/// command, which is what the topics' <c>Correlation(c =&gt; c.Key().Document("CheckoutOrder"))</c> declaration says: KStudio links a record to
/// this document, and Marten Studio's page for it back to the records.
/// </summary>
/// <remarks>It lives in the sample, not in <c>MartenStudio.SampleDomain</c>, which does not know Kafka (D17).</remarks>
public sealed class CheckoutOrder
{
    /// <summary>The order's id: <c>chk-0001</c> and on.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Who placed the order.</summary>
    public string Customer { get; set; } = string.Empty;

    /// <summary>The order's total.</summary>
    public decimal Total { get; set; }

    /// <summary>When the handler stored the document.</summary>
    public DateTimeOffset PlacedAt { get; set; }
}
