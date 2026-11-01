namespace PostgresStudio.Services.Health;

/// <summary>The rule for the gate a finding needs, applied by every reader: the Health page's visitor gate and the host gate (D29, D31).</summary>
internal static class HealthGateRule
{
    /// <summary>
    /// The gate a reader must pass to be shown <paramref name="finding" /> at all (D29): its check's, or - for a finding
    /// about one tenant, which names it - <see cref="HealthGate.Activity" /> at least. The one rule the Health page's visitor
    /// gate and the diagnostics API's host gate (<c>HostGate</c>, D31) both apply; a visitor also needs that tenant's store
    /// policy, and the host needs nothing more.
    /// </summary>
    /// <param name="finding">The finding.</param>
    public static HealthGate Required(HealthFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.TenantId is not null && finding.Gate != HealthGate.Activity ? HealthGate.Activity : finding.Gate;
    }
}
