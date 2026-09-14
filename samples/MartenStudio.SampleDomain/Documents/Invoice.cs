namespace MartenStudio.SampleDomain.Documents;

/// <summary>
/// An invoice: an int id (Marten's HiLo sequence) and conjoined multi-tenancy, so the studio's tenant
/// selector has something to be about and the tenant filter has rows to hide.
/// </summary>
public sealed class Invoice
{
    /// <summary>An int id, which Marten assigns from a HiLo sequence rather than from the client.</summary>
    public int Id { get; set; }

    public Guid CustomerId { get; set; }

    public string Number { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public bool Paid { get; set; }

    public DateTimeOffset IssuedAt { get; set; }
}
