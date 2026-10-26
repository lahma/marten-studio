using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>One session waiting for a lock, as C06 read it.</summary>
/// <param name="ProcessId">Its process id. A detail.</param>
/// <param name="User">Its role. A detail.</param>
/// <param name="Application">Its application name. A detail.</param>
/// <param name="ClientAddress">Its client address. A detail.</param>
/// <param name="Mode">The mode it asks for.</param>
/// <param name="Target">What it waits for, labelled in Marten's terms when it is Marten's.</param>
/// <param name="ExactStart">Whether the wait is measured from <c>pg_locks.waitstart</c> (PostgreSQL 14+).</param>
/// <param name="WaitedAtRead">How long it had waited when read.</param>
/// <param name="BlockedBy">Who blocks it. A detail.</param>
internal sealed record LockWaiter(
    int ProcessId,
    string? User,
    string? Application,
    string? ClientAddress,
    string Mode,
    LockTarget Target,
    bool ExactStart,
    TimeSpan WaitedAtRead,
    IReadOnlyList<int> BlockedBy);

/// <summary>One session that blocks a waiter, as C06 read it.</summary>
/// <param name="ProcessId">Its process id. A detail.</param>
/// <param name="SameDatabase">Whether it is in this database.</param>
/// <param name="State">Its state, or <see langword="null" /> when the role may not read it.</param>
/// <param name="User">Its role. A detail.</param>
/// <param name="Application">Its application name. A detail.</param>
/// <param name="ClientAddress">Its client address. A detail.</param>
/// <param name="TransactionAtRead">How long its transaction had been open when read.</param>
/// <param name="HasTransactionId">Whether it has written.</param>
/// <param name="Waiting">Whether it waits for a lock itself - a link in a chain rather than its head.</param>
internal sealed record LockBlocker(
    int ProcessId,
    bool SameDatabase,
    string? State,
    string? User,
    string? Application,
    string? ClientAddress,
    TimeSpan? TransactionAtRead,
    bool HasTransactionId,
    bool Waiting)
{
    /// <summary>Whether it is running a <c>VACUUM</c> (it has a row in <c>pg_stat_progress_vacuum</c>, which every role may read).</summary>
    public bool Vacuum { get; init; }

    /// <summary>Whether <c>pg_stat_activity</c> shows it as an autovacuum worker - known only to a role with <c>pg_read_all_stats</c>.</summary>
    public bool Autovacuum { get; init; }

    /// <summary>Whether it is autovacuum running to prevent wraparound - known only to a role with <c>pg_read_all_stats</c>.</summary>
    public bool PreventsWraparound { get; init; }

    /// <summary>What the head of a chain is, when it is a <c>VACUUM</c>: "an autovacuum to prevent wraparound", "an autovacuum", "a VACUUM".</summary>
    public string? VacuumLabel => PreventsWraparound
        ? "an autovacuum to prevent wraparound"
        : Autovacuum ? "an autovacuum" : Vacuum ? "a VACUUM" : null;
}

/// <summary>What C06 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Visibility">How much of <c>pg_stat_activity</c> the role could see.</param>
/// <param name="Total">How many sessions wait, beyond the ones read.</param>
/// <param name="Waiters">The longest waits, at most <see cref="HealthConnectionQueries.MaxOffenders" />.</param>
/// <param name="Blockers">The sessions blocking them.</param>
internal sealed record LockWaitFacts(
    DateTimeOffset ReadAt,
    SessionVisibility Visibility,
    long Total,
    IReadOnlyList<LockWaiter> Waiters,
    IReadOnlyList<LockBlocker> Blockers);

/// <summary>C06: sessions that have waited thirty seconds or more for a lock, and what holds them up.</summary>
/// <remarks>
/// <para>
/// What is waited for is labelled the way the store would name it (<see cref="SessionLockLabels" />): Marten's
/// apply-changes lock (<c>ApplyChangesLockId</c>, read from the store's options), the async daemon's lock or one
/// shard's, a row of <c>mt_streams</c> - two appends to one stream - a document table by its alias. An index, a
/// partition or a TOAST table is labelled by the table it belongs to, never by its own name. A relation Marten
/// does not own is "a relation Marten does not own" here, and named only on Database › Sessions, behind the
/// activity gate and masked; another store's relation, or an <c>mt_</c> name no store declares, is named nowhere;
/// a hidden document type's table is never named at all, nor its part said.
/// </para>
/// <para>
/// The head of a chain is a blocker that does not wait itself; the finding says what state it is in - very often
/// <c>idle in transaction</c>, which is C04's - and how long its transaction has been open. Who the sessions are
/// is evidence behind the activity gate.
/// </para>
/// </remarks>
[HealthCheck("C06", HealthArea.Connections, HealthCostTier.Catalog, "Lock waits and blocking chains")]
internal sealed class LockWaitCheck : HealthCheck<LockWaitFacts>
{
    /// <inheritdoc />
    public override async Task<LockWaitFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        SessionLockLabels labels = context.Services.GetRequiredService<ISessionLockLabeler>().For(context.Source, Classifier(context));

        IReadOnlyList<(SessionVisibility Visibility, LockWaiter? Waiter, long Total)> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.LockWaits(ConnectionThresholds.CandidateFloor), reader => MapWaiter(reader, labels), cancellationToken)
            .ConfigureAwait(false);

        List<LockWaiter> waiters = [.. rows.Where(static x => x.Waiter is not null).Select(static x => x.Waiter!)];

        HashSet<int> blocking = [.. waiters.SelectMany(static x => x.BlockedBy)];

        IReadOnlyList<LockBlocker> blockers = blocking.Count == 0
            ? []
            : await context.Sql
                .QueryAsync(HealthConnectionQueries.Blockers([.. blocking.Order()]), MapBlocker, cancellationToken)
                .ConfigureAwait(false);

        return new LockWaitFacts(
            context.Now,
            rows.Count > 0 ? rows[0].Visibility : SessionVisibility.Everything,
            rows.Count > 0 ? rows[0].Total : 0,
            waiters,
            blockers);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(LockWaitFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<(LockWaiter Waiter, TimeSpan Waited)> waits =
        [
            .. facts.Waiters
                .Select(x => (Waiter: x, Waited: SnapshotAge.At(x.WaitedAtRead, facts.ReadAt, evaluation)))
                .Where(static x => x.Waited >= ConnectionThresholds.LockWaitWarning)
                .OrderByDescending(static x => x.Waited),
        ];

        if (waits.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No session in this database has waited for a lock for " + SessionText.Duration(ConnectionThresholds.LockWaitWarning) + " or more.");
        }

        TimeSpan longest = waits[0].Waited;
        bool critical = longest >= ConnectionThresholds.LockWaitCritical;
        bool exact = waits.All(static x => x.Waiter.ExactStart);

        HashSet<int> blocking = [.. waits.SelectMany(static x => x.Waiter.BlockedBy)];

        List<(LockBlocker Blocker, TimeSpan? Open)> heads =
        [
            .. facts.Blockers
                .Where(x => blocking.Contains(x.ProcessId) && !x.Waiting)
                .Select(x => (Blocker: x, Open: x.TransactionAtRead is { } open ? SnapshotAge.At(open, facts.ReadAt, evaluation) : (TimeSpan?) null))
                .OrderByDescending(static x => x.Open ?? TimeSpan.Zero),
        ];

        string waitingFor = string.Join("; ", waits
            .GroupBy(static x => (x.Waiter.Target.Label, x.Waiter.Mode))
            .OrderByDescending(static x => x.Count())
            .Select(static x => x.Key.Label + " in " + x.Key.Mode + (x.Count() > 1 ? " (" + SessionText.Number(x.Count()) + " sessions)" : string.Empty)));

        List<HealthEvidence> evidence =
        [
            new("Waiting sessions", SessionText.Number(waits.Count)),
            new("Longest wait", SessionText.Duration(longest) + (exact ? string.Empty : " (at most: measured from the session's last state change)")),
            new("Waiting for", waitingFor),
            new("Held up by", heads.Count == 0
                ? "sessions this role cannot see into, or that finished as the check read them"
                : string.Join("; ", heads
                    .GroupBy(static x => x.Blocker.VacuumLabel
                        ?? (x.Blocker.SameDatabase ? x.Blocker.State ?? "a session whose state is not readable" : "a session in another database"))
                    .Select(x => SessionText.Count(x.Count(), "session") + " " + x.Key
                        + (x.Max(static y => y.Open) is { } open ? ", the oldest transaction open " + SessionText.Duration(open) : string.Empty)))),
        ];

        for (int i = 0; i < Math.Min(waits.Count, ConnectionThresholds.SessionsNamed); i++)
        {
            (LockWaiter waiter, TimeSpan waited) = waits[i];

            evidence.Add(SessionEvidence.Session(
                SessionEvidence.Label("Waiter", i),
                waiter.ProcessId,
                waiter.User,
                waiter.Application,
                waiter.ClientAddress,
                null,
                "waits for " + waiter.Target.Label + " in " + waiter.Mode + " for " + SessionText.Duration(waited)
                + (waiter.BlockedBy.Count > 0
                    ? ", blocked by pid " + string.Join(", ", waiter.BlockedBy.Select(static x => x.ToString(CultureInfo.InvariantCulture)))
                    : string.Empty)));
        }

        for (int i = 0; i < Math.Min(heads.Count, ConnectionThresholds.SessionsNamed); i++)
        {
            (LockBlocker blocker, TimeSpan? open) = heads[i];

            evidence.Add(SessionEvidence.Session(
                SessionEvidence.Label("Blocker", i, "oldest transaction"),
                blocker.ProcessId,
                blocker.User,
                blocker.Application,
                blocker.ClientAddress,
                null,
                (blocker.VacuumLabel is { } vacuum ? vacuum + ", " : string.Empty)
                + (blocker.SameDatabase ? blocker.State ?? "state not readable" : "in another database")
                + (open is { } age ? ", transaction open " + SessionText.Duration(age) : string.Empty)
                + (blocker.HasTransactionId ? ", has written" : string.Empty)));
        }

        bool applyLock = waits.Any(static x => x.Waiter.Target.Kind == LockTargetKind.ApplyChangesLock);
        bool streamRows = waits.Any(static x => x.Waiter.Target is { Kind: LockTargetKind.EventTable, Row: true });
        LockBlocker? vacuumHead = heads.Select(static x => x.Blocker).FirstOrDefault(static x => x.VacuumLabel is not null);

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C06",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = waits.Count == 1
                ? "A session has been waiting for a lock for " + SessionText.Duration(longest)
                : SessionText.Number(waits.Count) + " sessions are waiting for locks, the longest for " + SessionText.Duration(longest),
            WhyItMatters =
                "A session waiting for a lock is a request that is not being answered, and everything that needs the same lock queues "
                + "behind it - so one transaction left open can stall a whole table, and a waiting ALTER stalls even readers. "
                + (applyLock
                    ? "Here it is Marten's apply-changes lock: a schema apply is running or stuck, and every node that applies on startup waits for it. "
                    : string.Empty)
                + (streamRows
                    ? "Waits on an mt_streams row are appends to one stream from two sessions at once; they are serialized on that row by design, "
                      + "and a long wait there means the first append's transaction is still open. "
                    : string.Empty)
                + (vacuumHead is { } head ? VacuumHeadAdvice(head, evaluation.Text) : string.Empty)
                + "The head of the chain is the session to look at; the studio never cancels or ends one.",
            Evidence = evidence,
            Caveat = SessionEvidence.Caveat(
                facts.Visibility,
                facts.ReadAt,
                (exact
                    ? "Waits are measured by pg_locks.waitstart."
                    : "On PostgreSQL before 14 there is no pg_locks.waitstart, so a wait is measured from the session's last state change - an upper bound.")
                + " " + evaluation.Text.StatementsOnSessionsPage),
            Link = SessionEvidence.SessionsLink,
            Remedy = applyLock
                ? new HealthRemedy(
                    """
                    // Apply schema changes once, at deploy, rather than from every node as it starts:
                    opts.AutoCreateSchemaObjects = AutoCreate.None;
                    // then: dotnet run -- db-apply   (with app.RunJasperFxCommands(args))
                    """,
                    "Marten serializes applies on ApplyChangesLockId so that two nodes never migrate at once; nodes that start together "
                    + "wait for the one that got the lock.")
                : null,
        });
    }

    /// <summary>
    /// What a <c>VACUUM</c> at the head of a chain means, and what to do about it (W5-V1, the 2026-10-26 health-gap analysis,
    /// 7a): a manual <c>VACUUM</c> and an anti-wraparound autovacuum never give way to a lock request - an ordinary autovacuum
    /// is cancelled by one after <c>deadlock_timeout</c> - so a migration's <c>ALTER</c> queued behind it waits, and every reader
    /// of the table queues behind the <c>ALTER</c>.
    /// </summary>
    internal static string VacuumHeadAdvice(LockBlocker head, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(text);

        string migration = text.Product == StudioProduct.MartenStudio
            ? "a migration queued behind it (ApplyAllDatabaseChangesOnStartup, db-apply)"
            : "a migration queued behind it (an ALTER TABLE, from whatever tool runs the host's migrations)";

        return "The head of the chain is " + head.VacuumLabel + ". "
            + (head.PreventsWraparound
                ? "An anti-wraparound autovacuum does not give way to a lock request, as a manual VACUUM does not; "
                : head.Autovacuum
                    ? "An ordinary autovacuum is cancelled by a lock request after deadlock_timeout, but one running to prevent wraparound, "
                      + "like a manual VACUUM, does not give way; "
                    : "A manual VACUUM, like an anti-wraparound autovacuum, does not give way to a lock request (an ordinary autovacuum is "
                      + "cancelled by one after deadlock_timeout); ")
            + migration + " holds every reader of the table behind itself. Let the vacuum finish and retry the migration - and never "
            + "cancel the vacuum while T12 is near its line. ";
    }

    /// <summary>
    /// The classifier over every registered source's declarations - the one the database browser uses, read afresh
    /// for the sweep, which has no circuit (<see cref="IDatabaseDeclarationsSource.ReadUncached" />) - or
    /// <see langword="null" /> when they cannot all be read, the studio registers no declarations, or the declarations
    /// source cannot be built in the sweep's scope: a classifier names relations, and none at all is the closed failure.
    /// </summary>
    /// <remarks>
    /// Only the source's <em>resolution</em> is tolerated, because only that is new: in 0.4 C06 read the store registry, a
    /// singleton, and the declarations without a source between them, and a scoped source's graph is something a sweep's
    /// scope with no circuit might not build. A failure of the read itself is not swallowed - <c>ReadUncached</c> fails
    /// closed on everything it expects, so anything it throws is a bug, and it surfaces as 0.4's did: a "could not check"
    /// row and event 9222.
    /// </remarks>
    internal static DatabaseObjectClassifier? Classifier(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IDatabaseDeclarationsSource? declarations;
        try
        {
            declarations = context.Services.GetService<IDatabaseDeclarationsSource>();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail closed, as 0.4's read did: a source that will not resolve in a scope with no circuit costs C06 its
            // relation names, not its row.
            return null;
        }

        if (declarations is null)
        {
            return null;
        }

        DatabaseDeclarations declared = declarations.ReadUncached(context.Source);
        return declared.Succeeded ? declared.Classifier : null;
    }

    private static (SessionVisibility Visibility, LockWaiter? Waiter, long Total) MapWaiter(NpgsqlDataReader reader, SessionLockLabels labels)
    {
        SessionVisibility visibility = SessionReads.Visibility(reader);

        if (reader.IsDBNull(2))
        {
            return (visibility, null, 0);
        }

        LockTarget target = labels.Describe(
            reader.GetString(3),
            SessionReads.Locked(reader, 5),
            SessionReads.Value<long>(reader, 9),
            SessionReads.Locked(reader, 16));

        return (
            visibility,
            new LockWaiter(
                reader.GetInt32(2),
                SessionReads.Text(reader, 12),
                SessionReads.Text(reader, 13),
                SessionReads.Text(reader, 14),
                reader.GetString(4),
                target,
                !reader.IsDBNull(10) && reader.GetBoolean(10),
                SessionReads.Seconds(reader, 11) ?? TimeSpan.Zero,
                SessionReads.Pids(reader, 20)),
            reader.GetInt64(21));
    }

    private static LockBlocker MapBlocker(NpgsqlDataReader reader) => new(
        reader.GetInt32(0),
        !reader.IsDBNull(1) && reader.GetBoolean(1),
        SessionReads.Text(reader, 2),
        SessionReads.Text(reader, 3),
        SessionReads.Text(reader, 4),
        SessionReads.Text(reader, 5),
        SessionReads.Seconds(reader, 6),
        !reader.IsDBNull(8) && reader.GetBoolean(8),
        !reader.IsDBNull(9) && reader.GetBoolean(9))
    {
        Vacuum = !reader.IsDBNull(10) && reader.GetBoolean(10),
        Autovacuum = string.Equals(SessionReads.Text(reader, 11), "autovacuum worker", StringComparison.Ordinal),
        PreventsWraparound = !reader.IsDBNull(12) && reader.GetBoolean(12),
    };
}
