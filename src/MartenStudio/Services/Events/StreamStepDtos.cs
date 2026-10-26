using JasperFx.Events.Daemon;

namespace MartenStudio.Services.Events;

/// <summary>What folding one event threw.</summary>
/// <param name="ExceptionType">The full name of what the projection threw.</param>
/// <param name="Message">Its message.</param>
internal sealed record StepFailure(string ExceptionType, string Message)
{
    /// <summary>
    /// What the projection threw: Marten wraps it in <see cref="ApplyEventException" />, whose own message only names the
    /// event (<c>Failure to apply event #n</c>) - the stepper already shows which event - so the wrapper is taken off, as
    /// JasperFx's own stepper takes it off (<c>foldSliceAsync</c>, read at <c>V2.74.0</c>).
    /// </summary>
    public static StepFailure From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Exception thrown = exception is ApplyEventException { InnerException: { } inner } ? inner : exception;
        return new StepFailure(thrown.GetType().FullName ?? thrown.GetType().Name, thrown.Message);
    }
}

/// <summary>A dead letter that names one step's event.</summary>
/// <param name="Id">The dead letter's own id.</param>
/// <param name="ProjectionName">The projection whose daemon agent recorded it.</param>
/// <param name="ShardName">Its shard.</param>
/// <param name="ExceptionType">What the projection threw, as the daemon recorded it.</param>
/// <param name="ExceptionMessage">The message the daemon recorded.</param>
/// <param name="TenantId">The tenant of the event, as the daemon recorded it.</param>
internal sealed record StepDeadLetter(
    Guid Id,
    string ProjectionName,
    string ShardName,
    string ExceptionType,
    string ExceptionMessage,
    string TenantId);

/// <summary>One event of a stream, and the aggregate after it.</summary>
/// <param name="Sequence">The event's sequence.</param>
/// <param name="Version">Its version in the stream.</param>
/// <param name="EventTypeName">Its event type name, as <c>mt_events.type</c> holds it.</param>
/// <param name="DotNetTypeName">Its CLR type, when the store records one.</param>
/// <param name="Timestamp">When it was appended.</param>
/// <param name="TenantId">Its tenant.</param>
/// <param name="EventJson">
/// Its body as the aggregate was handed it - after any upcaster, through the store's own serializer.
/// </param>
internal sealed record AggregateStep(
    long Sequence,
    long Version,
    string EventTypeName,
    string? DotNetTypeName,
    DateTimeOffset Timestamp,
    string? TenantId,
    string? EventJson)
{
    /// <summary>The aggregate after the event, or <see langword="null" /> when there is none - or when folding it threw.</summary>
    public string? StateJson { get; init; }

    /// <summary>What folding the event threw; the stepper stops here.</summary>
    public StepFailure? Failure { get; init; }

    /// <summary>
    /// How many values differ from the aggregate before the event, by <c>RoundTripDiffer</c> - or <see langword="null" />
    /// when there is no aggregate on one side to compare.
    /// </summary>
    public int? Changes { get; init; }

    /// <summary>The dead letters that name this event.</summary>
    public IReadOnlyList<StepDeadLetter> DeadLetters { get; init; } = [];
}

/// <summary>
/// One window of a stream stepped into one aggregate type: the steps, the aggregate before the first of them, and where
/// the fold stopped.
/// </summary>
internal sealed record StreamStepWindow
{
    /// <summary>The aggregate type stepped into, by its short name.</summary>
    public required string TypeName { get; init; }

    /// <summary>The version that was asked for.</summary>
    public long RequestedVersion { get; init; }

    /// <summary>The window's steps, oldest first.</summary>
    public IReadOnlyList<AggregateStep> Steps { get; init; } = [];

    /// <summary>The aggregate before the window's first step, or <see langword="null" /> when there was none.</summary>
    public string? StateBeforeJson { get; init; }

    /// <summary>Whether the stream has events after the window.</summary>
    public bool HasMore { get; init; }

    /// <summary>Whether the stream has events before the window - folded, and not shown.</summary>
    public bool HasEarlier { get; init; }

    /// <summary>How the window was folded.</summary>
    public AggregateStepMode Mode { get; init; }

    /// <summary>Why it was folded from the start, when it was.</summary>
    public string? ModeReason { get; init; }

    /// <summary>The version whose event threw: the last step, and the end of the fold.</summary>
    public long? StoppedAt { get; init; }

    /// <summary>Why the walk would not fold on, when it would not - and the version it stopped before.</summary>
    public string? Refusal { get; init; }

    /// <summary>The version the walk refused to fold.</summary>
    public long? RefusedAt { get; init; }

    /// <summary>What went wrong reading or folding, as a value.</summary>
    public EventDataError? Error { get; init; }

    /// <summary>Why there are no steps at all, when the stream gave the fold nothing.</summary>
    public AggregateMissingReason? Missing { get; init; }

    /// <summary>The stream's own last version, where the missing reason read it.</summary>
    public long? StreamVersion { get; init; }

    /// <summary>Why the dead letters naming these events are not shown, when they could not be read.</summary>
    public string? DeadLetterNote { get; init; }

    /// <summary>A window that could not be read or folded.</summary>
    public static StreamStepWindow Failed(string typeName, long version, EventDataError error) =>
        new() { TypeName = typeName, RequestedVersion = version, Error = error };

    /// <summary>A stream that gave the fold nothing.</summary>
    public static StreamStepWindow NotFound(string typeName, long version, AggregateMissingReason reason, long? streamVersion = null) =>
        new() { TypeName = typeName, RequestedVersion = version, Missing = reason, StreamVersion = streamVersion };
}

/// <summary>Where the next event that changed a value of the aggregate is.</summary>
internal sealed record StepSearch
{
    /// <summary>The path as the diff names it, or what was typed when it could not be read.</summary>
    public required string Path { get; init; }

    /// <summary>The version the search started after.</summary>
    public long AfterVersion { get; init; }

    /// <summary>The version of the first event after it that changed the value, or whose fold threw.</summary>
    public long? FoundVersion { get; init; }

    /// <summary>Whether the event found is one whose fold threw - the stepper stops there.</summary>
    public bool FoundFailure { get; init; }

    /// <summary>The last version looked at, when nothing was found.</summary>
    public long? ScannedThrough { get; init; }

    /// <summary>Whether the search stopped at its limit rather than at the stream's end.</summary>
    public bool ReachedLimit { get; init; }

    /// <summary>Why the search did not run, or stopped - a path that names nothing, or a walk that would not fold on.</summary>
    public string? Refusal { get; init; }

    /// <summary>What went wrong reading or folding, as a value.</summary>
    public EventDataError? Error { get; init; }
}
