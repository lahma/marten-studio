using Marten;
using Marten.Storage;

using MartenStudio.SampleDomain.Generation;

using Npgsql;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// Reads how far behind the async daemon is, straight out of <c>mt_event_progression</c>.
/// </summary>
/// <remarks>
/// <para>
/// Raw SQL in the sample rather than a call into the studio, because the sample domain deliberately does
/// not reference MartenStudio (D17) and the sample host must demonstrate what a host can do on its own.
/// The studio's own projections screen answers the same question far better; this exists so the generate
/// panel can say "the daemon is 400 000 events behind and catching up" on the page that just created
/// that backlog, which is the moment somebody decides whether to go and watch it.
/// </para>
/// <para>
/// <b>Not every row is a shard.</b> The high-water row is named <c>HighWaterMark</c>, but Marten 9.35
/// keeps two more bookkeeping rows in the same table - <c>HighWaterAllocationFence</c> and
/// <c>HighWaterStuckGap</c> - and the latter deliberately trails the mark by the detection gap. Counting
/// it as a projection made this panel report a permanent ten-thousand-event lag while both real shards
/// were fully caught up (observed live, 2026-09-14). A real shard identity is
/// <c>{projection}:{shardKey}</c> and optionally <c>:{tenantId}</c> (JasperFx's <c>ShardName</c>
/// grammar), so the colon is what tells the two apart, and it excludes all three bookkeeping rows with
/// one rule. A store whose daemon has never run has no rows at all, which is
/// <see cref="DemoDataDaemonLag.Unavailable" /> rather than a lag of zero.
/// </para>
/// </remarks>
internal static class DemoDataDaemonLagReader
{
    private const string HighWaterMarkRow = "HighWaterMark";

    /// <summary>Reads the progression table, or says it could not.</summary>
    /// <param name="store">The demo store.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<DemoDataDaemonLag> ReadAsync(
        IDocumentStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
            await using NpgsqlConnection connection = databases[0].CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            string table = DemoDataSql.QuoteQualified(
                store.Options.Events.DatabaseSchemaName,
                "mt_event_progression");

            await using var command = new NpgsqlCommand(
                "select name, coalesce(last_seq_id, 0) from " + table, connection)
            {
                CommandTimeout = 5,
            };

            long highWater = 0;
            List<(string Name, long Sequence)> shards = [];

            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string name = reader.GetString(0);
                    long sequence = reader.GetInt64(1);

                    if (string.Equals(name, HighWaterMarkRow, StringComparison.Ordinal))
                    {
                        highWater = sequence;
                    }
                    else if (name.Contains(':', StringComparison.Ordinal))
                    {
                        shards.Add((name, sequence));
                    }
                }
            }

            if (shards.Count == 0 && highWater == 0)
            {
                return DemoDataDaemonLag.Unavailable;
            }

            return new DemoDataDaemonLag(
                highWater,
                shards
                    .Select(x => new DemoDataShardLag(x.Name, x.Sequence, Math.Max(0, highWater - x.Sequence)))
                    .OrderByDescending(static x => x.Lag)
                    .ToList(),
                Available: true);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // The event store has never been created in this database. Nothing to report, and creating
            // it to find that out would be a write on a read path.
            return DemoDataDaemonLag.Unavailable;
        }
        catch (NpgsqlException)
        {
            return DemoDataDaemonLag.Unavailable;
        }
    }
}
