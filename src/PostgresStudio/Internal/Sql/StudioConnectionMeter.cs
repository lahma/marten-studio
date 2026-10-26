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
    /// <returns>The same connection.</returns>
    public static NpgsqlConnection Attach(NpgsqlConnection connection, StudioDatabaseTime? meter = null, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Metered.TryGetValue(connection, out _))
        {
            return connection;
        }

        Tracker tracker = new(StudioDatabaseTime.KeyOf(connection), meter ?? StudioDatabaseTime.Shared, clock ?? Stopwatch.GetTimestamp);
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

    /// <summary>One connection's open time, and where it goes.</summary>
    /// <param name="database">The database's key.</param>
    /// <param name="meter">Where it is recorded.</param>
    /// <param name="clock">The timestamp source.</param>
    internal sealed class Tracker(string database, StudioDatabaseTime meter, Func<long> clock)
    {
        private readonly Lock gate = new();
        private long openedAt;
        private bool open;

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
