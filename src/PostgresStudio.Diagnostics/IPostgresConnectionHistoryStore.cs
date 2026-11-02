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
/// let <see cref="ReadAsync" />'s <c>since</c> bound the reads) and answer reads quickly. A failure is logged and the next
/// interval tries again; it never stops the sampler or a page.
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
