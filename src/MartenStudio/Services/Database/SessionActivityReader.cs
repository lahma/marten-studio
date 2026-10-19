using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;
using MartenStudio.Services.Live;

using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Database;

/// <summary>Reads one snapshot of a database's sessions, locks and prepared transactions.</summary>
/// <remarks>An interface so a component or service test can hand the page a snapshot without Postgres (D12).</remarks>
internal interface ISessionActivityReader
{
    /// <summary>Reads the snapshot. Throws what the database throws.</summary>
    /// <param name="resolved">The database, resolved by a visitor the store policy passed for it as a whole.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<SessionSnapshot> ReadAsync(ResolvedScope resolved, CancellationToken cancellationToken);
}

/// <summary>
/// The real reader: three statements from <see cref="HealthConnectionQueries" />, on one connection, each in the
/// health advisor's read-only transaction.
/// </summary>
/// <remarks>
/// It runs through the advisor's own <see cref="HealthSqlRunner" /> rather than a session of its own, so the
/// guard is the same one the checks have: <c>SET TRANSACTION READ ONLY</c>, <c>statement_timeout</c> (the
/// studio's <c>QueryTimeout</c>, never more than fifteen seconds), <c>lock_timeout</c>, <c>search_path</c> pinned
/// to <c>pg_catalog</c>, <c>SqlConsoleRole</c> when one is set - which is the role whose
/// <c>pg_read_all_stats</c> membership decides what it sees - a row cap, and every statement recorded for Show
/// SQL.
/// </remarks>
internal sealed class SessionActivityReader : ISessionActivityReader
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly TimeProvider timeProvider;

    public SessionActivityReader(IOptions<MartenStudioOptions> options, TimeProvider? timeProvider = null)
    {
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SessionSnapshot> ReadAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        MartenStudioOptions value = options.Value;
        TimeSpan timeout = value.QueryTimeout < HealthSweeper.MaxStatementTimeout ? value.QueryTimeout : HealthSweeper.MaxStatementTimeout;

        await using HealthConnection connection = new(resolved.Database);
        HealthSqlRunner sql = new(connection, new ReadOnlySqlOptions { StatementTimeout = timeout, Role = value.SqlConsoleRole }, observer: null);

        DateTimeOffset readAt = timeProvider.GetUtcNow();

        IReadOnlyList<(SessionVisibility Visibility, SessionRow? Row, long Total)> sessions = await sql
            .QueryAsync(HealthConnectionQueries.SessionSnapshot(), MapSession, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<(LockRow Row, long Total)> locks = await sql
            .QueryAsync(HealthConnectionQueries.LockSnapshot(), MapLock, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<(int MaxPrepared, PreparedRow? Row)> prepared = await sql
            .QueryAsync(HealthConnectionQueries.PreparedTransactions(), SessionReads.Prepared, cancellationToken)
            .ConfigureAwait(false);

        return new SessionSnapshot(
            readAt,
            sessions.Count > 0 ? sessions[0].Visibility : SessionVisibility.Everything,
            sessions.Count > 0 ? sessions[0].Total : 0,
            [.. sessions.Where(static x => x.Row is not null).Select(static x => x.Row!)],
            locks.Count > 0 ? locks[0].Total : 0,
            [.. locks.Select(static x => x.Row)],
            prepared.Count > 0 ? prepared[0].MaxPrepared : 0,
            [.. prepared.Where(static x => x.Row is not null).Select(static x => x.Row!)],
            sql.Statements);
    }

    /// <summary>The most bytes one character takes in any server encoding Postgres supports (UTF-8's four).</summary>
    internal const int MaxCharacterBytes = 4;

    /// <summary>One row of <see cref="HealthConnectionQueries.SessionSnapshot" />.</summary>
    internal static (SessionVisibility Visibility, SessionRow? Row, long Total) MapSession(NpgsqlDataReader reader)
    {
        SessionVisibility visibility = new(reader.GetBoolean(0), reader.GetInt64(1));

        if (reader.IsDBNull(3))
        {
            return (visibility, null, 0);
        }

        int? trackSize = SessionReads.Value<int>(reader, 2);
        int? bytes = SessionReads.Value<int>(reader, 21);
        int? characters = SessionReads.Value<int>(reader, 22);

        // Postgres keeps at most track_activity_query_size - 1 bytes, and cuts on a character boundary
        // (pg_mbcliplen), so a cut text in a multibyte encoding can stop up to three bytes short of that - a UTF-8
        // character is at most four. The read then keeps MaxQueryCharacters of what Postgres kept.
        bool truncated = (trackSize is { } size && bytes is { } length && length >= size - MaxCharacterBytes)
            || characters > HealthConnectionQueries.MaxQueryCharacters;

        SessionRow row = new(
            reader.GetInt32(3),
            SessionReads.Text(reader, 4),
            SessionReads.Text(reader, 5),
            SessionReads.Text(reader, 6),
            SessionReads.Value<DateTimeOffset>(reader, 7),
            SessionReads.Value<DateTimeOffset>(reader, 8),
            SessionReads.Value<DateTimeOffset>(reader, 9),
            SessionReads.Text(reader, 11),
            SessionReads.Text(reader, 12),
            SessionReads.Text(reader, 13),
            !reader.IsDBNull(14) && reader.GetBoolean(14),
            !reader.IsDBNull(15) && reader.GetBoolean(15),
            SessionReads.Seconds(reader, 16),
            SessionReads.Seconds(reader, 17),
            SessionReads.Seconds(reader, 18),
            SessionReads.Pids(reader, 19),
            SessionReads.Text(reader, 20),
            truncated);

        return (visibility, row, reader.GetInt64(23));
    }

    /// <summary>One row of <see cref="HealthConnectionQueries.LockSnapshot" />.</summary>
    internal static (LockRow Row, long Total) MapLock(NpgsqlDataReader reader) => (
        new LockRow(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            SessionReads.Locked(reader, 4),
            SessionReads.Value<long>(reader, 8),
            SessionReads.Seconds(reader, 9)),
        reader.GetInt64(10));
}

/// <summary>
/// One snapshot per database every few seconds, however many visitors or clicks ask - the floor that keeps
/// Refresh from becoming a poll of the process array.
/// </summary>
/// <remarks>
/// <para>
/// A single-flight <see cref="StudioSnapshotCache" /> keyed by store and database (D10). Its time to live is
/// <see cref="MinimumAge" />: the Sessions page asks once per <c>HealthInterval</c> and on Refresh, and two
/// visitors - or one visitor clicking twice - within that floor share one read.
/// </para>
/// <para>
/// <b>Authorization comes before this, not inside it</b>, as for the health sweep: a key is the database and
/// nothing else, so the caller resolves the scope - the store policy, for the database as a whole - first, and
/// what a visitor is shown of a snapshot is decided afterwards.
/// </para>
/// </remarks>
internal sealed class SessionActivitySnapshots
{
    /// <summary>The youngest a snapshot is read again.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromSeconds(10);

    private readonly ISessionActivityReader reader;
    private readonly StudioSnapshotCache cache;
    private int reads;

    public SessionActivitySnapshots(ISessionActivityReader reader, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        this.reader = reader;
        cache = new StudioSnapshotCache(timeProvider ?? TimeProvider.System, MinimumAge);
    }

    /// <summary>How many snapshots have actually been read, for a test.</summary>
    internal int ReadCount => Volatile.Read(ref reads);

    /// <summary>The database's snapshot: a recent one, or a new one shared with everybody who asks while it runs.</summary>
    public Task<SessionSnapshot> GetAsync(ResolvedScope resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string key = resolved.Registration.Key + "|" + resolved.Database.Id.Identity;

        return cache.GetAsync(key, token => ReadAsync(resolved, token), cancellationToken);
    }

    private Task<SessionSnapshot> ReadAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref reads);
        return reader.ReadAsync(resolved, cancellationToken);
    }
}

/// <summary>Column readers the session statements share, for the checks and the page alike.</summary>
internal static class SessionReads
{
    /// <summary>A nullable value column.</summary>
    public static T? Value<T>(NpgsqlDataReader reader, int ordinal)
        where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    /// <summary>A nullable text column.</summary>
    public static string? Text(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>A nullable number of seconds, as a duration never below zero.</summary>
    public static TimeSpan? Seconds(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : TimeSpan.FromSeconds(Math.Max(0d, reader.GetDouble(ordinal)));

    /// <summary>A nullable <c>int4[]</c> of process ids, as a list never <see langword="null" />.</summary>
    public static IReadOnlyList<int> Pids(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<int[]>(ordinal);

    /// <summary>
    /// A locked relation's owner, from four columns starting at <paramref name="ordinal" />: its schema, name,
    /// <c>relkind</c> and the part locked - or <see langword="null" /> when the lock is on no relation, or the read
    /// could not resolve one.
    /// </summary>
    public static LockedRelation? Locked(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || reader.IsDBNull(ordinal + 1)
            ? null
            : new LockedRelation(
                reader.GetString(ordinal),
                reader.GetString(ordinal + 1),
                Text(reader, ordinal + 2) ?? string.Empty,
                LockedRelation.PartOf(Text(reader, ordinal + 3)));

    /// <summary>The visibility columns every session statement starts with.</summary>
    public static SessionVisibility Visibility(NpgsqlDataReader reader) => new(reader.GetBoolean(0), reader.GetInt64(1));

    /// <summary>One row of <see cref="HealthConnectionQueries.PreparedTransactions" />.</summary>
    public static (int MaxPrepared, PreparedRow? Row) Prepared(NpgsqlDataReader reader) => (
        reader.GetInt32(0),
        reader.IsDBNull(1)
            ? null
            : new PreparedRow(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                TimeSpan.FromSeconds(Math.Max(0d, reader.GetDouble(4))),
                reader.GetString(5)));
}
