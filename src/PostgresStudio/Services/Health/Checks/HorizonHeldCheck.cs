using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>How a holder's age in time is known.</summary>
internal enum HorizonTimeKind
{
    /// <summary>
    /// A lower bound from Postgres' own timestamps: a running statement's age, the time idle in a transaction, a prepared
    /// transaction's age, an inactive slot's <c>inactive_since</c> (PostgreSQL 17+).
    /// </summary>
    AtLeast,

    /// <summary>
    /// A lower bound from the advisor's own samples of the transaction-id counter: the first sample already past the id the
    /// holder <em>owns</em> - a session's own transaction id, a prepared transaction's (<see cref="CounterHistory.PassedBy" />)
    /// - never more than the holder's transaction has been open. A snapshot, a slot or a standby owns no id, and is never
    /// dated this way (H9 re-review, blocking 1).
    /// </summary>
    FromHistory,

    /// <summary>
    /// A lower bound from a holder behind it: a snapshot whose oldest running transaction was this holder's own id, held that
    /// long - so this holder's transaction was already running then (<see cref="HorizonHeldCheck.RootsOf" />).
    /// </summary>
    Behind,

    /// <summary>Not known: not recorded, not readable, and no sample of the counter past an id it owns.</summary>
    Unknown,
}

/// <summary>One holder of the vacuum horizon, of any kind, as T14 judges it.</summary>
/// <remarks>Init properties, so a unit test states only the facts its case is about.</remarks>
internal sealed record HorizonHolder
{
    /// <summary>What it is.</summary>
    public required HealthHorizonHolderKind Kind { get; init; }

    /// <summary>How far back it holds the horizon, in transactions.</summary>
    public required long XidAge { get; init; }

    /// <summary>
    /// The age of the id it <em>owns</em> - a session's own transaction id, a prepared transaction's - or
    /// <see langword="null" /> for a snapshot alone, a replication slot or a standby, whose horizon is the oldest transaction
    /// running elsewhere when it was taken (<see cref="HealthHorizonHolderRow.OwnXidAge" />).
    /// </summary>
    public long? OwnXidAge { get; init; }

    /// <summary>
    /// The age of the id it owns: <see cref="OwnXidAge" />, or - for a prepared transaction, whose horizon is always its own
    /// transaction id - its <see cref="XidAge" />.
    /// </summary>
    public long? OwnedXidAge => OwnXidAge
        ?? (Kind is HealthHorizonHolderKind.Prepared or HealthHorizonHolderKind.OtherDatabasePrepared ? XidAge : null);

    /// <summary>
    /// Whether the horizon it holds is an id it owns, rather than a snapshot of somebody else's: a writer whose own id is
    /// older than its snapshot, or a prepared transaction.
    /// </summary>
    public bool HoldsOwnId => OwnedXidAge is { } own && own >= XidAge;

    /// <summary>The least it can have held it, as of the read (<see cref="HealthHorizonHolderRow.AtLeast" />).</summary>
    public TimeSpan? AtLeast { get; init; }

    /// <summary>The most it can have held it, as of the read: a session's transaction age.</summary>
    public TimeSpan? AtMost { get; init; }

    /// <summary>Which timestamp <see cref="AtLeast" /> came from (<see cref="HealthHorizonHolderRow.Bound" />).</summary>
    public string? Bound { get; init; }

    /// <summary>A session's or a standby's process id. A detail.</summary>
    public int? ProcessId { get; init; }

    /// <summary>A session's role, or a prepared transaction's owner. A detail.</summary>
    public string? User { get; init; }

    /// <summary>A session's or a standby's application name. A detail.</summary>
    public string? Application { get; init; }

    /// <summary>A session's or a standby's client address. A detail.</summary>
    public string? Client { get; init; }

    /// <summary>When a session's backend started. A detail.</summary>
    public DateTimeOffset? BackendStart { get; init; }

    /// <summary>A session's or a standby's state.</summary>
    public string? State { get; init; }

    /// <summary>A prepared transaction's global id, or a slot's name. A detail.</summary>
    public string? Name { get; init; }

    /// <summary>A slot's type: <c>physical</c> or <c>logical</c>.</summary>
    public string? SlotType { get; init; }

    /// <summary>A logical slot's plugin. A detail.</summary>
    public string? Plugin { get; init; }

    /// <summary>A logical slot's database. A detail.</summary>
    public string? SlotDatabase { get; init; }

    /// <summary>Whether a slot has a consumer streaming from it now.</summary>
    public bool Active { get; init; }

    /// <summary>
    /// Whether Postgres reports a slot <c>lost</c> but has not invalidated it yet: the WAL it needs is gone, so no consumer
    /// can ever use it again, but its <c>xmin</c> still holds the horizon until a checkpoint invalidates it (H9 re-review, F2).
    /// </summary>
    public bool Lost { get; init; }

    /// <summary>
    /// For a slot: which slot of the read it is, so two holders of one slot (its <c>xmin</c> and its <c>catalog_xmin</c>)
    /// count once - and a slot of another database, which has no name here, is still told from another.
    /// </summary>
    public int? SlotOrdinal { get; init; }

    /// <summary>
    /// Whether a slot belongs to another database on the server: a logical slot whose database is not this one. It is
    /// never named - no name, plugin or database at any gate - because it may be another tenant's; its age still counts.
    /// </summary>
    public bool OtherDatabase { get; init; }

    /// <summary>Whether a session has written - holds a transaction id.</summary>
    public bool HasTransactionId { get; init; }

    /// <summary>Whether this role may not read into it.</summary>
    public bool Hidden { get; init; }

    /// <summary>Whether the session holds one of the store's daemon locks: Marten's HotCold leader.</summary>
    public bool HoldsDaemonLock { get; init; }

    /// <summary>How many holders of this kind there were.</summary>
    public long Total { get; init; } = 1;

    /// <summary>
    /// Whether it holds back vacuum of the store's tables - dead rows stay, freezing stops at it. The other kinds hold back
    /// only the system catalogs, and through them every database's freeze.
    /// </summary>
    public bool HoldsTables => Kind is HealthHorizonHolderKind.Session or HealthHorizonHolderKind.Prepared
        or HealthHorizonHolderKind.Standby or HealthHorizonHolderKind.SlotXmin;

    /// <summary>A holder from the holders read.</summary>
    public static HorizonHolder From(HealthHorizonHolderRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new HorizonHolder
        {
            Kind = row.Kind,
            XidAge = row.XidAge,
            AtLeast = row.AtLeast,
            AtMost = row.TransactionOpen,
            Bound = row.Bound,
            ProcessId = row.ProcessId,
            User = row.User,
            Application = row.Application,
            Client = row.Client,
            BackendStart = row.BackendStart,
            State = row.State,
            Name = row.Gid,
            HasTransactionId = row.HasTransactionId,
            Hidden = row.Hidden,
            HoldsDaemonLock = row.HoldsDaemonLock,
            Total = row.Total,
            OwnXidAge = row.OwnXidAge,
        };
    }

    /// <summary>
    /// Whether Postgres has invalidated a slot, so its <c>xmin</c> and <c>catalog_xmin</c> - which invalidation leaves in
    /// place, and the view still shows - hold nothing back (H9 re-review, F2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ReplicationSlotsComputeRequiredXmin</c> skips an invalidated slot ("invalidated slots need not apply"), and an
    /// invalidation never clears either xmin - read on the 13 to 17 stable branches. On 13 to 15 a slot is invalidated only for
    /// removed WAL, which sets <c>invalidated_at</c> and clears <c>restart_lsn</c> (<c>slot.c</c> 1231-1232 on 13, 1284-1285 on
    /// 14, 1364-1365 on 15), the computation skips a slot with exactly that shape (806-811, 856-861, 924-929), and
    /// <c>pg_get_replication_slots</c> reports it <c>lost</c> with a null <c>restart_lsn</c> (<c>slotfuncs.c</c> 348-354 and
    /// 403 on 13). On 16 and 17 the computation skips <c>data.invalidated != RS_INVAL_NONE</c> (<c>slot.c</c> 926-929 on 16,
    /// 1139-1142 on 17); the view reports every invalidated slot <c>lost</c> (<c>slotfuncs.c</c> 321-322, 329-330), clears
    /// <c>restart_lsn</c> for removed WAL (<c>slot.c</c> 1488, 1712), says <c>conflicting</c> of a logical slot invalidated
    /// for any cause on 16 (<c>slotfuncs.c</c> 409), and gives the <c>invalidation_reason</c> of every one on 17 (428-438).
    /// So a slot is invalidated exactly when it has an invalidation reason, is conflicting, or is <c>lost</c> with no
    /// <c>restart_lsn</c> - which is what a null <see cref="ReplicationSlot.RetainedBytes" /> is.
    /// </para>
    /// <para>
    /// A slot reported <c>lost</c> that still has a <c>restart_lsn</c> is not invalidated: its WAL is gone
    /// (<c>GetWALAvailability</c> answered <c>WALAVAIL_REMOVED</c> and no consumer is connected - <c>slotfuncs.c</c> 376-403 on
    /// 13), and the next checkpoint will invalidate it, but until then it holds the horizon. That one is a holder, marked
    /// <see cref="Lost" />.
    /// </para>
    /// </remarks>
    internal static bool Invalidated(ReplicationSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        return slot.InvalidationReason is { Length: > 0 }
            || slot.Conflicting == true
            || (IsReportedLost(slot) && slot.RetainedBytes is null);
    }

    private static bool IsReportedLost(ReplicationSlot slot) => string.Equals(slot.WalStatus, "lost", StringComparison.Ordinal);

    /// <summary>The holders a replication slot is: its <c>xmin</c>, its <c>catalog_xmin</c>, both or neither.</summary>
    /// <remarks>
    /// The slot is H10's <see cref="ReplicationSlot" />, the shared read's one shape (<c>server.replication-slots</c>), so a
    /// sweep that runs R01 and T14 reads the view once.
    /// </remarks>
    public static IEnumerable<HorizonHolder> From(ReplicationSlot slot, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(slot);

        // An invalidated slot holds nothing, though the view still shows its xmin and catalog_xmin.
        if (Invalidated(slot))
        {
            yield break;
        }

        // An inactive slot's xmin has not moved since it went inactive: a lower bound of how long it has held it.
        TimeSpan? inactive = slot.Active ? null : slot.InactiveAtRead;

        foreach ((HealthHorizonHolderKind kind, int? age) in new[]
                 {
                     (HealthHorizonHolderKind.SlotXmin, slot.XminAge),
                     (HealthHorizonHolderKind.SlotCatalogXmin, slot.CatalogXminAge),
                 })
        {
            if (age is { } xids)
            {
                yield return new HorizonHolder
                {
                    Kind = kind,
                    XidAge = xids,
                    AtLeast = inactive,
                    SlotOrdinal = ordinal,
                    OtherDatabase = !slot.ThisDatabase,
                    // Another database's slot is described by its kind and its age alone: its name, plugin and database
                    // can be another tenant's, and nothing about it is this database's to say.
                    Name = slot.ThisDatabase ? slot.Name : null,
                    SlotType = slot.SlotType,
                    Plugin = slot.ThisDatabase ? slot.Plugin : null,
                    SlotDatabase = slot.ThisDatabase ? slot.Database : null,
                    Active = slot.Active,
                    Lost = IsReportedLost(slot),
                };
            }
        }
    }
}

/// <summary>What T14 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Visibility">How much of <c>pg_stat_activity</c> the role could see.</param>
/// <param name="Holders">Every holder found, of every kind.</param>
/// <param name="Wraparound">The database-wide wraparound facts T12 reads too.</param>
/// <param name="Tables">The store's tables, for the dead rows vacuum could not remove (T07's read).</param>
/// <param name="HasAsyncShards">Whether the store runs async projections, for which an open writer can pin the high-water mark (D01).</param>
/// <param name="LeaderLockInTransaction">The store's <c>Events.UseAdvisoryLockTransaction</c>.</param>
internal sealed record HorizonFacts(
    DateTimeOffset ReadAt,
    SessionVisibility Visibility,
    IReadOnlyList<HorizonHolder> Holders,
    HealthWraparoundDatabase Wraparound,
    IReadOnlyList<TableFacts> Tables,
    bool HasAsyncShards,
    bool LeaderLockInTransaction);

/// <summary>
/// T14: vacuum is held back - and by which session, prepared transaction, replication slot or standby.
/// </summary>
/// <remarks>
/// <para>
/// Vacuum may remove a dead row, or freeze a live one, only once no transaction could still see it: the oldest
/// transaction id or snapshot anything holds is the <em>horizon</em>, and nothing newer than it is cleaned up. Four
/// kinds of thing hold it, and each is read where Postgres keeps it: a session's <c>backend_xid</c> and
/// <c>backend_xmin</c> (<c>pg_stat_activity</c>), a prepared transaction (<c>pg_prepared_xacts</c>), a replication
/// slot's <c>xmin</c> and <c>catalog_xmin</c> (<c>pg_replication_slots</c>, read through R01's
/// <see cref="ReplicationSlotSnapshot" /> under its key, <c>server.replication-slots</c>) and a standby's <c>hot_standby_feedback</c>
/// (<c>pg_stat_replication.backend_xmin</c>). The one furthest back binds.
/// </para>
/// <para>
/// <b>Two reaches.</b> This database's sessions and prepared transactions, a slot's <c>xmin</c> and a standby's
/// feedback hold back vacuum of the store's own tables: dead rows stay (T07's "vacuum falling behind" is often this),
/// and freezing stops at them (T12). Another database's sessions and prepared transactions, and a logical slot's
/// <c>catalog_xmin</c>, hold back only the system catalogs - but every database's <c>datfrozenxid</c> counts the
/// shared catalogs, so they stop every database's freeze. The first kind is judged by time; the second only once it
/// stops a forced freeze, because a catalog that waits an hour is not a problem anybody has.
/// </para>
/// <para>
/// <b>Thresholds.</b> A holder of the store's tables older than <see cref="WarningAfter" /> is a Warning, older than
/// <see cref="CriticalAfter" /> Critical - and so is any holder older than an hour while the database is near T12's
/// line. A holder of either reach further back than <c>autovacuum_freeze_max_age</c> is a Warning whatever its age in
/// time, because the forced freeze cannot get past it, and past T12's Warning age Critical.
/// </para>
/// <para>
/// <b>Age in time, honestly.</b> Never a session's transaction age alone: a read-committed transaction takes a fresh
/// snapshot with every statement, so a transaction open for a day may hold the horizon back by a minute - Marten's own
/// HotCold leader at 9.39 is such a session, measured live. So the age is the lower bound Postgres' timestamps give (a
/// running statement's age, the time idle in a transaction, a prepared transaction's age, an inactive slot's
/// <c>inactive_since</c> on PostgreSQL 17), or - longer, and never longer than the transaction has been open - the
/// bound the advisor's own samples of the transaction-id counter give for an id the holder <em>owns</em>: the first sample
/// T12 took that was already past a session's own transaction id or a prepared transaction's - never a rate, so a burst of
/// ids cannot make a minute-old hold look hours old, and never further back than the ring's oldest sample, about an hour
/// at the default <c>HealthInterval</c>. A snapshot's <c>xmin</c>, a slot's and a standby's are not their
/// holder's: each is the oldest transaction running anywhere when the snapshot was taken, so the counter dates whoever was
/// handed that id, not the snapshot, and is never used for them (H9 re-review, blocking 1). A slot or a standby therefore
/// has no age in time but an inactive slot's <c>inactive_since</c>, and is judged by how far back it holds. Without
/// <c>pg_read_all_stats</c> another role's session is shown to this role with its transaction ids but without its timings
/// - measured on PostgreSQL 17 - so it is still found and still judged by how far back it holds the horizon, and the caveat
/// says how many such sessions the role cannot see the start of (H3's hidden-session rule,
/// <see cref="HealthConnectionQueries.ClientSession" />).
/// </para>
/// <para>
/// <b>Behind another.</b> A snapshot whose <c>xmin</c> is exactly an id another holder owns is behind that holder: it moves
/// on the moment that one ends, and naming it would send a person to the wrong session - Marten's own HotCold leader is the
/// one it would usually be, its minute-old snapshot pinned at a prepared transaction's id. So it is never the holder named:
/// the owner is, and inherits the reach (an owner in another database holds this database's tables through the snapshots
/// behind it here) and the longest age of what is behind it, since a snapshot that saw the owner running was taken while
/// it ran (<see cref="RootsOf" />). Among holders equally far back, the one that owns its id wins, and the daemon's leader
/// never wins over another holder.
/// </para>
/// <para>
/// <b>Gates.</b> The kind and the age are the database's (<see cref="HealthGate.Database" />); who it is - process id,
/// role, application, client, a prepared transaction's gid and owner, a slot's name, plugin and database, a standby's
/// address - is evidence behind <see cref="HealthGate.Activity" />. Another database's holder is never named at all.
/// </para>
/// <para>
/// <b>Marten's angle.</b> A long rebuild batch and an open transaction that has written are one mechanism with two
/// symptoms: the writer pins the high-water mark (D01, whose Explain names it), and its transaction pins the horizon.
/// A session that holds one of the store's daemon locks is named as the store's own HotCold leader.
/// </para>
/// </remarks>
[HealthCheck("T14", HealthArea.Tables, HealthCostTier.Catalog, "Vacuum held back")]
internal sealed class HorizonHeldCheck : HealthCheck<HorizonFacts>
{
    /// <summary>A holder of the store's tables this old is a Warning.</summary>
    public static readonly TimeSpan WarningAfter = TimeSpan.FromHours(1);

    /// <summary>A holder of the store's tables this old is Critical.</summary>
    public static readonly TimeSpan CriticalAfter = TimeSpan.FromHours(24);

    /// <summary>The link a writer's finding carries: D01's Explain, which names what pins the high-water mark.</summary>
    internal static readonly HealthLink ExplainLink = new("Explain it on the projections screen", "projections");

    /// <inheritdoc />
    public override async Task<HorizonFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only the source's own advisory keys are wanted, so no relation classifier is built.
        SessionLockLabels labels = context.Services.GetRequiredService<ISessionLockLabeler>().For(context.Source, classifier: null);

        IReadOnlyList<(bool CanReadAllStats, long HiddenSessions, HealthHorizonHolderRow? Holder)> rows = await context.Sql
            .QueryAsync(HealthHorizonQueries.Holders(labels.DaemonLockIds), HealthHorizonQueries.ReadHolder, cancellationToken)
            .ConfigureAwait(false);

        ReplicationSlotSnapshot slots = await ReplicationSlotSnapshot.SharedAsync(context).ConfigureAwait(false);
        WraparoundFacts wraparound = await Wraparound.SharedAsync(context).ConfigureAwait(false);
        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);

        // What the source's own machinery means here - a daemon whose progress a writer pins, a leader that holds its lock
        // in a transaction - from its configuration; a source with no daemon has none.
        HorizonSourceFacts source = context.Services.GetService<IHorizonContributor>()?.Describe(context) ?? HorizonSourceFacts.None;

        List<HorizonHolder> holders = [.. rows.Where(static x => x.Holder is not null).Select(static x => HorizonHolder.From(x.Holder!))];
        holders.AddRange(slots.Slots.SelectMany(static (slot, ordinal) => HorizonHolder.From(slot, ordinal)));

        return new HorizonFacts(
            context.Now,
            rows.Count > 0 ? new SessionVisibility(rows[0].CanReadAllStats, rows[0].HiddenSessions) : SessionVisibility.Everything,
            holders,
            wraparound.Database,
            tables,
            source.HasAsyncShards,
            source.LeaderLockInTransaction);
    }

    /// <summary>
    /// A holder's age in time as of the evaluation, and how it is known: the lower bound Postgres' timestamps give, or - where
    /// it is longer - the one the advisor's samples of the transaction-id counter give, held to the most the holder's
    /// transaction allows. Both are bounds; nothing here is extrapolated from a rate (H9 review, follow-up 1).
    /// </summary>
    /// <remarks>
    /// Never a transaction's age alone (<see cref="HealthHorizonHolderRow.AtLeast" /> says why): that is only the most it can
    /// be, and both lower bounds are held to it.
    /// </remarks>
    /// <param name="holder">The holder.</param>
    /// <param name="facts">The read, for its time and the counter's value then.</param>
    /// <param name="evaluation">When it is judged.</param>
    /// <param name="counter">T12's samples of the transaction-id counter, at or below this check's gate.</param>
    internal static (TimeSpan? Age, HorizonTimeKind Kind) AgeOf(HorizonHolder holder, HorizonFacts facts, HealthEvaluation evaluation, HealthTrend counter)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(counter);

        TimeSpan? low = holder.AtLeast is { } atLeast ? SnapshotAge.At(atLeast, facts.ReadAt, evaluation) : null;
        TimeSpan? high = holder.AtMost is { } atMost ? SnapshotAge.At(atMost, facts.ReadAt, evaluation) : null;

        // A lower bound never outlives the upper (H9 review, B2): the statement of a DO that commits in a loop is older
        // than its transaction, and the SQL caps it already; this holds the evaluator to it as well.
        if (low is { } lower && high is { } upper && lower > upper)
        {
            low = upper;
        }

        // Only an id the holder owns is dated by the counter (H9 re-review, blocking 1): it has held its own transaction id
        // since the id was handed out, and the first sample of the counter already past it is when, at the latest, that
        // was. A snapshot's xmin is the oldest id running anywhere when it was taken - somebody else's - so it is not.
        if (holder.OwnedXidAge is { } own && own > 0 && facts.Wraparound.XidCounter > 0
            && CounterHistory.PassedBy(counter, facts.Wraparound.XidCounter - (double) own) is { } passedBy
            && evaluation.Now > passedBy)
        {
            TimeSpan fromHistory = evaluation.Now - passedBy;

            if (high is { } most && fromHistory > most)
            {
                fromHistory = most;
            }

            if (low is not { } least || fromHistory > least)
            {
                return (fromHistory, HorizonTimeKind.FromHistory);
            }
        }

        return low is { } bound ? (bound, HorizonTimeKind.AtLeast) : (null, HorizonTimeKind.Unknown);
    }

    /// <summary>
    /// Whether the database is near T12's line: its age or the server's past T12's Warning age, or, past the forced line, the
    /// write-refusal line within T12's forecast horizon.
    /// </summary>
    internal static bool NearTransactionIdLine(HealthWraparoundDatabase database, HorizonForecast? rate)
    {
        ArgumentNullException.ThrowIfNull(database);

        long age = Math.Max(database.DatabaseXidAge, database.ServerXidAge);

        return age > Wraparound.WarningAge(database.FreezeMaxAge)
            || (age > database.FreezeMaxAge && rate?.Until(age, Wraparound.RefusalAge(database.ServerVersion)) is not null);
    }

    /// <summary>
    /// Which holders are only behind another, mapped to the holder at the root (H9 re-review, blocking 1).
    /// </summary>
    /// <remarks>
    /// A holder whose horizon is a snapshot - not an id it owns (<see cref="HorizonHolder.HoldsOwnId" />) - and whose
    /// <c>XidAge</c> is exactly another holder's <see cref="HorizonHolder.OwnedXidAge" /> is behind that holder: a snapshot's
    /// <c>xmin</c> is the oldest transaction running anywhere when it was taken, and that is the owner's id. It moves on the
    /// moment the owner ends. Followed to the root, which owns its horizon or is behind nothing found. Exact, because both
    /// ages come from one statement and <c>age()</c> measures against one transaction id throughout it; a replication slot
    /// is read in another statement - H10's shared read - so it is never matched, and is judged as its own holder.
    /// </remarks>
    internal static IReadOnlyDictionary<HorizonHolder, HorizonHolder> RootsOf(IReadOnlyList<HorizonHolder> holders)
    {
        ArgumentNullException.ThrowIfNull(holders);

        // By reference: two holders read alike are still two holders.
        Dictionary<HorizonHolder, HorizonHolder> direct = new(ReferenceEqualityComparer.Instance);

        foreach (HorizonHolder holder in holders)
        {
            if (holder.HoldsOwnId || holder.SlotOrdinal is not null)
            {
                continue;
            }

            HorizonHolder? owner = holders.FirstOrDefault(x =>
                !ReferenceEquals(x, holder) && x.SlotOrdinal is null && x.OwnedXidAge is { } own && own == holder.XidAge);

            if (owner is not null)
            {
                direct[holder] = owner;
            }
        }

        Dictionary<HorizonHolder, HorizonHolder> roots = new(ReferenceEqualityComparer.Instance);

        foreach ((HorizonHolder behind, HorizonHolder owner) in direct)
        {
            // Each step is strictly further back, so this ends; the bound is only a guard.
            HorizonHolder root = owner;
            for (int steps = 0; steps < holders.Count && direct.TryGetValue(root, out HorizonHolder? next); steps++)
            {
                root = next;
            }

            if (!ReferenceEquals(root, behind))
            {
                roots[behind] = root;
            }
        }

        return roots;
    }

    /// <summary>What one holder alone warrants, or <see langword="null" />.</summary>
    /// <param name="holder">The holder.</param>
    /// <param name="age">Its age in time, if known.</param>
    /// <param name="database">The wraparound facts.</param>
    /// <param name="nearLine">Whether the database is near T12's line.</param>
    /// <param name="holdsTables">
    /// Whether it holds back the store's tables, if not by its own kind (<see cref="HorizonHolder.HoldsTables" />): an owner in
    /// another database holds them through the snapshots behind it here.
    /// </param>
    internal static HealthSeverity? SeverityOf(HorizonHolder holder, TimeSpan? age, HealthWraparoundDatabase database, bool nearLine, bool? holdsTables = null)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(database);

        long freezeLine = database.FreezeMaxAge;
        long t12Warning = Wraparound.WarningAge(freezeLine);

        if (holdsTables ?? holder.HoldsTables)
        {
            if (age >= CriticalAfter || holder.XidAge >= t12Warning || (age >= WarningAfter && nearLine))
            {
                return HealthSeverity.Critical;
            }

            return age >= WarningAfter || holder.XidAge >= freezeLine ? HealthSeverity.Warning : null;
        }

        if (holder.XidAge >= t12Warning || (holder.XidAge >= freezeLine && nearLine))
        {
            return HealthSeverity.Critical;
        }

        return holder.XidAge >= freezeLine ? HealthSeverity.Warning : null;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(HorizonFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        HorizonForecast? rate = TransactionIdAgeCheck.Forecast(evaluation.History);
        bool nearLine = NearTransactionIdLine(facts.Wraparound, rate);
        HealthTrend counter = evaluation.History.Trend(Wraparound.XidCounterSeries);

        IReadOnlyDictionary<HorizonHolder, HorizonHolder> rootOf = RootsOf(facts.Holders);

        Dictionary<HorizonHolder, (TimeSpan? Age, HorizonTimeKind Kind)> ages = new(ReferenceEqualityComparer.Instance);
        foreach (HorizonHolder holder in facts.Holders)
        {
            ages[holder] = AgeOf(holder, facts, evaluation, counter);
        }

        List<Judged> judged = [];

        foreach (HorizonHolder holder in facts.Holders.Where(x => !rootOf.ContainsKey(x)))
        {
            List<HorizonHolder> behind = [.. facts.Holders.Where(x => rootOf.TryGetValue(x, out HorizonHolder? root) && ReferenceEquals(root, holder))];
            (TimeSpan? age, HorizonTimeKind kind) = ages[holder];

            // A snapshot that saw this holder's id running was taken while it ran: the longest held of them bounds this one -
            // held, like every bound here, to the most this holder's own transaction allows.
            TimeSpan? most = holder.AtMost is { } atMost ? SnapshotAge.At(atMost, facts.ReadAt, evaluation) : null;

            foreach (HorizonHolder waiting in behind)
            {
                if (ages[waiting].Age is { } waited && (age is not { } own || waited > own))
                {
                    (age, kind) = (most is { } upper && waited > upper ? upper : waited, HorizonTimeKind.Behind);
                }
            }

            bool holdsTables = holder.HoldsTables || behind.Any(static x => x.HoldsTables);
            judged.Add(new Judged(holder, age, kind, holdsTables, behind, SeverityOf(holder, age, facts.Wraparound, nearLine, holdsTables)));
        }

        // Equally far back: the one that owns its id, and never the daemon's leader over another holder.
        Judged? oldest = judged
            .OrderByDescending(static x => x.Holder.XidAge)
            .ThenByDescending(static x => x.Holder.HoldsOwnId)
            .ThenBy(static x => x.Holder.HoldsDaemonLock)
            .FirstOrDefault();

        Judged? chosen = judged
            .Where(static x => x.Severity is not null)
            .OrderBy(static x => x.Severity)
            .ThenByDescending(static x => x.HoldsTables)
            .ThenByDescending(static x => x.Holder.XidAge)
            .ThenByDescending(static x => x.Holder.HoldsOwnId)
            .ThenBy(static x => x.Holder.HoldsDaemonLock)
            .FirstOrDefault();

        string? hidden = HiddenCaveat(facts.Visibility, counter.Points.Count > 0);

        if (chosen is null)
        {
            // Only what is known (H9 re-review 2, follow-up 1): an age is a lower bound, the counter's reaches back no further
            // than the ring, and a standby, an active slot or a withheld snapshot has none at all.
            string note = oldest is null
                ? "Nothing holds the vacuum horizon back: no session, prepared transaction, replication slot or standby has an old transaction id or snapshot."
                : "No holder is known to have held the vacuum horizon back for an hour, or past autovacuum_freeze_max_age: the oldest holder is "
                  + KindText(oldest.Holder) + ", " + Wraparound.Number(oldest.Holder.XidAge) + " transactions back"
                  + (oldest.Age is { } age
                      ? ", for " + TimeText(oldest.Holder, age, oldest.TimeKind)
                      : " - for how long is not known: " + UnknownWhy(oldest.Holder)) + ".";

            return HealthVerdict.Quiet(hidden is null ? note : note + " " + hidden);
        }

        HorizonHolder held = chosen.Holder;
        bool leader = held.Kind == HealthHorizonHolderKind.Session && held.HoldsDaemonLock;

        List<HealthEvidence> evidence =
        [
            new("Held back by", KindText(held)),
            new("Transactions behind", Wraparound.Number(held.XidAge)),
            new("Held for", chosen.Age is { } heldFor ? TimeText(held, heldFor, chosen.TimeKind) : "not known - " + UnknownWhy(held)),
            new("What it holds back", chosen.HoldsTables
                ? (held.HoldsTables
                    ? "vacuum of every table in this database: dead rows stay, and freezing stops at it"
                    : "vacuum of every table in this database, through the snapshots behind it here: dead rows stay, and freezing stops at it")
                : "vacuum of the system catalogs - and, through the shared ones, the freeze of every database on the server"),
        ];

        if (held.AtMost is { } open)
        {
            evidence.Add(new HealthEvidence("Its transaction has been open for", SessionText.Duration(SnapshotAge.At(open, facts.ReadAt, evaluation))
                + " - the most it can have held the horizon"));
        }

        if (Who(held) is { } who)
        {
            evidence.Add(new HealthEvidence("Who it is", who, HealthGate.Activity));
        }

        if (chosen.Behind.Count > 0)
        {
            bool one = chosen.Behind.Count == 1;
            evidence.Add(new HealthEvidence("Behind it, at the same point", string.Join("; ", chosen.Behind.Select(KindText).Distinct(StringComparer.Ordinal))
                + " - " + (one ? "its snapshot's" : "their snapshots'") + " oldest running transaction is this one, so "
                + (one ? "it moves" : "they move") + " on once this one ends"));
        }

        if (chosen.HoldsTables)
        {
            evidence.Add(new HealthEvidence("Dead rows in the store's tables", DeadRows(facts, chosen.Age, chosen.TimeKind)));
        }

        if (oldest is { } binding && binding.Holder.XidAge > held.XidAge)
        {
            evidence.Add(new HealthEvidence("Oldest holder of all", KindText(binding.Holder) + ", " + Wraparound.Number(binding.Holder.XidAge) + " transactions back"));
        }

        evidence.Add(new HealthEvidence("Holders found", Counts(facts.Holders, chosen.Behind)));
        evidence.Add(new HealthEvidence("autovacuum_freeze_max_age", Wraparound.Number(facts.Wraparound.FreezeMaxAge)));
        evidence.Add(new HealthEvidence("This database's age(datfrozenxid)", Wraparound.Number(facts.Wraparound.DatabaseXidAge)
            + (nearLine ? " - near T12's line" : string.Empty)));

        List<string> caveat =
        [
            "Read in pg_stat_activity, pg_prepared_xacts, pg_replication_slots and pg_stat_replication at "
            + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + "; ages are measured by Postgres's own clock.",
        ];

        if (hidden is not null)
        {
            caveat.Add(hidden);
        }

        if (chosen.TimeKind == HorizonTimeKind.FromHistory && !held.Hidden)
        {
            caveat.Add("Its age in time is dated from the advisor's own samples of the transaction-id counter: the first sample already "
                + "past the transaction id it owns. That is a bound, never an estimate - the hold may be older, never younger: it reaches "
                + "back no further than the oldest sample the advisor holds (60 of them, about an hour at the default HealthInterval), "
                + "and is never more than its transaction has been open.");
        }

        if (chosen.TimeKind == HorizonTimeKind.Behind)
        {
            caveat.Add("Its age in time is the longest a holder behind it has kept a snapshot at this one's id: that snapshot was taken "
                + "while this transaction was running, so this one is at least that old.");
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "T14",
            Severity = chosen.Severity!.Value,
            Title = Title(held, chosen.Age, chosen.TimeKind, chosen.HoldsTables),
            WhyItMatters = WhyItMatters(held, leader, facts, chosen.HoldsTables, chosen.Behind.Count > 0),
            Evidence = evidence,
            Caveat = string.Join(" ", caveat),
            Remedy = RemedyFor(held, leader, facts.LeaderLockInTransaction, evaluation.Text),
            Link = LinkFor(held, facts.HasAsyncShards),
        });
    }

    /// <summary>The kind of holder, in the database's words - never who it is.</summary>
    internal static string KindText(HorizonHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);

        return holder.Kind switch
        {
            HealthHorizonHolderKind.Session when holder.HoldsDaemonLock => "the store's own HotCold daemon leader",
            HealthHorizonHolderKind.Session when holder.Hidden => "a session of another role (its state is not readable)",
            HealthHorizonHolderKind.Session when holder.HasTransactionId => "a session's open transaction that has written",
            HealthHorizonHolderKind.Session => "a session's snapshot",
            HealthHorizonHolderKind.OtherDatabaseSession => "a session in another database on this server",
            HealthHorizonHolderKind.Prepared => "a prepared transaction",
            HealthHorizonHolderKind.OtherDatabasePrepared => "a prepared transaction in another database on this server",
            HealthHorizonHolderKind.Standby => "a standby's hot_standby_feedback",
            HealthHorizonHolderKind.SlotXmin when holder.OtherDatabase => "a logical slot of another database on this server",
            HealthHorizonHolderKind.SlotXmin => "a " + (holder.Lost ? "lost " : string.Empty) + (holder.SlotType ?? "replication") + " replication slot",
            _ when holder.OtherDatabase => "a logical slot of another database on this server (its hold on the system catalogs)",
            _ => "a " + (holder.Lost ? "lost " : string.Empty) + (holder.SlotType ?? "logical") + " replication slot's hold on the system catalogs",
        };
    }

    /// <summary>"at least 3 h 5 min (its statement has run that long)", "at least 45 min (by the advisor's samples of the transaction-id counter)".</summary>
    internal static string TimeText(HorizonHolder holder, TimeSpan age, HorizonTimeKind kind) => kind switch
    {
        HorizonTimeKind.AtLeast => "at least " + SessionText.Duration(age) + " (" + LowerBoundWhy(holder) + ")",
        HorizonTimeKind.FromHistory => "at least " + SessionText.Duration(age) + " (by the advisor's samples of the transaction-id counter)",
        HorizonTimeKind.Behind => "at least " + SessionText.Duration(age) + " (a snapshot behind it, at its id, has been held that long)",
        _ => "not known",
    };

    private static string LowerBoundWhy(HorizonHolder holder) => holder.Kind switch
    {
        HealthHorizonHolderKind.Prepared or HealthHorizonHolderKind.OtherDatabasePrepared => "it was prepared that long ago",
        HealthHorizonHolderKind.SlotXmin or HealthHorizonHolderKind.SlotCatalogXmin => "the slot has been inactive that long",
        _ => holder.Bound switch
        {
            "statement" => "its statement has run that long",
            "idle" => "it has been idle in its transaction that long",
            "transaction" => "its transaction has been open that long",
            _ => "by Postgres' own timestamps",
        },
    };

    private static string UnknownWhy(HorizonHolder holder) => holder.Kind switch
    {
        HealthHorizonHolderKind.Session or HealthHorizonHolderKind.OtherDatabaseSession when holder.OwnXidAge is null =>
            (holder.Hidden
                ? "this role may not read when its snapshot was taken"
                : "Postgres records no time for a snapshot held outside a running statement or an open transaction")
            + ", and a snapshot carries no transaction id of its own for the transaction-id counter's samples to date",
        HealthHorizonHolderKind.Session or HealthHorizonHolderKind.OtherDatabaseSession =>
            "this role may not read when its transaction began, and no sample of the transaction-id counter is past its "
            + "transaction id yet",
        HealthHorizonHolderKind.Prepared or HealthHorizonHolderKind.OtherDatabasePrepared =>
            "no sample of the transaction-id counter is past its transaction id yet",
        _ => "Postgres records no time for it - not when it last moved its horizon - and the id it holds is the oldest "
            + "transaction that was running when its snapshot was taken, not its own, so the transaction-id counter's samples cannot "
            + "date it",
    };

    private static string Title(HorizonHolder holder, TimeSpan? age, HorizonTimeKind kind, bool holdsTables)
    {
        string xids = Wraparound.Count(holder.XidAge) + " transactions";

        string subject = holder.Kind switch
        {
            HealthHorizonHolderKind.Session when holder.HoldsDaemonLock => "The store's own HotCold daemon leader",
            HealthHorizonHolderKind.Session => "A session",
            HealthHorizonHolderKind.OtherDatabaseSession => "A session in another database",
            HealthHorizonHolderKind.Prepared => "A prepared transaction",
            HealthHorizonHolderKind.OtherDatabasePrepared => "A prepared transaction in another database",
            HealthHorizonHolderKind.Standby => "A standby's hot_standby_feedback",
            HealthHorizonHolderKind.SlotXmin or HealthHorizonHolderKind.SlotCatalogXmin when holder.OtherDatabase =>
                "A logical slot of another database on this server",
            HealthHorizonHolderKind.SlotXmin => holder.Lost ? "A lost replication slot" : "A replication slot",
            _ => holder.Lost ? "A lost logical replication slot" : "A logical replication slot",
        };

        string what = holdsTables ? "vacuum" : "vacuum of the system catalogs, and every database's freeze,";

        return age is { } held && kind != HorizonTimeKind.Unknown
            ? subject + " has held back " + what + " for " + TimeText(holder, held, kind) + " (" + xids + ")"
            : subject + " holds back " + what + " by " + xids;
    }

    private static string? Who(HorizonHolder holder) => holder.Kind switch
    {
        HealthHorizonHolderKind.Session when holder.ProcessId is { } pid => SessionEvidence.Describe(
            pid,
            holder.User,
            holder.Application,
            holder.Hidden ? "(not readable)" : holder.Client,
            holder.BackendStart,
            holder.Hidden
                ? "another role's session; its state and timings are withheld from this role"
                : (holder.State ?? "state unknown") + (holder.HasTransactionId ? ", has written" : string.Empty)),
        HealthHorizonHolderKind.Prepared => "gid '" + holder.Name + "', owner " + (holder.User ?? "(unknown)"),
        HealthHorizonHolderKind.Standby => "pid " + (holder.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?")
            + ", role " + (holder.User ?? "(not readable)")
            + (string.IsNullOrEmpty(holder.Application) ? string.Empty : ", application '" + holder.Application + "'")
            + ", from " + (holder.Client ?? "(not readable)")
            + (holder.State is { } state ? ", " + state : string.Empty),
        HealthHorizonHolderKind.SlotXmin or HealthHorizonHolderKind.SlotCatalogXmin when !holder.OtherDatabase =>
            "slot '" + holder.Name + "'"
            + (holder.Plugin is { } plugin ? ", plugin " + plugin : string.Empty)
            + (holder.SlotDatabase is { } database ? ", database " + database : string.Empty)
            + (holder.Active ? ", active" : ", inactive"),
        _ => null,
    };

    private static string DeadRows(HorizonFacts facts, TimeSpan? age, HorizonTimeKind kind)
    {
        long dead = facts.Tables.Sum(static x => x.Row.DeadRows ?? 0);
        int tables = facts.Tables.Count(static x => x.Row.DeadRows > 0);

        string text = Wraparound.Number(dead) + " in " + SessionText.Count(tables, "table");

        if (age is { } held && kind == HorizonTimeKind.AtLeast)
        {
            DateTimeOffset since = facts.ReadAt - held;
            List<TableFacts> vacuumed = [.. facts.Tables.Where(x => x.Row.DeadRows > 0 && x.LastVacuumOfAnyKind > since)];
            long left = vacuumed.Sum(static x => x.Row.DeadRows ?? 0);

            text += "; " + Wraparound.Number(left) + " of them in " + SessionText.Count(vacuumed.Count, "table")
                + " vacuumed since it began - rows that vacuum could not remove (T07)";
        }

        return text;
    }

    private static string Counts(IReadOnlyList<HorizonHolder> holders, IReadOnlyList<HorizonHolder> behind)
    {
        List<string> found = [];
        List<string> waiting = [];

        void Add(string one, string many, IEnumerable<HorizonHolder> of, Func<HorizonHolder, bool> kind)
        {
            long count = of.Sum(static x => x.Total);
            long back = behind.LongCount(kind);

            if (count - back > 0)
            {
                found.Add(SessionText.Number(count - back) + " " + (count - back == 1 ? one : many));
            }

            if (back > 0)
            {
                waiting.Add(SessionText.Number(back) + " " + (back == 1 ? one : many));
            }
        }

        // A read returns the oldest of each kind and how many there were; Total says how many. What is behind the holder
        // named is said apart, because it holds nothing that would stay once that one ends.
        Add("session", "sessions", holders.Where(static x => x.Kind == HealthHorizonHolderKind.Session).Take(1),
            static x => x.Kind == HealthHorizonHolderKind.Session);
        Add("prepared transaction", "prepared transactions", holders.Where(static x => x.Kind == HealthHorizonHolderKind.Prepared).Take(1),
            static x => x.Kind == HealthHorizonHolderKind.Prepared);
        Add("replication slot", "replication slots", holders
            .Where(static x => x.Kind is HealthHorizonHolderKind.SlotXmin or HealthHorizonHolderKind.SlotCatalogXmin)
            .DistinctBy(static x => x.SlotOrdinal), static _ => false);
        Add("standby", "standbys", holders.Where(static x => x.Kind == HealthHorizonHolderKind.Standby).Take(1),
            static x => x.Kind == HealthHorizonHolderKind.Standby);

        // Another database's sessions and prepared transactions are an age and nothing else - not even how many there are.

        return (found.Count, waiting.Count) switch
        {
            (0, 0) => "none",
            (_, 0) => string.Join(", ", found),
            (0, _) => string.Join(", ", waiting) + ", behind it at the same point",
            _ => string.Join(", ", found) + "; and " + string.Join(", ", waiting) + " behind it, at the same point",
        };
    }

    private static string? HiddenCaveat(SessionVisibility visibility, bool sampled)
    {
        if (visibility.CanReadAllStats || visibility.HiddenSessions == 0)
        {
            return null;
        }

        return "The studio's Postgres role is not a member of pg_read_all_stats, so it cannot see when "
            + SessionText.Count(visibility.HiddenSessions, "session") + " of other roles in this database began their transactions. "
            + "Postgres still shows it their transaction ids, so they are found and judged by how far back they hold the horizon. The "
            + "age in time of one that has written is " + (sampled
                ? "dated, as a lower bound, from the advisor's samples of the transaction-id counter past its own transaction id"
                : "not known until the advisor has sampled the transaction-id counter")
            + "; of one that holds only a snapshot it is not known, because the snapshot's id is somebody else's.";
    }

    private static string WhyItMatters(HorizonHolder holder, bool leader, HorizonFacts facts, bool holdsTables, bool anythingBehind)
    {
        string why =
            "Vacuum removes a dead row, and freezes a live one, only once no transaction could still see it - and the oldest "
            + "transaction id or snapshot anything on the server holds is that line, the horizon. While it stays put, every "
            + "update and delete in the database leaves a row vacuum cannot remove: tables and indexes grow, scans slow down, and "
            + "freezing stops at it, so the database's transaction-id age climbs towards wraparound (T12).";

        why += holder.Kind switch
        {
            HealthHorizonHolderKind.Session when leader && facts.LeaderLockInTransaction =>
                " This session is the store's own HotCold daemon leader. With Events.UseAdvisoryLockTransaction on - the default at "
                + "Marten 9.39 and 9.40 - it holds its leadership lock in a transaction it keeps open for as long as it leads. With "
                + "Events.UseMonitoredAdvisoryLock on (the default) its lock monitor runs a statement in that transaction about once a "
                + "minute - pg_sleep(60) at Marten 9.39; at 9.50.2 DistributedLock's keepalive, the session idle in between, or "
                + "pg_sleep(60) again, the session active, where the connection string sets Npgsql's Keepalive or Multiplexing; at "
                + "read committed, Postgres' default "
                + "isolation, each of those statements takes a fresh snapshot, so it holds the horizon back by about a minute at a "
                + "time - measured at Marten 9.39 and 9.50.2. Under default_transaction_isolation = 'repeatable read' "
                + "or 'serializable' the snapshot is the transaction's own, and the monitor does not move it. With the monitor off "
                + "nothing refreshes it, and the leader holds the horizon for as long as it leads - measured at Marten 9.39.",
            HealthHorizonHolderKind.Session when leader =>
                " This session holds the store's own daemon leadership lock.",
            HealthHorizonHolderKind.Session or HealthHorizonHolderKind.Prepared when holder.HasTransactionId && facts.HasAsyncShards =>
                " It has written, so it also holds a transaction id Marten's high-water detection waits behind: async projections stop "
                + "at it (D01). A long rebuild batch and a transaction left open are the same mechanism - Explain on the projections "
                + "screen names the transaction.",
            HealthHorizonHolderKind.Session =>
                " A session that keeps a transaction open - a report, a rebuild batch, a debugger at a breakpoint, code that began a "
                + "transaction and never ended it - holds it for as long as it stays open.",
            HealthHorizonHolderKind.Prepared =>
                " A prepared transaction outlives its session and a restart of the server, and nothing ends it but COMMIT PREPARED or "
                + "ROLLBACK PREPARED from its transaction manager - or a DBA (C07).",
            HealthHorizonHolderKind.SlotXmin or HealthHorizonHolderKind.SlotCatalogXmin when holder.Lost =>
                " Postgres reports this slot lost: the WAL it needs is gone, so no consumer can ever use it again - but until a "
                + "checkpoint invalidates it, it still holds the horizon. A lost slot that still holds the horizon: drop it "
                + "(pg_drop_replication_slot, a DBA's; R01 reports the slot lost).",
            HealthHorizonHolderKind.SlotXmin or HealthHorizonHolderKind.SlotCatalogXmin when holder.OtherDatabase =>
                " It is a logical slot of another database on this server - possibly another tenant's - so the studio names "
                + "nothing about it; its hold still counts, because every database's freeze counts the shared catalogs. Its "
                + "database's own studio, or a DBA, can see which slot it is (R01 watches the WAL slots retain).",
            HealthHorizonHolderKind.SlotXmin =>
                " A physical slot's xmin is its standby's hot_standby_feedback, kept even while the standby is away; a DBA ends it by "
                + "bringing the standby back or dropping a slot nothing uses any more (R01 watches the WAL it retains).",
            HealthHorizonHolderKind.SlotCatalogXmin =>
                " A logical slot keeps the system catalogs as they were when its consumer last confirmed - Debezium, a CDC pipeline on "
                + "mt_events, a subscription - and an abandoned one holds them, and every database's freeze, for good. Restart its "
                + "consumer, or have a DBA drop a slot nothing uses (R01 watches the WAL it retains).",
            HealthHorizonHolderKind.Standby =>
                " A standby with hot_standby_feedback = on sends its oldest snapshot here, so a long query on the standby holds back "
                + "vacuum on the primary.",
            _ when holdsTables =>
                " It is in another database, but every snapshot a session here takes while it runs counts it as running - so the "
                + "snapshots behind it hold back this database's tables as well, for as long as it stays open. Its database's own "
                + "studio, or a DBA, can see which session it is.",
            _ =>
                " It is in another database, so it holds back only the shared system catalogs - but every database's freeze counts "
                + "them, so a session there stops the forced freeze here too.",
        };

        if (anythingBehind)
        {
            why += " The holders behind it keep their snapshots at the very id it owns, and move on the moment it ends: it is the "
                + "one to end.";
        }

        return why;
    }

    private static HealthRemedy? RemedyFor(HorizonHolder holder, bool leader, bool leaderLockInTransaction, StudioText text)
    {
        if (holder.Kind != HealthHorizonHolderKind.Session)
        {
            // A prepared transaction, a slot, a standby or another database's session is a DBA's, in words: the studio ends
            // nothing, and none of them is a StoreOptions line.
            return null;
        }

        if (leader && leaderLockInTransaction)
        {
            return new HealthRemedy(
                "opts.Events.UseAdvisoryLockTransaction = false;",
                "Takes HotCold leadership with a session-scoped advisory lock, so the leader keeps no transaction open; it is the "
                + "default from Marten 9.41. A host that must keep the transaction lock - behind a transaction-mode pooler, where a "
                + "session lock is not safe - keeps Events.UseMonitoredAdvisoryLock = true instead: its lock monitor runs a statement "
                + "in the leader's transaction about once a minute (pg_sleep(60) at Marten 9.39; at 9.50.2 DistributedLock's keepalive, "
                + "the session idle in its transaction in between - or, where the connection string sets Npgsql's Keepalive or "
                + "Multiplexing, pg_sleep(60) as at 9.39, the session active), and at read committed - Postgres' default - each takes a fresh "
                + "snapshot, which bounds the leader's hold to about a minute (measured at Marten 9.39 and 9.50.2). Under "
                + "default_transaction_isolation = 'repeatable read' or 'serializable' the snapshot is the transaction's own and the "
                + "monitor does not move it; only the session lock helps.");
        }

        if (leader)
        {
            return null;
        }

        return new HealthRemedy(
            text.ShortTransactionCode,
            "The studio never ends another session. Find it from the application name on Database › Sessions; a server-wide "
            + "idle_in_transaction_session_timeout or transaction_timeout (PostgreSQL 17) is the DBA's backstop.");
    }

    private static HealthLink? LinkFor(HorizonHolder holder, bool hasAsyncShards) => holder.Kind switch
    {
        HealthHorizonHolderKind.Session or HealthHorizonHolderKind.Prepared when (holder.HasTransactionId && hasAsyncShards) => ExplainLink,
        HealthHorizonHolderKind.Session or HealthHorizonHolderKind.Prepared => SessionEvidence.SessionsLink,
        _ => null,
    };

    /// <summary>A holder that is behind nothing, as judged: its age with what is behind it, its reach, its severity.</summary>
    private sealed record Judged(
        HorizonHolder Holder,
        TimeSpan? Age,
        HorizonTimeKind TimeKind,
        bool HoldsTables,
        IReadOnlyList<HorizonHolder> Behind,
        HealthSeverity? Severity);
}
