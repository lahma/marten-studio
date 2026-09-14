using JasperFx.Events;

using Marten;
using Marten.Schema;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.SampleDomain.Events;

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
    private const int SeedVersion = 3;

    /// <summary>How big <see cref="MediaAsset" />'s decoded payload is: base64 makes it about 2 MB.</summary>
    private const int MediaAssetBytes = 1_500_000;

    public async Task Populate(IDocumentStore store, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(store);

        // Before the marker check, not after it: the events have an idempotency guard of their own, and a
        // database that an older build already marked as seeded would otherwise never get them.
        await SeedEventsAsync(store, cancellation);

        await using IDocumentSession session = store.LightweightSession();

        SeedMarker? marker = await session.LoadAsync<SeedMarker>(SeedMarker.WellKnownId, cancellation);
        if (marker is not null && marker.Version >= SeedVersion)
        {
            return;
        }

        if (marker is not null)
        {
            // Re-seeding, not seeding. Writing the same ids again is not enough: Order uses optimistic
            // concurrency, so storing one whose mt_version this session never read fails the check and
            // takes the whole batch with it. Clearing the demo's own collections first is what makes a
            // version bump a thing a developer can actually run.
            await ClearAsync(store, cancellation);
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

        // A hierarchy: cars and trucks share the vehicle table and are told apart by mt_doc_type. Stored
        // as the concrete types, which is what makes Marten write the discriminator.
        List<Car> cars = [];
        List<Truck> trucks = [];

        for (int i = 1; i <= 6; i++)
        {
            cars.Add(new Car
            {
                Id = DeterministicGuid("car", i),
                Make = Makes[i % Makes.Length],
                Model = $"C{i:00}",
                Year = 2018 + (i % 8),
                Doors = i % 2 == 0 ? 5 : 3,
                IsConvertible = i % 5 == 0
            });
        }

        for (int i = 1; i <= 4; i++)
        {
            trucks.Add(new Truck
            {
                Id = DeterministicGuid("truck", i),
                Make = Makes[(i + 1) % Makes.Length],
                Model = $"T{i:00}",
                Year = 2015 + i,
                PayloadTonnes = 3.5m * i,
                Axles = 2 + (i % 3)
            });
        }

        session.Store(cars.ToArray());
        session.Store(trucks.ToArray());

        // A string primary key the application assigns, through UseIdentityKey().
        List<Product> products = [];
        for (int i = 1; i <= 12; i++)
        {
            products.Add(new Product
            {
                Id = $"SKU-{i:000}",
                Name = $"Product {i:00}",
                Category = Categories[i % Categories.Length],
                Price = 9.90m + (i * 3),
                Discontinued = i % 7 == 0
            });
        }

        session.Store(products.ToArray());

        // Every optional metadata column on, and every one off, so both ends of the range have rows.
        List<AuditNote> auditNotes = [];
        for (int i = 1; i <= 8; i++)
        {
            auditNotes.Add(new AuditNote
            {
                Id = DeterministicGuid("audit", i),
                Subject = $"Audit note {i:00}",
                Body = $"Something worth writing down happened, for the {i} time.",
                Severity = i % 4 == 0 ? "warning" : "info"
            });
        }

        session.Store(auditNotes.ToArray());

        session.Store(new MinimalNote
        {
            Id = DeterministicGuid("minimal", 1),
            Text = "This collection's table is id and data and nothing else."
        });

        // One two-megabyte document, written once. Seeding fifty of them would make every `dotnet run` of
        // the sample a hundred-megabyte write for no extra demonstration.
        session.Store(BuildMediaAsset());

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

    /// <summary>
    /// Appends the demo's order streams, one of them poisoned and one of them archived.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the documents above and idempotent on its own terms, because appending is not
    /// upserting: running it twice would double every stream rather than replace it. The guard is the
    /// first stream's own state, so this is safe to call directly - which the integration suite does,
    /// against a store the sample host never touched.
    /// </para>
    /// <para>
    /// Correlation, causation and headers are set on every session: <c>SampleStore.Configure</c> turns
    /// those columns on, and a column that is enabled and always null tells a studio nothing.
    /// </para>
    /// <para>
    /// This runs from <c>IInitialData</c>, which is before the daemon starts. That is fine and is the
    /// point: the projections screen opens on a daemon that has a backlog to work through.
    /// </para>
    /// </remarks>
    /// <param name="store">The store to append to.</param>
    /// <param name="cancellation">Cancels the seeding.</param>
    /// <returns>How many streams were appended - zero when the events were already there.</returns>
    public static async Task<int> SeedEventsAsync(IDocumentStore store, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        await using (IQuerySession probe = store.QuerySession())
        {
            StreamState? existing = await probe.Events.FetchStreamStateAsync(OrderStreamId(1), cancellation);
            if (existing is not null)
            {
                return 0;
            }
        }

        DateTimeOffset day = new(2026, 2, 1, 8, 0, 0, TimeSpan.Zero);

        await using IDocumentSession session = store.LightweightSession();

        session.CorrelationId = "sample-seed";
        session.CausationId = "SampleDataSeeder.SeedEventsAsync";
        session.SetHeader("source", "marten-studio-sample");

        for (int i = 1; i <= OrderStreamCount; i++)
        {
            // Five orders a day across five days, so DailySales has more than one row to roll up into.
            DateTimeOffset placedAt = day.AddDays((i - 1) / 5).AddHours(i % 5);

            List<object> events =
            [
                new OrderPlaced(OrderStreamId(i), $"Customer {i % 25 + 1:00}", placedAt),
                new ItemAdded($"SKU-{i:000}", i % 3 + 1, 19.90m + i, placedAt.AddMinutes(1))
            ];

            // One stream carries the SKU that ShipmentTracker throws on. That is the whole dead-letter
            // story: nothing writes a dead letter here, the daemon does.
            if (i == PoisonedOrderIndex)
            {
                events.Add(new ItemAdded(ItemAdded.PoisonSku, 1, 999m, placedAt.AddMinutes(2)));
            }

            if (i % 5 == 0)
            {
                events.Add(new OrderShipped($"Carrier {i % 3 + 1}", placedAt.AddHours(6)));
            }
            else if (i % 7 == 0)
            {
                events.Add(new OrderCancelled("Out of stock", placedAt.AddHours(2)));
            }

            session.Events.StartStream(OrderStreamId(i), events);
        }

        await session.SaveChangesAsync(cancellation);

        // One archived stream, so the events screens have a stream that is deliberately out of the
        // default view. Archiving is queued on a session like any other operation.
        await using IDocumentSession archiving = store.LightweightSession();
        archiving.CorrelationId = "sample-seed";
        archiving.Events.ArchiveStream(OrderStreamId(ArchivedOrderIndex));
        await archiving.SaveChangesAsync(cancellation);

        return OrderStreamCount;
    }

    /// <summary>How many order streams <see cref="SeedEventsAsync" /> appends.</summary>
    public const int OrderStreamCount = 25;

    /// <summary>The order whose stream carries the poisoned item.</summary>
    public const int PoisonedOrderIndex = 13;

    /// <summary>The order whose stream is archived after it is written.</summary>
    public const int ArchivedOrderIndex = 24;

    /// <summary>The stream id of demo order <paramref name="index" />, stable across runs.</summary>
    public static Guid OrderStreamId(int index) => DeterministicGuid("order-stream", index);

    /// <summary>
    /// Empties the demo's own collections, and only those: a sample store may be sharing a database with
    /// something that is not a demo.
    /// </summary>
    private static async Task ClearAsync(IDocumentStore store, CancellationToken cancellation)
    {
        Type[] seeded =
        [
            typeof(Customer), typeof(Order), typeof(Invoice), typeof(Product),
            typeof(Vehicle), typeof(AuditNote), typeof(MinimalNote), typeof(MediaAsset),
        ];

        foreach (Type type in seeded)
        {
            await store.Advanced.Clean.DeleteDocumentsByTypeAsync(type, cancellation);
        }
    }

    private static readonly string[] Cities = ["Helsinki", "Tampere", "Turku", "Oulu"];

    private static readonly string[] Makes = ["Volvo", "Scania", "Toyota", "Ford"];

    private static readonly string[] Categories = ["tools", "hardware", "consumables"];

    /// <summary>
    /// The one big document, built from a deterministic byte pattern rather than from randomness so that
    /// two runs of the demo produce the same bytes and the same size on every screen that reports it.
    /// </summary>
    private static MediaAsset BuildMediaAsset()
    {
        byte[] payload = new byte[MediaAssetBytes];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte) (i * 31 % 251);
        }

        return new MediaAsset
        {
            Id = DeterministicGuid("media", 1),
            FileName = "sample-asset.bin",
            ContentType = "application/octet-stream",
            ByteCount = payload.Length,
            Base64 = Convert.ToBase64String(payload)
        };
    }

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
