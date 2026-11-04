namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #57: the sentences for a connection history that holds no sample of a server yet while the host's
/// <see cref="PostgresConnectionHistoryOptions.ShouldSample" /> decides who samples - one spelling, for Holders and for the
/// diagnostics API's section, which must not disagree.
/// </summary>
internal static class ConnectionHistorySamplingText
{
    /// <summary>What a sampler never does, said wherever a sample is promised: a server only a dynamic source names is not sampled.</summary>
    public const string DynamicDatabases = "A server only a source of dynamic databases names is not sampled.";

    /// <summary><see cref="ConnectionHistorySampling.Chosen" />: this process is the sampler now.</summary>
    public const string Chosen =
        "The connection history holds no sample of this server yet: the first is taken at the next interval, now that ShouldSample has chosen this process to sample. "
        + DynamicDatabases;

    /// <summary><see cref="ConnectionHistorySampling.NotChosen" />: another process samples, or none has been chosen yet.</summary>
    public const string NotChosen =
        "The connection history holds no sample of this server yet: another process samples (ShouldSample chooses it), and its samples appear here once stored. "
        + DynamicDatabases;

    /// <summary>
    /// <see cref="ConnectionHistorySampling.NotChosenInMemory" />: another process samples, but this one keeps the default in-memory
    /// history, which that process's samples never reach.
    /// </summary>
    public const string NotChosenInMemory =
        "The connection history holds no sample of this server yet: another process samples (ShouldSample chooses it), but this process keeps the history in its own memory, "
        + "so that process's samples never appear here. Register a shared IPostgresConnectionHistoryStore to see them.";
}
