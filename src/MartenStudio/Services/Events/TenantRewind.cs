using JasperFx.Events.Projections;

namespace MartenStudio.Services.Events;

/// <summary>
/// Whether the daemon in this process runs a tenant's agent of a projection - the condition the dead-letter page
/// offers a per-tenant Rewind subscription on, and that <c>EventDataService</c> refuses one without.
/// </summary>
/// <remarks>
/// The page has the daemon's agents as their shard identities (<c>DaemonStatus.Agents</c>); the service has the agents
/// themselves (<c>EventDataService.TenantAgentOf</c>). Both compare the same way: the projection name as Marten compares
/// it, ignoring case, and the tenant ordinally. An identity is parsed with <c>ShardName.TryParse</c>, never split by
/// hand: <c>Orders:All:acme</c>'s last segment is a tenant and <c>Orders:V2:All</c>'s is a shard key, and only the
/// grammar tells them apart.
/// </remarks>
internal static class TenantRewind
{
    /// <summary>Whether any of <paramref name="agentIdentities" /> is <paramref name="projectionName" />'s agent for <paramref name="tenantId" />.</summary>
    /// <param name="agentIdentities">The shard identities of the agents the daemon here runs.</param>
    /// <param name="projectionName">The projection a dead letter names.</param>
    /// <param name="tenantId">The dead letter's tenant.</param>
    public static bool RunsAgent(IEnumerable<string> agentIdentities, string projectionName, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(agentIdentities);

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(projectionName))
        {
            return false;
        }

        foreach (string identity in agentIdentities)
        {
            if (ShardName.TryParse(identity, out ShardName? name)
                && name is { TenantId: { } tenant }
                && string.Equals(tenant, tenantId, StringComparison.Ordinal)
                && string.Equals(name.Name, projectionName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
