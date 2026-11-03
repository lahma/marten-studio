using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Connection;

/// <summary>This application's sessions, as the database answered.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="ApplicationCount">How many applications the role has sessions under, beyond <paramref name="Rows" /> when the read was capped.</param>
/// <param name="Rows">The applications, the most sessions first.</param>
internal sealed record ConnectionApplicationsRead(DateTimeOffset ReadAt, long ApplicationCount, IReadOnlyList<ConnectionApplicationRow> Rows);

/// <summary>Where the Connection page's facts come from: the sweep's, or a read of the page's own.</summary>
/// <remarks>An interface so the service can be driven in a test without a database (D12).</remarks>
internal interface IConnectionFactsSource
{
    /// <summary>
    /// The database's connection facts - the redacted settings and what the server saw - from the regular sweep's own shared
    /// read when a fresh sweep holds it, and otherwise from one read of the page's own, cached for <c>HealthInterval</c>.
    /// Gate-free: the caller has resolved the scope, which is where the store policy is asked.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    Task<ConnectionFacts> GetAsync(ResolvedSource resolved, CancellationToken cancellationToken);

    /// <summary>
    /// The sessions the studio's login has in this database, by application - for a caller that has passed the activity gate.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    Task<ConnectionApplicationsRead> ReadApplicationsAsync(ResolvedSource resolved, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IConnectionFactsSource" /> over the health sweep and, when no fresh sweep holds the read, a
/// <see cref="HealthSqlRunner" /> of its own - the advisor's read-only transaction under <c>statement_timeout</c> and
/// <c>lock_timeout</c>, as <c>SqlConsoleRole</c> when one is set - exactly as the Aurora facts reader reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same read, not a second opinion.</b> The page and the connection checks share <see cref="ConnectionFacts.SharedAsync" />,
/// so a page opened while a sweep is fresh costs the database nothing the sweep did not already cost; a page opened when no sweep
/// has asked for the facts reads them once per <c>HealthInterval</c>, however many visitors look.
/// </para>
/// <para>
/// <b>The connection string stays inside <see cref="ConnectionFacts" />.</b> This class never sees it: it hands the context to
/// the facts read and takes back redacted records.
/// </para>
/// </remarks>
internal sealed class ConnectionFactsSource : IConnectionFactsSource
{
    private readonly IDiagnosticsSettings settings;
    private readonly IServiceProvider services;
    private readonly HealthSweepCache sweeps;
    private readonly IStudioRuntime runtime;
    private readonly TimeProvider timeProvider;
    private readonly StudioSnapshotCache cache;
    private int reads;
    private int applicationReads;

    public ConnectionFactsSource(
        IDiagnosticsSettings settings,
        IServiceProvider services,
        HealthSweepCache sweeps,
        TimeProvider? timeProvider = null,
        IStudioRuntime? runtime = null)
    {
        this.settings = settings;
        this.services = services;
        this.sweeps = sweeps;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.runtime = runtime ?? NoStudioRuntime.Instance;
        cache = new StudioSnapshotCache(this.timeProvider, settings.EffectiveHealthInterval);
    }

    /// <summary>How many times the page's own read ran, for a test.</summary>
    internal int ReadCount => Volatile.Read(ref reads);

    /// <summary>How many times the applications were read, for a test.</summary>
    internal int ApplicationReadCount => Volatile.Read(ref applicationReads);

    /// <inheritdoc />
    public Task<ConnectionFacts> GetAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;

        if (sweeps.Latest(storeKey, databaseId) is { } sweep
            && sweeps.Now - sweep.MeasuredAt < settings.EffectiveHealthInterval
            && sweep.TryShared<ConnectionFacts>(ConnectionObservedQueries.FactsKey, out ConnectionFacts? shared))
        {
            return Task.FromResult(shared);
        }

        return cache.GetAsync("facts|" + storeKey + "|" + databaseId, token =>
        {
            Interlocked.Increment(ref reads);
            return ReadFactsAsync(resolved, token);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConnectionApplicationsRead> ReadApplicationsAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return cache.GetAsync("applications|" + resolved.Source.Key + "|" + resolved.Target.Identity, token =>
        {
            Interlocked.Increment(ref applicationReads);
            return ReadApplicationsCoreAsync(resolved, token);
        }, cancellationToken);
    }

    private async Task<ConnectionFacts> ReadFactsAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = Context(resolved, connection, cancellationToken);

        return await ConnectionFacts.SharedAsync(context).ConfigureAwait(false);
    }

    private async Task<ConnectionApplicationsRead> ReadApplicationsCoreAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = Context(resolved, connection, cancellationToken);

        IReadOnlyList<(ConnectionApplicationRow Row, long Applications)> rows = await context.Sql
            .QueryAsync(
                ConnectionObservedQueries.Applications(),
                static reader => (
                    new ConnectionApplicationRow(
                        reader.GetString(0),
                        reader.GetInt64(1),
                        reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                        reader.GetInt64(3),
                        reader.GetInt64(4)),
                    reader.GetInt64(5)),
                cancellationToken)
            .ConfigureAwait(false);

        return new ConnectionApplicationsRead(context.Now, rows.Count == 0 ? 0 : rows[0].Applications, [.. rows.Select(static x => x.Row)]);
    }

    private HealthCheckContext Context(ResolvedSource resolved, HealthConnection connection, CancellationToken cancellationToken)
    {
        TimeSpan timeout = settings.QueryTimeout < HealthSweeper.MaxStatementTimeout ? settings.QueryTimeout : HealthSweeper.MaxStatementTimeout;
        HealthSqlRunner sql = new(connection, new ReadOnlySqlOptions { StatementTimeout = timeout, Role = settings.SqlConsoleRole }, observer: null);

        return new HealthCheckContext(resolved, settings, runtime, services, sql, timeProvider.GetUtcNow(), new HealthSweepMemo(), cancellationToken);
    }
}
