using System.Globalization;

namespace PostgresStudio.Services.Health;

/// <summary>One number a check contributes to a sweep's sample.</summary>
/// <remarks>
/// <para>
/// <b>A series is read at or below its writer's gate.</b> The history is the one place a number crosses from
/// one check to another - a rate one check measures becomes another's evidence - so it is also the one place a
/// number could cross a gate: a session count by role that an <see cref="HealthGate.Activity" /> check samples
/// must not become the evidence of a check every visitor sees. So every part carries the gate of the check that
/// wrote it (the sweep stamps it: <see cref="Gate" /> is raised to the writer's gate and never lowered), and an
/// evaluator is handed only the series written at or below its own check's gate
/// (<see cref="HealthHistoryView.VisibleAt" />). A check may name a stricter gate for one part than its own -
/// a count at <see cref="HealthGate.None" /> beside a per-role breakdown at <see cref="HealthGate.Activity" /> -
/// and never a looser one.
/// </para>
/// <para>
/// Two checks writing one series in the same sweep is a bug, and fails the second.
/// </para>
/// </remarks>
/// <param name="Series">
/// The series it belongs to - <c>daemon.highest-sequence</c>, <c>daemon.shard-position:OrderSummary:All</c>.
/// Lower case, the area first, a qualifier after a colon.
/// </param>
/// <param name="Value">
/// The value. A <see cref="double" /> holds every counter and sequence Postgres hands out exactly up to
/// 2^53, which is well past anything a sample will see.
/// </param>
internal sealed record HealthSamplePart(string Series, double Value)
{
    /// <summary>
    /// Who may read this part through the history: the stricter of what the check says here and the check's own
    /// gate, which the sweep stamps.
    /// </summary>
    public HealthGate Gate { get; init; } = HealthGate.None;

    /// <summary>
    /// Whether this part is a sighting rather than a number: the history keeps when it first saw the series - beyond its
    /// ring of samples, in the database's first-seen map (<see cref="HealthHistoryView.FirstSeen" />) - and nothing of its
    /// <see cref="Value" />, which is not put in the sample at all (W5-V1).
    /// </summary>
    /// <remarks>
    /// For T14's holder identities, which date a hold by the first sweep that saw it unchanged: a series name that carries an
    /// identity is a hash, never the identity, so nothing it is made of - a process id, a slot's name - is in the history.
    /// </remarks>
    public bool Remembered { get; init; }
}

/// <summary>What one sweep recorded: when, and a bag of numbers keyed by series.</summary>
/// <remarks>
/// A bag rather than a type with a property per number, so that a later check adds its numbers by naming a
/// series, without editing a type every other check shares.
/// </remarks>
/// <param name="At">When the sweep read the facts.</param>
/// <param name="Values">Every check's parts, by series.</param>
internal sealed record HealthSample(DateTimeOffset At, IReadOnlyDictionary<string, double> Values)
{
    private static readonly IReadOnlyDictionary<string, HealthGate> NoGates = new Dictionary<string, HealthGate>();

    /// <summary>
    /// The gate each series was written at (<see cref="HealthSamplePart.Gate" />). A series with no entry is read
    /// as <see cref="HealthGate.Activity" />: a number whose writer is unknown is shown to nobody short of the
    /// strictest gate.
    /// </summary>
    public IReadOnlyDictionary<string, HealthGate> Gates { get; init; } = NoGates;

    /// <summary>The gate <paramref name="series" /> was written at.</summary>
    public HealthGate GateOf(string series) => Gates.TryGetValue(series, out HealthGate gate) ? gate : HealthGate.Activity;
}

/// <summary>One series over the samples that have it, oldest first.</summary>
/// <param name="Series">The series.</param>
/// <param name="Points">Every sample's value for it, oldest first.</param>
internal sealed record HealthTrend(string Series, IReadOnlyList<(DateTimeOffset At, double Value)> Points)
{
    /// <summary>Whether a rate exists: two samples, at two different times.</summary>
    public bool HasRate => Points.Count >= 2 && Points[^1].At > Points[0].At;

    /// <summary>How long this series has been watched: the first sample to the last.</summary>
    public TimeSpan Watched => Points.Count == 0 ? TimeSpan.Zero : Points[^1].At - Points[0].At;

    /// <summary>The latest value, or <see langword="null" /> when there is none.</summary>
    public double? Latest => Points.Count == 0 ? null : Points[^1].Value;

    /// <summary>
    /// The change per second between the first sample and the last, or <see langword="null" /> before
    /// <see cref="HasRate" />. A rate over the whole window, not the last step: one slow sweep must not make
    /// a steady drain look stalled.
    /// </summary>
    public double? PerSecond => HasRate
        ? (Points[^1].Value - Points[0].Value) / Watched.TotalSeconds
        : null;
}

/// <summary>A database's history as a check's evaluator is handed it: immutable, oldest first.</summary>
/// <param name="Samples">The samples, oldest first.</param>
/// <param name="WatchingSince">
/// When the first regular sweep of the database ran in this process - which is when watching began, whether
/// or not any check has contributed a number yet.
/// </param>
internal sealed record HealthHistoryView(IReadOnlyList<HealthSample> Samples, DateTimeOffset? WatchingSince = null)
{
    private static readonly IReadOnlyDictionary<string, HealthSighting> NoSightings = new Dictionary<string, HealthSighting>();

    /// <summary>A history that has seen nothing.</summary>
    public static HealthHistoryView Empty { get; } = new([]);

    /// <summary>When watching began, or <see langword="null" /> before the first sweep.</summary>
    public DateTimeOffset? Since => WatchingSince ?? (Samples.Count == 0 ? null : Samples[0].At);

    /// <summary>
    /// The database's first-seen map: for each remembered series (<see cref="HealthSamplePart.Remembered" />), when a sweep
    /// first saw it and the gate it was written at. It outlives the ring, and is dropped with the database's history.
    /// </summary>
    public IReadOnlyDictionary<string, HealthSighting> Sightings { get; init; } = NoSightings;

    /// <summary>When a remembered series was first seen, or <see langword="null" /> when it never was (or is above this view's gate).</summary>
    public DateTimeOffset? FirstSeen(string series)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(series);
        return Sightings.TryGetValue(series, out HealthSighting? sighting) ? sighting.First : null;
    }

    /// <summary>
    /// The history as a check at <paramref name="gate" /> may read it: only the series written at or below that
    /// gate, and only the samples left with any number in them. What the sweeper hands each evaluator.
    /// </summary>
    public HealthHistoryView VisibleAt(HealthGate gate)
    {
        if (gate >= HealthGate.Activity)
        {
            return this;
        }

        List<HealthSample> visible = [];

        foreach (HealthSample sample in Samples)
        {
            Dictionary<string, double> values = new(StringComparer.Ordinal);
            Dictionary<string, HealthGate> gates = new(StringComparer.Ordinal);

            foreach ((string series, double value) in sample.Values)
            {
                HealthGate written = sample.GateOf(series);
                if (written <= gate)
                {
                    values[series] = value;
                    gates[series] = written;
                }
            }

            if (values.Count > 0)
            {
                visible.Add(new HealthSample(sample.At, values) { Gates = gates });
            }
        }

        return new HealthHistoryView(visible, Since)
        {
            Sightings = Sightings.Where(x => x.Value.Gate <= gate).ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// One series over every sample that has it. A sample from a sweep that did not run the check simply has
    /// no value for it, and is skipped rather than read as zero.
    /// </summary>
    public HealthTrend Trend(string series)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(series);

        List<(DateTimeOffset At, double Value)> points = [];
        foreach (HealthSample sample in Samples)
        {
            if (sample.Values.TryGetValue(series, out double value))
            {
                points.Add((sample.At, value));
            }
        }

        return new HealthTrend(series, points);
    }

    /// <summary>
    /// What the page says about how long the advisor has been watching: "watching for 4 min", or, before a
    /// second sample makes a rate possible, that it has only just started.
    /// </summary>
    public string Describe(DateTimeOffset now)
    {
        if (Since is not { } since)
        {
            return "Not watched yet: rates appear once two checks, one interval apart, have run.";
        }

        TimeSpan watched = now - since;
        string span = watched < TimeSpan.FromMinutes(1)
            ? "under a minute"
            : ((int) watched.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";

        return Samples.Count switch
        {
            0 => "Watching for " + span + ". No check here measures a rate yet.",
            1 => "Watching for " + span + " (one sample): rates appear after the next check.",
            _ => "Watching for " + span + " (" + Samples.Count.ToString(CultureInfo.InvariantCulture)
                + " samples). History is kept in memory, only while a page is open, and is gone after a restart.",
        };
    }
}

/// <summary>
/// What the advisor has seen of each database, so that a later sweep can compute a rate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Filled only by a sweep</b>, and a sweep runs only when a page asks (plan §3.5): a ring of
/// <see cref="Capacity" /> samples per store and database - an hour at the default interval - kept in memory
/// and lost on a restart, deliberately. There is no timer here and nothing that outlives the process's
/// interest in it.
/// </para>
/// <para>
/// <b>What it costs.</b> A sample holds one number per series its checks write: a few per check, one per async
/// shard or projection for the daemon checks and up to four more per shard D05 saw failed or paused, and one per
/// replication slot on the server for R01 - every slot,
/// another database's included, at most <c>HealthServerQueries.MaxSlots</c> (200), most WAL first. So a database's
/// ring is <see cref="Capacity" /> times that many numbers and their series names: tens of kilobytes for an
/// ordinary store, up to a few megabytes for one with hundreds of shards or slots. A store with a database per
/// tenant keeps a ring per database it has been swept for, each carrying the same server's slots, so R01's part is
/// bounded by <c>MaxSlots</c> × the databases swept × <see cref="Capacity" /> - the one place this grows with the
/// deployment. A database whose statement checks or Statements page were run has a second ring (<see cref="RingOf" />), of
/// at most <c>HealthStatementQueries.MaxTexts</c> (120, or 220 on Aurora) statements' seven or eight series a sample (W5-Q1),
/// written only when a visitor through the activity gate runs the statement checks or opens the Statements page - at most
/// one sample per statement sweep, every five minutes - and with each series name shared between samples
/// (<c>StatementReads.SeriesName</c>): about 25 KB a sample, so up to about 1.5 MB a database for a full ring at its worst,
/// never a number per statement the extension holds. Beside it, the statement checks' <c>StatementTextCache</c> holds up to
/// 256 texts of at most 4,096 characters per database whose statement checks have run, for the life of the process - about
/// 2 MB a database at its worst - so it, too, grows with the number of databases swept, and with nothing else. Since 0.5
/// that number is bounded: the 64 databases swept or read most recently keep their rings and their texts, and the least
/// recently used past them loses both (<see cref="HealthDatabaseBound" />, API design §3.5).
/// </para>
/// <para>
/// Singleton. Two circuits watching one database are watching one history: the sweep is shared, so its
/// sample is too.
/// </para>
/// <para>
/// <b>Regular sweeps only, and read per gate.</b> A deeper or statement sweep adds nothing to a database's history: a
/// number only a gated tier-3 sweep could read must not become readable to an ungated evaluator through
/// <see cref="HealthEvaluation.History" />, which would be a channel across the gates. A statement sweep keeps a
/// history of its own instead - a second ring per database (<see cref="RingOf" />), which only statement sweeps
/// append to and read, so the statement checks can judge a window rather than the totals since a reset, and every
/// check that reads it is behind the activity gate (the registry refuses a tier-3 check that is not). Within a
/// regular sweep the same holds check by check: every series carries its writer's gate, and an evaluator reads only the series at
/// or below its own (<see cref="HealthSamplePart" />). And a sample is kept only if it is newer than the newest one
/// already held and at least <c>minimumSpacing</c> after it - the sweeper passes half of
/// <c>EffectiveHealthInterval</c> - so a page and the opt-in health check sweeping the same database do not
/// fill the ring twice as fast and halve the hour it covers. A sample with no numbers in it marks only when
/// watching began.
/// </para>
/// </remarks>
internal sealed class HealthHistory : IPerDatabaseState
{
    /// <summary>How many samples a database keeps.</summary>
    public const int Capacity = 60;

    /// <param name="bound">
    /// The process's bound on databases with state (API design §3.5), which forgets a database's rings - its first-seen map
    /// with them - once it is the least recently used past the bound; <see langword="null" /> for no bound, in a test.
    /// </param>
    public HealthHistory(HealthDatabaseBound? bound = null)
    {
        bound?.Register(this);
    }

    /// <summary>
    /// How many remembered series a database's first-seen map keeps (W5-V1): T14's holder identities, at most twenty
    /// sessions, twenty standbys and two hundred slots a sweep. Past it the series seen longest ago goes first, and a new one
    /// is dated from a later sweep - a bound that comes out shorter, never longer.
    /// </summary>
    public const int SightingCapacity = 512;

    private readonly Dictionary<string, Ring> rings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    /// <summary>How many databases have a history, for a test and for nothing else.</summary>
    internal int DatabaseCount
    {
        get
        {
            lock (gate)
            {
                return rings.Count;
            }
        }
    }

    /// <summary>
    /// Adds a regular sweep's sample, dropping the oldest past <see cref="Capacity" />, unless it is not newer
    /// than the newest one held by at least <paramref name="minimumSpacing" /> or carries no numbers.
    /// </summary>
    /// <param name="storeKey">The store.</param>
    /// <param name="databaseId">The database's ring (<see cref="RingOf" />).</param>
    /// <param name="sample">The sweep's numbers.</param>
    /// <param name="minimumSpacing">How long after the newest sample held this one must be to be kept.</param>
    /// <param name="remembered">
    /// The sweep's sightings (<see cref="HealthSamplePart.Remembered" />), with the gate each was written at: recorded in the
    /// first-seen map whether or not the sample is kept, because a sighting is an observation, not a number.
    /// </param>
    /// <returns>Whether the sample was kept.</returns>
    public bool Append(
        string storeKey,
        string databaseId,
        HealthSample sample,
        TimeSpan minimumSpacing = default,
        IReadOnlyDictionary<string, HealthGate>? remembered = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);
        ArgumentNullException.ThrowIfNull(sample);

        string key = KeyFor(storeKey, databaseId);

        lock (gate)
        {
            if (!rings.TryGetValue(key, out Ring? ring))
            {
                ring = new Ring(sample.At);
                rings[key] = ring;
            }

            if (remembered is { Count: > 0 })
            {
                ring.See(remembered, sample.At);
            }

            if (sample.Values.Count == 0)
            {
                return false;
            }

            if (ring.Samples.Count > 0)
            {
                DateTimeOffset newest = ring.Samples.Last().At;
                if (sample.At <= newest || sample.At - newest < minimumSpacing)
                {
                    return false;
                }
            }

            ring.Samples.Enqueue(sample);

            while (ring.Samples.Count > Capacity)
            {
                ring.Samples.Dequeue();
            }

            ring.Forget();

            return true;
        }
    }

    /// <summary>The database's history, oldest first, as an immutable copy.</summary>
    public HealthHistoryView Read(string storeKey, string databaseId)
    {
        lock (gate)
        {
            return rings.TryGetValue(KeyFor(storeKey, databaseId), out Ring? ring)
                ? new HealthHistoryView([.. ring.Samples], ring.WatchingSince)
                {
                    Sightings = ring.Sightings.ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal),
                }
                : HealthHistoryView.Empty;
        }
    }

    /// <summary>
    /// Which ring a sweep of <paramref name="kind" /> appends to and reads: the database's own, or - for a statement
    /// sweep - the statement history beside it, which nothing else reads.
    /// </summary>
    public static string RingOf(string databaseId, HealthSweepKind kind)
    {
        ArgumentNullException.ThrowIfNull(databaseId);
        return kind == HealthSweepKind.Statements ? databaseId + StatementsRingSuffix : databaseId;
    }

    /// <summary>What a statement history's ring is keyed by after the database's identity.</summary>
    internal const string StatementsRingSuffix = "|statements";

    /// <inheritdoc />
    /// <remarks>Both of the database's rings - its own and its statement history - and the first-seen map that is part of each.</remarks>
    public void Forget(string storeKey, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        lock (gate)
        {
            rings.Remove(KeyFor(storeKey, RingOf(databaseId, HealthSweepKind.Regular)));
            rings.Remove(KeyFor(storeKey, RingOf(databaseId, HealthSweepKind.Statements)));
        }
    }

    private static string KeyFor(string storeKey, string databaseId) => storeKey + "|" + databaseId;

    /// <summary>One database: when watching began, its samples, and its first-seen map - dropped together.</summary>
    private sealed class Ring(DateTimeOffset watchingSince)
    {
        public DateTimeOffset WatchingSince { get; } = watchingSince;

        public Queue<HealthSample> Samples { get; } = new(Capacity);

        public Dictionary<string, HealthSighting> Sightings { get; } = new(StringComparer.Ordinal);

        /// <summary>Records one sweep's sightings: a new series is first seen now, a known one is seen again.</summary>
        public void See(IReadOnlyDictionary<string, HealthGate> remembered, DateTimeOffset at)
        {
            foreach ((string series, HealthGate gate) in remembered)
            {
                if (Sightings.TryGetValue(series, out HealthSighting? known))
                {
                    Sightings[series] = known with { Last = at > known.Last ? at : known.Last, Gate = gate > known.Gate ? gate : known.Gate };
                    continue;
                }

                if (Sightings.Count >= SightingCapacity)
                {
                    // The one seen longest ago goes first; if every one was seen in this very sweep, the new one waits.
                    KeyValuePair<string, HealthSighting> stalest = Sightings.MinBy(static x => x.Value.Last);
                    if (stalest.Value.Last >= at)
                    {
                        continue;
                    }

                    Sightings.Remove(stalest.Key);
                }

                Sightings[series] = new HealthSighting(at, at, gate);
            }
        }

        /// <summary>
        /// Drops every sighting not seen since the oldest sample the ring still holds: what no sweep in the window saw has
        /// ended - an identity carries its own transaction id, so it does not come back.
        /// </summary>
        public void Forget()
        {
            if (Samples.Count < Capacity)
            {
                return;
            }

            DateTimeOffset oldest = Samples.Peek().At;

            foreach (string gone in Sightings.Where(x => x.Value.Last < oldest).Select(static x => x.Key).ToList())
            {
                Sightings.Remove(gone);
            }
        }
    }
}

/// <summary>When the history first and last saw a remembered series, and the gate it was written at.</summary>
/// <param name="First">The first sweep that saw it.</param>
/// <param name="Last">The latest.</param>
/// <param name="Gate">Who may read it through the history: its writer's gate.</param>
internal sealed record HealthSighting(DateTimeOffset First, DateTimeOffset Last, HealthGate Gate);
