using Marten;

using MartenStudio.Services.Events;
using MartenStudio.Services.Health.Checks;

using Npgsql;

using static MartenStudio.Integration.Tests.Events.EventShapeLiveSupport;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// 1.2-SEC-FIX: time travel and the stepper never register an unmapped alias on the host's store. A stream holds an event whose
/// <c>type</c> no mapping answers to and whose <c>mt_dotnet_type</c> names a type this process has loaded - exactly what Marten's own
/// resolver would load and register on first read, silencing E05 until a restart. The replay and the stepper refuse to fold it, say
/// so, and leave <c>AllKnownEventTypes()</c> and E05's own alias predicate as they were. (The events' shape check's equivalent is
/// <see cref="EventShapeCheckLiveTests" />.)
/// </summary>
public class UnmappedEventReplayLiveTests(UnmappedEventReplayLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<UnmappedEventReplayLiveTests.Fixture>
{
    private const string Alias = "replay_loadable_v1";

    private readonly Fixture data = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Ledger => typeof(UnmappedReplayLedger).FullName!;

    /// <summary>The store: a ledger folded from two mapped events, and one stream with an unmapped event between them.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>Opened (1), credited (2), the unmapped event (3), credited (4).</summary>
        public Guid Stream { get; } = Guid.NewGuid();

        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Events.MapEventType<UnmappedReplayOpened>("replay_opened");
            options.Events.MapEventType<UnmappedReplayCredited>("replay_credited");
            options.Projections.Snapshot<UnmappedReplayLedger>(JasperFx.Events.Projections.SnapshotLifecycle.Inline);
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;

            await using (IDocumentSession session = Marten.Store.LightweightSession())
            {
                session.Events.StartStream<UnmappedReplayLedger>(Stream, new UnmappedReplayOpened("acme"), new UnmappedReplayCredited(5));
                await session.SaveChangesAsync(token);
            }

            await using NpgsqlConnection connection = await Postgres.OpenAsync(token);

            await InsertAsync(
                connection, Schema, Stream, 3, Alias, typeof(UnmappedReplayLoadable).FullName + ", " + typeof(UnmappedReplayLoadable).Assembly.GetName().Name,
                "{\"Amount\":3}", null, token);
            await InsertAsync(
                connection, Schema, Stream, 4, "replay_credited", typeof(UnmappedReplayCredited).FullName + ", " + typeof(UnmappedReplayCredited).Assembly.GetName().Name,
                "{\"Amount\":4}", null, token);
        }
    }

    [PostgresFact]
    public async Task Time_travel_folds_up_to_an_unmapped_event_and_refuses_it_without_registering_its_type()
    {
        var events = Store.Options.Events;
        string[] before = [.. events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal)];

        EventHealth.KnownAliases(events)!(Alias).Should().BeFalse("the arrangement: nothing maps this alias");

        using MartenFixture.ScopedService<IEventDataService> service = Marten.Resolve<IEventDataService>();
        string stream = data.Stream.ToString();

        AggregateSnapshot upTo = await service.Service.AggregateAtVersionAsync(Scope, stream, Ledger, 2, Token);

        upTo.Error.Should().BeNull();
        upTo.Found.Should().BeTrue("versions 1 and 2 are mapped events: read and folded as ever");
        upTo.Json.Should().Contain("\"Balance\":5");

        foreach (long version in new long[] { 3, 0 })
        {
            AggregateSnapshot past = await service.Service.AggregateAtVersionAsync(Scope, stream, Ledger, version, Token);

            past.Found.Should().BeFalse();
            past.Error.Should().NotBeNull($"version {version} reaches the unmapped event");
            past.Error!.Message.Should().Contain("not mapped in this process; not folded").And.Contain(Alias).And.Contain("version 3");
        }

        // The point: a press registered nothing. E05's own predicate still calls the alias unmapped.
        events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal).Should().Equal(before);
        EventHealth.KnownAliases(events)!(Alias).Should().BeFalse();
    }

    [PostgresFact]
    public async Task The_stepper_and_its_search_fold_nothing_past_an_unmapped_event_and_register_nothing()
    {
        var events = Store.Options.Events;
        string[] before = [.. events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal)];

        EventHealth.KnownAliases(events)!(Alias).Should().BeFalse("the arrangement: nothing maps this alias");

        using MartenFixture.ScopedService<IEventDataService> service = Marten.Resolve<IEventDataService>();
        string stream = data.Stream.ToString();

        StreamStepWindow window = await service.Service.StepAsync(Scope, stream, Ledger, 1, Token);

        window.Error.Should().NotBeNull("the window reads the unmapped event");
        window.Error!.Message.Should().Contain("not mapped in this process; not folded").And.Contain(Alias);
        window.Steps.Should().BeEmpty();

        StepSearch search = await service.Service.FindNextChangeAsync(Scope, stream, Ledger, 2, "Balance", Token);

        search.Error.Should().NotBeNull("the search from version 2 reads the unmapped event next");
        search.Error!.Message.Should().Contain("not mapped in this process; not folded");

        events.AllKnownEventTypes().Select(static x => x.EventTypeName).Order(StringComparer.Ordinal).Should().Equal(before);
        EventHealth.KnownAliases(events)!(Alias).Should().BeFalse();
    }
}

/// <summary>Opens a ledger.</summary>
/// <param name="Owner">Whose.</param>
public sealed record UnmappedReplayOpened(string Owner);

/// <summary>Credits a ledger.</summary>
/// <param name="Amount">How much.</param>
public sealed record UnmappedReplayCredited(int Amount);

/// <summary>A loadable event type no store registers: what the unmapped alias's stored .NET type names.</summary>
public sealed class UnmappedReplayLoadable
{
    /// <summary>How much.</summary>
    public int Amount { get; set; }
}

/// <summary>The aggregate the unmapped event sits in the middle of.</summary>
public sealed class UnmappedReplayLedger
{
    /// <summary>The stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Whose.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The sum of the credits.</summary>
    public int Balance { get; set; }

    /// <summary>Opens it.</summary>
    public static UnmappedReplayLedger Create(UnmappedReplayOpened opened) => new() { Owner = opened.Owner };

    /// <summary>Credits it.</summary>
    public void Apply(UnmappedReplayCredited credited) => Balance += credited.Amount;
}
