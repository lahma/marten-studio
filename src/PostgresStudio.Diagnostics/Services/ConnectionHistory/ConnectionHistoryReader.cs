using Microsoft.Extensions.Options;

using PostgresStudio.Services.Live;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #43: how the studio's pages read the connection history - once per <see cref="CacheFor" /> and server, whatever the number
/// of circuits polling, and never longer than the studio's <c>QueryTimeout</c>.
/// </summary>
/// <remarks>
/// <para>
/// The store may be the host's own - a table, a time-series database, a remote service - and Database › Sessions polls every
/// <c>HealthInterval</c> in every circuit that has it open. So the reads share one single-flight cache (the same
/// <see cref="StudioSnapshotCache" /> every live page polls through, with a longer time to live: a sample arrives a minute at the
/// most often), and each read is cut off at <c>QueryTimeout</c>: a store that hangs makes the history absent for that poll, never the
/// page slow.
/// </para>
/// <para>
/// A cached entry is keyed by server alone, and holds samples, not a view: the gate - counts for the page's own gate, application
/// names past <c>ViewDatabaseActivity</c> - is the caller's, applied after the read, every time.
/// </para>
/// </remarks>
internal sealed class ConnectionHistoryReader
{
    /// <summary>How long one read of a server's history is reused for.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IPostgresConnectionHistoryStore store;
    private readonly IOptions<PostgresConnectionHistoryOptions> options;
    private readonly IDiagnosticsSettings settings;
    private readonly TimeProvider time;
    private readonly StudioSnapshotCache cache;

    public ConnectionHistoryReader(
        IPostgresConnectionHistoryStore store,
        IOptions<PostgresConnectionHistoryOptions> options,
        IDiagnosticsSettings settings,
        TimeProvider? time = null,
        ConnectionHistoryRegistration? registration = null)
    {
        ReaderOnly = registration?.ReaderOnly ?? false;
        this.store = store;
        this.options = options;
        this.settings = settings;
        this.time = time ?? TimeProvider.System;
        cache = new StudioSnapshotCache(this.time, CacheFor);
    }

    /// <summary>Issue #48: whether this process only reads a history another process fills (<c>AddPostgresConnectionHistoryReader()</c>).</summary>
    public bool ReaderOnly { get; }

    /// <summary>The history's window: how far back a page reads, the options' <c>Retention</c>.</summary>
    public TimeSpan Window => options.Value.Retention;

    /// <summary>The samples of <paramref name="server" /> within <see cref="Window" />, as the store holds them.</summary>
    /// <param name="server">The server, <c>host:port</c> as the connection history keys it.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    /// <exception cref="OperationCanceledException">
    /// The store took longer than <c>QueryTimeout</c>, while <paramref name="cancellationToken" /> was not cancelled.
    /// </exception>
    public Task<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);

        return cache.GetAsync<IReadOnlyList<PostgresConnectionSample>>(
            "connection-history:" + server.ToLowerInvariant(),
            async _ =>
            {
                using CancellationTokenSource limit = new(settings.QueryTimeout, time);
                return await store.ReadAsync(server, time.GetUtcNow() - Window, limit.Token).ConfigureAwait(false);
            },
            cancellationToken);
    }
}
