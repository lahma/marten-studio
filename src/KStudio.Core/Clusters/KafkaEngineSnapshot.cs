namespace KStudio.Core.Clusters;

/// <summary>
/// The engine options' values as they were when the registry validated them (decision 2.5, REVIEW-ENG-A 8). The options are mutable, so
/// the engine reads <see cref="RequestTimeout" />, <see cref="GroupIdPrefix" />, <see cref="AdminIdleTimeout" /> and the limits from here
/// and never from the options: a write after validation reaches nothing, so no bound can be skipped by it.
/// </summary>
/// <remarks>
/// The limits type is the public options class and so writable: the snapshot keeps its own copy, private, and <see cref="Limits" /> hands
/// out a fresh copy on every read. A caller that writes to what it got (a governor narrowing one profile's bound, say) changes its own
/// copy and nothing else, and no write reaches a scan that is running or skips the validation (REVIEW-ENG-A-FIX 5,
/// <c>EngineOptionsSnapshotTests</c>).
/// </remarks>
internal sealed class KafkaEngineSnapshot
{
    private readonly KafkaScanLimits limits;

    private KafkaEngineSnapshot(KafkaEngineOptions options)
    {
        GroupIdPrefix = options.GroupIdPrefix;
        RequestTimeout = options.RequestTimeout;
        AdminIdleTimeout = options.AdminIdleTimeout;
        limits = options.Limits.Copy();
    }

    /// <summary>The prefix of every scan consumer's group id.</summary>
    public string GroupIdPrefix { get; }

    /// <summary>How long one request to a cluster may take.</summary>
    public TimeSpan RequestTimeout { get; }

    /// <summary>How long an admin client may sit unused.</summary>
    public TimeSpan AdminIdleTimeout { get; }

    /// <summary>What a scan may cost: a new copy on every read, so a write to it reaches no one else.</summary>
    public KafkaScanLimits Limits => limits.Copy();

    /// <summary>Copies the options' values. The caller has validated them; the registry is the only caller outside tests.</summary>
    /// <param name="options">The validated options.</param>
    public static KafkaEngineSnapshot Take(KafkaEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new KafkaEngineSnapshot(options);
    }
}
