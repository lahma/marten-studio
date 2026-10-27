using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;

using JasperFx.Events;

using Marten;

using MartenStudio.Internal.Sql;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Json;

namespace MartenStudio.Services.Events;

/// <summary>
/// The projection stepper (M1): a stream folded into one aggregate type one event at a time, with the aggregate after
/// each event, where the fold threw, and the dead letters that name its events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gated as time travel is, because it is time travel.</b> The stepper reads the stream and runs the projection's own
/// code through Marten's aggregator - the same fold (<see cref="IAggregateFolder" />), with the same scope resolution: the
/// store policy for the scope and nothing more. It writes nothing, starts nothing and asks no daemon.
/// </para>
/// <para>
/// <b>No storage is ensured.</b> The events are read with the replay's own SQL (<see cref="StreamReadQueries" />) and
/// Marten's own event selector, as <see cref="AggregateAtVersionAsync" /> reads them, and the dead letters with
/// <see cref="DeadLetterQueries" />: never <c>FetchStreamAsync</c>, <c>AggregateStreamAsync</c> or a LINQ terminal (hard rule
/// 14). The session is pinned with <c>SessionOptions.ForDatabase</c> (hard rule 15); it runs nothing itself and is what the
/// aggregator hands the projection.
/// </para>
/// <para>
/// <b>Bounded.</b> A window is <see cref="StepWindowSize" /> events, read with a limit. The events before it are streamed
/// past the fold one at a time and never held - so a step deep in a long stream costs one read of the events before it,
/// not one round trip each - except for a projection folded from the start, which keeps at most
/// <see cref="AggregateWalk.FromTheStartLimit" />. Every call runs under <see cref="MartenStudioOptions.QueryTimeout" />, and
/// a fold that runs past it is a value on the page.
/// </para>
/// </remarks>
internal sealed partial class EventDataService
{
    /// <summary>How many steps one window holds.</summary>
    internal const int StepWindowSize = 20;

    /// <summary>How many events the search for the next change looks at, at most.</summary>
    internal const int StepSearchLimit = 5_000;

    /// <inheritdoc />
    public async Task<StreamStepWindow> StepAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string typeName = aggregateTypeFullName ?? string.Empty;
        long requested = Math.Max(1, version);

        try
        {
            StepOpening opening = await OpenStepSourceAsync(scope, streamId, typeName, cancellationToken).ConfigureAwait(false);

            if (opening.Source is not { } source)
            {
                return opening.Missing is { } missing
                    ? StreamStepWindow.NotFound(opening.TypeName, requested, missing)
                    : StreamStepWindow.Failed(opening.TypeName, requested, opening.Error!);
            }

            await using (source.ConfigureAwait(false))
            {
                using CancellationTokenSource budget = Budget(cancellationToken);

                try
                {
                    StepRun run = await RunStepsAsync(source, requested, budget.Token).ConfigureAwait(false);

                    // The fold threw before the window: the stepper stops on that event, so the window is the one that holds
                    // it - one more walk to it, which folds the same events up to it again.
                    if (run.PrefixFailure is { } stop)
                    {
                        run = await RunStepsAsync(source, stop, budget.Token).ConfigureAwait(false);
                    }

                    // Past the stream's last event: the window that holds the last one.
                    else if (run.Steps.Count == 0 && run.Refusal is null && run.LastBefore is { } last)
                    {
                        run = await RunStepsAsync(source, last, budget.Token).ConfigureAwait(false);
                    }

                    if (run.Steps.Count == 0 && run.Refusal is null && run.LastBefore is null)
                    {
                        return await NothingToStepAsync(scope, source, streamId, requested, cancellationToken).ConfigureAwait(false);
                    }

                    (IReadOnlyList<AggregateStep> steps, string? deadLetterNote) =
                        await WithDeadLettersAsync(scope, source, run.Steps, budget.Token).ConfigureAwait(false);

                    return new StreamStepWindow
                    {
                        TypeName = source.Candidate.Name,
                        RequestedVersion = requested,
                        Steps = steps,
                        StateBeforeJson = run.StateBefore,
                        HasMore = run.HasMore,
                        HasEarlier = run.LastBefore is not null,
                        Mode = run.Mode,
                        ModeReason = run.ModeReason,
                        StoppedAt = run.StoppedAt,
                        Refusal = run.Refusal,
                        RefusedAt = run.RefusedAt,
                        DeadLetterNote = deadLetterNote,
                    };
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return StreamStepWindow.Failed(source.Candidate.Name, requested, OverBudget("Folding this stream to the window"));
                }
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return StreamStepWindow.Failed(typeName, requested, Describe(scope, exception, "step the stream"));
        }
    }

    /// <inheritdoc />
    public async Task<StepSearch> FindNextChangeAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long afterVersion,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string typed = (path ?? string.Empty).Trim();
        long after = Math.Max(0, afterVersion);

        if (!StepPaths.TryParse(typed, out IReadOnlyList<JsonPathSegment> segments))
        {
            return new StepSearch
            {
                Path = typed,
                AfterVersion = after,
                Refusal = "Name a value of the aggregate to watch - a path such as total, items[0].sku or $.status.",
            };
        }

        string display = StepPaths.Display(segments);

        try
        {
            StepOpening opening = await OpenStepSourceAsync(scope, streamId, aggregateTypeFullName ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (opening.Source is not { } source)
            {
                return new StepSearch
                {
                    Path = display,
                    AfterVersion = after,
                    Refusal = opening.Missing is not null ? "There is no such stream in this scope to search." : null,
                    Error = opening.Error,
                };
            }

            await using (source.ConfigureAwait(false))
            {
                using CancellationTokenSource budget = Budget(cancellationToken);

                try
                {
                    return await SearchAsync(source, after, segments, display, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new StepSearch { Path = display, AfterVersion = after, Error = OverBudget("Searching this stream") };
                }
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return new StepSearch { Path = display, AfterVersion = after, Error = Describe(scope, exception, "search the stream") };
        }
    }

    /// <summary>One walk over the stream to the window around <paramref name="target" />, and the window's steps.</summary>
    private async Task<StepRun> RunStepsAsync(StepSource source, long target, CancellationToken cancellationToken)
    {
        long from = ((target - 1) / StepWindowSize * StepWindowSize) + 1;
        var walk = new AggregateWalk(source.Folder, source.Session, source.Serializer, source.StreamId);
        var run = new StepRun { Mode = walk.Mode, ModeReason = walk.ModeReason };

        // The events before the window, streamed past the fold and never held.
        if (from > 1)
        {
            await foreach (IEvent before in ReadStepEventsAsync(source, source.Query with { Version = from - 1 }, cancellationToken)
                               .ConfigureAwait(false))
            {
                WalkOutcome outcome = await walk.FeedAsync(before, cancellationToken).ConfigureAwait(false);
                run.LastBefore = before.Version;

                if (outcome.Kind == WalkOutcomeKind.Failed)
                {
                    run.PrefixFailure = before.Version;
                    return run;
                }

                if (outcome.Kind == WalkOutcomeKind.Refused)
                {
                    return run.Refused(walk, outcome.Refusal!, before.Version);
                }
            }
        }

        run.StateBefore = walk.StateJson();

        // The window: at most one more than it shows, so whether there is another is answered without a count.
        List<IEvent> window = [];
        await foreach (IEvent next in ReadStepEventsAsync(
                               source, source.Query with { AfterVersion = from - 1, Limit = StepWindowSize + 1 }, cancellationToken)
                           .ConfigureAwait(false))
        {
            window.Add(next);
        }

        string? previous = run.StateBefore;

        for (int index = 0; index < Math.Min(StepWindowSize, window.Count); index++)
        {
            IEvent next = window[index];
            WalkOutcome outcome = await walk.FeedAsync(next, cancellationToken).ConfigureAwait(false);

            if (outcome.Kind == WalkOutcomeKind.Refused)
            {
                return run.Refused(walk, outcome.Refusal!, next.Version);
            }

            AggregateStep step = Step(source, next);

            if (outcome.Kind == WalkOutcomeKind.Failed)
            {
                run.Steps.Add(step with { Failure = outcome.Failure });
                run.StoppedAt = next.Version;
                run.Mode = walk.Mode;
                run.ModeReason = walk.ModeReason;
                return run;
            }

            string? after = walk.StateJson();
            run.Steps.Add(step with { StateJson = after, Changes = Changes(previous, after) });
            previous = after;
        }

        run.HasMore = window.Count > StepWindowSize;
        run.Mode = walk.Mode;
        run.ModeReason = walk.ModeReason;
        return run;
    }

    /// <summary>The walk for the search: to the version, then on until the value at the path differs or a fold throws.</summary>
    private async Task<StepSearch> SearchAsync(
        StepSource source,
        long after,
        IReadOnlyList<JsonPathSegment> segments,
        string display,
        CancellationToken cancellationToken)
    {
        var walk = new AggregateWalk(source.Folder, source.Session, source.Serializer, source.StreamId);
        var found = new StepSearch { Path = display, AfterVersion = after };

        if (after > 0)
        {
            await foreach (IEvent before in ReadStepEventsAsync(source, source.Query with { Version = after }, cancellationToken)
                               .ConfigureAwait(false))
            {
                WalkOutcome outcome = await walk.FeedAsync(before, cancellationToken).ConfigureAwait(false);

                if (outcome.Kind == WalkOutcomeKind.Failed)
                {
                    // The stepper stops there, so nothing after it changes anything it can show.
                    return found with { FoundVersion = before.Version, FoundFailure = true };
                }

                if (outcome.Kind == WalkOutcomeKind.Refused)
                {
                    return found with { Refusal = outcome.Refusal };
                }
            }
        }

        string value = StepPaths.ValueAt(walk.StateJson(), segments);
        long? scanned = null;
        int count = 0;

        await foreach (IEvent next in ReadStepEventsAsync(
                               source, source.Query with { AfterVersion = after, Limit = StepSearchLimit }, cancellationToken)
                           .ConfigureAwait(false))
        {
            count++;
            WalkOutcome outcome = await walk.FeedAsync(next, cancellationToken).ConfigureAwait(false);

            if (outcome.Kind == WalkOutcomeKind.Failed)
            {
                return found with { FoundVersion = next.Version, FoundFailure = true };
            }

            if (outcome.Kind == WalkOutcomeKind.Refused)
            {
                return found with { Refusal = outcome.Refusal, ScannedThrough = scanned };
            }

            string now = StepPaths.ValueAt(walk.StateJson(), segments);

            if (!string.Equals(now, value, StringComparison.Ordinal))
            {
                return found with { FoundVersion = next.Version };
            }

            scanned = next.Version;
        }

        return found with { ScannedThrough = scanned, ReachedLimit = count >= StepSearchLimit };
    }

    /// <summary>
    /// Everything a stepper's read needs, or why there is nothing to read: the scope resolved, the aggregate type the
    /// picker offered, the stream proved to be the scope's, the table described and the replay's query built.
    /// </summary>
    /// <remarks>
    /// Every refusal is <see cref="AggregateAtVersionAsync" />'s, word for word, so the stepper and time travel refuse the
    /// same requests alike: a type the store does not offer (never <c>Type.GetType</c> on a string a browser sent), an id
    /// the store's identity cannot read, the empty GUID, a stream another tenant's or not there, and a column the
    /// configuration reads that the table lacks.
    /// </remarks>
    private async Task<StepOpening> OpenStepSourceAsync(
        StudioScope scope,
        string streamId,
        string typeName,
        CancellationToken cancellationToken)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        AggregateTypeCandidate? candidate = AggregateStreamInvoker
            .Candidates(resolved.Store.Options, options.Value.IsDocumentTypeVisible)
            .FirstOrDefault(x => string.Equals(x.FullName, typeName, StringComparison.Ordinal));

        if (candidate is null)
        {
            // The type name is the visitor's text (?type=), so it reaches the refusal escaped and bounded (H1; INT-W1).
            return StepOpening.Refused(typeName, $"'{StudioLogValue.Cap(typeName)}' is not one of this store's aggregate types.");
        }

        IdParseResult parsed = ParseStreamIdFor(resolved, streamId);
        if (!parsed.Success)
        {
            return StepOpening.Refused(candidate.Name, parsed.Error ?? "That stream id is not usable.");
        }

        if (StreamCompactionRules.IsWholeLogId(parsed.Value))
        {
            return StepOpening.Refused(
                candidate.Name,
                "This stream's id is the empty GUID, which Marten's stream reads below 9.51 take as no stream filter at all, "
                + "so a replay through them would fold every event in the log. The studio does not replay it.");
        }

        IQuerySession session = OpenQuerySession(resolved);
        NpgsqlConnection? connection = null;

        try
        {
            connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            // No event store is a stream that does not exist - never created to find out - and so is a stream that is
            // another tenant's: the same answer, so a guessed id learns nothing.
            if (table.EventColumns.Count == 0
                || !await StreamInScopeAsync(resolved, connection, table, parsed.Value!, cancellationToken).ConfigureAwait(false))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
                return StepOpening.NotThere(candidate.Name);
            }

            Marten.Events.IEventStorage storage = ((Marten.Internal.IMartenSession) session).EventStorage();

            var query = new ReplayEventsQuery
            {
                SelectFields = storage.SelectFields(),
                StreamId = parsed.Value!,
                TenantId = resolved.Store.Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
                    ? session.TenantId
                    : table.HasTenantId ? resolved.TenantId : null,
                IgnoreSkipped = resolved.Store.Options.Events.EnableEventSkippingInProjectionsOrSubscriptions,
            };

            // The window's bound reads `version`, so it is asked about whether or not this store's replay would.
            IReadOnlyList<string> missing = StreamReadQueries.MissingColumns(table, query with { AfterVersion = 1 });
            if (missing.Count > 0)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);

                return StepOpening.Refused(
                    candidate.Name,
                    $"{table.Schema}.{EventTableInfo.EventsTable} has no {(missing.Count == 1 ? "column" : "columns")} "
                    + $"{string.Join(", ", missing)}, which this store's configuration reads. Marten would add "
                    + $"{(missing.Count == 1 ? "it" : "them")} by migrating the event store on its first event read; the studio "
                    + "does not migrate a table to read it. Apply the pending change from the Schema screen, then step again.");
            }

            return new StepOpening(candidate.Name, new StepSource
            {
                Resolved = resolved,
                Candidate = candidate,
                Folder = AggregateStreamInvoker.FolderFor(candidate.Type),
                StreamId = parsed.Value!,
                Session = session,
                Connection = connection,
                Table = table,
                Storage = storage,
                Query = query,
                Serializer = resolved.Store.Options.Serializer(),
            }, null, null);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>One stream's events for the stepper, streamed through Marten's own event selector as they are read.</summary>
    private async IAsyncEnumerable<IEvent> ReadStepEventsAsync(
        StepSource source,
        ReplayEventsQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(source.Table, query);
        command.Connection = source.Connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        Weasel.Storage.ISelector<IEvent> selector = source.Storage;

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return await selector.ResolveAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Why a stream gave the stepper nothing at all - time travel's four answers (<see cref="DescribeMissingAggregateAsync" />),
    /// and a conjoined event store with no tenant selected, whose replay reads no events.
    /// </summary>
    private async Task<StreamStepWindow> NothingToStepAsync(
        StudioScope scope,
        StepSource source,
        string streamId,
        long requested,
        CancellationToken cancellationToken)
    {
        if (source.Resolved.TenantId is null
            && source.Resolved.Store.Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            return StreamStepWindow.NotFound(source.Candidate.Name, requested, AggregateMissingReason.TenantRequired);
        }

        AggregateSnapshot missing = await DescribeMissingAggregateAsync(
            scope, source.Candidate.Name, streamId ?? string.Empty, 0, cancellationToken).ConfigureAwait(false);

        return StreamStepWindow.NotFound(source.Candidate.Name, requested, missing.Reason, missing.StreamVersion);
    }

    /// <summary>
    /// The steps with the dead letters that name their events - or the steps alone, and why, when the dead letters cannot
    /// be read. A host that hides <c>DeadLetterEvent</c> is asked nothing and told nothing (D27: a hidden type is absent).
    /// </summary>
    /// <remarks>
    /// The list's own read (<see cref="DeadLetterQueries.BuildPage" />), narrowed to the window's sequences and - as the list
    /// is - to the scope's tenant by the dead letter's own <c>TenantId</c> property, which is what <c>DeadLetterEvent</c>,
    /// single-tenanted on every store, has to narrow by (hard rule 15). Under per-tenant sequences a sequence names one event
    /// in each tenant, so a dead letter names a step only when its tenant is the step's too.
    /// </remarks>
    private async Task<(IReadOnlyList<AggregateStep> Steps, string? Note)> WithDeadLettersAsync(
        StudioScope scope,
        StepSource source,
        List<AggregateStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 0 || !DeadLettersVisible(scope))
        {
            return (steps, null);
        }

        List<DeadLetterRow> letters = [];

        try
        {
            DocumentTableInfo? table = await DescribeDeadLettersAsync(source.Resolved, source.Connection, cancellationToken)
                .ConfigureAwait(false);

            if (table is null)
            {
                return (steps, null);
            }

            DeadLetterJsonKeys keys = DeadLetterJsonKeys.For(
                MartenStudio.Services.Documents.DocumentDataService.NamingPolicyFor(source.Resolved.Store));

            await using NpgsqlCommand command = DeadLetterQueries.BuildPage(table, keys, new DeadLetterPageQuery
            {
                TenantId = source.Resolved.TenantId,
                EventSequences = [.. steps.Select(static x => x.Sequence)],
                Limit = DeadLetterQueries.MaxLimit,
            });
            command.Connection = source.Connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid id = reader.GetGuid(0);
                JasperFx.Events.Daemon.DeadLetterEvent letter = await source.Serializer
                    .FromJsonAsync<JasperFx.Events.Daemon.DeadLetterEvent>(reader, 1, cancellationToken)
                    .ConfigureAwait(false);

                letters.Add(new DeadLetterRow(
                    id,
                    letter.ProjectionName ?? string.Empty,
                    letter.ShardName ?? string.Empty,
                    letter.Timestamp,
                    letter.ExceptionType ?? string.Empty,
                    letter.ExceptionMessage ?? string.Empty,
                    letter.EventSequence,
                    letter.TenantId ?? string.Empty));
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            EventDataError error = Describe(scope, exception, "read the dead letters of the stepped events");
            return (steps, "The dead letters that name these events could not be read: " + error.Display);
        }

        bool perTenant = source.Resolved.Store.Options.Events.UseTenantPartitionedEvents;

        return ([.. steps.Select(step => step with
        {
            DeadLetters =
            [
                .. letters
                    .Where(x => x.EventSequence == step.Sequence
                                && (!perTenant || string.Equals(x.TenantId, step.TenantId, StringComparison.Ordinal)))
                    .Select(static x => new StepDeadLetter(
                        x.Id, x.ProjectionName, x.ShardName, x.ExceptionType, x.ExceptionMessage, x.TenantId)),
            ],
        })], null);
    }

    /// <summary>One event as a step, before it is folded: what it is, and its body as the aggregate is handed it.</summary>
    private static AggregateStep Step(StepSource source, IEvent next) =>
        new(
            next.Sequence,
            next.Version,
            next.EventTypeName,
            next.DotNetTypeName,
            next.Timestamp,
            next.TenantId,
            next.Data is null ? null : source.Serializer.ToJson(next.Data));

    /// <summary>How many values differ, by the JSON toolkit's own differ; null when there is no aggregate on one side.</summary>
    private static int? Changes(string? before, string? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        JsonDiffResult diff = RoundTripDiffer.Diff(before, after);
        return diff.IsFaulted ? null : diff.Count;
    }

    /// <summary>The studio's query timeout over the whole call - the reads and the fold - linked to the page's own token.</summary>
    private CancellationTokenSource Budget(CancellationToken cancellationToken)
    {
        CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Value.QueryTimeout);
        return budget;
    }

    /// <summary>What a stepper call that ran past the query timeout says: a value, with Retry offered.</summary>
    private EventDataError OverBudget(string what) =>
        new(
            $"{what} took longer than the studio's query timeout "
            + $"({options.Value.QueryTimeout.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)} s), so it was stopped. "
            + "Each window folds every event before it; a stream this long, or a projection this slow, needs a longer "
            + "MartenStudioOptions.QueryTimeout.",
            null,
            true);

    /// <summary>What one stepper read works with, owned by the call that opened it.</summary>
    private sealed class StepSource : IAsyncDisposable
    {
        public required ResolvedScope Resolved { get; init; }

        public required AggregateTypeCandidate Candidate { get; init; }

        public required IAggregateFolder Folder { get; init; }

        public required object StreamId { get; init; }

        public required IQuerySession Session { get; init; }

        public required NpgsqlConnection Connection { get; init; }

        public required EventTableInfo Table { get; init; }

        public required Marten.Events.IEventStorage Storage { get; init; }

        public required ReplayEventsQuery Query { get; init; }

        public required ISerializer Serializer { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            await Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A stepper read's source, or why there is none: a refusal, or a stream that is not there.</summary>
    private sealed record StepOpening(string TypeName, StepSource? Source, EventDataError? Error, AggregateMissingReason? Missing)
    {
        public static StepOpening Refused(string typeName, string message) =>
            new(typeName, null, new EventDataError(message, null, false), null);

        public static StepOpening NotThere(string typeName) =>
            new(typeName, null, null, AggregateMissingReason.StreamMissing);
    }

    /// <summary>One walk's result: the window's steps, the aggregate before them, and where the walk stopped.</summary>
    private sealed class StepRun
    {
        public List<AggregateStep> Steps { get; } = [];

        public string? StateBefore { get; set; }

        public bool HasMore { get; set; }

        public AggregateStepMode Mode { get; set; }

        public string? ModeReason { get; set; }

        /// <summary>The version of the last event before the window, when there was one.</summary>
        public long? LastBefore { get; set; }

        /// <summary>The version of an event before the window whose fold threw.</summary>
        public long? PrefixFailure { get; set; }

        public long? StoppedAt { get; set; }

        public string? Refusal { get; set; }

        public long? RefusedAt { get; set; }

        public StepRun Refused(AggregateWalk walk, string reason, long version)
        {
            Refusal = reason;
            RefusedAt = version;
            Mode = walk.Mode;
            ModeReason = walk.ModeReason;
            return this;
        }
    }
}
