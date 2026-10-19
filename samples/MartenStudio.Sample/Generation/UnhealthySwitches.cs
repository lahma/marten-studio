using System.Data;
using System.Globalization;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.SampleDomain.Generation;

using Npgsql;

using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// The pinned gap: a transaction draws the next number from the event sequence Marten uses and holds it, while one
/// event is committed above it.
/// </summary>
/// <remarks>
/// <para>
/// This is an append that has drawn its sequence number and not committed yet, made to last. The async daemon's
/// high-water mark moves contiguously, so it stops under the drawn number; Marten's own gap-liveness probe sees a
/// transaction that may still fill the gap, so it does not skip it either. The marker event committed above the gap is
/// what makes it a stall rather than a curiosity: <b>a pin with nothing committed above it holds nothing back</b>, and
/// D01 rightly says nothing about it (docs/health/daemon.md).
/// </para>
/// <para>
/// The sequence's name is read from the event store's own schema objects, never typed here, so a store with another
/// event schema pins its own sequence. The marker is an event type of the sample's own,
/// <see cref="UnhealthyDemoMarker" />, registered with the store (<see cref="UnhealthyDemoStore.Configure" />) so the
/// health advisor never calls it unregistered (E05), and ignored by every projection.
/// </para>
/// <para>
/// Turned off, the transaction rolls back and the drawn number is never committed: Marten's detector sees nothing alive
/// that could fill the gap, skips it within a few seconds, and - under advanced async tracking, on in the sample -
/// records the skip, which D07 shows as an Info for a day. That is the trace every rolled-back append leaves.
/// </para>
/// </remarks>
/// <param name="store">The sample store.</param>
internal sealed class PinnedGapSwitch(IDocumentStore store) : UnhealthySwitch
{
    /// <inheritdoc />
    public override UnhealthySwitchKind Kind => UnhealthySwitchKind.PinnedGap;

    /// <inheritdoc />
    public override string Slug => "pinned-gap";

    /// <inheritdoc />
    public override string Title => "Pinned gap";

    /// <inheritdoc />
    public override string Description =>
        "A transaction draws the next number from the event sequence Marten uses and holds it open, uncommitted, while one "
        + "marker event is committed above it. The async daemon's high-water mark cannot pass the number, so every async "
        + "projection stops there.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Codes { get; } = ["D01", "C04"];

    /// <inheritdoc />
    public override string WhereToLook =>
        "D01 once the marker has waited a minute (it needs this host's daemon, so not with --no-daemon), and C04 for the "
        + "open transaction. Projections › Explain names the session that holds the mark. After it is turned off Marten "
        + "skips the abandoned number and records the skip: D07, an Info, for a day.";

    /// <inheritdoc />
    public override async Task RunAsync(Action<string> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        string sequence = UnhealthyDemoStore.EventSequence(store);
        IMartenDatabase database = store.Storage.Database;

        await using NpgsqlConnection connection = database.CreateConnection(ConnectionUsage.ReadWrite);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await NameSessionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            long drawn;
            await using (NpgsqlCommand draw = new("select pg_catalog.nextval(@sequence::regclass)", connection, transaction))
            {
                draw.Parameters.AddWithValue("sequence", sequence);
                drawn = (long) (await draw.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            // Committed above the held number, through Marten's own append path on a connection of its own: the event
            // the daemon cannot reach.
            await using (IDocumentSession session = store.LightweightSession())
            {
                session.Events.StartStream(Guid.NewGuid(), new UnhealthyDemoMarker(Slug, DateTimeOffset.UtcNow));
                await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            report("Holding event sequence number " + Number(drawn) + " of " + sequence
                + " in an open transaction, with a marker event committed above it.");

            await UntilCancelledAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CleanUpAsync(async token =>
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }

    /// <summary>A stalled store, every async projection held at the mark: refused on a configured database.</summary>
    public override bool OnlyOnTheThrowawayDatabase => true;
}

/// <summary>
/// Idle in transaction: a session begins a transaction, runs one statement and does nothing more.
/// </summary>
/// <remarks>
/// <para>
/// The commonest connection leak there is: a session that began a transaction and was never disposed. Repeatable read,
/// so the one statement takes the transaction's snapshot and the session keeps it for as long as it idles - which holds
/// back vacuum, and is what C04 calls holding something back. A read-committed session that ran one <c>select</c> would
/// hold no snapshot once it finished, and C04 deliberately does not raise a session that pins nothing.
/// </para>
/// <para>
/// No relation is read, so nothing waits on a lock the switch holds: a schema apply and the demo panel's own truncation
/// still run while it is on.
/// </para>
/// </remarks>
/// <param name="store">The sample store.</param>
internal sealed class IdleInTransactionSwitch(IDocumentStore store) : UnhealthySwitch
{
    /// <inheritdoc />
    public override UnhealthySwitchKind Kind => UnhealthySwitchKind.IdleInTransaction;

    /// <inheritdoc />
    public override string Slug => "idle-in-transaction";

    /// <inheritdoc />
    public override string Title => "Idle in transaction";

    /// <inheritdoc />
    public override string Description =>
        "A session begins a repeatable-read transaction, runs one statement and then does nothing: idle in transaction, "
        + "holding the snapshot it took, so vacuum cannot remove anything that snapshot can still see.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Codes { get; } = ["C04"];

    /// <inheritdoc />
    public override string WhereToLook =>
        "C04: a Warning once the session has been idle for a minute, Critical at ten. Database › Sessions lists it under "
        + "its application name.";

    /// <inheritdoc />
    public override async Task RunAsync(Action<string> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        await using NpgsqlConnection connection = store.Storage.Database.CreateConnection(ConnectionUsage.ReadWrite);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // The one statement: naming the session takes the transaction's snapshot, as any first statement does.
            await NameSessionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            report("Session " + connection.ProcessID.ToString(CultureInfo.InvariantCulture)
                + " is idle in a repeatable-read transaction, holding its snapshot.");

            await UntilCancelledAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CleanUpAsync(async token =>
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The bloated table: a demo table of the sample's own, autovacuum switched off for it, and every row updated once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared to Marten, owned by the sample.</b> T07 judges only the tables a store's configuration declares - a table
/// Marten knows nothing about is somebody else's, and its dead rows are not the store's problem. So the demo table is
/// one of <c>StoreOptions.Storage.ExtendedSchemaObjects</c> (<see cref="UnhealthyDemoStore.Configure" />): Marten-managed
/// relational data, in the store's own schema, holding nothing but this switch's rows - not a document table, not an
/// event table, and not named <c>mt_</c>. It lives in the document schema rather than one of its own because the
/// advisor reads a declared table only in a schema the store's documents or events use.
/// </para>
/// <para>
/// <b>Recreated every time.</b> T07 also wants the last vacuum to be more than a day old, or never - and turning the
/// switch off runs <c>VACUUM</c>. So each run drops and recreates the table from its own declaration (Weasel's, the same
/// statement Marten's apply would write), which gives it fresh statistics; then switches autovacuum off for it, fills
/// it, and updates every row once. The updated column is indexed, so no update can be HOT and every old version stays a
/// dead row until a vacuum: half the table dead, against T07's line of 20% and 10,000 rows. <c>ANALYZE</c> last, so the
/// planner's statistics are current and T08 (never analysed) stays out of this demo.
/// </para>
/// <para>
/// Turned off: <c>autovacuum_enabled</c> is reset, which is the table's own default, and the table is vacuumed.
/// </para>
/// </remarks>
/// <param name="store">The sample store.</param>
internal sealed class BloatedTableSwitch(IDocumentStore store) : UnhealthySwitch
{
    /// <summary>How many rows the table is filled with, and so how many dead rows one pass of updates leaves.</summary>
    public const int Rows = 30_000;

    /// <inheritdoc />
    public override UnhealthySwitchKind Kind => UnhealthySwitchKind.BloatedTable;

    /// <inheritdoc />
    public override string Slug => "bloated-table";

    /// <inheritdoc />
    public override string Title => "Bloated table";

    /// <inheritdoc />
    public override string Description =>
        "Recreates the sample's own demo table " + UnhealthyDemoStore.BloatTableName + ", switches autovacuum off for that "
        + "table alone, fills it with " + Number(Rows) + " rows and updates every one of them, so half the table is dead "
        + "rows that nothing will vacuum.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Codes { get; } = ["T07"];

    /// <inheritdoc />
    public override string WhereToLook =>
        "T07 at the next check: with autovacuum off for the table there is nothing to wait for. Turning it off restores "
        + "autovacuum and runs VACUUM on the table.";

    /// <inheritdoc />
    public override async Task RunAsync(Action<string> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        Table table = UnhealthyDemoStore.BloatTable(store);
        string quoted = DemoDataSql.QuoteQualified(table.Identifier.Schema, table.Identifier.Name);
        bool touched = false;

        try
        {
            // A connection for the set-up only: nothing has to be held open for the dead rows to stay dead, and a pooled
            // connection held for ten minutes would be one fewer for everything else (the pool hog's arithmetic included).
            await using (NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                await connection.EnsureSchemaExists(table.Identifier.Schema, cancellationToken).ConfigureAwait(false);

                touched = true;
                await table.DropAsync(connection, cancellationToken).ConfigureAwait(false);
                await table.CreateAsync(connection, cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(connection, "alter table " + quoted + " set (autovacuum_enabled = false)", cancellationToken).ConfigureAwait(false);

                await using (NpgsqlCommand fill = new(
                    "insert into " + quoted + " (id, revision, payload) "
                    + "select g, 0, pg_catalog.repeat('unhealthy demo row ', 6) from pg_catalog.generate_series(1, @rows) g",
                    connection))
                {
                    fill.Parameters.AddWithValue("rows", Rows);
                    await fill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await ExecuteAsync(connection, "update " + quoted + " set revision = revision + 1", cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, "analyze " + quoted, cancellationToken).ConfigureAwait(false);
            }

            report(quoted + " holds " + Number(Rows) + " live rows and " + Number(Rows)
                + " dead ones, with autovacuum switched off for it.");

            await UntilCancelledAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (touched)
            {
                await CleanUpAsync(async token =>
                {
                    await using NpgsqlConnection connection = await OpenAsync(token).ConfigureAwait(false);
                    await RestoreAsync(connection, table, quoted, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Puts the demo table back the way a store expects it: autovacuum reset and the table vacuumed - or, when it is not
    /// there at all (a run cancelled between dropping and recreating it), created again from its declaration, so the
    /// declared table is never missing until the next restart. The start-up initializer uses it too, for a host that
    /// was killed with the switch on.
    /// </summary>
    internal static async Task RestoreAsync(NpgsqlConnection connection, Table table, string quoted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);

        if (!await UnhealthyDemoStore.ExistsAsync(connection, quoted, cancellationToken).ConfigureAwait(false))
        {
            await connection.EnsureSchemaExists(table.Identifier.Schema, cancellationToken).ConfigureAwait(false);
            await table.CreateAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Reset, not "= true": the table's own default is to follow the server's autovacuum setting.
        await ExecuteAsync(connection, "alter table " + quoted + " reset (autovacuum_enabled)", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "vacuum " + quoted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A connection from the store's pool, left unnamed: it goes back to the pool idle, and a name set on the session
    /// would stay on that idle connection in <c>pg_stat_activity</c> until the pool next used it.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = store.Storage.Database.CreateConnection(ConnectionUsage.ReadWrite);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The pool hog: most of the store's own connection pool, opened and held idle.
/// </summary>
/// <remarks>
/// <para>
/// <b>The store's own pool</b>, through <c>IMartenDatabase.CreateConnection</c>: C01 reads Npgsql's counters for the
/// pool Marten uses, and a connection opened any other way would be another pool - or, under the same connection
/// string, one C01 cannot tell from Marten's and refuses to judge.
/// </para>
/// <para>
/// <b>A bounded share, and it stops short.</b> It aims for <see cref="Share" /> of <c>Maximum Pool Size</c>, at most
/// <see cref="LeaveFree" /> connections short of the ceiling, and at most the server's room less
/// <see cref="ServerMargin" /> - measured once, when it starts. Those are targets, not guarantees: the host's own work
/// borrows from the same pool, so before it keeps each connection the hog checks that <see cref="LeaveFree" /> more can
/// still be had within <see cref="ProbeWait" />, and stops growing as soon as they cannot - rather than wait out the
/// pool's <c>Timeout</c> or fail. What it does not do is watch the pool afterwards: a host that later borrows the
/// connections it left can still find the pool exhausted while the hog is on, which is what C01 is there to say. When
/// the server or the pool is the tighter limit the hog holds less, says so, and C01 may not fire while C03 does.
/// </para>
/// <para>
/// <b>Only on a database that is the demo's to break</b> (<see cref="UnhealthySwitchesDatabase" />): the throwaway
/// container, or a host started with <c>--allow-data-generation</c>.
/// </para>
/// <para>
/// Turned off, the connections go back to the pool and the pool is cleared, so they are closed rather than left idle in
/// it - an idle physical connection still counts against the server's <c>max_connections</c> for minutes.
/// </para>
/// </remarks>
/// <param name="store">The sample store.</param>
internal sealed class PoolHogSwitch(IDocumentStore store) : UnhealthySwitch
{
    /// <summary>The share of the pool the hog aims for: past C01's line of 80%.</summary>
    public const double Share = 0.9;

    /// <summary>How many connections the hog always leaves in the pool.</summary>
    public const int LeaveFree = 2;

    /// <summary>How many connections the hog always leaves on the server.</summary>
    public const int ServerMargin = 5;

    /// <summary>
    /// How long the hog waits for a connection before it takes the pool to have none free: far shorter than the pool's own
    /// <c>Timeout</c>, so a pool the host is using stops the hog growing instead of stalling it.
    /// </summary>
    public static readonly TimeSpan ProbeWait = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc />
    public override UnhealthySwitchKind Kind => UnhealthySwitchKind.PoolHog;

    /// <inheritdoc />
    public override string Slug => "pool-hog";

    /// <inheritdoc />
    public override string Title => "Pool hog";

    /// <inheritdoc />
    public override string Description =>
        "Opens up to " + (Share * 100).ToString("0", CultureInfo.InvariantCulture) + "% of the store's own connection pool "
        + "and holds the connections open and idle, as a host that never disposes its sessions does. It stops growing while "
        + LeaveFree.ToString(CultureInfo.InvariantCulture) + " connections can still be had, so the studio and the host keep a "
        + "few - unless they then take those as well.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Codes { get; } = ["C01"];

    /// <inheritdoc />
    public override string WhereToLook =>
        "C01 at the next check. On a server whose max_connections is close to the pool's size, C03 too - and the hog holds "
        + "fewer, and says so, rather than use the server up.";

    /// <summary>A server's connections, most of them: refused on a configured database.</summary>
    public override bool OnlyOnTheThrowawayDatabase => true;

    /// <summary>
    /// How many connections to aim for: <see cref="Share" /> of <paramref name="maxPoolSize" />, at most
    /// <see cref="LeaveFree" /> short of it, and at most one more than the server's room less <see cref="ServerMargin" />
    /// (the one is the connection that asked). Never fewer than one.
    /// </summary>
    /// <param name="maxPoolSize">The pool's ceiling.</param>
    /// <param name="serverRoom">How many more client connections the server accepts, the asking one not counted.</param>
    internal static int Target(int maxPoolSize, int serverRoom)
    {
        int wanted = Math.Min((int) Math.Ceiling(maxPoolSize * Share), maxPoolSize - LeaveFree);
        int allowed = 1 + Math.Max(0, serverRoom - ServerMargin);

        return Math.Max(1, Math.Min(wanted, allowed));
    }

    /// <inheritdoc />
    public override async Task RunAsync(Action<string> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        IMartenDatabase database = store.Storage.Database;
        List<NpgsqlConnection> held = [];

        try
        {
            NpgsqlConnection first = await TryOpenAsync(database, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The pool had no connection free within " + Describe(ProbeWait) + "; the hog took none.");
            held.Add(first);

            if (!await CanStillHaveAsync(database, LeaveFree, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The pool had fewer than " + Number(LeaveFree + 1) + " connections free; the hog took none, so the host keeps them.");
            }

            await NameSessionAsync(first, null, cancellationToken).ConfigureAwait(false);

            int maxPoolSize = new NpgsqlConnectionStringBuilder(first.ConnectionString).MaxPoolSize;
            int serverRoom = await ServerRoomAsync(first, cancellationToken).ConfigureAwait(false);
            int target = Target(maxPoolSize, serverRoom);
            bool stoppedShort = false;

            while (held.Count < target)
            {
                NpgsqlConnection? next = await TryOpenAsync(database, cancellationToken).ConfigureAwait(false);

                if (next is null)
                {
                    stoppedShort = true;
                    break;
                }

                held.Add(next);

                // Before keeping it: can LeaveFree more still be had? If not, the host needs what is left.
                if (!await CanStillHaveAsync(database, LeaveFree, cancellationToken).ConfigureAwait(false))
                {
                    held.Remove(next);
                    await next.DisposeAsync().ConfigureAwait(false);
                    stoppedShort = true;
                    break;
                }

                await NameSessionAsync(next, null, cancellationToken).ConfigureAwait(false);
            }

            double share = (double) held.Count / maxPoolSize;
            report("Holding " + Number(held.Count) + " of the pool's " + Number(maxPoolSize) + " connections ("
                + (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%) open and idle"
                + (stoppedShort
                    ? "; it stopped growing there because no more than " + Number(LeaveFree) + " others could still be had - the host is using the rest"
                    : target < Target(maxPoolSize, int.MaxValue / 2)
                        ? "; the server has room for only " + Number(serverRoom) + " more connections, so it holds fewer"
                        : string.Empty)
                + (share > 0.8 ? "." : ". That is not past C01's 80% line; look for C03, or for C01 once the host takes more."));

            await UntilCancelledAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CleanUpAsync(async _ =>
            {
                foreach (NpgsqlConnection connection in held)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                // Every one of them is idle in the pool now; clearing it closes them. Any of the host's own that are busy
                // are closed when they come back, not interrupted.
                if (held.Count > 0 && database is PostgresqlDatabase { DataSource: { } source })
                {
                    source.Clear();
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A connection from the store's pool within <see cref="ProbeWait" />, or <see langword="null" /> when the pool has none
    /// free that soon.
    /// </summary>
    private static async Task<NpgsqlConnection?> TryOpenAsync(IMartenDatabase database, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = database.CreateConnection(ConnectionUsage.ReadWrite);
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(ProbeWait);

        try
        {
            await connection.OpenAsync(wait.Token).ConfigureAwait(false);
            return connection;
        }
        // Whatever Npgsql raised when the wait was given up on; the switch's own cancellation is rethrown below.
#pragma warning disable CA1031 // Deliberate: a pool wait that timed out is an answer, not a failure.
        catch (Exception) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Whether <paramref name="count" /> more connections can still be had from the pool, each within <see cref="ProbeWait" />.</summary>
    private static async Task<bool> CanStillHaveAsync(IMartenDatabase database, int count, CancellationToken cancellationToken)
    {
        List<NpgsqlConnection> probes = [];

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (await TryOpenAsync(database, cancellationToken).ConfigureAwait(false) is not { } probe)
                {
                    return false;
                }

                probes.Add(probe);
            }

            return true;
        }
        finally
        {
            foreach (NpgsqlConnection probe in probes)
            {
                await probe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Describe(TimeSpan span) => span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static async Task<int> ServerRoomAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "select pg_catalog.current_setting('max_connections')::int"
            + " - pg_catalog.current_setting('superuser_reserved_connections')::int"
            + " - coalesce(nullif(pg_catalog.current_setting('reserved_connections', true), '')::int, 0)"
            + " - (select count(*) from pg_catalog.pg_stat_activity where backend_type = 'client backend')::int",
            connection);

        return (int) (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}

/// <summary>The event the pinned-gap switch commits above the number it holds.</summary>
/// <param name="Switch">The switch that wrote it.</param>
/// <param name="At">When.</param>
public sealed record UnhealthyDemoMarker(string Switch, DateTimeOffset At);

/// <summary>
/// What the switches need the sample store to declare, and the names they read back from it.
/// </summary>
internal static class UnhealthyDemoStore
{
    /// <summary>The bloated-table switch's demo table, in the store's document schema.</summary>
    public const string BloatTableName = "demo_bloat";

    /// <summary>
    /// Declares the demo table and registers the marker event type. Called from <c>ConfigureMarten</c>, after
    /// <c>SampleStore.Configure</c>, so the document schema is the store's.
    /// </summary>
    /// <param name="options">The store's options.</param>
    public static void Configure(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Table table = new(options.DatabaseSchemaName + "." + BloatTableName);
        table.AddColumn("id", "bigint").AsPrimaryKey();
        table.AddColumn("revision", "integer").NotNull();
        table.AddColumn("payload", "text");

        // The column every update changes is indexed, so no update is HOT and every old row version stays dead.
        table.Indexes.Add(new IndexDefinition(BloatTableName + "_revision_idx") { Columns = ["revision"] });

        options.Storage.ExtendedSchemaObjects.Add(table);
        options.Events.AddEventType<UnhealthyDemoMarker>();
    }

    /// <summary>The demo table as the store declares it.</summary>
    /// <param name="store">The sample store.</param>
    public static Table BloatTable(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        return store.Options is StoreOptions options
            && options.Storage.ExtendedSchemaObjects.OfType<Table>().FirstOrDefault(static x => x.Identifier.Name == BloatTableName) is { } table
            ? table
            : throw new InvalidOperationException(
                "The store does not declare the demo table " + BloatTableName + ": register the switches with AddUnhealthySwitches().");
    }

    /// <summary>The demo table, quoted, as the switches' SQL names it.</summary>
    /// <param name="table">The declared table.</param>
    public static string Quoted(Table table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return DemoDataSql.QuoteQualified(table.Identifier.Schema, table.Identifier.Name);
    }

    /// <summary>Whether a relation of that (quoted) name exists.</summary>
    public static async Task<bool> ExistsAsync(NpgsqlConnection connection, string quoted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new("select pg_catalog.to_regclass(@table) is not null", connection);
        command.Parameters.AddWithValue("table", quoted);
        return (bool) (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Whether the table's own storage parameters switch autovacuum off - a run of the switch that never ended.</summary>
    public static async Task<bool> AutovacuumOffAsync(NpgsqlConnection connection, string quoted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(
            "select coalesce('autovacuum_enabled=false' = any(c.reloptions), false) "
            + "from pg_catalog.pg_class c where c.oid = pg_catalog.to_regclass(@table)",
            connection);
        command.Parameters.AddWithValue("table", quoted);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>
    /// The event sequence Marten draws from, quoted for a <c>regclass</c> parameter - read from the event store's own
    /// schema objects, which touches no connection.
    /// </summary>
    /// <param name="store">The sample store.</param>
    public static string EventSequence(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Options.Events.UseTenantPartitionedEvents)
        {
            throw new InvalidOperationException("This store keeps an event sequence per tenant, which the pinned-gap switch does not pin.");
        }

        Sequence sequence = store.Options.Events is IFeatureSchema events
            && events.Objects.OfType<Sequence>().FirstOrDefault() is { } found
            ? found
            : throw new InvalidOperationException("The event store declares no sequence.");

        return DemoDataSql.QuoteQualified(sequence.Identifier.Schema, sequence.Identifier.Name);
    }
}

/// <summary>
/// Puts the demo table right when the host starts: creates it when it is missing, so the Schema screen never shows it
/// missing, and switches autovacuum back on when a previous run of the bloated-table switch never ended.
/// </summary>
/// <remarks>
/// <para>
/// Through Weasel, from the store's own declaration. An existing, healthy table is left alone, its rows included; the
/// bloated-table switch recreates it each time it is turned on.
/// </para>
/// <para>
/// <b>A host that was killed</b> - Ctrl+C twice, a crashed process, a closed terminal - runs no clean-up, and on a
/// persistent database (a reused throwaway container, or a configured one) the table stays with autovacuum off and its
/// dead rows in it. So start-up resets <c>autovacuum_enabled</c> and vacuums it, exactly as turning the switch off would
/// have: all four switches are off when the host starts, and nothing of the last run is left on.
/// </para>
/// </remarks>
internal sealed class UnhealthyDemoTableInitializer : IInitialData
{
    /// <inheritdoc />
    public async Task Populate(IDocumentStore store, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(store);

        Table table = UnhealthyDemoStore.BloatTable(store);
        string quoted = UnhealthyDemoStore.Quoted(table);

        await using NpgsqlConnection connection = store.Storage.Database.CreateConnection(ConnectionUsage.ReadWrite);
        await connection.OpenAsync(cancellation).ConfigureAwait(false);

        if (!await UnhealthyDemoStore.ExistsAsync(connection, quoted, cancellation).ConfigureAwait(false)
            || await UnhealthyDemoStore.AutovacuumOffAsync(connection, quoted, cancellation).ConfigureAwait(false))
        {
            await BloatedTableSwitch.RestoreAsync(connection, table, quoted, cancellation).ConfigureAwait(false);
        }
    }
}

/// <summary>Registers the switches.</summary>
internal static class UnhealthySwitchesRegistration
{
    /// <summary>
    /// Registers the four switches and the board that runs them, declares what they need on the store, and creates the
    /// demo table at start-up.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="database">Which database the switches act on, and so which of them may run there.</param>
    /// <remarks>
    /// Whether or not the endpoints are mapped, like the demo-data job: the board is also what turns every switch off
    /// when the host stops, and a store whose declarations depended on a command-line switch would migrate differently
    /// from one start to the next.
    /// </remarks>
    public static IServiceCollection AddUnhealthySwitches(this IServiceCollection services, UnhealthySwitchesDatabase database)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(database);

        services.AddSingleton(database);
        services.AddSingleton<UnhealthySwitch, PinnedGapSwitch>();
        services.AddSingleton<UnhealthySwitch, IdleInTransactionSwitch>();
        services.AddSingleton<UnhealthySwitch, BloatedTableSwitch>();
        services.AddSingleton<UnhealthySwitch, PoolHogSwitch>();
        services.AddSingleton<UnhealthySwitchBoard>();
        services.AddHostedService(static provider => provider.GetRequiredService<UnhealthySwitchBoard>());

        services.ConfigureMarten(UnhealthyDemoStore.Configure);
        services.InitializeMartenWith<UnhealthyDemoTableInitializer>();

        return services;
    }
}
