namespace PostgresStudio.SampleData;

/// <summary>
/// The customers the Marten sample seeds, as far as the relational demo needs to know them: how many there are, and
/// each one's id.
/// </summary>
/// <remarks>
/// <c>legacy.customer_credit</c> holds a credit line per Marten customer, keyed by the customer's document id, so the
/// relational seeding has to name the same ids the Marten seeder writes. It cannot ask the Marten seeder - this project
/// references no Marten (D17) - so the ids are defined here, and <c>MartenStudio.SampleDomain.SampleDataSeeder</c>'s
/// <c>CustomerCount</c> and <c>CustomerId</c> are this class: one definition, so the two can never drift apart.
/// </remarks>
public static class SampleCustomers
{
    /// <summary>How many customers the Marten sample seeds, numbered from one.</summary>
    public const int Count = 25;

    /// <summary>
    /// The id of seeded customer <paramref name="index" />, one to <see cref="Count" />, stable across runs - which is
    /// what lets <c>legacy.customer_credit</c> point at the same customers every time it is filled.
    /// </summary>
    /// <param name="index">The customer's number.</param>
    public static Guid Id(int index)
    {
        // The Marten seeder's deterministic id for kind "customer": the kind's UTF-8 bytes, then the index in the last
        // four bytes. Unchanged from the seeder it was taken from, so every id an existing database holds still matches.
        byte[] bytes = new byte[16];
        System.Text.Encoding.UTF8.GetBytes("customer").CopyTo(bytes, 0);
        BitConverter.GetBytes(index).CopyTo(bytes, 12);
        return new Guid(bytes);
    }
}
