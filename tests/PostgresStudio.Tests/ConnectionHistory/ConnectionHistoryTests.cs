using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.ConnectionHistory;

/// <summary>
/// Issue #43 without a database: a sample from one Holders reading, the server a data source names, the in-memory store's window,
/// the Holders page's view of a history, what <c>AddPostgresConnectionHistory()</c> registers, and which sources the sampler
/// lists, how often.
/// </summary>
public class ConnectionHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SessionHolderGroup Group(string database, string application, long total, long active = 0, long idle = 0) =>
        new(database, application, "app", "10.0.0.7", false, total, active, idle, 0, 0, null, null);

    [Fact]
    public void A_sample_counts_the_servers_client_sessions_less_its_own_and_its_applications_most_first()
    {
        SessionServerHolders holders = new(true, 100, 3, 2, 170000, 41, 3,
        [
            Group("orders", "orders-api", 25, active: 5, idle: 20),
            Group("billing", "orders-api", 5, idle: 5),
            Group("orders", "", 10, idle: 10),
        ]);

        PostgresConnectionSample sample = ConnectionHistorySampler.Sample("db:5432", "10.0.0.1:5432@2026-11-01T00:00:00Z", Now, holders, maxApplications: 20);

        sample.Server.Should().Be("db:5432");
        sample.Connections.Should().Be(40, "the sampler's own session is not counted");
        sample.ClientLimit.Should().Be(95, "max_connections less both reservations, as C03 judges");
        sample.ByApplication.Select(static x => (x.ApplicationName, x.Total)).Should().Equal(("orders-api", 30), (null, 10));
        sample.ByApplication[0].Idle.Should().Be(25);
    }

    [Fact]
    public void A_sample_keeps_the_most_applications_it_may()
    {
        SessionServerHolders holders = new(true, 100, 3, 0, 170000, 4, 3, [Group("a", "one", 1), Group("a", "two", 3), Group("a", "three", 2)]);

        ConnectionHistorySampler.Sample("db:5432", null, Now, holders, maxApplications: 2).ByApplication.Select(static x => x.ApplicationName)
            .Should().Equal("two", "three");
    }

    [Theory]
    [InlineData("Host=DB.Example;Port=6432;Database=orders;Username=app;Password=secret", "db.example:6432")]
    [InlineData("Host=db;Database=orders", "db:5432")]
    [InlineData("Database=orders", null)]
    [InlineData("not a connection string", null)]
    public void The_server_is_the_host_and_port_and_never_a_credential(string connectionString, string? server) =>
        ConnectionHistorySampler.ServerOf(connectionString).Should().Be(server);

    [Fact]
    public async Task The_in_memory_store_keeps_each_server_for_the_retention_and_reads_from_a_time()
    {
        ManualClock clock = new(Now);
        InMemoryConnectionHistoryStore store = new(Options.Create(new PostgresConnectionHistoryOptions { Retention = TimeSpan.FromHours(1) }), clock);

        await store.AppendAsync(new PostgresConnectionSample { Server = "db:5432", At = Now.AddMinutes(-90), Connections = 1 }, Token);
        await store.AppendAsync(new PostgresConnectionSample { Server = "db:5432", At = Now.AddMinutes(-30), Connections = 2 }, Token);
        await store.AppendAsync(new PostgresConnectionSample { Server = "other:5432", At = Now, Connections = 9 }, Token);
        await store.AppendAsync(new PostgresConnectionSample { Server = "DB:5432", At = Now, Connections = 3 }, Token);

        (await store.ReadAsync("db:5432", Now.AddDays(-1), Token)).Select(static x => x.Connections).Should().Equal([2L, 3L],
            "the one older than the retention is gone, and the server is matched ignoring case");
        (await store.ReadAsync("db:5432", Now.AddMinutes(-10), Token)).Select(static x => x.Connections).Should().Equal([3L]);
        (await store.ReadAsync("nowhere:5432", Now.AddDays(-1), Token)).Should().BeEmpty();
    }

    [Fact]
    public void The_view_finds_the_first_peak_the_instance_breaks_and_names_applications_only_past_the_gate()
    {
        PostgresConnectionSample[] samples =
        [
            new() { At = Now, Server = "db:5432", Instance = "a", Connections = 10, ClientLimit = 95, ByApplication = [new() { ApplicationName = "api", Total = 8 }] },
            new() { At = Now.AddMinutes(1), Server = "db:5432", Instance = "a", Connections = 90, ClientLimit = 95, ByApplication = [new() { ApplicationName = "api", Total = 70 }, new() { ApplicationName = null, Total = 20 }] },
            new() { At = Now.AddMinutes(2), Server = "db:5432", Instance = "b", Connections = 90, ClientLimit = 95, ByApplication = [new() { ApplicationName = "api", Total = 70 }] },
            new() { At = Now.AddMinutes(3), Server = "db:5432", Instance = "b", Connections = 5, ClientLimit = 95 },
        ];

        ConnectionHistoryView counts = ConnectionHistoryView.Of(samples, names: false)!;

        counts.Samples.Should().Be(4);
        counts.Peak.Should().Be(90);
        counts.PeakAt.Should().Be(Now.AddMinutes(1), "the first time it was reached");
        counts.ClientLimit.Should().Be(95);
        counts.InstanceChanges.Should().Be(1);
        counts.Since.Should().Be(Now);
        counts.Until.Should().Be(Now.AddMinutes(3));
        counts.Applications.Should().BeEmpty("below the activity gate no application is named");

        ConnectionHistoryView.Of(samples, names: true)!.Applications.Should().Equal(
            new ConnectionHistoryPeak("api", 70, Now.AddMinutes(1)),
            new ConnectionHistoryPeak(null, 20, Now.AddMinutes(1)));

        ConnectionHistoryView.Of([], names: true).Should().BeNull();
    }

    [Fact]
    public void The_registration_adds_one_hosted_service_and_a_store_the_host_may_replace_before_or_after()
    {
        ServiceCollection before = new();
        before.AddSingleton<IPostgresConnectionHistoryStore, HostStore>();
        before.AddPostgresConnectionHistory();
        before.AddPostgresConnectionHistory();

        before.Count(static x => x.ServiceType == typeof(IHostedService)).Should().Be(1, "registering twice adds the sampler once");
        before.Where(static x => x.ServiceType == typeof(IPostgresConnectionHistoryStore)).Should().ContainSingle()
            .Which.ImplementationType.Should().Be<HostStore>();

        ServiceCollection after = new();
        after.AddPostgresConnectionHistory();
        after.AddSingleton<IPostgresConnectionHistoryStore, HostStore>();

        after.AddOptions().BuildServiceProvider().GetRequiredService<IPostgresConnectionHistoryStore>().Should().BeOfType<HostStore>(
            "the last registration of a service is the one resolved");
    }

    [Fact]
    public void Without_a_studio_or_the_engine_the_sampler_says_what_to_register()
    {
        ServiceCollection services = new();
        services.AddPostgresConnectionHistory();

        Action resolve = () => services.BuildServiceProvider().GetRequiredService<ConnectionHistorySampler>();

        resolve.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("AddPostgresDiagnostics()");
    }

    [Theory]
    [InlineData(null, "Host=db;Application Name=orders-api", "orders-api (Postgres Studio history)")]
    [InlineData(null, "Host=db", "Postgres Studio history")]
    [InlineData("", "Host=db;Application Name=orders-api", null)]
    [InlineData("studio", "Host=db;Application Name=orders-api", "studio (history)")]
    public void The_kept_session_is_named_as_the_history_unless_the_host_keeps_the_applications(string? configured, string connectionString, string? expected) =>
        ConnectionHistorySampler.HistoryName(configured, connectionString, "Postgres Studio").Should().Be(expected);

    [Fact]
    public void A_long_host_name_is_cut_so_the_history_is_still_said_within_what_PostgreSQL_keeps()
    {
        string name = ConnectionHistorySampler.HistoryName(new string('a', 63), "Host=db", "Postgres Studio")!;

        name.Should().HaveLength(63).And.EndWith(" (history)");
    }

    [Fact]
    public async Task The_in_memory_store_keeps_at_most_its_bound_a_server_the_oldest_dropped_first()
    {
        ManualClock clock = new(Now);
        InMemoryConnectionHistoryStore store = new(Options.Create(new PostgresConnectionHistoryOptions { Retention = TimeSpan.FromDays(90) }), clock);

        for (int i = 0; i <= InMemoryConnectionHistoryStore.MaxSamplesPerServer; i++)
        {
            await store.AppendAsync(new PostgresConnectionSample { Server = "db:5432", At = Now.AddSeconds(i - InMemoryConnectionHistoryStore.MaxSamplesPerServer), Connections = i }, Token);
        }

        IReadOnlyList<PostgresConnectionSample> kept = await store.ReadAsync("db:5432", Now.AddDays(-90), Token);

        kept.Should().HaveCount(InMemoryConnectionHistoryStore.MaxSamplesPerServer);
        kept[0].Connections.Should().Be(1, "the first sample was the one dropped");
    }

    [Fact]
    public async Task The_pages_read_a_servers_history_once_per_interval_and_give_up_on_a_store_that_hangs()
    {
        CountingStore counting = new();
        ConnectionHistoryReader reader = new(
            counting,
            Options.Create(new PostgresConnectionHistoryOptions()),
            PostgresStudioSettings.Of(new PostgresStudioOptions { QueryTimeout = TimeSpan.FromMilliseconds(200) }));

        await reader.ReadAsync("db:5432", Token);
        await reader.ReadAsync("DB:5432", Token);
        counting.Reads.Should().Be(1, "two visitors within ConnectionHistoryReader.CacheFor share one read of the store");

        ConnectionHistoryReader hanging = new(
            new HangingStore(),
            Options.Create(new PostgresConnectionHistoryOptions()),
            PostgresStudioSettings.Of(new PostgresStudioOptions { QueryTimeout = TimeSpan.FromMilliseconds(200) }));

        Func<Task> read = () => hanging.ReadAsync("db:5432", Token);
        await read.Should().ThrowAsync<OperationCanceledException>("a store that hangs is cut off at QueryTimeout");
    }

    private sealed class CountingStore : IPostgresConnectionHistoryStore
    {
        public int Reads { get; private set; }

        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<PostgresConnectionSample>>([]);
        }
    }

    private sealed class HangingStore : IPostgresConnectionHistoryStore
    {
        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    [Theory]
    [InlineData(10, 7, 20, "Interval")]
    [InlineData(60, 0, 20, "Retention")]
    [InlineData(60, 7, 0, "MaxApplications")]
    public void The_options_are_bounded(int intervalSeconds, int retentionDays, int maxApplications, string member)
    {
        ServiceCollection services = new();
        services.AddPostgresConnectionHistory(o =>
        {
            o.Interval = TimeSpan.FromSeconds(intervalSeconds);
            o.Retention = TimeSpan.FromDays(retentionDays);
            o.MaxApplications = maxApplications;
        });

        Action read = () => _ = services.BuildServiceProvider().GetRequiredService<IOptions<PostgresConnectionHistoryOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("PostgresConnectionHistoryOptions." + member);
    }

    [Fact]
    public async Task The_sampler_never_lists_a_dynamic_source_and_lists_the_others_again_only_after_ten_minutes()
    {
        ManualClock clock = new(Now);
        ListingSource dynamic = new("tenants", StudioDatabaseCardinality.DynamicMultiple);
        ListingSource single = new("orders", StudioDatabaseCardinality.Single);
        ServiceCollection services = new();
        services.AddSingleton<IStudioSourceProvider>(new ListingProvider(dynamic, single));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IOptions<PostgresConnectionHistoryOptions> options = Options.Create(new PostgresConnectionHistoryOptions());

        using ConnectionHistorySampler sampler = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new InMemoryConnectionHistoryStore(options, clock),
            PostgresStudioSettings.Of(new PostgresStudioOptions()),
            NullLogger<ConnectionHistorySampler>.Instance,
            new StudioLogThrottle(clock),
            clock);

        (await sampler.SampleOnceAsync(Token)).Should().Be(0, "neither source names a server with a data source");
        (await sampler.SampleOnceAsync(Token)).Should().Be(0);

        dynamic.Listings.Should().Be(0, "enumerating a source of dynamic databases is not read-only (D27), and a background loop never does it");
        single.Listings.Should().Be(1, "the servers are kept for ConnectionHistorySampler.ServersFor, not listed every round");

        clock.Now = Now + ConnectionHistorySampler.ServersFor;
        await sampler.SampleOnceAsync(Token);

        single.Listings.Should().Be(2);
        dynamic.Listings.Should().Be(0);
    }

    private sealed class ListingProvider(params ListingSource[] sources) : IStudioSourceProvider
    {
        public long Generation => 0;

        public IReadOnlyList<StudioSourceRegistration> Registrations() => [.. sources.Select(static x => x.Registration)];

        public StudioSourceRegistration? Find(string? key) => sources.FirstOrDefault(x => x.Registration.Key == key)?.Registration;

        public StoreAvailability TryResolve(StudioSourceRegistration registration, [NotNullWhen(true)] out IStudioSource? source)
        {
            source = sources.First(x => x.Registration == registration);
            return StoreAvailability.Available;
        }

        public void Invalidate()
        {
        }

        public IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The sampler lists through the sources, never unattended.");

        public ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The sampler lists through the sources, never unattended.");
    }

    private sealed class ListingSource(string key, StudioDatabaseCardinality cardinality) : IStudioSource
    {
        public int Listings { get; private set; }

        public StudioSourceRegistration Registration { get; } = new(key, key, StudioSourceKind.DataSource);

        public StudioDatabaseCardinality Cardinality => cardinality;

        public bool ShowsTenantSelector => false;

        public ValueTask<IReadOnlyList<IStudioDatabase>> DatabasesAsync(CancellationToken cancellationToken)
        {
            Listings++;
            return ValueTask.FromResult<IReadOnlyList<IStudioDatabase>>([new NoDataSource(key)]);
        }

        public ValueTask<TenantList> DiscoverTenantsAsync(IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken) =>
            ValueTask.FromResult(TenantList.Unavailable);

        public ValueTask<ResolvedSource> CompleteAsync(StudioScope scope, IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ResolvedSource(scope, Registration, database));
    }

    private sealed class NoDataSource(string identity) : IStudioDatabase
    {
        public string Identity { get; } = identity;

        public string Name => Identity;

        public string Server => "localhost";

        public NpgsqlDataSource? DataSource => null;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) =>
            throw new NotSupportedException("The sampler opens only a database that has a data source.");
    }

    private sealed class HostStore : IPostgresConnectionHistoryStore
    {
        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PostgresConnectionSample>>([]);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
