using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace PostgresStudio.Services.Storage;

/// <summary>One of the largest tables, as one visitor may see it.</summary>
/// <param name="Schema">The schema, or <see langword="null" /> when the table sits in a schema the visitor may not see (D27).</param>
/// <param name="Name">The table, or <see langword="null" /> with <paramref name="Schema" />: a withheld table is never half-named.</param>
/// <param name="Link">The database browser's page for it, which measures it exactly; <see langword="null" /> when withheld.</param>
/// <param name="Ownership">Whose design it is (issue #8 G1); <see langword="null" /> when withheld, which a library's name would give away.</param>
/// <param name="Owner">The library that owns it, for a library's table; <see langword="null" /> otherwise and when withheld.</param>
/// <param name="TotalBytes">Heap, indexes and TOAST, from <c>relpages</c>.</param>
/// <param name="HeapBytes">The heap.</param>
/// <param name="IndexBytes">Its indexes.</param>
/// <param name="ToastBytes">Its TOAST table and that table's index.</param>
/// <param name="EstimatedRows"><c>reltuples</c>; <c>-1</c> is "never analysed".</param>
/// <param name="ShareOfDatabase">Its share of the database, 0 to 1; <see langword="null" /> when the database's size is not known.</param>
/// <param name="HotUpdateRatio">The share of updates that were HOT, 0 to 1; <see langword="null" /> with no updates.</param>
/// <param name="NewPageShare">The share of updates that went to another page (16+); <see langword="null" /> below 16 or with none.</param>
/// <param name="GrowthBytesPerDay">Its growth a day, until the window has counted for a day <see langword="null" />.</param>
internal sealed record StorageTableView(
    string? Schema,
    string? Name,
    string? Link,
    StudioTableOwnership? Ownership,
    string? Owner,
    long TotalBytes,
    long HeapBytes,
    long IndexBytes,
    long ToastBytes,
    long EstimatedRows,
    double? ShareOfDatabase,
    double? HotUpdateRatio,
    double? NewPageShare,
    double? GrowthBytesPerDay)
{
    /// <summary>Whether its name is withheld from this visitor.</summary>
    public bool Withheld => Name is null;
}

/// <summary>What the Storage page draws for one visitor: the database's figures, and the largest tables as the visitor may see them.</summary>
internal sealed record StorageView
{
    /// <summary>Why nothing is shown, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal.</summary>
    public string? Reason { get; init; }

    /// <summary>When the facts were read.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>The database's measured size, or <see langword="null" />.</summary>
    public long? DatabaseBytes { get; init; }

    /// <summary>Why it was not measured, naming the grant, or <see langword="null" />.</summary>
    public string? DatabaseBytesNotRead { get; init; }

    /// <summary>The database's size by the catalog's own estimate.</summary>
    public long EstimatedDatabaseBytes { get; init; }

    /// <summary>The database's growth a day, or <see langword="null" /> until a day of statistics, and while they look reset.</summary>
    public double? GrowthBytesPerDay { get; init; }

    /// <summary>A month at the current daily rate: thirty days of it (issue #8 G3).</summary>
    public double? GrowthBytesPerMonth => GrowthBytesPerDay * 30;

    /// <summary>What the growth tiles say while there is no growth to show - "needs a day of statistics: 5 h so far".</summary>
    public string? GrowthPending { get; init; }

    /// <summary>The statistics window the growth is measured over, or <see langword="null" /> when it could not be read.</summary>
    public StatisticsWindow? Window { get; init; }

    /// <summary>Why <see cref="Window" /> is missing, or <see langword="null" />.</summary>
    public string? WindowNotRead { get; init; }

    /// <summary>How many tables the source judges.</summary>
    public int TableCount { get; init; }

    /// <summary>What the set leaves out, or <see langword="null" />.</summary>
    public string? Coverage { get; init; }

    /// <summary>Why no table was read, or <see langword="null" />.</summary>
    public string? TablesNotRead { get; init; }

    /// <summary>The largest tables, largest first, as this visitor may see them.</summary>
    public IReadOnlyList<StorageTableView> Tables { get; init; } = [];

    /// <summary>Why some of <see cref="Tables" /> are shown without their names, or <see langword="null" /> when none is.</summary>
    public string? WithheldReason { get; init; }

    /// <summary>Whether the server reports updates that went to another page (16+), for the column's note.</summary>
    public bool NewPageUpdatesRead { get; init; }

    /// <summary>
    /// Operations › Maintenance for this scope (W5-V1), where autovacuum's view of the same tables is - or <see langword="null" />
    /// when the studio offers no such page (<see cref="MaintenanceLinks.ToMaintenance" />).
    /// </summary>
    public string? MaintenanceLink { get; init; }

    /// <summary>What the read ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>A refusal: nothing but the sentence.</summary>
    public static StorageView Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>What the Storage page reads: one database's storage, as one visitor may see it.</summary>
/// <remarks>An interface so a page can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IStorageService
{
    /// <summary>
    /// The storage of the scope's database, for anybody the store policy passes for it as a whole. Never throws but for
    /// cancellation; a refusal or a failed read is a value.
    /// </summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped, because a table's size belongs to no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    Task<StorageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Storage page's per-visitor half (W5-CAP): the shared facts (<see cref="IStorageFacts" />), shown through this visitor's
/// gates.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database-wide gate.</b> Sizes, rows, growth and the HOT ratio are numbers about the database as a whole, so the page
/// needs the store policy for it with <c>TenantId = null</c>, as the health advisor's database-wide findings do (D29): asked by
/// the resolver with the visitor's spelling of the scope, then about the store's registered key and the database's own
/// identity when either spelling differs.
/// </para>
/// <para>
/// <b>Names follow D27.</b> A table in the source's own schemas is structure, and named to everybody the gate admits. A table in
/// any other schema the source judges (one <c>BrowsableSchemas</c> admits) is named only to a visitor the database browser's own
/// gate shows that schema - read once per view, and only when such a table is among the largest - and otherwise is listed with
/// its sizes and without its name, its owner or its link: a structured field is blanked, never half-masked. A gate that cannot
/// be read withholds every such name. A hidden document type's table is absent: the table source leaves it out, and this drops
/// any that reached it anyway. The Tables tile - how many tables are judged, and what the set leaves out - counts tables in
/// those other schemas only for a visitor past the browser's gate, and the source's own schemas' alone for anyone else (H1b).
/// </para>
/// <para>
/// <b>Nothing is audited.</b> Sizes are the Schema screen's kind of structure, not another session's activity; the page reads
/// once interactive and every <c>HealthInterval</c>, through the facts' shared, single-flight read.
/// </para>
/// </remarks>
internal sealed class StorageService : IStorageService
{
    private readonly IStudioSettings settings;
    private readonly StudioDescriptor descriptor;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly IStorageFacts facts;
    private readonly DatabaseAccess? access;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<StorageService> logger;

    public StorageService(
        IStudioSettings settings,
        StudioDescriptor descriptor,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        IStorageFacts facts,
        StudioLogThrottle throttle,
        ILogger<StorageService> logger,
        DatabaseAccess? access = null)
    {
        this.settings = settings;
        this.descriptor = descriptor;
        this.authorization = authorization;
        this.resolver = resolver;
        this.facts = facts;
        this.throttle = throttle;
        this.logger = logger;
        this.access = access;
    }

    /// <inheritdoc />
    public async Task<StorageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
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
            return StorageView.Refused(DatabaseRefusal.StorePolicy, settings.Text.StorageStorePolicyDenial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return StorageView.Refused(DatabaseRefusal.Unavailable, exception.Message);
        }

        // From here on, the store and database the scope resolved to, by the registered key and the identity (D29).
        StudioScope whole = new(resolved.Source.Key, resolved.Target.Identity, null);

        bool samePlace = string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal);

        if (!samePlace && !await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return StorageView.Refused(DatabaseRefusal.StorePolicy, settings.Text.StorageStorePolicyDenial);
        }

        StorageFacts read;

        try
        {
            read = await facts.ReadAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return StorageView.Refused(DatabaseRefusal.Unavailable, Failure(exception, resolved));
        }

        return await ShowAsync(read, scope, () => GateAsync(whole, resolved, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="read" /> as one visitor may see it - pure but for <paramref name="browserGate" />, which is asked at most once,
    /// and only when a table outside the source's own schemas is among the largest, or the source judges one at all.
    /// </summary>
    /// <remarks>
    /// <b>The Tables tile per visitor</b> (G1M F6, H1b): how many tables the source judges, and what its set leaves out, count the
    /// tables in schemas only <c>BrowsableSchemas</c> admits - which is the database browser's to show. So a visitor past its gate
    /// (<see cref="DatabaseGate.IsOpen" />, the same rule the Health page's coverage follows) reads the whole count, and anyone
    /// else - a gate that is shut, or one that cannot be read - the count over the source's own schemas alone.
    /// </remarks>
    /// <param name="read">The shared facts.</param>
    /// <param name="scope">The visitor's scope, which the links carry.</param>
    /// <param name="browserGate">The database browser's gate for this visitor, or <see langword="null" /> when it cannot be read.</param>
    internal async Task<StorageView> ShowAsync(StorageFacts read, StudioScope scope, Func<Task<DatabaseGate?>> browserGate)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(browserGate);

        StatisticsWindow? window = read.Window;
        DatabaseGate? gate = null;
        bool gateRead = false;
        int withheld = 0;
        List<StorageTableView> tables = [];

        async Task<DatabaseGate?> GateOnceAsync()
        {
            if (!gateRead)
            {
                gate = await browserGate().ConfigureAwait(false);
                gateRead = true;
            }

            return gate;
        }

        foreach (StorageTableFacts table in read.LargestTables)
        {
            if (table.Table.DocumentTypes.Any(type => settings.IsDocumentTypeVisible?.Invoke(type) == false))
            {
                // Absent, as from every list (D27).
                continue;
            }

            bool named = true;

            if (table.Table.GatedSchemas.Count > 0)
            {
                DatabaseGate? shown = await GateOnceAsync().ConfigureAwait(false);
                named = shown is not null && !table.Table.GatedSchemas.Any(shown.IsWithheld);
            }

            if (!named)
            {
                withheld++;
            }

            long share = read.ShareBase;

            tables.Add(new StorageTableView(
                named ? table.Table.Schema : null,
                named ? table.Table.Table : null,
                named ? DatabaseLinks.ToObject(settings, descriptor, scope, table.Table.Schema, table.Table.Table) : null,
                named ? table.Table.Ownership : null,
                named ? table.Table.Library : null,
                table.TotalBytes,
                table.Row.HeapBytes,
                table.Row.IndexBytes,
                table.Row.ToastBytes,
                table.EstimatedRows,
                share > 0 ? (double) table.TotalBytes / share : null,
                table.HotUpdateRatio,
                table.NewPageShare,
                window is null ? null : table.GrowthBytesPerDay(window)));
        }

        string? withheldReason = withheld == 0 ? null : WithheldReason(withheld, gate is not null);

        // The whole count past the browser's gate, the own schemas' short of it or when it cannot be read (fail closed).
        bool whole = !read.HasOwnSchemasReading || await GateOnceAsync().ConfigureAwait(false) is { IsOpen: true };

        return new StorageView
        {
            MeasuredAt = read.MeasuredAt,
            DatabaseBytes = read.DatabaseBytes,
            DatabaseBytesNotRead = read.DatabaseBytesNotRead,
            EstimatedDatabaseBytes = read.EstimatedDatabaseBytes,
            GrowthBytesPerDay = read.GrowthBytesPerDay,
            GrowthPending = read.GrowthBytesPerDay is not null
                ? null
                : window is null
                    ? "not available: " + (read.WindowNotRead ?? "the statistics window was not read")
                    : GrowthPendingText(window),
            Window = window,
            WindowNotRead = read.WindowNotRead,
            TableCount = whole ? read.TableCount : read.OwnSchemasTableCount,
            Coverage = whole ? read.Coverage : read.OwnSchemasCoverage,
            TablesNotRead = read.TablesNotRead,
            Tables = tables,
            WithheldReason = withheldReason,
            NewPageUpdatesRead = read.NewPageUpdatesRead,
            MaintenanceLink = MaintenanceLinks.ToMaintenance(settings, descriptor, scope),
            Statements = read.Statements,
        };
    }

    /// <summary>
    /// Why <paramref name="count" /> of the largest tables are listed without their names - the database browser's gate, naming
    /// what it needs, or a gate that could not be read for this visitor.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <param name="gateRead">Whether the gate was read, and said no.</param>
    internal string WithheldReason(int count, bool gateRead)
    {
        string which = count == 1 ? "1 table is shown without its name: it sits" : SessionText.Number(count) + " tables are shown without their names: they sit";

        return gateRead
            ? which + " in a schema the database browser does not show you (it needs " + settings.CapabilityOption(StudioCapability.BrowseDatabase)
              + ", the write policy and a BrowsableSchemas entry for that schema)."
            : which + " in a schema outside " + settings.Text.SourcePossessive + " own, which could not be checked against the database "
              + "browser's gate for you.";
    }

    /// <summary>What the growth tiles say while there is no growth: why, and how long there is to go.</summary>
    internal static string GrowthPendingText(StatisticsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.LooksReset)
        {
            return "the statistics look reset since the last check (" + (window.Signal ?? window.ReasonText)
                + "): growth is estimated again once they have counted for a day";
        }

        return "needs a day of statistics: " + HoursSoFar(window.Length) + " so far";
    }

    /// <summary>"under an hour", "1 h", "17 h".</summary>
    private static string HoursSoFar(TimeSpan length) => length < TimeSpan.FromHours(1)
        ? "under an hour"
        : ((int) length.TotalHours).ToString(System.Globalization.CultureInfo.InvariantCulture) + " h";

    /// <summary>The database browser's gate for this visitor and the database as a whole, or <see langword="null" />.</summary>
    private async Task<DatabaseGate?> GateAsync(StudioScope whole, ResolvedSource resolved, CancellationToken cancellationToken)
    {
        if (access is null)
        {
            return null;
        }

        try
        {
            DatabaseGateRead read = await access.GateAsync(whole, resolved, cancellationToken).ConfigureAwait(false);
            return read.Succeeded ? read.Gate : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                if (settings.Text.Product == StudioProduct.PostgresStudio)
                {
                    logger.LogDebug(exception, "Postgres Studio could not read the schema gate to name the tables of {DatabaseId}", resolved.Target.Identity);
                }
                else
                {
                    logger.LogDebug(exception, "Marten Studio could not read the schema gate to name the tables of {DatabaseId}", resolved.Target.Identity);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The sentence for a read that failed - the studio's words and the SQLSTATE, never Postgres' own, which could name a schema
    /// this visitor may not see - and the log line: Debug for an expected state, a throttled Warning for anything else.
    /// </summary>
    private string Failure(Exception exception, ResolvedSource resolved)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        string? expected = postgres?.SqlState switch
        {
            PostgresErrorCodes.InsufficientPrivilege => "The studio's Postgres role may not read this database's catalog or statistics.",
            PostgresErrorCodes.QueryCanceled => "Reading the storage ran out of its statement_timeout; Refresh tries again.",
            PostgresErrorCodes.LockNotAvailable => "Reading the storage waited too long for a lock; Refresh tries again.",
            _ => PostgresFailure.IsTimeout(exception) ? "Reading the storage timed out; Refresh tries again." : null,
        };

        if (expected is not null)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                if (settings.Text.Product == StudioProduct.PostgresStudio)
                {
                    logger.LogDebug(exception, "Postgres Studio did not read the storage of {DatabaseId}: {Reason}", databaseId, expected);
                }
                else
                {
                    logger.LogDebug(exception, "Marten Studio did not read the storage of {DatabaseId}: {Reason}", databaseId, expected);
                }
            }

            return expected;
        }

        LogLevel level = throttle.WarningOrDebug("Storage.Read", storeKey, databaseId, StudioLogThrottle.KindOf(exception));
        if (settings.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioStorageReadFailed(level, exception, storeKey, databaseId, postgres?.SqlState);
        }
        else
        {
            logger.StorageReadFailed(level, exception, storeKey, databaseId, postgres?.SqlState);
        }

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "The storage could not be read" + (postgres is null
                ? " (" + exception.GetType().Name + ")."
                : " (SQLSTATE " + postgres.SqlState + ").");
    }
}
