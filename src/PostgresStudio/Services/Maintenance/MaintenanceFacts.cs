using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Maintenance;

/// <summary>
/// What the Maintenance page knows about one database, before anybody is shown any of it (W5-V1): the regular sweep's own
/// reads - every judged table's autovacuum decisions, what maintenance is running, the server's workers, the wraparound
/// facts - and the history they are dated by.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gate-free, and the database's</b> (W5 §2.3). Nothing here knows who is looking, so a visitor's view applies D27 and
/// D29 afterwards (<c>MaintenanceService</c>), and the API packet's unattended path can take the same record.
/// </para>
/// <para>
/// <b>Read once per interval, by the sweep.</b> These are the regular sweep's shared reads (<see cref="HealthSweep.Shared" />),
/// taken from the one sweep per database per interval the Health page and the Overview already cause - a page open on
/// Maintenance costs the database nothing they did not already pay. A read the sweep did not finish - a check that failed or
/// was out of budget - is absent here, and the view says so.
/// </para>
/// </remarks>
internal sealed record MaintenanceFacts
{
    /// <summary>When the sweep read them.</summary>
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The source's registered key.</summary>
    public required string StoreKey { get; init; }

    /// <summary>The database's identity.</summary>
    public required string DatabaseId { get; init; }

    /// <summary>
    /// Every judged table and its autovacuum decisions (<c>HealthTableQueries.TablesSql</c>), or <see langword="null" /> when
    /// the sweep did not read them.
    /// </summary>
    public IReadOnlyList<TableFacts>? Tables { get; init; }

    /// <summary>What a capped table set left out, in words (<see cref="IStudioTableSet.Coverage" />), or <see langword="null" />.</summary>
    public string? Coverage { get; init; }

    /// <summary>The server's autovacuum workers (T16's read), or <see langword="null" />.</summary>
    public MaintenanceWorkersRow? Workers { get; init; }

    /// <summary>What maintenance runs in this database (T16's read), or <see langword="null" />.</summary>
    public IReadOnlyList<MaintenanceProgressRow>? Running { get; init; }

    /// <summary>The wraparound facts T12, T13 and T14 share, or <see langword="null" />.</summary>
    public WraparoundFacts? Wraparound { get; init; }

    /// <summary>Who runs the server, or <see langword="null" />.</summary>
    public PostgresPlatform? Platform { get; init; }

    /// <summary>The reading role's grants, when a check read them this sweep, or <see langword="null" />.</summary>
    public PostgresGrants? Grants { get; init; }

    /// <summary>T14's result - its finding or its note - for the horizon section.</summary>
    public HealthCheckResult? Horizon { get; init; }

    /// <summary>The database's history as the database-wide gate may read it: what progress and the forecast are dated by.</summary>
    public HealthHistoryView History { get; init; } = HealthHistoryView.Empty;
}

/// <summary>The Maintenance page's facts read: one database's <see cref="MaintenanceFacts" />.</summary>
internal interface IMaintenanceFacts
{
    /// <summary>
    /// The database's facts: the regular sweep's reads (run now if the cached sweep has expired, joined if one is running).
    /// Gate-free: the caller has resolved the scope, which is where the store policy is asked.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared sweep.</param>
    Task<MaintenanceFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IMaintenanceFacts" /> over the health sweep: single flight and cached for <c>HealthInterval</c> because the sweep
/// is (<see cref="HealthSweepCache" />), keyed by the source's key and the database's identity.
/// </summary>
internal sealed class MaintenanceFactsReader(HealthSweepCache cache, HealthHistory history) : IMaintenanceFacts
{
    /// <inheritdoc />
    public async Task<MaintenanceFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        HealthSweep sweep = await cache.GetAsync(resolved, HealthSweepKind.Regular, resolved.Scope.DatabaseId, cancellationToken).ConfigureAwait(false);
        return From(sweep, history.Read(sweep.StoreKey, sweep.DatabaseId));
    }

    /// <summary>The facts a sweep holds, with the history at the database-wide gate. Pure.</summary>
    internal static MaintenanceFacts From(HealthSweep sweep, HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(history);

        return new MaintenanceFacts
        {
            MeasuredAt = sweep.MeasuredAt,
            StoreKey = sweep.StoreKey,
            DatabaseId = sweep.DatabaseId,
            Tables = sweep.TryShared<IReadOnlyList<TableFacts>>(HealthTables.TablesKey, out var tables) ? tables : null,
            Coverage = sweep.TryShared<IStudioTableSet>(HealthTables.SetKey, out var set) ? set.Coverage : null,
            Workers = sweep.TryShared<MaintenanceWorkersRow>(MaintenanceQueries.WorkersKey, out var workers) ? workers : null,
            Running = sweep.TryShared<IReadOnlyList<MaintenanceProgressRow>>(MaintenanceQueries.ProgressKey, out var running) ? running : null,
            Wraparound = sweep.TryShared<WraparoundFacts>(Wraparound.Key, out var wraparound) ? wraparound : null,
            Platform = sweep.TryShared<PostgresServerFacts>(HealthServerQueries.PlatformKey, out var server) ? server.Platform : null,
            Grants = sweep.TryShared<PostgresGrants>(ServerReachQueries.GrantsKey, out var grants) ? grants : null,
            Horizon = sweep.Results.FirstOrDefault(static x => string.Equals(x.Check.Code, "T14", StringComparison.Ordinal)),
            History = history.VisibleAt(HealthGate.Database),
        };
    }
}
