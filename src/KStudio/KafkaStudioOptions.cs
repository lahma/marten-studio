using KStudio.Internal;

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

    private readonly List<KafkaExternalLinkRegistration> externalLinks = [];

    /// <summary>The links the host added, in order.</summary>
    internal IReadOnlyList<KafkaExternalLinkRegistration> ExternalLinks => externalLinks;

    /// <summary>
    /// Adds a link from a record to the host's own tracing or log tool (AGENTS.md D44). The placeholders are <c>{traceId}</c>, <c>{correlationId}</c>,
    /// <c>{from}</c> and <c>{to}</c> (the record's timestamp 15 minutes either side, ISO 8601 UTC with milliseconds), and <c>{fromMs}</c> and
    /// <c>{toMs}</c> (the same instants in Unix milliseconds). Every value is URL-escaped, and a link is drawn only for a record that has a value for every
    /// placeholder the template names: a masked, cut or absent value means no link.
    /// </summary>
    /// <remarks>
    /// <b>Never put a credential in a template.</b> It is rendered into every visitor's page and kept in their browser history. The host is refused at
    /// start for a label that is empty, longer than 40 characters or used twice, more than eight links, a template longer than 2,000 characters or that is not an
    /// absolute <c>http</c> or <c>https</c> address once its placeholders are filled, an unknown placeholder, user info, and a query parameter named
    /// <c>access_token</c>, <c>token</c>, <c>api_key</c>, <c>apikey</c>, <c>x-api-key</c>, <c>password</c>, <c>passwd</c>, <c>pwd</c>, <c>secret</c>,
    /// <c>client_secret</c>, <c>signature</c>, <c>sig</c> or <c>auth</c>. The error names the link's label and the rule, never the template.
    /// </remarks>
    /// <param name="label">The link's text, 1 to 40 characters, unique ignoring case.</param>
    /// <param name="urlTemplate">The address, with placeholders.</param>
    /// <returns>This options object, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="label" /> or <paramref name="urlTemplate" /> is null.</exception>
    public KafkaStudioOptions ExternalLink(string label, string urlTemplate)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(urlTemplate);

        externalLinks.Add(new KafkaExternalLinkRegistration(label, urlTemplate));
        return this;
    }
}
