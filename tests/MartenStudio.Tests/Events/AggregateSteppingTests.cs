using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Aggregation;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Sql;

using PostgresStudio.Services.Json;

namespace MartenStudio.Tests.Events;

/// <summary>
/// M1: the stepper folds a stream one event at a time with the invoker's one fold, and each step is the aggregate Marten's
/// own single fold of the stream up to it would be - for every dispatch the packet names (<c>Create</c>/<c>Apply</c>,
/// <c>ShouldDelete</c>, <c>Evolve</c>, <c>EvolveAsync</c>, a self-aggregating <c>Snapshot&lt;T&gt;</c>), at whichever Marten
/// this run restores. Where it cannot be - a projection of the host's own <c>DetermineAction</c>, or a
/// <c>DeleteEvent&lt;T&gt;()</c> registration - it folds from the start and says so, and still agrees.
/// </summary>
/// <remarks>
/// No database: <c>DocumentStore.For</c> connects to nothing and nothing here runs a statement, so the unreachable
/// connection string would fail any that did. The comparison is time travel's own replay
/// (<see cref="AggregateStreamInvoker.AggregateAtVersionAsync" />, Marten's <c>BuildAsync</c> over every event up to a
/// version), serialized with the store's own serializer.
/// </remarks>
public class AggregateSteppingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Shapes => ["create-apply", "should-delete", "evolve", "evolve-async", "self-aggregating"];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Each_step_is_Martens_own_fold_of_the_stream_up_to_it(string shape)
    {
        (Type type, Action<StoreOptions> register) = Shape(shape);
        await using DocumentStore store = Store(register);
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();
        IReadOnlyList<IEvent> events = Script(stream);

        IReadOnlyList<string?> stepped = await WalkAsync(store, session, type, stream, events);

        stepped.Should().HaveCount(events.Count);

        for (int version = 1; version <= events.Count; version++)
        {
            stepped[version - 1].Should().Be(
                await WholeFoldAsync(store, session, type, stream, events, version),
                "folding events 1..{0} one at a time is folding them all at once, for the {1} shape", version, shape);
        }

        AggregateStreamInvoker.FolderFor(type).ShapeOf(session).Mode.Should().Be(AggregateStepMode.EventByEvent);
    }

    /// <summary>The deletes and the re-creation the script holds are seen, so the theory above is not vacuous for them.</summary>
    [Fact]
    public async Task ShouldDelete_deletes_mid_stream_and_a_later_create_starts_again()
    {
        (Type type, Action<StoreOptions> register) = Shape("should-delete");
        await using DocumentStore store = Store(register);
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();

        IReadOnlyList<string?> stepped = await WalkAsync(store, session, type, stream, Script(stream));

        stepped[3].Should().BeNull("event 4 is the cancellation ShouldDelete answers true for");
        stepped[4].Should().NotBeNull("event 5 opens the tally again");
        stepped[4].Should().Contain("\"Opened\":2");
    }

    /// <summary>
    /// A thousand events, every one compared: the identity holds over a long stream too, and the walk keeps nothing but the
    /// aggregate (the cost on a 1,000-event stream is Step 0's measurement; this is its correctness half).
    /// </summary>
    [Fact]
    public async Task A_thousand_events_stepped_one_at_a_time_agree_with_the_whole_fold_at_every_version()
    {
        (Type type, Action<StoreOptions> register) = Shape("create-apply");
        await using DocumentStore store = Store(register);
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();

        List<IEvent> events = [Event(stream, 1, new StepOpened("acme"))];
        for (int version = 2; version <= 1_000; version++)
        {
            events.Add(Event(stream, version, new StepCredited(version % 13)));
        }

        IReadOnlyList<string?> stepped = await WalkAsync(store, session, type, stream, events);

        for (int version = 1; version <= events.Count; version++)
        {
            stepped[version - 1].Should().Be(await WholeFoldAsync(store, session, type, stream, events, version));
        }
    }

    [Fact]
    public async Task A_projection_with_its_own_DetermineAction_is_folded_from_the_start_and_says_why()
    {
        await using DocumentStore store = Store(static options => options.Projections.Add(new StepBatchProjection(), ProjectionLifecycle.Inline));
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();
        IReadOnlyList<IEvent> events = Script(stream);

        AggregateStepShape shape = AggregateStreamInvoker.FolderFor(typeof(StepTally)).ShapeOf(session);

        shape.Mode.Should().Be(AggregateStepMode.FromTheStart);
        shape.Reason.Should().Contain("StepBatchProjection").And.Contain("DetermineAction");

        IReadOnlyList<string?> stepped = await WalkAsync(store, session, typeof(StepTally), stream, events);

        for (int version = 1; version <= events.Count; version++)
        {
            stepped[version - 1].Should().Be(await WholeFoldAsync(store, session, typeof(StepTally), stream, events, version));
        }

        stepped[^1].Should().Contain("\"Batches\":1", "each step is one batch - the stream up to it - as Marten's own fold is");
    }

    /// <summary>
    /// <c>DeleteEvent&lt;T&gt;()</c> has two meanings in Marten, and the walk agrees with both. On the <c>Evolve</c> path the
    /// batch fold answers nothing for any batch that holds one, whatever came before or comes after it
    /// (<c>buildActionAsync</c>'s <c>MatchesAnyDeleteType</c>) - where one event at a time would carry the state on. For a
    /// projection whose dispatch is generated, JasperFx's generator turns a constructor's registration into a delete arm
    /// of its own (<c>EvolverCodeEmitter</c>, read at <c>V2.74.0</c>): the aggregate is deleted at the event, and a later
    /// event starts a new one. The walk cannot tell the two apart from outside, so at such an event it folds from the start,
    /// which is exact for both.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_delete_event_moves_the_walk_to_folding_from_the_start_and_it_still_agrees(bool evolve)
    {
        await using DocumentStore store = Store(evolve
            ? static options => options.Projections.Add(new StepDeletingEvolveProjection(), ProjectionLifecycle.Inline)
            : static options => options.Projections.Add(new StepDeletingProjection(), ProjectionLifecycle.Inline));
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();

        IEvent[] events =
        [
            Event(stream, 1, new StepOpened("acme")),
            Event(stream, 2, new StepCredited(5)),
            Event(stream, 3, new StepClosed("done")),
            Event(stream, 4, new StepCredited(7)),
        ];

        var walk = new AggregateWalk(
            AggregateStreamInvoker.FolderFor(typeof(StepTally)), session, store.Options.Serializer(), stream);

        walk.Mode.Should().Be(AggregateStepMode.EventByEvent, "nothing about the projection says otherwise until the event");

        List<string?> stepped = [];
        foreach (IEvent next in events)
        {
            (await walk.FeedAsync(next, Token)).Kind.Should().Be(WalkOutcomeKind.Folded);
            stepped.Add(walk.StateJson());
        }

        walk.Mode.Should().Be(AggregateStepMode.FromTheStart);
        walk.ModeReason.Should().Contain("DeleteEvent<T>()");

        for (int version = 1; version <= events.Length; version++)
        {
            stepped[version - 1].Should().Be(await WholeFoldAsync(store, session, typeof(StepTally), stream, events, version));
        }

        stepped[1].Should().NotBeNull();
        stepped[2].Should().BeNull("Marten's own fold of the stream up to the delete event is nothing");

        if (evolve)
        {
            stepped[3].Should().BeNull("on the Evolve path a batch that holds the delete event is nothing, whatever follows it");
        }
        else
        {
            stepped[3].Should().Contain("\"Balance\":7", "the generated delete arm deletes at the event, and a later one starts again");
        }
    }

    /// <summary>
    /// Stop on the throw, and attribute it without guessing: the event folded when the fold threw is the event, and the
    /// step shows what the projection threw, not Marten's <see cref="ApplyEventException" /> wrapper around it.
    /// </summary>
    [Theory]
    [InlineData("create-apply")]
    [InlineData("evolve")]
    public async Task The_event_whose_fold_throws_is_the_failure_with_the_projections_own_exception(string shape)
    {
        (Type type, Action<StoreOptions> register) = Shape(shape);
        await using DocumentStore store = Store(register);
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();

        var walk = new AggregateWalk(AggregateStreamInvoker.FolderFor(type), session, store.Options.Serializer(), stream);

        (await walk.FeedAsync(Event(stream, 1, new StepOpened("acme")), Token)).Kind.Should().Be(WalkOutcomeKind.Folded);
        (await walk.FeedAsync(Event(stream, 2, new StepCredited(5)), Token)).Kind.Should().Be(WalkOutcomeKind.Folded);
        string? before = walk.StateJson();

        WalkOutcome jammed = await walk.FeedAsync(Event(stream, 3, new StepJammed("the third one")), Token);

        jammed.Kind.Should().Be(WalkOutcomeKind.Failed);
        jammed.Failure.Should().Be(new StepFailure(typeof(InvalidOperationException).FullName!, "Jammed: the third one"));
        before.Should().Contain("\"Balance\":5", "the step before the failure is what the page shows beside it");

        Func<Task> whole = () => AggregateStreamInvoker.AggregateAtVersionAsync(
            session, type, stream, [Event(stream, 1, new StepOpened("acme")), Event(stream, 2, new StepCredited(5)), Event(stream, 3, new StepJammed("x"))], 0, Token);

        (await whole.Should().ThrowAsync<ApplyEventException>("Marten's own fold to version 3 throws too, naming the event"))
            .Which.Event.Version.Should().Be(3);
    }

    /// <summary>
    /// The budget is looked at before every event - a generated dispatch never looks at it - and running out of it is a
    /// cancellation, which the service turns into "past the query timeout", never the event's failure.
    /// </summary>
    [Fact]
    public async Task A_spent_budget_stops_the_walk_before_the_next_event_and_is_not_that_events_failure()
    {
        (Type type, Action<StoreOptions> register) = Shape("create-apply");
        await using DocumentStore store = Store(register);
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();

        var walk = new AggregateWalk(AggregateStreamInvoker.FolderFor(type), session, store.Options.Serializer(), stream);
        (await walk.FeedAsync(Event(stream, 1, new StepOpened("acme")), Token)).Kind.Should().Be(WalkOutcomeKind.Folded);

        using var spent = new CancellationTokenSource();
        await spent.CancelAsync();

        Func<Task> next = () => walk.FeedAsync(Event(stream, 2, new StepCredited(5)), spent.Token);

        await next.Should().ThrowAsync<OperationCanceledException>();
        walk.Count.Should().Be(1, "the event after the spent budget was not folded");
    }

    [Fact]
    public void A_failure_is_what_the_projection_threw_and_not_Martens_wrapper()
    {
        var inner = new InvalidOperationException("the projection said no");
        var wrapped = new ApplyEventException(Event(Guid.NewGuid(), 7, new StepCredited(1)), inner);

        StepFailure.From(wrapped).Should().Be(new StepFailure("System.InvalidOperationException", "the projection said no"));
        StepFailure.From(new TimeoutException("slow")).Should().Be(new StepFailure("System.TimeoutException", "slow"));
    }

    /// <summary>A walk from the start keeps the events it refolds, so it has a bound, and past it says so rather than folding.</summary>
    [Fact]
    public async Task Folding_from_the_start_refuses_past_its_limit()
    {
        await using DocumentStore store = Store(static options => options.Projections.Add(new StepBatchProjection(), ProjectionLifecycle.Inline));
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();

        var walk = new AggregateWalk(
            AggregateStreamInvoker.FolderFor(typeof(StepTally)), session, store.Options.Serializer(), stream, limit: 3);

        for (int version = 1; version <= 3; version++)
        {
            object data = version == 1 ? new StepOpened("acme") : new StepCredited(version);
            (await walk.FeedAsync(Event(stream, version, data), Token)).Kind.Should().Be(WalkOutcomeKind.Folded);
        }

        WalkOutcome fourth = await walk.FeedAsync(Event(stream, 4, new StepCredited(4)), Token);

        fourth.Kind.Should().Be(WalkOutcomeKind.Refused);
        fourth.Refusal.Should().Contain("first 3 events");
    }

    /// <summary>
    /// The stepper draws a step with the stream's id on it, as time travel does, and leaves the aggregate as the fold left
    /// it: an <c>Apply</c> that reads the id sees what Marten's single fold would have shown it - the id is set only after.
    /// </summary>
    [Fact]
    public async Task A_step_carries_the_streams_id_and_the_fold_never_sees_it()
    {
        await using DocumentStore store = Store(static options => options.Projections.Snapshot<StepIdWatcher>(SnapshotLifecycle.Inline));
        await using IQuerySession session = store.QuerySession();
        Guid stream = Guid.NewGuid();

        IEvent[] events = [Event(stream, 1, new StepOpened("acme")), Event(stream, 2, new StepCredited(1)), Event(stream, 3, new StepCredited(2))];

        IReadOnlyList<string?> stepped = await WalkAsync(store, session, typeof(StepIdWatcher), stream, events);

        string? whole = await WholeFoldAsync(store, session, typeof(StepIdWatcher), stream, events, 3);

        stepped[^1].Should().Contain(stream.ToString(), "the step is drawn with the stream's id, as time travel draws it");
        stepped[^1].Should().Be(whole, "each Apply saw the id Marten's own single fold showed it, not one the studio put there");

        if (whole!.Contains("\"SeenIds\":[\"" + Guid.Empty + "\",\"" + Guid.Empty + "\"]", StringComparison.Ordinal))
        {
            // Where Marten's fold leaves the id unset until it is done, this is the case the restore exists for: without it
            // the second Apply would have seen the id drawn onto the first step.
            stepped[^1].Should().NotContain("\"SeenIds\":[\"" + Guid.Empty + "\",\"" + stream + "\"]");
        }
    }

    [Theory]
    [InlineData("total", "$.total")]
    [InlineData("$.total", "$.total")]
    [InlineData(".items[0].sku", "$.items[0].sku")]
    [InlineData("  $.items[1]  ", "$.items[1]")]
    public void A_typed_path_is_read_in_every_spelling_a_visitor_copies(string typed, string display)
    {
        StepPaths.TryParse(typed, out IReadOnlyList<JsonPathSegment> segments).Should().BeTrue();
        StepPaths.Display(segments).Should().Be(display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("$")]
    [InlineData("$.")]
    public void A_blank_path_names_nothing(string typed) =>
        StepPaths.TryParse(typed, out _).Should().BeFalse();

    [Fact]
    public void A_value_and_its_absence_are_told_apart_and_an_absent_aggregate_is_an_absent_value()
    {
        StepPaths.TryParse("items[1].sku", out IReadOnlyList<JsonPathSegment> sku).Should().BeTrue();
        StepPaths.TryParse("total", out IReadOnlyList<JsonPathSegment> total).Should().BeTrue();

        const string Json = """{"total":10,"items":[{"sku":"a"},{"sku":"b"}]}""";

        StepPaths.ValueAt(Json, sku).Should().Be("\"b\"");
        StepPaths.ValueAt(Json, total).Should().Be("10");
        StepPaths.ValueAt("""{"total":10.0}""", total).Should().Be(StepPaths.ValueAt(Json, total), "numbers are compared canonically");
        StepPaths.ValueAt("""{"items":[{"sku":"a"}]}""", sku).Should().Be(StepPaths.ValueAt(null, sku));
        StepPaths.ValueAt("""{"items":[]}""", total).Should().NotBe(StepPaths.ValueAt(Json, total));
    }

    /// <summary>The script every shape folds: open, credit, credit, cancel (deleted only by the ShouldDelete shape), open again, credit.</summary>
    private static IReadOnlyList<IEvent> Script(Guid stream) =>
    [
        Event(stream, 1, new StepOpened("acme")),
        Event(stream, 2, new StepCredited(10)),
        Event(stream, 3, new StepCredited(15)),
        Event(stream, 4, new StepCancelled("changed their mind")),
        Event(stream, 5, new StepOpened("globex")),
        Event(stream, 6, new StepCredited(4)),
    ];

    private static (Type Type, Action<StoreOptions> Register) Shape(string shape) => shape switch
    {
        "create-apply" => (typeof(StepTally), static x => x.Projections.Add(new StepTallyProjection(), ProjectionLifecycle.Inline)),
        "should-delete" => (typeof(StepTally), static x => x.Projections.Add(new StepCancellingProjection(), ProjectionLifecycle.Inline)),
        "evolve" => (typeof(StepTally), static x => x.Projections.Add(new StepEvolveProjection(), ProjectionLifecycle.Inline)),
        "evolve-async" => (typeof(StepTally), static x => x.Projections.Add(new StepEvolveAsyncProjection(), ProjectionLifecycle.Inline)),
        "self-aggregating" => (typeof(StepLedger), static x => x.Projections.Snapshot<StepLedger>(SnapshotLifecycle.Inline)),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static async Task<IReadOnlyList<string?>> WalkAsync(
        DocumentStore store, IQuerySession session, Type type, Guid stream, IReadOnlyList<IEvent> events)
    {
        var walk = new AggregateWalk(AggregateStreamInvoker.FolderFor(type), session, store.Options.Serializer(), stream);
        List<string?> stepped = [];

        foreach (IEvent next in events)
        {
            WalkOutcome outcome = await walk.FeedAsync(next, Token);
            outcome.Kind.Should().Be(WalkOutcomeKind.Folded, "event {0} folds", next.Version);
            stepped.Add(walk.StateJson());
        }

        return stepped;
    }

    /// <summary>Time travel's replay to a version: Marten's whole fold, the stream's id set, the store's serializer.</summary>
    private static async Task<string?> WholeFoldAsync(
        DocumentStore store, IQuerySession session, Type type, Guid stream, IReadOnlyList<IEvent> events, int version)
    {
        object? aggregate = await AggregateStreamInvoker.AggregateAtVersionAsync(
            session, type, stream, [.. events.Take(version)], 0, Token);

        return aggregate is null ? null : store.Options.Serializer().ToJson(aggregate);
    }

    private static DocumentStore Store(Action<StoreOptions> register) =>
        DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            register(options);
        });

    private static Event<T> Event<T>(Guid stream, long version, T data) where T : notnull =>
        new(data)
        {
            Id = Guid.NewGuid(),
            StreamId = stream,
            Version = version,
            Sequence = version,
            Timestamp = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero).AddSeconds(version),
        };
}

/// <summary>Opens a tally.</summary>
/// <param name="Owner">Whose.</param>
public sealed record StepOpened(string Owner);

/// <summary>Adds to it.</summary>
/// <param name="Amount">How much.</param>
public sealed record StepCredited(int Amount);

/// <summary>Cancels it: deleted by the ShouldDelete shape, ignored by the others.</summary>
/// <param name="Reason">Why.</param>
public sealed record StepCancelled(string Reason);

/// <summary>Closes it: a <c>DeleteEvent&lt;T&gt;()</c> registration.</summary>
/// <param name="Reason">Why.</param>
public sealed record StepClosed(string Reason);

/// <summary>An event whose fold throws, every time.</summary>
/// <param name="Why">What the exception says.</param>
public sealed record StepJammed(string Why);

/// <summary>The aggregate the projection shapes fold.</summary>
public sealed class StepTally
{
    public Guid Id { get; set; }

    public string Owner { get; set; } = string.Empty;

    public int Balance { get; set; }

    public int Credits { get; set; }

    public int Opened { get; set; }

    public int Batches { get; set; }
}

/// <summary><c>Create</c> and <c>Apply</c>, static, as the sample's <c>OrderSummaryProjection</c> is written.</summary>
public sealed partial class StepTallyProjection : SingleStreamProjection<StepTally, Guid>
{
    public static StepTally Create(StepOpened opened) => new() { Owner = opened.Owner, Opened = 1 };

    public static void Apply(StepCredited credited, StepTally tally)
    {
        tally.Balance += credited.Amount;
        tally.Credits++;
    }

    public static void Apply(StepOpened opened, StepTally tally)
    {
        tally.Owner = opened.Owner;
        tally.Opened++;
    }

    public static void Apply(StepJammed jammed, StepTally tally) =>
        throw new InvalidOperationException("Jammed: " + jammed.Why);
}

/// <summary><c>ShouldDelete</c>: Marten's generated <c>DetermineAction</c> dispatch.</summary>
public sealed partial class StepCancellingProjection : SingleStreamProjection<StepTally, Guid>
{
    public static StepTally Create(StepOpened opened) => new() { Owner = opened.Owner, Opened = 2 };

    public static void Apply(StepCredited credited, StepTally tally)
    {
        tally.Balance += credited.Amount;
        tally.Credits++;
    }

    public static bool ShouldDelete(StepCancelled cancelled) => cancelled is not null;
}

/// <summary>An <c>Evolve</c> override: one event at a time, written by hand.</summary>
public sealed class StepEvolveProjection : SingleStreamProjection<StepTally, Guid>
{
    public override StepTally? Evolve(StepTally? snapshot, Guid id, IEvent e) => e.Data switch
    {
        StepOpened opened when snapshot is null => new StepTally { Owner = opened.Owner, Opened = 1 },
        StepOpened opened => Reopen(snapshot, opened),
        StepCredited credited when snapshot is not null => Credit(snapshot, credited.Amount),
        StepJammed jammed => throw new InvalidOperationException("Jammed: " + jammed.Why),
        _ => snapshot,
    };

    private static StepTally Reopen(StepTally tally, StepOpened opened)
    {
        tally.Owner = opened.Owner;
        tally.Opened++;
        return tally;
    }

    private static StepTally Credit(StepTally tally, int amount)
    {
        tally.Balance += amount;
        tally.Credits++;
        return tally;
    }
}

/// <summary>An <c>EvolveAsync</c> override.</summary>
public sealed class StepEvolveAsyncProjection : SingleStreamProjection<StepTally, Guid>
{
    public override async ValueTask<StepTally?> EvolveAsync(
        StepTally? snapshot, Guid id, IQuerySession session, IEvent e, CancellationToken cancellation)
    {
        await Task.Yield();

        switch (e.Data)
        {
            case StepOpened opened when snapshot is null:
                return new StepTally { Owner = opened.Owner, Opened = 1 };
            case StepOpened opened:
                snapshot.Owner = opened.Owner;
                snapshot.Opened++;
                return snapshot;
            case StepCredited credited when snapshot is not null:
                snapshot.Balance += credited.Amount;
                snapshot.Credits++;
                return snapshot;
            default:
                return snapshot;
        }
    }
}

/// <summary>A projection that decides over the whole batch: it counts the batches it is handed.</summary>
public sealed class StepBatchProjection : SingleStreamProjection<StepTally, Guid>
{
    public override (StepTally?, ActionType) DetermineAction(StepTally? snapshot, Guid identity, IReadOnlyList<IEvent> events)
    {
        StepTally tally = snapshot ?? new StepTally();
        tally.Batches++;

        foreach (IEvent e in events)
        {
            if (e.Data is StepCredited credited)
            {
                tally.Balance += credited.Amount;
                tally.Credits++;
            }
        }

        return (tally, ActionType.Store);
    }
}

/// <summary>Conventional methods and a <c>DeleteEvent&lt;T&gt;()</c> registration.</summary>
public sealed partial class StepDeletingProjection : SingleStreamProjection<StepTally, Guid>
{
    public StepDeletingProjection() => DeleteEvent<StepClosed>();

    public static StepTally Create(StepOpened opened) => new() { Owner = opened.Owner, Opened = 1 };

    public static void Apply(StepCredited credited, StepTally tally)
    {
        tally.Balance += credited.Amount;
        tally.Credits++;
    }
}

/// <summary>An <c>Evolve</c> override and a <c>DeleteEvent&lt;T&gt;()</c> registration: Marten's batch short-circuit.</summary>
public sealed class StepDeletingEvolveProjection : SingleStreamProjection<StepTally, Guid>
{
    public StepDeletingEvolveProjection() => DeleteEvent<StepClosed>();

    public override StepTally? Evolve(StepTally? snapshot, Guid id, IEvent e) => e.Data switch
    {
        StepOpened opened when snapshot is null => new StepTally { Owner = opened.Owner, Opened = 1 },
        StepCredited credited => Credit(snapshot ?? new StepTally(), credited.Amount),
        _ => snapshot,
    };

    private static StepTally Credit(StepTally tally, int amount)
    {
        tally.Balance += amount;
        tally.Credits++;
        return tally;
    }
}

/// <summary>A self-aggregating aggregate, the shape <c>Snapshot&lt;T&gt;()</c> wants.</summary>
public sealed class StepLedger
{
    public Guid Id { get; set; }

    public string Owner { get; set; } = string.Empty;

    public int Balance { get; set; }

    public int Opened { get; set; }

    public static StepLedger Create(StepOpened opened) => new() { Owner = opened.Owner, Opened = 1 };

    public void Apply(StepOpened opened)
    {
        Owner = opened.Owner;
        Opened++;
    }

    public void Apply(StepCredited credited) => Balance += credited.Amount;
}

/// <summary>A self-aggregating aggregate whose <c>Apply</c> records the id it sees.</summary>
public sealed class StepIdWatcher
{
    public Guid Id { get; set; }

    public string Owner { get; set; } = string.Empty;

    public int Balance { get; set; }

    public List<Guid> SeenIds { get; set; } = [];

    public static StepIdWatcher Create(StepOpened opened) => new() { Owner = opened.Owner };

    public void Apply(StepCredited credited)
    {
        SeenIds.Add(Id);
        Balance += credited.Amount;
    }
}
