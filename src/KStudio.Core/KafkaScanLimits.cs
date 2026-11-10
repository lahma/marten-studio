using System.Globalization;

namespace KStudio;

/// <summary>
/// What one scan, one owner and the process may cost (AGENTS.md D41): data and validation only. A profile or a visitor can only
/// lower a limit; nothing raises one above the value the host set here.
/// </summary>
/// <remarks>
/// The memory a process holds for scans is about $S \cdot (Q + F + m \cdot c)$ with the concurrent scans
/// <see cref="MaxConcurrentScans" />, the prefetch queue <see cref="QueueKilobytes" />, the fetch size <see cref="FetchBytes" />,
/// the kept matches <see cref="MaxMatches" /> and the bytes kept per match <see cref="MaxMatchBytes" />.
/// </remarks>
public sealed class KafkaScanLimits
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    /// <summary>How many scans run at once in this process (1 to 8). Further starts are refused, never queued. Default 2.</summary>
    public int MaxConcurrentScans { get; set; } = 2;

    /// <summary>How many scans one owner (a visitor) may run at once (1 to <see cref="MaxConcurrentScans" />). Default 1.</summary>
    public int MaxConcurrentScansPerOwner { get; set; } = 1;

    /// <summary>The byte rate every scan in this process shares (64 KiB to 1 GiB per second). Default 10 MiB/s.</summary>
    public long BytesPerSecond { get; set; } = 10 * MiB;

    /// <summary>The bytes one owner may read in any rolling hour (1 MiB to 1 TiB). Default 2 GiB.</summary>
    public long OwnerBytesPerHour { get; set; } = 2 * GiB;

    /// <summary>The most bytes one scan reads (1 MiB to 64 GiB). Default 256 MiB.</summary>
    public long MaxBytesPerScan { get; set; } = 256 * MiB;

    /// <summary>The most records one scan reads (1,000 to 1,000,000,000). Default 5,000,000.</summary>
    public long MaxRecordsPerScan { get; set; } = 5_000_000;

    /// <summary>The longest one scan runs (5 seconds to 30 minutes). Default 2 minutes.</summary>
    public TimeSpan MaxScanDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The most matches one scan keeps (1 to 10,000). Default 500.</summary>
    public int MaxMatches { get; set; } = 500;

    /// <summary>The most bytes a kept match holds of a record's value (1 KiB to 1 MiB). Default 64 KiB.</summary>
    public int MaxMatchBytes { get; set; } = 64 * 1024;

    /// <summary>The largest value parsed for evaluation (64 KiB to 16 MiB). Default 1 MiB.</summary>
    public int MaxDecodeBytes { get; set; } = 1024 * 1024;

    /// <summary>A scan reading more bytes than this needs a confirmation (0 to <see cref="MaxBytesPerScan" />). Default 64 MiB.</summary>
    public long ConfirmAboveBytes { get; set; } = 64 * MiB;

    /// <summary>A scan reading more records than this needs a confirmation (0 to <see cref="MaxRecordsPerScan" />). Default 1,000,000.</summary>
    public long ConfirmAboveRecords { get; set; } = 1_000_000;

    /// <summary>The prefetch queue of one scan's consumer, in kilobytes (1,024 to 65,536). Default 8,192.</summary>
    public int QueueKilobytes { get; set; } = 8192;

    /// <summary>The fetch size of one scan's consumer, in bytes (1 MiB to 50 MiB). Default 8 MiB.</summary>
    public int FetchBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>How far past a window's end a <c>CreateTime</c> scan reads, for records stamped out of order (0 to 1 hour). Default 2 minutes.</summary>
    public TimeSpan CreateTimeMargin { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a partition may make no progress before it is reported stalled (1 second to 5 minutes). Default 15 seconds.</summary>
    public TimeSpan PartitionStallTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A copy of every value, for the engine's snapshot of the options.</summary>
    internal KafkaScanLimits Copy() => (KafkaScanLimits) MemberwiseClone();

    /// <summary>The problems with these limits, each naming its property; empty when they are all inside their bounds.</summary>
    /// <param name="prefix">What precedes a property name in a message, for example <c>Limits.</c>.</param>
    internal IReadOnlyList<string> Validate(string prefix = "Limits.")
    {
        List<string> problems = [];

        Check(problems, prefix, nameof(MaxConcurrentScans), MaxConcurrentScans, 1, 8);
        Check(problems, prefix, nameof(MaxConcurrentScansPerOwner), MaxConcurrentScansPerOwner, 1, Math.Max(1, MaxConcurrentScans));
        Check(problems, prefix, nameof(BytesPerSecond), BytesPerSecond, 64 * KiB, GiB);
        Check(problems, prefix, nameof(OwnerBytesPerHour), OwnerBytesPerHour, MiB, 1024 * GiB);
        Check(problems, prefix, nameof(MaxBytesPerScan), MaxBytesPerScan, MiB, 64 * GiB);
        Check(problems, prefix, nameof(MaxRecordsPerScan), MaxRecordsPerScan, 1_000, 1_000_000_000);
        Check(problems, prefix, nameof(MaxScanDuration), MaxScanDuration, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30));
        Check(problems, prefix, nameof(MaxMatches), MaxMatches, 1, 10_000);
        Check(problems, prefix, nameof(MaxMatchBytes), MaxMatchBytes, KiB, MiB);
        Check(problems, prefix, nameof(MaxDecodeBytes), MaxDecodeBytes, 64 * KiB, 16 * MiB);
        Check(problems, prefix, nameof(ConfirmAboveBytes), ConfirmAboveBytes, 0, MaxBytesPerScan);
        Check(problems, prefix, nameof(ConfirmAboveRecords), ConfirmAboveRecords, 0, MaxRecordsPerScan);
        Check(problems, prefix, nameof(QueueKilobytes), QueueKilobytes, 1_024, 65_536);
        Check(problems, prefix, nameof(FetchBytes), FetchBytes, MiB, 50 * MiB);
        Check(problems, prefix, nameof(CreateTimeMargin), CreateTimeMargin, TimeSpan.Zero, TimeSpan.FromHours(1));
        Check(problems, prefix, nameof(PartitionStallTimeout), PartitionStallTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));

        return problems;
    }

    private static void Check(List<string> problems, string prefix, string name, long value, long min, long max)
    {
        if (value < min || value > max)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}{name} must be between {min:N0} and {max:N0}, but is {value:N0}."));
        }
    }

    private static void Check(List<string> problems, string prefix, string name, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
        {
            problems.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}{name} must be between {min} and {max}, but is {value}."));
        }
    }
}
