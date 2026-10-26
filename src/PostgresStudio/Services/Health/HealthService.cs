using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services.Health;

/// <summary>
/// What the Health page, the Overview and the sidebar read about a database's health.
/// </summary>
/// <remarks>
/// An interface so a page can be rendered in a component test with a fake behind it - the same seam every
/// other area has (D12).
/// </remarks>
internal interface IHealthService
{
    /// <summary>
    /// The visitor's report for the scope's database: the shared sweep (run now if the cached one has expired),
    /// shown through the visitor's gates. Never throws but for cancellation; a refusal is a value.
    /// </summary>
    /// <param name="scope">The store and database; a tenant in it narrows what is shown, not what is swept.</param>
    /// <param name="request">
    /// Whether to run the costlier tiers, or only show a recent run of them. A request to run is honoured only for a
    /// visitor through that tier's gate - <see cref="HealthGate.Database" /> for tier 2, <see cref="HealthGate.Activity" />
    /// for tier 3 - and is otherwise read as <see cref="HealthDeeperChecks.IfCached" />; the report says why.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared sweep.</param>
    Task<HealthReport> GetReportAsync(StudioScope scope, HealthReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the navigation badge shows: the last sweep's counts for this visitor, unmuted, and how many of its
    /// findings they are not shown - or <see langword="null" /> when there is no recent sweep, or the scope's tenant
    /// is not one of the swept database's. Never starts a sweep. With no tenant in the scope it opens no connection;
    /// a tenant is placed in the swept database by the resolver's own check, which every page of that scope runs too.
    /// </summary>
    Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The findings with these codes that this visitor may see and has not muted, from the last regular sweep
    /// of the scope's database - for a banner on the screen a finding belongs to (D01 and D02 on Projections,
    /// T03-T05 on Schema). <b>Cache-only</b>, like <see cref="GlanceAsync" />: it never starts a sweep, and asks
    /// what the badge asks before it shows anything; with no recent sweep it answers an empty list.
    /// The gates are the Health page's, finding by finding and piece of evidence by piece of evidence.
    /// </summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="codes">The check codes the banner is about.</param>
    /// <param name="cancellationToken">Cancels the policy questions.</param>
    Task<IReadOnlyList<HealthReportFinding>> CachedFindingsAsync(
        StudioScope scope,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The health advisor's per-visitor half: it resolves the scope, takes the shared sweep and shows it through
/// this visitor's gates.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sweep is the database's; the report is the visitor's</b> (AGENTS.md D27). The scope is resolved
/// first - the store policy for the scope as selected, before anything is looked up - and the database is
/// then swept as a whole, once per interval for everybody (<see cref="HealthSweepCache" />). What this visitor
/// is shown of the result is decided here, finding by finding and piece of evidence by piece of evidence:
/// <see cref="HealthGate.None" /> to anyone the scope admitted, <see cref="HealthGate.Database" /> to a visitor
/// the store policy passes for the database with <c>TenantId = null</c>, and <see cref="HealthGate.Activity" />
/// only with <c>ViewDatabaseActivity</c> on and the write policy passing, asked with the capability named
/// against the same tenant-less resource. A finding about one tenant also needs the store policy for that
/// tenant. Each question is asked at most once per report.
/// </para>
/// <para>
/// <b>About the store and database the scope resolved to, by their own names</b> (AGENTS.md D29). Every tenant-less
/// question names the store's registered key and <c>Id.Identity</c> of the swept database - never the visitor's
/// spelling of either, a store key matching in any case and a database id often empty, meaning the store's first
/// database - because that is the resource this report's audit records and the high-water Explain asks about, so
/// one policy is asked one question about one database; the mute keys name it too. Only the resolver's own first
/// question takes the spelling: it is asked before anything is looked up, which is the point of it.
/// </para>
/// <para>
/// <b>Withheld is never "nothing found".</b> A finding a visitor is not shown is counted in the report's notice,
/// and the check it came from says so on its row: below the check's gate, "findings not shown to you" whether or not
/// there are any, so the row does not say which checks found something; through it, "N shown, others not shown to
/// you" when findings about tenants were withheld one by one (<see cref="HealthCheckStatus.FindingsWithheld" />).
/// </para>
/// <para>
/// <b>What the activity gate shows is audited, by signature.</b> Evidence behind <see cref="HealthGate.Activity" />
/// names other people's sessions - process ids, roles, applications, client addresses, a prepared transaction's
/// owner - which is what <c>ViewDatabaseActivity</c> guards on Database › Sessions too. So the first report or
/// cached read that shows any of it in a circuit is one Activity entry under that capability, per store and
/// database, and the next is recorded only when the set of checks showing it changes; the sidebar's glance shows no
/// evidence and records nothing.
/// </para>
/// <para>
/// <b>Postgres' words are masked, and only Postgres' words are called Postgres'.</b> A check that failed on a
/// <c>PostgresException</c> carries Postgres' message, which can name a schema this visitor may not see; it is
/// shown, labelled "Postgres said:", only after every withheld schema's name is masked out of it with the
/// database browser's own rule (<see cref="PostgresErrorText" />), against this visitor's gate - and when that
/// gate cannot be read, the words are withheld and only the SQLSTATE is shown. A connection error's own words
/// are labelled as such and shown at the database-wide gate; any other exception's message is not on the page
/// at all (<see cref="HealthCheckProblem" />).
/// </para>
/// </remarks>
internal sealed class HealthService : IHealthService
{
    /// <summary>What the audit ring calls showing findings with session details in them.</summary>
    internal const string ActivityAction = "Read health session details";

    /// <summary>What the audit ring says was shown.</summary>
    internal const string ActivityTarget = "health findings' session evidence";

    // The audit ledger is keyed by store, database and what was read, as the Sessions page's is; these name "what".
    private const string ActivityLedgerSchema = "(health)";
    private const string ActivityLedgerRelation = "findings";
    private const string ActivityLedgerKind = "activity";

    /// <summary>
    /// What this circuit has recorded of Activity-gated evidence it showed, per store and database: the set of checks
    /// whose findings carried it. Scoped with the service, so one per circuit.
    /// </summary>
    private readonly TableRowAuditLedger activityLedger = new();

    /// <summary>
    /// How long this circuit trusts its answer to "is this tenant one of this database's": tenant discovery's own
    /// window. The answer is read through the source's own minute-long list, so it can be up to two minutes behind a
    /// tenant that moved; the scope selector's refresh, which clears that list, clears this as well
    /// (<see cref="IStudioSourceProvider.Generation" />).
    /// </summary>
    internal static readonly TimeSpan TenantPlacementTimeToLive = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether a tenant is one of a database's, by store key, identity and tenant, when that was read, and under which
    /// of tenant discovery's generations.
    /// </summary>
    private readonly Dictionary<(string StoreKey, string DatabaseId, string TenantId), (DateTimeOffset At, long Generation, bool InDatabase)> placements = [];
    private readonly Lock placementGate = new();
    private int tenantPlacements;

    private readonly IStudioSettings settings;
    private readonly StudioSourceResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly HealthSweepCache cache;
    private readonly HealthCheckRegistry registry;
    private readonly HealthHistory history;
    private readonly HealthMutes mutes;
    private readonly IServiceProvider services;
    private readonly ILogger<HealthService> logger;
    private readonly IStudioRuntime runtime;
    private readonly StudioDatabaseTime meter;
    private readonly IStudioSourceProvider sources;

    public HealthService(
        IStudioSettings settings,
        StudioSourceResolver resolver,
        IStudioSourceProvider sources,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        HealthSweepCache cache,
        HealthCheckRegistry registry,
        HealthHistory history,
        HealthMutes mutes,
        IServiceProvider services,
        ILogger<HealthService> logger,
        IStudioRuntime? runtime = null,
        StudioDatabaseTime? meter = null)
    {
        this.settings = settings;
        this.resolver = resolver;
        this.sources = sources;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.cache = cache;
        this.registry = registry;
        this.history = history;
        this.mutes = mutes;
        this.services = services;
        this.logger = logger;
        this.runtime = runtime ?? NoStudioRuntime.Instance;
        this.meter = meter ?? StudioDatabaseTime.Shared;
    }

    /// <summary>How many times this circuit has resolved a scope to place a tenant for a badge or a banner, for a test.</summary>
    internal int TenantPlacements => Volatile.Read(ref tenantPlacements);

    /// <summary>How old the last sweep may be and still decorate the sidebar.</summary>
    internal TimeSpan GlanceFreshness
    {
        get
        {
            TimeSpan three = settings.EffectiveHealthInterval * 3;
            return three > TimeSpan.FromMinutes(5) ? three : TimeSpan.FromMinutes(5);
        }
    }

    /// <inheritdoc />
    public async Task<HealthReport> GetReportAsync(
        StudioScope scope,
        HealthReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException exception)
        {
            return HealthReport.Refused(HealthRefusal.StorePolicy, exception.Message);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return HealthReport.Refused(HealthRefusal.Unavailable, exception.Message);
        }

        // The store by its registered key and the database by its identity - not the visitor's spelling of either, a store
        // key matching in any case and a database id often empty (D29) - for every question, the audit and the mute keys.
        // A copy of the resolver's own record with the scope replaced, never one rebuilt from its parts: a studio's checks
        // read the record's subtype (MartenStudio's ResolvedScope).
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        ResolvedSource whole = resolved with { Scope = new StudioScope(storeKey, databaseId, null) };

        VisitorGate gate = new(this, whole.Scope);

        HealthSweep regular = await cache.GetAsync(whole, HealthSweepKind.Regular, scope.DatabaseId, cancellationToken).ConfigureAwait(false);

        // Who may START a costlier sweep is decided here, not by the page: a circuit is a long-lived object a client
        // can drive, so a disabled button is a courtesy and this is the refusal (hard rule 5). A tier-2 sweep reads a
        // bounded slice of the data in every table it checks, whoever asked - a database-wide act - so only a visitor
        // through the database-wide gate starts one. Anybody else is shown a recent run if there is one, through the
        // same per-finding gates as the regular sweep, and never one they caused.
        StudioSourceKind sourceKind = resolved.Source.Kind;
        bool deeperExist = registry.HasTier(HealthCostTier.BoundedData, sourceKind);
        bool deeperAllowed = deeperExist && await gate.PassesAsync(HealthGate.Database).ConfigureAwait(false);

        HealthSweep? deeper = deeperExist
            ? await SweepOfAsync(whole, HealthSweepKind.Deeper, deeperAllowed && request.Deeper == HealthDeeperChecks.Run, scope, cancellationToken)
                .ConfigureAwait(false)
            : null;

        // Tier 3 reads other people's statements: nothing of it is run or shown short of the activity gate.
        bool statementsExist = registry.HasTier(HealthCostTier.Statements, sourceKind);
        bool statementsAllowed = statementsExist && await gate.PassesAsync(HealthGate.Activity).ConfigureAwait(false);

        HealthSweep? statements = statementsAllowed
            ? await SweepOfAsync(whole, HealthSweepKind.Statements, request.Statements == HealthDeeperChecks.Run, scope, cancellationToken)
                .ConfigureAwait(false)
            : null;

        List<HealthCheckResult> results = [.. regular.Results];
        results.AddRange(deeper?.Results ?? []);
        results.AddRange(statements?.Results ?? []);

        // The database browser's own gate, for this visitor and the database as a whole - read at most once per report, and
        // only if something needs it: a statement's text to mask, a finding about a table outside the store's own schemas
        // (D27), or Postgres' words in a check's failure.
        Task<DatabaseGateRead?>? gateRead = null;
        Task<DatabaseGateRead?> BrowserGate() => gateRead ??= BrowserGateAsync(whole, cancellationToken);

        Task<StatementScreen>? screenRead = null;
        Task<StatementScreen> Screen() => screenRead ??= StatementScreenAsync(BrowserGate);

        (List<HealthReportFinding> findings, int withheld, IReadOnlyList<string> reasons) =
            await ShowAsync(results, gate, storeKey, databaseId, audit: true, Screen, BrowserGate).ConfigureAwait(false);

        IReadOnlyList<HealthCheckStatus> statuses = await StatusesAsync(results, findings, gate, BrowserGate).ConfigureAwait(false);

        // The studio's own footprint in this database is a database-wide number, so it is shown at that gate.
        bool database = await gate.PassesAsync(HealthGate.Database).ConfigureAwait(false);

        return new HealthReport
        {
            StoreKey = storeKey,
            DatabaseId = databaseId,
            MeasuredAt = regular.MeasuredAt,
            SweepDuration = regular.Duration,
            DeeperMeasuredAt = deeper?.MeasuredAt,
            StatementsMeasuredAt = statements?.MeasuredAt,
            Findings = findings,
            WithheldCount = withheld,
            WithheldReason = reasons.Count == 0 ? null : string.Join("; ", reasons),
            Checks = statuses,
            History = history.Read(storeKey, databaseId).Describe(cache.Now),
            Cost = database ? meter.Total(CostKey(resolved.Target)) : null,
            CostWithheld = database ? null : gate.WhyNot(HealthGate.Database),
            Server = database ? HealthServerLine.Of(regular) : null,
            StatisticsReset = database && regular.Statistics is { LooksReset: true } window
                ? "The statistics look reset since the last check: " + (window.Signal ?? window.ReasonText) + ". Rates and verdicts that "
                  + "need a long window are judged again once the counters have counted long enough."
                : null,
            DeeperChecksExist = deeperExist,
            DeeperChecksAllowed = deeperAllowed,
            DeeperChecksRefused = deeperExist && !deeperAllowed ? gate.WhyNot(HealthGate.Database) : null,
            StatementChecksExist = statementsExist,
            StatementChecksAllowed = statementsAllowed,
            StatementChecksRefused = statementsExist && !statementsAllowed ? gate.WhyNot(HealthGate.Activity) : null,
        };
    }

    /// <inheritdoc />
    public async Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // A badge shows counts and titles, never evidence: nothing is revealed, so nothing is audited.
        (HealthSweep? sweep, List<HealthReportFinding> unmuted, int withheld) =
            await CachedAsync(scope, static _ => true, audit: false, cancellationToken).ConfigureAwait(false);

        if (sweep is null)
        {
            return null;
        }

        return new HealthGlance(
            sweep.MeasuredAt,
            unmuted.Count(static x => x.Severity == HealthSeverity.Critical),
            unmuted.Count(static x => x.Severity == HealthSeverity.Warning),
            [.. unmuted.Where(static x => x.Severity == HealthSeverity.Critical).Select(static x => new HealthGlanceItem(x.MuteKey, x.Finding.Title))],
            withheld)
        {
            // The visitor's scope as asked, not the swept names: the toast compares it with the scope the sidebar is on
            // when the glance reaches it.
            Scope = scope,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealthReportFinding>> CachedFindingsAsync(
        StudioScope scope,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(codes);

        if (codes.Count == 0)
        {
            return [];
        }

        (_, List<HealthReportFinding> unmuted, _) = await CachedAsync(
                scope,
                result => codes.Contains(result.Check.Code, StringComparer.Ordinal),
                audit: true,
                cancellationToken)
            .ConfigureAwait(false);

        return unmuted;
    }

    /// <summary>The mute key of a finding: store, database and the finding's own key.</summary>
    internal static string MuteKeyOf(string storeKey, string databaseId, HealthFinding finding) =>
        storeKey + "|" + databaseId + "|" + finding.Key;

    /// <summary>
    /// The gate a reader must pass to be shown <paramref name="finding" /> at all (D29): its check's, or - for a finding
    /// about one tenant, which names it - <see cref="HealthGate.Activity" /> at least. The one rule the Health page's visitor
    /// gate and the diagnostics API's host gate (<c>HostGate</c>, D31) both apply; a visitor also needs that tenant's store
    /// policy, and the host needs nothing more.
    /// </summary>
    /// <param name="finding">The finding.</param>
    internal static HealthGate RequiredGate(HealthFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.TenantId is not null && finding.Gate != HealthGate.Activity ? HealthGate.Activity : finding.Gate;
    }

    /// <summary>
    /// The key the studio-cost meter files this database under - read off an unopened connection's settings,
    /// which costs no I/O.
    /// </summary>
    private static string CostKey(IStudioDatabase database)
    {
        using Npgsql.NpgsqlConnection connection = database.CreateConnection(StudioConnectionUsage.ReadWrite);
        return StudioDatabaseTime.KeyOf(connection);
    }

    /// <summary>
    /// The last regular sweep, if it is recent and the scope places this visitor in the database it swept, and the
    /// findings of the checks <paramref name="wanted" /> picks that this visitor may see and has not muted - with how
    /// many they are not shown. The badge's and the banners' shared read, which never starts a sweep.
    /// </summary>
    private async Task<(HealthSweep? Sweep, List<HealthReportFinding> Unmuted, int Withheld)> CachedAsync(
        StudioScope scope,
        Func<HealthCheckResult, bool> wanted,
        bool audit,
        CancellationToken cancellationToken)
    {
        HealthSweep? sweep = cache.Latest(scope.StoreKey, scope.DatabaseId);

        if (sweep is null || cache.Now - sweep.MeasuredAt > GlanceFreshness)
        {
            // Nothing recent to say, and nothing is started to find out: a badge or a banner is a decoration.
            return (null, [], 0);
        }

        // The swept store and database as the sweep names them - the registered key and the identity, which the cache
        // found under the visitor's spelling of either - so every question below names what the report's gates, its
        // audit and its mute keys name (D29).
        StudioScope whole = new(sweep.StoreKey, sweep.DatabaseId, null);

        if (!await InSweptDatabaseAsync(scope, whole, cancellationToken).ConfigureAwait(false))
        {
            return (null, [], 0);
        }

        VisitorGate gate = new(this, whole);

        // No screen: a cached read resolves no scope, so a statement's text cannot be masked for this visitor and is
        // withheld (fail closed). The Health page itself goes through GetReportAsync, which masks it. No browser gate
        // either, for the same reason: a finding about a table outside the store's own schemas is counted as withheld.
        (List<HealthReportFinding> findings, int withheld, _) = await ShowAsync(
                sweep.Results.Where(wanted), gate, sweep.StoreKey, sweep.DatabaseId, audit, screen: null, browserGate: null)
            .ConfigureAwait(false);

        return (sweep, [.. findings.Where(x => !mutes.IsMuted(x.MuteKey))], withheld);
    }

    /// <summary>
    /// Whether the scope admits this visitor to the database <paramref name="whole" /> names: the store policy for
    /// that database's own identity, and for a scope with a tenant, the resolver's own answer to "is this tenant one
    /// of this database's" - exactly what <see cref="GetReportAsync" /> is told, so a badge or a banner is never shown
    /// for <c>?db=B&amp;tenant=A</c> when A is not one of B's tenants and the report would be refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With no tenant the question is the policy's alone, which is in-process, and the badge opens no connection.
    /// With one, the store policy is asked about the swept database and the tenant on every call - it is this visitor's
    /// answer, and may change - and then the tenant is placed in that database by resolving the scope against it: the
    /// resolver's own check, with the same discovery every page of that scope runs.
    /// </para>
    /// <para>
    /// <b>Placed once a minute, not once a poll.</b> Resolving enumerates the store's databases, which on a master-table
    /// or sharded tenancy is a read of its lookup table, and the sidebar asks on its prerender, when it becomes
    /// interactive, after every mute and at every <c>RefreshInterval</c>. So whether a tenant is one of a database's -
    /// a fact about the database, not about the visitor - is remembered per store, database and tenant for
    /// <see cref="TenantPlacementTimeToLive" />, tenant discovery's own window, and for this circuit only. It is read
    /// through discovery's own minute-long list, so it can lag a tenant that moved by up to two minutes; the scope
    /// selector's refresh moves <see cref="IStudioSourceProvider.Generation" />, and an answer read under an earlier generation
    /// is read again. A refusal by the policy and a store that would not build are not facts about the database and are
    /// never remembered. Anything that refuses or fails answers <see langword="false" /> - a decoration fails closed and
    /// quietly.
    /// </para>
    /// </remarks>
    private async Task<bool> InSweptDatabaseAsync(StudioScope scope, StudioScope whole, CancellationToken cancellationToken)
    {
        if (scope.TenantId is not { } tenantId)
        {
            return await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false);
        }

        StudioScope placed = whole with { TenantId = tenantId };

        if (!await authorization.IsAuthorizedAsync(placed, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        (string StoreKey, string DatabaseId, string TenantId) key = (whole.StoreKey, whole.DatabaseId, tenantId);
        DateTimeOffset now = cache.Now;

        // Taken before the resolve: a refresh that lands while it runs leaves this answer a generation behind.
        long generation = sources.Generation;

        lock (placementGate)
        {
            if (placements.TryGetValue(key, out (DateTimeOffset At, long Generation, bool InDatabase) known)
                && known.Generation == generation
                && now - known.At < TenantPlacementTimeToLive)
            {
                return known.InDatabase;
            }
        }

        bool inDatabase;

        try
        {
            Interlocked.Increment(ref tenantPlacements);
            await resolver.ResolveAsync(placed, null, cancellationToken).ConfigureAwait(false);
            inDatabase = true;
        }
        catch (KeyNotFoundException)
        {
            // Not one of this database's tenants (or no longer a database of the store's): the fact this remembers.
            inDatabase = false;
        }
        catch (Exception exception) when (exception is StudioNotAuthorizedException or StudioStoreUnavailableException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.LogDebug(exception, "Postgres Studio could not place a tenant in a swept database for a health badge or banner");
            }
            else
            {
                logger.LogDebug(exception, "Marten Studio could not place a tenant in a swept database for a health badge or banner");
            }

            return false;
        }

        lock (placementGate)
        {
            foreach ((string, string, string) stale in placements
                         .Where(x => x.Value.Generation != generation || now - x.Value.At >= TenantPlacementTimeToLive)
                         .Select(static x => x.Key)
                         .ToList())
            {
                placements.Remove(stale);
            }

            placements[key] = (now, generation, inDatabase);
        }

        return inDatabase;
    }

    /// <summary>
    /// A costlier sweep: run when <paramref name="start" /> - which the caller decides from the visitor's gate,
    /// never from the request alone - and otherwise only a recent one still in the cache.
    /// </summary>
    private async Task<HealthSweep?> SweepOfAsync(
        ResolvedSource whole,
        HealthSweepKind kind,
        bool start,
        StudioScope scope,
        CancellationToken cancellationToken)
    {
        if (start)
        {
            return await cache.GetAsync(whole, kind, scope.DatabaseId, cancellationToken).ConfigureAwait(false);
        }

        HealthSweep? recent = cache.Latest(whole.Scope.StoreKey, whole.Scope.DatabaseId, kind);
        return recent is not null && cache.Now - recent.MeasuredAt < HealthSweepCache.DeeperTimeToLive ? recent : null;
    }

    /// <summary>
    /// The findings this visitor may see, worst first, with their evidence redacted - and how many were
    /// withheld, and behind which gate.
    /// </summary>
    /// <param name="results">The checks' results.</param>
    /// <param name="gate">This visitor's gate.</param>
    /// <param name="storeKey">The store, by its registered key.</param>
    /// <param name="databaseId">The database, by its identity.</param>
    /// <param name="audit">
    /// Whether what is shown is to be audited (<see cref="RecordActivityShown" />); <see langword="false" /> for a read
    /// that shows no evidence.
    /// </param>
    /// <param name="screen">
    /// What this visitor's statements are checked against, read on first use; <see langword="null" /> withholds every
    /// statement's text (<see cref="HealthEvidence.Statement" />).
    /// </param>
    /// <param name="browserGate">
    /// The database browser's gate for this visitor, read on first use - only when a finding names a table outside the
    /// store's own schemas (<see cref="HealthFinding.GatedSchemas" />). <see langword="null" />, or a read that answers
    /// <see langword="null" />, withholds every such finding (fail closed).
    /// </param>
    private async Task<(List<HealthReportFinding> Findings, int Withheld, IReadOnlyList<string> Reasons)> ShowAsync(
        IEnumerable<HealthCheckResult> results,
        VisitorGate gate,
        string storeKey,
        string databaseId,
        bool audit,
        Func<Task<StatementScreen>>? screen,
        Func<Task<DatabaseGateRead?>>? browserGate)
    {
        List<HealthReportFinding> shown = [];
        int withheld = 0;
        List<string> reasons = [];
        SortedSet<string> activityShown = new(StringComparer.Ordinal);

        foreach (HealthCheckResult result in results)
        {
            foreach (HealthFinding finding in result.Findings)
            {
                HealthGate required = RequiredGate(finding);

                string? reason = !await gate.PassesAsync(required).ConfigureAwait(false)
                    ? gate.WhyNot(required)
                    : finding.TenantId is not null && !await gate.TenantAsync(finding.TenantId).ConfigureAwait(false)
                        ? "names a tenant the store policy does not show you"
                        : finding.GatedSchemas.Count > 0
                            ? await SchemaRefusalAsync(settings, finding.GatedSchemas, browserGate).ConfigureAwait(false)
                            : null;

                if (reason is not null)
                {
                    withheld++;

                    if (!reasons.Contains(reason, StringComparer.Ordinal))
                    {
                        reasons.Add(reason);
                    }

                    continue;
                }

                bool revealsActivity = required == HealthGate.Activity;

                List<HealthEvidence> evidence = [];
                foreach (HealthEvidence item in finding.Evidence)
                {
                    bool passes = await gate.PassesAsync(item.Gate).ConfigureAwait(false);
                    revealsActivity |= passes && item.Gate == HealthGate.Activity;

                    if (!passes)
                    {
                        evidence.Add(item with { Value = "withheld - " + gate.WhyNot(item.Gate), Statement = null });
                    }
                    else if (item.Statement is { } statement)
                    {
                        // Another program's text, masked for this visitor by Database › Sessions' own rule - and never
                        // handed on as it is: the item a page receives carries only what this visitor may read.
                        StatementScreen visitor = screen is null
                            ? StatementScreen.Unchecked(settings.IsDocumentTypeVisible is not null)
                            : await screen().ConfigureAwait(false);

                        SessionStatementText masked = SessionQueryText.Present(statement.Text, statement.Truncated, visitor);
                        evidence.Add(item with { Value = masked.Text ?? masked.Note ?? HealthEvidence.StatementPending, Statement = null });
                    }
                    else
                    {
                        evidence.Add(item);
                    }
                }

                if (revealsActivity)
                {
                    activityShown.Add(finding.Code);
                }

                StudioFeatureRequirement? feature = finding.Remedy?.Requires;
                bool available = feature is null || runtime.IsAvailable(feature);

                shown.Add(new HealthReportFinding(
                    finding,
                    evidence,
                    MuteKeyOf(storeKey, databaseId, finding),
                    result.Check.DocsUrl,
                    available,
                    available || feature is null ? null : runtime.DescribeHost(feature)));
            }
        }

        shown.Sort(static (a, b) =>
        {
            int bySeverity = a.Severity.CompareTo(b.Severity);
            if (bySeverity != 0)
            {
                return bySeverity;
            }

            int byArea = a.Finding.Area.CompareTo(b.Finding.Area);
            return byArea != 0 ? byArea : string.CompareOrdinal(a.Finding.Key, b.Finding.Key);
        });

        // An element id is one per page: two keys whose anchors read alike ("a b" and "a-b") get a suffix.
        HashSet<string> anchors = new(StringComparer.Ordinal);
        for (int i = 0; i < shown.Count; i++)
        {
            string anchor = shown[i].Anchor;
            for (int n = 2; !anchors.Add(anchor); n++)
            {
                anchor = shown[i].Anchor + "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (!ReferenceEquals(anchor, shown[i].Anchor))
            {
                shown[i] = shown[i] with { Anchor = anchor };
            }
        }

        if (audit && activityShown.Count > 0)
        {
            RecordActivityShown(storeKey, databaseId, activityShown);
        }

        return (shown, withheld, reasons);
    }

    /// <summary>
    /// What withholds a finding naming a table in a schema outside the store's own from a visitor short of the browser's
    /// gate - naming the option the way the studio's own registration spells it.
    /// </summary>
    /// <param name="settings">The studio's settings.</param>
    internal static string WithheldSchemaReasonFor(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return "names a table in a schema the database browser does not show you (it needs "
            + settings.CapabilityOption(StudioCapability.BrowseDatabase)
            + ", the write policy and a BrowsableSchemas entry for that schema)";
    }

    /// <summary>What withholds such a finding when the browser's gate could not be read for this visitor.</summary>
    internal const string UncheckedSchemaReason =
        "names a table in a schema outside the store's own, which could not be checked against the database browser's gate for you";

    /// <summary><see cref="UncheckedSchemaReason" /> in the studio's own words: MartenStudio's is that constant.</summary>
    /// <param name="text">The studio's words.</param>
    internal static string UncheckedSchemaReasonFor(StudioText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "names a table in a schema outside " + text.SourcePossessive
            + " own, which could not be checked against the database browser's gate for you";
    }

    /// <summary>
    /// Why a finding naming tables in <paramref name="schemas" /> - each outside the store's own - is withheld from this
    /// visitor, or <see langword="null" /> when the database browser shows them every one (AGENTS.md D27).
    /// </summary>
    /// <remarks>
    /// Asked with <see cref="DatabaseGate.IsWithheld" />, not against <see cref="DatabaseGate.WithheldSchemas" />: a schema
    /// created after the gate's schema list was read is in neither list, and the gate counts it as withheld.
    /// </remarks>
    private static async Task<string?> SchemaRefusalAsync(
        IStudioSettings settings,
        IReadOnlyList<string> schemas,
        Func<Task<DatabaseGateRead?>>? browserGate)
    {
        DatabaseGateRead? read = browserGate is null ? null : await browserGate().ConfigureAwait(false);

        if (read is null || !read.Succeeded)
        {
            return UncheckedSchemaReasonFor(settings.Text);
        }

        DatabaseGate gate = read.Gate;
        return schemas.Any(gate.IsWithheld) ? WithheldSchemaReasonFor(settings) : null;
    }

    /// <summary>
    /// The database browser's gate for this visitor and the database as a whole - the one read a report shares between
    /// statement masking, findings about tables outside the store's own schemas and Postgres' words - or
    /// <see langword="null" /> when it cannot be read, which withholds all three.
    /// </summary>
    private async Task<DatabaseGateRead?> BrowserGateAsync(ResolvedSource whole, CancellationToken cancellationToken)
    {
        if (services.GetService<DatabaseAccess>() is not { } access)
        {
            return null;
        }

        try
        {
            return await access.GateAsync(whole.Scope, whole, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.LogDebug(exception, "Postgres Studio could not read the database browser's gate for a health report");
            }
            else
            {
                logger.LogDebug(exception, "Marten Studio could not read the database browser's gate for a health report");
            }

            return null;
        }
    }

    /// <summary>
    /// One audit entry for Activity-gated evidence shown - process ids, roles, applications, client addresses, a
    /// prepared transaction's gid and owner - the first time a circuit is shown it for a store and database, and
    /// again only when the set of checks showing it changes: the Sessions page's first-read rule, by signature, so a
    /// page's interval reads record nothing more.
    /// </summary>
    private void RecordActivityShown(string storeKey, string databaseId, SortedSet<string> codes)
    {
        string signature = string.Join(",", codes);

        if (services.GetService<StudioActionLog>() is not { } audit
            || !activityLedger.TryRecord(
                TableRowAuditLedger.Key(storeKey, databaseId, ActivityLedgerSchema, ActivityLedgerRelation, ActivityLedgerKind),
                signature,
                out _))
        {
            return;
        }

        audit.Record(
            ActivityAction,
            ActivityTarget,
            succeeded: true,
            "Health findings shown with session details: " + string.Join(", ", codes) + ".",
            StudioCapability.ViewDatabaseActivity,
            new StudioScope(storeKey, databaseId, null));
    }

    /// <summary>
    /// Every check's status row, with Postgres' words masked for this visitor - and, for a check whose gate
    /// this visitor is short of, nothing but its code, title, tier and outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A status row carries the check's note, its failure and its statements, and a note can say what a
    /// finding would have said ("3 sessions of role reporting are idle"), so the row is shown at its check's
    /// gate exactly as the findings are. Short of it the note is not drawn at all - not even as "withheld" - because
    /// whether a check wrote one says something of what it saw (T03, S02, R01 and D04 write one only on some sweeps). A
    /// failure stays as "withheld - ...": the outcome already says the check did not run.
    /// </para>
    /// <para>
    /// <b>And its count is never a false "nothing found".</b> Short of the gate the row's count is 0 and it is marked
    /// <see cref="HealthCheckStatus.FindingsWithheld" /> whatever the check found - so it reads "findings not shown to
    /// you" and does not say which checks found something. Through the gate the count is what this visitor is shown,
    /// and the mark says when findings about tenants were withheld one by one: every finding of a result is either
    /// shown or withheld (<see cref="ShowAsync" />), and a code is one check's.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<HealthCheckStatus>> StatusesAsync(
        IReadOnlyList<HealthCheckResult> results,
        IReadOnlyList<HealthReportFinding> shown,
        VisitorGate gate,
        Func<Task<DatabaseGateRead?>> browserGate)
    {
        IReadOnlySet<string>? withheld = null;
        bool withheldRead = false;

        List<HealthCheckStatus> statuses = [];

        foreach (HealthCheckResult result in results)
        {
            if (!await gate.PassesAsync(result.Check.Gate).ConfigureAwait(false))
            {
                string reason = "withheld - " + gate.WhyNot(result.Check.Gate);

                statuses.Add(new HealthCheckStatus(
                    result.Check.Code,
                    result.Check.Title,
                    result.Check.Area,
                    result.Check.Tier,
                    result.Outcome,
                    0,
                    null,
                    result.Problem is null ? null : reason,
                    null,
                    result.Check.DocsUrl,
                    [],
                    FindingsWithheld: true) { BelowGate = gate.WhyNot(result.Check.Gate) });

                continue;
            }

            // The studio's sentence first, as it is; then each other source under its own label (HealthCheckProblem).
            string? problem = result.Problem?.Reason;

            if (result.Problem?.PostgresText is { Length: > 0 } text)
            {
                if (!withheldRead)
                {
                    withheldRead = true;

                    // The schemas this visitor may not see, from the database browser's own gate - or none when it cannot
                    // be read, which withholds Postgres' words outright.
                    DatabaseGateRead? read = await browserGate().ConfigureAwait(false);
                    withheld = read is not null && read.Succeeded ? read.Gate.WithheldSchemas : null;
                }

                problem = withheld is null
                    ? problem + " What Postgres said is withheld: the schemas it may name could not be checked for this visitor."
                    : problem + " Postgres said: " + PostgresErrorText.Redact(text, withheld);
            }

            if (result.Problem?.Detail is { Length: > 0 } detail)
            {
                // A connection error's words name the database's address, which is the database-wide gate's to show.
                problem = await gate.PassesAsync(HealthGate.Database).ConfigureAwait(false)
                    ? problem + " The connection error said: " + detail
                    : problem + " What the connection error said is withheld - " + gate.WhyNot(HealthGate.Database) + ".";
            }

            int shownCount = shown.Count(x => string.Equals(x.Code, result.Check.Code, StringComparison.Ordinal));

            statuses.Add(new HealthCheckStatus(
                result.Check.Code,
                result.Check.Title,
                result.Check.Area,
                result.Check.Tier,
                result.Outcome,
                shownCount,
                result.Note,
                problem,
                result.Problem?.SqlState,
                result.Check.DocsUrl,
                result.Statements,
                FindingsWithheld: result.Findings.Count > shownCount));
        }

        return statuses;
    }

    /// <summary>
    /// What this visitor's statements are checked against - the database browser's gate for the database as a whole,
    /// as Database › Sessions reads it - or <see cref="StatementScreen.Unchecked" /> when it cannot be read, which
    /// withholds every statement's text.
    /// </summary>
    private async Task<StatementScreen> StatementScreenAsync(Func<Task<DatabaseGateRead?>> browserGate)
    {
        bool hidesDocumentTypes = settings.IsDocumentTypeVisible is not null;

        DatabaseGateRead? read = await browserGate().ConfigureAwait(false);
        return read is null ? StatementScreen.Unchecked(hidesDocumentTypes) : StatementScreen.Of(read, hidesDocumentTypes);
    }

    /// <summary>
    /// One visitor's answers to the three gates, asked lazily and at most once each per report.
    /// </summary>
    /// <remarks>
    /// <c>scope</c> is the swept database as a whole, by the store's registered key and the database's identity and with
    /// no tenant - never the visitor's spelling of either (D29) - so the gates' tenant-less questions and a finding's
    /// tenant question name the database the report's audit records.
    /// </remarks>
    private sealed class VisitorGate(HealthService owner, StudioScope scope)
    {
        private readonly Dictionary<string, bool> tenants = new(StringComparer.Ordinal);
        private bool? database;
        private bool? activity;
        private string? activityRefusal;

        public async ValueTask<bool> PassesAsync(HealthGate gate) => gate switch
        {
            HealthGate.None => true,
            HealthGate.Database => await DatabaseAsync().ConfigureAwait(false),
            HealthGate.Activity => await ActivityAsync().ConfigureAwait(false),
            _ => false,
        };

        public async ValueTask<bool> TenantAsync(string tenantId)
        {
            if (!tenants.TryGetValue(tenantId, out bool allowed))
            {
                allowed = await owner.authorization.IsAuthorizedAsync(scope with { TenantId = tenantId }, capability: null).ConfigureAwait(false);
                tenants[tenantId] = allowed;
            }

            return allowed;
        }

        /// <summary>What it would take to pass <paramref name="gate" />, naming the setting or the policy.</summary>
        public string WhyNot(HealthGate gate) => gate switch
        {
            HealthGate.Database => owner.settings.Text.DatabaseGateReason,
            HealthGate.Activity => activityRefusal ?? "needs " + owner.capabilities.OptionFor(StudioCapability.ViewDatabaseActivity),
            _ => "withheld",
        };

        private async ValueTask<bool> DatabaseAsync()
        {
            database ??= await owner.authorization.IsAuthorizedAsync(scope with { TenantId = null }, capability: null).ConfigureAwait(false);
            return database.Value;
        }

        /// <summary>
        /// <c>ViewDatabaseActivity</c>, in the database browser's order: the capability, then the store policy
        /// for the database as a whole, then the write policy with the capability named - all tenant-less.
        /// </summary>
        private async ValueTask<bool> ActivityAsync()
        {
            if (activity is { } known)
            {
                return known;
            }

            if (!owner.capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
            {
                activityRefusal = owner.capabilities.ReadOnly
                    ? "needs ViewDatabaseActivity, and " + owner.settings.ReadOnlyOption() + " is true, which turns every capability off"
                    : "needs " + owner.capabilities.OptionFor(StudioCapability.ViewDatabaseActivity);
                activity = false;
                return false;
            }

            if (!await DatabaseAsync().ConfigureAwait(false))
            {
                activityRefusal = WhyNot(HealthGate.Database);
                activity = false;
                return false;
            }

            activity = await owner.authorization
                .IsAuthorizedAsync(scope with { TenantId = null }, StudioCapability.ViewDatabaseActivity)
                .ConfigureAwait(false);

            if (activity == false)
            {
                activityRefusal = "needs your account's write policy to allow ViewDatabaseActivity for this database";
            }

            return activity.Value;
        }
    }
}
