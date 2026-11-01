using System.Globalization;
using System.Text.Json;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;

namespace PostgresStudio.Services.Aurora;

/// <summary>One wait event of an Aurora instance, cumulative since the instance started (<c>aurora_stat_system_waits()</c>).</summary>
/// <param name="TypeId">The wait type's id.</param>
/// <param name="EventId">The wait event's id.</param>
/// <param name="TypeName">The type's name - <c>IO</c>, <c>Lock</c>, <c>LWLock</c> - or its id when no name was found.</param>
/// <param name="EventName">The event's name - <c>XactSync</c> - or its id when no name was found.</param>
/// <param name="Waits">How many times it was waited for.</param>
/// <param name="TimeMicroseconds">How long it was waited for, in microseconds (the function's page).</param>
internal sealed record AuroraWaitEvent(long TypeId, long EventId, string TypeName, string EventName, double Waits, double TimeMicroseconds)
{
    /// <summary>"IO:XactSync", as Aurora's and PostgreSQL's documentation write a wait event.</summary>
    public string Name => TypeName + ":" + EventName;

    /// <summary>The history series of its time: <c>aurora.wait:IO:XactSync</c>.</summary>
    public string TimeSeries => AuroraSamples.WaitTimePrefix + Name;

    /// <summary>The history series of its count.</summary>
    public string CountSeries => AuroraSamples.WaitCountPrefix + Name;
}

/// <summary>
/// An Aurora instance's waits since it started - the one place a commit's wait for storage (<c>IO:XactSync</c>) shows, which a
/// snapshot of <c>pg_stat_activity</c> almost never catches (issue #8 A4) - most time first, the idle <c>Activity</c> type left
/// out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cumulative since the instance started, in microseconds</b>: "Statistics returned by this function are reset when a DB
/// instance restarts", and <c>wait_time</c> is "the total amount of time in microseconds spent waiting for this event"
/// (<i>aurora_stat_system_waits</i>, Aurora User Guide, read 2026-10-26). The rate over a window is the difference of two of the
/// advisor's samples (<see cref="AuroraSamples" />).
/// </para>
/// <para>
/// <b>Named by joining on <c>(type_id, event_id)</c></b> with <c>aurora_stat_wait_event()</c> and <c>aurora_stat_wait_type()</c>,
/// the join the function's own page shows; the event function's documented <c>type_name</c> is used only where the type
/// function has none (B0: its example has no such column).
/// </para>
/// <para>
/// <b>No finding.</b> Issue #8 names no threshold for a wait, and parity with host A's page is the user's question
/// (W5-index §7.1), so the profile is shown, never judged.
/// </para>
/// </remarks>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Events">Every event but <c>Activity</c>'s, most time first.</param>
/// <param name="IdleLeftOut">How many <c>Activity</c> events were left out.</param>
internal sealed record AuroraWaitProfile(DateTimeOffset ReadAt, IReadOnlyList<AuroraWaitEvent> Events, int IdleLeftOut)
{
    /// <summary>The memo key of the read.</summary>
    public const string Key = "aurora.waits";

    /// <summary>The wait type every background process's main loop waits in when it has nothing to do.</summary>
    public const string IdleType = "Activity";

    /// <summary>How many events, most time first, each sweep samples for the window deltas.</summary>
    public const int Sampled = 20;

    /// <summary>How many events the Server page shows: the most time over the window.</summary>
    public const int Shown = 10;

    /// <summary>
    /// The three arrays of <see cref="AuroraQueries.SystemWaits" />, joined on <c>(type_id, event_id)</c> and read by name; an
    /// event the name functions do not know is named by its ids, never dropped.
    /// </summary>
    public static AuroraWaitProfile FromJson(DateTimeOffset readAt, string? waits, string? events, string? types)
    {
        WaitNames names = WaitNames.FromJson(events, types);
        List<AuroraWaitEvent> listed = [];
        int idle = 0;

        foreach (JsonElement row in AuroraJson.Objects(waits))
        {
            if (AuroraJson.Number(row, "type_id") is not { } type || AuroraJson.Number(row, "event_id") is not { } @event)
            {
                continue;
            }

            AuroraWaitEvent wait = names.Event(
                (long) type,
                (long) @event,
                AuroraJson.Number(row, "waits") ?? 0,
                AuroraJson.Number(row, "wait_time") ?? 0);

            if (string.Equals(wait.TypeName, IdleType, StringComparison.Ordinal))
            {
                idle++;
                continue;
            }

            listed.Add(wait);
        }

        return new AuroraWaitProfile(
            readAt,
            [.. listed.OrderByDescending(static x => x.TimeMicroseconds).ThenBy(static x => x.TypeId).ThenBy(static x => x.EventId)],
            idle);
    }

    /// <summary>The sweep's one read of it, whichever asks first.</summary>
    public static Task<AuroraWaitProfile> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(Key, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<AuroraWaitProfile> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = AuroraQueries.SystemWaits();
        IReadOnlyList<(string? Waits, string? Events, string? Types)> rows = await context.Sql
            .QueryAsync(query, static reader => (Text(reader, 0), Text(reader, 1), Text(reader, 2)), cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0
            ? new AuroraWaitProfile(context.Now, [], 0)
            : FromJson(context.Now, rows[0].Waits, rows[0].Events, rows[0].Types);
    }

    private static string? Text(Npgsql.NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

/// <summary>Aurora's wait event and type names, by id - from <c>aurora_stat_wait_event()</c> and <c>aurora_stat_wait_type()</c>.</summary>
internal sealed class WaitNames
{
    private readonly Dictionary<(long Type, long Event), (string? Event, string? Type)> events = [];
    private readonly Dictionary<long, string> types = [];

    /// <summary>The two arrays, read by name.</summary>
    public static WaitNames FromJson(string? events, string? types)
    {
        WaitNames names = new();

        foreach (JsonElement row in AuroraJson.Objects(types))
        {
            if (AuroraJson.Number(row, "type_id") is { } type && AuroraJson.Text(row, "type_name") is { Length: > 0 } name)
            {
                names.types.TryAdd((long) type, name);
            }
        }

        foreach (JsonElement row in AuroraJson.Objects(events))
        {
            if (AuroraJson.Number(row, "type_id") is { } type && AuroraJson.Number(row, "event_id") is { } @event)
            {
                names.events.TryAdd(((long) type, (long) @event), (AuroraJson.Text(row, "event_name"), AuroraJson.Text(row, "type_name")));
            }
        }

        return names;
    }

    /// <summary>The event, named - its ids where no name is known.</summary>
    public AuroraWaitEvent Event(long type, long @event, double waits, double time)
    {
        events.TryGetValue((type, @event), out (string? Event, string? Type) known);

        string typeName = types.TryGetValue(type, out string? name) ? name
            : known.Type is { Length: > 0 } fromEvent ? fromEvent
            : "type " + type.ToString(CultureInfo.InvariantCulture);

        string eventName = known.Event is { Length: > 0 } eventText ? eventText : "event " + @event.ToString(CultureInfo.InvariantCulture);

        return new AuroraWaitEvent(type, @event, typeName, eventName, waits, time);
    }
}

/// <summary>One kind of DML as <c>aurora_stat_dml_activity(oid)</c> counts it: successful statements and their cumulative latency.</summary>
/// <param name="Kind"><c>select</c>, <c>insert</c>, <c>update</c> or <c>delete</c>.</param>
/// <param name="Count">How many ran, or <see langword="null" /> when the release reports none.</param>
/// <param name="LatencyMicroseconds">Their latency together, in microseconds.</param>
internal sealed record AuroraDmlKind(string Kind, double? Count, double? LatencyMicroseconds)
{
    /// <summary>The mean latency, or <see langword="null" /> before one ran.</summary>
    public double? MeanMicroseconds => Count is > 0 && LatencyMicroseconds is { } latency ? latency / Count.Value : null;
}

/// <summary>
/// This database's commit latency and DML activity on the Aurora instance that answered (issue #8 A4): cumulative since the
/// statistics were last reset - "You can reset this statistic by using ... <c>pg_stat_reset</c>" (both functions' pages, read
/// 2026-10-26) - in microseconds.
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Commits"><c>pg_stat_database.xact_commit</c>, the denominator of the mean commit latency.</param>
/// <param name="CommitLatencyMicroseconds"><c>aurora_stat_get_db_commit_latency(oid)</c>, or <see langword="null" />.</param>
/// <param name="Dml">The four kinds, in that order.</param>
internal sealed record AuroraDatabaseActivity(DateTimeOffset ReadAt, long Commits, double? CommitLatencyMicroseconds, IReadOnlyList<AuroraDmlKind> Dml)
{
    /// <summary>The memo key of the read.</summary>
    public const string Key = "aurora.database";

    /// <summary>The DML kinds, in the function's own order.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["select", "insert", "update", "delete"];

    /// <summary>The mean commit latency since the statistics began, or <see langword="null" /> before a commit.</summary>
    public double? MeanCommitMicroseconds => Commits > 0 && CommitLatencyMicroseconds is { } latency ? latency / Commits : null;

    /// <summary>The row of <see cref="AuroraQueries.Database" />: the commits, and the two functions' rows read by name.</summary>
    public static AuroraDatabaseActivity FromJson(DateTimeOffset readAt, long commits, string? commitLatency, string? dml)
    {
        JsonElement? activity = AuroraJson.Object(dml);

        return new AuroraDatabaseActivity(
            readAt,
            commits,
            AuroraJson.Scalar(commitLatency),
            [
                .. Kinds.Select(kind => new AuroraDmlKind(
                    kind,
                    activity is { } row ? AuroraJson.Number(row, kind + "_count") : null,
                    activity is { } latency ? AuroraJson.Number(latency, kind + "_latency_microsecs") : null)),
            ]);
    }

    /// <summary>The sweep's one read of it, whichever asks first.</summary>
    public static Task<AuroraDatabaseActivity> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(Key, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    /// <exception cref="HealthCheckUnavailableException"><c>pg_stat_database</c> has no row for this database.</exception>
    public static async Task<AuroraDatabaseActivity> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = AuroraQueries.Database();
        IReadOnlyList<(long Commits, string? Latency, string? Dml)> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0
            ? throw new HealthCheckUnavailableException("pg_stat_database has no row for this database, so its commit latency cannot be read.")
            : FromJson(context.Now, rows[0].Commits, rows[0].Latency, rows[0].Dml);
    }
}

/// <summary>One wait of one session: its count, and its share of the session's own wait time.</summary>
/// <param name="Name">"IO:XactSync".</param>
/// <param name="Waits">How many times the session waited for it.</param>
/// <param name="Share">Its share of every wait of the session's, by time - a ratio of two of the function's own numbers, whatever their unit.</param>
internal sealed record AuroraSessionWait(string Name, double Waits, double? Share);

/// <summary>One client session's waits since it connected (<c>aurora_stat_backend_waits(pid)</c>).</summary>
/// <param name="ProcessId">Its process id.</param>
/// <param name="User">Its role.</param>
/// <param name="Application">Its <c>application_name</c>.</param>
/// <param name="State">Its state.</param>
/// <param name="ConnectedAt">When it connected.</param>
/// <param name="Waits">How many waits it has had, every event together.</param>
/// <param name="Top">Its biggest waits by share, at most <see cref="AuroraSessionWaits.PerSession" />.</param>
internal sealed record AuroraSession(int ProcessId, string? User, string? Application, string? State, DateTimeOffset? ConnectedAt, double Waits, IReadOnlyList<AuroraSessionWait> Top);

/// <summary>
/// This database's sessions and what each has waited for - <b>counts and shares, never a time</b>: the function's page says
/// <c>wait_time</c> is in milliseconds where <c>aurora_stat_system_waits</c>' says microseconds, and the two pages' example
/// magnitudes agree with each other, so its unit is not known (B0). Who a session is, and what it waits for, is other
/// people's activity: shown only through the activity gate (D29), and read only for a visitor who passes it.
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Sessions">The longest-connected first, at most <see cref="AuroraQueries.MaxSessions" />.</param>
internal sealed record AuroraSessionWaits(DateTimeOffset ReadAt, IReadOnlyList<AuroraSession> Sessions)
{
    /// <summary>How many waits one session's line names.</summary>
    public const int PerSession = 5;

    /// <summary>One session's row of <see cref="AuroraQueries.BackendWaits" />, its waits read by name and named through <paramref name="names" />.</summary>
    public static AuroraSession Session(int processId, string? user, string? application, string? state, DateTimeOffset? connectedAt, string? waits, WaitNames names)
    {
        ArgumentNullException.ThrowIfNull(names);

        List<AuroraWaitEvent> events =
        [
            .. AuroraJson.Objects(waits)
                .Where(static x => AuroraJson.Number(x, "type_id") is not null && AuroraJson.Number(x, "event_id") is not null)
                .Select(x => names.Event(
                    (long) AuroraJson.Number(x, "type_id")!.Value,
                    (long) AuroraJson.Number(x, "event_id")!.Value,
                    AuroraJson.Number(x, "waits") ?? 0,
                    AuroraJson.Number(x, "wait_time") ?? 0)),
        ];

        double total = events.Sum(static x => x.TimeMicroseconds);

        return new AuroraSession(
            processId,
            user,
            application,
            state,
            connectedAt,
            events.Sum(static x => x.Waits),
            [
                .. events
                    .OrderByDescending(static x => x.TimeMicroseconds)
                    .ThenByDescending(static x => x.Waits)
                    .Take(PerSession)
                    .Select(x => new AuroraSessionWait(x.Name, x.Waits, total > 0 ? x.TimeMicroseconds / total : null)),
            ]);
    }
}

/// <summary>
/// What a regular sweep of an Aurora database samples for the Server page's window deltas (issue #8 A4): the instance's waits,
/// this database's commit latency and its DML, each a counter at the database-wide gate, and the instance's identity, so a
/// window never spans two instances.
/// </summary>
/// <remarks>
/// <b>No check writes them.</b> The profile raises no finding, so the sweep reads it itself once its gatherers have run - on
/// an Aurora server only - and adds these parts to its sample, as it adds the statistics window's (<c>HealthSweeper</c>). The
/// series are internal: never shown, exported or logged.
/// </remarks>
internal static class AuroraSamples
{
    /// <summary>The prefix of a wait event's time series: <c>aurora.wait:IO:XactSync</c>.</summary>
    public const string WaitTimePrefix = "aurora.wait:";

    /// <summary>The prefix of a wait event's count series.</summary>
    public const string WaitCountPrefix = "aurora.wait-count:";

    /// <summary>The answering instance's identity, hashed (<see cref="StatisticsWindow.Hash" />).</summary>
    public const string InstanceSeries = "aurora.instance";

    /// <summary>The cumulative commit latency, microseconds.</summary>
    public const string CommitLatencySeries = "aurora.commit-latency";

    /// <summary>The commits it is the latency of.</summary>
    public const string CommitsSeries = "aurora.commits";

    /// <summary>A DML kind's count: <c>aurora.dml-count:select</c>.</summary>
    public const string DmlCountPrefix = "aurora.dml-count:";

    /// <summary>A DML kind's cumulative latency: <c>aurora.dml-latency:select</c>.</summary>
    public const string DmlLatencyPrefix = "aurora.dml-latency:";

    /// <summary>The identity a sample carries for <paramref name="server" />'s instance - an unnamed one included.</summary>
    public static double InstanceOf(PostgresServerFacts server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return StatisticsWindow.Hash(server.Instance ?? string.Empty);
    }

    /// <summary>The parts one sweep adds, every one at <see cref="HealthGate.Database" />.</summary>
    public static IEnumerable<HealthSamplePart> Of(PostgresServerFacts server, AuroraWaitProfile? waits, AuroraDatabaseActivity? database)
    {
        ArgumentNullException.ThrowIfNull(server);

        yield return Part(InstanceSeries, InstanceOf(server));

        foreach (AuroraWaitEvent wait in waits?.Events.Take(AuroraWaitProfile.Sampled) ?? [])
        {
            yield return Part(wait.TimeSeries, wait.TimeMicroseconds);
            yield return Part(wait.CountSeries, wait.Waits);
        }

        if (database is null)
        {
            yield break;
        }

        if (database.CommitLatencyMicroseconds is { } latency)
        {
            yield return Part(CommitLatencySeries, latency);
            yield return Part(CommitsSeries, database.Commits);
        }

        foreach (AuroraDmlKind kind in database.Dml)
        {
            if (kind is { Count: { } count, LatencyMicroseconds: { } dmlLatency })
            {
                yield return Part(DmlCountPrefix + kind.Kind, count);
                yield return Part(DmlLatencyPrefix + kind.Kind, dmlLatency);
            }
        }

        static HealthSamplePart Part(string series, double value) => new(series, value) { Gate = HealthGate.Database };
    }

    /// <summary>
    /// The samples of <paramref name="history" /> taken on the same instance as <paramref name="server" /> - a window over the
    /// others would subtract one instance's counters from another's.
    /// </summary>
    public static HealthHistoryView SameInstance(HealthHistoryView history, PostgresServerFacts server)
    {
        ArgumentNullException.ThrowIfNull(history);

        double instance = InstanceOf(server);

        return new HealthHistoryView(
            [.. history.Samples.Where(x => x.Values.TryGetValue(InstanceSeries, out double value) && value == instance)],
            history.WatchingSince);
    }

    /// <summary>
    /// What one counter pair gained over the window of <paramref name="history" /> up to <paramref name="now" /> - or
    /// <see langword="null" /> before two samples, or across a reset (<see cref="ReplicationHistory.Window" />).
    /// </summary>
    public static CounterWindow? Window(HealthHistoryView history, string first, double firstNow, string second, double secondNow, DateTimeOffset now) =>
        ReplicationHistory.Window(history, [(first, firstNow), (second, secondNow)], now);
}
