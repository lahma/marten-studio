namespace PostgresStudio;

/// <summary>
/// Compute capacity that SQL cannot see, supplied by the host: Aurora Serverless v2's ACU from CloudWatch, for example.
/// The studio takes no cloud SDK dependency. Register one in the container; the studio finds it there.
/// </summary>
/// <remarks>
/// <para>
/// Asked at most once per <c>HealthInterval</c> per database and instance, one call at a time, and given five seconds.
/// An answer of null, a timeout or an exception means "capacity unknown" for that interval. An exception is logged, at
/// Warning once in ten minutes. Cache your own reads: CloudWatch calls cost money.
/// </para>
/// <para>New members are added only as default interface members.</para>
/// </remarks>
public interface IPostgresCapacitySource
{
    /// <summary>
    /// The capacity behind <paramref name="database" /> as <paramref name="server" /> answered, or null when this
    /// source does not know it (it is not Aurora, say).
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="server">The server and instance that answered, so a multi-instance cluster reads the right one.</param>
    /// <param name="cancellationToken">Cancelled after five seconds.</param>
    ValueTask<PostgresComputeCapacity?> GetCapacityAsync(
        PostgresDatabaseRef database,
        PostgresServerInfo server,
        CancellationToken cancellationToken);
}
