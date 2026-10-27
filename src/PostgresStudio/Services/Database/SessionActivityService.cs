using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace PostgresStudio.Services.Database;

/// <summary>
/// Database › Sessions' per-visitor half: the shared snapshot, shown through this visitor's gates.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two gates, in the database browser's order</b> (decision 4, D27). The summary needs the store policy for
/// the database as a whole (<c>TenantId = null</c>): a session belongs to no tenant, so a visitor confined to one
/// is refused it, as they should be. The details are <c>ViewDatabaseActivity</c>'s: <see cref="StudioCapabilityGuard.Require" />
/// first, before anybody is asked who is asking; then the scope, resolved with no tenant - the store policy, asked
/// by the resolver before anything is looked up; then the store policy and the write policy, one at a time and
/// both tenant-less, so the refusal and event 9203 name the one that said no. Every refusal of the details is
/// audited, under <c>ViewDatabaseActivity</c>, so the Activity screen shows it only to a visitor who may make the
/// same read (<c>StudioActionLog.GetVisibleAsync</c>).
/// </para>
/// <para>
/// <b>About the store and database the scope resolved to, by their own names</b> (AGENTS.md D29). The resolver has to
/// ask before it looks anything up, so its question carries the visitor's spelling - a store key matches in any case,
/// and a database id is often empty, meaning the store's first. Every question after it, and every audit entry, names
/// the store's registered key and <c>Id.Identity</c> of the database it resolved to, as the Health page and the
/// high-water Explain do: one policy is asked one question about one database, and the audit never names a resource
/// the policy was not shown. Where either spelling differs, the store policy is asked again about the resolved names,
/// and both must pass.
/// </para>
/// <para>
/// <b>Audited by signature, never per poll.</b> The first details read of a store's database in a circuit is
/// one ring entry and one event 9245; the page's interval reads and its Refresh button repeat that signature and
/// record nothing (the D27 first-read rule, through <see cref="TableRowAuditLedger" />). The page asks for details
/// only once interactive and only while the summary's <see cref="SessionDetailsAccess" /> says the gate is open,
/// so a page whose gate is closed refuses nothing.
/// </para>
/// <para>
/// <b>Masked per visitor.</b> A statement is shown through <see cref="SessionQueryText" /> with this visitor's
/// withheld schemas - the database browser's own gate (<see cref="DatabaseAccess.GateAsync" />) - and withheld
/// outright when those cannot be read, when it names a hidden document type's table, or (while hiding is
/// configured) one no store declares; a lock on a relation in a schema the visitor may not see names no
/// relation; a hidden document type's table is never named to anybody.
/// </para>
/// <para>
/// <b>Labelled over every store.</b> Locks are labelled with the database browser's classifier over every
/// registered store's declarations (<see cref="DatabaseAccess.ReadDeclarations(ResolvedSource)" />), so another
/// store's table is Marten's and named to nobody, and by their owners - the lock reads never return an index's,
/// a partition's or a TOAST table's own name (<see cref="SessionLockLabels" />). Which advisory keys are the source's
/// own is its <see cref="ISessionLockLabeler" />'s to say.
/// </para>
/// <para>
/// Scoped: the ledger is this circuit's. The snapshot is the process's (<see cref="SessionActivitySnapshots" />).
/// </para>
/// </remarks>
internal sealed class SessionActivityService : ISessionActivityService
{
    /// <summary>What the audit ring calls reading session details.</summary>
    internal const string DetailsAction = "Read database sessions";

    /// <summary>What the audit ring says was read.</summary>
    internal const string DetailsTarget = "sessions, locks and prepared transactions";

    /// <summary>What a capability that is off says.</summary>
    internal const string CapabilityDenial =
        "Process ids, roles, client addresses, application names, connection times and statements need "
        + "MartenStudioOptions.Capabilities.ViewDatabaseActivity.";

    /// <summary>What <c>ReadOnly</c> says.</summary>
    internal const string ReadOnlyDenial =
        "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.ViewDatabaseActivity off with every "
        + "other capability.";

    /// <summary>What the store policy's refusal says.</summary>
    internal const string StorePolicyDenial =
        "Your account may not see this database's sessions. The store policy (MartenStudioOptions.StoreAuthorizationPolicy) "
        + "refused this store and database as a whole, with no tenant selected: a session belongs to no tenant.";

    /// <summary>What the write policy's refusal says.</summary>
    internal const string WritePolicyDenial =
        "Your account may not see who is using this database. The write policy (MartenStudioOptions.WriteAuthorizationPolicy, "
        + "or StoreAuthorizationPolicy when no write policy is set) refused ViewDatabaseActivity for this store and database "
        + "as a whole, with no tenant selected.";

    // The audit ledger is keyed by store, database and what was read; these name "what" for the ledger, which
    // was written for relations and is reused here as it is.
    private const string LedgerSchema = "(activity)";
    private const string LedgerRelation = "sessions";
    private const string LedgerKind = "details";
    private const string Signature = "details";

    private readonly IStudioSettings settings;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly StudioActionLog audit;
    private readonly SessionActivitySnapshots snapshots;
    private readonly SessionPoolObserver pools;
    private readonly DatabaseAccess access;
    private readonly ISessionLockLabeler labeler;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<SessionActivityService> logger;
    private readonly TableRowAuditLedger ledger = new();

    public SessionActivityService(
        IStudioSettings settings,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        StudioActionLog audit,
        SessionActivitySnapshots snapshots,
        SessionPoolObserver pools,
        DatabaseAccess access,
        ISessionLockLabeler labeler,
        StudioLogThrottle throttle,
        ILogger<SessionActivityService> logger)
    {
        this.settings = settings;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.resolver = resolver;
        this.audit = audit;
        this.snapshots = snapshots;
        this.pools = pools;
        this.access = access;
        this.labeler = labeler;
        this.throttle = throttle;
        this.logger = logger;
    }

    /// <summary>How many first reads this circuit has recorded, for a test.</summary>
    internal int RecordedReads => ledger.Count;

    /// <inheritdoc />
    public async Task<SessionActivitySummary> GetSummaryAsync(StudioScope scope, CancellationToken cancellationToken = default)
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
            return SessionActivitySummary.Refused(DatabaseRefusal.StorePolicy, settings.Text.SessionsStorePolicyDenial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return SessionActivitySummary.Refused(DatabaseRefusal.Unavailable, exception.Message);
        }

        // From here on, the store and database the scope resolved to, by the registered key and the identity (D29).
        StudioScope whole = WholeDatabase(resolved);

        if (!await StorePolicyPassesAsync(asked, whole, cancellationToken).ConfigureAwait(false))
        {
            return SessionActivitySummary.Refused(DatabaseRefusal.StorePolicy, settings.Text.SessionsStorePolicyDenial);
        }

        SessionDetailsAccess details = await AccessAsync(whole, cancellationToken).ConfigureAwait(false);

        SessionSnapshot snapshot;

        try
        {
            snapshot = await snapshots.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SessionActivitySummary.Refused(DatabaseRefusal.Unavailable, Failure(exception, resolved)) with { Details = details };
        }

        SessionLockLabels labels = labeler.For(resolved, Classifier(resolved));

        List<string> caveats = [];

        if (snapshot.Visibility.Caveat is { } hidden)
        {
            caveats.Add(hidden);
        }

        if (snapshot.TotalSessions > snapshot.Sessions.Count)
        {
            caveats.Add("Only the " + SessionText.Number(snapshot.Sessions.Count) + " sessions with the oldest transactions of "
                + SessionText.Number(snapshot.TotalSessions) + " are listed.");
        }

        if (snapshot.TotalLocks > snapshot.Locks.Count)
        {
            caveats.Add("Only " + SessionText.Number(snapshot.Locks.Count) + " of " + SessionText.Number(snapshot.TotalLocks)
                + " locks are counted, the awaited ones first.");
        }

        return new SessionActivitySummary
        {
            MeasuredAt = snapshot.ReadAt,
            SessionCount = snapshot.TotalSessions,
            ByState =
            [
                .. snapshot.Sessions
                    .GroupBy(static x => x.State ?? "not readable", StringComparer.Ordinal)
                    .OrderByDescending(static x => x.Count())
                    .ThenBy(static x => x.Key, StringComparer.Ordinal)
                    .Select(static x => new KeyValuePair<string, int>(x.Key, x.Count())),
            ],
            Sessions =
            [
                .. snapshot.Sessions.Select(static x => new SessionSummaryRow(
                    x.State, x.WaitEventType, x.TransactionAge, x.QueryAge, x.StateAge, x.BlockedBy.Count, x.HasTransactionId)),
            ],
            Waiting = snapshot.Sessions.Count(static x => string.Equals(x.WaitEventType, "Lock", StringComparison.Ordinal)),
            LongestTransaction = snapshot.Sessions.Max(static x => x.TransactionAge),
            Locks =
            [
                .. snapshot.Locks
                    .Select(x => (Lock: x, Target: labels.Describe(x.LockType, x.Relation, x.AdvisoryKey)))
                    .GroupBy(static x => (x.Target.Label, x.Lock.Mode, x.Lock.Granted))
                    .OrderBy(static x => x.Key.Granted)
                    .ThenByDescending(static x => x.Count())
                    .Select(static x => new LockSummaryRow(x.Key.Label, x.Key.Mode, x.Key.Granted, x.Count(), x.Max(static y => y.Lock.Waited))),
            ],
            LockCount = snapshot.TotalLocks,
            PreparedCount = snapshot.Prepared.Count,
            OldestPrepared = snapshot.Prepared.Count == 0 ? null : snapshot.Prepared.Max(static x => x.Age),
            MaxPreparedTransactions = snapshot.MaxPreparedTransactions,
            Pool = PoolOf(resolved),

            // Issue #8 G9: the database's total row and the server's budget are database-wide; the applications are named
            // only in the details, behind ViewDatabaseActivity.
            Connections = snapshot.Connections?.Database,
            Budget = snapshot.Connections?.ToBudget(),
            ApplicationCount = snapshot.Connections?.ApplicationCount ?? 0,
            Caveats = caveats,
            Blocking = CountsOf(snapshot.Blocking),
            LockStatistics = snapshot.LockStatistics,
            Details = details,
            Statements = snapshot.Statements,
        };
    }

    /// <summary>Sessions › Blocking's numbers (X3): how many chains, how many sessions they hold up, for how long - naming nobody.</summary>
    internal static SessionBlockingCounts CountsOf(LockTableRead? read)
    {
        if (read is null || read.Nodes.Count == 0)
        {
            return SessionBlockingCounts.None;
        }

        IReadOnlyList<BlockingChain<LockGraphNode>> chains = Chains(read);
        List<BlockingChain<LockGraphNode>> headed = [.. chains.Where(static x => !x.HeadWaits && !x.Root.Node.WaitsForALock)];

        // Every waiting session is held up by somebody, a session in a cycle included, and each counts once.
        return new SessionBlockingCounts(
            headed.Count,
            read.Waiting.Select(static x => x.ProcessId).Distinct().Count(),
            read.Waiting.Select(static x => (TimeSpan?) x.Wanted!.Waited).Max(),
            headed.Count(static x => x.Root.Node.SameDatabase && x.Root.Node.IsIdleInTransaction),
            chains.Count - headed.Count);
    }

    /// <summary>The read's chains, through the one tree builder C12 judges with.</summary>
    private static IReadOnlyList<BlockingChain<LockGraphNode>> Chains(LockTableRead read) =>
        BlockingForest.Build(read.Nodes, static x => x.ProcessId, static x => x.BlockedBy, static x => x.Wanted?.Waited);

    /// <summary>
    /// Sessions › Blocking's tree for this visitor (X3): every chain, each session with what it waits for in the studio's
    /// words and the relation masked through <paramref name="withheld" /> (D27).
    /// </summary>
    internal static IReadOnlyList<BlockingChainView> TreeOf(LockTableRead? read, SessionLockLabels labels, IReadOnlySet<string>? withheld)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (read is null || read.Nodes.Count == 0)
        {
            return [];
        }

        return
        [
            .. Chains(read).Select(chain => new BlockingChainView(
                View(chain.Root),
                chain.HeadWaits || chain.Root.Node.WaitsForALock,
                chain.Blocked.Count,
                BlockingForest.Longest(chain.Root, static x => x.Wanted?.Waited) is { } longest && longest > TimeSpan.Zero ? longest : null)),
        ];

        BlockingNodeView View(BlockingBranch<LockGraphNode> branch)
        {
            LockGraphNode node = branch.Node;
            LockTarget? target = node.Wanted is { } wanted
                ? labels.Describe(wanted.LockType, wanted.Relation, wanted.AdvisoryKey, wanted.TupleRelation)
                : null;

            return new BlockingNodeView
            {
                ProcessId = node.ProcessId,
                User = node.User,
                Application = node.Application,
                ClientAddress = node.ClientAddress,
                State = node.State,
                WaitEventType = node.WaitEventType,
                WaitEvent = node.WaitEvent,
                TransactionAge = node.TransactionAge,
                StateAge = node.StateAge,
                Waited = node.Wanted?.Waited,
                ExactWait = node.Wanted?.ExactStart ?? true,
                WantedMode = node.Wanted?.Mode,
                HeldModes = node.HeldModes,

                // A label names only this store's own objects and Postgres' catalogs; masked all the same, as the lock list's is.
                Target = target is null ? null : withheld is null ? target.Label : WithheldNames.Redact(target.Label, withheld) ?? target.Label,
                Relation = target is null ? null : RelationShown(target, withheld),
                SameDatabase = node.SameDatabase,
                Known = node.Known,
                VacuumLabel = node.VacuumLabel,
                BlockedBy = node.BlockedBy,
                Repeat = branch.Repeat,
                Waiters = [.. branch.Waiters.Select(View)],
            };
        }
    }

    /// <summary>
    /// A waited-for relation as Sessions › Blocking shows it (D27): qualified, for one in a schema the visitor may see;
    /// <see cref="WithheldNames.Token" /> alone - its name blanked, as a structured field is - for one in a schema they may not;
    /// nothing for one nobody is told about, or when the visitor's withheld schemas could not be read.
    /// </summary>
    internal static string? RelationShown(LockTarget target, IReadOnlySet<string>? withheld)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Schema is not { } schema || target.Name is not { } name || withheld is null)
        {
            return null;
        }

        return withheld.Contains(schema) && !BrowsableSchemaMatcher.IsSystemSchema(schema)
            ? WithheldNames.Token
            : DatabaseLinks.QualifiedName(schema, name);
    }

    /// <inheritdoc />
    public async Task<SessionActivityDetails> GetDetailsAsync(StudioScope scope, CancellationToken cancellationToken = default)
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
            audit.RecordCapabilityDenied(denied, DetailsAction, DetailsTarget);

            return denied.Reason == CapabilityDenialReason.ReadOnly
                ? SessionActivityDetails.Refused(DatabaseRefusal.ReadOnly, settings.Text.SessionsReadOnlyDenial)
                : SessionActivityDetails.Refused(DatabaseRefusal.CapabilityOff, settings.Text.SessionsCapabilityDenial);
        }

        // 2. The scope, resolved with no tenant: the store policy, which the resolver asks before it looks anything up -
        //    with the visitor's spelling of the database, because nothing has been resolved to ask about yet.
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(asked, DatabaseAccess.StorePolicyName(settings), DetailsAction, DetailsTarget, StudioCapability.ViewDatabaseActivity);
            return SessionActivityDetails.Refused(DatabaseRefusal.StorePolicy, settings.Text.SessionsStorePolicyDenial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            audit.Record(DetailsAction, DetailsTarget, succeeded: false, exception.Message, StudioCapability.ViewDatabaseActivity, asked);
            return SessionActivityDetails.Refused(DatabaseRefusal.Unavailable, exception.Message);
        }

        // 3. The two policies, of the store and database it resolved to, by their own names (D29), one at a time
        //    so the refusal names the one that said no. The audit names that database too.
        StudioScope whole = WholeDatabase(resolved);

        if (!await StorePolicyPassesAsync(asked, whole, cancellationToken).ConfigureAwait(false))
        {
            audit.RecordScopeDenied(whole, DatabaseAccess.StorePolicyName(settings), DetailsAction, DetailsTarget, StudioCapability.ViewDatabaseActivity);
            return SessionActivityDetails.Refused(DatabaseRefusal.StorePolicy, settings.Text.SessionsStorePolicyDenial);
        }

        if (!await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false))
        {
            audit.RecordScopeDenied(whole, DatabaseAccess.WritePolicyName(settings), DetailsAction, DetailsTarget, StudioCapability.ViewDatabaseActivity);
            return SessionActivityDetails.Refused(DatabaseRefusal.WritePolicy, settings.Text.SessionsWritePolicyDenial);
        }

        // 4. Only now anything about the database.
        SessionSnapshot snapshot;

        try
        {
            snapshot = await snapshots.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SessionActivityDetails.Refused(DatabaseRefusal.Unavailable, Failure(exception, resolved));
        }

        StatementScreen screen = await ScreenAsync(whole, resolved, settings.IsDocumentTypeVisible is not null, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string>? withheld = screen.Withheld;
        SessionLockLabels labels = labeler.For(resolved, Classifier(resolved));

        SessionActivityDetails details = new()
        {
            MeasuredAt = snapshot.ReadAt,
            Sessions =
            [
                .. snapshot.Sessions.Select(x => new SessionDetailRow(
                    x.ProcessId,
                    x.User,
                    x.Application,
                    x.ClientAddress,
                    x.BackendStart,
                    x.State,
                    x.WaitEventType,
                    x.WaitEvent,
                    x.TransactionAge,
                    x.QueryAge,
                    x.StateAge,
                    x.BlockedBy,
                    SessionQueryText.Present(x.Query, x.QueryTruncated, screen))),
            ],
            Locks =
            [
                .. snapshot.Locks.Select(x =>
                {
                    LockTarget target = labels.Describe(x.LockType, x.Relation, x.AdvisoryKey);

                    // A label names only this store's own objects and Postgres' catalogs, whose schemas are never
                    // withheld; masked all the same, so that a label that ever named more could not say it here.
                    string label = withheld is null ? target.Label : WithheldNames.Redact(target.Label, withheld) ?? target.Label;
                    return new LockDetailRow(x.ProcessId, label, RelationOf(target, withheld), x.Mode, x.Granted, x.Waited);
                }),
            ],
            Prepared = [.. snapshot.Prepared.Select(static x => new PreparedDetailRow(x.Gid, x.Owner, x.Prepared, x.Age))],
            Blocking = TreeOf(snapshot.Blocking, labels, withheld),
            Applications = snapshot.Connections?.Applications ?? [],
            ApplicationCount = snapshot.Connections?.ApplicationCount ?? 0,
            StatementsWithheld = withheld is null,
        };

        await RecordFirstReadAsync(resolved, whole, details).ConfigureAwait(false);

        return details;
    }

    /// <summary>
    /// The database a resolved scope names, as a whole: the store's registered key - a scope's key matches in any case -
    /// the database's own identity, and no tenant.
    /// </summary>
    private static StudioScope WholeDatabase(ResolvedSource resolved) =>
        new(resolved.Source.Key, resolved.Target.Identity, null);

    /// <summary>
    /// The store policy for the resolved store and database. The resolver has asked it already when the visitor spelt
    /// both that way, so it is asked again only when either spelling was another - an empty database, or another case.
    /// </summary>
    private async ValueTask<bool> StorePolicyPassesAsync(StudioScope asked, StudioScope whole, CancellationToken cancellationToken) =>
        (string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal))
        || await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The page's question, never audited: whether the details would be read. Asked the way
    /// <see cref="GetDetailsAsync" /> will ask it, so a section the page draws open is one the service then serves.
    /// The store policy for the database as a whole has already passed by the time this is asked.
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

    /// <summary>
    /// What this visitor's statements are checked against, from the database browser's own gate: the schemas they
    /// may not see, the classifier over every store, and the tables of every store whose policy refuses them - or
    /// <see cref="StatementScreen.Unchecked" /> when the gate cannot be read, which withholds every statement.
    /// </summary>
    private async Task<StatementScreen> ScreenAsync(StudioScope whole, ResolvedSource resolved, bool hidesDocumentTypes, CancellationToken cancellationToken)
    {
        try
        {
            DatabaseGateRead read = await access.GateAsync(whole, resolved, cancellationToken).ConfigureAwait(false);
            return StatementScreen.Of(read, hidesDocumentTypes);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                if (settings.Text.Product == StudioProduct.PostgresStudio)
                {
                    logger.LogDebug(exception, "Postgres Studio could not read the schema gate to mask the statements of {DatabaseId}", resolved.Target.Identity);
                }
                else
                {
                    logger.LogDebug(exception, "Marten Studio could not read the schema gate to mask the statements of {DatabaseId}", resolved.Target.Identity);
                }
            }

            return StatementScreen.Unchecked(hidesDocumentTypes);
        }
    }

    /// <summary>
    /// The database browser's classifier over every registered store's declarations, remembered for the circuit -
    /// or <see langword="null" /> when they cannot all be read, which names no relation and, while document types
    /// may be hidden, shows no statement (fail closed).
    /// </summary>
    private DatabaseObjectClassifier? Classifier(ResolvedSource resolved)
    {
        try
        {
            DatabaseDeclarations declared = access.ReadDeclarations(resolved);
            return declared.Succeeded ? declared.Classifier : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                if (settings.Text.Product == StudioProduct.PostgresStudio)
                {
                    logger.LogDebug(exception, "Postgres Studio could not classify the relations of {DatabaseId}", resolved.Target.Identity);
                }
                else
                {
                    logger.LogDebug(exception, "Marten Studio could not classify the relations of {DatabaseId}", resolved.Target.Identity);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A lock's relation as this visitor may be told it: qualified, for one in a schema they may see; nothing for
    /// a relation in a withheld schema (a structured field is blanked, never half-masked, D27) or one nobody is
    /// told about.
    /// </summary>
    private static string? RelationOf(LockTarget target, IReadOnlySet<string>? withheld)
    {
        if (target.Schema is not { } schema || target.Name is not { } name || withheld is null)
        {
            return null;
        }

        return withheld.Contains(schema) && !BrowsableSchemaMatcher.IsSystemSchema(schema)
            ? null
            : DatabaseLinks.QualifiedName(schema, name);
    }

    /// <summary>One ring entry and one event 9245 for the first details read of this database in this circuit.</summary>
    private async Task RecordFirstReadAsync(ResolvedSource resolved, StudioScope whole, SessionActivityDetails details)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;

        if (!ledger.TryRecord(TableRowAuditLedger.Key(storeKey, databaseId, LedgerSchema, LedgerRelation, LedgerKind), Signature, out _))
        {
            return;
        }

        audit.Record(
            DetailsAction,
            DetailsTarget,
            succeeded: true,
            "Session details read: " + SessionText.Count(details.Sessions.Count, "session") + ", "
            + SessionText.Count(details.Locks.Count, "lock") + ", " + SessionText.Count(details.Prepared.Count, "prepared transaction") + ".",
            StudioCapability.ViewDatabaseActivity,
            whole);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.SessionDetailsRead(
                settings.Text.Product,
                await authorization.UserNameAsync().ConfigureAwait(false),
                storeKey,
                databaseId,
                details.Sessions.Count,
                details.Locks.Count,
                details.Prepared.Count);
        }
    }

    /// <summary>The database's pool in this process, while somebody holds a lease on the observer.</summary>
    private SessionPoolSummary? PoolOf(ResolvedSource resolved)
    {
        DatabasePoolSettings? pool = DatabasePoolSettings.Of(resolved.Target);

        if (pool is null)
        {
            return null;
        }

        if (!pool.Pooling)
        {
            return new SessionPoolSummary(false, null, null, pool.MaxPoolSize, false);
        }

        if (!pools.IsListening)
        {
            return null;
        }

        try
        {
            NpgsqlPoolReading? reading = pools.Read().FirstOrDefault(x => DatabasePoolSettings.IsPoolOf(resolved.Target, x.PoolName));

            return reading switch
            {
                null => new SessionPoolSummary(false, null, null, pool.MaxPoolSize, true),
                { IsAmbiguous: true } => new SessionPoolSummary(false, null, null, pool.MaxPoolSize, true, Ambiguous: true),
                _ => new SessionPoolSummary(true, reading.Used, reading.Idle, reading.Max ?? pool.MaxPoolSize, true),
            };
        }
        catch (InvalidOperationException)
        {
            // The last lease went between the question and the read.
            return null;
        }
    }

    /// <summary>
    /// The sentence for a read that failed - the studio's words and the SQLSTATE, never Postgres' own, which could
    /// name a schema this visitor may not see - and the log line: Debug for an expected state, a throttled Warning
    /// for anything else.
    /// </summary>
    private string Failure(Exception exception, ResolvedSource resolved)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        string? expected = postgres?.SqlState switch
        {
            PostgresErrorCodes.InsufficientPrivilege => "The studio's Postgres role may not read this database's sessions or locks.",
            PostgresErrorCodes.QueryCanceled => "Reading the sessions ran out of its statement_timeout; Refresh tries again.",
            PostgresErrorCodes.LockNotAvailable => "Reading the sessions waited too long for a lock; Refresh tries again.",
            _ => PostgresFailure.IsTimeout(exception) ? "Reading the sessions timed out; Refresh tries again." : null,
        };

        if (expected is not null)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                if (settings.Text.Product == StudioProduct.PostgresStudio)
                {
                    logger.LogDebug(exception, "Postgres Studio did not read the sessions of {DatabaseId}: {Reason}", databaseId, expected);
                }
                else
                {
                    logger.LogDebug(exception, "Marten Studio did not read the sessions of {DatabaseId}: {Reason}", databaseId, expected);
                }
            }

            return expected;
        }

        LogLevel level = throttle.WarningOrDebug("Sessions.Read", storeKey, databaseId, StudioLogThrottle.KindOf(exception));
        if (settings.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioSessionActivityReadFailed(level, exception, storeKey, databaseId, postgres?.SqlState);
        }
        else
        {
            logger.SessionActivityReadFailed(level, exception, storeKey, databaseId, postgres?.SqlState);
        }

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "The sessions could not be read" + (postgres is null
                ? " (" + exception.GetType().Name + ")."
                : " (SQLSTATE " + postgres.SqlState + ").");
    }
}
