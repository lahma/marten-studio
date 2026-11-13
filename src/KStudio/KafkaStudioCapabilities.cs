namespace KStudio;

/// <summary>
/// What KStudio may read beyond metadata (AGENTS.md D35). Every flag defaults to <see langword="false" /> (D4); describing clusters,
/// topics, partitions and groups needs none of them (K5). The host studio's <c>ReadOnly</c> turns every flag off at once.
/// </summary>
public sealed class KafkaStudioCapabilities
{
    /// <summary>Run scans, and read keys, timestamps, headers and CloudEvents attributes.</summary>
    public bool SearchRecords { get; set; }

    /// <summary>
    /// Read record values: the value in the record detail and on the record page, value and JSON-path searches, compare and
    /// "copy as" of a value. Needs <see cref="SearchRecords" />.
    /// </summary>
    public bool ReadPayloads { get; set; }

    /// <summary>
    /// Save records as a file: the records a search or a live tail holds, and one record or its value from the record page, as JSON Lines, CSV or
    /// the value alone, redacted as the page shows them. Values are included only where <see cref="ReadPayloads" /> lets the visitor read them.
    /// Needs <see cref="SearchRecords" />. A cluster whose profile is <c>Production</c> never exports.
    /// </summary>
    public bool ExportRecords { get; set; }

    /// <summary>
    /// Follow topics as records arrive, from the end of each partition. A tail holds a scan slot and reads against the visitor's hourly budget while
    /// it runs, and pauses itself. Needs <see cref="SearchRecords" />. A cluster whose profile is <c>Production</c> never tails.
    /// </summary>
    public bool LiveTail { get; set; }

    /// <summary>No capability: the clusters and topics can be described and nothing is read.</summary>
    public static KafkaStudioCapabilities None() => new();

    /// <summary>Every capability on, including those a later version adds. Still subject to the host studio's ReadOnly.</summary>
    public static KafkaStudioCapabilities All() => new() { SearchRecords = true, ReadPayloads = true, ExportRecords = true, LiveTail = true };
}
