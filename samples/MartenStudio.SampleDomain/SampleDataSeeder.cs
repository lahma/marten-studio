using Marten;
using Marten.Schema;

using MartenStudio.SampleDomain.Documents;

namespace MartenStudio.SampleDomain;

/// <summary>
/// A marker document, so the seeder can tell a fresh database from one it has already filled.
/// </summary>
public sealed class SeedMarker
{
    /// <summary>The one row there is ever meant to be.</summary>
    public static readonly Guid WellKnownId = new("0f1a1f7e-6d2f-4a4b-9f1d-9f9e2c9a0001");

    public Guid Id { get; set; }

    public int Version { get; set; }

    public DateTimeOffset SeededAt { get; set; }
}

/// <summary>
/// Writes the demo data on host start, once.
/// </summary>
/// <remarks>
/// Idempotent through <see cref="SeedMarker" />: the sample host is expected to be started and stopped
/// repeatedly against a reused container, and a seeder that appended another twenty-five customers every
/// time would make every screen in the studio a lie about what the demo contains.
/// </remarks>
public sealed class SampleDataSeeder : IInitialData
{
    /// <summary>Bumped when the data below changes, so an existing database is re-seeded.</summary>
    private const int SeedVersion = 1;

    public async Task Populate(IDocumentStore store, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(store);

        await using IDocumentSession session = store.LightweightSession();

        SeedMarker? marker = await session.LoadAsync<SeedMarker>(SeedMarker.WellKnownId, cancellation);
        if (marker is not null && marker.Version >= SeedVersion)
        {
            return;
        }

        // A fixed seed, so two runs of the demo describe the same data and a screenshot stays true.
        Random random = new(20260914);

        List<Customer> customers = [];
        for (int i = 1; i <= 25; i++)
        {
            customers.Add(new Customer
            {
                Id = DeterministicGuid("customer", i),
                Name = $"Customer {i:00}",
                Email = $"customer{i:00}@example.com",
                Address = new Address($"{i} Example Street", Cities[i % Cities.Length], $"{10000 + i}", "FI"),
                Tags = i % 3 == 0 ? ["preferred", "eu"] : ["eu"],
                RegisteredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i)
            });
        }

        session.Store(customers.ToArray());

        List<Order> orders = [];
        for (int i = 1; i <= 15; i++)
        {
            Customer customer = customers[i % customers.Count];
            List<OrderLine> lines =
            [
                new($"SKU-{i:000}", random.Next(1, 4), 19.90m + i),
                new($"SKU-{i + 100:000}", random.Next(1, 3), 5.50m)
            ];

            orders.Add(new Order
            {
                Id = new OrderId(DeterministicGuid("order", i)),
                CustomerId = customer.Id,
                Reference = $"ORD-2026-{i:0000}",
                Total = lines.Sum(static line => line.Quantity * line.UnitPrice),
                Status = i % 4 == 0 ? "Shipped" : "Placed",
                Lines = lines,
                PlacedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddHours(i * 7)
            });
        }

        session.Store(orders.ToArray());

        session.Store(new SeedMarker
        {
            Id = SeedMarker.WellKnownId,
            Version = SeedVersion,
            SeededAt = DateTimeOffset.UtcNow
        });

        await session.SaveChangesAsync(cancellation);

        // Three of the fifteen are soft-deleted, so the documents screen has a tri-state to show: alive,
        // deleted, and "deleted rows exist but are hidden". A second round trip, because Marten has to
        // have written the rows before they can be marked deleted.
        await using (IDocumentSession deletions = store.LightweightSession())
        {
            foreach (Order order in orders.Take(3))
            {
                deletions.Delete(order);
            }

            await deletions.SaveChangesAsync(cancellation);
        }

        // Invoices are conjoined multi-tenant, so each tenant gets its own session and its own rows.
        int invoiceIndex = 0;
        foreach (string tenantId in SampleStore.TenantIds)
        {
            await using IDocumentSession tenantSession = store.LightweightSession(tenantId);
            for (int i = 0; i < 3; i++)
            {
                invoiceIndex++;
                Customer customer = customers[invoiceIndex % customers.Count];
                tenantSession.Store(new Invoice
                {
                    CustomerId = customer.Id,
                    Number = $"{tenantId.ToUpperInvariant()}-INV-{invoiceIndex:000}",
                    Amount = 100m * invoiceIndex,
                    Paid = invoiceIndex % 2 == 0,
                    IssuedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero).AddDays(invoiceIndex)
                });
            }

            await tenantSession.SaveChangesAsync(cancellation);
        }
    }

    private static readonly string[] Cities = ["Helsinki", "Tampere", "Turku", "Oulu"];

    /// <summary>
    /// The same id on every run, so re-seeding replaces rows rather than adding a second set of them.
    /// </summary>
    private static Guid DeterministicGuid(string kind, int index)
    {
        byte[] bytes = new byte[16];
        System.Text.Encoding.UTF8.GetBytes(kind).CopyTo(bytes, 0);
        BitConverter.GetBytes(index).CopyTo(bytes, 12);
        return new Guid(bytes);
    }
}
