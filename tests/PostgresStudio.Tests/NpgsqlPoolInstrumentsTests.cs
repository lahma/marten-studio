using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests;

/// <summary>
/// NPG10: C01 and Database › Sessions read Npgsql's two observable pool instruments under either of their names -
/// Npgsql 8 and 9's <c>db.client.connections.usage</c> and <c>…connections.max</c>, tagged <c>pool.name</c> and
/// <c>state</c>, and Npgsql 10's <c>db.client.connection.count</c> and <c>…connection.max</c>, tagged
/// <c>db.client.connection.pool.name</c> and <c>db.client.connection.state</c> - with nothing else enabled under either.
/// </summary>
/// <remarks>
/// The synthetic meters are the test's own, so nothing there touches the process's pools. The rest reads the real Npgsql
/// this project restored, through data sources that never connect: 9.0.4 in the whole-solution pass, and in the Npgsql
/// pass over <c>postgres-studio.slnf</c> the latest-tested 10.x - so each pass proves its own end, and an Npgsql that
/// renames the instruments again fails here rather than leaving C01 and the panel quietly empty.
/// </remarks>
public sealed class NpgsqlPoolInstrumentsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The Npgsql this project restored.</summary>
    private static Version NpgsqlVersion => typeof(NpgsqlDataSource).Assembly.GetName().Version!;

    /// <summary>The names the restored Npgsql publishes, by its major version.</summary>
    private static NpgsqlPoolInstruments Expected =>
        NpgsqlVersion.Major >= 10 ? NpgsqlPoolInstruments.FromNpgsql10 : NpgsqlPoolInstruments.UpToNpgsql9;

    /// <summary>
    /// The rest of Npgsql's instruments under each set of names, as the probe listed them at 9.0.4 and 10.0.3: the
    /// synchronous ones every command records, and the observable ratio whose reading resets the host's counters.
    /// </summary>
    private sealed record Shape(NpgsqlPoolInstruments Pool, string Executing, string PendingRequests, string Duration, string PreparedRatio)
    {
        public static Shape Of(string name) => name switch
        {
            nameof(NpgsqlPoolInstruments.UpToNpgsql9) => new Shape(
                NpgsqlPoolInstruments.UpToNpgsql9,
                "db.client.commands.executing",
                "db.client.connections.pending_requests",
                "db.client.commands.duration",
                "db.client.commands.prepared_ratio"),
            nameof(NpgsqlPoolInstruments.FromNpgsql10) => new Shape(
                NpgsqlPoolInstruments.FromNpgsql10,
                "db.client.operation.npgsql.executing",
                "db.client.connection.npgsql.pending_requests",
                "db.client.operation.duration",
                "db.client.operation.npgsql.prepared_ratio"),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    private static Measurement<int> Usage(NpgsqlPoolInstruments names, int value, string pool, string state) =>
        new(value, new KeyValuePair<string, object?>(names.PoolNameTag, pool), new KeyValuePair<string, object?>(names.StateTag, state));

    private static Measurement<int> Ceiling(NpgsqlPoolInstruments names, int value, string pool) =>
        new(value, new KeyValuePair<string, object?>(names.PoolNameTag, pool));

    private static string NewMeterName() => "PostgresStudio.Tests.Pools." + Guid.NewGuid().ToString("N");

    // ----------------------------------------------------------------------------------------------
    // Both sets of names, on meters of the test's own
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(NpgsqlPoolInstruments.UpToNpgsql9))]
    [InlineData(nameof(NpgsqlPoolInstruments.FromNpgsql10))]
    public void Either_set_of_names_is_read_with_only_its_two_observable_pool_instruments_enabled(string names)
    {
        Shape shape = Shape.Of(names);
        string meterName = NewMeterName();
        using Meter meter = new(meterName);
        int ratioCalls = 0;

        UpDownCounter<int> executing = meter.CreateUpDownCounter<int>(shape.Executing);
        UpDownCounter<int> pending = meter.CreateUpDownCounter<int>(shape.PendingRequests);
        Histogram<double> duration = meter.CreateHistogram<double>(shape.Duration);
        ObservableGauge<double> ratio = meter.CreateObservableGauge(shape.PreparedRatio, () =>
        {
            Interlocked.Increment(ref ratioCalls);
            return 50d;
        });

        ObservableUpDownCounter<int> usage = meter.CreateObservableUpDownCounter(shape.Pool.Usage, () => new[]
        {
            Usage(shape.Pool, 3, "pool-a", "idle"),
            Usage(shape.Pool, 7, "pool-a", "used"),
            Usage(shape.Pool, 1, "pool-b", "idle"),
            Usage(shape.Pool, 0, "pool-b", "used"),
        });

        ObservableUpDownCounter<int> max = meter.CreateObservableUpDownCounter(shape.Pool.Max, () => new[]
        {
            Ceiling(shape.Pool, 10, "pool-a"),
            Ceiling(shape.Pool, 100, "pool-b"),
        });

        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        usage.Enabled.Should().BeTrue();
        max.Enabled.Should().BeTrue();
        executing.Enabled.Should().BeFalse("a synchronous instrument is recorded on every command the host runs");
        pending.Enabled.Should().BeFalse("pending requests is synchronous under both sets of names");
        duration.Enabled.Should().BeFalse();
        ratio.Enabled.Should().BeFalse("reading the prepared ratio resets the counters the host's own exporter reads");

        lease.Read().Should().Equal(
            new NpgsqlPoolReading("pool-a", 3, 7, 10) { Instruments = shape.Pool },
            new NpgsqlPoolReading("pool-b", 1, 0, 100) { Instruments = shape.Pool });

        long after = observer.MeasurementsReceived;

        for (int i = 0; i < 100; i++)
        {
            executing.Add(1);
            executing.Add(-1);
            pending.Add(1);
            pending.Add(-1);
            duration.Record(0.01);
        }

        observer.MeasurementsReceived.Should().Be(after, "nothing is recorded per command: only a reading produces measurements");
        ratioCalls.Should().Be(0);
    }

    /// <summary>
    /// An Npgsql that published both sets at once - as a library moving between OpenTelemetry's conventions may - reports
    /// each pool twice. With the same numbers that is one pool, read from Npgsql 10's names, and it stands, as two pools
    /// under one name that agree do; with different numbers it is nobody's in particular.
    /// </summary>
    [Fact]
    public void A_pool_reported_under_both_sets_of_names_is_one_pool_whose_numbers_stand_when_they_agree()
    {
        string meterName = NewMeterName();
        using Meter meter = new(meterName);

        NpgsqlPoolInstruments old = NpgsqlPoolInstruments.UpToNpgsql9;
        NpgsqlPoolInstruments current = NpgsqlPoolInstruments.FromNpgsql10;

        meter.CreateObservableUpDownCounter(old.Usage, () => new[]
        {
            Usage(old, 2, "dual", "idle"), Usage(old, 5, "dual", "used"),
            Usage(old, 2, "split", "idle"), Usage(old, 5, "split", "used"),
            Usage(old, 1, "older-only", "idle"), Usage(old, 4, "older-only", "used"),
        });

        meter.CreateObservableUpDownCounter(old.Max, () => new[]
        {
            Ceiling(old, 20, "dual"), Ceiling(old, 20, "split"), Ceiling(old, 30, "older-only"),
        });

        meter.CreateObservableUpDownCounter(current.Usage, () => new[]
        {
            Usage(current, 2, "dual", "idle"), Usage(current, 5, "dual", "used"),
            Usage(current, 2, "split", "idle"), Usage(current, 9, "split", "used"),
        });

        meter.CreateObservableUpDownCounter(current.Max, () => new[]
        {
            Ceiling(current, 20, "dual"), Ceiling(current, 20, "split"),
        });

        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        Dictionary<string, NpgsqlPoolReading> pools = lease.Read().ToDictionary(static x => x.PoolName);

        pools.Keys.Should().BeEquivalentTo(["dual", "split", "older-only"], "one pool per name, whichever names reported it");

        pools["dual"].IsAmbiguous.Should().BeFalse("both sets said 5 of 20");
        pools["dual"].Used.Should().Be(5);
        pools["dual"].Max.Should().Be(20);
        pools["dual"].Instruments.Should().Be(current);

        pools["split"].IsAmbiguous.Should().BeTrue("5 under one set of names and 9 under the other is nobody's 5");

        pools["older-only"].Should().Be(new NpgsqlPoolReading("older-only", 1, 4, 30) { Instruments = old });
    }

    /// <summary>A pool and a state are recognised by either set's tag, whichever instrument carries it.</summary>
    [Fact]
    public void A_pool_is_recognised_by_either_tag_name()
    {
        string meterName = NewMeterName();
        using Meter meter = new(meterName);

        NpgsqlPoolInstruments old = NpgsqlPoolInstruments.UpToNpgsql9;
        NpgsqlPoolInstruments current = NpgsqlPoolInstruments.FromNpgsql10;

        // Npgsql 10's instruments with the older tags, and the older instruments with Npgsql 10's tags.
        meter.CreateObservableUpDownCounter(current.Usage, () => new[] { Usage(old, 1, "new-names-old-tags", "idle"), Usage(old, 2, "new-names-old-tags", "used") });
        meter.CreateObservableUpDownCounter(current.Max, () => new[] { Ceiling(old, 8, "new-names-old-tags") });
        meter.CreateObservableUpDownCounter(old.Usage, () => new[] { Usage(current, 3, "old-names-new-tags", "idle"), Usage(current, 4, "old-names-new-tags", "used") });
        meter.CreateObservableUpDownCounter(old.Max, () => new[] { Ceiling(current, 9, "old-names-new-tags") });

        using SessionPoolObserver observer = new(meterName);
        using SessionPoolLease lease = observer.Lease();

        lease.Read().Should().Equal(
            new NpgsqlPoolReading("new-names-old-tags", 1, 2, 8) { Instruments = current },
            new NpgsqlPoolReading("old-names-new-tags", 3, 4, 9) { Instruments = old });
    }

    [Fact]
    public void The_observer_enables_the_two_pool_instruments_by_either_name_and_nothing_else()
    {
        string meterName = NewMeterName();
        using Meter meter = new(meterName);

        foreach (NpgsqlPoolInstruments names in NpgsqlPoolInstruments.All)
        {
            SessionPoolObserver.Observes(meter.CreateObservableUpDownCounter(names.Usage, static () => Array.Empty<Measurement<int>>()), meterName)
                .Should().BeTrue();
            SessionPoolObserver.Observes(meter.CreateObservableUpDownCounter(names.Max, static () => Array.Empty<Measurement<int>>()), meterName)
                .Should().BeTrue();
            SessionPoolObserver.Observes(meter.CreateUpDownCounter<int>(names.Usage + ".sync"), meterName).Should().BeFalse();
        }

        SessionPoolObserver.Observes(meter.CreateUpDownCounter<int>(NpgsqlPoolInstruments.FromNpgsql10.Max), meterName)
            .Should().BeFalse("a synchronous instrument is never enabled, whatever it is called");

        using Meter other = new(NewMeterName());
        SessionPoolObserver.Observes(other.CreateObservableUpDownCounter(NpgsqlPoolInstruments.FromNpgsql10.Usage, static () => Array.Empty<Measurement<int>>()), meterName)
            .Should().BeFalse("only the meter the observer watches");

        NpgsqlPoolInstruments.Publishing("db.client.connection.npgsql.pending_requests").Should().BeNull();
        NpgsqlPoolInstruments.Publishing("db.client.operation.npgsql.prepared_ratio").Should().BeNull();
    }

    // ----------------------------------------------------------------------------------------------
    // The real Npgsql this project restored
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The names the restored Npgsql publishes are the set its major version says, the other set is absent, and pending
    /// requests is still synchronous - the facts C01's design rests on, at whichever end this pass runs.
    /// </summary>
    [Fact]
    public void The_restored_Npgsql_publishes_the_names_its_major_version_says()
    {
        using NpgsqlDataSource source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=npg10_shape_" + Guid.NewGuid().ToString("N") + ";Username=none;Password=none");

        List<Instrument> published = [];

        using (MeterListener survey = new())
        {
            survey.InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == SessionPoolObserver.NpgsqlMeterName)
                {
                    lock (published)
                    {
                        published.Add(instrument);
                    }
                }
            };

            survey.Start();
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            "Npgsql " + NpgsqlVersion.ToString(3) + ": " + string.Join(", ", published.Select(static x => x.Name).Order(StringComparer.Ordinal)));

        Dictionary<string, bool> observable = published.ToDictionary(static x => x.Name, static x => x.IsObservable, StringComparer.Ordinal);
        Shape shape = Shape.Of(NpgsqlVersion.Major >= 10 ? nameof(NpgsqlPoolInstruments.FromNpgsql10) : nameof(NpgsqlPoolInstruments.UpToNpgsql9));
        NpgsqlPoolInstruments other = NpgsqlPoolInstruments.All.Single(x => x != Expected);

        observable.Should().Contain(Expected.Usage, true);
        observable.Should().Contain(Expected.Max, true);
        observable.Should().NotContainKey(other.Usage);
        observable.Should().NotContainKey(other.Max);
        observable.Should().Contain(shape.PendingRequests, false, "C01 judges saturation by used reaching max because this is synchronous");
        observable.Should().Contain(shape.PreparedRatio, true);
        observable.Should().Contain(shape.Executing, false);

        published.Where(static x => SessionPoolObserver.Observes(x, SessionPoolObserver.NpgsqlMeterName)).Select(static x => x.Name)
            .Should().BeEquivalentTo([Expected.Usage, Expected.Max], "the observer enables the two pool instruments and nothing else");
    }

    /// <summary>The real observer, on the real Npgsql meter, finds a data source's pool by its connection string.</summary>
    [Fact]
    public void The_real_observer_reads_a_real_data_sources_pool()
    {
        using NpgsqlDataSource source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=npg10_pool_" + Guid.NewGuid().ToString("N") + ";Username=none;Password=secret;Maximum Pool Size=7");

        using SessionPoolObserver observer = new();
        using SessionPoolLease lease = observer.Lease();

        lease.Read().Should().ContainSingle(x => x.PoolName == source.ConnectionString).Which
            .Should().Be(new NpgsqlPoolReading(source.ConnectionString, 0, 0, 7) { Instruments = Expected });
    }

    /// <summary>
    /// C01's gatherer, as a sweep runs it, against a real data source that never connects: the pool is found and read -
    /// at Npgsql 10 with the older names alone it was "not among the pools Npgsql reports", and C01 judged nothing.
    /// </summary>
    [Fact]
    public async Task C01_gathers_a_real_data_sources_pool()
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=npg10_c01_" + Guid.NewGuid().ToString("N") + ";Username=none;Password=secret;Maximum Pool Size=7;Timeout=1");

        await using ServiceProvider services = new ServiceCollection().AddSingleton<SessionPoolObserver>().BuildServiceProvider();
        SessionPoolObserver observer = services.GetRequiredService<SessionPoolObserver>();

        DataSourceOnly target = new(source);
        ResolvedSource resolved = new(new StudioScope("default", "default", null), new StudioSourceRegistration("default", "Default", StudioSourceKind.DataSource), target);
        DateTimeOffset now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

        await using HealthConnection connection = new(target);
        HealthCheckContext context = new(
            resolved,
            new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
            NoStudioRuntime.Instance,
            services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            now,
            new HealthSweepMemo(),
            Token);

        PoolSaturationCheck check = new();
        PoolFacts facts = await check.GatherAsync(context, Token);

        facts.Should().Be(new PoolFacts(true, 7, 0, 0, 7, facts.PoolsInProcess, now) { Instruments = Expected });
        facts.PoolsInProcess.Should().BeGreaterThan(0);
        observer.IsListening.Should().BeFalse("the gatherer's lease is given back when it has read");

        check.Evaluate(facts, HealthEvaluation.At(now)).Note.Should().Be("0 of 7 connections of this process's pool are in use.");
    }

    // ----------------------------------------------------------------------------------------------
    // What C01 says it read
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The caveat names the instruments the pool was read from. Under Npgsql 8 and 9's names it is, byte for byte, the
    /// sentence MartenStudio has always shown; under Npgsql 10's it names those.
    /// </summary>
    [Fact]
    public void C01s_caveat_names_the_instruments_it_read()
    {
        DateTimeOffset now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        PoolFacts nearlyFull = new(true, 10, 9, 1, 10, 1, now);

        string older = new PoolSaturationCheck().Evaluate(nearlyFull, HealthEvaluation.At(now)).Findings.Single().Caveat!;

        older.Should().Be(
            "Read in this process from Npgsql's own pool counters (db.client.connections.usage and db.client.connections.max) at "
            + "2026-10-20 09:00:00Z, including the studio's own connection while it checks. Other processes on this database have "
            + "pools of their own, which this does not see. Npgsql counts waiting requests only per request, which the studio never "
            + "listens to, so \"fully in use\" is read from the pool reaching its ceiling on consecutive checks, one HealthInterval "
            + "apart.");

        string current = new PoolSaturationCheck()
            .Evaluate(nearlyFull with { Instruments = NpgsqlPoolInstruments.FromNpgsql10 }, HealthEvaluation.At(now))
            .Findings.Single().Caveat!;

        current.Should().Be(older.Replace(
            "(db.client.connections.usage and db.client.connections.max)",
            "(db.client.connection.count and db.client.connection.max)",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// The database a health context is resolved to, as little of one as C01 reads: its data source. Kept here rather than
    /// borrowed from the source provider, so this test reads the pool and nothing the provider decides.
    /// </summary>
    private sealed class DataSourceOnly(NpgsqlDataSource dataSource) : IStudioDatabase
    {
        public string Identity => "default";

        public string Name => "npg10";

        public string Server => "127.0.0.1:1";

        public NpgsqlDataSource? DataSource => dataSource;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => dataSource.CreateConnection();
    }
}
