using Marten;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.SampleDomain.Generation;
using MartenStudio.Services.Events;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// A generated set that is then truncated, so that "removes only what it generated" is an assertion.
/// </summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
/// <remarks>
/// Its own fixture because the tests in it write. The plan is <see cref="DemoDataSize.Small" />: what is
/// being tested is which rows go and which stay, and that is the same question at twenty-five rows as at
/// six hundred thousand - only faster.
/// </remarks>
public sealed class TruncationDataSetFixture(PostgresFixture postgres) : GeneratedDataFixtureBase(postgres)
{
    /// <inheritdoc />
    public override string Schema => "generation_truncate";

    /// <inheritdoc />
    internal override DemoDataPlan Plan => DemoDataPlan.For(DemoDataSize.Small) with { AnalyzeWhenDone = false };
}

/// <summary>
/// Truncation: every generated document goes, every seeded one stays, and the generated streams are
/// archived rather than silently left behind.
/// </summary>
/// <param name="fixture">The generated set, built once for this class.</param>
public class DemoDataTruncationLiveTests(TruncationDataSetFixture fixture) : IClassFixture<TruncationDataSetFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// One truncation, with every before-and-after assertion in it.
    /// </summary>
    /// <remarks>
    /// One test rather than five, because truncation is a one-way door: a second test in this class
    /// would be asserting about a database the first one had already emptied, which is a test that
    /// passes for the wrong reason.
    /// </remarks>
    [PostgresFact]
    public async Task Truncation_removes_the_generated_rows_and_leaves_the_seeded_ones()
    {
        IDocumentStore store = fixture.Marten.Store;
        DemoDataPlan plan = DemoDataPlan.For(DemoDataSize.Small);

        await using (IQuerySession before = store.QuerySession())
        {
            (await before.Query<Customer>().CountAsync(Token)).Should().Be(plan.Customers + 25);
            (await before.Query<Product>().CountAsync(Token)).Should().Be(plan.Products + 12);
        }

        // The run was recorded, which is the only way a later process could find the streams it appended.
        var truncator = new DemoDataTruncator(store);

        IReadOnlyList<DemoDataRun> runs = await truncator.ListRunsAsync(Token);
        runs.Should().ContainSingle(x => x.Id == fixture.RunId);
        runs.Single(x => x.Id == fixture.RunId).Completed.Should().BeTrue();

        var archived = 0;
        DemoDataTruncation result = await truncator.TruncateAsync(
            deleteAllEventData: false,
            progress: count => archived = count,
            Token);

        result.Runs.Should().Be(1);
        result.StreamsArchived.Should().Be(plan.Streams);
        result.EventDataDeleted.Should().BeFalse();
        archived.Should().Be(plan.Streams);

        await using (IQuerySession after = store.QuerySession())
        {
            // Exactly the seeder's rows are left, in every collection the generator writes.
            (await after.Query<Customer>().CountAsync(Token)).Should().Be(25);
            (await after.Query<Product>().CountAsync(Token)).Should().Be(12);
            (await after.Query<Vehicle>().CountAsync(Token)).Should().Be(10);
            (await after.Query<MediaAsset>().CountAsync(Token)).Should().Be(1);
            (await after.Query<AuditNote>().CountAsync(Token)).Should().Be(8);

            // Order is soft-deleted, so a generated order that was only marked deleted would still be a
            // row. HardDeleteWhere is what makes "truncate" mean what it says.
            (await after.Query<Order>().CountAsync(Token)).Should().Be(12, "three of the seeder's fifteen are soft-deleted");

            (await after.Query<DemoDataRun>().CountAsync(Token)).Should().Be(0);
        }

        // Invoices are conjoined, so each tenant has to have been visited.
        foreach (string tenantId in MartenStudio.SampleDomain.SampleStore.TenantIds)
        {
            await using IQuerySession tenant = store.QuerySession(tenantId);
            (await tenant.Query<Invoice>().CountAsync(Token)).Should().Be(3, "the seeder writes three per tenant");
        }

        // The generated streams are archived, not deleted: Marten has no operation that deletes a stream,
        // and the events screens have to be able to tell the difference.
        using var events = fixture.Marten.Resolve<IEventDataService>();

        var streamId = DemoDataGenerator.StreamId(fixture.RunId, 0).ToString();

        MartenStudio.Services.Events.StreamState state = await events.Service.GetStreamAsync(
            fixture.Marten.Scope, streamId, Token);

        state.Exists.Should().BeTrue("archiving is not deleting");
        state.IsArchived.Should().BeTrue();

        // ... and the seeder's own streams are untouched.
        MartenStudio.Services.Events.StreamState seeded = await events.Service.GetStreamAsync(
            fixture.Marten.Scope,
            MartenStudio.SampleDomain.SampleDataSeeder.OrderStreamId(1).ToString(),
            Token);

        seeded.Exists.Should().BeTrue();
        seeded.IsArchived.Should().BeFalse();
    }
}
