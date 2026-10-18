using System.Globalization;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What the wraparound read found: the database-wide facts, and the store's tables' oldest ages.</summary>
/// <param name="Database">The settings, the database's and the server's oldest ages, the counters, the other tables counted.</param>
/// <param name="Tables">The store's tables that exist, a hidden document type's never among them.</param>
/// <param name="HealthInterval">
/// The cadence the history is sampled at (<c>EffectiveHealthInterval</c>), which sizes how long a forecast can be held
/// (<see cref="Wraparound.ForecastPersistFor" />). A minute when a test does not say.
/// </param>
internal sealed record WraparoundFacts(HealthWraparoundDatabase Database, IReadOnlyList<HealthWraparoundTable> Tables, TimeSpan? HealthInterval = null)
{
    /// <summary>The interval, a minute when unknown.</summary>
    public TimeSpan Interval => HealthInterval ?? TimeSpan.FromMinutes(1);
}

/// <summary>
/// The database-wide wraparound read T12, T13 and T14 share, the lines they judge against, and their wording.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read once per sweep</b> (<see cref="HealthCheckContext.Shared{T}" />) under <see cref="Key" />, with the store's
/// tables from <see cref="MartenTables.SetAsync" /> - the configuration's, never <c>AllObjects()</c>.
/// </para>
/// <para>
/// <b>The lines are Postgres' own.</b> Transaction ids and multixact ids are both 32-bit and compared modulo 2^31, so
/// both wrap round at about 2.1 billion from the oldest unfrozen one; Postgres refuses to hand out new ones a margin
/// before that - three million on PostgreSQL 14 and later, one million on 13 - which is the "write-refusal line" a
/// forecast counts down to. Autovacuum forces a freeze of a table at <c>autovacuum_freeze_max_age</c>
/// (<c>autovacuum_multixact_freeze_max_age</c> for multixacts), which is the "forced-vacuum line". Both are cited
/// from the PostgreSQL documentation's "Routine Vacuuming" chapter (versions 13, 14, 16 and 17 read on 2026-10-17).
/// </para>
/// </remarks>
internal static class Wraparound
{
    /// <summary>The memo key of the shared read.</summary>
    internal const string Key = "tables.wraparound";

    /// <summary>The series T12 samples the transaction-id counter into, for the rate a forecast is drawn from.</summary>
    internal const string XidCounterSeries = "tables.xid-counter";

    /// <summary>The series T13 samples the multixact counter into.</summary>
    internal const string MultixactCounterSeries = "tables.multixact-counter";

    /// <summary>The series T12 writes in every sweep with the database read, for <see cref="TableHistory.HeldSince" />.</summary>
    internal const string XidSweptSeries = "tables.xid-forced.swept";

    /// <summary>The series T12 writes, as 1, while the oldest age is past <c>autovacuum_freeze_max_age</c>.</summary>
    internal const string XidPastForcedSeries = "tables.xid-forced";

    /// <summary>The series T13 writes in every sweep.</summary>
    internal const string MultixactSweptSeries = "tables.multixact-forced.swept";

    /// <summary>The series T13 writes, as 1, while the oldest age is past <c>autovacuum_multixact_freeze_max_age</c>.</summary>
    internal const string MultixactPastForcedSeries = "tables.multixact-forced";

    /// <summary>The longest an age must stay past its forced line before a forecast alone is a finding.</summary>
    public static readonly TimeSpan ForecastPersistCeiling = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an age must have stayed past its forced line, over the samples, before a forecast alone is a finding (H9
    /// review, follow-up 2): <see cref="ForecastPersistCeiling" />, or less where the history cannot hold that long. Routine
    /// anti-wraparound autovacuum crosses the line and comes back - <c>template0</c> and <c>postgres</c>, which
    /// <c>ServerXidAge</c> counts, do it every <c>autovacuum_freeze_max_age</c> transactions - and a forecast that spoke on the
    /// first sample past it would flicker with every cycle. An age-based finding never waits.
    /// </summary>
    /// <remarks>
    /// The ring holds <see cref="HealthHistory.Capacity" /> samples, kept at least half an interval apart, so the shortest
    /// span it is sure to hold is <c>(Capacity - 1) x interval / 2</c> - 29.5 minutes at the default minute, 14.75 at 30
    /// seconds, 7.4 at 15 (H9 re-review, fix 4). A fixed 30 minutes would never be reached at a short interval.
    /// </remarks>
    public static TimeSpan ForecastPersistFor(TimeSpan healthInterval)
    {
        TimeSpan held = healthInterval * (HealthHistory.Capacity - 1) / 2;
        return held < ForecastPersistCeiling ? held : ForecastPersistCeiling;
    }

    /// <summary>"29 min 30 s", "14 min 45 s", "30 min": the persistence a note states, to the second it is.</summary>
    public static string PersistText(TimeSpan span)
    {
        int minutes = (int) span.TotalMinutes;
        int seconds = span.Seconds;

        return minutes.ToString(CultureInfo.InvariantCulture) + " min"
            + (seconds > 0 ? " " + seconds.ToString(CultureInfo.InvariantCulture) + " s" : string.Empty);
    }

    /// <summary>Where both counters wrap round, as an age: <c>2^31</c>.</summary>
    public const long WraparoundAge = 2_147_483_648;

    /// <summary>The share of the way from the forced line to wraparound at which an age is a Warning (H4's, kept).</summary>
    public const double WarningShare = 0.4;

    /// <summary>The share of the way from the forced line to wraparound at which an age is Critical (H4's, kept).</summary>
    public const double CriticalShare = 2.0 / 3;

    /// <summary>
    /// The age at which Postgres refuses new ids: the wrap limit (<c>oldest + 2^31 - 1</c>) less its stop margin -
    /// three million on PostgreSQL 14 and later, one million on 13 (the documentation's "Routine Vacuuming", 13 and 14).
    /// The documentation gives no multixact margin for 13, so 13's transaction-id one is used for both.
    /// </summary>
    public static long RefusalAge(int serverVersion) => WraparoundAge - 1 - (serverVersion >= 140_000 ? 3_000_000 : 1_000_000);

    /// <summary>
    /// A failsafe age as <c>VACUUM</c> applies it: the setting, raised silently to 105% of the forced line where it is
    /// below it (the documentation of <c>vacuum_failsafe_age</c> and <c>vacuum_multixact_failsafe_age</c>), or
    /// <see langword="null" /> before PostgreSQL 14, which has neither.
    /// </summary>
    public static long? EffectiveFailsafe(long? setting, long forcedLine) =>
        setting is { } value ? Math.Max(value, (long) Math.Ceiling(forcedLine * 1.05)) : null;

    /// <summary>The age past which a finding is a Warning, given the forced line.</summary>
    public static long WarningAge(long forcedLine) => Line(forcedLine, WarningShare);

    /// <summary>The age past which a finding is Critical, given the forced line.</summary>
    public static long CriticalAge(long forcedLine) => Line(forcedLine, CriticalShare);

    private static long Line(long forcedLine, double share)
    {
        long forced = Math.Clamp(forcedLine, 0, WraparoundAge);
        return forced + (long) ((WraparoundAge - forced) * share);
    }

    /// <summary>The sweep's one wraparound read, shared by T12, T13 and T14.</summary>
    public static async Task<WraparoundFacts> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenTableSet set = await MartenTables.SetAsync(context).ConfigureAwait(false);
        return await context.Shared(Key, token => ReadAsync(context, set, token)).ConfigureAwait(false);
    }

    private static async Task<WraparoundFacts> ReadAsync(HealthCheckContext context, MartenTableSet set, CancellationToken cancellationToken)
    {
        IReadOnlyList<(HealthWraparoundDatabase Database, HealthWraparoundTable? Table)> rows = await context.Sql
            .QueryAsync(HealthHorizonQueries.Wraparound(set.Targets), HealthHorizonQueries.ReadWraparound, cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            // pg_database has no row for current_database(): a database being dropped under the sweep.
            throw new HealthCheckUnavailableException("Postgres has no pg_database row for this database at the moment, so its age was not read.");
        }

        return new WraparoundFacts(
            rows[0].Database,
            [.. rows.Where(static x => x.Table is not null).Select(static x => x.Table!)],
            context.StudioOptions.EffectiveHealthInterval);
    }

    /// <summary>"1.2 billion", "340 million", "12,345".</summary>
    public static string Count(long value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " billion",
        >= 10_000_000 => (value / 1_000_000.0).ToString("0", CultureInfo.InvariantCulture) + " million",
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + " million",
        _ => value.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>A whole number, grouped.</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>When the transaction-id counter passed a value, as the advisor's own samples of it date it.</summary>
/// <remarks>
/// <para>
/// <b>A bound on an id's age, never an estimate.</b> The first sample of the counter's latest unbroken run that is already
/// past an id says the id was handed out by that sample's time - so the id is at least as old as the time since. Nothing is
/// interpolated and no rate is assumed, so a burst of transaction ids early in the window and a quiet spell after (H9
/// review, follow-up 1) only moves which sample is first past it.
/// </para>
/// <para>
/// <b>It dates a hold only for the id's owner</b> (H9 re-review, blocking 1). A session holds its own <c>backend_xid</c>,
/// and a prepared transaction its xid, from the moment the id is handed out, so for them the id's age is the hold's. A
/// snapshot's <c>xmin</c> - a session's <c>backend_xmin</c>, a replication slot's, a standby's feedback - is not its
/// holder's at all: it is the oldest transaction running anywhere when the snapshot was taken, an id somebody else may
/// have been handed hours before a forty-second-old snapshot saw it running. Dating that would name the snapshot for the
/// owner's age, so T14 never does: <see cref="HorizonHeldCheck.AgeOf" /> passes only an owned id here.
/// </para>
/// <para>
/// <b>Gaps only make it smaller.</b> Only the sample after the crossing is used, so a stretch with no samples - a page closed
/// for an hour, a longer <c>HealthInterval</c> - can only move that sample later and the bound shorter, never longer. A
/// counter that went backwards (a restore, a failover) starts the run again, as the forecast's does.
/// </para>
/// </remarks>
internal static class CounterHistory
{
    /// <summary>
    /// The time of the first sample of <paramref name="counter" />'s latest unbroken run that is past
    /// <paramref name="value" /> - by which the counter had handed <paramref name="value" /> out - or
    /// <see langword="null" /> when no sample is past it yet.
    /// </summary>
    public static DateTimeOffset? PassedBy(HealthTrend counter, double value)
    {
        ArgumentNullException.ThrowIfNull(counter);

        IReadOnlyList<(DateTimeOffset At, double Value)> points = counter.Points;
        if (points.Count == 0)
        {
            return null;
        }

        int start = points.Count - 1;
        while (start > 0 && points[start - 1].Value <= points[start].Value)
        {
            start--;
        }

        for (int i = start; i < points.Count; i++)
        {
            if (points[i].Value > value)
            {
                return points[i].At;
            }
        }

        return null;
    }
}

/// <summary>
/// How long, at the rate one of the counters rose over the latest unbroken run of samples, until an age reaches a line.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never claims more than the window supports.</b> The rate is the counter's change over the run of samples the
/// in-memory history holds - its last 60 samples, however far apart they are (an hour at the default interval, longer at a
/// longer one or with gaps), from the latest reset of the counter: a restore or a failover that moves it backwards starts
/// the run again - and every sentence built from it says "at the rate of the last N minutes", with N that run's real
/// length. Below two samples there is no forecast at all; a counter that did not move gives none either;
/// and a line more than <see cref="Horizon" /> away is not forecast - the forecast says nothing rather than a number of
/// months a window of minutes cannot support.
/// </para>
/// <para>
/// <b>Linear, and only past the forced line a warning.</b> The forecast assumes nothing freezes in between. Before an age
/// reaches the forced-vacuum line that assumption is plainly false - autovacuum is about to freeze - so the time to the
/// forced line is a status note and nothing more. Past it, autovacuum has been asked and has not finished: the time to the
/// write-refusal line is then the time left if it never does.
/// </para>
/// </remarks>
/// <param name="PerSecond">How fast the counter rose.</param>
/// <param name="Window">Over how long.</param>
internal sealed record HorizonForecast(double PerSecond, TimeSpan Window)
{
    /// <summary>The furthest a forecast looks: beyond it, the window cannot say.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(90);

    /// <summary>A forecast this close to the write-refusal line is Critical.</summary>
    public static readonly TimeSpan CriticalWithin = TimeSpan.FromDays(7);

    /// <summary>
    /// The forecast a counter's samples support, or <see langword="null" /> below two samples of an unbroken run, or when
    /// the counter did not move.
    /// </summary>
    public static HorizonForecast? From(HealthTrend counter)
    {
        ArgumentNullException.ThrowIfNull(counter);

        if (TableHistory.Delta(counter) is not { } delta || delta.Span <= TimeSpan.Zero || delta.Change <= 0)
        {
            return null;
        }

        return new HorizonForecast(delta.Change / delta.Span.TotalSeconds, delta.Span);
    }

    /// <summary>
    /// How long until <paramref name="age" /> reaches <paramref name="line" /> at this rate: zero when it already has,
    /// <see langword="null" /> when that is beyond <see cref="Horizon" />.
    /// </summary>
    public TimeSpan? Until(long age, long line)
    {
        if (age >= line)
        {
            return TimeSpan.Zero;
        }

        double seconds = (line - age) / PerSecond;
        return seconds > Horizon.TotalSeconds ? null : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>"the last 12 minutes", "the last minute", "the last 45 seconds".</summary>
    public string WindowText => Window switch
    {
        _ when Window < TimeSpan.FromMinutes(1) => "the last " + ((int) Window.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " seconds",
        _ when Window < TimeSpan.FromMinutes(2) => "the last minute",
        _ => "the last " + ((int) Window.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes",
    };

    /// <summary>"at the rate of the last 12 minutes".</summary>
    public string AtTheRate => "at the rate of " + WindowText;

    /// <summary>The rate, as a person reads it: "about 1,200 an hour".</summary>
    public string RateText(string noun) =>
        "about " + Wraparound.Number((long) Math.Round(PerSecond * 3600)) + " " + noun + " an hour, " + AtTheRate;

    /// <summary>"about 7 days", "about 30 hours", "under an hour" - whole units, no false precision.</summary>
    public static string Span(TimeSpan span) => span switch
    {
        _ when span <= TimeSpan.Zero => "now",
        _ when span < TimeSpan.FromHours(1) => "under an hour",
        _ when span < TimeSpan.FromDays(2) => "about " + ((int) Math.Round(span.TotalHours)).ToString(CultureInfo.InvariantCulture) + " hours",
        _ => "about " + ((int) Math.Round(span.TotalDays)).ToString(CultureInfo.InvariantCulture) + " days",
    };

    /// <summary>The caveat sentence every forecast carries.</summary>
    public string Caveat(string counter) =>
        "The forecast is linear: " + counter + " rose " + RateText("ids") + ", and a burst or a quiet spell moves it; "
        + "it assumes no freeze completes in between, and looks no further than " + ((int) Horizon.TotalDays).ToString(CultureInfo.InvariantCulture)
        + " days.";
}
