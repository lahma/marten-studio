using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.Logging;

namespace MartenStudio.Services.Projections;

/// <summary>
/// H2's half of the projections service: the high-water Explain, a start that is checked rather than assumed, and
/// rebuild awareness.
/// </summary>
internal sealed partial class ProjectionDataService
{
    /// <summary>
    /// The longest the Explain's statements may run: the health advisor's own bound, because they are the same kind of
    /// read (<c>pg_locks</c> and <c>pg_stat_activity</c>).
    /// </summary>
    private static readonly TimeSpan ExplainStatementTimeout = HealthSweeper.MaxStatementTimeout;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>On demand, never on a poll.</b> The questions are a lock-table and a process-array scan; they are asked when
    /// somebody presses Explain and at no other time.
    /// </para>
    /// <para>
    /// <b>Gated as the advisor gates.</b> The scope is resolved first (the store policy for the scope as selected); the
    /// database as a whole is then asked with <c>TenantId = null</c>, because every number here is every tenant's; and
    /// the sessions are named only with <c>ViewDatabaseActivity</c> on and the write policy passing, against the same
    /// tenant-less resource. A visitor without it gets the counts and ages. Naming them is audited, once per press.
    /// </para>
    /// <para>
    /// <b>Through the health runner.</b> Each statement runs in its own read-only transaction under the advisor's
    /// <c>statement_timeout</c> and <c>lock_timeout</c>, as <c>SqlConsoleRole</c> when one is set, with
    /// <c>search_path</c> pinned - and is recorded for "Show SQL".
    /// </para>
    /// <para>
    /// <b>One read a minute, per database, for everyone.</b> The read is shared through
    /// <see cref="HighWaterExplainCache" /> - single-flight, kept for a minute across circuits (plan §8) - and each
    /// visitor's gate is applied to it afterwards: whether the sessions are named, and every statement masked for that
    /// visitor's withheld schemas (D27) or withheld outright when they cannot be read.
    /// </para>
    /// </remarks>
    public async Task<HighWaterExplanation> ExplainHighWaterAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        StudioScope whole = new(resolved.Registration.Key, resolved.Database.Id.Identity, null);

        if (!await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return HighWaterExplanation.Refused(
                "The Explain reads the whole database's sessions, so it needs the store policy to allow this database as a whole, "
                + "with no tenant selected in the policy's resource.");
        }

        if (resolved.Store.Options.Events.UseTenantPartitionedEvents)
        {
            return HighWaterExplanation.Refused(DaemonCheckConfiguration.TenantPartitionedReason);
        }

        (bool details, string? withheld) = await ActivityGateAsync(whole, cancellationToken).ConfigureAwait(false);

        HighWaterExplainRead read = await controlState.Explains
            .GetAsync(resolved.Registration.Key + "|" + resolved.Database.Id.Identity, token => ReadExplainAsync(resolved, token), cancellationToken)
            .ConfigureAwait(false);

        if (read.Unavailable is { } unavailable)
        {
            return HighWaterExplanation.Refused(unavailable) with { Statements = read.Statements };
        }

        HighWaterReading reading = read.Reading!;

        if (!reading.IsPinnedBehindGap || reading.StuckSince is not { } pinnedSince)
        {
            return new HighWaterExplanation
            {
                IsPinned = false,
                Mark = reading.Mark,
                AskedAt = reading.ServerNow,
                DetailsShown = details,
                DetailsWithheldReason = withheld,
                Statements = read.Statements,
            };
        }

        IReadOnlyList<HighWaterSuspect> sessions = read.Sessions;
        IReadOnlyList<PreparedTransactionInfo> prepared = read.Prepared;
        List<HighWaterSuspect> counted = [.. sessions.Where(static x => x.RuledOutBecause is null)];
        DateTimeOffset? oldest = counted.Min(static x => x.TransactionStart);

        IReadOnlyList<HighWaterSuspect> shown = [];
        if (details)
        {
            StatementScreen screen = await StatementScreenAsync(whole, resolved, cancellationToken).ConfigureAwait(false);
            shown = [.. sessions.Select(x => x with { Query = ExplainQueryText.Present(x.Query, x.QueryTruncated, screen) })];
        }

        HighWaterExplanation explanation = new()
        {
            IsPinned = true,
            Mark = reading.Mark,
            PinnedSince = pinnedSince,
            PinnedFor = reading.PinnedFor,
            AskedAt = reading.ServerNow,
            OpenTransactions = counted.Count,
            EventsWriteLockHolders = counted.Count(static x => x.HoldsEventsWriteLock),
            Writers = counted.Count(static x => x.HasTransactionId),
            RuledOut = sessions.Count - counted.Count,
            HiddenWriters = (int) Math.Min(read.HiddenWriters, int.MaxValue),
            PreparedBeforePin = prepared.Count(x => x.Prepared <= pinnedSince),
            PreparedTransactions = prepared.Count,
            OldestOpenTransaction = oldest is { } start ? reading.ServerNow - start : null,
            DetailsShown = details,
            DetailsWithheldReason = withheld,
            Sessions = shown,
            Prepared = details ? prepared : [],
            Statements = read.Statements,
        };

        if (details && (sessions.Count > 0 || prepared.Count > 0))
        {
            audit.Record(
                "ExplainHighWater",
                "high-water mark " + reading.Mark,
                true,
                $"Named {sessions.Count} session(s) and {prepared.Count} prepared transaction(s) that could hold the mark.",
                StudioCapability.ViewDatabaseActivity,
                whole);
        }

        return explanation;
    }

    /// <summary>
    /// Each shard's lag trend from the health advisor's samples, judged as D04 judges it, and the samples themselves -
    /// read at the gate the samples were written at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Behind the database gate.</b> The daemon checks that write these series are <see cref="HealthGate.Database" />
    /// checks - every number is the whole database's - so the history is read with
    /// <see cref="HealthHistoryView.VisibleAt" /> at that gate only for a visitor the store policy passes for the
    /// database as a whole (already asked, when the selected scope has no tenant), and at <see cref="HealthGate.None" />,
    /// which holds none of them, for anyone else. No number crosses a gate on its way to an arrow.
    /// </para>
    /// <para>
    /// <b>Judged where the facts are.</b> Here the daemon's state is in hand - the studio's own pause, a rebuild by the
    /// studio or by the daemon, each agent's status - so the arrow is withheld exactly where D04 would be, and for the
    /// same reasons (<see cref="ProjectionTrend.Judged" />). Samples older than three health intervals (and at least
    /// five minutes) draw no arrow at all.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyDictionary<string, ShardTrend> Trends, HealthHistoryView History)> TrendsAsync(
        ResolvedScope resolved,
        DaemonStatus daemon,
        IReadOnlyList<ShardProgress> progress,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ShardTrend> trends = new(StringComparer.OrdinalIgnoreCase);
        HealthHistoryView raw = healthHistory.Read(resolved.Registration.Key, resolved.Database.Id.Identity);

        if (raw.Samples.Count == 0 || progress.Count == 0)
        {
            return (trends, HealthHistoryView.Empty);
        }

        bool wholeDatabase = resolved.Scope.TenantId is null
            || await authorization.IsAuthorizedAsync(
                    new StudioScope(resolved.Registration.Key, resolved.Database.Id.Identity, null), capability: null, cancellationToken)
                .ConfigureAwait(false);

        HealthHistoryView history = raw.VisibleAt(wholeDatabase ? HealthGate.Database : HealthGate.None);

        TimeSpan freshness = options.Value.EffectiveHealthInterval * 3;
        if (freshness < TimeSpan.FromMinutes(5))
        {
            freshness = TimeSpan.FromMinutes(5);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<StudioOperation> running = operations.RunningFor(resolved.Registration.Key, resolved.Database.Id.Identity);

        foreach (ShardProgress shard in progress)
        {
            ShardTrend trend = ProjectionTrend.For(history, shard.ShardName);

            if (trend.MeasuredAt is not { } at || now - at > freshness)
            {
                continue;
            }

            bool rebuilding = daemon.IsRebuildingHere(shard.ProjectionName)
                || running.Any(x => string.Equals(x.Kind, "Rebuild", StringComparison.Ordinal)
                    && string.Equals(x.Target, shard.ProjectionName, StringComparison.OrdinalIgnoreCase));

            string? agentHere = daemon.IsHostedHere
                ? daemon.Agents.FirstOrDefault(x => string.Equals(x.ShardName, shard.ShardName, StringComparison.OrdinalIgnoreCase))?.Status
                : null;

            trends[shard.ShardName] = ProjectionTrend.Judged(trend, daemon.IsPausedByStudio, rebuilding, agentHere);
        }

        return (trends, history);
    }

    /// <summary>
    /// What this visitor's statements are checked against - the same screen Database › Sessions uses, from the database
    /// browser's own gate - or <see cref="StatementScreen.Unchecked" /> when the gate could not be read, which withholds
    /// every statement rather than showing one unmasked.
    /// </summary>
    private async Task<StatementScreen> StatementScreenAsync(StudioScope whole, ResolvedScope resolved, CancellationToken cancellationToken)
    {
        bool hidesDocumentTypes = options.Value.IsDocumentTypeVisible is not null;

        try
        {
            DatabaseGateRead gate = await databaseAccess.GateAsync(whole, resolved, cancellationToken).ConfigureAwait(false);
            return StatementScreen.Of(gate, hidesDocumentTypes);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not read the schema gate to mask the Explain's statements for {DatabaseId}", resolved.Database.Id.Identity);
            }

            return StatementScreen.Unchecked(hidesDocumentTypes);
        }
    }

    /// <summary>The Explain's questions, put once - what <see cref="HighWaterExplainCache" /> shares.</summary>
    private async Task<HighWaterExplainRead> ReadExplainAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        IReadOnlyStoreOptions storeOptions = resolved.Store.Options;
        string schema = storeOptions.Events.DatabaseSchemaName;
        int lockId = DaemonCheckConfiguration.DaemonLockIdOf(storeOptions);
        IReadOnlyList<long> lockKeys = DaemonCheckConfiguration.LockKeysFor(schema, DaemonCheckConfiguration.AsyncShardsOf(resolved.Store), lockId);

        MartenStudioOptions studio = options.Value;
        ReadOnlySqlOptions sessionOptions = new()
        {
            StatementTimeout = studio.QueryTimeout < ExplainStatementTimeout ? studio.QueryTimeout : ExplainStatementTimeout,
            Role = studio.SqlConsoleRole,
        };

        await using HealthConnection connection = new(resolved.Database);
        HealthSqlRunner sql = new(connection, sessionOptions, observer: null);
        HealthCheckContext context = new(
            resolved, studio, MartenRuntime.Current, NoServices.Instance, sql, DateTimeOffset.UtcNow, new HealthSweepMemo(), cancellationToken);

        EventStoreShape shape = await EventStoreShape.ReadAsync(context, schema, cancellationToken).ConfigureAwait(false);

        if (!shape.HasHighWater)
        {
            return new HighWaterExplainRead(EventStoreShape.NoEventStoreReason, null, [], [], 0, sql.Statements);
        }

        HighWaterReading reading = await HighWaterReading.ReadAsync(context, schema, cancellationToken).ConfigureAwait(false);

        if (!reading.IsPinnedBehindGap || reading.StuckSince is not { } pinnedSince)
        {
            return new HighWaterExplainRead(null, reading, [], [], 0, sql.Statements);
        }

        DateTimeOffset? fence = reading.FenceUpdated is { } fenced && reading.FenceMark is { } fenceMark && fenceMark <= reading.Mark
            ? fenced
            : null;

        var sessionsQuery = HealthDaemonQueries.ExplainSessions(schema, lockKeys, pinnedSince, fence);
        IReadOnlyList<HighWaterSuspect> sessions = await sql.QueryAsync(
                sessionsQuery,
                static reader => new HighWaterSuspect(
                    reader.GetInt32(0),
                    Text(reader, 1),
                    Text(reader, 2),
                    Text(reader, 3),
                    HighWaterReading.NullableTime(reader, 4),
                    Text(reader, 5),
                    HighWaterReading.NullableTime(reader, 6),
                    Text(reader, 7) is { } type ? type + (Text(reader, 8) is { } wait ? " / " + wait : string.Empty) : null,
                    Text(reader, 9),
                    reader.GetBoolean(10),
                    reader.GetBoolean(11),
                    reader.GetBoolean(12)
                        ? "idle in transaction since before the gap's numbers were handed out"
                        : reader.GetBoolean(13)
                            ? "holds this daemon's own leadership lock and has written nothing"
                            : null,
                    reader.GetBoolean(14)),
                cancellationToken)
            .ConfigureAwait(false);

        var preparedQuery = HealthDaemonQueries.ExplainPreparedTransactions();
        IReadOnlyList<PreparedTransactionInfo> prepared = await sql.QueryAsync(
                preparedQuery,
                static reader => new PreparedTransactionInfo(reader.GetString(0), Text(reader, 1), reader.GetFieldValue<DateTimeOffset>(2)),
                cancellationToken)
            .ConfigureAwait(false);

        var hiddenQuery = HealthDaemonQueries.ExplainHiddenWriters();
        long hidden = await sql.ScalarAsync<long>(hiddenQuery, cancellationToken).ConfigureAwait(false);

        return new HighWaterExplainRead(null, reading, sessions, prepared, hidden, sql.Statements);
    }

    /// <summary>
    /// <c>ViewDatabaseActivity</c>, in the advisor's order: the capability (and <c>ReadOnly</c>), then the write policy
    /// with the capability named, against the database as a whole.
    /// </summary>
    private async Task<(bool Passes, string? Reason)> ActivityGateAsync(StudioScope whole, CancellationToken cancellationToken)
    {
        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            return (false, capabilities.ReadOnly
                ? "Naming sessions needs ViewDatabaseActivity, and MartenStudioOptions.ReadOnly is true, which turns every capability off."
                : "Naming sessions needs " + StudioCapabilityGuard.OptionName(StudioCapability.ViewDatabaseActivity) + ".");
        }

        bool allowed = await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false);

        return allowed
            ? (true, null)
            : (false, "Naming sessions needs your account's write policy to allow ViewDatabaseActivity for this database.");
    }

    /// <summary>
    /// Starts <paramref name="shardName" /> - every shard of it, for a bare projection name - and says why when the
    /// daemon did not, rather than assuming it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The bug this fixes.</b> From JasperFx 2.86 (Marten 9.50) the daemon's <c>StartAgentAsync(string)</c> returns
    /// without starting anything while that projection is being rebuilt, and logs it at Information (jasperfx#1009);
    /// the studio called that overload and audited the call as a success. So each shard is now started through
    /// <c>StartAgentAsync(ShardName)</c>, which exists at the floor, returns the agent and throws
    /// <see cref="ShardStartException" /> when nothing started - carrying a classified <c>Reason</c> from JasperFx 2.76
    /// - and the agent's status is then read: a start the daemon did not do is refused, audited as not done, and
    /// recorded for D05 with its reason.
    /// </para>
    /// <para>
    /// <b>Rebuilds, asked twice.</b> A rebuild Marten Studio is running is known at every version, from the operation
    /// tracker; one somebody else started is known only where the daemon says so (<c>IsRebuilding</c>, JasperFx 2.86,
    /// whose <see langword="false" /> proves nothing). At the floor, JasperFx 2.74 has no guard: a start during a rebuild
    /// the studio did not start goes through and runs a continuous agent beside the replay - recorded by the live tests
    /// rather than papered over.
    /// </para>
    /// </remarks>
    /// <returns>The refusal, in words a page can show, or <see langword="null" /> when every shard started.</returns>
    private async Task<string?> StartAgentCheckedAsync(
        IProjectionDaemon daemon,
        ResolvedScope resolved,
        string shardName,
        CancellationToken cancellationToken)
    {
        string storeKey = resolved.Registration.Key;
        string databaseId = resolved.Database.Id.Identity;

        IReadOnlyList<ShardName> targets = StartTargetsOf(resolved.Store, shardName);
        if (targets.Count == 0)
        {
            // Not one of the registered shards by name - a tenant's shard is spelled with its tenant - but never handed
            // to StartAgentAsync(string), which from JasperFx 2.86 returns without starting anything during a rebuild and
            // would be audited as a start. A name that parses goes through the checked path; one that does not is refused.
            if (shardName.Contains(':', StringComparison.Ordinal) && ShardName.TryParse(shardName, out ShardName? parsed) && parsed is not null)
            {
                targets = [parsed];
            }
            else
            {
                return shardName + " was not started: it is not an async projection, subscription or shard this store registers.";
            }
        }

        List<string> refusals = [];

        foreach (ShardName target in targets)
        {
            if (await StartOneAsync(daemon, storeKey, databaseId, target, cancellationToken).ConfigureAwait(false) is { } refusal)
            {
                refusals.Add(refusal);
            }
        }

        return refusals.Count == 0 ? null : string.Join(" ", refusals);
    }

    private async Task<string?> StartOneAsync(
        IProjectionDaemon daemon,
        string storeKey,
        string databaseId,
        ShardName target,
        CancellationToken cancellationToken)
    {
        if (operations.RunningFor(storeKey, databaseId).FirstOrDefault(x =>
                string.Equals(x.Kind, "Rebuild", StringComparison.Ordinal)
                && string.Equals(x.Target, target.Name, StringComparison.OrdinalIgnoreCase)) is { } rebuild)
        {
            return RefuseStart(storeKey, databaseId, target, RebuildingReason, true,
                target.Identity + " was not started: Marten Studio is rebuilding " + target.Name + " (operation " + rebuild.Id
                + "), and a continuous agent started now would run beside the replay. Start it once the rebuild has finished.");
        }

        if (DaemonLightUp.IsRebuilding(daemon, target))
        {
            return RefuseStart(storeKey, databaseId, target, RebuildingReason, true,
                target.Identity + " was not started: its projection is being rebuilt on this daemon right now, and the daemon "
                + "refuses a continuous start until the rebuild finishes.");
        }

        ISubscriptionAgent agent;

        try
        {
            agent = await daemon.StartAgentAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (ShardStartException exception)
        {
            ShardStartRefusal refusal = DaemonLightUp.Describe(exception);
            return RefuseStart(storeKey, databaseId, target, refusal.Reason, refusal.IsTransient, target.Identity + ": " + DaemonLightUp.Explain(refusal));
        }

        AgentStatus status = Read(() => agent.Status, AgentStatus.Stopped);

        if (status != AgentStatus.Running)
        {
            ShardFailure? failure = Read(() => agent.Failure, null);

            return RefuseStart(storeKey, databaseId, target,
                status == AgentStatus.Paused ? AgentPausedReason : null,
                false,
                target.Identity + " was not started: the daemon has an agent for it, and it is " + status
                + (failure is { Message.Length: > 0 } ? " after " + failure.Category + ": " + failure.Message : string.Empty) + ".");
        }

        controlState.ClearStartRefusal(storeKey, databaseId, target.Identity);
        return null;
    }

    private string RefuseStart(string storeKey, string databaseId, ShardName target, string? reason, bool? transient, string message)
    {
        controlState.RecordStartRefusal(new StudioStartRefusal(
            storeKey, databaseId, target.Identity, reason, transient, message, DateTimeOffset.UtcNow));

        return message;
    }

    /// <summary>
    /// The shards a start is about: the named one, or every registered shard of a bare projection name - the same
    /// expansion <c>StartAgentAsync(string)</c> makes. Empty for a name this store does not register.
    /// </summary>
    private static IReadOnlyList<ShardName> StartTargetsOf(IDocumentStore store, string shardName)
    {
        IReadOnlyList<ShardName> registered = DaemonCheckConfiguration.AsyncShardsOf(store);

        if (shardName.Contains(':', StringComparison.Ordinal))
        {
            return [.. registered.Where(x => string.Equals(x.Identity, shardName, StringComparison.Ordinal))];
        }

        return [.. registered.Where(x => string.Equals(x.Name, shardName, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Why a rebuild would not be started, when the daemon here says this projection is already being rebuilt and it is
    /// not Marten Studio doing it - "already rebuilding" as a state, not a failure.
    /// </summary>
    /// <remarks>
    /// A rebuild the studio is running is answered by the operation tracker with its own handle, as it always was. One
    /// started elsewhere is visible only where <c>IsRebuilding</c> exists (JasperFx 2.86+); there a second rebuild would
    /// throw <c>ProjectionAlreadyRebuildingException</c> inside the detached operation, so it is refused before one is
    /// started. Below 2.86 a second rebuild is the daemon's to arbitrate, as before.
    /// </remarks>
    private string? RebuildRefusal(IProjectionDaemon daemon, ResolvedScope resolved, ISubscriptionSource source)
    {
        bool studioRebuilding = operations.RunningFor(resolved.Registration.Key, resolved.Database.Id.Identity)
            .Any(x => string.Equals(x.Kind, "Rebuild", StringComparison.Ordinal)
                && string.Equals(x.Target, source.Name, StringComparison.OrdinalIgnoreCase));

        if (studioRebuilding || !IsBeingRebuilt(daemon, source))
        {
            return null;
        }

        return source.Name + " is already being rebuilt on this daemon, by something other than Marten Studio. Nothing was "
            + "started; the projections screen shows it as rebuilding until the daemon says it has finished.";
    }

    /// <summary>The registered projections the daemon here says it is rebuilding (JasperFx 2.86+; empty below).</summary>
    private static List<string> RebuildingProjectionsOf(IProjectionDaemon daemon, IDocumentStore store)
    {
        List<string> rebuilding = [];

        if (!DaemonLightUp.CanTellRebuilding)
        {
            return rebuilding;
        }

        foreach (ISubscriptionSource source in store.Options.Events.Projections())
        {
            if (source.Lifecycle == ProjectionLifecycle.Async && IsBeingRebuilt(daemon, source))
            {
                rebuilding.Add(source.Name);
            }
        }

        return rebuilding;
    }

    private static bool IsBeingRebuilt(IProjectionDaemon daemon, ISubscriptionSource source)
    {
        foreach (string identity in ShardIdentities(source))
        {
            if (ShardName.TryParse(identity, out ShardName? shard) && shard is not null && DaemonLightUp.IsRebuilding(daemon, shard))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How long a stopping shard may take to finish its page: the store's own <c>StopAndDrainTimeout</c>.</summary>
    internal static TimeSpan StopAndDrainTimeoutOf(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Options.Events.Daemon.StopAndDrainTimeout;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>
    /// <see cref="StopAndDrainTimeoutOf" /> in whole milliseconds, for the daemon card - <c>0</c> for no bound. JasperFx
    /// cancels a drain only after a timeout greater than zero (<c>stopAndDrainCancellation</c>, read at JasperFx.Events
    /// 2.74.0), so zero, a negative span and <c>Timeout.InfiniteTimeSpan</c> all let a stopping agent finish its page
    /// however long it takes.
    /// </summary>
    private static int StopAndDrainMillisecondsOf(IDocumentStore store) =>
        StopAndDrainTimeoutOf(store) is { } timeout && timeout > TimeSpan.Zero
            ? (int) Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue)
            : 0;

    private static string? Text(Npgsql.NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// <c>ShardStartFailureReason.Rebuilding</c>'s name, spelled here because the floor's JasperFx has no such enum - so
    /// a refusal the studio classifies itself reads the same as one JasperFx classified.
    /// </summary>
    private const string RebuildingReason = "Rebuilding";

    /// <summary><c>ShardStartFailureReason.AgentPaused</c>'s name, for the same reason.</summary>
    private const string AgentPausedReason = "AgentPaused";

    /// <summary>The services the Explain's health context is given: none - the Explain asks for none.</summary>
    private sealed class NoServices : IServiceProvider
    {
        public static NoServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
