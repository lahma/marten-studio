namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #57: the sentences for a connection history that holds no sample of a server yet while the host's
/// <see cref="PostgresConnectionHistoryOptions.ShouldSample" /> decides who samples - one spelling, for Holders and for the
/// diagnostics API's section, which must not disagree.
/// </summary>
internal static class ConnectionHistorySamplingText
{
    /// <summary><see cref="ConnectionHistorySampling.Chosen" />: this process is the sampler now.</summary>
    public const string Chosen =
        "The connection history holds no sample of this server yet: the first is taken at the next interval, now that ShouldSample has chosen this process to sample.";

    /// <summary><see cref="ConnectionHistorySampling.NotChosen" />: another process samples, or none has been chosen yet.</summary>
    public const string NotChosen =
        "The connection history holds no sample of this server yet: another process samples (ShouldSample chooses it), and its samples appear here once stored.";
}
