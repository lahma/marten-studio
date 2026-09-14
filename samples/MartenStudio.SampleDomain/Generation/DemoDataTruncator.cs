using Marten;

using MartenStudio.SampleDomain.Documents;

namespace MartenStudio.SampleDomain.Generation;

/// <summary>What a truncation removed.</summary>
/// <param name="Runs">How many generation runs were found and cleared.</param>
/// <param name="StreamsArchived">How many generated streams were archived.</param>
/// <param name="EventDataDeleted">Whether <em>all</em> event data was deleted, generated or not.</param>
public readonly record struct DemoDataTruncation(int Runs, int StreamsArchived, bool EventDataDeleted);

/// <summary>
/// Removes what a generation run wrote, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Documents are easy and streams are not.</b> Every generated document carries
/// <see cref="IGeneratedDocument.GeneratedRun" />, so one <c>DeleteWhere</c> per collection removes
/// exactly the generated rows and leaves the seeder's demo data alone. Marten has no equivalent for
/// streams - there is no "delete these streams" operation at any level of the API - so the two honest
/// options are to <em>archive</em> the generated streams, which is a real Marten operation and leaves
/// the events in place under <c>mt_events</c> with <c>is_archived</c>, or to delete every event in the
/// store with <c>Advanced.Clean.DeleteAllEventDataAsync()</c>, which also takes the demo's own
/// twenty-five order streams and every projection's progress with it.
/// </para>
/// <para>
/// The destructive one is never the default and never implied: the caller has to ask for it by name.
/// </para>
/// </remarks>
public sealed class DemoDataTruncator
{
    private readonly IDocumentStore store;

    /// <summary>Points a truncator at a store.</summary>
    /// <param name="store">The store to clear generated data from.</param>
    public DemoDataTruncator(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
    }

    /// <summary>
    /// Deletes every generated document, archives every generated stream, and forgets the runs.
    /// </summary>
    /// <param name="deleteAllEventData">
    /// When true, every event in the store is deleted rather than the generated streams archived. This
    /// takes the seeded demo streams and all projection progress with it, so it exists only for the
    /// caller who typed the confirmation.
    /// </param>
    /// <param name="progress">Called with the number of streams archived so far.</param>
    /// <param name="cancellationToken">Stops between batches.</param>
    /// <returns>What was removed.</returns>
    public async Task<DemoDataTruncation> TruncateAsync(
        bool deleteAllEventData = false,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DemoDataRun> runs = await ListRunsAsync(cancellationToken).ConfigureAwait(false);

        await DeleteGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);

        int archived = 0;

        if (deleteAllEventData)
        {
            // Everything: the generated streams, the seeded ones, mt_streams, mt_events and every
            // projection's progression row. Marten offers no narrower cleaner.
            await store.Advanced.Clean.DeleteAllEventDataAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (DemoDataRun run in runs)
            {
                archived += await ArchiveRunStreamsAsync(run, archived, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Last, so that a cancelled truncation can be resumed: while the run records are still there,
        // the streams they name can still be found.
        await using (IDocumentSession session = store.LightweightSession())
        {
            session.DeleteWhere<DemoDataRun>(static x => x.Id != null);
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DemoDataTruncation(runs.Count, archived, deleteAllEventData);
    }

    /// <summary>Every generation run this store has a record of, oldest first.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<DemoDataRun>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        await using IQuerySession session = store.QuerySession();

        try
        {
            return await session.Query<DemoDataRun>()
                .OrderBy(x => x.StartedAt)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.UndefinedTable)
        {
            // Nobody has ever generated anything here. That is not an error, and creating the table to
            // find out would be a write on a read path.
            return [];
        }
    }

    /// <summary>
    /// Deletes generated rows from every demo collection, in one statement per collection per tenant.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    /// Three things here are not obvious. <c>Order</c> is hard-deleted rather than soft-deleted: the
    /// collection is configured <c>SoftDeleted()</c>, so the ordinary <c>DeleteWhere</c> would only set
    /// <c>mt_deleted</c> and leave a million rows in the table - the opposite of what "truncate" means.
    /// It also goes first, in a transaction of its own, because it holds a foreign key to
    /// <c>Customer</c>. And <c>Invoice</c> is conjoined multi-tenant, so it needs one session per
    /// tenant; a session for one tenant cannot see, and therefore cannot delete, another tenant's rows.
    /// </remarks>
    public async Task DeleteGeneratedDocumentsAsync(CancellationToken cancellationToken = default)
    {
        // Orders first, and in a transaction of their own: Order declares a foreign key to Customer, and
        // Marten orders the statements in a batch by the order they were queued rather than by the
        // dependency graph - so deleting customers in the same batch is a 23503 from Postgres. This is
        // the ordinary "delete the children before the parent" rule, and it is the reason the demo
        // domain declares that foreign key at all.
        await using (IDocumentSession orders = store.LightweightSession())
        {
            // Hard, not soft: the collection is SoftDeleted(), and a truncation that only set
            // mt_deleted would leave every generated row in the table.
            orders.HardDeleteWhere<Order>(static x => x.GeneratedRun != null);
            await orders.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (IDocumentSession session = store.LightweightSession())
        {
            session.DeleteWhere<Customer>(static x => x.GeneratedRun != null);
            session.DeleteWhere<Product>(static x => x.GeneratedRun != null);
            session.DeleteWhere<Vehicle>(static x => x.GeneratedRun != null);
            session.DeleteWhere<AuditNote>(static x => x.GeneratedRun != null);
            session.DeleteWhere<MediaAsset>(static x => x.GeneratedRun != null);

            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (string tenantId in SampleStore.TenantIds)
        {
            await using IDocumentSession tenantSession = store.LightweightSession(tenantId);
            tenantSession.DeleteWhere<Invoice>(static x => x.GeneratedRun != null);
            await tenantSession.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> ArchiveRunStreamsAsync(
        DemoDataRun run,
        int alreadyArchived,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        const int BatchSize = 500;

        int archived = 0;

        for (int start = 0; start < run.Streams; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = Math.Min(run.Streams, start + BatchSize);

            await using IDocumentSession session = store.LightweightSession();

            for (int index = start; index < end; index++)
            {
                session.Events.ArchiveStream(DemoDataGenerator.StreamId(run.Id, index));
            }

            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            archived += end - start;
            progress?.Invoke(alreadyArchived + archived);
        }

        return archived;
    }
}
