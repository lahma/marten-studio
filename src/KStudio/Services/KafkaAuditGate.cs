using System.Security.Claims;

using PostgresStudio.Services;

namespace KStudio.Services;

/// <summary>
/// Decides which of the module's audit entries a visitor may read (D34): an entry names topics, a window and a predicate, so it is shown only
/// to a visitor who may make the same read. Judged here and never by the host's store policy, which is written for the host's resources.
/// </summary>
/// <remarks>
/// An entry is shown when the cluster policy passes <c>(Cluster, TenantId, Topic = null)</c> and every topic it names and, when it carries a
/// capability, that capability is on and its policy passes for every topic with the capability named. This mirrors the host's
/// <c>ReadsBeyondTheStore</c>. An entry whose scope is not the module's own is hidden: fail closed.
/// </remarks>
/// <param name="authorization">The module's policies.</param>
/// <param name="capabilities">Which capabilities the process has on.</param>
internal sealed class KafkaAuditGate(KafkaAuthorization authorization, KafkaCapabilityGuard capabilities) : IStudioAuditScopeGate
{
    /// <inheritdoc />
    public string Kind => KafkaStudioText.Key;

    /// <inheritdoc />
    public IStudioAuditSweep BeginSweep(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new Sweep(user, authorization, capabilities);
    }

    /// <summary>One sweep: the answers are this visitor's, kept for the sweep only and never where another circuit could read them.</summary>
    private sealed class Sweep(ClaimsPrincipal user, KafkaAuthorization authorization, KafkaCapabilityGuard capabilities) : IStudioAuditSweep
    {
        private readonly Dictionary<Question, bool> answers = [];

        public async ValueTask<bool> IsVisibleAsync(StudioActionLogEntry entry, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (entry.ModuleScope is not KafkaAuditScope scope)
            {
                return false;
            }

            KafkaCapability? capability = null;
            if (entry.Capability is not null)
            {
                if (!Enum.TryParse(entry.Capability, ignoreCase: false, out KafkaCapability parsed) || !Enum.IsDefined(parsed))
                {
                    return false;
                }

                if (!capabilities.IsEnabled(parsed))
                {
                    return false;
                }

                capability = parsed;
            }

            if (!await AskAsync(scope, topic: null, capability: null, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            foreach (string topic in scope.Topics)
            {
                if (!await AskAsync(scope, topic, capability: null, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                if (capability is not null && !await AskAsync(scope, topic, capability, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            return capability is null
                || scope.Topics.Count > 0
                || await AskAsync(scope, topic: null, capability, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<bool> AskAsync(KafkaAuditScope scope, string? topic, KafkaCapability? capability, CancellationToken cancellationToken)
        {
            Question question = new(scope.Cluster, scope.TenantId, topic, capability);

            if (answers.TryGetValue(question, out bool known))
            {
                return known;
            }

            bool answer = await authorization
                .IsAuthorizedAsync(user, scope.Cluster, scope.TenantId, topic, capability, cancellationToken)
                .ConfigureAwait(false);
            answers[question] = answer;
            return answer;
        }

        private readonly record struct Question(string Cluster, string? TenantId, string? Topic, KafkaCapability? Capability);
    }
}
