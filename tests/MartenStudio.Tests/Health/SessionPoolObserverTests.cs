using System.Diagnostics.Metrics;

using PostgresStudio.Services.Database;

using Npgsql;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C01's listener (acceptance 2): it enables Npgsql's two observable pool instruments and nothing else, records
/// nothing per command, and is gone with the last lease - against a meter of this test's own, so nothing here
/// touches the process's real pools; and the instrument shapes it depends on, pinned against the real Npgsql.
/// </summary>
public sealed class SessionPoolObserverTests : IDisposable
{
    private readonly string meterName = "MartenStudio.Tests.Pools." + Guid.NewGuid().ToString("N");
    private readonly Meter meter;
    private readonly UpDownCounter<int> executing;
    private readonly UpDownCounter<int> pending;
    private readonly Counter<int> timeouts;
    private readonly Histogram<double> duration;
    private readonly ObservableUpDownCounter<int> usage;
    private readonly ObservableUpDownCounter<int> max;
    private readonly ObservableGauge<double> preparedRatio;
    private int usageCalls;
    private int maxCalls;
    private int ratioCalls;

    public SessionPoolObserverTests()
    {
        meter = new Meter(meterName);

        // The same names and kinds as Npgsql 9.0.4's MetricsReporter: the synchronous ones every command records,
        // and the three observable ones.
        executing = meter.CreateUpDownCounter<int>("db.client.commands.executing");
        pending = meter.CreateUpDownCounter<int>("db.client.connections.pending_requests");
        timeouts = meter.CreateCounter<int>("db.client.connections.timeouts");
        duration = meter.CreateHistogram<double>("db.client.commands.duration");

        usage = meter.CreateObservableUpDownCounter(SessionPoolObserver.UsageInstrument, () =>
        {
            Interlocked.Increment(ref usageCalls);
            return new[]
            {
                new Measurement<int>(3, new KeyValuePair<string, object?>("pool.name", "pool-a"), new KeyValuePair<string, object?>("state", "idle")),
                new Measurement<int>(7, new KeyValuePair<string, object?>("pool.name", "pool-a"), new KeyValuePair<string, object?>("state", "used")),
                new Measurement<int>(1, new KeyValuePair<string, object?>("pool.name", "pool-b"), new KeyValuePair<string, object?>("state", "idle")),
                new Measurement<int>(0, new KeyValuePair<string, object?>("pool.name", "pool-b"), new KeyValuePair<string, object?>("state", "used")),
            };
        });

        max = meter.CreateObservableUpDownCounter(SessionPoolObserver.MaxInstrument, () =>
        {
            Interlocked.Increment(ref maxCalls);
            return new[]
            {
                new Measurement<int>(10, new KeyValuePair<string, object?>("pool.name", "pool-a")),
                new Measurement<int>(100, new KeyValuePair<string, object?>("pool.name", "pool-b")),
            };
        });

        preparedRatio = meter.CreateObservableGauge("db.client.commands.prepared_ratio", () =>
        {
            Interlocked.Increment(ref ratioCalls);
            return 50d;
        });
    }

    public void Dispose() => meter.Dispose();

    [Fact]
    public void Nothing_is_enabled_before_a_lease_or_after_the_last_one()
    {
        using SessionPoolObserver observer = new(meterName);

        observer.IsListening.Should().BeFalse("an observer nobody leased listens to nothing");
        usage.Enabled.Should().BeFalse();

        SessionPoolLease first = observer.Lease();
        SessionPoolLease second = observer.Lease();

        usage.Enabled.Should().BeTrue();
        max.Enabled.Should().BeTrue();

        first.Dispose();
        observer.IsListening.Should().BeTrue("a second lease is still held");
        usage.Enabled.Should().BeTrue();

        second.Dispose();
        observer.IsListening.Should().BeFalse("the last lease takes the listener with it");
        usage.Enabled.Should().BeFalse("a disposed MeterListener disables what it enabled");
        max.Enabled.Should().BeFalse();
        observer.Leases.Should().Be(0);
    }

    [Fact]
    public void Only_the_two_observable_pool_instruments_are_ever_enabled()
    {
        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        usage.Enabled.Should().BeTrue();
        max.Enabled.Should().BeTrue();

        executing.Enabled.Should().BeFalse("a synchronous instrument is recorded on every command the host runs");
        pending.Enabled.Should().BeFalse("pending_requests is synchronous in Npgsql 9.0.4, whatever its name suggests");
        timeouts.Enabled.Should().BeFalse();
        duration.Enabled.Should().BeFalse();
        preparedRatio.Enabled.Should().BeFalse(
            "prepared_ratio is observable, but enabling it makes Npgsql count every command and reading it resets the host exporter's counters");
    }

    [Fact]
    public void A_synchronous_measurement_never_reaches_the_observer()
    {
        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        long before = observer.MeasurementsReceived;

        for (int i = 0; i < 100; i++)
        {
            executing.Add(1);
            executing.Add(-1);
            pending.Add(1);
            pending.Add(-1);
            timeouts.Add(1);
            duration.Record(0.01);
        }

        observer.MeasurementsReceived.Should().Be(before, "nothing is recorded per command: only a reading produces measurements");
    }

    [Fact]
    public void A_reading_calls_the_two_callbacks_once_each_and_reports_every_pool()
    {
        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        IReadOnlyList<NpgsqlPoolReading> pools = lease.Read();

        pools.Should().Equal(
            new NpgsqlPoolReading("pool-a", 3, 7, 10),
            new NpgsqlPoolReading("pool-b", 1, 0, 100));

        usageCalls.Should().Be(1);
        maxCalls.Should().Be(1);
        ratioCalls.Should().Be(0, "the observer never asks prepared_ratio for a value");
        pools[0].UsedShare.Should().BeApproximately(0.7, 0.0001);
    }

    /// <summary>
    /// Follow-up 4: two data sources built from one connection string report under one <c>pool.name</c> - the host's
    /// <c>AddNpgsqlDataSource(cs)</c> beside Marten's. The observer counts them; when their numbers differ the reading
    /// is nobody's in particular, and when they agree it stands, since whichever is Marten's says the same.
    /// </summary>
    [Fact]
    public void Two_pools_under_one_name_are_counted_and_ambiguous_when_their_numbers_differ()
    {
        string name = "MartenStudio.Tests.SharedPools." + Guid.NewGuid().ToString("N");
        using Meter shared = new(name);

        static Measurement<int> Of(int value, string pool, string? state = null) => state is null
            ? new Measurement<int>(value, new KeyValuePair<string, object?>("pool.name", pool))
            : new Measurement<int>(value, new KeyValuePair<string, object?>("pool.name", pool), new KeyValuePair<string, object?>("state", state));

        shared.CreateObservableUpDownCounter(SessionPoolObserver.UsageInstrument, static () => new[]
        {
            // Marten's and the host's, one connection string: one busy, one idle.
            Of(0, "shared", "idle"), Of(9, "shared", "used"),
            Of(4, "shared", "idle"), Of(0, "shared", "used"),

            // Two pools that happen to report the same.
            Of(2, "twins", "idle"), Of(1, "twins", "used"),
            Of(2, "twins", "idle"), Of(1, "twins", "used"),

            Of(1, "alone", "idle"), Of(2, "alone", "used"),
        });

        shared.CreateObservableUpDownCounter(SessionPoolObserver.MaxInstrument, static () => new[]
        {
            Of(10, "shared"), Of(10, "shared"),
            Of(20, "twins"), Of(20, "twins"),
            Of(30, "alone"),
        });

        using SessionPoolObserver observer = new(name);
        using SessionPoolLease lease = observer.Lease();

        Dictionary<string, NpgsqlPoolReading> pools = lease.Read().ToDictionary(static x => x.PoolName);

        pools["shared"].Reporters.Should().Be(2);
        pools["shared"].IsAmbiguous.Should().BeTrue("9 lent out of one pool and none of the other is nobody's 9");

        pools["twins"].Reporters.Should().Be(2);
        pools["twins"].IsAmbiguous.Should().BeFalse("every pool under the name says 1 of 20, so Marten's does too");
        pools["twins"].Used.Should().Be(1);

        pools["alone"].Reporters.Should().Be(1);
        pools["alone"].IsAmbiguous.Should().BeFalse();
    }

    [Fact]
    public void No_measurement_arrives_between_readings()
    {
        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        lease.Read();
        long after = observer.MeasurementsReceived;

        // Observable instruments are measured only when asked; nothing is pushed in between.
        usageCalls.Should().Be(1);
        observer.MeasurementsReceived.Should().Be(after);

        lease.Read();
        observer.MeasurementsReceived.Should().Be(after * 2);
    }

    [Fact]
    public void A_reading_without_a_lease_is_refused_and_a_returned_lease_cannot_read()
    {
        using SessionPoolObserver observer = new(meterName);

        Action unleased = () => observer.Read();
        unleased.Should().Throw<InvalidOperationException>();

        SessionPoolLease lease = observer.Lease();
        lease.Dispose();
        lease.Dispose();

        observer.Leases.Should().Be(0, "a lease given back twice is given back once");

        Action stale = () => lease.Read();
        stale.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void An_instrument_published_while_leased_is_enabled_when_it_is_one_of_the_two()
    {
        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        using Meter later = new(meterName);
        ObservableUpDownCounter<int> lateUsage = later.CreateObservableUpDownCounter(
            SessionPoolObserver.UsageInstrument, static () => Array.Empty<Measurement<int>>());
        UpDownCounter<int> lateExecuting = later.CreateUpDownCounter<int>("db.client.commands.executing");

        lateUsage.Enabled.Should().BeTrue("a data source created after the page opened is a pool like any other");
        lateExecuting.Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_restart_after_the_last_lease_starts_a_new_listener()
    {
        using SessionPoolObserver observer = new(meterName);

        observer.Lease().Dispose();
        using SessionPoolLease again = observer.Lease();

        observer.Starts.Should().Be(2);
        again.Read().Should().HaveCount(2);
    }

    [Fact]
    public void The_observer_watches_only_its_own_meter()
    {
        SessionPoolObserver.Observes(usage, meterName).Should().BeTrue();
        SessionPoolObserver.Observes(max, meterName).Should().BeTrue();
        SessionPoolObserver.Observes(usage, SessionPoolObserver.NpgsqlMeterName).Should().BeFalse("this test's meter is not Npgsql's");
        SessionPoolObserver.Observes(preparedRatio, meterName).Should().BeFalse();
        SessionPoolObserver.Observes(pending, meterName).Should().BeFalse();
        SessionPoolObserver.Observes(executing, meterName).Should().BeFalse();
    }

    /// <summary>
    /// The facts the design rests on, read off the real Npgsql: the meter's name, the two observable pool
    /// instruments, and that pending_requests is synchronous - so a later Npgsql that changes any of them fails
    /// here rather than quietly reporting nothing.
    /// </summary>
    [Fact]
    public void Npgsqls_own_instruments_are_the_shapes_the_observer_assumes()
    {
        // A data source creates Npgsql's MetricsReporter (and so its static meter) without connecting anywhere.
        using NpgsqlDataSource source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none");

        Dictionary<string, bool> published = new(StringComparer.Ordinal);

        using (MeterListener survey = new())
        {
            survey.InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == SessionPoolObserver.NpgsqlMeterName)
                {
                    lock (published)
                    {
                        published[instrument.Name] = instrument.IsObservable;
                    }
                }
            };

            survey.Start();
        }

        published.Should().Contain(SessionPoolObserver.UsageInstrument, true);
        published.Should().Contain(SessionPoolObserver.MaxInstrument, true);
        published.Should().Contain("db.client.connections.pending_requests", false,
            "Npgsql 9.0.4 keeps pending requests as a synchronous UpDownCounter, which the observer never enables");
        published.Should().Contain("db.client.commands.prepared_ratio", true);
        published.Should().Contain("db.client.commands.executing", false);
    }

    /// <summary>The real observer, on the real Npgsql meter, finds a data source's pool by its connection string.</summary>
    [Fact]
    public void The_real_observer_reads_a_real_data_sources_pool_by_its_connection_string()
    {
        using NpgsqlDataSource source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=pool_probe_" + Guid.NewGuid().ToString("N") + ";Username=none;Password=secret;Maximum Pool Size=7");

        using SessionPoolObserver observer = new();
        using SessionPoolLease lease = observer.Lease();

        NpgsqlPoolReading pool = lease.Read().Should().ContainSingle(x => x.PoolName == source.ConnectionString).Subject;

        pool.Max.Should().Be(7);
        pool.Used.Should().Be(0);
        source.ConnectionString.Should().NotContain("secret", "the pool's name is the connection string without its password");
    }
}
