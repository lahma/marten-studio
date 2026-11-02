namespace PostgresStudio;

/// <summary>
/// Where the connection history keeps its samples (issue #43). Register an implementation in the container to keep them in a
/// table of your own - so the history outlives a deploy and every process of a service reads the same one; without one, the
/// samples are kept in this process's memory for <see cref="PostgresConnectionHistoryOptions.Retention" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>The studio issues no DDL.</b> A host store owns its table and its migration. A shape that holds everything a sample says:
/// </para>
/// <code>
/// create table connection_history (
///     taken_at     timestamptz not null,
///     server       text        not null,
///     instance     text,
///     connections  bigint      not null,
///     client_limit int         not null,
///     applications jsonb       not null,   -- [{ "name": "orders-api", "total": 12, "active": 3, "idle": 9 }, ...]
///     primary key (server, taken_at));
/// </code>
/// <para>
/// Both calls are made from the sampler's background loop and from the Holders page; a store should keep its own retention (or
/// let <see cref="ReadAsync" />'s <c>since</c> bound the reads) and answer reads quickly. A failure never stops the sampler or a
/// page: it is logged, and the sample is kept in the sampling process, per server, for up to an hour of samples at the
/// configured <c>Interval</c>. Before the next sample the sampler appends what it holds, oldest first, one <see cref="AppendAsync" />
/// each, and stops at the first that fails; past the hour the oldest is dropped, and a process that stops loses what it held.
/// </para>
/// <para>
/// <b>Reads are incremental.</b> The studio asks <see cref="ReadAsync" /> for the whole window the first time (and after it dropped
/// a server it had not read for ten minutes, or more than 64 servers), and then only for the samples since its last read of the
/// server less five minutes - de-duplicated by <see cref="PostgresConnectionSample.At" />, so a store may return a sample twice -
/// with a full read every ten minutes to find samples that landed late. Serve <c>since</c> with an index on
/// <c>(server, taken_at)</c>, which the example table's primary key already gives.
/// </para>
/// </remarks>
public interface IPostgresConnectionHistoryStore
{
    /// <summary>Keeps one sample.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken);

    /// <summary>The samples of <paramref name="server" /> taken at or after <paramref name="since" />, oldest first.</summary>
    /// <param name="server">The server, as <see cref="PostgresConnectionSample.Server" /> names it.</param>
    /// <param name="since">The earliest sample wanted.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken);
}
