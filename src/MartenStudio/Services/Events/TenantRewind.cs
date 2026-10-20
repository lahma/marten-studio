using JasperFx.Events.Projections;

namespace MartenStudio.Services.Events;

/// <summary>
/// Whether the daemon in this process runs a tenant's agent of a projection - the condition the dead-letter page
/// offers a per-tenant Rewind subscription on, and that <c>EventDataService</c> refuses one without - and whether a tenant's
/// agents can be addressed by a per-tenant rewind at all.
/// </summary>
/// <remarks>
/// <para>
/// The page has the daemon's agents as their shard identities (<c>DaemonStatus.Agents</c>); the service has the agents
/// themselves (<c>EventDataService.TenantAgentOf</c>). Both compare the same way: the projection name as Marten compares
/// it, ignoring case, and the tenant ordinally. An identity is parsed with <c>ShardName.TryParse</c>, never split by
/// hand: <c>Orders:All:acme</c>'s last segment is a tenant and <c>Orders:V2:All</c>'s is a shard key, and only the
/// grammar tells them apart.
/// </para>
/// <para>
/// <b>And it has to read back.</b> Marten 9.51 resolves a per-tenant rewind's shard by parsing the identity the daemon
/// hands it (<c>DocumentStore.findTenantShard</c>), after the daemon has already hard-stopped the tenant's agents. A
/// tenant id with a <c>:</c> in it makes <c>Orders:All:a:b</c>, which does not parse, so that rewind would stop the agents
/// and fail. <see cref="IsAddressable" /> and <see cref="RoundTrips" /> are how the page and the service refuse it first.
/// </para>
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

    /// <summary>
    /// Whether <paramref name="name" />'s identity parses back to the same projection, shard key and tenant - which is what
    /// Marten does with it during a per-tenant rewind.
    /// </summary>
    public static bool RoundTrips(ShardName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ShardName.TryParse(name.Identity, out ShardName? parsed)
            && parsed is not null
            && string.Equals(parsed.Name, name.Name, StringComparison.Ordinal)
            && string.Equals(parsed.ShardKey, name.ShardKey, StringComparison.Ordinal)
            && string.Equals(parsed.TenantId, name.TenantId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a tenant's agents can be addressed by a per-tenant rewind: whether an agent identity carrying
    /// <paramref name="tenantId" /> reads back as that tenant's (<see cref="RoundTrips" />).
    /// </summary>
    public static bool IsAddressable(string? tenantId) =>
        !string.IsNullOrEmpty(tenantId) && RoundTrips(new ShardName("Projection", ShardName.All, 1, tenantId));
}
