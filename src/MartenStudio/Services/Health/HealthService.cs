using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services.Health;

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
    /// What the navigation badge shows: the last sweep's counts for this visitor, unmuted, or
    /// <see langword="null" /> when there is no recent sweep. Never starts one, and opens no connection.
    /// </summary>
    Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The findings with these codes that this visitor may see and has not muted, from the last regular sweep
    /// of the scope's database - for a banner on the screen a finding belongs to (D01 and D02 on Projections,
    /// T03-T05 on Schema). <b>Cache-only</b>, like <see cref="GlanceAsync" />: it never starts a sweep and opens
    /// no connection, so a page that polls it costs nothing; with no recent sweep it answers an empty list.
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

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly HealthSweepCache cache;
    private readonly HealthCheckRegistry registry;
    private readonly HealthHistory history;
    private readonly HealthMutes mutes;
    private readonly IServiceProvider services;
    private readonly ILogger<HealthService> logger;
    private readonly MartenRuntime runtime;
    private readonly StudioDatabaseTime meter;

    public HealthService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        HealthSweepCache cache,
        HealthCheckRegistry registry,
        HealthHistory history,
        HealthMutes mutes,
        IServiceProvider services,
        ILogger<HealthService> logger,
        MartenRuntime? runtime = null,
        StudioDatabaseTime? meter = null)
    {
        this.options = options;
        this.resolver = resolver;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.cache = cache;
        this.registry = registry;
        this.history = history;
        this.mutes = mutes;
        this.services = services;
        this.logger = logger;
        this.runtime = runtime ?? MartenRuntime.Current;
        this.meter = meter ?? StudioDatabaseTime.Shared;
    }

    /// <summary>How old the last sweep may be and still decorate the sidebar.</summary>
    internal TimeSpan GlanceFreshness
    {
        get
        {
            TimeSpan three = options.Value.EffectiveHealthInterval * 3;
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

        ResolvedScope resolved;

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

        string databaseId = resolved.Database.Id.Identity;
        ResolvedScope whole = new(new StudioScope(scope.StoreKey, databaseId, null), resolved.Registration, resolved.Store, resolved.Database);

        VisitorGate gate = new(this, scope);

        HealthSweep regular = await cache.GetAsync(whole, HealthSweepKind.Regular, scope.DatabaseId, cancellationToken).ConfigureAwait(false);

        // Who may START a costlier sweep is decided here, not by the page: a circuit is a long-lived object a client
        // can drive, so a disabled button is a courtesy and this is the refusal (hard rule 5). A tier-2 sweep reads a
        // bounded slice of the data in every table it checks, whoever asked - a database-wide act - so only a visitor
        // through the database-wide gate starts one. Anybody else is shown a recent run if there is one, through the
        // same per-finding gates as the regular sweep, and never one they caused.
        bool deeperExist = registry.HasTier(HealthCostTier.BoundedData);
        bool deeperAllowed = deeperExist && await gate.PassesAsync(HealthGate.Database).ConfigureAwait(false);

        HealthSweep? deeper = deeperExist
            ? await SweepOfAsync(whole, HealthSweepKind.Deeper, deeperAllowed && request.Deeper == HealthDeeperChecks.Run, scope, cancellationToken)
                .ConfigureAwait(false)
            : null;

        // Tier 3 reads other people's statements: nothing of it is run or shown short of the activity gate.
        bool statementsAllowed = registry.HasTier(HealthCostTier.Statements) && await gate.PassesAsync(HealthGate.Activity).ConfigureAwait(false);

        HealthSweep? statements = statementsAllowed
            ? await SweepOfAsync(whole, HealthSweepKind.Statements, request.Statements == HealthDeeperChecks.Run, scope, cancellationToken)
                .ConfigureAwait(false)
            : null;

        List<HealthCheckResult> results = [.. regular.Results];
        results.AddRange(deeper?.Results ?? []);
        results.AddRange(statements?.Results ?? []);

        (List<HealthReportFinding> findings, int withheld, IReadOnlyList<string> reasons) =
            await ShowAsync(results, gate, scope.StoreKey, databaseId, scope).ConfigureAwait(false);

        IReadOnlyList<HealthCheckStatus> statuses = await StatusesAsync(results, findings, gate, scope, resolved, cancellationToken)
            .ConfigureAwait(false);

        // The studio's own footprint in this database is a database-wide number, so it is shown at that gate.
        bool database = await gate.PassesAsync(HealthGate.Database).ConfigureAwait(false);

        return new HealthReport
        {
            StoreKey = scope.StoreKey,
            DatabaseId = databaseId,
            MeasuredAt = regular.MeasuredAt,
            SweepDuration = regular.Duration,
            DeeperMeasuredAt = deeper?.MeasuredAt,
            StatementsMeasuredAt = statements?.MeasuredAt,
            Findings = findings,
            WithheldCount = withheld,
            WithheldReason = reasons.Count == 0 ? null : string.Join("; ", reasons),
            Checks = statuses,
            History = history.Read(scope.StoreKey, databaseId).Describe(cache.Now),
            Cost = database ? meter.Total(CostKey(resolved.Database)) : null,
            CostWithheld = database ? null : gate.WhyNot(HealthGate.Database),
            DeeperChecksExist = deeperExist,
            DeeperChecksAllowed = deeperAllowed,
            DeeperChecksRefused = deeperExist && !deeperAllowed ? gate.WhyNot(HealthGate.Database) : null,
            StatementChecksExist = registry.HasTier(HealthCostTier.Statements),
            StatementChecksAllowed = statementsAllowed,
        };
    }

    /// <inheritdoc />
    public async Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // A badge shows counts and titles, never evidence: nothing is revealed, so nothing is audited.
        (HealthSweep? sweep, List<HealthReportFinding> unmuted) =
            await CachedAsync(scope, static _ => true, audit: false, cancellationToken).ConfigureAwait(false);

        if (sweep is null)
        {
            return null;
        }

        return new HealthGlance(
            sweep.MeasuredAt,
            unmuted.Count(static x => x.Severity == HealthSeverity.Critical),
            unmuted.Count(static x => x.Severity == HealthSeverity.Warning),
            [.. unmuted.Where(static x => x.Severity == HealthSeverity.Critical).Select(static x => new HealthGlanceItem(x.MuteKey, x.Finding.Title))]);
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

        (_, List<HealthReportFinding> unmuted) = await CachedAsync(
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
    /// The key the studio-cost meter files this database under - read off an unopened connection's settings,
    /// which costs no I/O.
    /// </summary>
    private static string CostKey(Marten.Storage.IMartenDatabase database)
    {
        using Npgsql.NpgsqlConnection connection = database.CreateConnection(Marten.Storage.ConnectionUsage.ReadWrite);
        return StudioDatabaseTime.KeyOf(connection);
    }

    /// <summary>
    /// The last regular sweep, if it is recent, and the findings of the checks <paramref name="wanted" /> picks
    /// that this visitor may see and has not muted - the badge's and the banners' shared read, which starts no
    /// sweep and opens no connection.
    /// </summary>
    private async Task<(HealthSweep? Sweep, List<HealthReportFinding> Unmuted)> CachedAsync(
        StudioScope scope,
        Func<HealthCheckResult, bool> wanted,
        bool audit,
        CancellationToken cancellationToken)
    {
        HealthSweep? sweep = cache.Latest(scope.StoreKey, scope.DatabaseId);

        if (sweep is null || cache.Now - sweep.MeasuredAt > GlanceFreshness)
        {
            // Nothing recent to say, and nothing is started to find out: a badge or a banner is a decoration.
            return (null, []);
        }

        if (!await authorization.IsAuthorizedAsync(scope, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return (null, []);
        }

        VisitorGate gate = new(this, scope);
        (List<HealthReportFinding> findings, _, _) = await ShowAsync(
                sweep.Results.Where(wanted), gate, scope.StoreKey, sweep.DatabaseId, audit ? scope : null)
            .ConfigureAwait(false);

        return (sweep, [.. findings.Where(x => !mutes.IsMuted(x.MuteKey))]);
    }

    /// <summary>
    /// A costlier sweep: run when <paramref name="start" /> - which the caller decides from the visitor's gate,
    /// never from the request alone - and otherwise only a recent one still in the cache.
    /// </summary>
    private async Task<HealthSweep?> SweepOfAsync(
        ResolvedScope whole,
        HealthSweepKind kind,
        bool start,
        StudioScope scope,
        CancellationToken cancellationToken)
    {
        if (start)
        {
            return await cache.GetAsync(whole, kind, scope.DatabaseId, cancellationToken).ConfigureAwait(false);
        }

        HealthSweep? recent = cache.Latest(scope.StoreKey, whole.Database.Id.Identity, kind);
        return recent is not null && cache.Now - recent.MeasuredAt < HealthSweepCache.DeeperTimeToLive ? recent : null;
    }

    /// <summary>
    /// The findings this visitor may see, worst first, with their evidence redacted - and how many were
    /// withheld, and behind which gate.
    /// </summary>
    /// <param name="results">The checks' results.</param>
    /// <param name="gate">This visitor's gate.</param>
    /// <param name="storeKey">The store.</param>
    /// <param name="databaseId">The database.</param>
    /// <param name="audited">
    /// The scope the visitor asked with, when what is shown is to be audited (<see cref="RecordActivityShown" />);
    /// <see langword="null" /> for a read that shows no evidence.
    /// </param>
    private async Task<(List<HealthReportFinding> Findings, int Withheld, IReadOnlyList<string> Reasons)> ShowAsync(
        IEnumerable<HealthCheckResult> results,
        VisitorGate gate,
        string storeKey,
        string databaseId,
        StudioScope? audited)
    {
        List<HealthReportFinding> shown = [];
        int withheld = 0;
        List<string> reasons = [];
        SortedSet<string> activityShown = new(StringComparer.Ordinal);

        foreach (HealthCheckResult result in results)
        {
            foreach (HealthFinding finding in result.Findings)
            {
                HealthGate required = finding.TenantId is not null && finding.Gate != HealthGate.Activity ? HealthGate.Activity : finding.Gate;

                string? reason = !await gate.PassesAsync(required).ConfigureAwait(false)
                    ? gate.WhyNot(required)
                    : finding.TenantId is not null && !await gate.TenantAsync(finding.TenantId).ConfigureAwait(false)
                        ? "names a tenant the store policy does not show you"
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

                    evidence.Add(passes
                        ? item
                        : item with { Value = "withheld - " + gate.WhyNot(item.Gate) });
                }

                if (revealsActivity)
                {
                    activityShown.Add(finding.Code);
                }

                RuntimeFeature? feature = finding.Remedy?.Requires;
                bool available = feature is null || feature.IsAvailable(runtime);

                shown.Add(new HealthReportFinding(
                    finding,
                    evidence,
                    MuteKeyOf(storeKey, databaseId, finding),
                    result.Check.DocsUrl,
                    available,
                    available || feature is null ? null : runtime.DescribeHost(feature.Package)));
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

        if (audited is not null && activityShown.Count > 0)
        {
            RecordActivityShown(audited, storeKey, databaseId, activityShown);
        }

        return (shown, withheld, reasons);
    }

    /// <summary>
    /// One audit entry for Activity-gated evidence shown - process ids, roles, applications, client addresses, a
    /// prepared transaction's gid and owner - the first time a circuit is shown it for a store and database, and
    /// again only when the set of checks showing it changes: the Sessions page's first-read rule, by signature, so a
    /// page's interval reads record nothing more.
    /// </summary>
    private void RecordActivityShown(StudioScope scope, string storeKey, string databaseId, SortedSet<string> codes)
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
            new StudioScope(scope.StoreKey, databaseId, null));
    }

    /// <summary>
    /// Every check's status row, with Postgres' words masked for this visitor - and, for a check whose gate
    /// this visitor is short of, nothing but its code, title, tier and outcome.
    /// </summary>
    /// <remarks>
    /// A status row carries the check's note, its failure and its statements, and a note can say what a
    /// finding would have said ("3 sessions of role reporting are idle"), so the row is shown at its check's
    /// gate exactly as the findings are.
    /// </remarks>
    private async Task<IReadOnlyList<HealthCheckStatus>> StatusesAsync(
        IReadOnlyList<HealthCheckResult> results,
        IReadOnlyList<HealthReportFinding> shown,
        VisitorGate gate,
        StudioScope scope,
        ResolvedScope resolved,
        CancellationToken cancellationToken)
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
                    result.Note is null ? null : reason,
                    result.Problem is null ? null : reason,
                    null,
                    result.Check.DocsUrl,
                    []));

                continue;
            }

            // The studio's sentence first, as it is; then each other source under its own label (HealthCheckProblem).
            string? problem = result.Problem?.Reason;

            if (result.Problem?.PostgresText is { Length: > 0 } text)
            {
                if (!withheldRead)
                {
                    withheldRead = true;
                    withheld = await WithheldSchemasAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
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

            statuses.Add(new HealthCheckStatus(
                result.Check.Code,
                result.Check.Title,
                result.Check.Area,
                result.Check.Tier,
                result.Outcome,
                shown.Count(x => string.Equals(x.Code, result.Check.Code, StringComparison.Ordinal)),
                result.Note,
                problem,
                result.Problem?.SqlState,
                result.Check.DocsUrl,
                result.Statements));
        }

        return statuses;
    }

    /// <summary>
    /// The schemas this visitor may not see, from the database browser's own gate - or <see langword="null" />
    /// when it cannot be read, which withholds Postgres' words outright.
    /// </summary>
    private async Task<IReadOnlySet<string>?> WithheldSchemasAsync(StudioScope scope, ResolvedScope resolved, CancellationToken cancellationToken)
    {
        DatabaseAccess? access = services.GetService<DatabaseAccess>();
        if (access is null)
        {
            return null;
        }

        try
        {
            DatabaseGateRead read = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
            return read.Succeeded ? read.Gate.WithheldSchemas : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Marten Studio could not read the schema gate to mask a health check's error");
            return null;
        }
    }

    /// <summary>
    /// One visitor's answers to the three gates, asked lazily and at most once each per report.
    /// </summary>
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
            HealthGate.Database => "needs the store policy to allow this database as a whole, with no tenant selected in the policy's resource",
            HealthGate.Activity => activityRefusal ?? "needs " + StudioCapabilityGuard.OptionName(StudioCapability.ViewDatabaseActivity),
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
                    ? "needs ViewDatabaseActivity, and MartenStudioOptions.ReadOnly is true, which turns every capability off"
                    : "needs " + StudioCapabilityGuard.OptionName(StudioCapability.ViewDatabaseActivity);
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
