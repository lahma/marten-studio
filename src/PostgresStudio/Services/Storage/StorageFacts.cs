using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;

namespace PostgresStudio.Services.Storage;

/// <summary>One judged table on the Storage page: what the catalog's estimates say of its size, and what its counters say of its use.</summary>
/// <param name="Table">The table, as the studio's table source describes it (issue #8 G1): whose it is, and which schemas gate it.</param>
/// <param name="Row">What <see cref="StorageQueries.Tables" /> read.</param>
internal sealed record StorageTableFacts(StudioTable Table, StorageTableRow Row)
{
    /// <summary>Heap, indexes and TOAST together, from <c>relpages</c>.</summary>
    public long TotalBytes => Row.HeapBytes + Row.IndexBytes + Row.ToastBytes;

    /// <summary><c>reltuples</c>; <c>-1</c> is "never analysed".</summary>
    public long EstimatedRows => Row.EstimatedRows;

    /// <summary>Whether the planner has never counted its rows (<c>reltuples = -1</c>).</summary>
    public bool NeverAnalysed => Row.EstimatedRows < 0;

    /// <summary>
    /// What one row costs on disk, its share of the indexes and TOAST included: <c>(heap + index + toast) / max(reltuples, 1)</c>
    /// (issue #8 G3, as written).
    /// </summary>
    public double BytesPerRow => (double) TotalBytes / Math.Max(Row.EstimatedRows, 1);

    /// <summary>The share of updates that were HOT, 0 to 1; <see langword="null" /> with no updates.</summary>
    public double? HotUpdateRatio => Row is { Updates: > 0 and { } updates, HotUpdates: { } hot } ? (double) hot / updates : null;

    /// <summary>
    /// The share of its heap's pages the visibility map marks all-visible, 0 to 1 (<c>relallvisible / relpages</c>, as of the
    /// last <c>VACUUM</c> or <c>ANALYZE</c>); <see langword="null" /> while the catalog counts no pages.
    /// </summary>
    public double? AllVisibleRatio => Row.AllVisibleRatio;

    /// <summary>
    /// The share of updates that went to another page (PostgreSQL 16+, <c>n_tup_newpage_upd</c>): B0's "why not HOT" - no room on
    /// the page. <see langword="null" /> below 16 or with no updates.
    /// </summary>
    public double? NewPageShare => Row is { Updates: > 0 and { } updates, NewPageUpdates: { } newPage } ? (double) newPage / updates : null;

    /// <summary>Rows inserted less rows deleted since the counters began; <see langword="null" /> when the collector has nothing.</summary>
    public long? NetInserts => Row.Inserts is null && Row.Deletes is null ? null : (Row.Inserts ?? 0) - (Row.Deletes ?? 0);

    /// <summary>
    /// Its growth a day over <paramref name="window" /> (issue #8 G3): <c>(n_tup_ins - n_tup_del) / window days × bytes per row</c>
    /// - <see langword="null" /> until the window has counted for a day, while it looks reset, or with no counters.
    /// </summary>
    public double? GrowthBytesPerDay(StatisticsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.CoversADay && NetInserts is { } net ? net / window.Days * BytesPerRow : null;
    }
}

/// <summary>
/// One database's storage, as the catalog's estimates and the statistics collector say it - gate-free and the same for every
/// visitor (W5-index §2.3): the facts half the Storage page and the public diagnostics API both read.
/// </summary>
/// <remarks>
/// Nothing here has been shown to anybody: a table's name outside the source's own schemas, or a hidden document type's, is
/// still in it. What a visitor may see of it is <c>StorageService</c>'s to decide.
/// </remarks>
internal sealed record StorageFacts
{
    /// <summary>How many of the largest tables the page and the API list (issue #8 G3: ten to fifteen; CAP takes fifteen).</summary>
    public const int LargestCount = 15;

    /// <summary>The source, by its registration key.</summary>
    public required string StoreKey { get; init; }

    /// <summary>The database, by its identity.</summary>
    public required string DatabaseId { get; init; }

    /// <summary>When the facts were read, by the studio's clock.</summary>
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary><c>pg_database_size(current_database())</c>, or <see langword="null" /> when the reading role may not ask it.</summary>
    public long? DatabaseBytes { get; init; }

    /// <summary>
    /// Why <see cref="DatabaseBytes" /> is missing, as a "not checked" sentence that names the grant (W5-index §2.6), or
    /// <see langword="null" /> when it was read.
    /// </summary>
    public string? DatabaseBytesNotRead { get; init; }

    /// <summary>
    /// Whether table sizes are the catalog's page counts, as of the last <c>VACUUM</c> or <c>ANALYZE</c>, rather than the files'.
    /// Always: a list locks nothing it lists (D27).
    /// </summary>
    public bool SizesAreEstimates { get; init; } = true;

    /// <summary>Every relation's <c>relpages</c> times the block size: the database's size by the same estimate as the tables'.</summary>
    public long EstimatedDatabaseBytes { get; init; }

    /// <summary>What a table's share is a share of: the measured size, or the estimate when it could not be measured.</summary>
    public long ShareBase => DatabaseBytes ?? EstimatedDatabaseBytes;

    /// <summary>
    /// The statistics window the growth figures are measured over, or <see langword="null" /> when it could not be read - the
    /// sizes and the tables are still read, and <see cref="WindowNotRead" /> says why there is no growth.
    /// </summary>
    public StatisticsWindow? Window { get; init; }

    /// <summary>Why <see cref="Window" /> is missing, in the studio's words and a SQLSTATE, or <see langword="null" />.</summary>
    public string? WindowNotRead { get; init; }

    /// <summary>
    /// The database's growth a day: the judged tables' <see cref="StorageTableFacts.GrowthBytesPerDay" /> summed (issue #8 G3) -
    /// <see langword="null" /> until the window has counted for a day, while it looks reset, and without a window.
    /// </summary>
    public double? GrowthBytesPerDay { get; init; }

    /// <summary>How many tables the source judges - every table the largest are chosen from.</summary>
    public int TableCount { get; init; }

    /// <summary>What the source's set leaves out (<see cref="IStudioTableSet.Coverage" />), or <see langword="null" /> for every table.</summary>
    public string? Coverage { get; init; }

    /// <summary>
    /// How many of <see cref="TableCount" /> sit in the source's own schemas (no <see cref="StudioTable.GatedSchemas" />): the
    /// count a visitor the database browser keeps out of every other schema is told, because how many tables a schema only
    /// <c>BrowsableSchemas</c> admits holds is the browser's to show (G1M F6, H1b).
    /// </summary>
    public int OwnSchemasTableCount { get; init; }

    /// <summary>
    /// <see cref="Coverage" /> counted over the source's own schemas alone (<see cref="IStudioTableSet.OwnSchemasCoverage" />):
    /// the sentence that visitor reads. The same as <see cref="Coverage" /> for a set that reads no other schema.
    /// </summary>
    public string? OwnSchemasCoverage { get; init; }

    /// <summary>Whether the own-schemas reading says less than the whole one - and a visitor's view must choose between them.</summary>
    public bool HasOwnSchemasReading =>
        OwnSchemasTableCount != TableCount || !string.Equals(OwnSchemasCoverage, Coverage, StringComparison.Ordinal);

    /// <summary>Why no table was read, when none could be - the studio has no table source - or <see langword="null" />.</summary>
    public string? TablesNotRead { get; init; }

    /// <summary>The <see cref="LargestCount" /> largest tables by their estimated total, largest first.</summary>
    public IReadOnlyList<StorageTableFacts> LargestTables { get; init; } = [];

    /// <summary><c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; }

    /// <summary>Whether the server reports updates that went to another page (<c>n_tup_newpage_upd</c>, PostgreSQL 16+).</summary>
    public bool NewPageUpdatesRead => PostgresFeatures.Has(PostgresFeature.NewPageUpdates, ServerVersion);

    /// <summary>What the read ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>
    /// The facts the reads describe: every table of <paramref name="set" /> that <paramref name="rows" /> has a row for, the
    /// largest <see cref="LargestCount" /> kept, and the growth summed over all of them. Pure, for a test as for the reader.
    /// </summary>
    /// <param name="storeKey">The source's key.</param>
    /// <param name="databaseId">The database's identity.</param>
    /// <param name="window">The statistics window.</param>
    /// <param name="database">The database as a whole.</param>
    /// <param name="set">The judged tables, or <see langword="null" /> when the studio has no table source.</param>
    /// <param name="rows">What the table statement read.</param>
    public static StorageFacts Build(
        string storeKey,
        string databaseId,
        StatisticsWindow window,
        StorageDatabaseRow database,
        IStudioTableSet? set,
        IReadOnlyList<StorageTableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Build(storeKey, databaseId, window.MeasuredAt, window, database, set, rows);
    }

    /// <summary>
    /// <see cref="Build(string, string, StatisticsWindow, StorageDatabaseRow, IStudioTableSet, IReadOnlyList{StorageTableRow})" />
    /// with the window optional: without one there is no growth, and every size and table is still there.
    /// </summary>
    /// <param name="storeKey">The source's key.</param>
    /// <param name="databaseId">The database's identity.</param>
    /// <param name="measuredAt">When the facts were read, by the studio's clock.</param>
    /// <param name="window">The statistics window, or <see langword="null" /> when it could not be read.</param>
    /// <param name="database">The database as a whole.</param>
    /// <param name="set">The judged tables, or <see langword="null" /> when the studio has no table source.</param>
    /// <param name="rows">What the table statement read.</param>
    public static StorageFacts Build(
        string storeKey,
        string databaseId,
        DateTimeOffset measuredAt,
        StatisticsWindow? window,
        StorageDatabaseRow database,
        IStudioTableSet? set,
        IReadOnlyList<StorageTableRow> rows)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(rows);

        List<StorageTableFacts> tables = [];

        if (set is not null)
        {
            foreach (StorageTableRow row in rows)
            {
                if (set.Find(row.Schema, row.Table) is { } table)
                {
                    tables.Add(new StorageTableFacts(table, row));
                }
            }
        }

        double? growth = null;

        if (window is { CoversADay: true })
        {
            growth = tables.Select(x => x.GrowthBytesPerDay(window) ?? 0d).Sum();
        }

        return new StorageFacts
        {
            StoreKey = storeKey,
            DatabaseId = databaseId,
            MeasuredAt = measuredAt,
            DatabaseBytes = database.DatabaseBytes,
            EstimatedDatabaseBytes = database.EstimatedBytes,
            Window = window,
            GrowthBytesPerDay = growth,
            TableCount = set?.Tables.Count ?? 0,
            Coverage = set?.Coverage,
            OwnSchemasTableCount = set?.Tables.Count(static x => x.GatedSchemas.Count == 0) ?? 0,
            OwnSchemasCoverage = set?.OwnSchemasCoverage,
            TablesNotRead = set is null ? HealthTables.NoSource : null,
            LargestTables =
            [
                .. tables
                    .OrderByDescending(static x => x.TotalBytes)
                    .ThenBy(static x => x.Table.Schema, StringComparer.Ordinal)
                    .ThenBy(static x => x.Table.Table, StringComparer.Ordinal)
                    .Take(LargestCount),
            ],
            ServerVersion = database.ServerVersion,
        };
    }
}

/// <summary>The Storage page's facts read: one database's sizes, growth and largest tables, gate-free (W5-index §2.3).</summary>
/// <remarks>An interface, so a page or service test hands the facts over without Postgres (D12), and the diagnostics API reads the same.</remarks>
internal interface IStorageFacts
{
    /// <summary>
    /// The facts of the database <paramref name="resolved" /> names: a recent read, or a new one shared with everybody who asks
    /// while it runs. Throws what the database throws.
    /// </summary>
    /// <param name="resolved">
    /// The database - resolved by a visitor the store policy passed for it as a whole, or by the unattended path. Nothing here
    /// asks who is asking.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    Task<StorageFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <see cref="ReadAsync" /> would be answered without a new read - a recent read in hand, or one running - so the
    /// diagnostics API spends a fresh read only on a miss (API design §3.4). <see langword="false" /> unless an implementation
    /// knows better: the cautious answer is "it would read".
    /// </summary>
    /// <param name="resolved">The database.</param>
    bool IsCached(ResolvedSource resolved) => false;
}

/// <summary>
/// The real facts read: the studio's table set (issue #8 G1), the statistics window and two catalog statements, through the
/// health advisor's own runner - single flight per database, kept for one <c>HealthInterval</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The checks' guard, not a session of its own.</b> It reads through a <see cref="HealthCheckContext" /> it makes for itself, as
/// a sweep does, so the table source is asked exactly as a sweep asks it and every statement runs where the checks' do:
/// <c>SET TRANSACTION READ ONLY</c>, <c>statement_timeout</c> (the studio's <c>QueryTimeout</c>, never more than fifteen
/// seconds), <c>lock_timeout</c>, <c>search_path</c> pinned to <c>pg_catalog</c>, <c>SqlConsoleRole</c> when one is set, and
/// every statement kept for "Show SQL".
/// </para>
/// <para>
/// <b>Locks nothing it lists</b> (D27; <see cref="StorageQueries" />): the set is the source's one catalog statement, the window's
/// statement reads statistics views and control functions, and the tables' statement reads <c>relpages</c> and the statistics
/// collector. No size function but <c>pg_database_size</c>, which takes no relation lock, and nothing deparsed.
/// </para>
/// <para>
/// <b>The window against the advisor's history, read-only.</b> The window is computed against the database's regular history -
/// the samples the sweeps wrote - so the page and the Health page agree on whether the counters look reset; this read writes
/// nothing to it.
/// </para>
/// <para>
/// <b>Authorization comes before this, not inside it</b>, as for the sweep: a key is the source and the database and nothing
/// else, so the caller resolves the scope first, and what a visitor is shown is decided afterwards (<c>StorageService</c>).
/// </para>
/// </remarks>
internal sealed class StorageFactsReader : IStorageFacts
{
    private readonly IStudioSettings settings;
    private readonly HealthHistory history;
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider timeProvider;
    private readonly IStudioRuntime runtime;
    private readonly IHealthStatementObserver? observer;
    private readonly StudioSnapshotCache cache;
    private int reads;

    public StorageFactsReader(
        IStudioSettings settings,
        HealthHistory history,
        IServiceScopeFactory scopes,
        TimeProvider? timeProvider = null,
        IStudioRuntime? runtime = null,
        IHealthStatementObserver? observer = null)
    {
        this.settings = settings;
        this.history = history;
        this.scopes = scopes;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.runtime = runtime ?? NoStudioRuntime.Instance;
        this.observer = observer;
        cache = new StudioSnapshotCache(this.timeProvider, settings.EffectiveHealthInterval);
    }

    /// <summary>How many reads actually ran, for a test.</summary>
    internal int ReadCount => Volatile.Read(ref reads);

    /// <summary>
    /// Every statement's <c>lock_timeout</c>, when a test needs one shorter than the read-only session's own - the no-lock proof's
    /// "returns under 300 ms behind somebody's <c>ACCESS EXCLUSIVE</c> lock"; <see langword="null" /> - always, in a host - is
    /// <see cref="ReadOnlySqlOptions.LockTimeout" />'s default.
    /// </summary>
    internal TimeSpan? LockTimeout { get; set; }

    /// <inheritdoc />
    public Task<StorageFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return cache.GetAsync(KeyOf(resolved), token => ReadUncachedAsync(resolved, token), cancellationToken);
    }

    /// <inheritdoc />
    public bool IsCached(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return cache.Holds(KeyOf(resolved));
    }

    private static string KeyOf(ResolvedSource resolved) => "storage|" + resolved.Source.Key + "|" + resolved.Target.Identity;

    /// <summary>One read, past the cache.</summary>
    internal async Task<StorageFacts> ReadUncachedAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        Interlocked.Increment(ref reads);

        TimeSpan statementTimeout = settings.QueryTimeout < HealthSweeper.MaxStatementTimeout ? settings.QueryTimeout : HealthSweeper.MaxStatementTimeout;
        ReadOnlySqlOptions options = new() { StatementTimeout = statementTimeout, Role = settings.SqlConsoleRole };

        if (LockTimeout is { } lockTimeout)
        {
            options = options with { LockTimeout = lockTimeout };
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await using HealthConnection connection = new(resolved.Target);

        DateTimeOffset now = timeProvider.GetUtcNow();
        HealthSqlRunner sql = new(connection, options, observer);
        HealthCheckContext context = new(resolved, settings, runtime, scope.ServiceProvider, sql, now, new HealthSweepMemo(), cancellationToken);

        // The window is the growth's alone: a window that cannot be read - a platform that refuses a control function, say - costs
        // the growth figures, never the sizes and the tables. Computed as a read that writes no history (recorded: false), so
        // a reset seen against a stale sample stops looking like one once the window is a day long.
        StatisticsWindow? window = null;
        string? windowNotRead = null;

        try
        {
            StatisticsReading reading = await StatisticsWindow.ReadingAsync(context).ConfigureAwait(false);
            window = StatisticsWindow.Compute(reading, history.Read(resolved.Source.Key, resolved.Target.Identity), recorded: false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            windowNotRead = StatisticsWindow.NotRead(exception);
        }

        IStudioTableSet? set = scope.ServiceProvider.GetService<IHealthTableSource>() is null
            ? null
            : await HealthTables.SetAsync(context).ConfigureAwait(false);

        IReadOnlyList<StorageDatabaseRow> database = await sql
            .QueryAsync(StorageQueries.Database(), StorageQueries.ReadDatabase, cancellationToken)
            .ConfigureAwait(false);

        if (database.Count != 1)
        {
            throw new InvalidOperationException(
                "The database's size came back as " + database.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " rows.");
        }

        IReadOnlyList<StorageTableRow> rows = set is { Tables.Count: > 0 }
            ? await sql.QueryAsync(StorageQueries.Tables(HealthTables.Targets(set)), StorageQueries.ReadTable, cancellationToken).ConfigureAwait(false)
            : [];

        string? notRead = database[0].DatabaseBytes is null ? await DatabaseSizeNotReadAsync(context).ConfigureAwait(false) : null;

        return StorageFacts.Build(resolved.Source.Key, resolved.Target.Identity, now, window, database[0], set, rows) with
        {
            DatabaseBytesNotRead = notRead,
            WindowNotRead = windowNotRead,
            Statements = [.. sql.Statements],
        };
    }

    /// <summary>
    /// Why <c>pg_database_size</c> was not asked, naming the grant (W5-index §2.6): <c>CONNECT</c> on the database is its owner's
    /// to grant, and <c>pg_read_all_stats</c> the platform's administrator's - from the one grant read and the one platform read.
    /// </summary>
    private static async Task<string> DatabaseSizeNotReadAsync(HealthCheckContext context)
    {
        const string Needs = "Not measured: pg_database_size needs CONNECT on this database";

        try
        {
            PostgresGrants grants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);
            PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

            return Needs + " or " + PostgresGrant.ReadAllStats.Described + " for role " + grants.ReadsAs + ". The database's owner can "
                + "grant CONNECT. " + PostgresGrants.Grantor(PostgresGrant.ReadAllStats, server.Platform);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Which role and platform could not be read: the sentence still says what the size needs.
            return Needs + " or " + PostgresGrant.ReadAllStats.Described + " for the role the studio reads as.";
        }
    }
}
