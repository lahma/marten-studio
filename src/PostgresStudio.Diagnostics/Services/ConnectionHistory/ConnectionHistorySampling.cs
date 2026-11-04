namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #57: whether this process samples now, as the sentence for a history without a sample of a server has to say it. Only the
/// host's <see cref="PostgresConnectionHistoryOptions.ShouldSample" /> makes it more than "always".
/// </summary>
internal enum ConnectionHistorySampling
{
    /// <summary>No <c>ShouldSample</c> (or no sampler in this process): the first sample is taken a moment after the application starts.</summary>
    Always = 0,

    /// <summary><c>ShouldSample</c> is set and its last answer was yes: this process is the sampler now.</summary>
    Chosen = 1,

    /// <summary><c>ShouldSample</c> is set and its last answer was no, or it has not answered yet: another process samples, if any does.</summary>
    NotChosen = 2,

    /// <summary>
    /// <c>ShouldSample</c> is set, this process is not chosen, and its store is the default in-memory one: the process that samples
    /// writes somewhere this process never reads.
    /// </summary>
    NotChosenInMemory = 3,
}
