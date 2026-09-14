using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// "Rewind subscription to this event", against a real daemon.
/// </summary>
/// <remarks>
/// <para>
/// The other dead-letter tests run against <see cref="PoisonStoreFixture" />, which builds a daemon of
/// its own to produce the records and then stops it - so no coordinator is registered and the studio
/// correctly reports that no daemon is hosted here. A rewind is the one dead-letter action that needs the
/// opposite, so this class stands up a real host with
/// <c>AddMarten(...).AddAsyncDaemon(DaemonMode.Solo)</c> and lets <see cref="DaemonAccessor" /> find the
/// coordinator exactly as it will in production. Nothing here builds a daemon on the studio's behalf
/// (hard rule 11).
/// </para>
/// <para>
/// Every wait is a wait-on-condition with a deadline, never a fixed delay: how long a daemon takes to
/// reach an event is a property of the machine, not of the test.
/// </para>
/// </remarks>
public class RewindSubscriptionLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>How long the daemon gets to do anything before the test gives up on it.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private const string Schema = "events_rewind";

    /// <summary>How many streams the seed poisons, and therefore how many dead letters there are.</summary>
    private const int PoisonStreams = 4;

    private IHost? host;
    private IDocumentStore? store;
    private IMartenDatabase? database;

    /// <summary>The scope every call is made in: the default store, its one database, no tenant.</summary>
    private StudioScope Scope { get; set; } = new("default", string.Empty, null);

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        CancellationToken token = TestContext.Current.CancellationToken;

        await postgres.CreateSchemaAsync(Schema, token);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                options.Connection(postgres.ConnectionString);
                options.DatabaseSchemaName = Schema;
                options.Events.DatabaseSchemaName = Schema;
                options.AutoCreateSchemaObjects = AutoCreate.All;

                options.Events.AddEventType<OrderPlaced>();
                options.Events.AddEventType<PoisonPill>();

                // Async, so the daemon runs it and its failures become dead letters - which is the whole
                // reason there is anything here to rewind.
                options.Projections.Add(new PoisonProjection(), ProjectionLifecycle.Async);
            })
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.Solo);

        // What a Blazor circuit supplies and a generic host does not.
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();

        builder.Services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.DiscoverTenantIds = false;
        });

        host = builder.Build();
        store = host.Services.GetRequiredService<IDocumentStore>();

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await SeedAsync(store, token);

        await host.StartAsync(token);

        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();
        database = databases[0];
        Scope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, database.Id.Identity, null);

        var coordinator = host.Services.GetRequiredService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>();

        await WaitForAsync(
            () => Task.FromResult(coordinator.DaemonForMainDatabase().IsRunning),
            "the async daemon to start",
            token);

        // Every poisoned stream, not merely the first: a shard that has only reached the first one has a
        // progression row below the other three, and a test that then asked "is the shard past this dead
        // letter" would be asking before the answer had settled.
        await WaitForAsync(
            async () => await CountDeadLettersAsync(token) >= PoisonStreams,
            $"the daemon to record {PoisonStreams} dead letters",
            token);
    }

    /// <summary>
    /// The rewind moves the shard's progression back below the event that failed, the daemon re-applies
    /// it, and the shard catches up again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-applied event is what is asserted rather than the intermediate progression value, and
    /// deliberately: Marten writes the floor and the daemon restarts the agents inside one call, so a
    /// test that polled for the progression to dip below its old value would be racing a replay of a
    /// hundred events. A <em>new</em> dead letter at the same sequence - a different document id for the
    /// same <c>EventSequence</c> - can only happen if the shard really did go back past it, and Marten
    /// deletes the old record as part of the rewind, so there is no way to see the old one twice.
    /// </para>
    /// <para>
    /// The floor itself is pinned by <c>RewindFloorTests</c> in the fast suite, against a daemon that
    /// records what it was asked for.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_rewind_re_applies_the_event_that_failed_and_the_shard_catches_up()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();
        IProjectionDataService projections = scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        DeadLetterRow letter = await NewestLetterAsync(events, token);

        ProjectionsView view = await projections.GetProjectionsAsync(Scope, token);
        ShardProgress shard = view.Progress.Single(x => x.ShardName.StartsWith(letter.ProjectionName, StringComparison.Ordinal));

        long sequenceBefore = shard.Sequence;
        sequenceBefore.Should().BeGreaterThanOrEqualTo(letter.EventSequence,
            "the shard skipped the poison event and carried on, which is what wrote the dead letter");

        await events.RewindSubscriptionAsync(Scope, letter.ProjectionName, letter.EventSequence, token);

        // The old record is gone the moment the rewind commits: Marten deletes the projection's dead
        // letters at or above the floor, because they are all about to be replayed.
        await WaitForAsync(
            async () => await FindLetterAsync(letter.EventSequence, token) is { } current && current.Id != letter.Id,
            "the daemon to re-apply the poison event and record a new dead letter",
            token);

        await WaitForAsync(
            async () => await ShardSequenceAsync(letter.ProjectionName, token) >= sequenceBefore,
            "the shard to catch up again",
            token);

        StudioActionLogService audit = host.Services.GetRequiredService<StudioActionLogService>();

        audit.GetLatest().Should().Contain(x =>
            x.Action == "Rewind subscription"
            && x.Succeeded
            && x.Target.Contains(letter.ProjectionName, StringComparison.Ordinal)
            && x.Message!.Contains("progression set to", StringComparison.Ordinal));
    }

    /// <summary>
    /// The audit entry names the event the rewind was asked for and the floor it wrote - one below it.
    /// </summary>
    /// <remarks>
    /// The floor is asserted here rather than read back out of <c>mt_event_progression</c>, because the
    /// rewind restarts the shard inside the same call and the replay moves the row on again within
    /// milliseconds: polling for the intermediate value would be racing the daemon. What the audit says
    /// is what the service decided, and what the service hands the daemon is pinned separately by
    /// <c>RewindFloorTests</c> against a daemon that records its arguments.
    /// </remarks>
    [PostgresFact]
    public async Task The_audit_entry_names_the_event_and_the_floor_one_below_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        DeadLetterRow letter = await NewestLetterAsync(events, token);

        await events.RewindSubscriptionAsync(Scope, letter.ProjectionName, letter.EventSequence, token);

        string sequence = letter.EventSequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string floor = (letter.EventSequence - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        StudioActionLogService audit = host.Services.GetRequiredService<StudioActionLogService>();

        audit.GetLatest().Should().Contain(x =>
            x.Action == "Rewind subscription"
            && x.Succeeded
            && x.Target.EndsWith("#" + sequence, StringComparison.Ordinal)
            && x.Message!.Contains("progression set to #" + floor, StringComparison.Ordinal));
    }

    /// <summary>
    /// A projection nothing registers has no shard for the daemon to restart, and Marten says so by name.
    /// The refusal is audited as a failed action rather than swallowed.
    /// </summary>
    [PostgresFact]
    public async Task Rewinding_a_projection_this_store_does_not_have_is_refused_and_audited()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        Func<Task> rewinding = () => events.RewindSubscriptionAsync(Scope, "NoSuchProjection", 5, token);

        await rewinding.Should().ThrowAsync<ArgumentOutOfRangeException>();

        StudioActionLogService audit = host.Services.GetRequiredService<StudioActionLogService>();

        audit.GetLatest().Should().Contain(x =>
            x.Action == "Rewind subscription" && !x.Succeeded && x.Target.StartsWith("NoSuchProjection", StringComparison.Ordinal));
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (host is null)
        {
            return;
        }

        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A daemon that will not stop must not fail a test that already passed.
        }

        host.Dispose();
    }

    private static async Task SeedAsync(IDocumentStore documentStore, CancellationToken token)
    {
        await using IDocumentSession session = documentStore.LightweightSession();

        // Several poisoned streams, so the class's three tests each have something to work with after the
        // ones before them have rewound and replayed.
        for (int index = 0; index < 4; index++)
        {
            session.Events.StartStream(
                Guid.CreateVersion7(),
                new OrderPlaced("customer-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new PoisonPill("a deliberately unapplicable event"));
        }

        await session.SaveChangesAsync(token);
    }

    /// <summary>
    /// The newest dead letter there is, waiting for one if a previous test in this class rewound them all
    /// away and the daemon has not written them back yet.
    /// </summary>
    /// <remarks>
    /// The newest rather than the oldest, because rewinding to it replays the shortest tail - and because
    /// Marten deletes the projection's dead letters at or above the floor, so rewinding to the oldest
    /// would take all four away and leave the other tests in this class waiting for the replay.
    /// </remarks>
    private async Task<DeadLetterRow> NewestLetterAsync(IEventDataService events, CancellationToken token)
    {
        await WaitForAsync(
            async () => await CountDeadLettersAsync(token) > 0,
            "a dead letter to be there",
            token);

        DeadLetterPage page = await events.ListDeadLettersAsync(Scope, new DeadLetterQuery(), token);

        page.Error.Should().BeNull();
        page.Rows.Should().NotBeEmpty();

        return page.Rows.OrderByDescending(x => x.EventSequence).First();
    }

    private async Task<long> CountDeadLettersAsync(CancellationToken token)
    {
        await using IQuerySession session = store!.QuerySession();
        return await session.Query<DeadLetterEvent>().CountAsync(token);
    }

    private async Task<DeadLetterEvent?> FindLetterAsync(long sequence, CancellationToken token)
    {
        await using IQuerySession session = store!.QuerySession();

        return await session.Query<DeadLetterEvent>()
            .Where(x => x.EventSequence == sequence)
            .FirstOrDefaultAsync(token);
    }

    private async Task<long> ShardSequenceAsync(string projectionName, CancellationToken token)
    {
        IReadOnlyList<ShardState> rows = await database!.AllProjectionProgress(token);

        ShardState? row = rows.FirstOrDefault(
            x => x.ShardName.StartsWith(projectionName, StringComparison.OrdinalIgnoreCase));

        return row?.Sequence ?? 0;
    }

    /// <summary>
    /// Waits until <paramref name="condition" /> holds, or fails naming what it was waiting for.
    /// </summary>
    /// <remarks>
    /// Wait on the condition, never on a fixed delay (AGENTS.md's testing section). The poll is a
    /// <see cref="PeriodicTimer" /> rather than a chain of <c>Task.Delay</c> so the wait costs one timer
    /// rather than a task per tick.
    /// </remarks>
    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Waited {Deadline.TotalSeconds:0} s for {what} and it did not happen.");
            }

            await timer.WaitForNextTickAsync(cancellationToken);
        }
    }
}
