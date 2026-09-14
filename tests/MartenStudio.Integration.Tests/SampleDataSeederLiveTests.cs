using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Documents;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The sample seeder's two halves compose, and running it twice leaves the store exactly as it was.
/// </summary>
/// <remarks>
/// <para>
/// Two packets wrote this seeder. P5 added <c>SeedEventsAsync</c>, which runs <em>before</em> the seed
/// marker is read, because appending is not upserting and a database an older build had already marked
/// as seeded would otherwise never get the events. P2 added the documents and bumped
/// <c>SeedVersion</c> to 2, which means a store seeded by an older build is cleared and re-seeded - and
/// the clear lists document types only. So the two are easy to get wrong in a way no unit test sees: an
/// events guard that is not consulted, a clear that takes the streams with it, or a re-seed that appends
/// a second set of everything.
/// </para>
/// <para>
/// What this asserts is the property the sample host depends on: the same <c>dotnet run</c> twice
/// against a reused container describes the same data, so every count the studio renders stays true.
/// The counts are taken through Marten rather than against <c>information_schema</c>, because
/// soft-deleted orders and conjoined invoices are exactly the rows a raw <c>count(*)</c> would get wrong.
/// </para>
/// </remarks>
public class SampleDataSeederLiveTests(SampleDataSeederLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<SampleDataSeederLiveTests.Fixture>
{
    /// <summary>The demo store and its seed, once for the class.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres);

    [PostgresFact]
    public async Task Both_halves_of_the_seeder_ran()
    {
        SeedCounts counts = await CountAsync();

        // The documents P2 seeds, and the events and streams P5 seeds. Every number is non-zero, so a
        // later "the counts did not change" can never pass because nothing was written at all.
        counts.Customers.Should().Be(25);
        counts.AliveOrders.Should().Be(12, "three of the fifteen are soft-deleted on purpose");
        counts.Products.Should().Be(12);
        counts.Cars.Should().Be(6);
        counts.Trucks.Should().Be(4);
        counts.AuditNotes.Should().Be(8);
        counts.MinimalNotes.Should().Be(1);
        counts.MediaAssets.Should().Be(1);
        counts.InvoicesByTenant.Should().Equal(3, 3);

        counts.Events.Should().BeGreaterThan(0);
        counts.Streams.Should().Be(SampleDataSeeder.OrderStreamCount);
    }

    /// <summary>
    /// The sample host is started and stopped repeatedly against one container, so the seeder runs again
    /// on a store it has already filled. Nothing may move.
    /// </summary>
    [PostgresFact]
    public async Task Running_the_seeder_again_changes_nothing()
    {
        SeedCounts before = await CountAsync();

        await new SampleDataSeeder().Populate(Store, TestContext.Current.CancellationToken);

        SeedCounts after = await CountAsync();

        after.Should().BeEquivalentTo(before);
    }

    /// <summary>
    /// The version-bump path, which is the one the two halves could break: the marker is old, so the
    /// documents are cleared and written again, while the events - which are not in the clear list and
    /// have an idempotency guard of their own - must survive untouched rather than be doubled.
    /// </summary>
    [PostgresFact]
    public async Task A_re_seed_replaces_the_documents_and_leaves_the_streams_alone()
    {
        SeedCounts before = await CountAsync();

        await using (IDocumentSession session = Store.LightweightSession())
        {
            // What a store seeded by an earlier build looks like.
            session.Store(new SeedMarker
            {
                Id = SeedMarker.WellKnownId,
                Version = 1,
                SeededAt = DateTimeOffset.UtcNow,
            });

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await new SampleDataSeeder().Populate(Store, TestContext.Current.CancellationToken);

        SeedCounts after = await CountAsync();

        after.Should().BeEquivalentTo(before);
    }

    private async Task<SeedCounts> CountAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        await using IQuerySession session = Store.QuerySession();

        var invoices = new List<int>();
        foreach (string tenantId in SampleStore.TenantIds)
        {
            await using IQuerySession tenantSession = Store.QuerySession(tenantId);

            invoices.Add(await tenantSession.Query<Invoice>().CountAsync(token));
        }

        return new SeedCounts(
            await session.Query<Customer>().CountAsync(token),
            await session.Query<Order>().CountAsync(token),
            await session.Query<Product>().CountAsync(token),
            await session.Query<Car>().CountAsync(token),
            await session.Query<Truck>().CountAsync(token),
            await session.Query<AuditNote>().CountAsync(token),
            await session.Query<MinimalNote>().CountAsync(token),
            await session.Query<MediaAsset>().CountAsync(token),
            invoices,
            await session.Events.QueryAllRawEvents().CountAsync(token),
            await CountStreamsAsync(token));
    }

    private async Task<int> CountStreamsAsync(CancellationToken token)
    {
        var streams = 0;

        for (var i = 1; i <= SampleDataSeeder.OrderStreamCount; i++)
        {
            await using IQuerySession session = Store.QuerySession();

            if (await session.Events.FetchStreamStateAsync(SampleDataSeeder.OrderStreamId(i), token) is not null)
            {
                streams++;
            }
        }

        return streams;
    }

    private sealed record SeedCounts(
        int Customers,
        int AliveOrders,
        int Products,
        int Cars,
        int Trucks,
        int AuditNotes,
        int MinimalNotes,
        int MediaAssets,
        IReadOnlyList<int> InvoicesByTenant,
        int Events,
        int Streams);
}
