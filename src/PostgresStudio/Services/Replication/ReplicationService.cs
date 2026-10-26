using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Replication;

/// <summary>What the Replication page reads: the database's facts through this visitor's gates.</summary>
/// <remarks>An interface so the page can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IReplicationService
{
    /// <summary>The visitor's view of the scope's database. Never throws but for cancellation; a refusal is a value.</summary>
    Task<ReplicationView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>One tablespace's size, on demand and one at a time. Never throws but for cancellation.</summary>
    Task<TablespaceSize> MeasureTablespaceAsync(StudioScope scope, string tablespace, CancellationToken cancellationToken = default);
}

/// <summary>Why a visitor is shown no replication facts.</summary>
internal enum ReplicationRefusal
{
    /// <summary>Shown.</summary>
    None,

    /// <summary>The store policy refused the database as a whole.</summary>
    StorePolicy,

    /// <summary>The source or the database could not be reached or resolved.</summary>
    Unavailable,
}

/// <summary>One standby as this visitor is shown it: its names only through the activity gate.</summary>
/// <param name="Name">Its <c>application_name</c>, or <see langword="null" /> when withheld.</param>
/// <param name="Client">Its client address, or <see langword="null" /> when withheld (or a local socket).</param>
/// <param name="User">The role it connected as, or <see langword="null" /> when withheld.</param>
/// <param name="Row">Its numbers and states - names blanked unless shown.</param>
internal sealed record StandbyView(string? Name, string? Client, string? User, StandbyRow Row);

/// <summary>The standbys section.</summary>
/// <param name="Total">How many physical senders and logical senders of this database there are.</param>
/// <param name="Unreadable">How many of the physical ones the reading role may not read.</param>
/// <param name="NotChecked">The "not checked: this needs pg_read_all_stats" sentence, when any is unreadable.</param>
/// <param name="Rows">The listed senders: physical ones first, then this database's logical ones.</param>
/// <param name="OtherDatabases">Logical senders of other databases: counted, never listed or named (W5-R2-fix B1).</param>
/// <param name="Downstream">Whether this server is a standby, so the physical senders are cascading standbys R03 does not judge.</param>
internal sealed record StandbysView(long Total, int Unreadable, string? NotChecked, IReadOnlyList<StandbyView> Rows, long OtherDatabases = 0, bool Downstream = false);

/// <summary>One replication slot as this visitor is shown it.</summary>
/// <param name="Description">
/// R01's own wording: <see cref="ReplicationSlot.Describe" /> through the activity gate, and otherwise only its kind; a
/// logical slot of another database is never named to anybody.
/// </param>
/// <param name="Slot">The slot's numbers and states (its name, plugin and database are not to be drawn from here).</param>
/// <param name="Statistics">Its decoding statistics and 17's failover flags, or <see langword="null" />.</param>
internal sealed record SlotView(string Description, ReplicationSlot Slot, SlotStatisticsRow? Statistics);

/// <summary>The slots section.</summary>
/// <param name="Total">How many slots the server has.</param>
/// <param name="MaxSlotWalKeepBytes"><c>max_slot_wal_keep_size</c>.</param>
/// <param name="WalKeepBytes"><c>wal_keep_size</c>.</param>
/// <param name="IdleSlotTimeout"><c>idle_replication_slot_timeout</c>, or the version it needs.</param>
/// <param name="HasStatistics">Whether the server has <c>pg_stat_replication_slots</c>.</param>
/// <param name="Rows">The slots, most WAL retained first.</param>
internal sealed record SlotsView(long Total, long? MaxSlotWalKeepBytes, long? WalKeepBytes, string IdleSlotTimeout, bool HasStatistics, IReadOnlyList<SlotView> Rows);

/// <summary>The archiver section.</summary>
/// <param name="Facts">The counters, file names and settings; the command's text is <see cref="CommandText" />'s alone.</param>
/// <param name="CommandText"><c>archive_command</c>'s text through the activity gate, or <see langword="null" />.</param>
/// <param name="CommandWithheld">Why its text is not shown, when it is set and not shown.</param>
/// <param name="Directory"><c>pg_wal</c>'s ready files and size, with <c>pg_monitor</c>.</param>
/// <param name="DirectoryNotChecked">The "not checked: this needs pg_monitor" sentence without it.</param>
internal sealed record ArchiverView(ArchiverFacts Facts, string? CommandText, string? CommandWithheld, WalDirectoryFacts? Directory, string? DirectoryNotChecked);

/// <summary>The WAL section.</summary>
/// <param name="Totals">The counters since the statistics were reset.</param>
/// <param name="Window">The window the rates are over, or <see langword="null" /> before two of the advisor's samples.</param>
/// <param name="BytesPerSecond">WAL written per second over the window.</param>
/// <param name="FullPageImageShare">Full-page images among the WAL records written over the window.</param>
internal sealed record WalView(WalStatistics Totals, CounterWindow? Window, double? BytesPerSecond, double? FullPageImageShare);

/// <summary>The checkpoints section.</summary>
/// <param name="Totals">The counters since they were reset.</param>
/// <param name="CheckpointTimeout"><c>checkpoint_timeout</c>.</param>
/// <param name="MaxWalSizeBytes"><c>max_wal_size</c>.</param>
/// <param name="Window">The window of the advisor's samples, or <see langword="null" />.</param>
/// <param name="Judgement">R04's reading of that window, or <see langword="null" /> without one.</param>
internal sealed record CheckpointsView(
    CheckpointStatistics Totals,
    TimeSpan? CheckpointTimeout,
    long? MaxWalSizeBytes,
    CounterWindow? Window,
    RequestedCheckpointsCheck.Judgement? Judgement);

/// <summary>One section as this visitor is shown it: a value, or the sentence in its place.</summary>
/// <typeparam name="T">What the section shows.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Outcome">How the read came out.</param>
/// <param name="Note">Why there is no value: not applicable here, not on this release, or the failure's sentence.</param>
internal sealed record ReplicationSectionView<T>(T? Value, ReplicationReadOutcome Outcome, string? Note)
    where T : class
{
    /// <summary>A section with a value.</summary>
    public static ReplicationSectionView<T> Of(T value) => new(value, ReplicationReadOutcome.Read, null);

    /// <summary>The same outcome as <paramref name="read" />, without its value.</summary>
    public static ReplicationSectionView<T> Without<TRead>(ReplicationRead<TRead> read)
        where TRead : class =>
        new(null, read.Outcome, read.Note);
}

/// <summary>The Replication page's view of one database, for one visitor.</summary>
internal sealed record ReplicationView
{
    /// <summary>Why nothing is shown, or <see cref="ReplicationRefusal.None" />.</summary>
    public ReplicationRefusal Refusal { get; init; }

    /// <summary>The refusal's sentence.</summary>
    public string? Reason { get; init; }

    /// <summary>When the facts were read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The server's version and platform.</summary>
    public PostgresServerFacts? Server { get; init; }

    /// <summary>The role the reads run as, for the "not checked" sentences.</summary>
    public string? ReadsAs { get; init; }

    /// <summary>What the role holds, in words ("pg_monitor, pg_read_all_stats", "superuser").</summary>
    public string? MemberOf { get; init; }

    /// <summary>Whether names - standbys', slots', the archive command's - are shown to this visitor.</summary>
    public bool NamesShown { get; init; }

    /// <summary>What it would take to see them, when they are withheld.</summary>
    public string? NamesWithheld { get; init; }

    /// <summary>Primary or standby, and a standby's replay.</summary>
    public ReplicationSectionView<ReplicationRole> Role { get; init; } = Empty<ReplicationRole>();

    /// <summary>The slots.</summary>
    public ReplicationSectionView<SlotsView> Slots { get; init; } = Empty<SlotsView>();

    /// <summary>The standbys.</summary>
    public ReplicationSectionView<StandbysView> Standbys { get; init; } = Empty<StandbysView>();

    /// <summary>
    /// On Aurora, the cluster's instances and each reader's lag (A3) - shown to whoever the page's database-wide gate admits,
    /// because an instance's identifier is the cluster's own name for it (D29) - and <see langword="null" /> elsewhere.
    /// </summary>
    public ReplicationSectionView<Aurora.AuroraReplicaStatus>? AuroraReplicas { get; init; }

    /// <summary>The archiver.</summary>
    public ReplicationSectionView<ArchiverView> Archiver { get; init; } = Empty<ArchiverView>();

    /// <summary>WAL volume.</summary>
    public ReplicationSectionView<WalView> Wal { get; init; } = Empty<WalView>();

    /// <summary>Checkpoints.</summary>
    public ReplicationSectionView<CheckpointsView> Checkpoints { get; init; } = Empty<CheckpointsView>();

    /// <summary><c>pg_stat_io</c>.</summary>
    public ReplicationSectionView<IoStatistics> Io { get; init; } = Empty<IoStatistics>();

    /// <summary>The tablespaces - names and locations, never sizes.</summary>
    public ReplicationSectionView<TablespaceList> Tablespaces { get; init; } = Empty<TablespaceList>();

    /// <summary>Every statement the facts read ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>A refusal.</summary>
    public static ReplicationView Refused(ReplicationRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };

    private static ReplicationSectionView<T> Empty<T>()
        where T : class =>
        new(null, ReplicationReadOutcome.Failed, null);
}

/// <summary>One tablespace's size as this visitor asked for it.</summary>
/// <param name="Tablespace">The tablespace.</param>
/// <param name="Bytes">Its size, or <see langword="null" /> when not measured.</param>
/// <param name="Problem">Why not: "not readable by this role", a refusal, a failure.</param>
internal sealed record TablespaceSize(string Tablespace, long? Bytes, string? Problem);

/// <summary>
/// The Replication page's per-visitor half: the scope resolved for the database as a whole, the shared facts, and each name
/// shown only through the activity gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two gates</b> (W5-R2 step 2; AGENTS.md D29). Every number is the server's or the database's, so it is shown to a
/// visitor the store policy passes for the database as a whole (<c>TenantId = null</c>): the Health page's
/// <see cref="HealthGate.Database" />. Names - a standby's <c>application_name</c>, client address and role, a slot's name,
/// plugin and database (R01's rule, through <see cref="ReplicationSlot.Describe" />; another database's logical slot is
/// never named), the archive command's text - are <see cref="HealthGate.Activity" />'s: <c>ViewDatabaseActivity</c> on, and
/// the write policy with the capability named, against the same tenant-less resource.
/// </para>
/// <para>
/// <b>About the store and database the scope resolved to, by their own names</b> (D29): the resolver's first question takes
/// the visitor's spelling, and every question after it, and the audit, the registered key and the resolved identity.
/// </para>
/// <para>
/// <b>Audited by signature, never per poll.</b> The first view in a circuit that shows any name is one Activity entry under
/// <c>ViewDatabaseActivity</c>, per store and database (<see cref="TableRowAuditLedger" />); the page's interval reads repeat
/// it and record nothing. A page that shows no name records nothing.
/// </para>
/// <para>
/// <b>The rates are the advisor's</b>: WAL per second and the full-page-image share are window deltas of R04's samples, and
/// the checkpoints' window is R04's own judgement, read from <see cref="HealthHistory" /> at the database-wide gate. The
/// page starts no sweep: the samples are taken while the Health page or the Overview is open.
/// </para>
/// <para>Scoped: the ledger and the "one measurement at a time" are this circuit's. The facts are the process's.</para>
/// </remarks>
internal sealed class ReplicationService : IReplicationService
{
    /// <summary>What the audit ring calls showing replication names.</summary>
    internal const string NamesAction = "Read replication names";

    /// <summary>What the audit ring says was shown.</summary>
    internal const string NamesTarget = "standbys' application names and client addresses, replication slot names";

    private const string LedgerSchema = "(replication)";
    private const string LedgerRelation = "names";
    private const string LedgerKind = "activity";
    private const string Signature = "names";

    private readonly IStudioSettings settings;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly StudioActionLog audit;
    private readonly ReplicationFactsCache facts;
    private readonly HealthHistory history;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<ReplicationService> logger;
    private readonly TableRowAuditLedger ledger = new();

    public ReplicationService(
        IStudioSettings settings,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        StudioActionLog audit,
        ReplicationFactsCache facts,
        HealthHistory history,
        StudioLogThrottle throttle,
        ILogger<ReplicationService> logger)
    {
        this.settings = settings;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.resolver = resolver;
        this.audit = audit;
        this.facts = facts;
        this.history = history;
        this.throttle = throttle;
        this.logger = logger;
    }

    /// <summary>How many first reads of names this circuit has recorded, for a test.</summary>
    internal int RecordedReads => ledger.Count;

    /// <inheritdoc />
    public async Task<ReplicationView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        (ResolvedSource? resolved, ReplicationView? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            return refused!;
        }

        StudioScope whole = Whole(resolved);
        (bool names, string? withheld) = await NamesAsync(whole, cancellationToken).ConfigureAwait(false);

        ReplicationFacts read;

        try
        {
            read = await facts.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ReplicationView.Refused(ReplicationRefusal.Unavailable, Unreachable(exception, resolved));
        }

        HealthHistoryView samples = history.Read(resolved.Source.Key, resolved.Target.Identity).VisibleAt(HealthGate.Database);
        ReplicationView view = Build(read, names, withheld, samples, settings.Text);

        if (names && ShowsNames(view))
        {
            RecordFirstRead(resolved, whole);
        }

        return view;
    }

    /// <inheritdoc />
    public async Task<TablespaceSize> MeasureTablespaceAsync(StudioScope scope, string tablespace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrEmpty(tablespace);

        (ResolvedSource? resolved, ReplicationView? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            return new TablespaceSize(tablespace, null, refused!.Reason);
        }

        // One measurement at a time per database, for the whole process: pg_tablespace_size walks a directory, and every
        // circuit's click is the same walk (W5-R2-fix 13).
        if (!facts.TryBeginMeasuring(resolved))
        {
            return new TablespaceSize(tablespace, null, "Another tablespace of this database is being measured; one at a time.");
        }

        try
        {
            long? bytes = await facts.Reader.TablespaceSizeAsync(resolved, tablespace, cancellationToken).ConfigureAwait(false);
            return new TablespaceSize(tablespace, bytes, bytes is null ? "No such tablespace on this server." : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

            if (postgres?.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                return new TablespaceSize(tablespace, null, "Not readable by this role: pg_tablespace_size needs CREATE on the tablespace or "
                    + PostgresGrant.ReadAllStats.Described + ", except for this database's default tablespace.");
            }

            if (postgres?.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable || PostgresFailure.IsTimeout(exception))
            {
                return new TablespaceSize(tablespace, null, "Measuring it ran out of time; try again.");
            }

            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioTablespaceSizeFailed(exception, resolved.Source.Key, resolved.Target.Identity, postgres?.SqlState);
            }
            else
            {
                logger.TablespaceSizeFailed(exception, resolved.Source.Key, resolved.Target.Identity, postgres?.SqlState);
            }

            return new TablespaceSize(tablespace, null, "It could not be measured" + (postgres is null ? "." : " (SQLSTATE " + postgres.SqlState + ")."));
        }
        finally
        {
            facts.EndMeasuring(resolved);
        }
    }

    /// <summary>
    /// The visitor's view of <paramref name="read" />: every section's numbers, with names only when <paramref name="names" />.
    /// Pure, for the unit tests of what is shown and withheld.
    /// </summary>
    internal static ReplicationView Build(ReplicationFacts read, bool names, string? withheld, HealthHistoryView samples, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(text);

        PostgresGrants? grants = read.Grants.Value;
        string readsAs = grants?.ReadsAs ?? "the studio's role";
        PostgresPlatform platform = read.Server.Platform;
        ReplicationSettings replicationSettings = read.Settings.Value ?? ReplicationSettings.None;

        return new ReplicationView
        {
            MeasuredAt = read.ReadAt,
            Server = read.Server,
            ReadsAs = grants?.ReadsAs,
            MemberOf = grants?.MemberOf,
            NamesShown = names,
            NamesWithheld = names ? null : withheld,
            Role = read.Role.Value is { } role ? ReplicationSectionView<ReplicationRole>.Of(role) : ReplicationSectionView<ReplicationRole>.Without(read.Role),
            Slots = read.Slots.Value is { } slots
                ? ReplicationSectionView<SlotsView>.Of(Slots(slots, read.SlotStatistics.Value, replicationSettings, read.Server, names))
                : ReplicationSectionView<SlotsView>.Without(read.Slots),
            Standbys = read.Standbys.Value is { } standbys
                ? ReplicationSectionView<StandbysView>.Of(Standbys(standbys, names, platform, readsAs, read.Role.Value?.InRecovery == true))
                : ReplicationSectionView<StandbysView>.Without(read.Standbys),
            AuroraReplicas = read.AuroraReplicas switch
            {
                null => null,
                { Value: { } replicas } => ReplicationSectionView<Aurora.AuroraReplicaStatus>.Of(replicas),
                { } failed => ReplicationSectionView<Aurora.AuroraReplicaStatus>.Without(failed),
            },
            Archiver = read.Archiver.Value is { } archiver
                ? ReplicationSectionView<ArchiverView>.Of(Archiver(archiver, read.WalDirectory, names, withheld, platform, readsAs))
                : ReplicationSectionView<ArchiverView>.Without(read.Archiver),
            Wal = read.Wal.Value is { } wal
                ? ReplicationSectionView<WalView>.Of(Wal(wal, samples, read.ReadAt))
                : read.Wal.Outcome == ReplicationReadOutcome.Read
                    ? new ReplicationSectionView<WalView>(null, ReplicationReadOutcome.NeedsVersion, "Not on this server: " + PostgresFeatures.Needs(PostgresFeature.PgStatWal) + ".")
                    : ReplicationSectionView<WalView>.Without(read.Wal),
            Checkpoints = read.Checkpoints.Value is { } checkpoints
                ? ReplicationSectionView<CheckpointsView>.Of(Checkpoints(checkpoints, replicationSettings, samples, read.ReadAt))
                : ReplicationSectionView<CheckpointsView>.Without(read.Checkpoints),
            Io = read.Io.Value is { } io
                ? ReplicationSectionView<IoStatistics>.Of(io)
                : read.Io.Outcome == ReplicationReadOutcome.Read
                    ? new ReplicationSectionView<IoStatistics>(null, ReplicationReadOutcome.NeedsVersion, "Not on this server: " + PostgresFeatures.Needs(PostgresFeature.PgStatIo) + ".")
                    : ReplicationSectionView<IoStatistics>.Without(read.Io),
            Tablespaces = read.Tablespaces.Value is { } tablespaces
                ? ReplicationSectionView<TablespaceList>.Of(tablespaces)
                : ReplicationSectionView<TablespaceList>.Without(read.Tablespaces),
            Statements = read.Statements,
        };
    }

    private static SlotsView Slots(ReplicationSlotSnapshot slots, SlotStatisticsList? statistics, ReplicationSettings settings, PostgresServerFacts server, bool names)
    {
        string idle = !PostgresFeatures.Has(PostgresFeature.IdleSlotTimeout, server.VersionNumber)
            ? PostgresFeatures.Needs(PostgresFeature.IdleSlotTimeout)
            : settings.IdleSlotTimeout switch
            {
                null => "not readable",
                { TotalSeconds: <= 0 } => "off (0, the default)",
                { } timeout => SessionText.Duration(timeout),
            };

        return new SlotsView(
            slots.Total,
            slots.MaxSlotWalKeepBytes,
            slots.WalKeepBytes,
            idle,
            statistics?.HasStatistics == true,
            [
                .. slots.Slots.Select(slot => new SlotView(
                    Describe(slot, names),
                    slot with { Name = string.Empty, Plugin = null, Database = null },
                    // Joined by name and then blanked, as the slot's own name is: the description is what may be shown (B1, 12).
                    statistics?.For(slot.Name) is { } row ? row with { SlotName = string.Empty } : null)),
            ]);
    }

    /// <summary>A slot as this visitor may be told it: R01's wording through the gate, its kind otherwise.</summary>
    internal static string Describe(ReplicationSlot slot, bool names)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (!slot.ThisDatabase)
        {
            return slot.Describe();
        }

        if (names)
        {
            return slot.Describe();
        }

        return (string.Equals(slot.SlotType, "logical", StringComparison.Ordinal) ? "a logical slot of this database" : "a physical slot")
            + (slot.Temporary ? ", temporary" : string.Empty);
    }

    private static StandbysView Standbys(StandbySnapshot standbys, bool names, PostgresPlatform platform, string readsAs, bool downstream)
    {
        string? notChecked = standbys.Unreadable > 0
            ? PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, platform, readsAs)
              + " Without it, pg_stat_replication shows a sender's role and application name and none of its positions or lag. "
              + "A data source that reaches a standby itself - a multi-host data source - measures the lag there, with no grant."
            : null;

        return new StandbysView(
            standbys.Total,
            standbys.Unreadable,
            notChecked,
            [
                .. standbys.Rows.Select(row => new StandbyView(
                    names ? row.ApplicationName : null,
                    names ? row.ClientAddress : null,
                    names ? row.User : null,
                    row with { ApplicationName = null, ClientAddress = null, User = null })),
            ],
            standbys.OtherDatabases,
            downstream);
    }

    private static ArchiverView Archiver(
        ArchiverFacts archiver,
        ReplicationRead<WalDirectoryFacts> directory,
        bool names,
        string? withheld,
        PostgresPlatform platform,
        string readsAs)
    {
        string? text = archiver.Settings.ArchiveCommandText;

        // The command's text leaves the facts only through the gate: the settings the view carries have it blanked.
        Dictionary<string, ReplicationSetting> blanked = new(archiver.Settings.Settings, StringComparer.Ordinal);
        if (text is not null)
        {
            blanked["archive_command"] = blanked["archive_command"] with { Value = "(withheld)" };
        }

        return new ArchiverView(
            archiver with { Settings = new ReplicationSettings(blanked) },
            names ? text : null,
            text is not null && !names ? withheld : null,
            directory.Value,
            directory is { Outcome: ReplicationReadOutcome.Read, Value: null }
                ? PostgresGrants.NotChecked(PostgresGrant.Monitor, platform, readsAs)
                : directory.Outcome == ReplicationReadOutcome.Failed ? directory.Note : null);
    }

    private static WalView Wal(WalStatistics wal, HealthHistoryView samples, DateTimeOffset now)
    {
        CounterWindow? window = ReplicationHistory.Window(
            samples,
            [(ReplicationHistory.WalBytes, wal.Bytes), (ReplicationHistory.WalRecords, wal.Records), (ReplicationHistory.WalFullPageImages, wal.FullPageImages)],
            now);

        if (window is null || window.Span <= TimeSpan.Zero)
        {
            return new WalView(wal, null, null, null);
        }

        double records = window.Gains[1];

        return new WalView(
            wal,
            window,
            window.Gains[0] / window.Span.TotalSeconds,
            records > 0 ? Math.Min(1d, window.Gains[2] / records) : null);
    }

    private static CheckpointsView Checkpoints(CheckpointStatistics checkpoints, ReplicationSettings settings, HealthHistoryView samples, DateTimeOffset now)
    {
        CounterWindow? window = ReplicationHistory.Window(samples, RequestedCheckpointsCheck.Counters(checkpoints), now);

        return new CheckpointsView(
            checkpoints,
            settings.CheckpointTimeout,
            settings.MaxWalSizeBytes,
            window,
            window is null
                ? null
                : RequestedCheckpointsCheck.Judge(
                    checkpoints.Counting,
                    window.Gains[0],
                    window.Gains[1],
                    window.Gains.Count > 2 ? window.Gains[2] : null,
                    window.Span,
                    settings.CheckpointTimeout));
    }

    /// <summary>Whether a view carries any name the activity gate guards.</summary>
    private static bool ShowsNames(ReplicationView view) =>
        view.Standbys.Value?.Rows.Count > 0
        || view.Slots.Value?.Rows.Any(static x => x.Slot.ThisDatabase) == true
        || view.Archiver.Value?.CommandText is not null;

    private async Task<(ResolvedSource? Resolved, ReplicationView? Refused)> ResolveAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        StudioScope asked = scope with { TenantId = null };
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return (null, ReplicationView.Refused(ReplicationRefusal.StorePolicy, StorePolicyDenial()));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return (null, ReplicationView.Refused(ReplicationRefusal.Unavailable, exception.Message));
        }

        StudioScope whole = Whole(resolved);

        bool samePlace = string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal);

        if (!samePlace && !await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return (null, ReplicationView.Refused(ReplicationRefusal.StorePolicy, StorePolicyDenial()));
        }

        return (resolved, null);
    }

    /// <summary>The activity gate, in the database browser's order: the capability, then the write policy - tenant-less.</summary>
    private async Task<(bool Names, string? Withheld)> NamesAsync(StudioScope whole, CancellationToken cancellationToken)
    {
        StudioText text = settings.Text;
        string capability = text.OptionName("Capabilities.ViewDatabaseActivity");

        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            return (false, capabilities.ReadOnly
                ? "Names need " + capability + ", and " + settings.ReadOnlyOption() + " is true, which turns every capability off."
                : "Standbys' application names and client addresses, slot names and the archive command need " + capability + ".");
        }

        return await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false)
            ? (true, null)
            : (false, "Your account's write policy (" + text.OptionName("WriteAuthorizationPolicy") + ") did not allow ViewDatabaseActivity for this database, so no standby or slot is named.");
    }

    private string StorePolicyDenial()
    {
        StudioText text = settings.Text;
        string policy = text.Product == StudioProduct.PostgresStudio ? text.OptionName("SourceAuthorizationPolicy") : text.OptionName("StoreAuthorizationPolicy");

        return "Your account may not see this database's replication. The policy (" + policy + ") refused this "
            + text.SourceNoun + " and database as a whole, with no tenant selected: the server's WAL and its standbys belong to no tenant.";
    }

    private static StudioScope Whole(ResolvedSource resolved) => new(resolved.Source.Key, resolved.Target.Identity, null);

    private void RecordFirstRead(ResolvedSource resolved, StudioScope whole)
    {
        if (!ledger.TryRecord(TableRowAuditLedger.Key(resolved.Source.Key, resolved.Target.Identity, LedgerSchema, LedgerRelation, LedgerKind), Signature, out _))
        {
            return;
        }

        audit.Record(NamesAction, NamesTarget, succeeded: true, "Replication names shown.", StudioCapability.ViewDatabaseActivity, whole);
    }

    /// <summary>
    /// The sentence for facts that could not be read at all - the server's version and platform, which every section is gated
    /// by - and the log line: Debug for an expected state, event 9430 at the throttle's level otherwise.
    /// </summary>
    private string Unreachable(Exception exception, ResolvedSource resolved)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        bool expected = postgres?.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable
            || PostgresFailure.IsTimeout(exception);

        if (expected)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} did not read the replication facts of {DatabaseId}", settings.Text.ProductName, databaseId);
            }
        }
        else
        {
            LogLevel level = throttle.WarningOrDebug("Replication.Read", storeKey, databaseId, StudioLogThrottle.KindOf(exception));

            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioReplicationReadFailed(level, exception, "server", storeKey, databaseId, postgres?.SqlState);
            }
            else
            {
                logger.ReplicationReadFailed(level, exception, "server", storeKey, databaseId, postgres?.SqlState);
            }
        }

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "The server's version and platform could not be read" + (postgres is null ? " (" + exception.GetType().Name + ")." : " (SQLSTATE " + postgres.SqlState + ").");
    }
}
