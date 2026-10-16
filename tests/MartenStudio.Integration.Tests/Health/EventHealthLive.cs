using System.Security.Claims;

using JasperFx.Events;

using Marten;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Generation;
using MartenStudio.Services;
using MartenStudio.Services.Health;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>The event type the event checks' live tests append: no projection of the sample domain handles it.</summary>
/// <param name="Number">Which one.</param>
/// <param name="Padding">A payload of the test's choosing, for the size checks.</param>
public sealed record HealthPing(int Number, string? Padding = null);

/// <summary>A DCB tag type of the tests' own: the sample domain registers none.</summary>
/// <param name="Value">The tag's value.</param>
public sealed record HealthTag(string Value);

/// <summary>A self-aggregating aggregate the tests register as live-only, for E06.</summary>
public sealed partial class HealthCounter
{
    /// <summary>The stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>How many pings it has seen.</summary>
    public int Count { get; set; }

    /// <summary>Counts a ping.</summary>
    public void Apply(HealthPing ping) => Count++;
}

/// <summary>
/// The sample domain's store on the live Postgres with a studio over it - <see cref="HealthLiveHost" />'s shape, plus
/// the sweeper itself, for a test that must sweep twice within one <c>HealthInterval</c>.
/// </summary>
internal sealed class EventHealthHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private EventHealthHost(ServiceProvider provider, LogCapture logs, string schema)
    {
        this.provider = provider;
        Logs = logs;
        Schema = schema;
    }

    /// <summary>The host's log, Marten Studio's categories only.</summary>
    public LogCapture Logs { get; }

    /// <summary>The document schema; the event store's is <see cref="EventSchema" />.</summary>
    public string Schema { get; }

    /// <summary>The event store's schema.</summary>
    public string EventSchema => Schema + "_events";

    /// <summary>The store.</summary>
    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    /// <summary>The store's database as a whole, as the scope selector names it.</summary>
    public StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <param name="postgres">The live container.</param>
    /// <param name="schema">A schema of the test's own; created empty, with an <c>_events</c> twin.</param>
    /// <param name="store">Changes on top of the sample domain's configuration.</param>
    /// <param name="studio">The studio's options.</param>
    /// <param name="services">Anything else the container needs.</param>
    public static async Task<EventHealthHost> CreateAsync(
        PostgresFixture postgres,
        string schema,
        Action<StoreOptions>? store = null,
        Action<MartenStudioOptions>? studio = null,
        Action<IServiceCollection>? services = null)
    {
        await postgres.CreateSchemaAsync(schema);
        await postgres.CreateSchemaAsync(schema + "_events");

        LogCapture logs = new();
        ServiceCollection collection = new();

        collection.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        collection.AddAuthorization();
        collection.AddSingleton<AuthenticationStateProvider, Anonymous>();
        collection.AddSingleton<IHostEnvironment>(new LiveEnvironment());

        collection.AddMarten(options =>
        {
            SampleStore.Configure(options, postgres.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";
            options.Events.AddEventType<HealthPing>();
            store?.Invoke(options);
        });

        collection.AddMartenStudio(options => studio?.Invoke(options));
        services?.Invoke(collection);

        EventHealthHost host = new(collection.BuildServiceProvider(), logs, schema);

        // The tables the checks read, created the way a deploy creates them - by the test, never by the studio.
        await host.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        return host;
    }

    /// <summary>The visitor's report, through a circuit-shaped scope of its own.</summary>
    public async Task<HealthReport> ReportAsync(HealthReportRequest? request = null)
    {
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        return await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(Scope, request ?? HealthReportRequest.Regular, TestContext.Current.CancellationToken);
    }

    /// <summary>A report that runs the deeper checks too.</summary>
    public Task<HealthReport> DeeperReportAsync() => ReportAsync(new HealthReportRequest(Deeper: HealthDeeperChecks.Run));

    /// <summary>One sweep straight through the sweeper, past the cache - so two of them are two samples.</summary>
    public Task<HealthSweep> SweepAsync(HealthSweepKind kind = HealthSweepKind.Regular)
    {
        IDocumentStore store = Store;
        ResolvedScope resolved = new(
            new StudioScope("default", store.Storage.Database.Id.Identity, null),
            new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
            store,
            store.Storage.Database);

        return provider.GetRequiredService<HealthSweeper>().SweepAsync(resolved, kind, TestContext.Current.CancellationToken);
    }

    /// <summary>One check's status row.</summary>
    public static HealthCheckStatus Status(HealthReport report, string code) =>
        report.Checks.Single(x => string.Equals(x.Code, code, StringComparison.Ordinal));

    /// <summary>One check's result in a sweep.</summary>
    public static HealthCheckResult Result(HealthSweep sweep, string code) =>
        sweep.Results.Single(x => string.Equals(x.Check.Code, code, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        await provider.DisposeAsync();
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class LiveEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "MartenStudio.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>
/// The system clock moved on by hand: the history keeps a sample only half a <c>HealthInterval</c> after the last,
/// so a test that needs two samples moves the studio's clock rather than waiting (never <c>Task.Delay</c>).
/// </summary>
internal sealed class SteppedClock : TimeProvider
{
    private long offsetTicks;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));

    /// <summary>Moves the clock on by <paramref name="by" />.</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref offsetTicks, by.Ticks);
}

/// <summary>What the event checks' live tests do to a store to produce, or rule out, a condition.</summary>
internal static class EventHealthData
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Appends <paramref name="streams" /> streams of <paramref name="eventsPerStream" /> pings each, in sessions of a
    /// few hundred streams, and returns their ids.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> AppendAsync(
        IDocumentStore store,
        int streams,
        int eventsPerStream,
        string? tenant = null,
        Func<int, string?>? padding = null)
    {
        List<Guid> ids = [];
        const int PerSession = 200;

        for (int first = 0; first < streams; first += PerSession)
        {
            await using IDocumentSession session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);

            for (int i = first; i < Math.Min(streams, first + PerSession); i++)
            {
                Guid id = Guid.NewGuid();
                ids.Add(id);
                session.Events.StartStream(id, Enumerable.Range(1, eventsPerStream).Select(n => (object) new HealthPing(n, padding?.Invoke(n))).ToArray());
            }

            await session.SaveChangesAsync(Token);
        }

        return ids;
    }

    /// <summary>
    /// Appends <paramref name="events" /> more pings to one stream, <paramref name="perSession" /> to a
    /// <c>SaveChanges</c> - so the stream is written by that many transactions.
    /// </summary>
    public static async Task AppendToAsync(IDocumentStore store, Guid stream, int events, string? tenant = null, int perSession = 500)
    {
        for (int done = 0; done < events; done += perSession)
        {
            await using IDocumentSession session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
            session.Events.Append(stream, Enumerable.Range(done, Math.Min(perSession, events - done)).Select(static n => (object) new HealthPing(n)).ToArray());
            await session.SaveChangesAsync(Token);
        }
    }

    /// <summary>
    /// The sample domain's own demo data - a small plan of only streams - so a quiet test runs over a store that
    /// looks like a real one rather than an empty one.
    /// </summary>
    public static async Task GenerateSampleAsync(IDocumentStore store, int streams = 400)
    {
        DemoDataPlan plan = DemoDataPlan.For(DemoDataSize.Small) with
        {
            Customers = 0,
            Orders = 0,
            SoftDeletedOrders = 0,
            Invoices = 0,
            Products = 0,
            Vehicles = 0,
            AuditNotes = 0,
            Streams = streams,
            AnalyzeWhenDone = true,
        };

        await new DemoDataGenerator(store, plan).GenerateAsync(progress: null, Token);
    }

    /// <summary>
    /// <paramref name="events" /> pings written by SQL in one statement - for a test that needs the row count of a
    /// large store and none of its realism - the oldest <paramref name="archivedShare" /> of them in an archived stream
    /// and the rest in a live one. Two stream rows, because an archived event's stream is archived too, and under
    /// <c>UseArchivedStreamPartitioning</c> the foreign key is <c>(stream_id, is_archived)</c>.
    /// </summary>
    public static async Task InsertSyntheticAsync(PostgresFixture postgres, string eventSchema, int events, double archivedShare = 0)
    {
        string streams = SqlIdentifier.Qualify(eventSchema, "mt_streams");
        string table = SqlIdentifier.Qualify(eventSchema, "mt_events");
        string sequence = SqlIdentifier.Qualify(eventSchema, "mt_events_sequence");
        int archived = (int) (events * archivedShare);

        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            $$"""
            insert into {{streams}} (id, version, is_archived) values (@live, @events - @archived, false), (@gone, @archived, true);
            insert into {{table}} (seq_id, id, stream_id, version, data, type, mt_dotnet_type, is_archived)
            select nextval('{{sequence}}'), gen_random_uuid(),
                   case when g <= @archived then @gone else @live end,
                   case when g <= @archived then g else g - @archived end,
                   '{"Number": 1}'::jsonb, 'health_ping', @dotnet, g <= @archived
              from generate_series(1, @events) g;
            """,
            connection);

        command.Parameters.AddWithValue("live", Guid.NewGuid());
        command.Parameters.AddWithValue("gone", Guid.NewGuid());
        command.Parameters.AddWithValue("events", events);
        command.Parameters.AddWithValue("archived", archived);
        command.Parameters.AddWithValue("dotnet", typeof(HealthPing).FullName + ", " + typeof(HealthPing).Assembly.GetName().Name);

        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Has PostgreSQL sample the event store's tables now, as autovacuum eventually would.</summary>
    public static Task AnalyzeAsync(PostgresFixture postgres, string eventSchema) =>
        ExecuteAsync(postgres, $"analyze {SqlIdentifier.Qualify(eventSchema, "mt_events")}; analyze {SqlIdentifier.Qualify(eventSchema, "mt_streams")};");

    /// <summary>Runs a statement of the test's own.</summary>
    public static async Task ExecuteAsync(PostgresFixture postgres, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Reads one number with a statement of the test's own.</summary>
    public static async Task<long> ScalarAsync(PostgresFixture postgres, string sql)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Waits, on a condition and never on a clock, for <paramref name="condition" /> to hold.</summary>
    public static async Task WaitForAsync(Func<Task<bool>> condition, string what, TimeSpan? deadline = null)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + (deadline ?? TimeSpan.FromSeconds(60));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));

        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= until)
            {
                throw new TimeoutException("Waited for " + what + ".");
            }

            await timer.WaitForNextTickAsync(Token);
        }
    }

    /// <summary>A store on Rich appends, where a failed save leaves tombstones.</summary>
    public static void Rich(StoreOptions options) => options.Events.AppendMode = EventAppendMode.Rich;
}
