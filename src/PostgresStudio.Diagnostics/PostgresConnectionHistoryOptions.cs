namespace PostgresStudio;

/// <summary>
/// The opt-in connection history (issue #43, <see cref="PostgresConnectionHistoryServiceCollectionExtensions.AddPostgresConnectionHistory" />):
/// a sample of each server's client connections by application every <see cref="Interval" />, so the question after a
/// <c>53300 too many clients</c> incident - what peaked, when, and who held the connections - has an answer once those connections
/// are gone.
/// </summary>
public sealed class PostgresConnectionHistoryOptions
{
    /// <summary>How often each server is sampled. Between fifteen seconds and one hour; a minute by default.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long this process's own memory keeps samples when no <see cref="IPostgresConnectionHistoryStore" /> is registered, and how
    /// far back the Holders page reads. Between one hour and ninety days; seven days by default.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The most applications a sample names, most connections first. Between 1 and 100; 20 by default.</summary>
    public int MaxApplications { get; set; } = 20;

    /// <summary>
    /// Whether this process samples now - asked before every round. A service running as several processes on one database
    /// answers it from the leader election or the advisory lock it already has, so one process samples for all of them
    /// (<c>pg_stat_activity</c> is server-wide); the studio takes no lock of its own. <see langword="null" />, the default, samples
    /// in every process. While it answers <see langword="false" /> the sampler holds no connection and takes no sample, and
    /// any samples it still holds from when it sampled are offered to the store, oldest first, at every round (older than an
    /// hour of intervals, they are dropped). A process that never samples registers
    /// <see cref="PostgresConnectionHistoryServiceCollectionExtensions.AddPostgresConnectionHistoryReader" /> instead.
    /// </summary>
    public Func<CancellationToken, ValueTask<bool>>? ShouldSample { get; set; }
}
