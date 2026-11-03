using System.Globalization;

using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Connection;

/// <summary>What the Connection page reads, through this visitor's gates.</summary>
/// <remarks>An interface so the page can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IConnectionInsightService
{
    /// <summary>
    /// The visitor's view of the scope's database: how the application connects, what the server saw, and what is worth a look.
    /// Never throws but for cancellation; a refusal is a value.
    /// </summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped, because a connection belongs to no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    Task<ConnectionPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// "This application's sessions": the sessions the studio's login has in this database, by application. The activity gate's -
    /// <c>ViewDatabaseActivity</c>, then the store policy and the write policy for the database as a whole - refused with a value
    /// and audited; the first read in a circuit is one ring entry.
    /// </summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    Task<ConnectionApplicationsView> GetApplicationsAsync(StudioScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Connection page's per-visitor half (NP3): the scope resolved for the database as a whole, the shared facts and the
/// visitor's Health report arranged by <see cref="ConnectionPageBuilder" />, and the application's own sessions behind the
/// activity gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two gates</b> (AGENTS.md D29). Everything on the page but the sessions is about the database's connection as a whole - the
/// host the application connects to, its role, its pool, what the server reports of the studio's connection - and is shown to a
/// visitor the source policy passes for the database with <c>TenantId = null</c>: the Health page's <see cref="HealthGate.Database" />.
/// The sessions are other programs' activity, as Database › Sessions' are: <c>ViewDatabaseActivity</c>, then the write policy asked
/// with the capability named, tenant-less - and read only for a visitor who passes. No new capability.
/// </para>
/// <para>
/// <b>About the store and database the scope resolved to, by their own names</b> (D29): the resolver's first question takes the
/// visitor's spelling, every later question and audit entry the registered key and the resolved identity.
/// </para>
/// <para>
/// <b>Not audited, but for the sessions.</b> The page is a read of configuration, like Marten Studio's Configuration page, and
/// records nothing. What names other programs' activity - the sessions, and the pool's peak counted by application name - is one
/// ring entry under <c>ViewDatabaseActivity</c> for the first reveal per circuit, per store and database; every refusal of it is
/// audited (hard rule 5).
/// </para>
/// <para>
/// <b>No connection string leaves the facts.</b> This service sees redacted records only; a contributor is handed the same.
/// </para>
/// </remarks>
internal sealed class ConnectionInsightService : IConnectionInsightService
{
    /// <summary>What the audit ring calls showing this application's sessions.</summary>
    internal const string ApplicationsAction = "Read application sessions";

    /// <summary>What the audit ring says was shown.</summary>
    internal const string ApplicationsTarget = "this application's sessions by application name";

    private const string LedgerSchema = "(connection)";
    private const string LedgerRelation = "applications";
    private const string LedgerKind = "details";
    private const string Signature = "applications";

    private readonly IStudioSettings settings;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly StudioActionLog audit;
    private readonly IHealthService health;
    private readonly IConnectionFactsSource facts;
    private readonly SessionPoolObserver pools;
    private readonly IEnumerable<IConnectionInsightContributor> contributors;
    private readonly ILogger<ConnectionInsightService> logger;
    private readonly ConnectionHistoryReader? history;
    private readonly ConnectionHistorySampler? sampler;
    private readonly TableRowAuditLedger ledger = new();

    public ConnectionInsightService(
        IStudioSettings settings,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        StudioActionLog audit,
        IHealthService health,
        IConnectionFactsSource facts,
        SessionPoolObserver pools,
        IEnumerable<IConnectionInsightContributor> contributors,
        ILogger<ConnectionInsightService> logger,
        ConnectionHistoryReader? history = null,
        ConnectionHistorySampler? sampler = null)
    {
        this.settings = settings;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.resolver = resolver;
        this.audit = audit;
        this.health = health;
        this.facts = facts;
        this.pools = pools;
        this.contributors = contributors;
        this.logger = logger;
        this.history = history;
        this.sampler = sampler;
    }

    /// <summary>How many first reveals of application sessions this circuit has recorded, for a test.</summary>
    internal int RecordedReads => ledger.Count;

    /// <inheritdoc />
    public async Task<ConnectionPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
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
            return ConnectionPageView.Refused(ConnectionRefusal.StorePolicy, StorePolicyDenial());
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return ConnectionPageView.Refused(ConnectionRefusal.Unavailable, exception.Message);
        }

        StudioScope whole = WholeDatabase(resolved);

        if (!await StorePolicyPassesAsync(asked, whole, cancellationToken).ConfigureAwait(false))
        {
            return ConnectionPageView.Refused(ConnectionRefusal.StorePolicy, StorePolicyDenial());
        }

        SessionDetailsAccess activity = await AccessAsync(whole, cancellationToken).ConfigureAwait(false);

        // The report first: it starts (or joins) the regular sweep, whose shared read of the facts the page then takes.
        HealthReport? report = null;
        string? reportNote = null;

        try
        {
            report = await health.GetReportAsync(asked, HealthReportRequest.Regular, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reportNote = "The health advisor's findings could not be read (" + Describe(exception) + ").";
        }

        ConnectionFacts read;

        try
        {
            read = await facts.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} could not read the connection facts of {DatabaseId}", settings.Text.ProductName, whole.DatabaseId);
            }

            return ConnectionPageView.Refused(
                ConnectionRefusal.Unavailable,
                PostgresFailure.IsConnectionFailure(exception)
                    ? PostgresFailure.CouldNotConnect(exception)
                    : "The connection settings could not be read (" + Describe(exception) + ").");
        }

        ConnectionPoolPeak? peak = await PoolPeakAsync(resolved, whole, read.Settings, activity, cancellationToken).ConfigureAwait(false);

        List<ConnectionContribution> contributions = [];
        ConnectionContributorContext context = new(resolved, read.Settings, read.Observed);

        foreach (IConnectionInsightContributor contributor in contributors)
        {
            try
            {
                if (await contributor.ContributeAsync(context, cancellationToken).ConfigureAwait(false) is { } contribution)
                {
                    contributions.Add(contribution);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A contributor cannot take the page down; the type's name is what a developer needs.
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(exception, "A Connection page contributor ({Contributor}) failed for {DatabaseId}", contributor.GetType().Name, whole.DatabaseId);
                }
            }
        }

        return ConnectionPageBuilder.Build(new ConnectionPageInputs
        {
            Facts = read,
            Report = report,
            ReportNote = reportNote,
            Pool = SessionActivityService.PoolOf(pools, resolved),
            PoolPeak = peak,
            Contributions = contributions,
            Activity = activity,
            MeasuredAt = DateTimeOffset.UtcNow,
        });
    }

    /// <inheritdoc />
    public async Task<ConnectionApplicationsView> GetApplicationsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        StudioScope asked = scope with { TenantId = null };

        // 1. The capability, before anybody is asked who is asking.
        try
        {
            capabilities.Require(StudioCapability.ViewDatabaseActivity);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            audit.RecordCapabilityDenied(denied, ApplicationsAction, ApplicationsTarget);

            return denied.Reason == CapabilityDenialReason.ReadOnly
                ? ConnectionApplicationsView.Refused(DatabaseRefusal.ReadOnly, settings.Text.SessionsReadOnlyDenial)
                : ConnectionApplicationsView.Refused(DatabaseRefusal.CapabilityOff, settings.Text.SessionsCapabilityDenial);
        }

        // 2. The scope, resolved with no tenant: the source policy, which the resolver asks before it looks anything up.
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(asked, DatabaseAccess.StorePolicyName(settings), ApplicationsAction, ApplicationsTarget, StudioCapability.ViewDatabaseActivity);
            return ConnectionApplicationsView.Refused(DatabaseRefusal.StorePolicy, settings.Text.SessionsStorePolicyDenial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            audit.Record(ApplicationsAction, ApplicationsTarget, succeeded: false, exception.Message, StudioCapability.ViewDatabaseActivity, asked);
            return ConnectionApplicationsView.Refused(DatabaseRefusal.Unavailable, exception.Message);
        }

        // 3. The two policies, of the store and database it resolved to, by their own names (D29), one at a time.
        StudioScope whole = WholeDatabase(resolved);

        if (!await StorePolicyPassesAsync(asked, whole, cancellationToken).ConfigureAwait(false))
        {
            audit.RecordScopeDenied(whole, DatabaseAccess.StorePolicyName(settings), ApplicationsAction, ApplicationsTarget, StudioCapability.ViewDatabaseActivity);
            return ConnectionApplicationsView.Refused(DatabaseRefusal.StorePolicy, settings.Text.SessionsStorePolicyDenial);
        }

        if (!await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false))
        {
            audit.RecordScopeDenied(whole, DatabaseAccess.WritePolicyName(settings), ApplicationsAction, ApplicationsTarget, StudioCapability.ViewDatabaseActivity);
            return ConnectionApplicationsView.Refused(DatabaseRefusal.WritePolicy, settings.Text.SessionsWritePolicyDenial);
        }

        // 4. Only now anything about the sessions.
        try
        {
            ConnectionApplicationsRead read = await facts.ReadApplicationsAsync(resolved, cancellationToken).ConfigureAwait(false);
            ConnectionFacts connection = await facts.GetAsync(resolved, cancellationToken).ConfigureAwait(false);

            RecordFirstReveal(resolved, whole, "Application sessions shown: " + read.Rows.Count.ToString(CultureInfo.InvariantCulture) + " of "
                + read.ApplicationCount.ToString(CultureInfo.InvariantCulture) + " applications.");

            return new ConnectionApplicationsView
            {
                MeasuredAt = read.ReadAt,
                Role = connection.Observed?.SessionUser,
                Rows = read.Rows,
                ApplicationCount = read.ApplicationCount,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} could not read the application sessions of {DatabaseId}", settings.Text.ProductName, whole.DatabaseId);
            }

            return ConnectionApplicationsView.Refused(
                DatabaseRefusal.Unavailable,
                postgres?.SqlState == PostgresErrorCodes.InsufficientPrivilege
                    ? "The role the studio reads as may not read the database's sessions (SQLSTATE 42501)."
                    : PostgresFailure.IsConnectionFailure(exception)
                        ? PostgresFailure.CouldNotConnect(exception)
                        : "The application's sessions could not be read (" + Describe(exception) + ").");
        }
    }

    /// <summary>
    /// The pool's busiest moment over the connection history, by this application's name - for a visitor through the activity
    /// gate, and one ring entry for the reveal. <see langword="null" /> without a history, without an application name or a pool.
    /// </summary>
    private async Task<ConnectionPoolPeak?> PoolPeakAsync(
        ResolvedSource resolved,
        StudioScope whole,
        ConnectionSettings connection,
        SessionDetailsAccess activity,
        CancellationToken cancellationToken)
    {
        if (!activity.IsOpen
            || history is null
            || !connection.Readable
            || !connection.Values.Pooling
            || connection.Values.Text("Application Name") is not { } application
            || ConnectionHistoryLookup.ServerOf(resolved) is not { } server)
        {
            return null;
        }

        ConnectionHistoryRead read = await ConnectionHistoryLookup.ReadAsync(history, sampler, server, names: true, null, cancellationToken).ConfigureAwait(false);

        if (read.View?.Applications.FirstOrDefault(x => string.Equals(x.Application ?? string.Empty, application, StringComparison.Ordinal)) is not { } own)
        {
            return null;
        }

        RecordFirstReveal(resolved, whole, "The pool's peak shown, counted by application name.");

        return new ConnectionPoolPeak(own.Peak, connection.Values.MaxPoolSize, WindowText(read.Window) + ", counted by application name");
    }

    /// <summary>"7 days", "36 hours".</summary>
    internal static string WindowText(TimeSpan window)
    {
        if (window >= TimeSpan.FromDays(1) && window.Ticks % TimeSpan.TicksPerDay == 0)
        {
            int days = (int) window.TotalDays;
            return days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " day" : " days");
        }

        int hours = Math.Max(1, (int) Math.Round(window.TotalHours));
        return hours.ToString(CultureInfo.InvariantCulture) + (hours == 1 ? " hour" : " hours");
    }

    /// <summary>
    /// The page's question, never audited: whether the application's sessions would be read. Asked the way
    /// <see cref="GetApplicationsAsync" /> will ask it, so a section the page draws open is one the service then serves.
    /// </summary>
    private async Task<SessionDetailsAccess> AccessAsync(StudioScope whole, CancellationToken cancellationToken)
    {
        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            // Nobody is asked about a capability the process does not have.
            return capabilities.ReadOnly
                ? new SessionDetailsAccess(false, true, null, DatabaseRefusal.ReadOnly, settings.Text.SessionsReadOnlyDenial)
                : new SessionDetailsAccess(false, false, null, DatabaseRefusal.CapabilityOff, settings.Text.SessionsCapabilityDenial);
        }

        return await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false)
            ? new SessionDetailsAccess(true, false, true, DatabaseRefusal.None, null)
            : new SessionDetailsAccess(true, false, false, DatabaseRefusal.WritePolicy, settings.Text.SessionsWritePolicyDenial);
    }

    private static StudioScope WholeDatabase(ResolvedSource resolved) => new(resolved.Source.Key, resolved.Target.Identity, null);

    /// <summary>
    /// The source policy for the resolved source and database. The resolver has asked it already when the visitor spelt both
    /// that way, so it is asked again only when either spelling was another - an empty database, or another case.
    /// </summary>
    private async ValueTask<bool> StorePolicyPassesAsync(StudioScope asked, StudioScope whole, CancellationToken cancellationToken) =>
        (string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal))
        || await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false);

    /// <summary>One ring entry and one event 9245-style line for the first reveal of application-grade data in this circuit.</summary>
    private void RecordFirstReveal(ResolvedSource resolved, StudioScope whole, string message)
    {
        if (!ledger.TryRecord(TableRowAuditLedger.Key(resolved.Source.Key, resolved.Target.Identity, LedgerSchema, LedgerRelation, LedgerKind), Signature, out _))
        {
            return;
        }

        audit.Record(ApplicationsAction, ApplicationsTarget, succeeded: true, message, StudioCapability.ViewDatabaseActivity, whole);
    }

    private string StorePolicyDenial()
    {
        StudioText text = settings.Text;
        string policy = text.Product == StudioProduct.PostgresStudio ? text.OptionName("SourceAuthorizationPolicy") : text.OptionName("StoreAuthorizationPolicy");

        return "Your account may not see how this application connects. The policy (" + policy + ") refused this " + text.SourceNoun
            + " and database as a whole, with no tenant selected: a connection belongs to no tenant.";
    }

    /// <summary>An exception as the page may say it: its SQLSTATE, or its type - never its message, which can name what a gate withholds.</summary>
    private static string Describe(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is { } postgres
            ? "SQLSTATE " + postgres.SqlState
            : exception.GetType().Name;
}
