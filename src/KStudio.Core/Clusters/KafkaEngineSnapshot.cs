namespace KStudio.Core.Clusters;

/// <summary>
/// The engine options' values as they were when the registry validated them (decision 2.5, REVIEW-ENG-A 8). The options are mutable, so
/// the engine reads <see cref="RequestTimeout" />, <see cref="GroupIdPrefix" />, <see cref="AdminIdleTimeout" /> and the limits from here
/// and never from the options: a write after validation reaches nothing, so no bound can be skipped by it.
/// </summary>
/// <remarks>
/// <see cref="Limits" /> is a private copy that only the registry has made and nothing writes; the limits type is the public options class,
/// so the copy is read-only by convention and by <c>EngineOptionsSnapshotTests</c>, not by its type.
/// </remarks>
internal sealed class KafkaEngineSnapshot
{
    private KafkaEngineSnapshot(KafkaEngineOptions options)
    {
        GroupIdPrefix = options.GroupIdPrefix;
        RequestTimeout = options.RequestTimeout;
        AdminIdleTimeout = options.AdminIdleTimeout;
        Limits = options.Limits.Copy();
    }

    /// <summary>The prefix of every scan consumer's group id.</summary>
    public string GroupIdPrefix { get; }

    /// <summary>How long one request to a cluster may take.</summary>
    public TimeSpan RequestTimeout { get; }

    /// <summary>How long an admin client may sit unused.</summary>
    public TimeSpan AdminIdleTimeout { get; }

    /// <summary>What a scan may cost; a copy no one writes.</summary>
    public KafkaScanLimits Limits { get; }

    /// <summary>Copies the options' values. The caller has validated them; the registry is the only caller outside tests.</summary>
    /// <param name="options">The validated options.</param>
    public static KafkaEngineSnapshot Take(KafkaEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new KafkaEngineSnapshot(options);
    }
}
