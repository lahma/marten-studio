using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace KStudio.Services;

/// <summary>
/// KStudio's authorization (D35): the cluster and capability policies, asked about a <see cref="KafkaTopicResource" /> on every call.
/// </summary>
/// <remarks>
/// <para>
/// <b>It never asks the host studio's policies, and never falls back to them.</b> <c>StoreAuthorizationPolicy</c>,
/// <c>SourceAuthorizationPolicy</c> and <c>WriteAuthorizationPolicy</c> are asked about <c>MartenStoreResource</c> or
/// <c>PostgresStudioResource</c>, and their handlers are written for those types: handing one a <see cref="KafkaTopicResource" /> under
/// the same policy name is what D30 forbids. For the same reason the host's <c>StudioAuthorization</c> is not reused through a second
/// resource factory. With neither KStudio policy set nothing is asked and everything passes.
/// </para>
/// <para>
/// <b>What a question asks.</b> A read (no capability) asks <see cref="KafkaStudioOptions.ClusterAuthorizationPolicy" /> with
/// <see cref="KafkaTopicResource.Capability" /> <see langword="null" />. A capability's use asks the cluster policy first, capability unnamed - it
/// decides which topics the visitor may reach at all - and then <c>CapabilityAuthorizationPolicy ?? ClusterAuthorizationPolicy</c> with the
/// capability named (the capability policy falls back to the cluster policy, D5's write-policy fallback). A call that touches three topics asks about each of the
/// three; the visitor comes from the circuit's authentication state, and nothing is remembered beyond one sweep.
/// </para>
/// </remarks>
/// <param name="options">KStudio's options.</param>
/// <param name="authorizationService">The application's policy evaluator.</param>
/// <param name="authenticationStateProvider">Who is asking.</param>
internal sealed class KafkaAuthorization(
    IOptions<KafkaStudioOptions> options,
    IAuthorizationService authorizationService,
    AuthenticationStateProvider authenticationStateProvider)
{
    /// <summary>Whether any KStudio policy is configured at all.</summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(options.Value.ClusterAuthorizationPolicy)
        || !string.IsNullOrWhiteSpace(options.Value.CapabilityAuthorizationPolicy);

    /// <summary>The policy a question about <paramref name="capability" /> goes to, or <see langword="null" /> when nothing is configured for it.</summary>
    /// <param name="capability">The capability, or <see langword="null" /> for a read that needs none.</param>
    public string? PolicyFor(KafkaCapability? capability)
    {
        string? cluster = Normalize(options.Value.ClusterAuthorizationPolicy);

        return capability is null ? cluster : Normalize(options.Value.CapabilityAuthorizationPolicy) ?? cluster;
    }

    /// <summary>Whether the visitor may reach <paramref name="topic" /> of <paramref name="cluster" /> (or the cluster itself, for <see langword="null" />).</summary>
    /// <param name="cluster">The cluster entry.</param>
    /// <param name="topic">The topic, or <see langword="null" /> for a cluster-level question.</param>
    /// <param name="capability">The capability being used, or <see langword="null" /> for a read that needs none.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    public async ValueTask<bool> IsAuthorizedAsync(
        KafkaClusterEntry cluster,
        string? topic,
        KafkaCapability? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (!IsEnabled)
        {
            return true;
        }

        ClaimsPrincipal user = await PrincipalAsync().ConfigureAwait(false);
        return await IsAuthorizedAsync(user, cluster.Key, cluster.TenantId, topic, capability, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same question for a caller that already holds the principal and names the entry by its parts (the audit gate).</summary>
    /// <param name="user">The visitor.</param>
    /// <param name="cluster">The cluster entry's key.</param>
    /// <param name="tenantId">The entry's tenant id, or <see langword="null" />.</param>
    /// <param name="topic">The topic, or <see langword="null" /> for a cluster-level question.</param>
    /// <param name="capability">The capability being used, or <see langword="null" /> for a read that needs none.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    public async ValueTask<bool> IsAuthorizedAsync(
        ClaimsPrincipal user,
        string cluster,
        string? tenantId,
        string? topic,
        KafkaCapability? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(cluster);

        string? clusterPolicy = Normalize(options.Value.ClusterAuthorizationPolicy);
        string? capabilityPolicy = Normalize(options.Value.CapabilityAuthorizationPolicy);

        if (capability is null)
        {
            return clusterPolicy is null
                || await AskAsync(user, clusterPolicy, new KafkaTopicResource(cluster, tenantId, topic, null), cancellationToken).ConfigureAwait(false);
        }

        // A capability's use (D-SHELL 5.2). The cluster policy is always asked first, with no capability named: it decides which topics the
        // visitor may reach at all. Then the capability is asked of the capability policy, which falls back to the cluster policy.
        KafkaTopicResource named = new(cluster, tenantId, topic, capability.Value.ToString());

        if (clusterPolicy is not null
            && !await AskAsync(user, clusterPolicy, named with { Capability = null }, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        string? policy = capabilityPolicy ?? clusterPolicy;
        return policy is null || await AskAsync(user, policy, named, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The entries of <paramref name="clusters" /> the visitor may see, in the order they arrived, at most <paramref name="take" />. The
    /// principal is fetched once.
    /// </summary>
    /// <param name="clusters">The entries.</param>
    /// <param name="take">Stop after this many, or <see langword="null" /> for all of them.</param>
    /// <param name="cancellationToken">Checked between entries.</param>
    public async ValueTask<IReadOnlyList<KafkaClusterEntry>> FilterClustersAsync(
        IEnumerable<KafkaClusterEntry> clusters,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        int limit = take ?? int.MaxValue;

        if (!IsEnabled)
        {
            return [.. clusters.Take(limit)];
        }

        ClaimsPrincipal user = await PrincipalAsync().ConfigureAwait(false);
        List<KafkaClusterEntry> allowed = [];

        foreach (KafkaClusterEntry cluster in clusters)
        {
            if (allowed.Count >= limit)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (await IsAuthorizedAsync(user, cluster.Key, cluster.TenantId, null, null, cancellationToken).ConfigureAwait(false))
            {
                allowed.Add(cluster);
            }
        }

        return allowed;
    }

    /// <summary>The topics of <paramref name="topics" /> the visitor may reach on <paramref name="cluster" />, one question each.</summary>
    /// <param name="cluster">The cluster entry.</param>
    /// <param name="topics">The topics.</param>
    /// <param name="take">Stop after this many, or <see langword="null" /> for all of them.</param>
    /// <param name="capability">The capability the topics are wanted for, or <see langword="null" /> for a read that needs none: the list a search offers asks the capability policy too.</param>
    /// <param name="cancellationToken">Checked between topics.</param>
    public async ValueTask<IReadOnlyList<string>> FilterTopicsAsync(
        KafkaClusterEntry cluster,
        IEnumerable<string> topics,
        int? take = null,
        KafkaCapability? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(topics);

        int limit = take ?? int.MaxValue;

        if (!IsEnabled)
        {
            return [.. topics.Take(limit)];
        }

        ClaimsPrincipal user = await PrincipalAsync().ConfigureAwait(false);
        List<string> allowed = [];

        foreach (string topic in topics)
        {
            if (allowed.Count >= limit)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (await IsAuthorizedAsync(user, cluster.Key, cluster.TenantId, topic, capability, cancellationToken).ConfigureAwait(false))
            {
                allowed.Add(topic);
            }
        }

        return allowed;
    }

    /// <summary>
    /// Whether the visitor may reach every topic of <paramref name="topics" /> on <paramref name="cluster" /> - and the cluster itself - for
    /// <paramref name="capability" />: a request that names a refused topic is refused as a whole, never narrowed (a search silently narrowed to
    /// the allowed topics would report a false negative).
    /// </summary>
    /// <param name="cluster">The cluster entry.</param>
    /// <param name="topics">The topics the request names.</param>
    /// <param name="capability">The capability being used, or <see langword="null" /> for a read that needs none.</param>
    /// <param name="cancellationToken">Checked between questions.</param>
    public async ValueTask<bool> AreAllAuthorizedAsync(
        KafkaClusterEntry cluster,
        IEnumerable<string> topics,
        KafkaCapability? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(topics);

        if (!IsEnabled)
        {
            return true;
        }

        ClaimsPrincipal user = await PrincipalAsync().ConfigureAwait(false);

        if (!await IsAuthorizedAsync(user, cluster.Key, cluster.TenantId, null, capability, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        foreach (string topic in topics)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await IsAuthorizedAsync(user, cluster.Key, cluster.TenantId, topic, capability, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Who the circuit belongs to, or <c>anonymous</c>.</summary>
    public async ValueTask<string> UserNameAsync()
    {
        ClaimsPrincipal user = await PrincipalAsync().ConfigureAwait(false);
        string? name = user.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }

    private async ValueTask<bool> AskAsync(ClaimsPrincipal user, string policy, KafkaTopicResource resource, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AuthorizationResult result = await authorizationService.AuthorizeAsync(user, resource, policy).ConfigureAwait(false);
        return result.Succeeded;
    }

    private async ValueTask<ClaimsPrincipal> PrincipalAsync()
    {
        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return state.User;
    }

    private static string? Normalize(string? policy) => string.IsNullOrWhiteSpace(policy) ? null : policy;
}
