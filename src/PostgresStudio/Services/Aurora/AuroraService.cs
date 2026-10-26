using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Aurora;

/// <summary>What the Server page's Aurora section reads: the database's Aurora facts, through this visitor's gates.</summary>
/// <remarks>An interface so the section can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IAuroraService
{
    /// <summary>The visitor's view of the scope's database. Never throws but for cancellation; a refusal is a value.</summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped, because a server belongs to no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    Task<AuroraView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default);
}

/// <summary>One wait event as the section shows it: its total since the instance started, and its share of the window.</summary>
/// <param name="Event">The event, cumulative.</param>
/// <param name="WindowMicroseconds">The time waited for it over the window, or <see langword="null" /> before two samples.</param>
/// <param name="WindowWaits">The waits over the window, or <see langword="null" />.</param>
internal sealed record AuroraWaitRow(AuroraWaitEvent Event, double? WindowMicroseconds, double? WindowWaits);

/// <summary>The wait profile as the section shows it.</summary>
/// <param name="Rows">The <see cref="AuroraWaitProfile.Shown" /> events with the most time over the window - or, before two samples, since the instance started.</param>
/// <param name="Window">How long the window is, or <see langword="null" /> when it is "since the instance started".</param>
/// <param name="IdleLeftOut">How many idle <c>Activity</c> events were left out.</param>
internal sealed record AuroraWaitsView(IReadOnlyList<AuroraWaitRow> Rows, TimeSpan? Window, int IdleLeftOut);

/// <summary>One DML kind as the section shows it.</summary>
/// <param name="Kind">The kind's counts and cumulative latency.</param>
/// <param name="WindowCount">How many ran over the window, or <see langword="null" />.</param>
/// <param name="WindowMeanMicroseconds">Their mean latency over the window, or <see langword="null" />.</param>
internal sealed record AuroraDmlRow(AuroraDmlKind Kind, double? WindowCount, double? WindowMeanMicroseconds);

/// <summary>The commit latency and DML as the section shows them.</summary>
/// <param name="Activity">The cumulative read.</param>
/// <param name="WindowCommitMeanMicroseconds">The mean commit latency over the window: Δ latency / Δ commits, or <see langword="null" />.</param>
/// <param name="WindowCommits">The commits over the window, or <see langword="null" />.</param>
/// <param name="Window">How long the window is, or <see langword="null" />.</param>
/// <param name="Dml">The four kinds.</param>
internal sealed record AuroraDatabaseView(
    AuroraDatabaseActivity Activity,
    double? WindowCommitMeanMicroseconds,
    double? WindowCommits,
    TimeSpan? Window,
    IReadOnlyList<AuroraDmlRow> Dml);

/// <summary>The Server page's Aurora section, for one visitor.</summary>
internal sealed record AuroraView
{
    /// <summary>Why nothing is shown - the store policy refused the database as a whole - or <see langword="null" />.</summary>
    public string? Refused { get; init; }

    /// <summary>Whether the server is Aurora: off Aurora the section shows only a capacity reading, if a source gave one.</summary>
    public bool IsAurora { get; init; }

    /// <summary>When the facts were read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>"instance x (the writer), Aurora 16.4.1", or <see langword="null" />.</summary>
    public string? AnsweredBy { get; init; }

    /// <summary>Why the instance is not named, when Aurora's version and instance could not be read.</summary>
    public string? InstanceNotRead { get; init; }

    /// <summary>When the instance that answered started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The host's capacity reading this interval.</summary>
    public CapacityReading? Capacity { get; init; }

    /// <summary>The cluster's instances.</summary>
    public ServerRead<AuroraReplicaStatus>? Replicas { get; init; }

    /// <summary>The wait profile.</summary>
    public ServerRead<AuroraWaitsView>? Waits { get; init; }

    /// <summary>The commit latency and DML.</summary>
    public ServerRead<AuroraDatabaseView>? Database { get; init; }

    /// <summary>Whether the per-session waits are shown to this visitor (the activity gate).</summary>
    public bool SessionsShown { get; init; }

    /// <summary>What it would take to see them, when they are not shown.</summary>
    public string? SessionsWithheld { get; init; }

    /// <summary>The per-session waits, through the activity gate.</summary>
    public ServerRead<AuroraSessionWaits>? Sessions { get; init; }

    /// <summary>Every statement the facts read ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];
}

/// <summary>
/// The Aurora section's per-visitor half (issue #8 A1, A3, A4, A8; W5-AUR): the scope resolved for the database as a whole,
/// the shared facts shown through the database-wide gate, and the per-session waits only through the activity gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two gates</b> (AGENTS.md D29). The instance's identifier, the cluster's instances and their lag, the instance's waits and
/// this database's commit latency are the server's and the database's as a whole - an instance id is the cluster's own name for
/// one of its instances, not a session's, a role's or a client's - so they are shown to a visitor the store policy passes for the
/// database with <c>TenantId = null</c>: the Health page's <see cref="HealthGate.Database" />. A session's process id, role,
/// application and what it waits for are other people's activity: <c>ViewDatabaseActivity</c>, then the write policy with the
/// capability named - and they are read only for a visitor who passes, so a visitor who may not see them costs nothing.
/// </para>
/// <para>
/// <b>Audited by signature, never per poll</b>, as the Replication page's names are: the first view in a circuit that shows a
/// session is one Activity entry under <c>ViewDatabaseActivity</c>, per store and database.
/// </para>
/// <para>
/// <b>The rates are the advisor's</b>: each wait's and the commit latency's window is the difference between this read and the
/// oldest of the health sweep's samples on the same instance (<see cref="AuroraSamples" />), read at the database-wide gate. The
/// section starts no sweep; before two samples it shows the totals since the instance started.
/// </para>
/// </remarks>
internal sealed class AuroraService : IAuroraService
{
    /// <summary>What the audit ring calls showing sessions' waits.</summary>
    internal const string SessionsAction = "Read Aurora session waits";

    /// <summary>What the audit ring says was shown.</summary>
    internal const string SessionsTarget = "sessions' process ids, roles, applications and waits";

    private readonly IStudioSettings settings;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly StudioActionLog audit;
    private readonly AuroraFactsCache facts;
    private readonly HealthHistory history;
    private readonly ILogger<AuroraService> logger;
    private readonly TableRowAuditLedger ledger = new();

    public AuroraService(
        IStudioSettings settings,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        StudioActionLog audit,
        AuroraFactsCache facts,
        HealthHistory history,
        ILogger<AuroraService> logger)
    {
        this.settings = settings;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.resolver = resolver;
        this.audit = audit;
        this.facts = facts;
        this.history = history;
        this.logger = logger;
    }

    /// <summary>How many first reads of sessions this circuit has recorded, for a test.</summary>
    internal int RecordedReads => ledger.Count;

    /// <inheritdoc />
    public async Task<AuroraView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        StudioScope asked = scope with { TenantId = null };
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return new AuroraView { Refused = StorePolicyDenial() };
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return new AuroraView { Refused = exception.Message };
        }

        StudioScope whole = new(resolved.Source.Key, resolved.Target.Identity, null);
        bool samePlace = string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal);

        if (!samePlace && !await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return new AuroraView { Refused = StorePolicyDenial() };
        }

        AuroraFacts read;

        try
        {
            read = await facts.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} did not read the Aurora facts of {DatabaseId}", settings.Text.ProductName, whole.DatabaseId);
            }

            PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
            return new AuroraView
            {
                Refused = PostgresFailure.IsConnectionFailure(exception)
                    ? "The studio could not connect to this database."
                    : "The server's version and platform could not be read" + (postgres is null ? " (" + exception.GetType().Name + ")." : " (SQLSTATE " + postgres.SqlState + ")."),
            };
        }

        HealthHistoryView samples = history.Read(resolved.Source.Key, resolved.Target.Identity).VisibleAt(HealthGate.Database);
        AuroraView view = Build(read, samples);

        if (!view.IsAurora)
        {
            return view;
        }

        (bool shown, string? withheld) = await SessionsGateAsync(whole, cancellationToken).ConfigureAwait(false);

        if (!shown)
        {
            return view with { SessionsShown = false, SessionsWithheld = withheld };
        }

        ServerRead<AuroraSessionWaits> sessions;

        try
        {
            sessions = ServerRead<AuroraSessionWaits>.Of(await facts.GetSessionsAsync(resolved, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} did not read the Aurora session waits of {DatabaseId}", settings.Text.ProductName, whole.DatabaseId);
            }

            sessions = new ServerRead<AuroraSessionWaits>(null, postgres?.SqlState == PostgresErrorCodes.InsufficientPrivilege
                ? "The role the studio reads as may not read the sessions' waits (SQLSTATE 42501)."
                : "The sessions' waits could not be read" + (postgres is null ? " (" + exception.GetType().Name + ")." : " (SQLSTATE " + postgres.SqlState + ")."));
        }

        if (sessions.Value is { Sessions.Count: > 0 })
        {
            RecordFirstRead(resolved, whole);
        }

        return view with { SessionsShown = true, Sessions = sessions };
    }

    /// <summary>
    /// The visitor's view of <paramref name="read" /> with the window deltas from <paramref name="samples" /> - everything but the
    /// sessions, which need the activity gate. Pure, for the unit tests of the arithmetic.
    /// </summary>
    internal static AuroraView Build(AuroraFacts read, HealthHistoryView samples)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(samples);

        HealthHistoryView same = read.IsAurora ? AuroraSamples.SameInstance(samples, read.Server) : HealthHistoryView.Empty;

        return new AuroraView
        {
            IsAurora = read.IsAurora,
            MeasuredAt = read.ReadAt,
            AnsweredBy = read.Server.AnsweredBy,
            InstanceNotRead = read.Server.InstanceNotRead,
            StartedAt = read.Server.StartedAt,
            Capacity = read.Capacity,
            Replicas = read.Replicas,
            Waits = read.Waits switch
            {
                null => null,
                { Value: { } profile } => ServerRead<AuroraWaitsView>.Of(Waits(profile, same, read.ReadAt)),
                { } failed => new ServerRead<AuroraWaitsView>(null, failed.Note),
            },
            Database = read.Database switch
            {
                null => null,
                { Value: { } activity } => ServerRead<AuroraDatabaseView>.Of(Database(activity, same, read.ReadAt)),
                { } failed => new ServerRead<AuroraDatabaseView>(null, failed.Note),
            },
            Statements = read.Statements,
        };
    }

    /// <summary>
    /// The <see cref="AuroraWaitProfile.Shown" /> events with the most time over the window - each event's own window, from the
    /// oldest sample that has it to this read - or, while no event has a window yet, the most time since the instance started.
    /// </summary>
    internal static AuroraWaitsView Waits(AuroraWaitProfile profile, HealthHistoryView samples, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(samples);

        List<(AuroraWaitRow Row, TimeSpan? Span)> rows =
        [
            .. profile.Events.Select(wait =>
            {
                CounterWindow? window = AuroraSamples.Window(samples, wait.TimeSeries, wait.TimeMicroseconds, wait.CountSeries, wait.Waits, now);
                return (new AuroraWaitRow(wait, window?.Gains[0], window?.Gains[1]), window?.Span);
            }),
        ];

        if (rows.Any(static x => x.Span is not null))
        {
            List<(AuroraWaitRow Row, TimeSpan? Span)> windowed =
            [
                .. rows.Where(static x => x.Span is not null)
                    .OrderByDescending(static x => x.Row.WindowMicroseconds)
                    .ThenByDescending(static x => x.Row.Event.TimeMicroseconds)
                    .Take(AuroraWaitProfile.Shown),
            ];

            return new AuroraWaitsView([.. windowed.Select(static x => x.Row)], windowed.Max(static x => x.Span), profile.IdleLeftOut);
        }

        return new AuroraWaitsView([.. rows.Take(AuroraWaitProfile.Shown).Select(static x => x.Row)], null, profile.IdleLeftOut);
    }

    /// <summary>The commit latency over the window - Δ latency over Δ <c>xact_commit</c> - and each DML kind's count and mean.</summary>
    internal static AuroraDatabaseView Database(AuroraDatabaseActivity activity, HealthHistoryView samples, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(samples);

        CounterWindow? commits = activity.CommitLatencyMicroseconds is { } latency
            ? AuroraSamples.Window(samples, AuroraSamples.CommitLatencySeries, latency, AuroraSamples.CommitsSeries, activity.Commits, now)
            : null;

        double? commitMean = commits is { } window && window.Gains[1] > 0 ? window.Gains[0] / window.Gains[1] : null;

        List<AuroraDmlRow> dml = [];
        TimeSpan? span = commits?.Span;

        foreach (AuroraDmlKind kind in activity.Dml)
        {
            CounterWindow? kindWindow = kind is { Count: { } count, LatencyMicroseconds: { } kindLatency }
                ? AuroraSamples.Window(samples, AuroraSamples.DmlCountPrefix + kind.Kind, count, AuroraSamples.DmlLatencyPrefix + kind.Kind, kindLatency, now)
                : null;

            span ??= kindWindow?.Span;
            dml.Add(new AuroraDmlRow(
                kind,
                kindWindow?.Gains[0],
                kindWindow is { } w && w.Gains[0] > 0 ? w.Gains[1] / w.Gains[0] : null));
        }

        return new AuroraDatabaseView(activity, commitMean, commits?.Gains[1], span, dml);
    }

    /// <summary>The activity gate, in the database browser's order: the capability, then the write policy - tenant-less.</summary>
    private async Task<(bool Shown, string? Withheld)> SessionsGateAsync(StudioScope whole, CancellationToken cancellationToken)
    {
        StudioText text = settings.Text;
        string capability = text.OptionName("Capabilities.ViewDatabaseActivity");

        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            return (false, capabilities.ReadOnly
                ? "Each session's waits need " + capability + ", and " + settings.ReadOnlyOption() + " is true, which turns every capability off."
                : "Each session's waits - its process id, role and application - need " + capability + ".");
        }

        return await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false)
            ? (true, null)
            : (false, "Your account's write policy (" + text.OptionName("WriteAuthorizationPolicy") + ") did not allow ViewDatabaseActivity for this database, so no session is shown.");
    }

    private string StorePolicyDenial()
    {
        StudioText text = settings.Text;
        string policy = text.Product == StudioProduct.PostgresStudio ? text.OptionName("SourceAuthorizationPolicy") : text.OptionName("StoreAuthorizationPolicy");

        return "Your account may not see this database's server. The policy (" + policy + ") refused this " + text.SourceNoun
            + " and database as a whole, with no tenant selected: the server belongs to no tenant.";
    }

    private void RecordFirstRead(ResolvedSource resolved, StudioScope whole)
    {
        if (!ledger.TryRecord(TableRowAuditLedger.Key(resolved.Source.Key, resolved.Target.Identity, "(aurora)", "sessions", "activity"), "sessions", out _))
        {
            return;
        }

        audit.Record(SessionsAction, SessionsTarget, succeeded: true, "Aurora session waits shown.", StudioCapability.ViewDatabaseActivity, whole);
    }
}
