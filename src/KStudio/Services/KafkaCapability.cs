namespace KStudio.Services;

/// <summary>
/// KStudio's capabilities as an enum, one member per <see cref="KafkaStudioCapabilities" /> flag and named identically, so that
/// <c>ToString()</c> is the name the audit, the policy and the chip use (D35).
/// </summary>
internal enum KafkaCapability
{
    /// <summary>Run scans, and read keys, timestamps, headers and CloudEvents attributes.</summary>
    SearchRecords,

    /// <summary>Read record values: the value in the detail and on the record page, value and JSON-path searches, compare and copy-as.</summary>
    ReadPayloads,

    /// <summary>Save the records a search or a live tail holds, or one record or its value, as a file.</summary>
    ExportRecords,

    /// <summary>Follow topics as records arrive.</summary>
    LiveTail,
}
