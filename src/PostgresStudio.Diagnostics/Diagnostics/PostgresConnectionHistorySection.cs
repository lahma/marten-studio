namespace PostgresStudio;

/// <summary>
/// The connection history of the database's server (issue #47, 1.0.1): the window it covers, the peak of client connections against
/// the client limit and when, and the breaks in it. Numbers are database-wide; the peak by application is Activity-grade (another
/// application's name). It is the summary Database › Sessions › Holders shows, from the same samples and the same arithmetic.
/// </summary>
/// <remarks>
/// <see cref="PostgresReadOutcome.NotApplicable" /> without a connection history registration (<c>AddPostgresConnectionHistory()</c>);
/// <see cref="PostgresReadOutcome.Unavailable" /> while the window holds no sample yet; <see cref="PostgresReadOutcome.TimedOut" />
/// and <see cref="PostgresReadOutcome.Failed" /> when the history's store did not answer. When it was not read the other properties
/// keep their defaults.
/// </remarks>
public sealed record PostgresConnectionHistorySection
{
    /// <summary>Whether it was read; when not, the other properties keep their defaults.</summary>
    public PostgresReadOutcome Outcome { get; init; }

    /// <summary>Why not, or what limits it.</summary>
    public string? Reason { get; init; }

    /// <summary>The server the history is of, <c>host:port</c> as the connection history keys it; never a credential.</summary>
    public string Server { get; init; } = "";

    /// <summary>How far back the history reaches: its <c>Retention</c>.</summary>
    public TimeSpan Window { get; init; }

    /// <summary>The first sample's time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>The last sample's time.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>How many samples the window holds.</summary>
    public int Samples { get; init; }

    /// <summary>
    /// How many samples this process has taken for the server and not yet stored (the store did not take them). Zero when the
    /// store keeps up.
    /// </summary>
    public int PendingSamples { get; init; }

    /// <summary>How many times the answering instance changed between two samples: a failover or a restart, a break in the series.</summary>
    public int InstanceChanges { get; init; }

    /// <summary>The most client connections one sample counted, every database's.</summary>
    public long Peak { get; init; }

    /// <summary>When that was, the first time it was reached.</summary>
    public DateTimeOffset? PeakAt { get; init; }

    /// <summary>The client limit at the peak: <c>max_connections</c> less what is reserved.</summary>
    public int ClientLimit { get; init; }

    /// <summary>The client limit at the latest sample - it differs from <see cref="ClientLimit" /> after the cluster was resized.</summary>
    public int ClientLimitNow { get; init; }

    /// <summary>
    /// Each application's own peak, most first: at most twenty. Only with <see cref="PostgresDiagnosticsRequest.IncludeActivity" />;
    /// otherwise empty, and the snapshot's <see cref="PostgresDiagnosticsSnapshot.Withheld" /> says so.
    /// </summary>
    public IReadOnlyList<PostgresConnectionPeak> ByApplication { get; init; } = [];
}
