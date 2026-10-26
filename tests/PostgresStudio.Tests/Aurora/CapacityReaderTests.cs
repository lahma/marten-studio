using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Aurora;

/// <summary>
/// W5-AUR A8: the host's <see cref="IPostgresCapacitySource" /> is found in the container once, asked at most once per
/// database and instance per <c>HealthInterval</c> - one call however many ask at once - and given five seconds; null, a
/// timeout and an exception are each "unknown" for the interval, and an exception is event 9500, at Warning once in ten
/// minutes and at Debug in between.
/// </summary>
public sealed class CapacityReaderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly PostgresPlatform Aurora = new(PostgresPlatformKind.AmazonAurora, "marker");

    private readonly NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1");

    private readonly CapturingLoggerProvider logs = new();

    private readonly ManualTimeProvider clock = new(Start);

    private sealed class Source(Func<PostgresDatabaseRef, PostgresServerInfo, CancellationToken, ValueTask<PostgresComputeCapacity?>> answer) : IPostgresCapacitySource
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public List<(PostgresDatabaseRef Database, PostgresServerInfo Server)> Asked { get; } = [];

        public ValueTask<PostgresComputeCapacity?> GetCapacityAsync(PostgresDatabaseRef database, PostgresServerInfo server, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);

            lock (Asked)
            {
                Asked.Add((database, server));
            }

            return answer(database, server, cancellationToken);
        }
    }

    private sealed class Database(NpgsqlDataSource dataSource) : IStudioDatabase
    {
        public string Identity => "orders-db";

        public string Name => "orders";

        public string Server => "127.0.0.1:1";

        public NpgsqlDataSource? DataSource => dataSource;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => dataSource.CreateConnection();
    }

    private ResolvedSource Resolved => new(
        new StudioScope("orders", "orders-db", null),
        new StudioSourceRegistration("orders", "Orders", StudioSourceKind.DataSource),
        new Database(dataSource));

    private static PostgresServerFacts Server(string? instance = "studio-test-instance-1") =>
        new(Start, 160004, "16.4", Aurora) { Instance = instance, InRecovery = false, StartedAt = Start.AddDays(-1) };

    private CapacityReader Reader(IPostgresCapacitySource? source, TimeSpan? timeout = null)
    {
        ServiceCollection services = new();

        if (source is not null)
        {
            services.AddSingleton(source);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        PostgresStudioOptions options = new() { HealthInterval = TimeSpan.FromSeconds(60) };

        return new CapacityReader(
            provider,
            new PostgresStudioSettings(Options.Create(options)),
            new StudioLogThrottle(clock),
            logs.CreateFactory().CreateLogger<CapacityReader>(),
            clock)
        {
            Timeout = timeout ?? CapacityReader.DefaultTimeout,
        };
    }

    public void Dispose()
    {
        dataSource.Dispose();
        logs.Dispose();
    }

    [Fact]
    public void A_source_is_given_five_seconds()
    {
        CapacityReader.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task With_no_source_registered_the_capacity_is_unknown_and_nothing_is_asked()
    {
        CapacityReader reader = Reader(null);

        CapacityReading reading = await reader.GetAsync(Resolved, Server(), TestContext.Current.CancellationToken);

        reading.Should().Be(new CapacityReading(null, Start, null, false));
        reader.SourceRegistered.Should().BeFalse();
        reader.Asked.Should().Be(0);
    }

    [Fact]
    public async Task A_reading_is_handed_on_with_the_database_and_the_instance_that_answered()
    {
        PostgresComputeCapacity capacity = PostgresComputeCapacity.AuroraServerlessV2(4.5, 0.5, 16, Start.AddMinutes(-1));
        Source source = new((_, _, _) => ValueTask.FromResult<PostgresComputeCapacity?>(capacity));

        CapacityReading reading = await Reader(source).GetAsync(Resolved, Server(), TestContext.Current.CancellationToken);

        reading.Capacity.Should().BeSameAs(capacity);
        reading.Unknown.Should().BeNull();
        reading.IsAcu.Should().BeTrue();
        reading.Text.Should().Be("4.5 ACU (min 0.5, max 16), measured 11:59 UTC");

        source.Asked.Should().ContainSingle();
        source.Asked[0].Database.Should().Be(new PostgresDatabaseRef("orders", "orders-db"));
        source.Asked[0].Server.Instance.Should().Be("studio-test-instance-1");
        source.Asked[0].Server.Platform.Should().Be(PostgresPlatformKind.AmazonAurora);
    }

    [Fact]
    public async Task A_null_answer_is_unknown_for_the_interval()
    {
        Source source = new((_, _, _) => ValueTask.FromResult<PostgresComputeCapacity?>(null));
        CapacityReader reader = Reader(source);

        CapacityReading reading = await reader.GetAsync(Resolved, Server(), TestContext.Current.CancellationToken);

        reading.Capacity.Should().BeNull();
        reading.SourceRegistered.Should().BeTrue();
        reading.Unknown.Should().Be("the capacity source does not know this database");

        await reader.GetAsync(Resolved, Server(), TestContext.Current.CancellationToken);
        source.Calls.Should().Be(1, "unknown is an answer, kept for the interval like any other");
        logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Information);
    }

    [Fact]
    public async Task A_source_past_its_timeout_is_unknown_and_nobody_waits_for_it_longer()
    {
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Source source = new(async (_, _, _) =>
        {
            // Ignores its token on purpose: the reader's own wait is what bounds it.
            await never.Task;
            return null;
        });

        CapacityReading reading = await Reader(source, TimeSpan.FromMilliseconds(100)).GetAsync(Resolved, Server(), TestContext.Current.CancellationToken);

        reading.Capacity.Should().BeNull();
        reading.Unknown.Should().Be("the capacity source took longer than 100 ms");
        logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Information, "a slow source is a value, said at Debug");
        never.SetResult();
    }

    [Fact]
    public async Task A_source_that_honours_its_token_is_cancelled_at_the_timeout()
    {
        CancellationToken handed = default;
        Source source = new(async (_, _, token) =>
        {
            handed = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });

        CapacityReading reading = await Reader(source, TimeSpan.FromMilliseconds(100)).GetAsync(Resolved, Server(), TestContext.Current.CancellationToken);

        reading.Unknown.Should().StartWith("the capacity source took longer than");
        handed.IsCancellationRequested.Should().BeTrue("the token the source was handed fires at the timeout");
    }

    [Fact]
    public async Task Many_callers_at_once_share_one_ask()
    {
        TaskCompletionSource<PostgresComputeCapacity?> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Source source = new((_, _, _) => new ValueTask<PostgresComputeCapacity?>(release.Task));
        CapacityReader reader = Reader(source);

        Task<CapacityReading>[] callers = [.. Enumerable.Range(0, 20).Select(_ => Task.Run(() => reader.GetAsync(Resolved, Server(), TestContext.Current.CancellationToken)))];

        // Released once the one ask has begun - whoever arrives after takes the same answer from the interval's cache.
        SpinWait.SpinUntil(() => source.Calls >= 1, TimeSpan.FromSeconds(10)).Should().BeTrue();
        release.SetResult(PostgresComputeCapacity.Of(2, 1, 4, "vCPU", Start));

        CapacityReading[] readings = await Task.WhenAll(callers);

        source.Calls.Should().Be(1);
        readings.Should().OnlyContain(static x => x.Capacity != null && x.Capacity.Unit == "vCPU");
    }

    [Fact]
    public async Task Each_interval_and_each_instance_asks_again()
    {
        Source source = new((_, server, _) => ValueTask.FromResult<PostgresComputeCapacity?>(PostgresComputeCapacity.Of(1, 1, 1, server.Instance ?? "none", Start)));
        CapacityReader reader = Reader(source);
        CancellationToken token = TestContext.Current.CancellationToken;

        await reader.GetAsync(Resolved, Server(), token);
        await reader.GetAsync(Resolved, Server(), token);
        source.Calls.Should().Be(1);

        (await reader.GetAsync(Resolved, Server("studio-test-instance-2"), token)).Capacity!.Unit.Should().Be("studio-test-instance-2",
            "a failover to another instance is another capacity");
        source.Calls.Should().Be(2);

        clock.Advance(TimeSpan.FromSeconds(61));
        await reader.GetAsync(Resolved, Server(), token);
        source.Calls.Should().Be(3, "a new HealthInterval asks again");
    }

    [Fact]
    public async Task An_exception_is_unknown_and_event_9500_at_Warning_once_in_ten_minutes_and_Debug_between()
    {
        Source source = new((_, _, _) => throw new InvalidOperationException("CloudWatch said no"));
        CapacityReader reader = Reader(source);
        CancellationToken token = TestContext.Current.CancellationToken;

        CapacityReading reading = await reader.GetAsync(Resolved, Server(), token);

        reading.Capacity.Should().BeNull();
        reading.Unknown.Should().Be("the capacity source failed (InvalidOperationException)");

        clock.Advance(TimeSpan.FromMinutes(1));
        await reader.GetAsync(Resolved, Server(), token);

        clock.Advance(TimeSpan.FromMinutes(10));
        await reader.GetAsync(Resolved, Server(), token);

        source.Calls.Should().Be(3);
        logs.Entries.Where(static x => x.EventId.Id == 9500).Select(static x => x.Level)
            .Should().Equal(LogLevel.Warning, LogLevel.Debug, LogLevel.Warning);
        logs.Entries.Where(static x => x.EventId.Id == 9500).Should().OnlyContain(static x => !x.Message.Contains("CloudWatch said no", StringComparison.Ordinal),
            "the source's own words are the exception's, never the message");
    }
}
