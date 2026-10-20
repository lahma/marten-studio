using System.Collections.Concurrent;
using System.Security.Claims;

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Projections;
using Marten.Storage;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Integration.Tests.Projections;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>A clock a test moves by hand - the sweeper's only; the daemon keeps the real one.</summary>
internal sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now = now.Add(by);
}

/// <summary>An event the <see cref="GatedProjection" /> blocks on when <paramref name="Gate" /> names a closed gate.</summary>
/// <param name="Gate">The gate to wait on, or <see langword="null" /> to pass straight through.</param>
/// <param name="DelayMilliseconds">How long the projection takes over it: a shard that moves, slowly - D04's shape.</param>
public sealed record GatedEvent(string? Gate, int DelayMilliseconds = 0);

/// <summary>The document the <see cref="GatedProjection" /> writes.</summary>
public sealed class GatedDocument
{
    public Guid Id { get; set; }

    public long Sequence { get; set; }
}

/// <summary>
/// An async projection a test can hang: an event naming a gate blocks its page until the test opens the gate - which is
/// a shard whose agent is alive and not moving, exactly what D03 looks for and D04 measures.
/// </summary>
/// <remarks>
/// A synchronous wait on purpose, so the page is held where the daemon cannot cancel it short of the gate. Every gate
/// times out after two minutes, so a test that forgets to open one stalls a shard, never the run.
/// </remarks>
internal sealed partial class GatedProjection : EventProjection
{
    public const string ProjectionName = "Gated";

    private static readonly ConcurrentDictionary<string, ManualResetEventSlim> Gates = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, int> Waiting = new(StringComparer.Ordinal);

    public GatedProjection()
    {
        Name = ProjectionName;
    }

    /// <summary>Closes (creates) a gate.</summary>
    public static void Close(string gate) => Gates[gate] = new ManualResetEventSlim(false);

    /// <summary>How many pages have reached the gate and are held at it - so a test knows a replay is really inside.</summary>
    public static int WaitingAt(string gate) => Waiting.GetValueOrDefault(gate);

    /// <summary>Opens a gate, releasing every page blocked on it.</summary>
    public static void Open(string gate)
    {
        if (Gates.TryGetValue(gate, out ManualResetEventSlim? held))
        {
            held.Set();
        }
    }

    public static void Project(IEvent<GatedEvent> gated, IDocumentOperations operations)
    {
        ArgumentNullException.ThrowIfNull(gated);
        ArgumentNullException.ThrowIfNull(operations);

        if (gated.Data.Gate is { } gate && Gates.TryGetValue(gate, out ManualResetEventSlim? held))
        {
            Waiting.AddOrUpdate(gate, 1, static (_, count) => count + 1);
            held.Wait(TimeSpan.FromMinutes(2));
            Waiting.AddOrUpdate(gate, 0, static (_, count) => count - 1);
        }

        if (gated.Data.DelayMilliseconds > 0)
        {
            Thread.Sleep(gated.Data.DelayMilliseconds);
        }

        operations.Store(new GatedDocument { Id = gated.Id, Sequence = gated.Sequence });
    }
}

/// <summary>
/// A real host over the sample store with a real daemon (or none), for the daemon checks' live tests: the store's own
/// configuration, a coordinator registered the way an application registers it, and a sweeper on a clock the test moves.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two clocks, on purpose.</b> The daemon, Marten and Postgres keep real time - a host clock registered as
/// <c>TimeProvider</c> would be read by parts of JasperFx too - and the sweeper the test drives is built here with a
/// <see cref="MovableClock" />, sharing the container's <see cref="HealthHistory" />. So "the advisor saw the mark ahead
/// of the shard two minutes ago" is two sweeps and an <see cref="Advance" />, while every age the checks read from the
/// database is the server's own.
/// </para>
/// <para>
/// Everything else goes through the studio's own services - the registry, the checks, <see cref="IHealthService" /> for a
/// visitor's report - so nothing is bypassed that a page would not bypass.
/// </para>
/// </remarks>
internal sealed class DaemonHealthHost : IAsyncDisposable
{
    private readonly IHost host;

    private DaemonHealthHost(IHost host, string schema, IMartenDatabase database, MovableClock clock, LogCapture logs, IHealthStatementObserver? observer)
    {
        this.host = host;
        Schema = schema;
        Database = database;
        Clock = clock;
        Logs = logs;

        IServiceProvider services = host.Services;
        Sweeper = new HealthSweeper(
            services.GetRequiredService<HealthCheckRegistry>(),
            services.GetRequiredService<HealthHistory>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<PostgresStudio.Services.Settings.IStudioSettings>(),
            services.GetRequiredService<StudioLogThrottle>(),
            services.GetRequiredService<ILogger<HealthSweeper>>(),
            clock,
            runtime: services.GetRequiredService<IStudioRuntime>(),
            observer: observer);
    }

    public string Schema { get; }

    public string EventSchema => Schema + "_events";

    public IMartenDatabase Database { get; }

    public MovableClock Clock { get; }

    public LogCapture Logs { get; }

    public HealthSweeper Sweeper { get; }

    public IServiceProvider Services => host.Services;

    public IDocumentStore Store => host.Services.GetRequiredService<IDocumentStore>();

    public StudioScope Scope => new(MartenStoreRegistry.DefaultStoreKey, Database.Id.Identity, null);

    public ResolvedScope Resolved => new(
        Scope,
        new MartenStoreRegistration(MartenStoreRegistry.DefaultStoreKey, "Default", typeof(IDocumentStore)),
        Store,
        Database);

    /// <param name="postgres">The live container.</param>
    /// <param name="schema">A schema of the test's own; created empty with an <c>_events</c> twin.</param>
    /// <param name="mode">The daemon this host runs, or <see langword="null" /> for none in this process.</param>
    /// <param name="seed">Whether to append the sample's events before the daemon starts.</param>
    /// <param name="gated">Whether to register the <see cref="GatedProjection" /> beside the sample's own.</param>
    /// <param name="store">Changes on top of the sample store's configuration.</param>
    /// <param name="studio">The studio's options; every capability by default.</param>
    /// <param name="observer">A statement observer, for the no-lock helper.</param>
    /// <param name="fresh">
    /// Whether to create the schemas empty (and seed them). <see langword="false" /> attaches a second host to a schema
    /// another host made - a studio beside a daemon in another process, as far as either can tell.
    /// </param>
    /// <param name="denyWrites">Whether the studio's write policy refuses every visitor - the activity gate's third refusal.</param>
    public static async Task<DaemonHealthHost> StartAsync(
        PostgresFixture postgres,
        string schema,
        DaemonMode? mode = DaemonMode.Solo,
        bool seed = true,
        bool gated = false,
        Action<StoreOptions>? store = null,
        Action<MartenStudioOptions>? studio = null,
        IHealthStatementObserver? observer = null,
        bool fresh = true,
        bool denyWrites = false)
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        if (fresh)
        {
            await postgres.CreateSchemaAsync(schema, token);
            await postgres.CreateSchemaAsync(schema + "_events", token);
        }

        LogCapture logs = new();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        MartenServiceCollectionExtensions.MartenConfigurationExpression marten = builder.Services
            .AddMarten(options =>
            {
                SampleStore.Configure(options, postgres.ConnectionString);
                options.DatabaseSchemaName = schema;
                options.Events.DatabaseSchemaName = schema + "_events";
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Projections.LeadershipPollingTime = 1_000;

                if (gated)
                {
                    options.Events.AddEventType<GatedEvent>();
                    options.Projections.Add(new GatedProjection(), ProjectionLifecycle.Async);
                }

                store?.Invoke(options);
            })
            .UseLightweightSessions();

        if (mode is { } daemonMode)
        {
            marten.AddAsyncDaemon(daemonMode);
        }

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, Anonymous>();
        builder.Services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();

            if (denyWrites)
            {
                options.WriteAuthorizationPolicy = DenyingWrites.PolicyName;
            }

            studio?.Invoke(options);
        });

        if (denyWrites)
        {
            builder.Services.AddSingleton<IAuthorizationService, DenyingWrites>();
        }

        IHost host = builder.Build();
        IDocumentStore built = host.Services.GetRequiredService<IDocumentStore>();

        await built.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        if (seed && fresh)
        {
            await SampleDataSeeder.SeedEventsAsync(built, token);
        }

        await host.StartAsync(token);

        IReadOnlyList<IMartenDatabase> databases = await built.Storage.AllDatabases();
        return new DaemonHealthHost(host, schema, databases[0], new MovableClock(DateTimeOffset.UtcNow), logs, observer);
    }

    /// <summary>One regular sweep, as the cache would run it, at the movable clock's time.</summary>
    public Task<HealthSweep> SweepAsync() =>
        Sweeper.SweepAsync(Resolved, HealthSweepKind.Regular, TestContext.Current.CancellationToken);

    /// <summary>Moves the sweeper's clock.</summary>
    public void Advance(TimeSpan by) => Clock.Advance(by);

    /// <summary>A visitor's report, through a circuit-shaped scope and the container's own cache.</summary>
    public async Task<HealthReport> ReportAsync()
    {
        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();

        return await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(Scope, HealthReportRequest.Regular, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs one projections-service call in a circuit-shaped scope.</summary>
    public async Task<T> ProjectionsAsync<T>(Func<IProjectionDataService, Task<T>> call)
    {
        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        return await call(circuit.ServiceProvider.GetRequiredService<IProjectionDataService>());
    }

    /// <summary>The daemon this process hosts, for a test that has to drive it directly - a rebuild the studio did not start.</summary>
    public IProjectionDaemon Daemon =>
        host.Services.GetRequiredService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>().DaemonForMainDatabase();

    /// <summary>Waits until the daemon here says it runs.</summary>
    public Task WaitForDaemonAsync() =>
        ProjectionsFixture.WaitForAsync(() => Task.FromResult(Daemon.IsRunning), "the async daemon to start", TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

    /// <summary>Waits until a shard's progression row is at least <paramref name="sequence" />.</summary>
    public Task WaitForShardAsync(string shard, long sequence, PostgresFixture postgres) =>
        ProjectionsFixture.WaitForAsync(
            async () => await ShardPositionAsync(shard, postgres) >= sequence,
            shard + " to reach " + sequence,
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken);

    /// <summary>A shard's position, read on a connection of the test's own.</summary>
    public async Task<long> ShardPositionAsync(string shard, PostgresFixture postgres)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(
            "select last_seq_id from " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression") + " where name = @name", connection);
        command.Parameters.AddWithValue("name", shard);

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is long position ? position : -1;
    }

    /// <summary>Sets one progression row's last_updated to <paramref name="ago" /> before the server's now: a shard that last moved then.</summary>
    public async Task BackdateAsync(string row, TimeSpan ago, PostgresFixture postgres)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(
            "update " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression")
            + " set last_updated = transaction_timestamp() - @ago where name = @name",
            connection);
        command.Parameters.AddWithValue("ago", ago);
        command.Parameters.AddWithValue("name", row);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Appends <paramref name="count" /> gated events to fresh streams, all passing through unless <paramref name="gate" /> is named.</summary>
    public async Task AppendGatedAsync(int count, string? gate = null, int delayMilliseconds = 0)
    {
        await using IDocumentSession session = Store.LightweightSession();

        for (int i = 0; i < count; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new GatedEvent(gate, delayMilliseconds));
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Moves every shard's row and the HighWaterMark row <paramref name="ago" /> into the past, in server time - what an
    /// idle store looks like once that much real time has gone by - and answers the server's time at the moment it did.
    /// The allocation fence is left alone: whether it is restamped is what is being watched.
    /// </summary>
    public async Task<DateTimeOffset> BackdateAllButTheFenceAsync(TimeSpan ago, PostgresFixture postgres)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(
            "with moved as (update " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression")
            + " set last_updated = transaction_timestamp() - @ago where name not in (@fence, @stuck) returning 1)"
            + " select transaction_timestamp() from (select count(*) from moved) m",
            connection);
        command.Parameters.AddWithValue("ago", ago);
        command.Parameters.AddWithValue("fence", HealthDaemonQueries.AllocationFenceRow);
        command.Parameters.AddWithValue("stuck", HealthDaemonQueries.StuckGapRow);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>When the allocation fence was last restamped, in server time, or <see langword="null" /> without one.</summary>
    public Task<DateTimeOffset?> FenceStampedAsync(PostgresFixture postgres) => StampedAsync(HealthDaemonQueries.AllocationFenceRow, postgres);

    /// <summary>When one progression row was last written, in server time, or <see langword="null" /> without one.</summary>
    public async Task<DateTimeOffset?> StampedAsync(string row, PostgresFixture postgres)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(
            "select last_updated from " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression") + " where name = @row", connection);
        command.Parameters.AddWithValue("row", row);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        return await reader.ReadAsync(TestContext.Current.CancellationToken) && !reader.IsDBNull(0) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    /// <summary>The highest sequence handed out, read on a connection of the test's own.</summary>
    public async Task<long> HighestSequenceAsync(PostgresFixture postgres)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(
            "select last_value from " + SqlIdentifier.Qualify(EventSchema, "mt_events_sequence"), connection);

        return (long) (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    /// <summary>
    /// Stops the host. A host that fails to stop fails the test: a daemon that will not stop is a defect to see, not one to
    /// swallow (H2 review). The failure is written to the test's output first, so it is visible even when the test body
    /// already failed for another reason.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("The daemon health host did not stop cleanly: " + exception);
            throw;
        }
        finally
        {
            host.Dispose();
        }
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>An authorization service whose write policy refuses everyone and whose every other policy passes.</summary>
    private sealed class DenyingWrites : IAuthorizationService
    {
        public const string PolicyName = "h2-deny-writes";

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(string.Equals(policyName, PolicyName, StringComparison.Ordinal) ? AuthorizationResult.Failed() : AuthorizationResult.Success());
    }
}

/// <summary>What the daemon checks' live tests assert over a sweep.</summary>
internal static class DaemonSweeps
{
    /// <summary>A check's result in the sweep.</summary>
    public static HealthCheckResult Result(HealthSweep sweep, string code) =>
        sweep.Results.Single(x => string.Equals(x.Check.Code, code, StringComparison.Ordinal));

    /// <summary>A check's findings in the sweep.</summary>
    public static IReadOnlyList<HealthFinding> Findings(HealthSweep sweep, string code) => Result(sweep, code).Findings;

    /// <summary>The event store's own objects a daemon check reads: the tables, their sequence, and their indexes.</summary>
    private static readonly string[] ReadTables = ["mt_event_progression", "mt_events", "mt_doc_deadletterevent"];

    /// <summary>
    /// The relations a daemon check may lock: the event store's own objects it reads, with the AccessShareLock a plain
    /// read takes, and nothing else (the no-lock helper's allow-list).
    /// </summary>
    /// <remarks>
    /// The planner opens <em>every</em> index of a table it plans against, whether or not the plan uses it, so a read
    /// of <c>mt_events</c> holds <c>pk_mt_events_stream_and_version</c> as well as its primary key. Marten names each
    /// index after its table (<c>pkey_</c>, <c>pk_</c>, <c>idx_</c>, <c>mt_..._idx_</c>), which is what is matched.
    /// </remarks>
    public static bool IsAllowedRead(HeldLock held, string eventSchema) =>
        held.IsCatalogRead
        || held.IsOwnTransaction
        || (held.LockType == "relation" && held.Mode == "AccessShareLock" && held.Schema == eventSchema
            && held.Relation is { } relation
            && Array.Exists(ReadTables, table => relation.Contains(table, StringComparison.Ordinal)));
}
