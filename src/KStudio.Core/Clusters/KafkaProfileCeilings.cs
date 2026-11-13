namespace KStudio.Core.Clusters;

/// <summary>
/// The ceilings of a cluster's profile (AGENTS.md D47, D-M5 decisions 4.3 and 4.4): data and one pure function. Every limit is
/// <c>min(host, ceiling)</c>, so a profile only lowers and never raises, and a cluster with no profile (and a <see cref="KafkaProfile.Development" />
/// one) gets the host's limits exactly. The table is copied into <c>docs/kstudio.md</c>.
/// </summary>
internal static class KafkaProfileCeilings
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    /// <summary>
    /// The properties of <see cref="KafkaScanLimits" /> that <see cref="Cap" /> decides, whether by a ceiling or by leaving them to the host. A
    /// test holds this list to the public properties by reflection, so a limit added later fails by name until it has a row here.
    /// </summary>
    public static IReadOnlyList<string> DecidedProperties { get; } =
    [
        nameof(KafkaScanLimits.MaxConcurrentScans),
        nameof(KafkaScanLimits.MaxConcurrentScansPerOwner),
        nameof(KafkaScanLimits.BytesPerSecond),
        nameof(KafkaScanLimits.OwnerBytesPerHour),
        nameof(KafkaScanLimits.MaxBytesPerScan),
        nameof(KafkaScanLimits.MaxRecordsPerScan),
        nameof(KafkaScanLimits.MaxScanDuration),
        nameof(KafkaScanLimits.MaxMatches),
        nameof(KafkaScanLimits.MaxMatchBytes),
        nameof(KafkaScanLimits.MaxDecodeBytes),
        nameof(KafkaScanLimits.ConfirmAboveBytes),
        nameof(KafkaScanLimits.ConfirmAboveRecords),
        nameof(KafkaScanLimits.QueueKilobytes),
        nameof(KafkaScanLimits.FetchBytes),
        nameof(KafkaScanLimits.CreateTimeMargin),
        nameof(KafkaScanLimits.PartitionStallTimeout),
        nameof(KafkaScanLimits.MaxExportBytes),
        nameof(KafkaScanLimits.MaxTailMatches),
        nameof(KafkaScanLimits.TailPauseAfter),
    ];

    /// <summary>Whether a cluster with this profile never exports, whatever the capability says.</summary>
    /// <param name="profile">The cluster's profile, or <see langword="null" /> for none.</param>
    public static bool ForbidsExport(KafkaProfile? profile) => profile == KafkaProfile.Production;

    /// <summary>Whether a cluster with this profile never runs a live tail, whatever the capability says.</summary>
    /// <param name="profile">The cluster's profile, or <see langword="null" /> for none.</param>
    public static bool ForbidsTail(KafkaProfile? profile) => profile == KafkaProfile.Production;

    /// <summary>
    /// The host's limits capped by the profile: a new object, with the host's values for a cluster with no profile or a <see cref="KafkaProfile.Development" />
    /// one. The caller passes a copy it owns (the snapshot hands one out on every read), and the result passes <c>Validate</c> whenever the host's did.
    /// </summary>
    /// <param name="profile">The cluster's profile, or <see langword="null" /> for none.</param>
    /// <param name="host">The host's limits.</param>
    public static KafkaScanLimits Cap(KafkaProfile? profile, KafkaScanLimits host)
    {
        ArgumentNullException.ThrowIfNull(host);

        KafkaScanLimits capped = host.Copy();

        switch (profile)
        {
            case KafkaProfile.Test:
                Apply(capped, 256 * MiB, 5_000_000, TimeSpan.FromMinutes(2), 64 * MiB, 1_000_000, 10 * MiB, 2 * GiB);
                break;

            case KafkaProfile.Production:
                Apply(capped, 64 * MiB, 1_000_000, TimeSpan.FromMinutes(1), 16 * MiB, 250_000, 5 * MiB, 512 * MiB);
                break;

            default:
                return capped;
        }

        // The same for both: matches, the match's bytes and the decode cap stay at KStudio's defaults.
        capped.MaxMatches = Math.Min(capped.MaxMatches, 500);
        capped.MaxMatchBytes = Math.Min(capped.MaxMatchBytes, 64 * 1024);
        capped.MaxDecodeBytes = Math.Min(capped.MaxDecodeBytes, 1024 * 1024);

        // Export and tail are forbidden on a Production cluster, which the services enforce; the values below are Test's ceilings, which
        // are the defaults, and for Production they are never used.
        capped.MaxExportBytes = Math.Min(capped.MaxExportBytes, 32 * MiB);
        capped.MaxTailMatches = Math.Min(capped.MaxTailMatches, 200);
        capped.TailPauseAfter = TimeSpan.FromTicks(Math.Min(capped.TailPauseAfter.Ticks, TimeSpan.FromMinutes(5).Ticks));

        // The confirmations sit at or below the budgets (Validate).
        capped.ConfirmAboveBytes = Math.Min(capped.ConfirmAboveBytes, capped.MaxBytesPerScan);
        capped.ConfirmAboveRecords = Math.Min(capped.ConfirmAboveRecords, capped.MaxRecordsPerScan);

        // MaxConcurrentScans, MaxConcurrentScansPerOwner, QueueKilobytes, FetchBytes, CreateTimeMargin and PartitionStallTimeout guard the
        // process's memory or the scan's correctness and stay the host's, whatever the cluster.
        return capped;
    }

    private static void Apply(
        KafkaScanLimits limits,
        long maxBytes,
        long maxRecords,
        TimeSpan maxDuration,
        long confirmBytes,
        long confirmRecords,
        long bytesPerSecond,
        long ownerBytesPerHour)
    {
        limits.MaxBytesPerScan = Math.Min(limits.MaxBytesPerScan, maxBytes);
        limits.MaxRecordsPerScan = Math.Min(limits.MaxRecordsPerScan, maxRecords);
        limits.MaxScanDuration = TimeSpan.FromTicks(Math.Min(limits.MaxScanDuration.Ticks, maxDuration.Ticks));
        limits.ConfirmAboveBytes = Math.Min(limits.ConfirmAboveBytes, confirmBytes);
        limits.ConfirmAboveRecords = Math.Min(limits.ConfirmAboveRecords, confirmRecords);
        limits.BytesPerSecond = Math.Min(limits.BytesPerSecond, bytesPerSecond);
        limits.OwnerBytesPerHour = Math.Min(limits.OwnerBytesPerHour, ownerBytesPerHour);
    }
}
