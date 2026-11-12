using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace KStudio.Services;

/// <summary>
/// The module's own scope as the audit ring records it (D34): the cluster entry and the topics an action named. Read only by
/// <see cref="KafkaAuditGate" />.
/// </summary>
/// <param name="Cluster">The cluster entry's key.</param>
/// <param name="TenantId">The entry's own tenant id, or <see langword="null" />.</param>
/// <param name="Topics">The topics the action named; empty for a cluster-level action.</param>
internal sealed record KafkaAuditScope(string Cluster, string? TenantId, IReadOnlyList<string> Topics);

/// <summary>
/// What a Kafka service calls when a visitor does, or is refused, something worth keeping: the process-wide Activity ring through the
/// studio's <see cref="StudioActionLog" />, under the module's own scope, and KStudio's own log events (D34). Written on failure as well as on
/// success (AGENTS.md hard rule 5).
/// </summary>
/// <param name="audit">The studio's audit, whose ring this writes into.</param>
/// <param name="logger">Where events 9602 and 9603 go.</param>
/// <param name="authenticationStateProvider">Who is asking.</param>
internal sealed class KafkaActionLog(
    StudioActionLog audit,
    ILogger<KafkaActionLog> logger,
    AuthenticationStateProvider authenticationStateProvider)
{
    private const string Unknown = "(unknown)";

    /// <summary>The most characters of a target the ring keeps before an ellipsis, like the SQL console's.</summary>
    internal const int MaxTargetLength = 200;

    /// <summary>
    /// Records one action into the ring under the cluster's scope. The ring entry only: a search logs its own event (9600, 9601), with the
    /// properties its template names.
    /// </summary>
    /// <param name="cluster">The cluster entry the action was aimed at.</param>
    /// <param name="topics">The topics it named.</param>
    /// <param name="action">What was done (<c>SearchRecords</c>).</param>
    /// <param name="target">What it was done to; bounded here.</param>
    /// <param name="succeeded">Whether it worked.</param>
    /// <param name="message">The outcome, or the reason it failed.</param>
    /// <param name="capability">The capability it required, when it required one.</param>
    public void Record(
        KafkaClusterEntry cluster,
        IReadOnlyList<string> topics,
        string action,
        string target,
        bool succeeded,
        string? message = null,
        KafkaCapability? capability = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(topics);

        audit.RecordModule(
            new StudioModuleAuditScope(KafkaStudioText.Key, Bounded(cluster.Label), new KafkaAuditScope(cluster.Key, cluster.TenantId, [.. topics])),
            action,
            Bounded(target),
            succeeded,
            message,
            capability?.ToString());
    }

    /// <summary>
    /// Records a capability refusal: the ring entry a person reads, and event 9602 for whoever is watching the application's log.
    /// </summary>
    /// <param name="cluster">The cluster entry the action was aimed at.</param>
    /// <param name="topics">The topics it named.</param>
    /// <param name="denial">The refusal.</param>
    /// <param name="action">The action tried.</param>
    /// <param name="target">What it was aimed at.</param>
    public void RecordCapabilityDenied(
        KafkaClusterEntry cluster,
        IReadOnlyList<string> topics,
        KafkaCapabilityDeniedException denial,
        string action,
        string target)
    {
        ArgumentNullException.ThrowIfNull(denial);

        Record(cluster, topics, action, target, succeeded: false, denial.Message, denial.Capability);
        logger.CapabilityRefused(UserName(), denial.Capability.ToString(), denial.CapabilityOption);
    }

    /// <summary>
    /// Records a refusal by the cluster or capability policy: event 9603, and a ring entry that names the topics tried and says nothing about
    /// why. The name is shown on the Activity screen only to a visitor who may read it (<see cref="KafkaAuditGate" />).
    /// </summary>
    /// <param name="cluster">The cluster entry the action was aimed at.</param>
    /// <param name="topic">The topic refused, or <see langword="null" /> for the cluster itself.</param>
    /// <param name="topics">The topics the request named.</param>
    /// <param name="policy">The policy that refused.</param>
    /// <param name="action">The action tried.</param>
    /// <param name="target">What it was aimed at.</param>
    /// <param name="capability">The capability tried, if any.</param>
    public void RecordClusterDenied(
        KafkaClusterEntry cluster,
        string? topic,
        IReadOnlyList<string> topics,
        string policy,
        string action,
        string target,
        KafkaCapability? capability = null)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        Record(cluster, topics, action, target, succeeded: false, KafkaStudioText.RefusedEntry, capability);
        logger.ClusterRefused(UserName(), cluster.Key, cluster.TenantId, topic, policy);
    }

    /// <summary>The ring's bound for a target or a label: two hundred characters, then an ellipsis.</summary>
    /// <param name="text">The text.</param>
    internal static string Bounded(string text) =>
        text.Length <= MaxTargetLength ? text : text[..MaxTargetLength] + "…";

    /// <summary>
    /// Who the circuit belongs to, or a placeholder. Read from the already-completed task rather than awaited: a circuit's authentication state
    /// is settled long before a visitor can click anything (<see cref="StudioActionLog" />'s own rule).
    /// </summary>
    private string UserName()
    {
        Task<AuthenticationState> state = authenticationStateProvider.GetAuthenticationStateAsync();
        if (!state.IsCompletedSuccessfully)
        {
            return Unknown;
        }

        string? name = state.Result.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }
}
