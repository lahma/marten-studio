using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Aggregation;
using Marten.Events.Projections;

using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

// ----------------------------------------------------------------------------------------------------
// The test-local domain. Deliberately not samples/MartenStudio.SampleDomain: that project is being
// changed by the projections packet at the same time, and an integration suite that depends on somebody
// else's in-flight domain is a suite that fails for reasons that have nothing to do with it.
// ----------------------------------------------------------------------------------------------------

/// <summary>The event that starts a stream.</summary>
/// <param name="Customer">Who placed it.</param>
public record OrderPlaced(string Customer);

/// <summary>A line added to an order.</summary>
/// <param name="Sku">What was added.</param>
/// <param name="Price">What it cost, in whole units.</param>
public record ItemAdded(string Sku, int Price);

/// <summary>The event that ends a stream.</summary>
/// <param name="TrackingNumber">The carrier's number.</param>
public record OrderShipped(string TrackingNumber);

/// <summary>
/// The marker the poison projection throws on, so the daemon has something real to dead-letter.
/// </summary>
/// <param name="Reason">Why it is here.</param>
public record PoisonPill(string Reason);

/// <summary>The single-stream aggregate, which is what the time-travel panel replays into.</summary>
public class OrderSummary
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>Who placed the order.</summary>
    public string Customer { get; set; } = string.Empty;

    /// <summary>How many lines it has.</summary>
    public int LineCount { get; set; }

    /// <summary>What it comes to.</summary>
    public int Total { get; set; }

    /// <summary>Whether it has shipped.</summary>
    public bool Shipped { get; set; }
}

/// <summary>The inline single-stream projection over <see cref="OrderSummary" />.</summary>
public partial class OrderSummaryProjection : SingleStreamProjection<OrderSummary, Guid>
{
    /// <summary>Starts the summary.</summary>
    public static OrderSummary Create(OrderPlaced placed) => new() { Customer = placed.Customer };

    /// <summary>Adds a line.</summary>
    public static void Apply(ItemAdded added, OrderSummary summary)
    {
        summary.LineCount++;
        summary.Total += added.Price;
    }

    /// <summary>Marks it shipped.</summary>
    public static void Apply(OrderShipped shipped, OrderSummary summary) => summary.Shipped = true;
}

/// <summary>What the poison projection writes when it does not throw.</summary>
public class PoisonNote
{
    /// <summary>The event id, so the document is keyed by the event that made it.</summary>
    public Guid Id { get; set; }

    /// <summary>What the event said.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// An async projection that throws on <see cref="PoisonPill" />.
/// </summary>
/// <remarks>
/// <para>
/// The daemon's continuous error policy skips apply errors by default
/// (<c>ProjectionGraph.Errors.SkipApplyErrors</c> is true), and a skipped apply error is exactly what
/// Marten records as a <c>DeadLetterEvent</c>. So this projection is how the suite gets a real dead
/// letter instead of a hand-written document that would agree with the test and disagree with Marten.
/// </para>
/// <para>
/// Internal rather than public on purpose: JasperFx's source generator emits the <c>ApplyAsync</c>
/// dispatcher as a public override on this class, and a public undocumented member would trip CS1591,
/// which this repository treats as an error and which no doc comment of ours can reach.
/// </para>
/// </remarks>
internal partial class PoisonProjection : EventProjection
{
    /// <summary>Records an ordinary event.</summary>
    public static PoisonNote Create(OrderPlaced placed) => new() { Text = placed.Customer };

    /// <summary>Throws, every time.</summary>
    public static PoisonNote Create(PoisonPill pill) =>
        throw new InvalidOperationException("PoisonProjection refuses to apply " + pill.Reason);
}

/// <summary>The visitor an integration test runs as.</summary>
/// <remarks>
/// The studio reads the principal from <see cref="AuthenticationStateProvider" /> and never from an
/// <c>HttpContext</c>, because a rendered studio is a circuit. With no policy configured nothing asks,
/// but the service still has to be resolvable.
/// </remarks>
internal sealed class StubAuthenticationStateProvider : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
}

/// <summary>
/// A real Marten event store in one schema, driven through the studio's own services.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes <em>through</em> <see cref="IEventDataService" /> resolved from a container that ran
/// <c>AddMartenStudio</c>, so the scope resolution, the capability gate and the audit entry are all
/// exercised rather than bypassed (plan §5.4). Two containers are built over the same store: one with
/// every capability on, one with none, so a refusal can be asserted without a second database.
/// </para>
/// <para>
/// The store is registered as a singleton <c>IDocumentStore</c> instance rather than through
/// <c>AddMarten</c>: the registry scans service descriptors for service types assignable to
/// <c>IDocumentStore</c>, which finds it, and building the store here keeps the seeding and the studio
/// pointed at exactly the same configuration.
/// </para>
/// </remarks>
internal sealed class EventsFixture : IAsyncDisposable
{
    /// <summary>How many streams the seed writes.</summary>
    public const int SeededStreamCount = 30;

    /// <summary>How many of them carry a poison event, when the poison projection is registered.</summary>
    public const int PoisonStreamCount = 4;

    private readonly ServiceProvider writableProvider;
    private readonly ServiceProvider readOnlyProvider;
    private readonly IServiceScope writableScope;
    private readonly IServiceScope readOnlyScope;

    private EventsFixture(
        DocumentStore store,
        string schema,
        ServiceProvider writableProvider,
        ServiceProvider readOnlyProvider)
    {
        Store = store;
        Schema = schema;
        this.writableProvider = writableProvider;
        this.readOnlyProvider = readOnlyProvider;

        writableScope = writableProvider.CreateScope();
        readOnlyScope = readOnlyProvider.CreateScope();
    }

    /// <summary>The store the studio reads.</summary>
    public DocumentStore Store { get; }

    /// <summary>The schema everything lives in.</summary>
    public string Schema { get; }

    /// <summary>The stream ids the seed wrote, in the order it wrote them.</summary>
    public List<Guid> StreamIds { get; } = [];

    /// <summary>The stream the seed archived.</summary>
    public Guid ArchivedStreamId { get; private set; }

    /// <summary>The streams carrying a poison event, when the poison projection is registered.</summary>
    public List<Guid> PoisonStreamIds { get; } = [];

    /// <summary>The correlation id every seeded stream carries.</summary>
    public string CorrelationId { get; } = "corr-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The scope every call is made in: the default store, its one database, no tenant.</summary>
    public StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>The data service with every capability enabled.</summary>
    public IEventDataService Service => writableScope.ServiceProvider.GetRequiredService<IEventDataService>();

    /// <summary>The data service of a studio that was mapped read-only, which is the default (D4).</summary>
    public IEventDataService ReadOnlyService => readOnlyScope.ServiceProvider.GetRequiredService<IEventDataService>();

    /// <summary>The audit ring the writable container wrote to.</summary>
    public StudioActionLog Audit => writableScope.ServiceProvider.GetRequiredService<StudioActionLog>();

    /// <summary>
    /// The audit ring of the read-only container. A separate one on purpose: the ring is a singleton per
    /// process, and the two containers are two processes as far as it is concerned - so a refusal made
    /// through <see cref="ReadOnlyService" /> is recorded here and not in <see cref="Audit" />.
    /// </summary>
    public StudioActionLog ReadOnlyAudit => readOnlyScope.ServiceProvider.GetRequiredService<StudioActionLog>();

    /// <summary>Builds the store, creates its schema and seeds it.</summary>
    /// <param name="connectionString">The container's connection string.</param>
    /// <param name="schema">The schema this test class owns.</param>
    /// <param name="withPoisonProjection">
    /// Whether the async projection that throws is registered. Off by default: a class that is not about
    /// dead letters should not pay for a daemon.
    /// </param>
    public static async Task<EventsFixture> CreateAsync(
        string connectionString,
        string schema,
        bool withPoisonProjection = false)
    {
        DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(connectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // The optional mt_events columns the studio has to discover rather than assume.
            options.Events.MetadataConfig.CorrelationIdEnabled = true;
            options.Events.MetadataConfig.CausationIdEnabled = true;
            options.Events.MetadataConfig.HeadersEnabled = true;

            // Adds mt_events.is_skipped, without which MarkEventsAsSkipped has nothing to write.
            options.Events.EnableEventSkippingInProjectionsOrSubscriptions = true;

            options.Events.AddEventType<OrderPlaced>();
            options.Events.AddEventType<ItemAdded>();
            options.Events.AddEventType<OrderShipped>();
            options.Events.AddEventType<PoisonPill>();

            options.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Inline);

            if (withPoisonProjection)
            {
                options.Projections.Add(new PoisonProjection(), ProjectionLifecycle.Async);
            }
        });

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var fixture = new EventsFixture(
            store,
            schema,
            BuildProvider(store, MartenStudioCapabilities.All()),
            BuildProvider(store, new MartenStudioCapabilities()));

        await fixture.SeedAsync(withPoisonProjection);

        return fixture;
    }

    /// <summary>Appends the seed: thirty streams, one archived, one poisoned, all with metadata.</summary>
    private async Task SeedAsync(bool withPoison)
    {
        await using IDocumentSession session = Store.LightweightSession();

        session.CorrelationId = CorrelationId;
        session.CausationId = "cause-seed";
        session.SetHeader("seeded-by", "EventsFixture");

        for (int index = 0; index < SeededStreamCount; index++)
        {
            var id = Guid.CreateVersion7();
            StreamIds.Add(id);

            List<object> events = [new OrderPlaced("customer-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))];

            for (int line = 0; line <= index % 3; line++)
            {
                events.Add(new ItemAdded(
                    "sku-" + line.ToString(System.Globalization.CultureInfo.InvariantCulture), (line + 1) * 10));
            }

            if (index % 5 == 0)
            {
                events.Add(new OrderShipped("track-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            session.Events.StartStream<OrderSummary>(id, events);
        }

        await session.SaveChangesAsync();

        // One archived stream, so the archived filters have something to hide and to show.
        ArchivedStreamId = StreamIds[^1];

        await using (IDocumentSession archiving = Store.LightweightSession())
        {
            archiving.Events.ArchiveStream(ArchivedStreamId);
            await archiving.SaveChangesAsync();
        }

        if (!withPoison)
        {
            return;
        }

        await using IDocumentSession poison = Store.LightweightSession();

        poison.CorrelationId = CorrelationId;

        // Several, not one: the tests in a dead-letter class share a database, and one of them discards a
        // record. With a single poisoned stream, whichever test ran after it would find nothing.
        for (int index = 0; index < PoisonStreamCount; index++)
        {
            var id = Guid.CreateVersion7();
            PoisonStreamIds.Add(id);
            StreamIds.Add(id);

            poison.Events.StartStream<OrderSummary>(
                id,
                new OrderPlaced("poisoned-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new PoisonPill("a deliberately unapplicable event"));
        }

        await poison.SaveChangesAsync();
    }

    /// <summary>An open connection to the store's own database, for the assertions that read raw rows.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Marten.Storage.IMartenDatabase> databases = await Store.Storage.AllDatabases();
        NpgsqlConnection connection = databases[0].CreateConnection(Marten.Storage.ConnectionUsage.Read);

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static ServiceProvider BuildProvider(IDocumentStore store, MartenStudioCapabilities capabilities)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton(store);

        services.AddMartenStudio(options =>
        {
            options.Capabilities = capabilities;

            // The tenant listing is a query the Events suite has no use for, and turning it off keeps the
            // scope resolution to one round trip.
            options.DiscoverTenantIds = false;
        });

        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        writableScope.Dispose();
        readOnlyScope.Dispose();

        await writableProvider.DisposeAsync();
        await readOnlyProvider.DisposeAsync();
        await Store.DisposeAsync();
    }
}

/// <summary>
/// One schema, one Marten store and one seed, shared by every test in a class.
/// </summary>
/// <remarks>
/// <para>
/// A class fixture rather than <c>PostgresTestBase</c>, which creates its schema per <em>test method</em>.
/// That is right for the SQL-builder suites, whose setup is one <c>create table</c>; it is wrong here,
/// where setup is a <c>DocumentStore</c>, a full schema application and thirty appended streams. Paying
/// that per method meant three dozen stores, three dozen connection pools and three dozen schema drops
/// against one Postgres in a run, which exhausted it.
/// </para>
/// <para>
/// The price is that the tests in a class share one database, so a test that changes something must not
/// change something another test asserts about. The seed is large enough to give each mutating test a
/// stream of its own, and the assertions that could see another test's write are written to be
/// order-independent.
/// </para>
/// </remarks>
public abstract class EventsStoreFixtureBase : IAsyncLifetime
{
    private readonly PostgresFixture postgres;

    /// <summary>Wires the class fixture to the assembly's one Postgres.</summary>
    /// <param name="postgres">The container fixture, injected by xunit.</param>
    protected EventsStoreFixtureBase(PostgresFixture postgres) => this.postgres = postgres;

    /// <summary>The schema this class owns.</summary>
    public abstract string Schema { get; }

    /// <summary>Whether the async projection that throws is registered.</summary>
    protected virtual bool WithPoisonProjection => false;

    /// <summary>The store, the seed and the studio's services over them.</summary>
    internal EventsFixture Events { get; private set; } = null!;

    /// <summary>Creates the schema and everything in it, unless there is no Docker to do it on.</summary>
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema);

        Events = await EventsFixture.CreateAsync(postgres.ConnectionString, Schema, WithPoisonProjection);

        await AfterSeedAsync();
    }

    /// <summary>Anything the class needs once the store exists - running a daemon, for instance.</summary>
    protected virtual Task AfterSeedAsync() => Task.CompletedTask;

    /// <summary>Disposes the store and the two containers built over it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Events is not null)
        {
            await Events.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>The store the streams, timeline, feed, types and time-travel tests read.</summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class EventsStoreFixture(PostgresFixture postgres) : EventsStoreFixtureBase(postgres)
{
    /// <inheritdoc />
    public override string Schema => "events_live";
}

/// <summary>
/// The store the dead-letter tests read: the same seed plus an async projection that throws, and a
/// daemon run once here so that every test in the class starts from real dead letters.
/// </summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class PoisonStoreFixture(PostgresFixture postgres) : EventsStoreFixtureBase(postgres)
{
    /// <summary>How long the daemon gets to find the poison events before the fixture gives up.</summary>
    private static readonly TimeSpan DaemonDeadline = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public override string Schema => "events_deadletters";

    /// <inheritdoc />
    protected override bool WithPoisonProjection => true;

    /// <summary>
    /// Runs the projection daemon until Marten has recorded a dead letter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The daemon is started here, in the test fixture.</b> AGENTS.md hard rule 11 forbids
    /// <c>store.BuildProjectionDaemonAsync()</c> in studio code, because a second daemon alongside the
    /// host's own fights it for the advisory lock. This fixture owns its store outright and no other
    /// daemon exists, so it is the one place the call is right - and the studio code under test never
    /// touches a daemon: the dead-letter screen only ever reads documents.
    /// </para>
    /// <para>
    /// The wait is a wait-on-condition with a deadline, never a fixed sleep: how long the daemon takes
    /// to reach the poison events is a property of the machine, not of the test.
    /// </para>
    /// </remarks>
    protected override async Task AfterSeedAsync()
    {
        if (await CountAsync() > 0)
        {
            return;
        }

        // IProjectionDaemon is IDisposable, not IAsyncDisposable (verified against JasperFx.Events 2.69.3).
        using IProjectionDaemon daemon = await Events.Store.BuildProjectionDaemonAsync();

        await daemon.StartAllAsync();

        try
        {
            using var poll = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            using var deadline = new CancellationTokenSource(DaemonDeadline);

            while (await poll.WaitForNextTickAsync(deadline.Token))
            {
                if (await CountAsync() > 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The projection daemon did not record a dead letter within {DaemonDeadline}. The poison " +
                "projection is registered async and throws on poison_pill, and Marten's continuous error " +
                "policy skips apply errors by default - if this fails, one of those three has changed.");
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    private async Task<long> CountAsync()
    {
        await using IQuerySession session = Events.Store.QuerySession();

        return await session.Query<DeadLetterEvent>().CountAsync();
    }
}
