using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// M1, live: the stepper over a real event store, driven through the studio's own <see cref="IEventDataService" /> - a long
/// stream stepped window by window, a stream whose fourth event the aggregate cannot apply, and the dead letter the async
/// daemon recorded for that very event.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate is a self-aggregating <see cref="StepperLedger" /> registered async, so the same <c>Apply</c> that throws in
/// the stepper's fold threw in the daemon first, and Marten's own continuous error policy recorded the
/// <c>DeadLetterEvent</c> - a real one, not a document written to agree with the test. The daemon runs once, in the
/// fixture (hard rule 11 is about studio code; this fixture owns its store and no other daemon exists).
/// </para>
/// <para>
/// Every step is compared with time travel's replay to the same version (<see cref="IEventDataService.AggregateAtVersionAsync" />,
/// Marten's whole fold), which is the claim the stepper makes. The class runs at whichever Marten the suite is built with;
/// run it at the floor and with <c>-p:MartenStudioMartenVersion=&lt;latest&gt;</c>.
/// </para>
/// </remarks>
public class StreamStepperLiveTests(StepperStoreFixture fixture) : IClassFixture<StepperStoreFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Ledger => typeof(StepperLedger).FullName!;

    private IEventDataService Events => fixture.Service;

    private StudioScope Scope => fixture.Scope;

    [PostgresFact]
    public async Task A_long_stream_is_stepped_window_by_window_and_each_step_is_time_travels_aggregate()
    {
        string stream = fixture.LongStream.ToString();

        StreamStepWindow first = await Events.StepAsync(Scope, stream, Ledger, 1, Token);

        first.Error.Should().BeNull();
        first.Mode.Should().Be(AggregateStepMode.EventByEvent, "a generated Create/Apply dispatch folds one event at a time");
        first.Steps.Select(static x => x.Version).Should().Equal(Enumerable.Range(1, 20).Select(static x => (long) x));
        first.StateBeforeJson.Should().BeNull();
        first.HasEarlier.Should().BeFalse();
        first.HasMore.Should().BeTrue();
        first.Steps[0].StateJson.Should().Contain("\"Owner\":\"acme\"").And.Contain(stream, "a step carries the stream's id, as time travel does");
        first.Steps[1].Changes.Should().Be(2, "a credit moves the balance and the count");

        StreamStepWindow last = await Events.StepAsync(Scope, stream, Ledger, StepperStoreFixture.LongLength, Token);

        last.Steps.Select(static x => x.Version).Should().Equal([41L, 42L, 43L, 44L, 45L]);
        last.HasEarlier.Should().BeTrue();
        last.HasMore.Should().BeFalse();
        last.StateBeforeJson.Should().Be(await TimeTravelAsync(stream, 40), "the events before the window were folded, not shown");

        foreach (AggregateStep step in first.Steps.Concat(last.Steps))
        {
            step.StateJson.Should().Be(await TimeTravelAsync(stream, step.Version), "version {0}", step.Version);
            step.EventJson.Should().NotBeNullOrEmpty();
            step.Failure.Should().BeNull();
        }
    }

    /// <summary>
    /// The packet's three live claims at once: a real stream, a fold that throws on its fourth event - where the stepper
    /// stops - and the dead letter the daemon recorded for that event, linked from the step and found by the link.
    /// </summary>
    [PostgresFact]
    public async Task The_stepper_stops_on_the_event_that_throws_and_links_the_dead_letter_the_daemon_recorded_for_it()
    {
        string stream = fixture.JammedStream.ToString();

        StreamStepWindow window = await Events.StepAsync(Scope, stream, Ledger, 5, Token);

        window.Error.Should().BeNull();
        window.Steps.Select(static x => x.Version).Should().Equal([1L, 2L, 3L, 4L], "the fold stops at the event that threw");
        window.StoppedAt.Should().Be(4);
        window.HasMore.Should().BeFalse("nothing after the failure is folded, though the stream goes on");

        AggregateStep jammed = window.Steps[^1];
        jammed.Failure.Should().NotBeNull();
        jammed.Failure!.ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
        jammed.Failure.Message.Should().Be("globex's ledger is jammed: " + StepperStoreFixture.JamReason);
        jammed.StateJson.Should().BeNull("a step whose fold threw has no aggregate after it");
        window.Steps[^2].StateJson.Should().Contain("\"Balance\":15", "the aggregate before the failure is the page's to show");

        StepDeadLetter letter = jammed.DeadLetters.Should().ContainSingle("the daemon dead-lettered exactly this event").Subject;
        letter.ProjectionName.Should().Contain(nameof(StepperLedger), "the ledger's own async projection recorded it");
        letter.ExceptionType.Should().Contain(nameof(InvalidOperationException));
        window.Steps.Take(3).Should().OnlyContain(static x => x.DeadLetters.Count == 0, "no other event of the stream failed");

        // The link from the step: the dead-letter screen's filter by the event's sequence finds that one dead letter.
        DeadLetterPage linked = await Events.ListDeadLettersAsync(Scope, new DeadLetterQuery { EventSequence = jammed.Sequence }, Token);
        linked.Rows.Should().ContainSingle().Which.Id.Should().Be(letter.Id);

        // And the link back: the dead letter names the event, whose version is the step.
        EventRow? offending = await Events.GetEventBySequenceAsync(Scope, linked.Rows[0].EventSequence, Token);
        offending!.StreamId.Should().Be(stream);
        offending.Version.Should().Be(4);

        AggregateSnapshot timeTravel = await Events.AggregateAtVersionAsync(Scope, stream, Ledger, 4, Token);
        timeTravel.Error.Should().NotBeNull("time travel to the same version fails on the same event - the stepper attributes it");
    }

    /// <summary>A step past the failure is answered with the window that holds it: the stepper cannot fold beyond it.</summary>
    [PostgresFact]
    public async Task A_step_past_the_failure_is_answered_with_the_window_that_holds_the_failure()
    {
        StreamStepWindow window = await Events.StepAsync(Scope, fixture.JammedStream.ToString(), Ledger, 25, Token);

        window.RequestedVersion.Should().Be(25);
        window.StoppedAt.Should().Be(4);
        window.Steps.Select(static x => x.Version).Should().Equal([1L, 2L, 3L, 4L]);
    }

    [PostgresFact]
    public async Task The_next_change_of_a_value_is_found_and_a_failure_on_the_way_is_where_the_search_stops()
    {
        string longStream = fixture.LongStream.ToString();

        StepSearch balance = await Events.FindNextChangeAsync(Scope, longStream, Ledger, 30, "Balance", Token);
        StepSearch owner = await Events.FindNextChangeAsync(Scope, longStream, Ledger, 1, "$.Owner", Token);
        StepSearch jammed = await Events.FindNextChangeAsync(Scope, fixture.JammedStream.ToString(), Ledger, 2, "Owner", Token);

        balance.FoundVersion.Should().Be(31);
        balance.Path.Should().Be("$.Balance");

        owner.FoundVersion.Should().BeNull("nothing after the first event changes the owner");
        owner.ScannedThrough.Should().Be(StepperStoreFixture.LongLength);
        owner.ReachedLimit.Should().BeFalse();

        jammed.FoundVersion.Should().Be(4);
        jammed.FoundFailure.Should().BeTrue();
    }

    /// <summary>
    /// Bounded in time: a fold that runs past <c>QueryTimeout</c> - here a projection that takes 60 ms an event, over the
    /// forty events before the last window - is stopped and answered as a value with a Retry, never a crash.
    /// </summary>
    [PostgresFact]
    public async Task A_fold_past_the_query_timeout_is_a_value_with_a_retry()
    {
        await using ServiceProvider impatient = fixture.Studio(static options => options.QueryTimeout = TimeSpan.FromSeconds(1));
        await using AsyncServiceScope scope = impatient.CreateAsyncScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        StreamStepWindow window = await events.StepAsync(
            Scope, fixture.LongStream.ToString(), typeof(StepperSlowLedger).FullName!, StepperStoreFixture.LongLength, Token);

        window.Error.Should().NotBeNull();
        window.Error!.Message.Should().Contain("query timeout").And.Contain("MartenStudioOptions.QueryTimeout");
        window.Error.CanRetry.Should().BeTrue();
        window.Steps.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_type_the_store_does_not_offer_and_a_stream_it_does_not_have_are_refused_as_time_travel_refuses_them()
    {
        StreamStepWindow unknown = await Events.StepAsync(Scope, fixture.LongStream.ToString(), "System.Object", 1, Token);
        StreamStepWindow missing = await Events.StepAsync(Scope, Guid.NewGuid().ToString(), Ledger, 1, Token);

        unknown.Error!.Message.Should().Contain("is not one of this store's aggregate types");
        missing.Missing.Should().Be(AggregateMissingReason.StreamMissing);
    }

    private async Task<string?> TimeTravelAsync(string stream, long version) =>
        (await Events.AggregateAtVersionAsync(Scope, stream, Ledger, version, Token)).Json;
}

/// <summary>Opens a ledger.</summary>
/// <param name="Owner">Whose.</param>
public record StepperOpened(string Owner);

/// <summary>Credits it.</summary>
/// <param name="Amount">How much.</param>
public record StepperCredited(int Amount);

/// <summary>An event the ledger cannot apply.</summary>
/// <param name="Reason">What the exception says.</param>
public record StepperJammed(string Reason);

/// <summary>
/// A self-aggregating ledger whose <c>Apply</c> throws on <see cref="StepperJammed" /> - in the daemon, which dead-letters
/// the event, and in the stepper's fold, which stops on it.
/// </summary>
public class StepperLedger
{
    /// <summary>The stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Whose.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The sum of the credits.</summary>
    public int Balance { get; set; }

    /// <summary>How many credits.</summary>
    public int Credits { get; set; }

    /// <summary>Opens it.</summary>
    public static StepperLedger Create(StepperOpened opened) => new() { Owner = opened.Owner };

    /// <summary>Credits it.</summary>
    public void Apply(StepperCredited credited)
    {
        Balance += credited.Amount;
        Credits++;
    }

    /// <summary>Throws, every time.</summary>
    public void Apply(StepperJammed jammed) =>
        throw new InvalidOperationException(Owner + "'s ledger is jammed: " + jammed.Reason);
}

/// <summary>
/// A document type that folds the same events slowly, for the timeout: the stepper offers it as "try any type".
/// </summary>
public class StepperSlowLedger
{
    /// <summary>The stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Whose.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The sum of the credits.</summary>
    public int Balance { get; set; }

    /// <summary>Opens it.</summary>
    public static StepperSlowLedger Create(StepperOpened opened) => new() { Owner = opened.Owner };

    /// <summary>Credits it, slowly - a projection that blocks, as a real one reading something remote might.</summary>
    public void Apply(StepperCredited credited)
    {
        Thread.Sleep(60);
        Balance += credited.Amount;
    }
}

/// <summary>
/// The stepper's store: one schema, the ledger registered async, two streams, and a daemon run once until it has
/// dead-lettered the jammed stream's fourth event.
/// </summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class StepperStoreFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>How many events the long stream has: two windows and a part.</summary>
    public const int LongLength = 45;

    /// <summary>What the jammed event says.</summary>
    public const string JamReason = "a deliberately unapplicable event";

    private const string Schema = "events_stepper";

    private static readonly TimeSpan DaemonDeadline = TimeSpan.FromSeconds(60);

    private DocumentStore? store;
    private ServiceProvider? provider;
    private IServiceScope? scope;

    /// <summary>A stream of <see cref="LongLength" /> events.</summary>
    public Guid LongStream { get; } = Guid.CreateVersion7();

    /// <summary>Opened, credited twice, jammed, credited.</summary>
    public Guid JammedStream { get; } = Guid.CreateVersion7();

    /// <summary>The scope every call is made in.</summary>
    internal StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>The studio's event service over the store, every capability off: the stepper needs none.</summary>
    internal IEventDataService Service => scope!.ServiceProvider.GetRequiredService<IEventDataService>();

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema);

        store = DocumentStore.For(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Events.AddEventType<StepperOpened>();
            options.Events.AddEventType<StepperCredited>();
            options.Events.AddEventType<StepperJammed>();

            options.Projections.Snapshot<StepperLedger>(SnapshotLifecycle.Async);
            options.Schema.For<StepperSlowLedger>();
        });

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await using (IDocumentSession session = store.LightweightSession())
        {
            List<object> events = [new StepperOpened("acme")];
            events.AddRange(Enumerable.Range(2, LongLength - 1).Select(static x => (object) new StepperCredited(x)));
            session.Events.StartStream<StepperLedger>(LongStream, events);

            session.Events.StartStream<StepperLedger>(
                JammedStream,
                new StepperOpened("globex"),
                new StepperCredited(10),
                new StepperCredited(5),
                new StepperJammed(JamReason),
                new StepperCredited(1));

            await session.SaveChangesAsync();
        }

        await RunDaemonUntilDeadLetteredAsync(store);

        provider = Studio(static _ => { });
        scope = provider.CreateScope();
    }

    /// <summary>A studio over the store, configured as the test needs: the class's own with no capability at all.</summary>
    internal ServiceProvider Studio(Action<MartenStudioOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton<IDocumentStore>(store!);
        services.AddMartenStudio(options =>
        {
            options.DiscoverTenantIds = false;
            configure(options);
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs the daemon until Marten has recorded the jammed event's dead letter - a wait on the condition with a deadline,
    /// never a fixed sleep.
    /// </summary>
    private static async Task RunDaemonUntilDeadLetteredAsync(DocumentStore documentStore)
    {
        using IProjectionDaemon daemon = await documentStore.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            using var poll = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            using var deadline = new CancellationTokenSource(DaemonDeadline);

            while (await poll.WaitForNextTickAsync(deadline.Token))
            {
                await using IQuerySession session = documentStore.QuerySession();

                if (await session.Query<DeadLetterEvent>().CountAsync(deadline.Token) > 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The projection daemon did not dead-letter the jammed event within {DaemonDeadline}: the ledger is registered "
                + "async and throws on it, and Marten's continuous error policy skips apply errors by default.");
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        scope?.Dispose();

        if (provider is not null)
        {
            await provider.DisposeAsync();
        }

        if (store is not null)
        {
            await store.DisposeAsync();
        }
    }
}
