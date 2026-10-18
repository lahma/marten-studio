using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using Npgsql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One session idle inside a transaction, as C04 read it.</summary>
/// <param name="ProcessId">Its process id. A detail.</param>
/// <param name="User">Its role. A detail.</param>
/// <param name="Application">Its application name. A detail.</param>
/// <param name="ClientAddress">Its client address. A detail.</param>
/// <param name="BackendStart">When its backend started. A detail.</param>
/// <param name="State"><c>idle in transaction</c>, or <c>… (aborted)</c>.</param>
/// <param name="IdleAtRead">How long it had been idle when read.</param>
/// <param name="TransactionAtRead">How long its transaction had been open when read.</param>
/// <param name="HasTransactionId">Whether it has written in the transaction.</param>
/// <param name="HasSnapshot">Whether it holds a snapshot.</param>
/// <param name="RelationLocks">How many relation locks it holds.</param>
/// <param name="HoldsDaemonLock">Whether one of its advisory locks is Marten's daemon's.</param>
internal sealed record IdleSession(
    int ProcessId,
    string? User,
    string? Application,
    string? ClientAddress,
    DateTimeOffset? BackendStart,
    string State,
    TimeSpan IdleAtRead,
    TimeSpan? TransactionAtRead,
    bool HasTransactionId,
    bool HasSnapshot,
    long RelationLocks,
    bool HoldsDaemonLock)
{
    /// <summary>
    /// Whether it holds anything back: a transaction id (it wrote, so the event sequence and vacuum wait for
    /// it), a snapshot (vacuum waits for it) or a relation lock (DDL waits for it). A session that holds none of
    /// these - nothing but advisory locks, as a lock listener does - pins nothing, and is not a finding.
    /// </summary>
    public bool PinsSomething => HasTransactionId || HasSnapshot || RelationLocks > 0;
}

/// <summary>What C04 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Visibility">How much of <c>pg_stat_activity</c> the role could see.</param>
/// <param name="Total">How many sessions are idle in a transaction for at least the floor, beyond the ones read.</param>
/// <param name="Sessions">The oldest of them, at most <see cref="HealthConnectionQueries.MaxOffenders" />.</param>
internal sealed record IdleInTransactionFacts(
    DateTimeOffset ReadAt,
    SessionVisibility Visibility,
    long Total,
    IReadOnlyList<IdleSession> Sessions);

/// <summary>C04: sessions idle inside an open transaction for a minute or more.</summary>
/// <remarks>
/// <para>
/// An open transaction that is not doing anything keeps every lock it has taken, holds back vacuum while it has a
/// snapshot, and - once it has written - holds a transaction id that Marten's high-water detection may wait
/// behind (D01). It is nearly always an application that began a transaction and never ended it.
/// </para>
/// <para>
/// <b>Marten's own daemon, as Marten configures it by default, is not one.</b> At the floor (Marten 9.39 and 9.40,
/// where <c>Events.UseAdvisoryLockTransaction</c> is on by default) the HotCold leader holds its leadership with
/// <c>pg_try_advisory_xact_lock</c> in a transaction it never ends - with a snapshot, measured live. What that session
/// then does, measured live at 9.39 with <c>Events.UseMonitoredAdvisoryLock</c> on (the default): it is
/// <c>idle in transaction</c> from taking the lock until about 0.9 s after the first leadership poll that follows - five
/// seconds or so at the default <c>LeadershipPollingTime</c> of 5,000 ms - and from then on <c>active</c>, running the
/// lock monitor's <c>pg_sleep(60)</c> inside that same transaction, the next sleep beginning as the last one ends. So
/// it is idle in its transaction for a few seconds once, when it takes the lead, and never for the minute this check
/// waits for - unless <c>LeadershipPollingTime</c> is set near a minute or more, when that first stretch reaches the
/// minute and the leader is raised, rightly, for as long as its snapshot sits idle. With monitoring off it is idle in
/// that transaction for as long as it leads, and this check raises it after a minute, because its snapshot does hold
/// back vacuum. From 9.41 the lock is the session's and the leader is not in a transaction at all. Marten's own
/// <c>GapLivenessProbe</c> rules the leader out too (#5125). A session that has no transaction id, no snapshot and no
/// relation lock pins nothing, whoever it is - Wolverine's lock listeners - and is counted in the status row rather
/// than raised; Marten's daemon lock ids are read too, so the row can say when it is the daemon's.
/// </para>
/// <para>
/// <b>Nothing here exempts the leader by name.</b> It is quiet because of what the session does - <c>active</c> in its
/// monitor's sleep, not idle - so C04 needs no rule of its own for it, and a leader that really does sit idle in its
/// transaction (monitoring off, or a polling interval of a minute) is raised like any other session holding a
/// snapshot.
/// </para>
/// <para>
/// Who the sessions are - process id, role, application, client, when they connected - is evidence behind the
/// activity gate; the count and the ages are the database's, and are shown to anybody the store policy passes
/// for it.
/// </para>
/// </remarks>
[HealthCheck("C04", HealthArea.Connections, HealthCostTier.Catalog, "Idle in transaction")]
internal sealed class IdleInTransactionCheck : HealthCheck<IdleInTransactionFacts>
{
    /// <inheritdoc />
    public override Task<IdleInTransactionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        ReadAsync(context, ConnectionThresholds.CandidateFloor, cancellationToken);

    /// <summary>
    /// The gatherer's read, of sessions idle in a transaction for at least <paramref name="floor" />: the check reads
    /// from <see cref="ConnectionThresholds.CandidateFloor" />, and a live test from zero, to read a session that is
    /// idle only for a few seconds, as the HotCold leader is at 9.39 and 9.40 before its lock monitor starts.
    /// </summary>
    internal static async Task<IdleInTransactionFacts> ReadAsync(HealthCheckContext context, TimeSpan floor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only the daemon's advisory keys are wanted here, so no relation classifier is built.
        SessionLockLabels labels = SessionLockLabels.For(context.Store, context.StoreKey, classifier: null);

        IReadOnlyList<(SessionVisibility Visibility, IdleSession? Session, long Total)> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.IdleInTransaction(floor, labels.DaemonLockIds), Map, cancellationToken)
            .ConfigureAwait(false);

        return new IdleInTransactionFacts(
            context.Now,
            rows.Count > 0 ? rows[0].Visibility : SessionVisibility.Everything,
            rows.Count > 0 ? rows[0].Total : 0,
            [.. rows.Where(static x => x.Session is not null).Select(static x => x.Session!)]);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(IdleInTransactionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<(IdleSession Session, TimeSpan Idle)> idle =
        [
            .. facts.Sessions
                .Select(x => (Session: x, Idle: SnapshotAge.At(x.IdleAtRead, facts.ReadAt, evaluation)))
                .Where(static x => x.Idle >= ConnectionThresholds.IdleInTransactionWarning)
                .OrderByDescending(static x => x.Idle),
        ];

        List<(IdleSession Session, TimeSpan Idle)> pinning = [.. idle.Where(static x => x.Session.PinsSomething)];
        int harmless = idle.Count - pinning.Count;

        string harmlessNote = harmless == 0
            ? string.Empty
            : " " + SessionText.Count(harmless, "session") + " idle in a transaction for a minute or more "
              + (harmless == 1 ? "holds" : "hold") + " no transaction id, no snapshot and no relation lock - only advisory locks"
              + (idle.Any(static x => !x.Session.PinsSomething && x.Session.HoldsDaemonLock) ? ", among them Marten's own daemon leadership" : string.Empty)
              + " - and " + (harmless == 1 ? "pins" : "pin") + " nothing, so " + (harmless == 1 ? "it is" : "they are") + " not raised.";

        if (pinning.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No session has been idle inside a transaction holding anything back for "
                + SessionText.Duration(ConnectionThresholds.IdleInTransactionWarning) + " or more." + harmlessNote);
        }

        TimeSpan longest = pinning[0].Idle;
        bool critical = longest >= ConnectionThresholds.IdleInTransactionCritical;

        List<HealthEvidence> evidence =
        [
            new("Sessions idle in a transaction", SessionText.Number(pinning.Count)
                + (facts.Total > facts.Sessions.Count ? " of the oldest " + SessionText.Number(facts.Sessions.Count) + " read" : string.Empty)),
            new("Longest idle", SessionText.Duration(longest)),
            new("Have written (hold a transaction id)", SessionText.Number(pinning.Count(static x => x.Session.HasTransactionId))),
            new("Hold a snapshot (hold back vacuum)", SessionText.Number(pinning.Count(static x => x.Session.HasSnapshot))),
            new("Hold relation locks", SessionText.Number(pinning.Count(static x => x.Session.RelationLocks > 0))),
        ];

        for (int i = 0; i < Math.Min(pinning.Count, ConnectionThresholds.SessionsNamed); i++)
        {
            (IdleSession session, TimeSpan idleFor) = pinning[i];

            string what = session.State + " for " + SessionText.Duration(idleFor)
                + (session.TransactionAtRead is { } open
                    ? ", transaction open " + SessionText.Duration(SnapshotAge.At(open, facts.ReadAt, evaluation))
                    : string.Empty)
                + (session.HasTransactionId ? ", has written" : string.Empty)
                + (session.RelationLocks > 0 ? ", holds " + SessionText.Count(session.RelationLocks, "relation lock") : string.Empty);

            evidence.Add(SessionEvidence.Session(
                SessionEvidence.Label("Session", i),
                session.ProcessId,
                session.User,
                session.Application,
                session.ClientAddress,
                session.BackendStart,
                what));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C04",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = pinning.Count == 1
                ? "A session has been idle inside an open transaction for " + SessionText.Duration(longest)
                : SessionText.Number(pinning.Count) + " sessions have been idle inside open transactions, the longest for " + SessionText.Duration(longest),
            WhyItMatters =
                "A transaction left open between statements keeps every lock it took - a schema apply, or any ALTER on those "
                + "tables, waits behind it, and everything after the ALTER waits too - holds back vacuum for the whole database while "
                + "it has a snapshot, and once it has written it holds a transaction id that Marten's high-water detection can wait "
                + "behind, so async projections stop moving (D01). It is nearly always application code that began a transaction "
                + "and never finished it: a session that was never disposed, an exception path that skipped SaveChangesAsync, a "
                + "debugger paused at a breakpoint." + harmlessNote,
            Evidence = evidence,
            Caveat = SessionEvidence.Caveat(facts.Visibility, facts.ReadAt, SessionEvidence.StatementsOnSessionsPage),
            Link = SessionEvidence.SessionsLink,
            Remedy = new HealthRemedy(
                """
                // Dispose every session on every path, and end what it began:
                await using var session = store.LightweightSession();
                // ... work ...
                await session.SaveChangesAsync(cancellationToken);
                """,
                "The studio never ends another session. Find the code path from the application name and the time it went idle; "
                + "a server-wide idle_in_transaction_session_timeout is the DBA's backstop. With Marten 9.39 and 9.40 it can also "
                + "end the HotCold daemon's leadership session, which holds its lock in a transaction: one shorter than the few "
                + "seconds the leader sits idle after taking the lead, or any timeout with Events.UseMonitoredAdvisoryLock off."),
        });
    }

    private static (SessionVisibility Visibility, IdleSession? Session, long Total) Map(NpgsqlDataReader reader)
    {
        SessionVisibility visibility = SessionReads.Visibility(reader);

        if (reader.IsDBNull(2))
        {
            return (visibility, null, 0);
        }

        return (
            visibility,
            new IdleSession(
                reader.GetInt32(2),
                SessionReads.Text(reader, 3),
                SessionReads.Text(reader, 4),
                SessionReads.Text(reader, 5),
                SessionReads.Value<DateTimeOffset>(reader, 6),
                SessionReads.Text(reader, 8) ?? "idle in transaction",
                SessionReads.Seconds(reader, 9) ?? TimeSpan.Zero,
                SessionReads.Seconds(reader, 10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetInt64(13),
                reader.GetBoolean(14)),
            reader.GetInt64(15));
    }
}
