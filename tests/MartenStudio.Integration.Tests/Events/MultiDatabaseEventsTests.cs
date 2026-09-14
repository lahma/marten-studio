using JasperFx;
using JasperFx.Events.Daemon;

using Marten;
using Marten.Services;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// A Marten store over <b>two real Postgres databases</b>, with the studio's services on top of it.
/// </summary>
/// <remarks>
/// <para>
/// Two schemas would not do. Marten identifies a database by
/// <c>DatabaseId.Identity</c> = <c>"{server}.{name}"</c>, so two schemas in one database are one database
/// as far as the scope selector, the audit entry and <c>Storage.AllDatabases()</c> are concerned. The
/// fixture therefore issues <c>create database</c> on the container for each of them.
/// </para>
/// <para>
/// <b>What this exists to catch.</b> <c>IStaticMultiTenancy.AddSingleTenantDatabase</c> makes the first
/// database registered the store's <em>default</em> (verified against Marten 9.35's
/// <c>StaticMultiTenancy</c>), so a session opened as <c>store.LightweightSession()</c> lands on database
/// A whatever the scope selector says. Every write and every session-based read in the Events area used to
/// be opened exactly that way, so archiving a stream that a visitor was looking at in database B wrote to
/// A - and the audit entry recorded B. The fix is one shared
/// <c>Marten.Services.SessionOptions.ForDatabase(...)</c>, and these tests are what says so.
/// </para>
/// </remarks>
internal sealed class MultiDatabaseEventsFixture : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    private MultiDatabaseEventsFixture(DocumentStore store, ServiceProvider provider)
    {
        Store = store;
        this.provider = provider;
        scope = provider.CreateScope();
    }

    /// <summary>The store, which spans both databases.</summary>
    public DocumentStore Store { get; }

    /// <summary>The first database registered, and therefore the store's default.</summary>
    public IMartenDatabase DatabaseA { get; private set; } = null!;

    /// <summary>The second, which is the one nothing reaches unless the session says so.</summary>
    public IMartenDatabase DatabaseB { get; private set; } = null!;

    /// <summary>The stream seeded into <see cref="DatabaseA" />.</summary>
    public Guid StreamInA { get; } = Guid.CreateVersion7();

    /// <summary>The stream seeded into <see cref="DatabaseB" />.</summary>
    public Guid StreamInB { get; } = Guid.CreateVersion7();

    /// <summary>The dead letter seeded into <see cref="DatabaseA" />.</summary>
    public Guid DeadLetterInA { get; } = Guid.CreateVersion7();

    /// <summary>The dead letter seeded into <see cref="DatabaseB" />.</summary>
    public Guid DeadLetterInB { get; } = Guid.CreateVersion7();

    /// <summary>The data service with every capability enabled.</summary>
    public IEventDataService Service => scope.ServiceProvider.GetRequiredService<IEventDataService>();

    /// <summary>The audit ring, so a test can check which database an action was recorded against.</summary>
    public StudioActionLog Audit => scope.ServiceProvider.GetRequiredService<StudioActionLog>();

    /// <summary>A studio scope pointed at one of the two databases, with no tenant pinned.</summary>
    public static StudioScope ScopeFor(IMartenDatabase database) => new("default", database.Id.Identity, null);

    /// <summary>Creates both databases, builds the store over them and seeds one stream into each.</summary>
    /// <param name="containerConnectionString">The container's own connection string.</param>
    /// <param name="prefix">A per-class prefix, so two classes never share a database name.</param>
    public static async Task<MultiDatabaseEventsFixture> CreateAsync(string containerConnectionString, string prefix)
    {
        string connectionA = await CreateDatabaseAsync(containerConnectionString, prefix + "_a");
        string connectionB = await CreateDatabaseAsync(containerConnectionString, prefix + "_b");

        DocumentStore store = DocumentStore.For(options =>
        {
            options.MultiTenantedDatabases(tenancy =>
            {
                // Order matters, and is the point: the first registration also becomes the store's
                // default, which is where an unpinned session goes.
                tenancy.AddSingleTenantDatabase(connectionA, "alpha");
                tenancy.AddSingleTenantDatabase(connectionB, "beta");
            });

            options.DatabaseSchemaName = "events_multi_db";
            options.Events.DatabaseSchemaName = "events_multi_db";
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Events.AddEventType<OrderPlaced>();
            options.Events.AddEventType<ItemAdded>();
            options.Events.AddEventType<OrderShipped>();

            options.Projections.Add(
                new OrderSummaryProjection(), JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var fixture = new MultiDatabaseEventsFixture(store, BuildProvider(store));

        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        fixture.DatabaseA = Find(databases, prefix + "_a");
        fixture.DatabaseB = Find(databases, prefix + "_b");

        await fixture.SeedAsync();

        return fixture;
    }

    /// <summary>A session pinned to one database, for the assertions that read the other side directly.</summary>
    public IDocumentSession SessionFor(IMartenDatabase database) =>
        Store.LightweightSession(SessionOptions.ForDatabase(database));

    public async ValueTask DisposeAsync()
    {
        scope.Dispose();
        await provider.DisposeAsync();
        await Store.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        await SeedOneAsync(DatabaseA, StreamInA, DeadLetterInA, "alpha");
        await SeedOneAsync(DatabaseB, StreamInB, DeadLetterInB, "beta");
    }

    private async Task SeedOneAsync(IMartenDatabase database, Guid streamId, Guid deadLetterId, string label)
    {
        await using IDocumentSession session = SessionFor(database);

        session.Events.StartStream<OrderSummary>(
            streamId,
            new OrderPlaced("customer-" + label),
            new ItemAdded("sku-" + label, 25),
            new OrderShipped("track-" + label));

        // A dead letter each, written straight in rather than produced by a daemon. What is under test
        // here is which database the studio's session lands on, and a daemon would only add a minute of
        // waiting to the same assertion. DeadLetterLiveTests covers the shape a real one has.
        session.Store(new DeadLetterEvent
        {
            Id = deadLetterId,
            ProjectionName = "OrderSummary",
            ShardName = "All",
            Timestamp = DateTimeOffset.UtcNow,
            ExceptionType = "InvalidOperationException",
            ExceptionMessage = "Failure to apply event in " + label,
            EventSequence = 2,
        });

        await session.SaveChangesAsync();
    }

    private static IMartenDatabase Find(IReadOnlyList<IMartenDatabase> databases, string name)
    {
        foreach (IMartenDatabase database in databases)
        {
            if (string.Equals(database.Id.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return database;
            }
        }

        throw new InvalidOperationException(
            $"The store has no database called '{name}'. It has: "
            + string.Join(", ", databases.Select(x => x.Id.Identity)));
    }

    /// <summary>
    /// <c>create database</c> on the container, dropping whatever a previous run left. Outside a
    /// transaction, because Postgres will not create a database inside one.
    /// </summary>
    private static async Task<string> CreateDatabaseAsync(string containerConnectionString, string name)
    {
        var builder = new NpgsqlConnectionStringBuilder(containerConnectionString);

        await using (var admin = new NpgsqlConnection(containerConnectionString))
        {
            await admin.OpenAsync();

            string quoted = MartenStudio.Internal.Sql.SqlIdentifier.Quote(name);

            await using (var drop = new NpgsqlCommand(
                $"drop database if exists {quoted} with (force)", admin))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await using var create = new NpgsqlCommand($"create database {quoted}", admin);
            await create.ExecuteNonQueryAsync();
        }

        builder.Database = name;
        return builder.ConnectionString;
    }

    private static ServiceProvider BuildProvider(IDocumentStore store)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton(store);

        services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.DiscoverTenantIds = false;
        });

        return services.BuildServiceProvider();
    }
}

/// <summary>One store, two databases, built once for the class.</summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class MultiDatabaseStoreFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The store, the two databases and the studio's services over them.</summary>
    internal MultiDatabaseEventsFixture Events { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        Events = await MultiDatabaseEventsFixture.CreateAsync(postgres.ConnectionString, "studio_p4fix_events");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Events is not null)
        {
            await Events.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Every session the Events area opens is pinned to the database the scope selected, not to the store's
/// default (P4 review finding B1).
/// </summary>
public class MultiDatabaseEventsTests(MultiDatabaseStoreFixture fixture) : IClassFixture<MultiDatabaseStoreFixture>
{
    private MultiDatabaseEventsFixture Events => fixture.Events;

    /// <summary>
    /// The premise: the two databases really are two, and the scope selector can tell them apart. If this
    /// ever fails, every other test in the class is asserting nothing.
    /// </summary>
    [PostgresFact]
    public async Task The_store_spans_two_distinct_databases()
    {
        IReadOnlyList<IMartenDatabase> databases = await Events.Store.Storage.AllDatabases();

        databases.Should().HaveCount(2);
        Events.DatabaseA.Id.Identity.Should().NotBe(Events.DatabaseB.Id.Identity);
    }

    /// <summary>
    /// Archiving a stream the visitor is looking at in database B archives it <em>in B</em>, and leaves
    /// database A - the store's default, and where an unpinned session would have gone - untouched.
    /// </summary>
    [PostgresFact]
    public async Task Archiving_a_stream_in_the_second_database_writes_to_that_database_and_not_the_default()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        await Events.Service.ArchiveStreamAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseB), Events.StreamInB.ToString(), token);

        (await IsArchivedAsync(Events.DatabaseB, Events.StreamInB)).Should().BeTrue("the scope named B");
        (await IsArchivedAsync(Events.DatabaseA, Events.StreamInA)).Should().BeFalse(
            "A is the store's default and nothing asked for it");

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Archive stream"
            && x.Succeeded
            && x.DatabaseId == Events.DatabaseB.Id.Identity);
    }

    /// <summary>
    /// Reads go the same way. A stream that exists only in B is found through B's scope and is genuinely
    /// absent through A's - which is what makes the archive assertion above mean anything.
    /// </summary>
    [PostgresFact]
    public async Task A_stream_is_only_visible_through_the_scope_of_the_database_that_holds_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        StreamState throughB = await Events.Service.GetStreamAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseB), Events.StreamInB.ToString(), token);
        StreamState throughA = await Events.Service.GetStreamAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseA), Events.StreamInB.ToString(), token);

        throughB.Exists.Should().BeTrue();
        throughA.Exists.Should().BeFalse();
    }

    /// <summary>
    /// The aggregate replay is a session read, which is exactly the shape that used to ignore the scope:
    /// it would have replayed against the default database and answered "produced nothing".
    /// </summary>
    [PostgresFact]
    public async Task An_aggregate_replay_reads_the_database_the_scope_named()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        AggregateSnapshot fromB = await Events.Service.AggregateAtVersionAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseB),
            Events.StreamInB.ToString(),
            typeof(OrderSummary).FullName!,
            version: 2,
            token);

        fromB.Error.Should().BeNull();
        fromB.Found.Should().BeTrue();
        fromB.Json.Should().Contain("customer-beta");

        AggregateSnapshot fromA = await Events.Service.AggregateAtVersionAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseA),
            Events.StreamInB.ToString(),
            typeof(OrderSummary).FullName!,
            version: 2,
            token);

        fromA.Found.Should().BeFalse("that stream is not in database A at all");
    }

    /// <summary>
    /// The dead-letter list and the discard are both session reads and writes, and both used to land on
    /// the default database.
    /// </summary>
    [PostgresFact]
    public async Task Dead_letters_are_listed_and_discarded_in_the_database_the_scope_named()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        DeadLetterPage inB = await Events.Service.ListDeadLettersAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseB), new DeadLetterQuery(), token);

        inB.Error.Should().BeNull();
        inB.Rows.Should().ContainSingle().Which.Id.Should().Be(Events.DeadLetterInB);

        await Events.Service.DiscardDeadLetterAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseB), Events.DeadLetterInB, token);

        DeadLetterPage afterB = await Events.Service.ListDeadLettersAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseB), new DeadLetterQuery(), token);
        DeadLetterPage afterA = await Events.Service.ListDeadLettersAsync(
            MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseA), new DeadLetterQuery(), token);

        afterB.Rows.Should().BeEmpty();
        afterA.Rows.Should().ContainSingle().Which.Id.Should().Be(
            Events.DeadLetterInA, "A's dead letter is nobody's business here and must survive");
    }

    /// <summary>The count is a raw read on the scope's own connection, and was already right - so it stays.</summary>
    [PostgresFact]
    public async Task The_dead_letter_count_is_per_database_too()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        long? inA = await Events.Service.CountDeadLettersAsync(MultiDatabaseEventsFixture.ScopeFor(Events.DatabaseA), token);

        inA.Should().Be(1);
    }

    private async Task<bool> IsArchivedAsync(IMartenDatabase database, Guid streamId)
    {
        await using IDocumentSession session = Events.SessionFor(database);

        JasperFx.Events.StreamState? state = await session.Events.FetchStreamStateAsync(
            streamId, TestContext.Current.CancellationToken);

        return state?.IsArchived == true;
    }
}
