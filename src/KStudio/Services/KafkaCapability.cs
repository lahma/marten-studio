namespace KStudio.Services;

/// <summary>
/// KStudio's capabilities as an enum, one member per <see cref="KafkaStudioCapabilities" /> flag and named identically, so that
/// <c>ToString()</c> is the name the audit, the policy and the chip use (D35).
/// </summary>
internal enum KafkaCapability
{
    /// <summary>Run scans, and read keys, timestamps, headers and CloudEvents attributes.</summary>
    SearchRecords,
}
