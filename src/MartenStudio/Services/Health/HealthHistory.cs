using System.Globalization;

namespace MartenStudio.Services.Health;

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
    /// <summary>A history that has seen nothing.</summary>
    public static HealthHistoryView Empty { get; } = new([]);

    /// <summary>When watching began, or <see langword="null" /> before the first sweep.</summary>
    public DateTimeOffset? Since => WatchingSince ?? (Samples.Count == 0 ? null : Samples[0].At);

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

        return new HealthHistoryView(visible, Since);
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
/// <see cref="Capacity" /> samples per store and database - an hour at the default interval, a few
/// kilobytes - kept in memory and lost on a restart, deliberately. There is no timer here and nothing
/// that outlives the process's interest in it.
/// </para>
/// <para>
/// Singleton. Two circuits watching one database are watching one history: the sweep is shared, so its
/// sample is too.
/// </para>
/// <para>
/// <b>Regular sweeps only, and read per gate.</b> A deeper or statement sweep adds nothing here: a number only a
/// gated tier-3 sweep could read must not become readable to an ungated evaluator through
/// <see cref="HealthEvaluation.History" />, which would be a channel across the gates. Within a regular sweep the
/// same holds check by check: every series carries its writer's gate, and an evaluator reads only the series at
/// or below its own (<see cref="HealthSamplePart" />). And a sample is kept only if it is newer than the newest one
/// already held and at least <c>minimumSpacing</c> after it - the sweeper passes half of
/// <c>EffectiveHealthInterval</c> - so a page and the opt-in health check sweeping the same database do not
/// fill the ring twice as fast and halve the hour it covers. A sample with no numbers in it marks only when
/// watching began.
/// </para>
/// </remarks>
internal sealed class HealthHistory
{
    /// <summary>How many samples a database keeps.</summary>
    public const int Capacity = 60;

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
    /// <returns>Whether the sample was kept.</returns>
    public bool Append(string storeKey, string databaseId, HealthSample sample, TimeSpan minimumSpacing = default)
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
                : HealthHistoryView.Empty;
        }
    }

    private static string KeyFor(string storeKey, string databaseId) => storeKey + "|" + databaseId;

    /// <summary>One database: when watching began, and its samples.</summary>
    private sealed class Ring(DateTimeOffset watchingSince)
    {
        public DateTimeOffset WatchingSince { get; } = watchingSince;

        public Queue<HealthSample> Samples { get; } = new(Capacity);
    }
}
