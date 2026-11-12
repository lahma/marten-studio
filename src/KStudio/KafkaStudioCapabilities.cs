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

    /// <summary>No capability: the clusters and topics can be described and nothing is read.</summary>
    public static KafkaStudioCapabilities None() => new();

    /// <summary>Every capability on.</summary>
    public static KafkaStudioCapabilities All() => new() { SearchRecords = true, ReadPayloads = true };
}
