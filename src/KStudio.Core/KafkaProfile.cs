namespace KStudio;

/// <summary>
/// What kind of cluster this is, for KStudio's limits (AGENTS.md D47). A profile only lowers limits and forbids; it never grants a capability
/// and never raises a limit above the host's. A cluster with no profile uses the host's limits as they are.
/// </summary>
public enum KafkaProfile
{
    /// <summary>The host's limits, unchanged: a developer's own cluster.</summary>
    Development,

    /// <summary>Every per-scan limit capped at KStudio's defaults, whatever the host raised them to: a shared test cluster.</summary>
    Test,

    /// <summary>Lower caps, a slower byte rate and a smaller hourly budget on this cluster, and no export and no live tail even when granted.</summary>
    Production,
}
