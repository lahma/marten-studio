using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using Npgsql;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// Times every connection the studio opens through its sources, open to close, into <see cref="StudioDatabaseTime" /> - the
/// one place the studio's own cost in a database is measured (W5-Q1, backlog §C).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it sits.</b> Both of the studio's databases attach it to every connection they hand out: MartenStudio's
/// <c>MartenStudioDatabase.CreateConnection</c>, which every Marten-side read of the studio's own SQL now goes through - the
/// documents and event reads, the projections screen, the Query page, the schema reads, tenant discovery - and Postgres
/// Studio's <c>DataSourceDatabase.CreateConnection</c>. So every statement the studio issues on a connection of its own is
/// inside a metered connection's time, whoever issued it. A Marten session the studio opens for a write or to steer the
/// daemon takes its connection from Marten, not from here, and is not counted (<see cref="StudioDatabaseTime" /> says so).
/// </para>
/// <para>
/// <b>How.</b> <see cref="DbConnection.StateChange" />, which Npgsql raises on exactly two transitions - into open and out of
/// it, a broken connection included (read in Npgsql 9.0.4's <c>NpgsqlConnection.FullState</c>) - and never while a statement
/// runs; so the meter adds nothing to a statement, and a connection opened twice is counted twice. Npgsql has no per-connection
/// count of the commands it ran, and its one per-command hook is the process-wide <c>Npgsql</c> activity source: listening
/// to it would make Npgsql build an activity for every command the host runs, which registering the studio must never do. So
/// the meter counts connections and their time, and says so.
/// </para>
/// <para>
/// <b>Never in the way.</b> The handler is synchronous, takes the meter's one lock and returns; it holds the connection by
/// nothing but the subscription, so a connection the studio disposes takes its tracker with it. A read-only session on a
/// metered connection counts its session and its rollback and no time (<see cref="IsMetered" />), so a statement is never
/// timed twice.
/// </para>
/// </remarks>
internal static class StudioConnectionMeter
{
    private static readonly ConditionalWeakTable<NpgsqlConnection, Tracker> Metered = new();

    /// <summary>
    /// <paramref name="connection" />, unopened, metered into <paramref name="meter" /> (the process's own when
    /// <see langword="null" />) under its database's key from now on.
    /// </summary>
    /// <param name="connection">A connection the studio has just created and not yet opened.</param>
    /// <param name="meter">Where to record, for a test; <see cref="StudioDatabaseTime.Shared" /> otherwise.</param>
    /// <param name="clock">A <see cref="Stopwatch" />-style timestamp source, for a test; <see cref="Stopwatch.GetTimestamp" /> otherwise.</param>
    /// <param name="pool">
    /// Issue #44: the pool share it is held to (<see cref="StudioConnectionPools.Of" />), or <see langword="null" /> for none - a
    /// connection opened with <see cref="StudioConnections.OpenStudioAsync" /> then takes a slot of it, and is named.
    /// </param>
    /// <returns>The same connection.</returns>
    public static NpgsqlConnection Attach(NpgsqlConnection connection, StudioDatabaseTime? meter = null, Func<long>? clock = null, StudioConnectionPool? pool = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Metered.TryGetValue(connection, out _))
        {
            return connection;
        }

        Tracker tracker = new(StudioDatabaseTime.KeyOf(connection), meter ?? StudioDatabaseTime.Shared, clock ?? Stopwatch.GetTimestamp)
        {
            Pool = pool,
        };
        Metered.AddOrUpdate(connection, tracker);
        connection.StateChange += tracker.OnStateChange;

        if (connection.State.HasFlag(ConnectionState.Open))
        {
            tracker.Opened();
        }

        return connection;
    }

    /// <summary>Whether <paramref name="connection" /> is metered, so that a session on it counts no time of its own.</summary>
    public static bool IsMetered(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Metered.TryGetValue(connection, out _);
    }

    /// <summary>
    /// Issue #44: the pool share <paramref name="connection" /> is held to (<see cref="StudioConnectionPools" />), or
    /// <see langword="null" /> for a connection the studio did not create, or one whose pool no studio configured.
    /// </summary>
    public static StudioConnectionPool? PoolOf(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Metered.TryGetValue(connection, out Tracker? tracker) ? tracker.Pool : null;
    }

    /// <summary>
    /// Issue #44: the <c>application_name</c> the studio's transactions on <paramref name="connection" /> set
    /// (<see cref="ReadOnlySqlSession" />), or <see langword="null" /> for none.
    /// </summary>
    public static string? ApplicationNameOf(NpgsqlConnection connection) => PoolOf(connection)?.ApplicationName;

    /// <summary>
    /// Issue #44: <paramref name="connection" /> holds <paramref name="lease" /> from its open until it closes - a dispose, a
    /// close or a break, each of which Npgsql reports as the state leaving Open - and the lease is given back once.
    /// </summary>
    internal static void Hold(NpgsqlConnection connection, StudioConnectionLease lease)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(lease);

        if (Metered.TryGetValue(connection, out Tracker? tracker))
        {
            tracker.Hold(lease);
        }
        else
        {
            lease.Release();
        }
    }

    /// <summary>One connection's open time, and where it goes.</summary>
    /// <param name="database">The database's key.</param>
    /// <param name="meter">Where it is recorded.</param>
    /// <param name="clock">The timestamp source.</param>
    internal sealed class Tracker(string database, StudioDatabaseTime meter, Func<long> clock)
    {
        private readonly Lock gate = new();
        private long openedAt;
        private bool open;
        private StudioConnectionLease? lease;

        /// <summary>Issue #44: the pool share the connection is held to, or <see langword="null" />.</summary>
        public StudioConnectionPool? Pool { get; init; }

        /// <summary>Holds <paramref name="held" /> until the connection closes; one held before it is given back first.</summary>
        public void Hold(StudioConnectionLease held)
        {
            StudioConnectionLease? previous;

            lock (gate)
            {
                previous = lease;
                lease = held;
            }

            previous?.Release();
        }

        private void ReleaseLease()
        {
            StudioConnectionLease? held;

            lock (gate)
            {
                held = lease;
                lease = null;
            }

            held?.Release();
        }

        /// <summary>Npgsql's <see cref="DbConnection.StateChange" />: into open starts the clock, out of it records.</summary>
        public void OnStateChange(object? sender, StateChangeEventArgs change)
        {
            ArgumentNullException.ThrowIfNull(change);

            bool wasOpen = change.OriginalState.HasFlag(ConnectionState.Open);
            bool isOpen = change.CurrentState.HasFlag(ConnectionState.Open);

            if (!wasOpen && isOpen)
            {
                Opened();
            }
            else if (wasOpen && !isOpen)
            {
                Closed();
                ReleaseLease();
            }
        }

        /// <summary>The connection opened.</summary>
        public void Opened()
        {
            lock (gate)
            {
                openedAt = clock();
                open = true;
            }
        }

        /// <summary>The connection closed or broke: its time since it opened is recorded, once.</summary>
        public void Closed()
        {
            TimeSpan elapsed;

            lock (gate)
            {
                if (!open)
                {
                    return;
                }

                open = false;
                elapsed = Stopwatch.GetElapsedTime(openedAt, clock());
            }

            meter.RecordConnection(database, elapsed);
        }
    }
}
