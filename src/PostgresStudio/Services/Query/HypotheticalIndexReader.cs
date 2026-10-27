using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace PostgresStudio.Services.Query;

/// <summary>How a surface plans its statement, twice, inside the what-if's transaction (X2).</summary>
/// <param name="PlanAsync">
/// Plans the statement and returns <c>EXPLAIN (FORMAT JSON)</c>'s text, or <see langword="null" /> when Postgres returned none.
/// Run once before the hypothetical index and once after it, in the same read-only transaction.
/// </param>
/// <param name="PrepareAsync">What runs first, once: the drill-in's <c>search_path</c>.</param>
/// <param name="Vet">
/// Judges the plan without the index before anything is imagined: a sentence refuses the what-if - the drill-in's withheld
/// plan, an index the plan does not suggest - and <see langword="null" /> lets it go on.
/// </param>
/// <param name="AfterFailureAsync">
/// Runs on the same connection after a what-if that threw, once hypopg is reset and before the connection goes back: the
/// drill-in takes back the statement its batch prepared. Must not throw.
/// </param>
internal sealed record HypotheticalIndexPlanner(
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<string?>> PlanAsync,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? PrepareAsync = null,
    Func<string, string?>? Vet = null,
    Func<NpgsqlConnection, Task>? AfterFailureAsync = null);

/// <summary>What one what-if read: hypopg's state, the two plans as Postgres printed them, and the index it made.</summary>
internal sealed record HypotheticalIndexRead
{
    /// <summary>hypopg in this database, for the role the studio plans as.</summary>
    public required HypotheticalIndexAvailability Availability { get; init; }

    /// <summary>The plan without the index, unmasked: the caller masks it.</summary>
    public string? BeforeJson { get; init; }

    /// <summary>The plan with the index, unmasked.</summary>
    public string? AfterJson { get; init; }

    /// <summary>The index hypopg made, or <see langword="null" />.</summary>
    public HypotheticalIndexMade? Made { get; init; }

    /// <summary>Why the what-if stopped before it imagined anything: <see cref="HypotheticalIndexPlanner.Vet" />'s sentence.</summary>
    public string? Refusal { get; init; }
}

/// <summary>A test seam (absent in a host): told what the what-if is doing, on the connection it does it on.</summary>
internal interface IHypotheticalIndexObserver
{
    /// <summary>The hypothetical index exists on <paramref name="processId" />'s backend; a test may fail the what-if here.</summary>
    Task MadeAsync(int processId, HypotheticalIndexMade made, CancellationToken cancellationToken);

    /// <summary>The what-if is over and its connection is about to go back to the pool, or be discarded.</summary>
    void Finished(int processId, HypotheticalIndexCleanup cleanup);
}

/// <summary>How a what-if left its connection.</summary>
internal enum HypotheticalIndexCleanup
{
    /// <summary>Nothing was imagined: there was nothing to reset.</summary>
    NothingMade,

    /// <summary>Reset in the what-if's own transaction, as the last step.</summary>
    Reset,

    /// <summary>Reset after the what-if failed, in a transaction of its own.</summary>
    ResetAfterFailure,

    /// <summary>The reset failed, and the studio ended its own session: the backend, and the index with it, are gone.</summary>
    SessionEnded,

    /// <summary>The connection was broken already: Npgsql discards it, and the backend with it.</summary>
    ConnectionBroken,

    /// <summary>Neither the reset nor ending the session worked, on a connection that is still open. An Error says so.</summary>
    Failed,
}

/// <summary>
/// X2's engine: one statement planned before and after one hypothetical index, in the read-only transaction, with hypopg reset
/// on the same connection before it goes back to the pool - or the session ended when the reset cannot be.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sequence.</b> Open a connection of the surface's own database; in <see cref="ReadOnlySqlSession" />'s read-only
/// transaction - <c>statement_timeout</c> (the studio's <c>QueryTimeout</c>), <c>lock_timeout</c>, and <c>SET LOCAL ROLE</c> to
/// <c>SqlConsoleRole</c> where the host set one (D29: "plans run as SqlConsoleRole") - read whether hypopg is usable, unless
/// the catalog cache knows; plan the statement; ask hypopg for the index (its definition a parameter); plan the statement again;
/// <c>hypopg_reset()</c>; roll back. hypopg works inside <c>BEGIN READ ONLY</c> and for a role short of a superuser (PROBE).
/// </para>
/// <para>
/// <b>Reset, always, on the same connection.</b> A hypothetical index lives in the backend and survives <c>ROLLBACK</c>
/// (PROBE): a pooled backend goes to the host's next caller, whose planner would see an index that does not exist. So the reset
/// is the transaction's last statement, and any way out after hypopg was asked - a plan that fails, a cancelled circuit, a test
/// observer that throws - resets it again in a read-only transaction of its own, as the same role, under a bound of its own and
/// never the caller's token (a cancelled what-if is exactly the one that must). <c>DISCARD ALL</c>, which Npgsql runs when it
/// resets a pooled connection, does not reach hypopg's memory.
/// </para>
/// <para>
/// <b>When the reset fails, the connection does not go back to the pool</b> - X2's choice of the two the packet offered: the
/// studio ends its own session (<see cref="HypotheticalIndexQueries.TerminateSelfSql" />), which a role may do to its own
/// backend whatever else it may not, and Npgsql then holds the connection broken and never pools it again; the backend exits,
/// and the index with it. Clearing the pool was the other choice, and is not taken: Npgsql's <c>ClearPool</c> finds no pool for
/// a data source's connection, and a data source's own <c>Clear()</c> would close the host's idle connections, which registering
/// the studio must never do (<c>ExpensiveStatementsCheck</c>' remarks). A connection already broken is discarded by Npgsql with
/// its backend. Only a server that refuses both a function call and a signal to its own backend, on a connection that stays
/// open, leaves the index behind - and an Error names the database and the backend's process id.
/// </para>
/// <para>
/// <b>Cached per database</b>: whether hypopg is installed and usable is kept for the catalog cache's time to live (60 seconds,
/// <see cref="CatalogCache{TValue}" />), keyed by the database and the role setting, so a page that offers a what-if under every
/// plan asks once a minute; an extension installed or granted shows within the minute.
/// </para>
/// </remarks>
internal sealed class HypotheticalIndexReader
{
    /// <summary>The longest the reader waits to reset, or to end its session, after a what-if that did not finish.</summary>
    internal static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(5);

    private readonly IStudioSettings settings;
    private readonly ILogger<HypotheticalIndexReader> logger;
    private readonly IHypotheticalIndexObserver? observer;
    private readonly CatalogCache<HypotheticalIndexAvailability> availability = new();

    public HypotheticalIndexReader(IStudioSettings settings, ILogger<HypotheticalIndexReader> logger, IHypotheticalIndexObserver? observer = null)
    {
        this.settings = settings;
        this.logger = logger;
        this.observer = observer;
    }

    /// <summary>
    /// The statement the reset runs in place of <see cref="HypotheticalIndexQueries.Reset" />, for a test that proves what happens
    /// when a reset fails; <see langword="null" /> - always, in a host - is hypopg's own.
    /// </summary>
    internal string? ResetSqlOverride { get; set; }

    /// <summary>Forgets what the cache knows of hypopg, for a test that installs or grants it.</summary>
    internal void Forget() => availability.Clear();

    /// <summary>The what-if for <paramref name="index" /> on <paramref name="database" />. Throws what Postgres or the connection throws, after cleaning up.</summary>
    /// <param name="database">The database, resolved through the surface's own gate.</param>
    /// <param name="index">The index to imagine.</param>
    /// <param name="planner">How the surface plans its statement.</param>
    /// <param name="cancellationToken">Cancels the what-if; never the cleanup.</param>
    public async Task<HypotheticalIndexRead> ReadAsync(
        IStudioDatabase database,
        HypotheticalIndex index,
        HypotheticalIndexPlanner planner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(planner);

        string key = CacheKey(database);

        HypotheticalIndexAvailability? known = availability.TryGet(key, out HypotheticalIndexAvailability cached) ? cached : null;

        if (known is { State: not HypotheticalIndexState.Available })
        {
            return new HypotheticalIndexRead { Availability = known };
        }

        ReadOnlySqlSession session = Session();
        int commandTimeout = CommandTimeoutSeconds();

        await using NpgsqlConnection connection = database.CreateConnection(StudioConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        int processId = connection.ProcessID;
        string? schema = null;
        bool asked = false;
        bool reset = false;
        bool finished = false;
        HypotheticalIndexCleanup cleanup = HypotheticalIndexCleanup.NothingMade;

        try
        {
            HypotheticalIndexRead read = await session.InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        HypotheticalIndexAvailability? usable = known;

                        if (usable is null)
                        {
                            usable = HypotheticalIndexAvailability.Of(
                                await HypotheticalIndexQueries.ReadAvailabilityAsync(connection, transaction, commandTimeout, token).ConfigureAwait(false));
                            availability.Set(key, usable);
                        }

                        if (usable.State != HypotheticalIndexState.Available || usable.Schema is not { } installedIn)
                        {
                            return new HypotheticalIndexRead { Availability = usable };
                        }

                        if (planner.PrepareAsync is { } prepare)
                        {
                            await prepare(connection, transaction, token).ConfigureAwait(false);
                        }

                        string? before = await planner.PlanAsync(connection, transaction, token).ConfigureAwait(false);

                        if (before is null)
                        {
                            return new HypotheticalIndexRead { Availability = usable };
                        }

                        if (planner.Vet?.Invoke(before) is { } refused)
                        {
                            return new HypotheticalIndexRead { Availability = usable, BeforeJson = before, Refusal = refused };
                        }

                        schema = installedIn;
                        asked = true;

                        HypotheticalIndexMade made = await HypotheticalIndexQueries
                            .CreateAsync(connection, transaction, installedIn, index.Definition, commandTimeout, token)
                            .ConfigureAwait(false);

                        if (observer is not null)
                        {
                            await observer.MadeAsync(processId, made, token).ConfigureAwait(false);
                        }

                        string? after = await planner.PlanAsync(connection, transaction, token).ConfigureAwait(false);

                        // The last statement, on the same connection, before the rollback: the index is gone before the pool sees it.
                        await HypotheticalIndexQueries.ResetAsync(connection, transaction, ResetSql(installedIn), commandTimeout, token).ConfigureAwait(false);
                        reset = true;

                        return new HypotheticalIndexRead { Availability = usable, BeforeJson = before, AfterJson = after, Made = made };
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            cleanup = reset ? HypotheticalIndexCleanup.Reset : HypotheticalIndexCleanup.NothingMade;
            finished = true;
            return read;
        }
        finally
        {
            if (asked && !reset)
            {
                cleanup = await CleanUpAsync(connection, session, schema!, commandTimeout, database).ConfigureAwait(false);
            }

            if (!finished && planner.AfterFailureAsync is { } afterFailure && connection.State == System.Data.ConnectionState.Open)
            {
                await afterFailure(connection).ConfigureAwait(false);
            }

            observer?.Finished(processId, cleanup);
        }
    }

    /// <summary>
    /// After a what-if that asked hypopg for an index and did not reset it: reset it in a transaction of its own, and if that
    /// fails end the session, so the backend never goes back to the pool holding it.
    /// </summary>
    private async Task<HypotheticalIndexCleanup> CleanUpAsync(
        NpgsqlConnection connection,
        ReadOnlySqlSession session,
        string schema,
        int commandTimeout,
        IStudioDatabase database)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            // Broken: Npgsql discards it, and the backend - with whatever it held - ends with it.
            return HypotheticalIndexCleanup.ConnectionBroken;
        }

        int processId = connection.ProcessID;

        using (CancellationTokenSource bound = new(CleanupBound))
        {
            try
            {
                await session.InTransactionAsync(
                        connection,
                        async (transaction, token) =>
                        {
                            await HypotheticalIndexQueries.ResetAsync(connection, transaction, ResetSql(schema), commandTimeout, token).ConfigureAwait(false);
                            return 0;
                        },
                        bound.Token)
                    .ConfigureAwait(false);

                return HypotheticalIndexCleanup.ResetAfterFailure;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(exception, "A what-if could not reset hypopg on backend {ProcessId}; ending that session", processId.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        if (connection.State != System.Data.ConnectionState.Open)
        {
            return HypotheticalIndexCleanup.ConnectionBroken;
        }

        using (CancellationTokenSource bound = new(CleanupBound))
        {
            try
            {
                await HypotheticalIndexQueries.TerminateSelfAsync(connection, commandTimeout, bound.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Expected: 57P01, the server ending the session this statement asked it to end.
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(exception, "A what-if ended its own session on backend {ProcessId}", processId.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        if (connection.State != System.Data.ConnectionState.Open)
        {
            return HypotheticalIndexCleanup.SessionEnded;
        }

        logger.LogError(
            "A what-if on database {Database} could neither reset hypopg nor end its own session; backend {ProcessId} may still hold a hypothetical index, which every later user of that pooled connection's planner sees until it is reset or the backend ends",
            StudioLogValue.Cap(database.Identity),
            processId.ToString(CultureInfo.InvariantCulture));

        return HypotheticalIndexCleanup.Failed;
    }

    private string ResetSql(string schema) =>
        ResetSqlOverride ?? HypotheticalIndexQueries.Reset(schema) ?? throw new InvalidOperationException("hypopg's schema is not a name the studio quotes.");

    private ReadOnlySqlSession Session()
    {
        TimeSpan statementTimeout = settings.QueryTimeout;
        return new ReadOnlySqlSession(new ReadOnlySqlOptions { StatementTimeout = statementTimeout, Role = settings.SqlConsoleRole, MaxRows = 1 });
    }

    private int CommandTimeoutSeconds() => (int) Math.Ceiling(settings.QueryTimeout.TotalSeconds) + 5;

    /// <summary>
    /// The database and the role the studio plans as: what decides whether hypopg is there and usable. Never a connection
    /// string.
    /// </summary>
    private string CacheKey(IStudioDatabase database) =>
        database.Identity + "\u001f" + database.Server + "\u001f" + database.Name + "\u001f" + (settings.SqlConsoleRole ?? string.Empty);
}
