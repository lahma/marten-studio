using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

using JasperFx.Events;
using JasperFx.Events.Aggregation;

using Marten;

using PostgresStudio.Services.Json;

namespace MartenStudio.Services.Events;

/// <summary>
/// Marten's aggregator for one aggregate type, closed over it (<see cref="AggregateStreamInvoker.FolderFor" />): the one
/// fold time travel and the stepper share.
/// </summary>
internal interface IAggregateFolder
{
    /// <summary>The aggregate type the folder is closed over.</summary>
    Type AggregateType { get; }

    /// <summary>
    /// <c>AggregatorFor&lt;T&gt;().BuildAsync(events, session, state, token)</c>: Marten's own fold of
    /// <paramref name="events" /> onto <paramref name="state" />, and nothing else.
    /// </summary>
    /// <param name="session">The scope's session, which Marten hands to the projection's own code.</param>
    /// <param name="state">The aggregate to fold onto, or <see langword="null" /> for none. Marten may change it in place.</param>
    /// <param name="events">The events, in sequence order.</param>
    /// <param name="cancellationToken">Cancels the fold, as far as the projection's code honours it.</param>
    /// <returns>The aggregate after the events, or <see langword="null" /> for none.</returns>
    Task<object?> FoldAsync(IQuerySession session, object? state, IReadOnlyList<IEvent> events, CancellationToken cancellationToken);

    /// <summary>Puts the stream's id on the aggregate as Marten's own replay does, where the type has an identity.</summary>
    void SetIdentity(IQuerySession session, object aggregate, object streamId);

    /// <summary>
    /// The aggregate as the store's own serializer writes it, with the stream's id on it - and the aggregate itself left
    /// as the fold made it, so a fold that goes on from it goes on from what Marten would have.
    /// </summary>
    string ToJson(IQuerySession session, ISerializer serializer, object aggregate, object streamId);

    /// <summary>Whether this store's aggregator for the type folds one event at a time, and why not when it does not.</summary>
    AggregateStepShape ShapeOf(IQuerySession session);
}

/// <summary>How the stepper folds a stream into one aggregate type.</summary>
internal enum AggregateStepMode
{
    /// <summary>
    /// One event onto the state before it. For every dispatch Marten's aggregator has - the generated
    /// <c>Create</c>/<c>Apply</c>/<c>ShouldDelete</c> dispatchers and an <c>Evolve</c> or <c>EvolveAsync</c> override -
    /// that is the same state Marten's single fold of every event up to it produces (read at JasperFx <c>V2.74.0</c> and
    /// <c>V2.95.1</c>, and held by <c>AggregateSteppingTests</c>).
    /// </summary>
    EventByEvent,

    /// <summary>
    /// Every event from the first, again, for every step: the projection decides over a batch of events, so a step is
    /// only what Marten's aggregate would be when the batch is the whole stream up to it.
    /// </summary>
    FromTheStart,
}

/// <summary>How the stepper may step one store's aggregate.</summary>
/// <param name="Mode">How it folds.</param>
/// <param name="Reason">Why it folds from the start, in a sentence the page shows; <see langword="null" /> event by event.</param>
/// <param name="IsDeleteEvent">
/// Whether an event is one the projection registered with <c>DeleteEvent&lt;T&gt;()</c>. On the <c>Evolve</c> path Marten's
/// single fold answers nothing for a batch that holds one (<c>buildActionAsync</c>'s <c>MatchesAnyDeleteType</c>), whatever
/// comes before or after it; a generated dispatch deletes at the event instead. Which one a projection has is not visible
/// from outside, so from that event on a step is folded from the start, which is exact for both.
/// </param>
internal sealed record AggregateStepShape(AggregateStepMode Mode, string? Reason, Func<IEvent, bool> IsDeleteEvent);

/// <summary>
/// What a store's aggregator is, as far as stepping it goes: read off its runtime type once per type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is asked.</b> Marten's <c>IAggregator&lt;T&gt;.BuildAsync</c> folds a batch. For a projection whose dispatch is
/// one of JasperFx's per-event loops - every generated dispatcher, and the <c>Evolve</c>/<c>EvolveAsync</c> overrides
/// (<c>evolveDefault</c>, <c>evolveDefaultAsync</c>, and the generated <c>DetermineAction</c> arms, read at JasperFx
/// <c>V2.74.0</c> and <c>V2.95.1</c>) - folding events 1..N is folding 1..N-1 and then N, so the stepper may fold one
/// event at a time. Two things break that, and both are recognised here rather than guessed at:
/// </para>
/// <list type="bullet">
/// <item><description>A projection that overrides <c>DetermineAction</c> or <c>DetermineActionAsync</c> with code of its
/// own (not generated): Marten hands it the whole batch, and what it does with one is its own.</description></item>
/// <item><description>An event registered with <c>DeleteEvent&lt;T&gt;()</c>: on the <c>Evolve</c> path the batch fold answers
/// nothing for any batch that holds one (<see cref="AggregateStepShape.IsDeleteEvent" />). JasperFx's generator turns a
/// constructor's registration into a per-event delete arm instead (<c>EvolverCodeEmitter</c>, read at <c>V2.74.0</c>), so
/// for a generated dispatch the two agree - but the studio cannot see which dispatch a projection has.</description></item>
/// </list>
/// <para>
/// An aggregator that is not a <c>JasperFxAggregationProjectionBase</c> at all - JasperFx's container-scoped wrapper is
/// one - is a shape the studio has not read, and is folded from the start too.
/// </para>
/// </remarks>
internal static class AggregateStepShapes
{
    private static readonly ConcurrentDictionary<(Type Aggregator, Type Aggregate), Inspection> Inspections = new();

    /// <summary>The shape of one store's aggregator for <paramref name="aggregateType" />.</summary>
    /// <param name="aggregator">What <c>AggregatorFor&lt;T&gt;()</c> answered.</param>
    /// <param name="aggregateType">The aggregate type.</param>
    public static AggregateStepShape Of(object aggregator, Type aggregateType)
    {
        ArgumentNullException.ThrowIfNull(aggregator);
        ArgumentNullException.ThrowIfNull(aggregateType);

        Inspection inspection = Inspections.GetOrAdd(
            (aggregator.GetType(), aggregateType),
            static key => Inspect(key.Aggregator, key.Aggregate));

        Func<IEvent, bool> isDeleteEvent = static _ => false;

        if (inspection.MatchesAnyDeleteType is { } method)
        {
            Func<IReadOnlyList<IEvent>, bool> matches = method.CreateDelegate<Func<IReadOnlyList<IEvent>, bool>>(aggregator);
            isDeleteEvent = e => matches([e]);
        }

        return new AggregateStepShape(inspection.Mode, inspection.Reason, isDeleteEvent);
    }

    /// <summary>The sentence a step folded from the start because of a delete event carries.</summary>
    public static string DeleteEventReason(string eventTypeName) =>
        $"The projection registers {eventTypeName} with DeleteEvent<T>(), which Marten's aggregator can apply to the whole batch "
        + "of events it folds rather than to that event alone - so from that event on, each step is folded from the stream's "
        + "first event, as Marten's own aggregate of the stream up to it would be.";

    private static Inspection Inspect(Type runtime, Type aggregateType)
    {
        Type? projection = AggregationBase(runtime, aggregateType);

        if (projection is null)
        {
            return new Inspection(
                AggregateStepMode.FromTheStart,
                $"Marten folds {aggregateType.Name} through {Name(runtime)}, which is not a projection whose fold the studio has "
                + "read to be one event at a time - so each step is folded from the stream's first event, as Marten's own "
                + "aggregate of the stream up to it would be.",
                null);
        }

        MethodInfo? matches = projection.GetMethod(
            nameof(JasperFxAggregationProjectionBase<object, Guid, IDocumentOperations, IQuerySession>.MatchesAnyDeleteType),
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(IReadOnlyList<IEvent>)]);

        if (OwnBatchDispatch(runtime, projection) is { } own)
        {
            return new Inspection(
                AggregateStepMode.FromTheStart,
                $"{Name(runtime)} overrides {own}, which Marten hands a whole batch of events at once - so each step is folded "
                + "from the stream's first event, as Marten's own aggregate of the stream up to it would be.",
                matches);
        }

        return new Inspection(AggregateStepMode.EventByEvent, null, matches);
    }

    /// <summary>
    /// The closed <c>JasperFxAggregationProjectionBase&lt;T, TId, IDocumentOperations, IQuerySession&gt;</c> in the
    /// aggregator's base chain, or <see langword="null" />.
    /// </summary>
    private static Type? AggregationBase(Type runtime, Type aggregateType)
    {
        for (Type? type = runtime; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(JasperFxAggregationProjectionBase<,,,>)
                && type.GetGenericArguments()[0] == aggregateType)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>
    /// The name of the batch dispatch the projection overrides with its own code - <c>DetermineAction</c> or
    /// <c>DetermineActionAsync</c> - or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// JasperFx's own test (<c>isOverridden</c>, read at <c>V2.74.0</c> and <c>V2.95.1</c>): declared outside JasperFx.Events
    /// and overriding JasperFx.Events' declaration, matched by the base declaration's parameter types. A generated override
    /// - <see cref="GeneratedCodeAttribute" /> - is a per-event loop and does not count.
    /// </remarks>
    private static string? OwnBatchDispatch(Type runtime, Type projection)
    {
        Assembly jasperFx = typeof(IEvent).Assembly;

        foreach (string name in (string[])
                 [
                     nameof(JasperFxAggregationProjectionBase<object, Guid, IDocumentOperations, IQuerySession>.DetermineAction),
                     nameof(JasperFxAggregationProjectionBase<object, Guid, IDocumentOperations, IQuerySession>.DetermineActionAsync),
                 ])
        {
            MethodInfo? declaration = projection.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal) && x.DeclaringType == projection);

            if (declaration is null)
            {
                continue;
            }

            Type[] parameters = [.. declaration.GetParameters().Select(static x => x.ParameterType)];

            // Every match, not the first: a member that hides the virtual (CS0114) has the same signature, and only the
            // one that overrides is the dispatch. Any override of the host's own counts.
            bool overridden = runtime.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => string.Equals(x.Name, name, StringComparison.Ordinal)
                            && x.GetParameters().Select(static p => p.ParameterType).SequenceEqual(parameters))
                .Any(x => x.DeclaringType is { } owner
                          && owner.Assembly != jasperFx
                          && x.GetBaseDefinition().DeclaringType?.Assembly == jasperFx
                          && !x.IsDefined(typeof(GeneratedCodeAttribute), inherit: false));

            if (overridden)
            {
                return name;
            }
        }

        return null;
    }

    private static string Name(Type type) =>
        type.IsGenericType ? type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)] : type.Name;

    private sealed record Inspection(AggregateStepMode Mode, string? Reason, MethodInfo? MatchesAnyDeleteType);
}

/// <summary>
/// A stream folded into one aggregate one event at a time, as the stepper walks it: each event fed is folded with the
/// invoker's one fold, and a failure is the event that was being folded - never a guess.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded.</b> Event by event, the walk holds the aggregate and nothing else, so a stream of any length can be walked
/// with its events streamed past it. Folding from the start needs every event so far, so the walk keeps the first
/// <see cref="FromTheStartLimit" /> it is fed and refuses (<see cref="WalkOutcome.Refused" />) beyond them.
/// </para>
/// <para>
/// <b>Host code runs here.</b> The fold is Marten's aggregator, which calls the projection's own <c>Create</c>,
/// <c>Apply</c>, <c>ShouldDelete</c> or <c>Evolve</c> - exactly what time travel has always run.
/// </para>
/// </remarks>
internal sealed class AggregateWalk
{
    /// <summary>The most events a walk that folds from the start will fold, and so keep.</summary>
    public const int FromTheStartLimit = 2_000;

    private readonly IAggregateFolder folder;
    private readonly IQuerySession session;
    private readonly ISerializer serializer;
    private readonly object streamId;
    private readonly AggregateStepShape shape;
    private readonly int limit;

    /// <summary>Every event fed so far, while there are no more than <see cref="limit" /> of them.</summary>
    private List<IEvent>? fed = [];

    /// <summary>The aggregate after the last event fed.</summary>
    private object? state;

    /// <summary>Whether a <c>Compacted&lt;T&gt;</c> event has been fed.</summary>
    private bool compacted;

    /// <summary>Starts a walk at the stream's beginning: no events, no aggregate.</summary>
    /// <param name="folder">The aggregate type's folder.</param>
    /// <param name="session">The scope's session.</param>
    /// <param name="serializer">The store's own serializer (hard rule 10).</param>
    /// <param name="streamId">The stream, parsed against the store's stream identity.</param>
    /// <param name="limit">How many events a walk from the start may fold; <see cref="FromTheStartLimit" /> outside tests.</param>
    public AggregateWalk(IAggregateFolder folder, IQuerySession session, ISerializer serializer, object streamId, int limit = FromTheStartLimit)
    {
        this.folder = folder ?? throw new ArgumentNullException(nameof(folder));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.streamId = streamId ?? throw new ArgumentNullException(nameof(streamId));
        this.limit = Math.Max(1, limit);

        shape = folder.ShapeOf(session);
        Mode = shape.Mode;
        ModeReason = shape.Reason;
    }

    /// <summary>How the walk is folding now. It moves from event by event to from the start at a delete event, never back.</summary>
    public AggregateStepMode Mode { get; private set; }

    /// <summary>Why it folds from the start, when it does.</summary>
    public string? ModeReason { get; private set; }

    /// <summary>How many events have been folded.</summary>
    public long Count { get; private set; }

    /// <summary>Whether there is an aggregate after the last event.</summary>
    public bool HasAggregate => state is not null;

    /// <summary>Folds the next event of the stream.</summary>
    /// <param name="next">The event, in sequence order after the last one fed.</param>
    /// <param name="cancellationToken">Cancels the fold; a cancellation is never reported as the event's failure.</param>
    /// <returns>Whether it folded, what it threw, or that the walk cannot fold it from the start.</returns>
    public async Task<WalkOutcome> FeedAsync(IEvent next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        // Before each event, not only inside the fold: a generated dispatch never looks at the token, so a slow projection
        // over a window already read would otherwise fold to its end past the studio's QueryTimeout.
        cancellationToken.ThrowIfCancellationRequested();

        if (fed is not null)
        {
            fed.Add(next);

            if (fed.Count > limit)
            {
                fed = null;
            }
        }

        compacted |= next.Data is { } data && data.GetType() is { IsGenericType: true } type
            && type.GetGenericTypeDefinition() == typeof(Compacted<>);

        if (Mode == AggregateStepMode.EventByEvent && shape.IsDeleteEvent(next))
        {
            Mode = AggregateStepMode.FromTheStart;
            ModeReason = AggregateStepShapes.DeleteEventReason(
                string.IsNullOrEmpty(next.EventTypeName) ? next.EventType.Name : next.EventTypeName);
        }

        if (Mode == AggregateStepMode.FromTheStart)
        {
            if (fed is null)
            {
                return WalkOutcome.Refused(
                    $"Folding from the stream's first event for every step is something the studio does for the first "
                    + $"{limit.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} events of a stream, and this "
                    + "step is past them.");
            }

            // A Compacted<T> event carries its snapshot as an object of its own, which Marten's fold starts from and a
            // projection that changes its aggregate in place changes - so a second fold from the start would begin from a
            // snapshot the first one changed. Event by event, every event is folded once, and this does not arise.
            if (compacted)
            {
                return WalkOutcome.Refused(
                    "This stream is compacted, and folding it from its first event more than once would start each fold from "
                    + "the snapshot the fold before it changed - so the studio does not step it this way.");
            }
        }

        try
        {
            // From the start: every event so far, with no state - a fresh aggregate each time, the one Marten's own fold of
            // the stream up to here would build.
            state = Mode == AggregateStepMode.EventByEvent
                ? await folder.FoldAsync(session, state, [next], cancellationToken).ConfigureAwait(false)
                : await folder.FoldAsync(session, null, fed!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The event being folded is the one that threw - by construction: the fold through the event before it did
            // not. Marten wraps a projection's own exception in ApplyEventException, naming the same event; the step shows
            // what the projection threw.
            return WalkOutcome.Failed(StepFailure.From(exception));
        }

        Count++;
        return WalkOutcome.Folded;
    }

    /// <summary>The aggregate now, as the store's serializer writes it, or <see langword="null" /> for none.</summary>
    public string? StateJson() => state is null ? null : folder.ToJson(session, serializer, state, streamId);
}

/// <summary>What feeding one event to an <see cref="AggregateWalk" /> did.</summary>
/// <param name="Kind">Folded, failed or refused.</param>
/// <param name="Failure">What the projection threw, when it failed.</param>
/// <param name="Refusal">Why the walk will not fold it, when it refused.</param>
internal readonly record struct WalkOutcome(WalkOutcomeKind Kind, StepFailure? Failure, string? Refusal)
{
    /// <summary>The event was folded.</summary>
    public static WalkOutcome Folded => new(WalkOutcomeKind.Folded, null, null);

    /// <summary>Folding the event threw.</summary>
    public static WalkOutcome Failed(StepFailure failure) => new(WalkOutcomeKind.Failed, failure, null);

    /// <summary>The walk folds from the start and cannot fold this event that way.</summary>
    public static WalkOutcome Refused(string reason) => new(WalkOutcomeKind.Refused, null, reason);
}

/// <summary>The three things feeding an event can do.</summary>
internal enum WalkOutcomeKind
{
    /// <summary>Folded.</summary>
    Folded,

    /// <summary>Folding it threw; the walk is over.</summary>
    Failed,

    /// <summary>The walk folds from the start and cannot fold it that way - past the limit, or a compacted stream.</summary>
    Refused,
}

/// <summary>A dotted JSON path a visitor typed, and the value it names in an aggregate's JSON.</summary>
internal static class StepPaths
{
    /// <summary>What a value that is not there - or an aggregate that is not there - reads as.</summary>
    private const string Absent = "\u0000absent";

    /// <summary>
    /// Reads <c>total</c>, <c>items[0].sku</c>, <c>$.items[0].sku</c> or <c>["odd key"].x</c> into segments; a blank
    /// path is <see langword="false" />.
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyList<JsonPathSegment> segments)
    {
        string trimmed = (text ?? string.Empty).Trim();

        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.StartsWith('.'))
        {
            trimmed = trimmed[1..];
        }

        segments = JsonPathExpressions.ParseDotPath(trimmed);
        return segments.Count > 0;
    }

    /// <summary>The path in JSONPath form, as the diff names it.</summary>
    public static string Display(IReadOnlyList<JsonPathSegment> segments) => JsonPathExpressions.ToJsonPath(segments);

    /// <summary>
    /// The value at the path, canonical - or one marker for "not there", whether the member or the whole aggregate is
    /// missing. Two values are the same exactly when these strings are.
    /// </summary>
    public static string ValueAt(string? json, IReadOnlyList<JsonPathSegment> segments)
    {
        if (json is null || !JsonCanonicalizer.TryCanonicalize(json, out JsonNode? node, out _))
        {
            return Absent;
        }

        foreach (JsonPathSegment segment in segments)
        {
            node = Child(node, segment);

            if (node is null)
            {
                return Absent;
            }
        }

        return JsonCanonicalizer.ToCanonicalString(node);
    }

    /// <summary>One step down: a member of an object, or an element of an array; <see langword="null" /> when it is not there.</summary>
    private static JsonNode? Child(JsonNode? node, JsonPathSegment segment)
    {
        if (segment.Key is { } key)
        {
            return node is JsonObject obj && obj.TryGetPropertyValue(key, out JsonNode? member) ? member : null;
        }

        return node is JsonArray array && segment.Index >= 0 && segment.Index < array.Count ? array[segment.Index] : null;
    }
}
