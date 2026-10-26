using System.Diagnostics.Metrics;

namespace PostgresStudio.Services.Database;

/// <summary>
/// One of the two sets of names Npgsql has published its two observable pool instruments under. The kinds, the values
/// and what they mean are the same in both; only the names moved, in Npgsql 10, to the OpenTelemetry database client
/// conventions' current spelling.
/// </summary>
/// <remarks>
/// Measured on 2026-10-20 (0.5's PS-0 and NPG10) with a probe - a data source per pool, a <c>MeterListener</c> on the
/// meter <c>"Npgsql"</c> (version <c>0.1.0</c> throughout), <c>RecordObservableInstruments</c> - at Npgsql 8.0.3 and
/// 9.0.4 (<see cref="UpToNpgsql9" />) and at every 10.0.x on nuget.org that day, 10.0.0 to 10.0.3
/// (<see cref="FromNpgsql10" />), and read in Npgsql 10.0.3's <c>MetricsReporter</c>: both instruments are still
/// <c>ObservableUpDownCounter&lt;int&gt;</c>, the state values still <c>idle</c> and <c>used</c>, the ceiling still
/// reported only for a pooling data source. <c>NpgsqlPoolInstrumentsTests</c> pins the set the restored Npgsql
/// publishes, at the Npgsql floor and in the Npgsql pass.
/// </remarks>
/// <param name="Usage">Connections per pool and state.</param>
/// <param name="Max">Each pool's ceiling.</param>
/// <param name="PoolNameTag">The tag naming the pool.</param>
/// <param name="StateTag">The tag naming a usage measurement's state, <c>idle</c> or <c>used</c>.</param>
internal sealed record NpgsqlPoolInstruments(string Usage, string Max, string PoolNameTag, string StateTag)
{
    /// <summary>
    /// Npgsql 8.x and 9.x: <c>db.client.connections.usage</c> and <c>db.client.connections.max</c>, tagged
    /// <c>pool.name</c> and <c>state</c>.
    /// </summary>
    public static NpgsqlPoolInstruments UpToNpgsql9 { get; } = new(
        "db.client.connections.usage",
        "db.client.connections.max",
        "pool.name",
        "state");

    /// <summary>
    /// Npgsql 10.x: <c>db.client.connection.count</c> and <c>db.client.connection.max</c>, tagged
    /// <c>db.client.connection.pool.name</c> and <c>db.client.connection.state</c>.
    /// </summary>
    public static NpgsqlPoolInstruments FromNpgsql10 { get; } = new(
        "db.client.connection.count",
        "db.client.connection.max",
        "db.client.connection.pool.name",
        "db.client.connection.state");

    /// <summary>Both, the older first.</summary>
    public static IReadOnlyList<NpgsqlPoolInstruments> All { get; } = [UpToNpgsql9, FromNpgsql10];

    /// <summary>
    /// The set <paramref name="instrumentName" /> is one of the two instruments of, or <see langword="null" /> for
    /// any other instrument.
    /// </summary>
    public static NpgsqlPoolInstruments? Publishing(string instrumentName)
    {
        foreach (NpgsqlPoolInstruments instruments in All)
        {
            if (string.Equals(instrumentName, instruments.Usage, StringComparison.Ordinal)
                || string.Equals(instrumentName, instruments.Max, StringComparison.Ordinal))
            {
                return instruments;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="tag" /> names the pool, in either set.</summary>
    public static bool IsPoolNameTag(string tag) =>
        string.Equals(tag, UpToNpgsql9.PoolNameTag, StringComparison.Ordinal)
        || string.Equals(tag, FromNpgsql10.PoolNameTag, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="tag" /> names a usage measurement's state, in either set.</summary>
    public static bool IsStateTag(string tag) =>
        string.Equals(tag, UpToNpgsql9.StateTag, StringComparison.Ordinal)
        || string.Equals(tag, FromNpgsql10.StateTag, StringComparison.Ordinal);
}

/// <summary>One Npgsql connection pool in this process, as its observable instruments reported it at one moment.</summary>
/// <param name="PoolName">
/// Npgsql's pool name tag (<c>pool.name</c>, from Npgsql 10 <c>db.client.connection.pool.name</c>): the data
/// source's name, which is its connection string without the password unless the host named it. Compared, never
/// shown.
/// </param>
/// <param name="Idle">Connections open and idle in the pool.</param>
/// <param name="Used">Connections open and lent out.</param>
/// <param name="Max">The pool's ceiling (<c>Max Pool Size</c>), or <see langword="null" /> when it was not reported.</param>
/// <param name="Reporters">
/// How many data sources reported under this name. More than one is two pools with one connection string - the
/// host's own <c>AddNpgsqlDataSource(cs)</c> beside Marten's, say - whose numbers cannot be told apart.
/// </param>
/// <param name="Disagree">Whether pools reporting under this name reported different numbers.</param>
internal sealed record NpgsqlPoolReading(string PoolName, int Idle, int Used, int? Max, int Reporters = 1, bool Disagree = false)
{
    /// <summary>
    /// The instruments the numbers were read from: Npgsql 10's when the pool was reported under both sets, the older
    /// ones otherwise - which is what a reading made in a test, with no instruments behind it, says too.
    /// </summary>
    public NpgsqlPoolInstruments Instruments { get; init; } = NpgsqlPoolInstruments.UpToNpgsql9;

    /// <summary>
    /// Whether the numbers belong to no pool in particular: more than one pool reported under this name, and they
    /// did not all report the same. When every pool under the name says the same thing, whichever of them is
    /// Marten's says it too - so the reading stands, and the next reading that differs is ambiguous.
    /// </summary>
    public bool IsAmbiguous => Reporters > 1 && Disagree;

    /// <summary>The share of the ceiling lent out, or <see langword="null" /> without one.</summary>
    public double? UsedShare => Max is > 0 ? (double) Used / Max.Value : null;
}

/// <summary>
/// The in-process view of Npgsql's connection pools that C01 and Database › Sessions read: a
/// <see cref="MeterListener" /> on two of Npgsql's <em>observable</em> instruments, alive only while somebody
/// holds a <see cref="SessionPoolLease" />, and read only when asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly two instruments, both observable, under either of their names.</b> Npgsql 8 and 9 call them
/// <c>db.client.connections.usage</c> (tagged <c>state = idle | used</c>) and <c>db.client.connections.max</c>, each
/// tagged <c>pool.name</c> - read from Npgsql 9.0.4's <c>MetricsReporter</c> (meter <c>"Npgsql"</c>, version
/// <c>0.1.0</c>). Npgsql 10 renamed them <c>db.client.connection.count</c> (tagged
/// <c>db.client.connection.state</c>) and <c>db.client.connection.max</c>, each tagged
/// <c>db.client.connection.pool.name</c>, with the same kinds and values (<see cref="NpgsqlPoolInstruments" />). The
/// observer enables whichever of the four the process's Npgsql publishes and recognises a pool and a state by either
/// tag, so it never asks which Npgsql it runs beside: a host on Npgsql 10 - PostgresStudio's own, or a MartenStudio
/// host that brings it - is read as one on 9 is, where the old names alone read nothing at all, silently. Their
/// callbacks read two <c>volatile int</c>s per pool (<c>PoolingDataSource.Statistics</c>) and run only when this
/// listener calls <see cref="MeterListener.RecordObservableInstruments" />, on the caller's thread, delivering to this
/// listener alone. Nothing else is enabled, and that is the point:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No synchronous instrument, ever</b> (plan §9). <c>db.client.commands.executing</c>,
/// <c>…commands.duration</c>, <c>…bytes_read</c> and <c>…bytes_written</c> (from Npgsql 10
/// <c>db.client.operation.npgsql.executing</c>, <c>db.client.operation.duration</c> and the rest) are recorded on
/// every command the host runs; enabling one would add a callback to every command of every pool in the process.
/// That includes <c>db.client.connections.pending_requests</c>, which the plan named as observable and which
/// Npgsql 9.0.4 declares as a synchronous <c>UpDownCounter</c> - as Npgsql 10.0.3 still does, as
/// <c>db.client.connection.npgsql.pending_requests</c> - so C01 does not read it, and judges a pool saturated from
/// <c>used</c> reaching <c>max</c> instead.
/// </description></item>
/// <item><description>
/// <b>Not <c>db.client.commands.prepared_ratio</c> either</b> (from Npgsql 10
/// <c>db.client.operation.npgsql.prepared_ratio</c>), <b>though it is observable.</b> While it is enabled
/// Npgsql counts every command it starts (an <c>Interlocked.Increment</c> on the hot path), and its callback
/// <em>resets</em> those counters as it reads them - so reading it would hand the host's own exporter a ratio
/// with the studio's reads taken out of it. That would change what the host measures, which the studio never
/// does.
/// </description></item>
/// </list>
/// <para>
/// <b>Why observing the host's pools does not change the host.</b> A <see cref="MeterListener" /> is a passive
/// subscriber: the host's own <c>MeterProvider</c>, if it has one, keeps its own subscriptions, and enabling
/// an observable instrument here changes nothing Npgsql does until this listener asks for a reading - Npgsql
/// checks <c>Enabled</c> only on <c>prepared_ratio</c> and the command duration, neither of which is
/// enabled here (read at 9.0.4 and 10.0.3). A reading takes Npgsql's own <c>lock (Reporters)</c> for as long as it
/// takes to read two integers per pool, which is the lock it takes when a data source is created or disposed.
/// </para>
/// <para>
/// <b>One pool name, one tally, whichever names reported it.</b> A pool reported under both sets - an Npgsql that
/// published the old and the new names at once, as libraries moving between OpenTelemetry's conventions may - is
/// one name measured twice with the same numbers, and stands, exactly as two pools under one name that agree do;
/// two that differ are ambiguous, exactly as they are under one set.
/// </para>
/// <para>
/// <b>Alive only while leased.</b> The first <see cref="Lease" /> starts the listener and the last lease's
/// disposal stops it, so a studio that nobody has open listens to nothing. C01's gatherer holds a lease for
/// the length of a sweep, and the Sessions page holds one while it is open.
/// </para>
/// <para>
/// Singleton. The meter's name is a constructor argument only so that a test can point it at a meter of its
/// own and never at the process's real Npgsql pools.
/// </para>
/// </remarks>
internal sealed class SessionPoolObserver : IDisposable
{
    /// <summary>The meter Npgsql publishes its instruments on, at 8.x, 9.x and 10.x alike.</summary>
    public const string NpgsqlMeterName = "Npgsql";

    private readonly string meterName;
    private readonly Lock gate = new();
    private MeterListener? listener;
    private Dictionary<string, PoolTally>? reading;
    private int leases;
    private long measurements;
    private long starts;
    private bool disposed;

    /// <summary>An observer of Npgsql's own meter.</summary>
    public SessionPoolObserver() : this(NpgsqlMeterName)
    {
    }

    /// <summary>An observer of <paramref name="meterName" />, for a test.</summary>
    internal SessionPoolObserver(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        this.meterName = meterName;
    }

    /// <summary>Whether the listener is running - for a test and the page's footnote.</summary>
    internal bool IsListening
    {
        get
        {
            lock (gate)
            {
                return listener is not null;
            }
        }
    }

    /// <summary>How many leases are held.</summary>
    internal int Leases
    {
        get
        {
            lock (gate)
            {
                return leases;
            }
        }
    }

    /// <summary>
    /// Every measurement this observer has ever been handed. A test reads it around a burst of commands to
    /// prove that nothing arrives per command: only a reading produces measurements.
    /// </summary>
    internal long MeasurementsReceived => Interlocked.Read(ref measurements);

    /// <summary>How many times a listener has been started, for a test.</summary>
    internal long Starts => Interlocked.Read(ref starts);

    /// <summary>
    /// Whether this observer enables <paramref name="instrument" />: one of the two observable instruments, by either
    /// of their names (<see cref="NpgsqlPoolInstruments" />), on the meter it watches. Everything else - every
    /// synchronous instrument, and <c>prepared_ratio</c> - is left alone.
    /// </summary>
    internal static bool Observes(Instrument instrument, string meterName)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        return instrument.IsObservable
            && string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal)
            && NpgsqlPoolInstruments.Publishing(instrument.Name) is not null;
    }

    /// <summary>Takes a lease, starting the listener if this is the first.</summary>
    /// <exception cref="ObjectDisposedException">The observer has been disposed with the container.</exception>
    public SessionPoolLease Lease()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (leases++ == 0)
            {
                listener = Start();
            }
        }

        return new SessionPoolLease(this);
    }

    /// <summary>
    /// Every pool in the process as its instruments report it now, by name. Only while a lease is held.
    /// </summary>
    /// <exception cref="InvalidOperationException">No lease is held.</exception>
    internal IReadOnlyList<NpgsqlPoolReading> Read()
    {
        lock (gate)
        {
            if (listener is null)
            {
                throw new InvalidOperationException("The pool observer is read only while a lease is held.");
            }

            reading = new Dictionary<string, PoolTally>(StringComparer.Ordinal);

            try
            {
                listener.RecordObservableInstruments();

                return
                [
                    .. reading
                        .OrderBy(static x => x.Key, StringComparer.Ordinal)
                        .Select(static x => new NpgsqlPoolReading(x.Key, x.Value.Idle, x.Value.Used, x.Value.Max, x.Value.Reporters, x.Value.Disagree)
                        {
                            Instruments = x.Value.Instruments,
                        }),
                ];
            }
            finally
            {
                reading = null;
            }
        }
    }

    /// <summary>Gives back one lease, stopping the listener with the last.</summary>
    internal void Release()
    {
        MeterListener? stopping = null;

        lock (gate)
        {
            if (leases == 0)
            {
                return;
            }

            if (--leases == 0)
            {
                stopping = listener;
                listener = null;
            }
        }

        stopping?.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        MeterListener? stopping;

        lock (gate)
        {
            disposed = true;
            leases = 0;
            stopping = listener;
            listener = null;
        }

        stopping?.Dispose();
    }

    private MeterListener Start()
    {
        string watched = meterName;

        MeterListener started = new()
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (Observes(instrument, watched))
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            },
        };

        // Npgsql 9.0.4's two are Measurement<int>, and so are 10.0.3's; long as well, so a later Npgsql that widens them
        // is still read.
        started.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Accept(instrument, value, tags));
        started.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Accept(instrument, value, tags));
        started.Start();

        Interlocked.Increment(ref starts);
        return started;
    }

    /// <summary>
    /// One measurement. They arrive only from <see cref="Read" />'s own call, on its thread and under its lock,
    /// because nothing that measures on its own is enabled - the count is what proves that.
    /// </summary>
    private void Accept(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Interlocked.Increment(ref measurements);

        Dictionary<string, PoolTally>? into = reading;
        if (into is null)
        {
            return;
        }

        NpgsqlPoolInstruments? instruments = NpgsqlPoolInstruments.Publishing(instrument.Name);
        if (instruments is null)
        {
            return;
        }

        string? pool = null;
        string? state = null;

        // Either set's tag, whichever instrument carries it: the instrument says which names were read, and a pool is
        // the same pool whatever its tag is called.
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (NpgsqlPoolInstruments.IsPoolNameTag(tag.Key))
            {
                pool = tag.Value as string;
            }
            else if (NpgsqlPoolInstruments.IsStateTag(tag.Key))
            {
                state = tag.Value as string;
            }
        }

        if (pool is null)
        {
            return;
        }

        if (!into.TryGetValue(pool, out PoolTally? tally))
        {
            tally = new PoolTally();
            into[pool] = tally;
        }

        if (instruments == NpgsqlPoolInstruments.FromNpgsql10)
        {
            tally.Instruments = instruments;
        }

        int count = (int) Math.Clamp(value, 0, int.MaxValue);

        // Each data source reports one measurement per instrument and state, so a name measured twice for the same
        // one is two data sources sharing it - counted, and a second value that differs from the first marks the
        // reading as nobody's in particular rather than silently replacing it. A pool reported under both sets of
        // names is measured twice the same way, with the same numbers, and stands.
        if (string.Equals(instrument.Name, instruments.Max, StringComparison.Ordinal))
        {
            tally.Disagree |= tally.MaxReports > 0 && tally.Max != count;
            tally.Max = count;
            tally.MaxReports++;
        }
        else if (string.Equals(state, "idle", StringComparison.Ordinal))
        {
            tally.Disagree |= tally.IdleReports > 0 && tally.Idle != count;
            tally.Idle = count;
            tally.IdleReports++;
        }
        else if (string.Equals(state, "used", StringComparison.Ordinal))
        {
            tally.Disagree |= tally.UsedReports > 0 && tally.Used != count;
            tally.Used = count;
            tally.UsedReports++;
        }
    }

    private sealed class PoolTally
    {
        public int Idle { get; set; }

        public int Used { get; set; }

        public int? Max { get; set; }

        public int MaxReports { get; set; }

        public int IdleReports { get; set; }

        public int UsedReports { get; set; }

        /// <summary>The names the numbers were read under: Npgsql 10's once any measurement came under them.</summary>
        public NpgsqlPoolInstruments Instruments { get; set; } = NpgsqlPoolInstruments.UpToNpgsql9;

        /// <summary>Whether two data sources under the name reported different numbers.</summary>
        public bool Disagree { get; set; }

        /// <summary>How many data sources reported under the name.</summary>
        public int Reporters => Math.Max(1, Math.Max(MaxReports, Math.Max(IdleReports, UsedReports)));
    }
}

/// <summary>
/// One holder's interest in the pool observer: while any lease is undisposed, the listener runs.
/// </summary>
internal sealed class SessionPoolLease : IDisposable
{
    private readonly SessionPoolObserver owner;
    private int released;

    internal SessionPoolLease(SessionPoolObserver owner) => this.owner = owner;

    /// <summary>Every pool in the process, now.</summary>
    /// <exception cref="ObjectDisposedException">This lease has been given back.</exception>
    public IReadOnlyList<NpgsqlPoolReading> Read()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref released) != 0, this);
        return owner.Read();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            owner.Release();
        }
    }
}
