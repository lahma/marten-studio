using System.Collections;
using System.Reflection;

using KStudio.Core.Clusters;

using Wolverine.Kafka.Internals;

namespace KStudio.Internal;

/// <summary>One broker-per-tenant registration whose child transport has its cluster configured.</summary>
/// <param name="TenantId">The tenant's id.</param>
/// <param name="Transport">The tenant's child transport: its configuration is the parent's, re-pointed at the tenant's own cluster.</param>
internal sealed record WolverineProbedTenant(string TenantId, KafkaTransport Transport);

/// <summary>What the probe found for one transport.</summary>
/// <param name="Tenants">The tenants that are ready, ordinal by id.</param>
/// <param name="Notes">What was not listed and why.</param>
internal sealed record WolverineTenantProbeResult(IReadOnlyList<WolverineProbedTenant> Tenants, IReadOnlyList<KafkaSourceNote> Notes)
{
    /// <summary>No tenants and no notes.</summary>
    public static WolverineTenantProbeResult None { get; } = new([], []);
}

/// <summary>
/// The only reflection in KStudio.Wolverine (D43, decision 5.4). Wolverine 6.61 keeps its broker-per-tenant registrations internal
/// (<c>KafkaTransport.Tenants</c> and <c>KafkaTenant</c> are <c>internal</c>; the public surface can only write them), so a tenant's cluster is
/// reached through exactly three members: <c>KafkaTransport.Tenants</c>, <c>KafkaTenant.TenantId</c> and <c>KafkaTenant.Transport</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It fails closed.</b> A shape that is not the one read here - the property missing, the cache not enumerable, an item of another type, a
/// member of another type, a getter that throws - gives no tenant cluster for that transport and one note. It never falls back to the parent's
/// cluster as the tenant's, which would show one tenant's data under another's name and ask the policy about the wrong tenant.
/// </para>
/// <para>
/// <b>It lists a tenant only when it is ready.</b> A tenant's child transport has no configuration until Wolverine's runtime has connected the
/// parent (<c>KafkaTenant.Compile</c>), so a registry built before that sees tenants it cannot reach and says so, once.
/// </para>
/// <para>It reads; it calls no method of Wolverine's and writes nothing. A tenant id the note names is never written to the log (event 9677).</para>
/// </remarks>
internal static class WolverineTenantProbe
{
    /// <summary>The cache of tenants on <c>KafkaTransport</c> (internal, an instance property).</summary>
    internal const string TenantsMember = "Tenants";

    /// <summary>A tenant's id (a public property of the internal <c>KafkaTenant</c>).</summary>
    internal const string TenantIdMember = "TenantId";

    /// <summary>A tenant's child transport (a public property of the internal <c>KafkaTenant</c>).</summary>
    internal const string TransportMember = "Transport";

    /// <summary>The full name of the one type whose members are read.</summary>
    internal const string TenantTypeName = "Wolverine.Kafka.Internals.KafkaTenant";

    /// <summary>The most tenants one transport lists.</summary>
    internal const int MaxTenants = 200;

    private const string ShapeReason = "this Wolverine version does not expose them as KStudio expects";

    /// <summary>Reads the tenants of <paramref name="transport" />.</summary>
    /// <param name="transport">A Kafka transport of the application's Wolverine configuration.</param>
    public static WolverineTenantProbeResult Probe(KafkaTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        try
        {
            PropertyInfo? property = typeof(KafkaTransport).GetProperty(TenantsMember, BindingFlags.Instance | BindingFlags.NonPublic);

            if (property is null)
            {
                return ShapeMismatch();
            }

            object? cache = property.GetValue(transport);

            return cache is IEnumerable tenants ? Read(tenants) : ShapeMismatch();
        }
        catch (Exception)
        {
            return ShapeMismatch();
        }
    }

    /// <summary>Reads the tenants out of the cache's items, each checked for its type and its members.</summary>
    /// <param name="cache">What <c>KafkaTransport.Tenants</c> returned.</param>
    /// <param name="tenantTypeName">The full name an item's type must have; only the tests name another, to give the probe a shape Wolverine 6.61 does not have.</param>
    internal static WolverineTenantProbeResult Read(IEnumerable cache, string tenantTypeName = TenantTypeName)
    {
        ArgumentNullException.ThrowIfNull(cache);

        List<WolverineProbedTenant> ready = [];
        List<KafkaSourceNote> notes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        try
        {
            // Copied inside the try: the cache is Wolverine's own, and a getter or an enumerator that throws is a shape mismatch.
            List<object?> items = [];

            foreach (object? item in cache)
            {
                items.Add(item);
            }

            foreach (object? item in items)
            {
                if (item is null || !string.Equals(item.GetType().FullName, tenantTypeName, StringComparison.Ordinal))
                {
                    return ShapeMismatch();
                }

                Type type = item.GetType();
                string? id = type.GetProperty(TenantIdMember, BindingFlags.Instance | BindingFlags.Public)?.GetValue(item) as string;

                if (type.GetProperty(TransportMember, BindingFlags.Instance | BindingFlags.Public)?.GetValue(item) is not KafkaTransport tenantTransport)
                {
                    return ShapeMismatch();
                }

                if (!KafkaClusterKey.IsValidTenantId(id))
                {
                    notes.Add(new KafkaSourceNote(
                        "a tenant cluster",
                        "its id is one KStudio cannot address",
                        "A tenant's cluster was not listed: its id is one KStudio cannot address."));
                    continue;
                }

                if (!seen.Add(id))
                {
                    continue;
                }

                if (!HasBootstrapServers(tenantTransport))
                {
                    notes.Add(new KafkaSourceNote(
                        "a tenant cluster",
                        "Wolverine had not connected it",
                        "Tenant " + id + "'s cluster is not listed: Wolverine had not connected it when KStudio read the configuration after the application started."));
                    continue;
                }

                ready.Add(new WolverineProbedTenant(id, tenantTransport));
            }
        }
        catch (Exception)
        {
            return ShapeMismatch();
        }

        // Wolverine keeps its tenants in a dictionary, whose order is not the order they were added in: ordinal by id, so the answer is the same every time.
        ready.Sort(static (a, b) => string.CompareOrdinal(a.TenantId, b.TenantId));

        if (ready.Count > MaxTenants)
        {
            int skipped = ready.Count - MaxTenants;
            ready.RemoveRange(MaxTenants, skipped);
            notes.Add(new KafkaSourceNote(
                "tenant clusters",
                "there are more than " + MaxTenants + " of them",
                "Only the first " + MaxTenants + " tenant clusters of a transport, by id, are listed; " + skipped + " more were not."));
        }

        return new WolverineTenantProbeResult(ready, notes);
    }

    private static bool HasBootstrapServers(KafkaTransport transport) =>
        !string.IsNullOrWhiteSpace(transport.AdminClientConfig.BootstrapServers) || !string.IsNullOrWhiteSpace(transport.ConsumerConfig.BootstrapServers);

    private static WolverineTenantProbeResult ShapeMismatch() =>
        new(
            [],
            [
                new KafkaSourceNote(
                    "tenant clusters",
                    ShapeReason,
                    "Wolverine's broker-per-tenant clusters are not listed: " + ShapeReason + "."),
            ]);
}
