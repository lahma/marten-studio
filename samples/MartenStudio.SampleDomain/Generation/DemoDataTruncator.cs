using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.SampleDomain.Documents;

using Npgsql;

using NpgsqlTypes;

// Casing is Weasel's, not Marten's - ISerializer.Casing returns Weasel.Core.Casing.
using Weasel.Core;

namespace MartenStudio.SampleDomain.Generation;

/// <summary>What a truncation removed.</summary>
/// <param name="Runs">How many generation runs were found and cleared.</param>
/// <param name="DocumentsDeleted">How many generated documents were deleted, across every collection.</param>
/// <param name="StreamsArchived">How many generated streams were archived.</param>
/// <param name="EventDataDeleted">Whether <em>all</em> event data was deleted, generated or not.</param>
public readonly record struct DemoDataTruncation(int Runs, long DocumentsDeleted, int StreamsArchived, bool EventDataDeleted);

/// <summary>Which part of a truncation is happening, for the progress line.</summary>
public enum DemoDataTruncationPhase
{
    /// <summary>Deleting generated documents, a bounded batch at a time.</summary>
    DeletingDocuments,

    /// <summary>Archiving the streams the generation runs appended.</summary>
    ArchivingStreams,

    /// <summary>Deleting every event in the store.</summary>
    DeletingAllEventData,
}

/// <summary>How far a truncation has got.</summary>
/// <param name="Phase">What the truncation is doing.</param>
/// <param name="DocumentsDeleted">Generated documents deleted so far.</param>
/// <param name="StreamsArchived">Generated streams archived so far.</param>
public readonly record struct DemoDataTruncationProgress(
    DemoDataTruncationPhase Phase,
    long DocumentsDeleted,
    int StreamsArchived);

/// <summary>
/// Removes what a generation run wrote, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Documents are easy and streams are not.</b> Every generated document carries
/// <see cref="IGeneratedDocument.GeneratedRun" />, so deleting by that marker removes exactly the
/// generated rows and leaves the seeder's demo data alone. Marten has no equivalent for streams - there
/// is no "delete these streams" operation at any level of the API - so the two honest options are to
/// <em>archive</em> the generated streams, which is a real Marten operation and leaves the events in
/// place under <c>mt_events</c> with <c>is_archived</c>, or to delete every event in the store with
/// <c>Advanced.Clean.DeleteAllEventDataAsync()</c>, which also takes the demo's own twenty-five order
/// streams and every projection's progress with it.
/// </para>
/// <para>
/// The destructive one is never the default and never implied: the caller has to ask for it by name.
/// </para>
/// </remarks>
public sealed class DemoDataTruncator
{
    /// <summary>
    /// The most rows one delete statement removes, unless the constructor is given another bound. See
    /// <see cref="DeleteGeneratedDocumentsAsync" /> for why there is a bound at all; at the Large preset's
    /// cost per customer row (about 65 µs, three foreign-key triggers included) a batch is well under a
    /// second, and the Large preset's 1.2 million documents are about 125 round trips.
    /// </summary>
    public const int DefaultDeleteBatchSize = 10_000;

    /// <summary>
    /// The command timeout for one batch, in seconds. A batch takes about a second; this is here so that
    /// a slow disk shows up as a slow truncation rather than as the connection string's thirty-second
    /// default cancelling the statement - which is exactly how the unbatched delete used to fail.
    /// </summary>
    private const int BatchCommandTimeoutSeconds = 300;

    /// <summary>
    /// The collections the generator writes, children before parents: <c>Order</c> and <c>Invoice</c>
    /// both hold a foreign key to <c>Customer</c>, so both have to be gone before the customers are.
    /// </summary>
    private static readonly Type[] GeneratedCollections =
    [
        typeof(Order), typeof(Invoice), typeof(Customer), typeof(Product),
        typeof(Vehicle), typeof(AuditNote), typeof(MediaAsset),
    ];

    private readonly IDocumentStore store;
    private readonly int deleteBatchSize;

    /// <summary>Points a truncator at a store.</summary>
    /// <param name="store">The store to clear generated data from.</param>
    /// <param name="deleteBatchSize">
    /// The most rows one delete statement removes. The default is what the panel uses; a test passes a
    /// small number to make a small data set take many batches.
    /// </param>
    public DemoDataTruncator(IDocumentStore store, int deleteBatchSize = DefaultDeleteBatchSize)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deleteBatchSize);

        this.store = store;
        this.deleteBatchSize = deleteBatchSize;
    }

    /// <summary>
    /// Deletes every generated document, archives every generated stream, and forgets the runs.
    /// </summary>
    /// <param name="deleteAllEventData">
    /// When true, every event in the store is deleted rather than the generated streams archived. This
    /// takes the seeded demo streams and all projection progress with it, so it exists only for the
    /// caller who typed the confirmation.
    /// </param>
    /// <param name="progress">Called after every batch, with the totals so far.</param>
    /// <param name="cancellationToken">
    /// Stops between batches. Every batch is its own statement and its own transaction, so a cancelled
    /// truncation leaves no half-deleted batch behind, and running it again carries on where it stopped.
    /// </param>
    /// <returns>What was removed.</returns>
    public async Task<DemoDataTruncation> TruncateAsync(
        bool deleteAllEventData = false,
        Action<DemoDataTruncationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DemoDataRun> runs = await ListRunsAsync(cancellationToken).ConfigureAwait(false);

        long documents = await DeleteGeneratedDocumentsAsync(
            deleted => progress?.Invoke(new DemoDataTruncationProgress(DemoDataTruncationPhase.DeletingDocuments, deleted, 0)),
            cancellationToken).ConfigureAwait(false);

        int archived = 0;

        if (deleteAllEventData)
        {
            progress?.Invoke(new DemoDataTruncationProgress(DemoDataTruncationPhase.DeletingAllEventData, documents, 0));

            // Everything: the generated streams, the seeded ones, mt_streams, mt_events and every
            // projection's progression row. Marten offers no narrower cleaner.
            await store.Advanced.Clean.DeleteAllEventDataAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (DemoDataRun run in runs)
            {
                archived += await ArchiveRunStreamsAsync(
                        run,
                        archived,
                        count => progress?.Invoke(new DemoDataTruncationProgress(DemoDataTruncationPhase.ArchivingStreams, documents, count)),
                        cancellationToken)
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

        return new DemoDataTruncation(runs.Count, documents, archived, deleteAllEventData);
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
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Nobody has ever generated anything here. That is not an error, and creating the table to
            // find out would be a write on a read path.
            return [];
        }
    }

    /// <summary>
    /// Deletes generated rows from every demo collection, a bounded batch per statement
    /// (<see cref="DefaultDeleteBatchSize" /> unless the constructor said otherwise), each statement its
    /// own transaction.
    /// </summary>
    /// <param name="progress">Called after every batch with the number of documents deleted so far.</param>
    /// <param name="cancellationToken">Stops between batches.</param>
    /// <returns>How many documents were deleted.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why batches.</b> This used to be one <c>DeleteWhere</c> per collection, and at the Large
    /// preset the customers' one never finished: deleting 600 000 rows from <c>mt_doc_customer</c> fires
    /// three foreign-key triggers per row - <c>mt_doc_order</c>'s and <c>mt_doc_invoice</c>'s
    /// <c>NO ACTION</c> checks and <c>legacy.customer_credit</c>'s <c>ON DELETE CASCADE</c> - and took
    /// 37 to 40 seconds as one statement, against the 30-second command timeout Marten inherits from the
    /// connection string. Npgsql cancelled it, the statement rolled back, and the panel said "Failed"
    /// with every generated customer, product, vehicle and stream still there. The per-row trigger cost
    /// cannot be avoided without disabling the triggers, which needs a superuser and would be the wrong
    /// thing for a sample to show; bounding the statement is what makes the cost a progress bar instead of
    /// a timeout.
    /// </para>
    /// <para>
    /// <b>Why SQL rather than a session.</b> A batch needs a bound, and Marten's <c>DeleteWhere</c> has
    /// none - it takes a predicate, not a limit. So each batch is one
    /// <c>delete … where ctid = any(array(select ctid … limit n))</c>: a TID scan on the rows the
    /// subquery found, with the generated-run predicate checked again on the row being deleted. The table
    /// names come from Marten's own mapping and are quoted, the JSON key follows the serializer's casing,
    /// and the batch size is a parameter. Nothing Marten does on a delete is skipped: these collections
    /// have no delete listeners and no projections, <c>Order</c> was already hard-deleted on purpose (it is
    /// <c>SoftDeleted()</c>, and a truncation that only set <c>mt_deleted</c> would leave every generated
    /// row in the table), and <c>Invoice</c>'s generated rows are removed from every tenant at once, which
    /// is what the per-tenant sessions used to do one tenant at a time.
    /// </para>
    /// <para>
    /// <b>Resumable.</b> The predicate is the marker, not a remembered position, so a cancelled or failed
    /// truncation simply finds fewer rows the next time.
    /// </para>
    /// </remarks>
    public async Task<long> DeleteGeneratedDocumentsAsync(
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string key = GeneratedRunKey(store.Options.Serializer().Casing);

        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
        await using NpgsqlConnection connection = databases[0].CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        long deleted = 0;

        foreach (Type type in GeneratedCollections)
        {
            IDocumentType mapping = store.Options.FindOrResolveDocumentType(type);
            string table = DemoDataSql.QuoteQualified(mapping.TableName.Schema, mapping.TableName.Name);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int rows = await DeleteBatchAsync(connection, table, key, deleteBatchSize, cancellationToken)
                    .ConfigureAwait(false);
                if (rows == 0)
                {
                    break;
                }

                deleted += rows;
                progress?.Invoke(deleted);
            }
        }

        return deleted;
    }

    /// <summary>The JSON property name <see cref="IGeneratedDocument.GeneratedRun" /> is written under.</summary>
    /// <param name="casing">The store serializer's casing.</param>
    internal static string GeneratedRunKey(Casing casing) => casing switch
    {
        Casing.CamelCase => "generatedRun",
        Casing.SnakeCase => "generated_run",
        _ => nameof(IGeneratedDocument.GeneratedRun),
    };

    private static async Task<int> DeleteBatchAsync(
        NpgsqlConnection connection,
        string table,
        string key,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "delete from " + table + " "
            + "where ctid = any (array (select ctid from " + table + " where data ->> @key is not null limit @batch)) "
            + "and data ->> @key is not null",
            connection)
        {
            CommandTimeout = BatchCommandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = key });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = batchSize });

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // A collection no run and no seeder ever wrote to has no table, and so no generated rows.
            // Marten's DeleteWhere would have created the table to delete nothing from it.
            return 0;
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
