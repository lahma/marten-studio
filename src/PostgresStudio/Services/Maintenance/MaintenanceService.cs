using System.Collections.Concurrent;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace PostgresStudio.Services.Maintenance;

/// <summary>What the Maintenance page reads, and the one thing it does: measure one table's bloat.</summary>
/// <remarks>An interface so a page can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IMaintenanceService
{
    /// <summary>The visitor's view of the scope's database. Never throws but for cancellation; a refusal is a value.</summary>
    /// <param name="scope">The store and database; a tenant in it is ignored - maintenance belongs to no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared sweep.</param>
    Task<MaintenanceView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Measures one table's bloat with <c>pgstattuple</c> - on demand, one table at a time, one measure per database at once -
    /// and audits it. Never throws but for cancellation; a refusal is a value.
    /// </summary>
    /// <param name="scope">The store and database.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">Cancels the wait; a measure already running finishes or times out on its own.</param>
    Task<BloatMeasureResult> MeasureBloatAsync(StudioScope scope, string schema, string table, CancellationToken cancellationToken = default);
}

/// <summary>
/// The database browser's gate, as the Maintenance page asks it which schemas a visitor is shown by name (D27): a seam over
/// <see cref="DatabaseAccess.GateAsync" />, so a test can say what the gate answers (D12).
/// </summary>
internal interface IMaintenanceNameGate
{
    /// <summary>The gate for <paramref name="scope" />, as <see cref="DatabaseAccess.GateAsync" /> reads it.</summary>
    Task<DatabaseGateRead> GateAsync(StudioScope scope, ResolvedSource resolved, CancellationToken cancellationToken);
}

/// <summary>The gate a host's studio asks: <see cref="DatabaseAccess" />'s own.</summary>
internal sealed class DatabaseAccessNameGate(DatabaseAccess access) : IMaintenanceNameGate
{
    /// <inheritdoc />
    public Task<DatabaseGateRead> GateAsync(StudioScope scope, ResolvedSource resolved, CancellationToken cancellationToken) =>
        access.GateAsync(scope, resolved, cancellationToken);
}

/// <summary>One measure per database at once: the measure running now, by store and database.</summary>
/// <remarks>
/// A singleton, because a database is everybody's: two visitors pressing at once on one database get one measure - the same
/// one when it is the same table, a refusal when it is another.
/// </remarks>
internal sealed class BloatMeasureFlights
{
    private readonly ConcurrentDictionary<string, (string Table, Lazy<Task<BloatMeasureResult>> Work)> running = new(StringComparer.Ordinal);

    /// <summary>
    /// The measure of <paramref name="table" /> on <paramref name="database" />: the one running, when it is that table; a new
    /// one, when none is running; or <see langword="null" /> when another table's measure holds the database.
    /// </summary>
    public Task<BloatMeasureResult>? Join(string database, string table, Func<Task<BloatMeasureResult>> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);

        Lazy<Task<BloatMeasureResult>> created = new(() => RunAsync(database, measure), LazyThreadSafetyMode.ExecutionAndPublication);
        (string Table, Lazy<Task<BloatMeasureResult>> Work) entry = running.GetOrAdd(database, _ => (table, created));

        return string.Equals(entry.Table, table, StringComparison.Ordinal) ? entry.Work.Value : null;
    }

    private async Task<BloatMeasureResult> RunAsync(string database, Func<Task<BloatMeasureResult>> measure)
    {
        // Off the caller's stack: the measure is nobody's to cancel.
        await Task.Yield();

        try
        {
            return await measure().ConfigureAwait(false);
        }
        finally
        {
            running.TryRemove(database, out _);
        }
    }
}

/// <summary>
/// The Maintenance page's per-visitor half (W5-V1): the database's facts (<see cref="IMaintenanceFacts" />), shown through this
/// visitor's gates, and the bloat measure.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database-wide gate</b> (W5 §4: Database). The store policy for the database as a whole (<c>TenantId = null</c>),
/// asked by the resolver and again about the registered key and the database's identity (D29): a table's dead rows and
/// what autovacuum is doing belong to no tenant.
/// </para>
/// <para>
/// <b>Names per D27.</b> A hidden document type's table is not in the judged set at all. A table in a schema outside the
/// source's own is shown only past the database browser's gate for that schema (<see cref="DatabaseAccess.GateAsync" />),
/// and counted as not shown otherwise - also when the gate cannot be read. A running command on a table this page does not
/// judge is "a table this page does not name", a system catalog by its own name. Who holds the horizon is T14's finding,
/// read through the Health page's own gates (<see cref="IHealthService.CachedFindingsAsync" />): its names only behind the
/// activity gate, and audited as the Health page audits them.
/// </para>
/// <para>
/// <b>The bloat measure</b> is the database gate's too, for a table this visitor is shown, and is audited - success, failure
/// and refusal alike - as <see cref="MeasureAction" /> with the table and nothing else; under <c>BrowseDatabase</c> when the
/// table is in a schema only that gate shows, so the Activity screen shows the entry only to whoever may see that schema.
/// </para>
/// </remarks>
internal sealed class MaintenanceService : IMaintenanceService
{
    /// <summary>What the audit ring calls a bloat measure.</summary>
    internal const string MeasureAction = "Measure bloat";

    /// <summary>The most tables the page lists, worst first.</summary>
    internal const int MaxTables = 200;

    /// <summary>How many tables the forecast lists.</summary>
    internal const int FreezeTables = 5;

    private readonly IStudioSettings settings;
    private readonly StudioSourceResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly IMaintenanceFacts facts;
    private readonly IHealthService health;
    private readonly IMaintenanceNameGate? access;
    private readonly StudioActionLog audit;
    private readonly BloatMeasureFlights flights;
    private readonly ILogger<MaintenanceService> logger;
    private readonly TimeProvider time;

    public MaintenanceService(
        IStudioSettings settings,
        StudioSourceResolver resolver,
        StudioAuthorization authorization,
        IMaintenanceFacts facts,
        IHealthService health,
        StudioActionLog audit,
        BloatMeasureFlights flights,
        ILogger<MaintenanceService> logger,
        IMaintenanceNameGate? access = null,
        TimeProvider? time = null)
    {
        this.settings = settings;
        this.resolver = resolver;
        this.authorization = authorization;
        this.facts = facts;
        this.health = health;
        this.audit = audit;
        this.flights = flights;
        this.logger = logger;
        this.access = access;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>What a store-policy refusal of the page says, naming the option the studio's registration spells.</summary>
    internal string StorePolicyDenial =>
        "Your account may not see this database's maintenance. The "
        + (settings.Text.Product == StudioProduct.PostgresStudio
            ? "source policy (" + settings.Text.OptionName("SourceAuthorizationPolicy") + ")"
            : "store policy (" + settings.Text.OptionName("StoreAuthorizationPolicy") + ")")
        + " refused this " + settings.Text.SourceNoun + " and database as a whole, with no tenant selected: maintenance belongs to no tenant.";

    /// <inheritdoc />
    public async Task<MaintenanceView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        (ResolvedSource? whole, MaintenanceView? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);

        if (whole is null)
        {
            return refused!;
        }

        MaintenanceFacts read;

        try
        {
            read = await facts.ReadAsync(whole, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return MaintenanceView.Refused(MaintenanceRefusal.Unavailable, "The database's maintenance facts could not be read: " + exception.GetType().Name + ".");
        }

        Func<IReadOnlyList<string>, Task<bool>> shown = await NamesAsync(whole, read, cancellationToken).ConfigureAwait(false);

        List<MaintenanceTableView> tables = [];
        int withheld = 0;

        foreach (TableFacts table in read.Tables ?? [])
        {
            if (!await shown(table.Table.GatedSchemas).ConfigureAwait(false))
            {
                withheld++;
                continue;
            }

            tables.Add(ViewOf(table, read.MeasuredAt));
        }

        List<MaintenanceTableView> ordered =
        [
            .. tables
                .OrderByDescending(static x => x.Behind)
                .ThenByDescending(static x => x.Off is not null)
                .ThenByDescending(static x => x.Due)
                .ThenByDescending(static x => x.Pressure)
                .ThenBy(static x => x.Key, StringComparer.Ordinal),
        ];

        return new MaintenanceView
        {
            MeasuredAt = read.MeasuredAt,
            TablesRead = read.Tables is not null,
            Tables = [.. ordered.Take(MaxTables)],
            TablesCut = Math.Max(0, ordered.Count - MaxTables),
            TablesWithheld = withheld,
            Coverage = read.Coverage,
            Running = await RunningAsync(read, shown).ConfigureAwait(false),
            Workers = WorkersOf(read),
            Freeze = await FreezeAsync(read, shown).ConfigureAwait(false),
            Horizon = await HorizonAsync(scope, read, cancellationToken).ConfigureAwait(false),
            ServerVersion = read.Tables is { Count: > 0 } any ? any[0].Row.ServerVersion : read.Wraparound?.Database.ServerVersion ?? 0,
        };
    }

    /// <inheritdoc />
    public async Task<BloatMeasureResult> MeasureBloatAsync(StudioScope scope, string schema, string table, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        string key = schema + "." + table;
        DateTimeOffset now = time.GetUtcNow();
        StudioScope asked = scope with { TenantId = null };

        (ResolvedSource? whole, MaintenanceView? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);

        if (whole is null)
        {
            audit.RecordScopeDenied(asked, DatabaseAccess.StorePolicyName(settings), MeasureAction, key);
            return new BloatMeasureResult(key, null, refused!.Reason, now);
        }

        // Only a table this visitor is shown: one the judged set holds - never a hidden type's, which it does not - and,
        // outside the source's own schemas, one past the database browser's gate.
        MaintenanceFacts read = await facts.ReadAsync(whole, cancellationToken).ConfigureAwait(false);
        TableFacts? found = read.Tables?.FirstOrDefault(x =>
            string.Equals(x.Table.Schema, schema, StringComparison.Ordinal) && string.Equals(x.Table.Table, table, StringComparison.Ordinal));

        Func<IReadOnlyList<string>, Task<bool>> shown = await NamesAsync(whole, read, cancellationToken).ConfigureAwait(false);

        if (found is null || !await shown(found.Table.GatedSchemas).ConfigureAwait(false))
        {
            // The name is the visitor's own, and may be one in a schema others are not shown: the entry is BrowseDatabase's, so
            // the Activity screen shows it only to whoever passes the database browser's gate.
            return Refuse(whole, key, StudioCapability.BrowseDatabase, "That table is not one this page judges, or not one it shows you.", now);
        }

        StudioCapability? capability = found.Table.GatedSchemas.Count > 0 ? StudioCapability.BrowseDatabase : null;

        if (found.Row.IsPartitioned)
        {
            return Refuse(whole, key, capability, "A partitioned table holds no rows of its own: its partitions are measured one at a time, from the "
                + "database browser's SQL console with pgstattuple_approx.", now);
        }

        Task<BloatMeasureResult>? work = flights.Join(
            whole.Source.Key + "|" + whole.Target.Identity,
            key,
            () => MeasureAsync(whole, found, key, capability));

        if (work is null)
        {
            return Refuse(whole, key, capability, "Another table's bloat is being measured on this database; one measure runs at a time.", now);
        }

        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The scope resolved with no tenant and the store policy asked about it by its own names, or the refusal.</summary>
    private async Task<(ResolvedSource? Whole, MaintenanceView? Refused)> ResolveAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        StudioScope asked = scope with { TenantId = null };
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return (null, MaintenanceView.Refused(MaintenanceRefusal.StorePolicy, StorePolicyDenial));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return (null, MaintenanceView.Refused(MaintenanceRefusal.Unavailable, exception.Message));
        }

        // The store and database the scope resolved to, by the registered key and the identity (D29); a copy of the resolver's
        // own record, never one rebuilt from its parts.
        StudioScope whole = new(resolved.Source.Key, resolved.Target.Identity, null);

        bool same = string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal);

        if (!same && !await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return (null, MaintenanceView.Refused(MaintenanceRefusal.StorePolicy, StorePolicyDenial));
        }

        return (resolved with { Scope = whole }, null);
    }

    /// <summary>
    /// Whether this visitor is shown a table in <c>schemas</c> - the schemas outside the source's own it is in: none is every
    /// visitor's; any other needs the database browser's gate, read at most once per view, and is withheld when it cannot be.
    /// </summary>
    private async Task<Func<IReadOnlyList<string>, Task<bool>>> NamesAsync(ResolvedSource whole, MaintenanceFacts read, CancellationToken cancellationToken)
    {
        bool anyGated = (read.Tables ?? []).Any(static x => x.Table.GatedSchemas.Count > 0);
        DatabaseGateRead? gate = null;
        bool gateRead = false;

        return async schemas =>
        {
            if (schemas.Count == 0)
            {
                return true;
            }

            if (!anyGated || access is null)
            {
                return false;
            }

            if (!gateRead)
            {
                gateRead = true;

                try
                {
                    gate = await access.GateAsync(whole.Scope, whole, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(exception, "{Product} could not read the database browser's gate for the Maintenance page", settings.Text.ProductName);
                    }

                    gate = null;
                }
            }

            return gate is { Succeeded: true } passed && !schemas.Any(passed.Gate!.IsWithheld);
        };
    }

    /// <summary>One table as the page draws it. Pure.</summary>
    internal static MaintenanceTableView ViewOf(TableFacts table, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(table);

        HealthTableRow row = table.Row;

        bool behind = VacuumBehindCheck.IsBehind(table, VacuumBehindKind.DeadRows, at)
            || VacuumBehindCheck.IsBehind(table, VacuumBehindKind.Inserts, at)
            || StaleStatisticsCheck.ProblemOf(table, at) is StatisticsProblem.Stale or StatisticsProblem.NeverAnalysed;

        string? off = !row.AutovacuumEnabled ? "autovacuum = off on the server"
            : !row.TrackCounts ? "track_counts = off on the server"
            : table.AutovacuumOffForTable ? (row.IsPartitioned ? "switched off for every partition" : "switched off for the table")
            : row.AutovacuumOffLeaves > 0 ? "switched off for " + SessionText.Count(row.AutovacuumOffLeaves, "partition")
            : row.ToastAutovacuumOffLeaves > 0 ? (row.IsPartitioned ? "switched off for some partitions' TOAST tables" : "switched off for its TOAST table")
            : null;

        double Ratio(AutovacuumUnit? unit) => unit is { } u ? u.Counter / Math.Max(u.Trigger, 1) : 0;

        return new MaintenanceTableView
        {
            Key = table.Key,
            Schema = table.Table.Schema,
            Table = table.Table.Table,
            Description = table.Table.Describe(),
            Partitions = row.PartitionLeaves,
            Vacuum = row.Vacuum,
            Insert = row.Insert,
            Analyze = row.Analyze,
            VacuumMaxThreshold = row.VacuumMaxThreshold,
            UnfrozenShare = row.UnfrozenShare,
            LastVacuum = row.LastVacuum,
            LastAutovacuum = row.LastAutovacuum,
            LastAnalyze = row.LastAnalyze,
            LastAutoanalyze = row.LastAutoanalyze,
            Overrides =
            [
                .. row.StorageOptions.Where(static x => x.StartsWith("autovacuum_", StringComparison.Ordinal)),
                .. row.ToastStorageOptions.Where(static x => x.StartsWith("autovacuum_", StringComparison.Ordinal)).Select(static x => "toast." + x),
            ],
            Due = row.Vacuum?.Due == true || row.Insert?.Due == true || row.Analyze?.Due == true,
            Behind = behind,
            Off = off,
            TotalVacuumMilliseconds = row.TotalVacuumMilliseconds,
            TotalAnalyzeMilliseconds = row.TotalAnalyzeMilliseconds,
            Vacuums = row.VacuumCount is null && row.AutovacuumCount is null ? null : (row.VacuumCount ?? 0) + (row.AutovacuumCount ?? 0),
            Analyzes = row.AnalyzeCount is null && row.AutoanalyzeCount is null ? null : (row.AnalyzeCount ?? 0) + (row.AutoanalyzeCount ?? 0),
            Measurable = !row.IsPartitioned,
            Pressure = Math.Max(Ratio(row.Vacuum), Math.Max(Ratio(row.Insert), Ratio(row.Analyze))),
        };
    }

    /// <summary>What is running now, as this visitor may see it.</summary>
    private static async Task<MaintenanceRunningView> RunningAsync(MaintenanceFacts read, Func<IReadOnlyList<string>, Task<bool>> shown)
    {
        if (read.Running is not { } running)
        {
            return MaintenanceRunningView.NotRead;
        }

        Dictionary<string, TableFacts> judged = (read.Tables ?? []).ToDictionary(static x => x.Key, StringComparer.Ordinal);
        List<MaintenanceRunningRow> rows = [];
        int unseen = 0;
        int unnamed = 0;

        foreach (MaintenanceProgressRow row in running)
        {
            if (row.RelationId is null)
            {
                unseen++;
                continue;
            }

            string? label = row.TableKey is { } key && judged.TryGetValue(key, out TableFacts? table) && await shown(table.Table.GatedSchemas).ConfigureAwait(false)
                ? key + row.Part switch { "toast" => " (its TOAST table)", "partition" => " (one of its partitions)", _ => string.Empty }
                : row.SystemCatalog && row.TableKey is { } catalog ? "the system catalog " + catalog
                : null;

            // A table this page does not name - withheld, hidden, or not one it judges - is counted and nothing more: its
            // blocks, its phase and an ETA would give away how big it is (W5-V1 review).
            if (label is null)
            {
                unnamed++;
                continue;
            }

            rows.Add(new MaintenanceRunningRow(
                row.Kind,
                label,
                row.Phase,
                row.BlocksDone,
                row.BlocksTotal,
                row.IndexesProcessed,
                row.IndexesTotal,
                row.Autovacuum switch
                {
                    true when row.PreventsWraparound == true => "autovacuum, to prevent wraparound",
                    true => "autovacuum",
                    false => "manual",
                    _ => null,
                },
                row.Running,
                Eta(row, read.History)));
        }

        string? notChecked = unseen > 0 && read.Platform is { } platform && read.Grants is { } grants
            ? PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, platform, grants.ReadsAs)
            : unseen > 0 ? "Not checked: this needs pg_read_all_stats (pg_monitor includes it) for the role the studio reads as." : null;

        return new MaintenanceRunningView(true, rows, unseen, notChecked) { Unnamed = unnamed };
    }

    /// <summary>
    /// How long until a command's phase is through, at the rate its blocks went between the samples T16 took of it - or
    /// <see langword="null" /> before two samples, or when it did not move.
    /// </summary>
    internal static TimeSpan? Eta(MaintenanceProgressRow row, HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(history);

        if (AutovacuumWorkersCheck.ProgressSeriesOf(row) is not { } series
            || row.BlocksTotal is not { } total
            || row.BlocksDone is not { } done
            || total <= done
            || TableHistory.Delta(history.Trend(series)) is not { } delta
            || delta.Change <= 0
            || delta.Span <= TimeSpan.Zero)
        {
            return null;
        }

        double perSecond = delta.Change / delta.Span.TotalSeconds;
        return TimeSpan.FromSeconds((total - done) / perSecond);
    }

    private static MaintenanceWorkersView? WorkersOf(MaintenanceFacts read)
    {
        if (read.Workers is not { } workers)
        {
            return null;
        }

        AutovacuumWorkersFacts facts = new(read.MeasuredAt, workers, read.Running ?? [], read.Platform ?? PostgresPlatform.SelfHosted);

        return new MaintenanceWorkersView(
            workers.MaxWorkers,
            workers.WorkerSlots,
            AutovacuumWorkersCheck.BusyText(facts),
            workers.NaptimeSeconds,
            workers.Autovacuum,
            facts.Saturated);
    }

    /// <summary>The freeze forecast from T12's and T13's own facts and their samples - no read of its own.</summary>
    private static async Task<MaintenanceFreezeView?> FreezeAsync(MaintenanceFacts read, Func<IReadOnlyList<string>, Task<bool>> shown)
    {
        if (read.Wraparound is not { } wraparound)
        {
            return null;
        }

        HealthWraparoundDatabase database = wraparound.Database;
        long refusal = Wraparound.RefusalAge(database.ServerVersion);

        long xidAge = TransactionIdAgeCheck.OldestAge(new TransactionIdAgeFacts(read.Tables ?? [], wraparound));
        long multixactAge = Math.Max(
            wraparound.Tables.Count == 0 ? 0 : wraparound.Tables.Max(static x => x.MultixactAge ?? 0),
            Math.Max(Math.Max(database.DatabaseMultixactAge, database.OtherOldestMultixactAge ?? 0), database.ServerMultixactAge));

        Dictionary<string, TableFacts> judged = (read.Tables ?? []).ToDictionary(static x => x.Key, StringComparer.Ordinal);
        List<MaintenanceFreezeTable> oldest = [];

        foreach (HealthWraparoundTable table in wraparound.Tables.Where(static x => x.XidAge is not null).OrderByDescending(static x => x.XidAge))
        {
            if (oldest.Count == FreezeTables)
            {
                break;
            }

            // Only a table this visitor is shown by name; the others are counted in the ages above and never named.
            if (judged.TryGetValue(table.Key, out TableFacts? facts) && await shown(facts.Table.GatedSchemas).ConfigureAwait(false))
            {
                oldest.Add(new MaintenanceFreezeTable(table.Key, table.XidAge!.Value));
            }
        }

        return new MaintenanceFreezeView(
            Forecast("transaction ids", xidAge, database.FreezeMaxAge, refusal, TransactionIdAgeCheck.Forecast(read.History)),
            Forecast("multixact ids", multixactAge, database.MultixactFreezeMaxAge, refusal, MultixactAgeCheck.Forecast(read.History)),
            oldest);

        static MaintenanceForecast Forecast(string noun, long age, long forced, long refused, HorizonForecast? rate) =>
            new(noun, age, forced, refused, rate?.Until(age, forced), rate?.Until(age, refused), rate?.RateText("ids"));
    }

    /// <summary>T14's finding through the Health page's own gates, or its note.</summary>
    private async Task<MaintenanceHorizonView> HorizonAsync(StudioScope scope, MaintenanceFacts read, CancellationToken cancellationToken)
    {
        if (read.Horizon is not { } result)
        {
            return new MaintenanceHorizonView(null, "T14 has not run in this sweep, so who holds the horizon is not known.");
        }

        if (result.Outcome != HealthCheckOutcome.Checked)
        {
            return new MaintenanceHorizonView(null, "T14 did not check in this sweep: " + (result.Problem?.Reason ?? result.Outcome.ToString()) + ".");
        }

        if (result.Findings.Count == 0)
        {
            return new MaintenanceHorizonView(null, result.Note);
        }

        IReadOnlyList<HealthReportFinding> shownFindings = await health.CachedFindingsAsync(scope with { TenantId = null }, ["T14"], cancellationToken).ConfigureAwait(false);

        return shownFindings.Count > 0
            ? new MaintenanceHorizonView(shownFindings[0], null)
            : new MaintenanceHorizonView(null, "T14's finding is muted on the Health page, so it is not shown here either.");
    }

    /// <summary>Runs the measure: the preconditions, then pgstattuple, in a read-only transaction as the console's role.</summary>
    private async Task<BloatMeasureResult> MeasureAsync(ResolvedSource whole, TableFacts table, string key, StudioCapability? capability)
    {
        DateTimeOffset now = time.GetUtcNow();
        ReadOnlySqlSession session = new(new ReadOnlySqlOptions
        {
            StatementTimeout = settings.QueryTimeout,
            Role = settings.SqlConsoleRole,
        });

        int commandTimeout = (int) Math.Ceiling(settings.QueryTimeout.TotalSeconds) + 5;

        try
        {
            // Who runs the server, for the wording of a refusal - from the sweep's facts, read before any transaction is open.
            PostgresPlatform platform = (await facts.ReadAsync(whole, CancellationToken.None).ConfigureAwait(false)).Platform ?? PostgresPlatform.SelfHosted;

            await using NpgsqlConnection connection = whole.Target.CreateConnection(StudioConnectionUsage.ReadWrite);
            await PostgresFailure.OpenAsync(connection, CancellationToken.None).ConfigureAwait(false);

            (BloatMeasureRow? measure, string? refusal) = await session.InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        BloatPreconditionRow pre = await MaintenanceQueries
                            .ReadBloatPreconditionsAsync(connection, transaction, table.Table.Schema, table.Table.Table, commandTimeout, token)
                            .ConfigureAwait(false);

                        if (pre.ExtensionSchema is not { } schema)
                        {
                            return ((BloatMeasureRow?) null, (string?) PostgresGrants.ExtensionNotInstalled(MaintenanceQueries.Extension, trusted: false, platform));
                        }

                        if (!pre.MayScan)
                        {
                            return (null, PostgresGrants.NotChecked(PostgresGrant.StatScanTables, platform, pre.ReadsAs));
                        }

                        if (pre.TableKind is not ("r" or "m"))
                        {
                            return (null, "That table is no longer there, or is not a table pgstattuple measures.");
                        }

                        BloatMeasureRow? measured = await MaintenanceQueries
                            .MeasureBloatAsync(
                                connection,
                                transaction,
                                schema,
                                table.Table.Schema,
                                table.Table.Table,
                                settings.QueryTimeout,
                                commandTimeout,
                                token,
                                MaintenanceQueries.HasApproximate(pre.ExtensionVersion))
                            .ConfigureAwait(false);

                        return measured is null ? (null, "That table is no longer there.") : (measured, null);
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (refusal is not null)
            {
                return Refuse(whole, key, capability, refusal, now);
            }

            audit.Record(MeasureAction, key, succeeded: true,
                "Measured: " + TableText.Percent(measure!.DeadPercent / 100) + " dead tuples, " + TableText.Percent(measure.FreePercent / 100) + " free space, "
                + SessionText.Number(measure.Indexes.Count) + (measure.Indexes.Count == 1 ? " index" : " indexes")
                + (measure.IndexesNotMeasured > 0 ? " (" + SessionText.Number(measure.IndexesNotMeasured) + " more not measured)" : string.Empty) + ".",
                capability, whole.Scope);

            return new BloatMeasureResult(key, measure, null, now);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.InsufficientPrivilege)
        {
            string why = exception.SqlState switch
            {
                PostgresErrorCodes.QueryCanceled => "It ran past its statement_timeout (" + settings.Text.OptionName("QueryTimeout") + ", "
                    + SessionText.Duration(settings.QueryTimeout) + "): measure a smaller table, or raise the timeout.",
                PostgresErrorCodes.LockNotAvailable => "The table is locked for a schema change right now, so the measure would queue behind it; try again later.",
                _ => "Postgres refused the role a read the measure needs (SQLSTATE 42501).",
            };

            return Refuse(whole, key, capability, why, now);
        }
        catch (Exception exception)
        {
            logger.BloatMeasureFailed(settings.Text.Product, exception, whole.Source.Key, whole.Target.Identity, (exception as PostgresException)?.SqlState);
            audit.Record(MeasureAction, key, succeeded: false, "The measure failed: " + exception.GetType().Name + ".", capability, whole.Scope);

            return new BloatMeasureResult(key, null, "The measure failed (" + exception.GetType().Name + "); the server log has the details.", now);
        }
    }

    /// <summary>A refusal: audited and logged at Debug, and returned in words.</summary>
    private BloatMeasureResult Refuse(ResolvedSource whole, string key, StudioCapability? capability, string reason, DateTimeOffset now)
    {
        audit.Record(MeasureAction, key, succeeded: false, reason, capability, whole.Scope);
        logger.BloatMeasureRefused(settings.Text.Product, whole.Source.Key, whole.Target.Identity, reason);
        return new BloatMeasureResult(key, null, reason, now);
    }
}
