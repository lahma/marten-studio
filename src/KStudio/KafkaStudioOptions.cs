namespace KStudio;

/// <summary>
/// KStudio's options (AGENTS.md D34, D35): the clusters and topics it may read and what a scan may cost, from its engine's
/// <see cref="KafkaEngineOptions" />, and the policies and capabilities that decide who may use them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chaining.</b> The cluster and topic methods (<c>AddCluster</c>, <c>Topic</c>) are the engine's and return
/// <see cref="KafkaEngineOptions" />. Chaining from them works; KStudio's own settings are properties, so they are set in the same
/// callback, and the fluent API is not duplicated here.
/// </para>
/// <para>
/// <b>Who gets in</b> is the host studio's own <c>AuthorizationPolicy</c> (or the caller's <c>RequireAuthorization()</c> on the
/// studio's map call): the Kafka pages are endpoints of that mapping. <b>Which cluster and topic</b> is
/// <see cref="ClusterAuthorizationPolicy" />, and <b>each capability's use</b> is <see cref="CapabilityAuthorizationPolicy" />. Both
/// are asked about a <see cref="KafkaTopicResource" />; neither falls back to the host studio's policies, whose handlers are written
/// for other resources.
/// </para>
/// </remarks>
public sealed class KafkaStudioOptions : KafkaEngineOptions
{
    /// <summary>
    /// The name of the authorization policy asked about every cluster and topic a call reaches, with <see cref="KafkaTopicResource.Capability" />
    /// <see langword="null" />: once at cluster level (<see cref="KafkaTopicResource.Topic" /> <see langword="null" />) and once for each
    /// topic. A topic it refuses is absent from every list, and a request that names one is refused whole. <see langword="null" /> or
    /// blank asks nothing.
    /// </summary>
    public string? ClusterAuthorizationPolicy { get; set; }

    /// <summary>
    /// The name of the authorization policy asked for each use of a capability, with the capability named and once per topic. It falls back to
    /// <see cref="ClusterAuthorizationPolicy" /> when blank.
    /// </summary>
    public string? CapabilityAuthorizationPolicy { get; set; }

    /// <summary>What KStudio may read beyond metadata. Every capability is off until the host sets it.</summary>
    public KafkaStudioCapabilities Capabilities { get; set; } = new();
}
