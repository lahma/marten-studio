using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// 1.0.1 (issue #47): what one read of a server's connection history came to, for every caller that summarizes it - Database ›
/// Sessions › Holders, the diagnostics API's <c>ConnectionHistory</c> section and check C15.
/// </summary>
/// <param name="Outcome">
/// <see cref="PostgresReadOutcome.Read" /> when the window holds samples; <see cref="PostgresReadOutcome.Unavailable" /> when it
/// holds none yet; <see cref="PostgresReadOutcome.TimedOut" /> when the store took longer than <c>QueryTimeout</c>;
/// <see cref="PostgresReadOutcome.Failed" /> for any other failure of the store.
/// </param>
/// <param name="Server">The server, <c>host:port</c> as the history keys it.</param>
/// <param name="Window">The history's window: its retention.</param>
/// <param name="View">The summary of the samples, or <see langword="null" /> unless <paramref name="Outcome" /> is <c>Read</c>.</param>
/// <param name="PendingSamples">How many samples of this server this process has taken and not yet stored.</param>
/// <param name="Failure">What the store threw, for a caller that logs it (Debug), or <see langword="null" />.</param>
internal sealed record ConnectionHistoryRead(
    PostgresReadOutcome Outcome,
    string Server,
    TimeSpan Window,
    ConnectionHistoryView? View,
    int PendingSamples,
    Exception? Failure);

/// <summary>
/// The one place a server's connection history becomes a <see cref="ConnectionHistoryView" />: Holders and the diagnostics API
/// both read it through here, so the two can never disagree on a peak (issue #47).
/// </summary>
internal static class ConnectionHistoryLookup
{
    /// <summary>The server whose history belongs to <paramref name="resolved" />, or <see langword="null" /> when the source exposes no data source.</summary>
    /// <param name="resolved">The database.</param>
    public static string? ServerOf(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return ConnectionHistorySampler.ServerOf(resolved.Target.DataSource?.ConnectionString);
    }

    /// <summary>
    /// Reads the history of <paramref name="server" /> through the shared cache and summarizes it. Never throws for the store's
    /// failure - it is a value - but for the caller's own cancellation.
    /// </summary>
    /// <param name="reader">The reader every page shares (the 30-second cache).</param>
    /// <param name="server">The server, <c>host:port</c>.</param>
    /// <param name="names">Whether applications may be named: the activity gate passed.</param>
    /// <param name="since">Only the samples from this time on, or <see langword="null" /> for the whole window.</param>
    /// <param name="cancellationToken">The caller's.</param>
    public static Task<ConnectionHistoryRead> ReadAsync(
        ConnectionHistoryReader reader,
        string server,
        bool names,
        DateTimeOffset? since,
        CancellationToken cancellationToken) =>
        ReadAsync(reader, null, server, names, since, cancellationToken);

    /// <summary>
    /// Reads the history of <paramref name="server" /> through the shared cache, with the samples <paramref name="sampler" /> holds
    /// because the store refused them (issue #46), and summarizes it. Never throws for the store's failure - it is a value - but for
    /// the caller's own cancellation; when the store cannot be read and the sampler holds samples, those alone are summarized.
    /// </summary>
    /// <param name="reader">The reader every page shares (the 30-second cache).</param>
    /// <param name="sampler">This process's sampler, or <see langword="null" /> where it samples nothing (a reader-only registration).</param>
    /// <param name="server">The server, <c>host:port</c>.</param>
    /// <param name="names">Whether applications may be named: the activity gate passed.</param>
    /// <param name="since">Only the samples from this time on, or <see langword="null" /> for the whole window.</param>
    /// <param name="cancellationToken">The caller's.</param>
    public static async Task<ConnectionHistoryRead> ReadAsync(
        ConnectionHistoryReader reader,
        ConnectionHistorySampler? sampler,
        string server,
        bool names,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(server);

        try
        {
            // Issue #46: the samples this process took and could not append join what the store holds, before the view is made
            // (ConnectionHistoryReader.ReadWithPendingAsync: ordered, each counted once, and alone when the store cannot be read).
            IReadOnlyList<PostgresConnectionSample> held = sampler?.PendingSamples(server) ?? [];
            IReadOnlyList<PostgresConnectionSample> samples = await reader.ReadWithPendingAsync(server, held, cancellationToken).ConfigureAwait(false);
            int pending = held.Count;

            if (since is { } from)
            {
                samples = [.. samples.Where(x => x.At >= from)];
            }

            ConnectionHistoryView? view = ConnectionHistoryView.Of(samples, names);

            return new ConnectionHistoryRead(
                view is null ? PostgresReadOutcome.Unavailable : PostgresReadOutcome.Read,
                server,
                reader.Window,
                view,
                pending,
                null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A store that failed, or took longer than QueryTimeout (the reader's own cut-off, an OperationCanceledException that is
            // not the caller's).
            return new ConnectionHistoryRead(
                exception is OperationCanceledException or TimeoutException ? PostgresReadOutcome.TimedOut : PostgresReadOutcome.Failed,
                server,
                reader.Window,
                null,
                0,
                exception);
        }
    }
}
