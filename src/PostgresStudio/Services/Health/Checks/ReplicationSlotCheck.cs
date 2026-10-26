using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What R01 reads.</summary>
/// <param name="Slots">Every replication slot on the server, and the settings that bound them.</param>
/// <param name="Server">The server's version and platform, for the remedy's wording.</param>
internal sealed record ReplicationSlotFacts(ReplicationSlotSnapshot Slots, PostgresServerFacts Server);

/// <summary>
/// R01: a replication slot holds back WAL with nobody reading it, is about to lose the WAL it needs, or has been lost.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Marten store has slots at all.</b> Change data capture - Debezium, a logical subscription, a cloud
/// provider's replication service - is how the events in <c>mt_events</c> and the documents in <c>mt_doc_*</c> leave the
/// database, and every one of those consumers reads through a slot. A slot keeps every WAL file its consumer has not
/// confirmed, so a consumer that stops - a connector paused for a deployment and never restarted, a decommissioned
/// replica whose slot nobody dropped - makes the slot grow with every write the store makes until the WAL volume fills,
/// and then the server stops accepting writes. That is the most common way a healthy Postgres goes down for no reason
/// the application can see.
/// </para>
/// <para>
/// <b>Severity</b> (plan R1, candidate 1):
/// </para>
/// <list type="bullet">
/// <item><b>Critical</b>: <c>wal_status = 'unreserved'</c> - Postgres has stopped keeping WAL for the slot and removes
/// it at the next checkpoint - or, with <c>max_slot_wal_keep_size</c> set, an inactive slot holding
/// <see cref="CapCriticalShare" /> of it or more on two consecutive samples, or for <see cref="CapWindow" /> by
/// PostgreSQL 17's <c>inactive_since</c> (<see cref="CapSustained" />), so that a consumer reconnecting between two sweeps
/// does not flash a Critical and its toast.</item>
/// <item><b>Warning</b>: an inactive slot holding <see cref="RetainedWarningBytes" /> or more, and doing so across
/// <see cref="RetainedWindow" /> of the advisor's samples - or, from PostgreSQL 17, inactive for that long by its own
/// <c>inactive_since</c>, which is Postgres's clock and needs no watching. Below that, the status row says it is
/// watching.</item>
/// <item><b>Info</b>: a slot Postgres has invalidated (<c>wal_status = 'lost'</c>, an <c>invalidation_reason</c> from
/// PostgreSQL 17, a recovery conflict from 16). It holds nothing, and its consumer has silently stopped receiving
/// changes.</item>
/// <item><b>Never</b> an active slot, unless it is unreserved: a connected consumer that is merely behind is
/// replication lag, not a forgotten slot. No slots is silence.</item>
/// </list>
/// <para>
/// <b>One finding per kind, a server-wide total.</b> Slots are the server's - a physical slot belongs to no database
/// and a logical one for another database still holds the WAL this one writes - so, like C03, the numbers are the
/// whole server's, and every store database on the server reports them. A finding's subject is its kind
/// (<c>near-limit</c>, <c>inactive</c>, <c>lost</c>) and never a slot: a subject is part of the mute key every visitor
/// of the finding is handed. Counts, sizes and the settings are the database's.
/// </para>
/// <para>
/// <b>A slot is named only where this database may name it</b> (H10 review, B1). A physical slot's name is the server's,
/// and so is evidence behind <see cref="HealthGate.Activity" />; so are a logical slot's name, plugin and database when the
/// slot is this database's. A logical slot of <em>another</em> database is described as
/// <see cref="ReplicationSlot.AnotherDatabase" /> and nothing more, whatever gate the visitor passes: in a
/// database-per-tenant store its name and database are another tenant's, and the activity gate is asked for this
/// database only - the rule C06 keeps for another database's session and S03 for another database's failures.
/// </para>
/// <para>
/// <b>Its own history, per slot.</b> Every slot read - another database's too, at most
/// <see cref="HealthServerQueries.MaxSlots" />, most WAL first - writes <c>replication.slot-idle-retained:&lt;slot&gt;</c>:
/// the WAL it held while inactive, zero while a consumer was connected or once it was lost, so a slot that was read
/// connected in between breaks the run rather than being skipped. Another database's slot is sampled here because nothing
/// else may ever sweep its database - a Debezium slot in a database the store does not have - and before PostgreSQL 17
/// the samples are the only clock it has. The series is the database's (the check's gate); its qualifier is a slot name,
/// which history keeps in memory and never renders, exports or logs (H10 review, fix 2).
/// </para>
/// <para>
/// <b>The horizon a slot pins</b> is evidence here (its <c>xmin</c> and <c>catalog_xmin</c> ages) and the finding of
/// T14, which reads the same snapshot through <c>server.replication-slots</c>.
/// </para>
/// </remarks>
[HealthCheck("R01", HealthArea.Replication, HealthCostTier.Catalog, "Replication slot retaining WAL")]
internal sealed class ReplicationSlotCheck : HealthCheck<ReplicationSlotFacts>
{
    /// <summary>An inactive slot holding this much WAL, for <see cref="RetainedWindow" />, is a Warning: 1 GiB.</summary>
    public const long RetainedWarningBytes = 1L << 30;

    /// <summary>With <c>max_slot_wal_keep_size</c> set, an inactive slot holding this share of it is Critical.</summary>
    public const double CapCriticalShare = 0.8;

    /// <summary>How many slots a finding names in its gated evidence; the count says how many there are.</summary>
    public const int SlotsNamed = 5;

    /// <summary>The series prefix; the slot's name follows.</summary>
    public const string SeriesPrefix = "replication.slot-idle-retained:";

    /// <summary>How long an inactive slot must have held <see cref="RetainedWarningBytes" /> for a Warning.</summary>
    public static readonly TimeSpan RetainedWindow = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public override async Task<ReplicationSlotFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ReplicationSlotSnapshot slots = await ReplicationSlotSnapshot.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return new ReplicationSlotFacts(slots, server);
    }

    /// <summary>
    /// How long a slot at <see cref="CapCriticalShare" /> of <c>max_slot_wal_keep_size</c> must have been so before it is
    /// Critical, by PostgreSQL 17's <c>inactive_since</c>; without it, two consecutive samples are the window.
    /// </summary>
    public static readonly TimeSpan CapWindow = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Every slot, another database's included.</b> A forgotten slot in a database the store does not have - a
    /// Debezium slot in <c>billing</c> beside the store's <c>app</c> - is swept by nobody but this database's sweep, and
    /// before PostgreSQL 17 there is no <c>inactive_since</c> to fall back on: without its samples it would never leave
    /// "watching", and with an unlimited <c>max_slot_wal_keep_size</c> nothing would fire until the disk was full.
    /// </para>
    /// <para>
    /// <b>The qualifier is internal.</b> A series name is never rendered, exported or logged: the sweep's rule against a
    /// series written twice logs the series' family and a count, never its qualifier, so another database's slot name
    /// never leaves this process's memory. B1 is about what is shown, and nothing here is.
    /// </para>
    /// <para>
    /// <b>Bounded</b>: at most <see cref="HealthServerQueries.MaxSlots" /> slots, most WAL retained first - the read's
    /// own order and limit, said again here so that a reader of this method does not have to look.
    /// </para>
    /// </remarks>
    public override IEnumerable<HealthSamplePart> Sample(ReplicationSlotFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        IEnumerable<ReplicationSlot> sampled = facts.Slots.Slots
            .DistinctBy(static x => x.Name, StringComparer.Ordinal)
            .OrderByDescending(static x => x.RetainedBytes ?? 0)
            .Take(HealthServerQueries.MaxSlots);

        foreach (ReplicationSlot slot in sampled)
        {
            yield return new HealthSamplePart(SeriesPrefix + slot.Name, IdleRetained(slot));
        }
    }

    /// <summary>What a slot's sample is: the WAL it holds while inactive, and zero otherwise.</summary>
    internal static double IdleRetained(ReplicationSlot slot) =>
        slot.Active || slot.IsLost ? 0 : slot.RetainedBytes ?? 0;

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ReplicationSlotFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ReplicationSlotSnapshot snapshot = facts.Slots;

        if (snapshot.Slots.Count == 0)
        {
            return HealthVerdict.Quiet("This server has no replication slots.");
        }

        List<(ReplicationSlot Slot, string Why)> nearLimit = [];
        List<(ReplicationSlot Slot, TimeSpan Held)> inactive = [];
        List<ReplicationSlot> lost = [];
        List<(ReplicationSlot Slot, TimeSpan Held)> watching = [];
        List<ReplicationSlot> nearCapOnce = [];

        foreach (ReplicationSlot slot in snapshot.Slots)
        {
            long retained = slot.RetainedBytes ?? 0;
            bool nearCap = !slot.Active && snapshot.HasSlotCap && retained >= CapCriticalShare * snapshot.MaxSlotWalKeepBytes!.Value;

            if (slot.IsLost)
            {
                lost.Add(slot);
            }
            else if (slot.IsUnreserved)
            {
                // Postgres's own verdict, and the next checkpoint acts on it: no window.
                nearLimit.Add((slot, "wal_status 'unreserved'"));
            }
            else if (nearCap && CapSustained(slot, snapshot, evaluation))
            {
                nearLimit.Add((slot, WalText.Percent((double) retained / snapshot.MaxSlotWalKeepBytes!.Value) + " of max_slot_wal_keep_size"));
            }
            else if (!slot.Active && retained >= RetainedWarningBytes)
            {
                TimeSpan held = HeldFor(slot, snapshot.ReadAt, evaluation);
                (held >= RetainedWindow ? inactive : watching).Add((slot, held));
            }
            else if (nearCap)
            {
                nearCapOnce.Add(slot);
            }
        }

        List<HealthFinding> findings = [];

        if (nearLimit.Count > 0)
        {
            findings.Add(NearLimit(facts, nearLimit, evaluation.Text));
        }

        if (inactive.Count > 0)
        {
            findings.Add(Inactive(facts, inactive, evaluation.Text));
        }

        if (lost.Count > 0)
        {
            findings.Add(Lost(facts, lost, evaluation.Text));
        }

        List<string> notes = [];

        if (nearCapOnce.Count > 0)
        {
            notes.Add(
                SessionText.Count(nearCapOnce.Count, "inactive replication slot") + " " + (nearCapOnce.Count == 1 ? "is" : "are")
                + " at " + WalText.Percent(CapCriticalShare) + " of max_slot_wal_keep_size or more for the first time; the advisor raises it as "
                + "Critical if the next sample - or, from PostgreSQL 17, a minute without a consumer - says so too, so that a consumer "
                + "reconnecting does not flash a Critical.");
        }

        if (watching.Count > 0)
        {
            long most = watching.Max(static x => x.Slot.RetainedBytes ?? 0);
            notes.Add(
                SessionText.Count(watching.Count, "inactive replication slot") + " " + (watching.Count == 1 ? "holds " : "hold ")
                + "at least " + WalText.Bytes(RetainedWarningBytes) + " of WAL (the most " + WalText.Bytes(most) + "); the advisor raises it once that has lasted "
                + SessionText.Duration(RetainedWindow) + " of its samples, and has seen " + SessionText.Duration(watching.Max(static x => x.Held)) + " so far.");
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings, notes.Count > 0 ? string.Join(" ", notes) : null);
        }

        if (notes.Count > 0)
        {
            return HealthVerdict.Quiet(string.Join(" ", notes));
        }

        long largest = snapshot.Slots.Max(static x => x.RetainedBytes ?? 0);
        return HealthVerdict.Quiet(
            SessionText.Count(snapshot.Total, "replication slot") + " on this server, none of them forgotten or near a limit: the most WAL one holds is "
            + WalText.Bytes(largest) + ".");
    }

    /// <summary>
    /// How long an inactive slot has held at least <see cref="RetainedWarningBytes" />, as far as the advisor can tell:
    /// the longer of its own unbroken run of samples at or above that, and - from PostgreSQL 17 - Postgres's
    /// <c>inactive_since</c>, aged to the evaluation's time.
    /// </summary>
    internal static TimeSpan HeldFor(ReplicationSlot slot, DateTimeOffset readAt, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(evaluation);

        DateTimeOffset now = evaluation.Now > readAt ? evaluation.Now : readAt;
        DateTimeOffset runStart = now;

        IReadOnlyList<(DateTimeOffset At, double Value)> points = evaluation.History.Trend(SeriesPrefix + slot.Name).Points;

        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (points[i].At > now)
            {
                continue;
            }

            if (points[i].Value < RetainedWarningBytes)
            {
                break;
            }

            runStart = points[i].At;
        }

        TimeSpan sampled = now - runStart;
        TimeSpan postgres = slot.InactiveAtRead is { } inactive ? SnapshotAge.At(inactive, readAt, evaluation) : TimeSpan.Zero;

        return sampled > postgres ? sampled : postgres;
    }

    /// <summary>
    /// Whether an inactive slot at <see cref="CapCriticalShare" /> of <c>max_slot_wal_keep_size</c> has been there long
    /// enough to be Critical: PostgreSQL 17's <c>inactive_since</c> at least <see cref="CapWindow" /> ago, aged to the
    /// evaluation's time, or the sample before this read at the line too. A consumer that reconnects between two sweeps
    /// is then never a Critical (and a toast) for one sweep and quiet the next.
    /// </summary>
    internal static bool CapSustained(ReplicationSlot slot, ReplicationSlotSnapshot snapshot, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (slot.InactiveAtRead is { } inactive && SnapshotAge.At(inactive, snapshot.ReadAt, evaluation) >= CapWindow)
        {
            return true;
        }

        if (!snapshot.HasSlotCap)
        {
            return false;
        }

        double line = CapCriticalShare * snapshot.MaxSlotWalKeepBytes!.Value;
        IReadOnlyList<(DateTimeOffset At, double Value)> points = evaluation.History.Trend(SeriesPrefix + slot.Name).Points;

        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (points[i].At < snapshot.ReadAt)
            {
                return points[i].Value >= line;
            }
        }

        return false;
    }

    private static HealthFinding NearLimit(ReplicationSlotFacts facts, List<(ReplicationSlot Slot, string Why)> slots, StudioText text)
    {
        ReplicationSlotSnapshot snapshot = facts.Slots;
        bool unreserved = slots.Any(static x => x.Slot.IsUnreserved);
        List<(ReplicationSlot Slot, string Why)> ordered = [.. slots.OrderByDescending(static x => x.Slot.RetainedBytes ?? 0)];

        List<HealthEvidence> evidence = Totals(snapshot, [.. ordered.Select(static x => x.Slot)], "Slots past the line");

        if (ordered.Min(static x => x.Slot.SafeWalSize) is { } safe)
        {
            evidence.Add(new HealthEvidence("WAL that may still be written before the first is lost", WalText.Bytes(Math.Max(0, safe))));
        }

        evidence.AddRange(Named(ordered.Select(static x => (x.Slot, x.Why))));
        evidence.Add(new HealthEvidence("Platform", facts.Server.Platform.Name));

        return new HealthFinding
        {
            Code = "R01",
            Severity = HealthSeverity.Critical,
            Subject = "near-limit",
            Title = unreserved
                ? (ordered.Count == 1
                    ? "A replication slot is about to lose WAL it still needs"
                    : SessionText.Number(ordered.Count) + " replication slots are about to lose WAL they still need")
                : (ordered.Count == 1
                    ? "An inactive replication slot holds " + ordered[0].Why
                    : SessionText.Number(ordered.Count) + " inactive replication slots are at " + WalText.Percent(CapCriticalShare) + " of max_slot_wal_keep_size or more"),
            WhyItMatters =
                "max_slot_wal_keep_size caps the WAL a slot may hold. Past it, Postgres stops keeping WAL for the slot (wal_status "
                + "'unreserved') and removes the files at the next checkpoint; the slot is then lost, and whatever consumed it - a change "
                + "data capture connector reading Marten's events, a logical subscription, a standby - stops receiving changes and has to "
                + "be rebuilt from a fresh copy of the data. A consumer that is restarted before that catches up and keeps its place.",
            Evidence = evidence,
            Caveat = Caveat(snapshot, text),
            Remedy = new HealthRemedy(
                "# " + text.ServerSettingComment + " (PostgreSQL 13+). Raise it only if the WAL volume can hold the difference:\n"
                + "max_slot_wal_keep_size = 100GB",
                "Restart the consumer that reads the slot now, before the next checkpoint, so that it catches up and Postgres can "
                + "recycle the WAL it confirms. If nothing will read the slot again, drop it (pg_drop_replication_slot) and the WAL "
                + "goes at the next checkpoint. Raising max_slot_wal_keep_size, in " + facts.Server.Platform.WhereSettingsChange + ", "
                + "buys time only where the disk has room for it."),
        };
    }

    private static HealthFinding Inactive(ReplicationSlotFacts facts, List<(ReplicationSlot Slot, TimeSpan Held)> slots, StudioText text)
    {
        ReplicationSlotSnapshot snapshot = facts.Slots;
        List<(ReplicationSlot Slot, TimeSpan Held)> ordered = [.. slots.OrderByDescending(static x => x.Slot.RetainedBytes ?? 0)];
        long total = ordered.Sum(static x => x.Slot.RetainedBytes ?? 0);

        List<HealthEvidence> evidence = Totals(snapshot, [.. ordered.Select(static x => x.Slot)], "Inactive slots holding " + WalText.Bytes(RetainedWarningBytes) + " or more");
        evidence.Add(new HealthEvidence("Held for at least", SessionText.Duration(ordered.Min(static x => x.Held))));
        evidence.AddRange(Named(ordered.Select(static x => (x.Slot, "inactive for " + SessionText.Duration(x.Held) + " or more"))));
        evidence.Add(new HealthEvidence("Platform", facts.Server.Platform.Name));

        return new HealthFinding
        {
            Code = "R01",
            Severity = HealthSeverity.Warning,
            Subject = "inactive",
            Title = ordered.Count == 1
                ? "An inactive replication slot has held " + WalText.Bytes(total) + " of WAL for " + SessionText.Duration(RetainedWindow) + " or more"
                : SessionText.Number(ordered.Count) + " inactive replication slots hold " + WalText.Bytes(total) + " of WAL between them, for "
                  + SessionText.Duration(RetainedWindow) + " or more",
            WhyItMatters =
                "A replication slot keeps every WAL file its consumer has not confirmed, and with no consumer connected it confirms "
                + "nothing: it grows with every write the server takes - every Marten append, every document upsert - until the WAL volume "
                + "fills and Postgres stops accepting writes altogether"
                + (snapshot.HasSlotCap
                    ? ", or until it passes max_slot_wal_keep_size and is invalidated, which loses its consumer's place. "
                    : ", because max_slot_wal_keep_size is unlimited on this server. ")
                + "A logical slot also holds back vacuum of the system catalogs, and one with a standby's feedback holds back vacuum "
                + "everywhere (T14). It is nearly always a connector that was stopped and never restarted, or a replica that went away "
                + "without its slot.",
            Evidence = evidence,
            Caveat = Caveat(snapshot, text)
                + " How long it has been inactive is measured across the advisor's own samples, which exist only while somebody watches"
                + (snapshot.ServerVersion >= 170000 ? ", and by the slot's own inactive_since." : "; from PostgreSQL 17 Postgres records it itself (inactive_since)."),
            Remedy = new HealthRemedy(
                "# " + text.ServerSettingComment + " (PostgreSQL 13+): no slot may hold more than this.\n"
                + "max_slot_wal_keep_size = 50GB",
                "First the consumer: restart whatever reads the slot - a change data capture connector, a logical subscription, a "
                + "standby - so that it catches up and Postgres can recycle the WAL; or, if nothing will read it again, drop the slot "
                + "(pg_drop_replication_slot). Then cap what any slot may hold, in " + facts.Server.Platform.WhereSettingsChange + ": a "
                + "slot that passes max_slot_wal_keep_size is invalidated instead of filling the disk, and its consumer has to be "
                + "re-initialised - so size it above the longest outage a consumer should survive."),
        };
    }

    private static HealthFinding Lost(ReplicationSlotFacts facts, List<ReplicationSlot> slots, StudioText text)
    {
        ReplicationSlotSnapshot snapshot = facts.Slots;

        List<HealthEvidence> evidence = Totals(snapshot, slots, "Invalidated slots", retained: false);
        evidence.AddRange(Named(slots.Select(static x => (x, "invalidated" + (x.InvalidationReason is { Length: > 0 } reason ? " (" + reason + ")" : string.Empty)))));

        return new HealthFinding
        {
            Code = "R01",
            Severity = HealthSeverity.Info,
            Subject = "lost",
            Title = slots.Count == 1
                ? "A replication slot has been invalidated and can no longer be used"
                : SessionText.Number(slots.Count) + " replication slots have been invalidated and can no longer be used",
            WhyItMatters =
                "Postgres invalidated the slot - it removed WAL the slot still needed (max_slot_wal_keep_size), or rows a standby's "
                + "logical slot needed - so its consumer has stopped receiving changes, whether or not it says so. The slot itself holds "
                + "nothing now. The consumer has to be re-initialised from a fresh copy of the data on a new slot; then the old slot "
                + "can be dropped (pg_drop_replication_slot).",
            Evidence = evidence,
            Caveat = Caveat(snapshot, text)
                + (snapshot.ServerVersion >= 170000 ? string.Empty : " Before PostgreSQL 17 Postgres does not record why a slot was invalidated."),
        };
    }

    /// <summary>The database-wide evidence every R01 finding starts with.</summary>
    private static List<HealthEvidence> Totals(ReplicationSlotSnapshot snapshot, List<ReplicationSlot> slots, string label, bool retained = true)
    {
        List<HealthEvidence> evidence =
        [
            new(label, SessionText.Number(slots.Count) + " of " + SessionText.Count(snapshot.Total, "slot") + " on this server"),
        ];

        if (retained)
        {
            evidence.Add(new HealthEvidence("WAL they hold", WalText.Bytes(slots.Sum(static x => x.RetainedBytes ?? 0))));
        }

        evidence.Add(new HealthEvidence("max_slot_wal_keep_size", WalText.Setting(snapshot.MaxSlotWalKeepBytes)));
        evidence.Add(new HealthEvidence("wal_keep_size", WalText.Setting(snapshot.WalKeepBytes)));

        if (snapshot.InRecovery)
        {
            evidence.Add(new HealthEvidence("Measured against", "the standby's last replayed WAL position: this server is in recovery"));
        }

        return evidence;
    }

    /// <summary>The slots themselves, behind the activity gate: name, kind, plugin, database, and what each holds.</summary>
    private static IEnumerable<HealthEvidence> Named(IEnumerable<(ReplicationSlot Slot, string What)> slots)
    {
        int index = 0;

        foreach ((ReplicationSlot slot, string what) in slots.Take(SlotsNamed))
        {
            List<string> parts = [slot.Describe(), slot.Active ? "active" : "inactive", what];

            if (slot.RetainedBytes is { } held)
            {
                parts.Add("holding " + WalText.Bytes(held));
            }

            if (slot.WalStatus is { Length: > 0 } status)
            {
                parts.Add("wal_status " + status);
            }

            if (slot.XminAge is { } xmin)
            {
                parts.Add("xmin age " + SessionText.Number(xmin));
            }

            if (slot.CatalogXminAge is { } catalogXmin)
            {
                parts.Add("catalog_xmin age " + SessionText.Number(catalogXmin));
            }

            yield return new HealthEvidence(SessionEvidence.Label("Slot", index, "most WAL"), string.Join(", ", parts), HealthGate.Activity);
            index++;
        }
    }

    private static string Caveat(ReplicationSlotSnapshot snapshot, StudioText text) =>
        "Read in pg_replication_slots at " + snapshot.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
        + "; what a slot holds is the server's WAL position less the slot's restart_lsn. Slots are the server's, not this "
        + "database's, so every store database on this server counts the same ones. A physical slot's name, and a logical slot's "
        + "name, plugin and database when it is this database's, are shown only with "
        + text.OptionName("Capabilities.ViewDatabaseActivity") + "; a logical slot of another database is never named here.";
}
